using System;
using System.Collections.Generic;
using System.Numerics;
using Core.Map;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// Small bounded pool of detached terrain bodies.
/// Each body owns one immutable voxel snapshot and a mesh built once at detach
/// time. Simulation uses a conservative oriented bounding box against the
/// static field; it does not create one rigid body per voxel.
/// </summary>
internal sealed class DynamicVoxelFragmentRenderer : IDisposable
{
    private const int MaximumBodies = 16;
    private const int MaximumChunksPerBody = 64;
    private const long MaximumDynamicSnapshotBytes = 32L * 1024 * 1024;
    private const int DynamicMeshStep = 2;
    private const float Gravity = 9.81f;
    private const float MaximumFallSpeed = 45f;
    private const float SleepLinearSpeed = 0.12f;
    private const float SleepAngularSpeed = 0.16f;
    private const float SleepDelaySeconds = 1.25f;

    private sealed class Body : IDisposable
    {
        public required Mesh[] Meshes { get; init; }
        public required VoxelFragmentSnapshot Snapshot { get; init; }
        public required Vector3 Position { get; set; }
        public required Vector3 HalfExtents { get; init; }
        public required Vector3 LocalPivot { get; init; }
        public required float MassKg { get; init; }
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
        public float QuietTime;
        public bool Sleeping;
        public bool Active = true;
        public bool Baked;
        public SparseVoxelField.FragmentMergeJob? MergeJob;
        public SparseVoxelField.VoxelBounds BakedBounds;

        public void Dispose()
        {
            foreach (Mesh mesh in Meshes)
                UnloadMesh(mesh);
        }
    }

    private readonly List<Body> _bodies = new(MaximumBodies);
    private readonly List<Body[]> _undoEdits = new();
    private readonly List<Body[]> _redoEdits = new();
    private List<Body>? _pendingEdit;
    private int _activeBodyCount;
    private long _snapshotBytes;
    private bool _disposed;

    public int Count => _activeBodyCount;
    public int AvailableSlots => Math.Max(0, MaximumBodies - _activeBodyCount);
    public bool IsMerging => _bodies.Exists(static body => body.MergeJob != null);

    public void BeginEdit()
    {
        ThrowIfDisposed();
        if (_pendingEdit != null)
            throw new InvalidOperationException("A dynamic fragment edit is already active.");
        _pendingEdit = new List<Body>(1);
    }

    public void EndEdit(bool commit)
    {
        ThrowIfDisposed();
        if (_pendingEdit == null)
            return;

        Body[] created = _pendingEdit.ToArray();
        _pendingEdit = null;
        if (!commit)
        {
            foreach (Body body in created)
            {
                if (body.Active)
                {
                    body.Active = false;
                    _activeBodyCount--;
                }
                body.Dispose();
                _snapshotBytes -= body.Snapshot.EstimatedBytes;
                _bodies.Remove(body);
            }
            return;
        }

        _undoEdits.Add(created);
        ClearRedoEdits();

        while (_undoEdits.Count > 16)
        {
            Body[] expired = _undoEdits[0];
            _undoEdits.RemoveAt(0);
            DisposeInactiveBodies(expired);
        }
    }

    public void UndoLastEdit()
    {
        ThrowIfDisposed();
        if (_pendingEdit != null || _undoEdits.Count == 0)
            return;

        int last = _undoEdits.Count - 1;
        Body[] edit = _undoEdits[last];
        _undoEdits.RemoveAt(last);
        foreach (Body body in edit)
        {
            if (!body.Active)
                continue;
            body.Active = false;
            _activeBodyCount--;
        }
        _redoEdits.Add(edit);
    }

    public void RedoLastEdit()
    {
        ThrowIfDisposed();
        if (_pendingEdit != null || _redoEdits.Count == 0)
            return;

        int last = _redoEdits.Count - 1;
        Body[] edit = _redoEdits[last];
        _redoEdits.RemoveAt(last);
        foreach (Body body in edit)
        {
            if (body.Active)
                continue;
            body.Active = true;
            _activeBodyCount++;
        }
        _undoEdits.Add(edit);
    }

    public bool TryAdd(
        VoxelFragmentSnapshot snapshot,
        Vector3 impulseOriginMeters,
        TerrainPalette palette)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(palette);
        if (AvailableSlots == 0 ||
            _snapshotBytes + snapshot.EstimatedBytes > MaximumDynamicSnapshotBytes)
            return false;

        int chunkSpan = SparseVoxelField.ChunkSize * DynamicMeshStep;
        int chunksX = (snapshot.Width + chunkSpan - 1) / chunkSpan;
        int chunksY = (snapshot.Height + chunkSpan - 1) / chunkSpan;
        int chunksZ = (snapshot.Depth + chunkSpan - 1) / chunkSpan;
        if ((long)chunksX * chunksY * chunksZ > MaximumChunksPerBody)
            return false;

        List<Mesh> meshes = new();
        try
        {
            for (int z = 0; z < chunksZ; z++)
            for (int y = 0; y < chunksY; y++)
            for (int x = 0; x < chunksX; x++)
            {
                Mesh[] chunkMeshes = VoxelChunkMesher.BuildSurface(
                    snapshot, x, y, z, step: DynamicMeshStep, palette: palette);
                meshes.AddRange(chunkMeshes);
            }

            if (meshes.Count == 0)
                return false;

            Vector3 position = snapshot.InitialCenterMeters;
            Vector3 launchDirection = position - impulseOriginMeters;
            if (launchDirection.LengthSquared() < 0.0001f)
                launchDirection = Vector3.UnitY;
            else
                launchDirection = Vector3.Normalize(launchDirection);

            Vector3 torqueAxis = Vector3.Cross(
                launchDirection,
                MathF.Abs(launchDirection.Y) < 0.85f
                    ? Vector3.UnitY
                    : Vector3.UnitX);
            if (torqueAxis.LengthSquared() < 0.0001f)
                torqueAxis = Vector3.UnitZ;
            else
                torqueAxis = Vector3.Normalize(torqueAxis);

            if (!MakeRoomForBody())
            {
                foreach (Mesh mesh in meshes)
                    UnloadMesh(mesh);
                return false;
            }

            Body body = new()
            {
                Meshes = meshes.ToArray(),
                Snapshot = snapshot,
                Position = position,
                HalfExtents = snapshot.HalfExtentsMeters,
                LocalPivot = snapshot.LocalPivotMeters,
                MassKg = snapshot.MassKg,
                Velocity = launchDirection * 1.4f + Vector3.UnitY * 1.1f,
                AngularVelocity = torqueAxis * 0.65f
            };
            _bodies.Add(body);
            _activeBodyCount++;
            _snapshotBytes += snapshot.EstimatedBytes;
            _pendingEdit?.Add(body);
            return true;
        }
        catch (Exception)
        {
            foreach (Mesh mesh in meshes)
                UnloadMesh(mesh);
            return false;
        }
    }

    public void Update(SparseVoxelField staticField, float deltaTime)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(staticField);
        if (_bodies.Count == 0)
            return;

        float remaining = Math.Clamp(deltaTime, 0f, 0.05f);
        if (remaining <= 0f)
            return;

        const float fixedStep = 1f / 60f;
        while (remaining > 0f)
        {
            float step = MathF.Min(fixedStep, remaining);
            for (int i = 0; i < _bodies.Count; i++)
            {
                Body body = _bodies[i];
                if (body.Active && !body.Baked && body.MergeJob == null)
                    Integrate(body, staticField, step);
            }
            ResolveBodyContacts();
            remaining -= step;
        }

        // Only one body bakes at a time. The source field owns the merge job and
        // commits it in fixed-size batches, keeping terrain edits out of Draw's
        // mesh-build budget.
        foreach (Body body in _bodies)
        {
            if (!body.Active)
                continue;

            if (body.Sleeping && body.MergeJob == null && !body.Baked)
            {
                body.MergeJob = staticField.BeginFragmentMerge(
                    body.Snapshot,
                    body.Position,
                    body.Rotation);
            }

            if (body.MergeJob == null)
                continue;

            SparseVoxelField.FragmentMergeJob mergeJob = body.MergeJob;
            if (staticField.ProcessFragmentMerge(
                    mergeJob,
                    maximumSamples: 12_000))
            {
                body.BakedBounds = mergeJob.Bounds;
                body.Baked = true;
                body.MergeJob = null;
            }

            break;
        }
    }

    public void Draw(Material material)
    {
        ThrowIfDisposed();
        foreach (Body body in _bodies)
        {
            if (!body.Active)
                continue;

            Matrix4x4 transform =
                Matrix4x4.CreateTranslation(-body.LocalPivot) *
                Matrix4x4.CreateFromQuaternion(body.Rotation) *
                Matrix4x4.CreateTranslation(body.Position);

            foreach (Mesh mesh in body.Meshes)
                DrawMesh(mesh, material, transform);
        }
    }

    /// <summary>
    /// Keeps a baked body's visual mesh alive until any cached static meshes
    /// overlapping its world bounds have been rebuilt. Missing cache entries
    /// are safe: they will be generated from the merged master field on demand.
    /// </summary>
    public void ReleaseBakedBodies(Func<SparseVoxelField.VoxelBounds, bool> staticMeshesReady)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(staticMeshesReady);

        for (int i = _bodies.Count - 1; i >= 0; i--)
        {
            Body body = _bodies[i];
            if (!body.Active || !body.Baked || body.MergeJob != null ||
                !staticMeshesReady(body.BakedBounds))
                continue;

            body.Active = false;
            _activeBodyCount--;
            _snapshotBytes -= body.Snapshot.EstimatedBytes;
            body.Dispose();
            _bodies.RemoveAt(i);
            RemoveFromHistory(body);
        }
    }

    public void Clear()
    {
        foreach (Body body in _bodies)
            body.Dispose();
        _bodies.Clear();
        _undoEdits.Clear();
        _redoEdits.Clear();
        _pendingEdit = null;
        _activeBodyCount = 0;
        _snapshotBytes = 0;
    }

    private bool MakeRoomForBody()
    {
        if (_bodies.Count < MaximumBodies)
            return true;

        int inactiveIndex = _bodies.FindIndex(static body => !body.Active);
        if (inactiveIndex < 0)
            return false;

        Body expired = _bodies[inactiveIndex];
        expired.Dispose();
        _snapshotBytes -= expired.Snapshot.EstimatedBytes;
        _bodies.RemoveAt(inactiveIndex);
        RemoveFromHistory(expired);
        return true;
    }

    private void RemoveFromHistory(Body body)
    {
        for (int i = 0; i < _undoEdits.Count; i++)
            _undoEdits[i] = RemoveBody(_undoEdits[i], body);
        for (int i = 0; i < _redoEdits.Count; i++)
            _redoEdits[i] = RemoveBody(_redoEdits[i], body);
        _pendingEdit?.Remove(body);
    }

    private static Body[] RemoveBody(Body[] source, Body target)
    {
        int remaining = 0;
        for (int i = 0; i < source.Length; i++)
            if (!ReferenceEquals(source[i], target))
                remaining++;

        if (remaining == source.Length)
            return source;

        Body[] result = new Body[remaining];
        int write = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (!ReferenceEquals(source[i], target))
                result[write++] = source[i];
        }
        return result;
    }

    private void ClearRedoEdits()
    {
        Body[][] expiredEdits = _redoEdits.ToArray();
        _redoEdits.Clear();
        foreach (Body[] edit in expiredEdits)
            DisposeInactiveBodies(edit);
    }

    private void DisposeInactiveBodies(Body[] edit)
    {
        foreach (Body body in edit)
        {
            if (body.Active)
                continue;
            body.Dispose();
            _snapshotBytes -= body.Snapshot.EstimatedBytes;
            _bodies.Remove(body);
            RemoveFromHistory(body);
        }
    }

    private static void Integrate(
        Body body,
        SparseVoxelField staticField,
        float deltaTime)
    {
        if (body.Sleeping)
            return;

        body.Velocity.Y = MathF.Max(
            -MaximumFallSpeed,
            body.Velocity.Y - Gravity * deltaTime);
        body.Position = body.Position + body.Velocity * deltaTime;

        float angularSpeed = body.AngularVelocity.Length();
        if (angularSpeed > 0.0001f)
        {
            Quaternion delta = Quaternion.CreateFromAxisAngle(
                body.AngularVelocity / angularSpeed,
                angularSpeed * deltaTime);
            body.Rotation = Quaternion.Normalize(delta * body.Rotation);
        }

        float verticalRadius = GetVerticalRadius(body);
        float bottom = body.Position.Y - verticalRadius;
        float searchBelow = Math.Clamp(
            MathF.Abs(body.Velocity.Y) * deltaTime + 0.3f,
            0.3f,
            2.5f);

        bool grounded = false;
        if (body.Velocity.Y <= 0f &&
            staticField.TryFindClosestWalkableSurface(
                body.Position.X,
                body.Position.Z,
                bottom,
                searchBelow,
                0.2f,
                0f,
                0.35f,
                out SparseVoxelField.WalkableSurface support) &&
            bottom <= support.HeightMeters)
        {
            float penetration = support.HeightMeters - bottom;
            if (penetration <= 2.5f)
            {
                body.Position = new Vector3(
                    body.Position.X,
                    body.Position.Y + penetration,
                    body.Position.Z);
                float impactSpeed = -body.Velocity.Y;
                body.Velocity.Y = impactSpeed > 1.5f
                    ? impactSpeed * 0.16f
                    : 0f;
                body.Velocity.X *= 0.76f;
                body.Velocity.Z *= 0.76f;
                body.AngularVelocity *= 0.72f;
                grounded = true;
            }
        }

        float linearSpeedSquared = body.Velocity.LengthSquared();
        float angularSpeedSquared = body.AngularVelocity.LengthSquared();
        if (grounded &&
            linearSpeedSquared <= SleepLinearSpeed * SleepLinearSpeed &&
            angularSpeedSquared <= SleepAngularSpeed * SleepAngularSpeed)
        {
            body.QuietTime += deltaTime;
            if (body.QuietTime >= SleepDelaySeconds)
            {
                body.Velocity = Vector3.Zero;
                body.AngularVelocity = Vector3.Zero;
                body.Sleeping = true;
            }
        }
        else
        {
            body.QuietTime = 0f;
        }
    }

    private void ResolveBodyContacts()
    {
        for (int i = 0; i < _bodies.Count; i++)
        {
            Body first = _bodies[i];
            for (int j = i + 1; j < _bodies.Count; j++)
            {
                Body second = _bodies[j];
                if (!first.Active || !second.Active ||
                    (first.Sleeping && second.Sleeping))
                    continue;

                float firstRadius = first.HalfExtents.Length();
                float secondRadius = second.HalfExtents.Length();
                Vector3 delta = second.Position - first.Position;
                float distanceSquared = delta.LengthSquared();
                float minimumDistance = firstRadius + secondRadius;
                if (distanceSquared >= minimumDistance * minimumDistance ||
                    distanceSquared < 0.000001f)
                    continue;

                float distance = MathF.Sqrt(distanceSquared);
                Vector3 normal = delta / distance;
                float penetration = minimumDistance - distance;
                float totalMass = first.MassKg + second.MassKg;
                float firstShare = second.MassKg / totalMass;
                float secondShare = first.MassKg / totalMass;

                first.Position = first.Position - normal * (penetration * firstShare);
                second.Position = second.Position + normal * (penetration * secondShare);

                Vector3 relativeVelocity = second.Velocity - first.Velocity;
                float closingSpeed = Vector3.Dot(relativeVelocity, normal);
                if (closingSpeed >= 0f)
                    continue;

                float impulseMagnitude = -(1.08f * closingSpeed) /
                    (1f / first.MassKg + 1f / second.MassKg);
                Vector3 impulse = normal * impulseMagnitude;
                first.Velocity -= impulse / first.MassKg;
                second.Velocity += impulse / second.MassKg;
                first.Sleeping = false;
                second.Sleeping = false;
                first.QuietTime = 0f;
                second.QuietTime = 0f;
            }
        }
    }

    private static float GetVerticalRadius(Body body)
    {
        Vector3 axisX = Vector3.Transform(Vector3.UnitX, body.Rotation);
        Vector3 axisY = Vector3.Transform(Vector3.UnitY, body.Rotation);
        Vector3 axisZ = Vector3.Transform(Vector3.UnitZ, body.Rotation);
        return MathF.Abs(axisX.Y) * body.HalfExtents.X +
            MathF.Abs(axisY.Y) * body.HalfExtents.Y +
            MathF.Abs(axisZ.Y) * body.HalfExtents.Z;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Clear();
        _disposed = true;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
