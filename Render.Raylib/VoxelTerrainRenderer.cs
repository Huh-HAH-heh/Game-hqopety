using System;
using System.Collections.Generic;
using System.Numerics;
using Core.Map;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// Chunked renderer for the authoritative 3D density field.
/// The visible mesh is extracted from the same scalar voxels used by CSG edits,
/// material queries and cut sections. No XY column or height-run mesh is used.
/// </summary>
internal sealed class VoxelTerrainRenderer : IDisposable
{
    private const int ChunkCells = SparseVoxelField.ChunkSize;
    private const int MaximumCachedChunks = 1800;
    private const int MaximumCoarseBuildsPerFrame = 10;
    private const int MaximumMediumBuildsPerFrame = 5;
    private const int MaximumFineBuildsPerFrame = 2;
    private const int MaximumCutBuildsPerFrame = 20;

    private const string VertexShader = """
        #version 330
        in vec3 vertexPosition;
        in vec4 vertexColor;
        in vec3 vertexNormal;
        uniform mat4 mvp;
        uniform mat4 matModel;
        out vec3 fragWorldPosition;
        out vec3 fragWorldNormal;
        out vec4 fragColor;
        void main()
        {
            vec4 worldPosition = matModel * vec4(vertexPosition, 1.0);
            fragWorldPosition = worldPosition.xyz;
            fragWorldNormal = normalize(mat3(matModel) * vertexNormal);
            fragColor = vertexColor;
            gl_Position = mvp * vec4(vertexPosition, 1.0);
        }
        """;

    private const string FragmentShader = """
        #version 330
        in vec3 fragWorldPosition;
        in vec3 fragWorldNormal;
        in vec4 fragColor;
        uniform int uCutEnabled;
        uniform float uCutHeight;
        uniform vec3 uSunDirection;
        uniform float uAmbientLight;
        uniform float uSunIntensity;
        out vec4 finalColor;
        void main()
        {
            if (uCutEnabled != 0 && fragWorldPosition.y > uCutHeight + 0.0001)
                discard;
            float diffuse = max(
                dot(normalize(fragWorldNormal), normalize(uSunDirection)),
                0.0);
            float illumination = clamp(
                uAmbientLight + diffuse * uSunIntensity,
                0.05,
                1.5);
            finalColor = vec4(fragColor.rgb * illumination, fragColor.a);
        }
        """;

    private readonly record struct ChunkKey(int Step, int X, int Y, int Z);

    private sealed class ChunkEntry : IDisposable
    {
        public Mesh[] Surface = Array.Empty<Mesh>();
        public Mesh[] CutCap = Array.Empty<Mesh>();
        public long BaseVersion = long.MinValue;
        public bool SurfaceDirty = true;
        public bool CutDirty = true;
        public int PaletteVersion = int.MinValue;
        public int CutLayer = int.MinValue;
        public long LastSeenFrame;
        public bool Built;

        public void Dispose()
        {
            foreach (Mesh mesh in Surface)
                UnloadMesh(mesh);
            foreach (Mesh mesh in CutCap)
                UnloadMesh(mesh);
            Surface = Array.Empty<Mesh>();
            CutCap = Array.Empty<Mesh>();
            Built = false;
        }
    }

    private readonly record struct Candidate(
        ChunkKey Key,
        Vector3 Center,
        float Radius,
        float DistanceSquared);

    private readonly Dictionary<ChunkKey, ChunkEntry> _chunks = new();
    private SparseVoxelField? _observedField;
    private readonly List<(ChunkKey Key, ChunkEntry Entry)> _visible = new();
    private readonly Material _material;
    private readonly Shader _shader;
    private readonly int _cutEnabledLocation;
    private readonly int _cutHeightLocation;
    private readonly int _sunDirectionLocation;
    private readonly int _ambientLightLocation;
    private readonly int _sunIntensityLocation;

    private TerrainPalette _palette;
    private int _paletteVersion;
    private long _frame;
    private bool _disposed;
    private int _activeStep = 40;

    public int ActiveRenderCellSize => _activeStep;
    public float ActiveVoxelSizeMeters => _activeStep * SparseVoxelField.VoxelSizeMeters;
    public bool IsUsingCoarseLod => _activeStep >= 40;
    public int DrawCallsLastFrame { get; private set; }
    public int VisibleChunksLastFrame { get; private set; }
    public int RebuiltChunks { get; private set; }
    public int RebuiltCutCaps { get; private set; }
    public int CachedChunks => _chunks.Count;

    public VoxelTerrainRenderer(TerrainPalette palette)
    {
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));

        _shader = LoadShaderFromMemory(VertexShader, FragmentShader);
        if (!IsShaderValid(_shader))
            throw new InvalidOperationException("The 3D voxel surface shader could not be created.");

        _cutEnabledLocation = GetShaderLocation(_shader, "uCutEnabled");
        _cutHeightLocation = GetShaderLocation(_shader, "uCutHeight");
        _sunDirectionLocation = GetShaderLocation(_shader, "uSunDirection");
        _ambientLightLocation = GetShaderLocation(_shader, "uAmbientLight");
        _sunIntensityLocation = GetShaderLocation(_shader, "uSunIntensity");

        if (_cutEnabledLocation < 0 ||
            _cutHeightLocation < 0 ||
            _sunDirectionLocation < 0 ||
            _ambientLightLocation < 0 ||
            _sunIntensityLocation < 0)
        {
            UnloadShader(_shader);
            throw new InvalidOperationException("The 3D voxel shader is missing a required uniform.");
        }

        _material = LoadMaterialDefault();
        _material.Shader = _shader;
    }

    public void SetPalette(TerrainPalette palette)
    {
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));
        _paletteVersion++;
        foreach (ChunkEntry entry in _chunks.Values)
        {
            entry.SurfaceDirty = true;
            entry.CutDirty = true;
        }
    }

    public void Draw(
        SparseVoxelField field,
        Camera3D camera,
        int viewportWidth,
        int viewportHeight,
        float maximumViewDistanceMeters,
        bool cutEnabled,
        float cutHeightMeters)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(field);
        EnsureFieldSubscription(field);

        _frame++;
        float cameraDistance = Vector3.Distance(camera.Position, camera.Target);
        int previousStep = _activeStep;
        int step = SelectLod(cameraDistance, previousStep);
        bool lodChanged = step != previousStep;
        if (_chunks.Count == 0)
        {
            previousStep = step;
            lodChanged = false;
        }
        _activeStep = step;

        float chunkSizeMeters = ChunkCells * step * SparseVoxelField.VoxelSizeMeters;
        float viewRadius = GetViewRadius(
            step,
            cameraDistance,
            chunkSizeMeters,
            maximumViewDistanceMeters);

        int chunksX = DivideRoundUp(field.Width, ChunkCells * step);
        int chunksY = DivideRoundUp(field.Height, ChunkCells * step);
        int chunksZ = DivideRoundUp(field.Depth, ChunkCells * step);

        int minX = Math.Clamp(
            (int)MathF.Floor((camera.Target.X - viewRadius) / chunkSizeMeters),
            0, chunksX - 1);
        int maxX = Math.Clamp(
            (int)MathF.Floor((camera.Target.X + viewRadius) / chunkSizeMeters),
            0, chunksX - 1);
        int minY = Math.Clamp(
            (int)MathF.Floor((camera.Target.Y - viewRadius) / chunkSizeMeters),
            0, chunksY - 1);
        int maxY = Math.Clamp(
            (int)MathF.Floor((camera.Target.Y + viewRadius) / chunkSizeMeters),
            0, chunksY - 1);
        int minZ = Math.Clamp(
            (int)MathF.Floor((camera.Target.Z - viewRadius) / chunkSizeMeters),
            0, chunksZ - 1);
        int maxZ = Math.Clamp(
            (int)MathF.Floor((camera.Target.Z + viewRadius) / chunkSizeMeters),
            0, chunksZ - 1);

        List<Candidate> candidates = new();
        float chunkRadius = chunkSizeMeters * 0.8660254f;
        for (int chunkZ = minZ; chunkZ <= maxZ; chunkZ++)
        for (int chunkY = minY; chunkY <= maxY; chunkY++)
        for (int chunkX = minX; chunkX <= maxX; chunkX++)
        {
            Vector3 center = new(
                (chunkX + 0.5f) * chunkSizeMeters,
                (chunkY + 0.5f) * chunkSizeMeters,
                (chunkZ + 0.5f) * chunkSizeMeters);

            float distanceSquared = Vector3.DistanceSquared(center, camera.Position);
            if (distanceSquared > maximumViewDistanceMeters * maximumViewDistanceMeters &&
                Vector3.DistanceSquared(center, camera.Target) >
                (maximumViewDistanceMeters + chunkRadius) *
                (maximumViewDistanceMeters + chunkRadius))
                continue;

            if (!TerrainFrustum.IsSphereVisible(
                camera,
                center,
                chunkRadius,
                viewportWidth,
                viewportHeight,
                maximumViewDistanceMeters))
                continue;

            candidates.Add(new Candidate(
                new ChunkKey(step, chunkX, chunkY, chunkZ),
                center,
                chunkRadius,
                distanceSquared));
        }

        candidates.Sort(static (a, b) =>
            a.DistanceSquared.CompareTo(b.DistanceSquared));

        _visible.Clear();
        HashSet<ChunkKey> visibleKeys = new();
        List<(Candidate Candidate, ChunkEntry Entry)> candidateEntries =
            new(candidates.Count);
        int baseBuilds = 0;
        int cutBuilds = 0;
        int maximumBaseBuilds = step >= 40
            ? MaximumCoarseBuildsPerFrame
            : step >= 10
                ? MaximumMediumBuildsPerFrame
                : MaximumFineBuildsPerFrame;

        int cutSampleY = (int)MathF.Floor(
            cutHeightMeters / SparseVoxelField.VoxelSizeMeters - 0.5f);
        int cutChunkY = cutEnabled
            ? Math.Clamp(cutSampleY / (ChunkCells * step), 0, chunksY - 1)
            : -1;
        int cutLayer = cutEnabled
            ? (int)MathF.Round(cutHeightMeters / SparseVoxelField.VoxelSizeMeters)
            : -1;
        float effectiveCutHeight = cutEnabled
            ? cutLayer * SparseVoxelField.VoxelSizeMeters
            : cutHeightMeters;

        foreach (Candidate candidate in candidates)
        {
            ChunkKey key = candidate.Key;
            if (!_chunks.TryGetValue(key, out ChunkEntry? entry))
            {
                entry = new ChunkEntry();
                _chunks.Add(key, entry);
            }

            entry.LastSeenFrame = _frame;
            bool surfaceStale = !entry.Built ||
                entry.SurfaceDirty ||
                entry.BaseVersion != field.BaseVersion ||
                entry.PaletteVersion != _paletteVersion;

            if (surfaceStale && baseBuilds < maximumBaseBuilds)
            {
                Mesh[] replacement = VoxelChunkMesher.BuildSurface(
                    field, key.X, key.Y, key.Z, step, _palette);

                Mesh[] previous = entry.Surface;
                entry.Surface = replacement;
                entry.BaseVersion = field.BaseVersion;
                entry.PaletteVersion = _paletteVersion;
                entry.SurfaceDirty = false;
                entry.Built = true;
                foreach (Mesh mesh in previous)
                    UnloadMesh(mesh);

                RebuiltChunks++;
                baseBuilds++;
            }

            if (cutEnabled && key.Y == cutChunkY &&
                (entry.CutLayer != cutLayer || entry.CutDirty) &&
                cutBuilds < MaximumCutBuildsPerFrame)
            {
                Mesh[] replacement = VoxelChunkMesher.BuildCutCap(
                    field, key.X, key.Z, step, effectiveCutHeight, _palette);

                Mesh[] previous = entry.CutCap;
                entry.CutCap = replacement;
                entry.CutLayer = cutLayer;
                entry.CutDirty = false;
                foreach (Mesh mesh in previous)
                    UnloadMesh(mesh);

                RebuiltCutCaps++;
                cutBuilds++;
            }
            else if (!cutEnabled && entry.CutCap.Length > 0)
            {
                ClearCutCap(entry);
            }

            candidateEntries.Add((candidate, entry));
        }

        if (!lodChanged)
        {
            foreach ((Candidate candidate, ChunkEntry entry) in candidateEntries)
            {
                if (entry.Built)
                    AddVisible(candidate.Key, entry, visibleKeys);
            }
        }
        else if (step < previousStep)
        {
            // Refining LOD: keep each old coarse parent until every visible
            // child that replaces it has a clean mesh. Never mix a coarse
            // parent with only some of its finer children (z-fighting/overdraw).
            float oldChunkSize = ChunkCells * previousStep *
                SparseVoxelField.VoxelSizeMeters;
            Dictionary<ChunkKey, List<(Candidate Candidate, ChunkEntry Entry)>> groups = new();
            foreach ((Candidate candidate, ChunkEntry entry) in candidateEntries)
            {
                ChunkKey parent = new(
                    previousStep,
                    (int)MathF.Floor(candidate.Center.X / oldChunkSize),
                    (int)MathF.Floor(candidate.Center.Y / oldChunkSize),
                    (int)MathF.Floor(candidate.Center.Z / oldChunkSize));
                if (!groups.TryGetValue(parent, out var children))
                {
                    children = new List<(Candidate, ChunkEntry)>();
                    groups.Add(parent, children);
                }
                children.Add((candidate, entry));
            }

            foreach ((ChunkKey parent, List<(Candidate Candidate, ChunkEntry Entry)> children) in groups)
            {
                bool allReady = true;
                foreach ((Candidate _, ChunkEntry child) in children)
                {
                    if (!IsCurrentMeshReady(child, field))
                    {
                        allReady = false;
                        break;
                    }
                }

                if (allReady)
                {
                    foreach ((Candidate candidate, ChunkEntry child) in children)
                        AddVisible(candidate.Key, child, visibleKeys);
                    continue;
                }

                if (_chunks.TryGetValue(parent, out ChunkEntry? fallback) && fallback.Built)
                {
                    AddVisible(parent, fallback, visibleKeys);
                    continue;
                }

                foreach ((Candidate candidate, ChunkEntry child) in children)
                {
                    if (child.Built)
                        AddVisible(candidate.Key, child, visibleKeys);
                }
            }
        }
        else
        {
            // Coarsening LOD: draw the previous finer meshes until their
            // replacement coarse chunk is ready.
            float newChunkSize = ChunkCells * step *
                SparseVoxelField.VoxelSizeMeters;
            float oldChunkSize = ChunkCells * previousStep *
                SparseVoxelField.VoxelSizeMeters;
            foreach ((Candidate candidate, ChunkEntry entry) in candidateEntries)
            {
                if (IsCurrentMeshReady(entry, field))
                {
                    AddVisible(candidate.Key, entry, visibleKeys);
                    continue;
                }

                float fallbackMinX = candidate.Key.X * newChunkSize;
                float fallbackMinY = candidate.Key.Y * newChunkSize;
                float fallbackMinZ = candidate.Key.Z * newChunkSize;
                float fallbackMaxX = fallbackMinX + newChunkSize;
                float fallbackMaxY = fallbackMinY + newChunkSize;
                float fallbackMaxZ = fallbackMinZ + newChunkSize;
                int firstX = (int)MathF.Floor(fallbackMinX / oldChunkSize);
                int firstY = (int)MathF.Floor(fallbackMinY / oldChunkSize);
                int firstZ = (int)MathF.Floor(fallbackMinZ / oldChunkSize);
                int lastX = (int)MathF.Floor((fallbackMaxX - 0.0001f) / oldChunkSize);
                int lastY = (int)MathF.Floor((fallbackMaxY - 0.0001f) / oldChunkSize);
                int lastZ = (int)MathF.Floor((fallbackMaxZ - 0.0001f) / oldChunkSize);
                bool addedFineFallback = false;

                for (int z = firstZ; z <= lastZ; z++)
                for (int y = firstY; y <= lastY; y++)
                for (int x = firstX; x <= lastX; x++)
                {
                    ChunkKey fineKey = new(previousStep, x, y, z);
                    if (!_chunks.TryGetValue(fineKey, out ChunkEntry? fine) || !fine.Built)
                        continue;

                    AddVisible(fineKey, fine, visibleKeys);
                    addedFineFallback = true;
                }

                if (!addedFineFallback && entry.Built)
                    AddVisible(candidate.Key, entry, visibleKeys);
            }
        }

        if (_chunks.Count > MaximumCachedChunks)
            EvictUnusedChunks();

        SetLighting();
        SetCutShader(cutEnabled, effectiveCutHeight);

        float worldRadius = 0.5f * MathF.Sqrt(
            field.Width * field.Width * SparseVoxelField.VoxelSizeMeters * SparseVoxelField.VoxelSizeMeters +
            field.Depth * field.Depth * SparseVoxelField.VoxelSizeMeters * SparseVoxelField.VoxelSizeMeters +
            field.Height * field.Height * SparseVoxelField.VoxelSizeMeters * SparseVoxelField.VoxelSizeMeters);
        float distanceToOrigin = camera.Position.Length();
        Rlgl.SetClipPlanes(
            Math.Clamp(cameraDistance * 0.002f, 0.02f, 2f),
            MathF.Max(1000f, distanceToOrigin + worldRadius + 100f));

        BeginMode3D(camera);
        int draws = 0;
        foreach ((ChunkKey _, ChunkEntry entry) in _visible)
        {
            foreach (Mesh mesh in entry.Surface)
            {
                DrawMesh(mesh, _material, Matrix4x4.Identity);
                draws++;
            }
        }

        if (cutEnabled)
        {
            SetCutShader(enabled: false, cutHeightMeters);
            foreach ((ChunkKey _, ChunkEntry entry) in _visible)
            {
                if (entry.CutLayer != cutLayer)
                    continue;

                foreach (Mesh mesh in entry.CutCap)
                {
                    DrawMesh(mesh, _material, Matrix4x4.Identity);
                    draws++;
                }
            }
        }

        EndMode3D();

        DrawCallsLastFrame = draws;
        VisibleChunksLastFrame = _visible.Count;

        if ((_frame % 90) == 0 && _chunks.Count > MaximumCachedChunks / 2)
            EvictUnusedChunks();
    }

    private static int SelectLod(float cameraDistance, int currentStep)
    {
        // Small dead bands stop the mesh tier from oscillating around thresholds.
        return currentStep switch
        {
            1 => cameraDistance < 28f ? 1 :
                 cameraDistance < 105f ? 5 :
                 cameraDistance < 450f ? 10 : 40,
            5 => cameraDistance < 24f ? 1 :
                 cameraDistance < 105f ? 5 :
                 cameraDistance < 450f ? 10 : 40,
            10 => cameraDistance < 24f ? 1 :
                  cameraDistance < 90f ? 5 :
                  cameraDistance < 450f ? 10 : 40,
            40 => cameraDistance < 24f ? 1 :
                  cameraDistance < 90f ? 5 :
                  cameraDistance < 390f ? 10 : 40,
            _ => cameraDistance < 26f ? 1 :
                 cameraDistance < 95f ? 5 :
                 cameraDistance < 420f ? 10 : 40
        };
    }

    private bool IsCurrentMeshReady(ChunkEntry entry, SparseVoxelField field) =>
        entry.Built &&
        !entry.SurfaceDirty &&
        entry.BaseVersion == field.BaseVersion &&
        entry.PaletteVersion == _paletteVersion;

    private void AddVisible(
        ChunkKey key,
        ChunkEntry entry,
        HashSet<ChunkKey> visibleKeys)
    {
        if (entry.Built && visibleKeys.Add(key))
            _visible.Add((key, entry));
    }

    private static float GetViewRadius(
        int step,
        float cameraDistance,
        float chunkSizeMeters,
        float maximumViewDistanceMeters)
    {
        float minimum = step switch
        {
            1 => 8f,
            5 => 30f,
            10 => 90f,
            _ => 300f
        };
        float multiplier = step switch
        {
            1 => 0.9f,
            5 => 1.0f,
            10 => 1.15f,
            _ => 1.4f
        };

        return Math.Clamp(
            MathF.Max(minimum, cameraDistance * multiplier),
            chunkSizeMeters * 2f,
            Math.Clamp(maximumViewDistanceMeters, minimum, 18000f));
    }

    private static int DivideRoundUp(int value, int divisor) =>
        (value + divisor - 1) / divisor;

    private void SetLighting()
    {
        Vector3 direction = _palette.GetSunDirection();
        SetShaderValue(_shader, _sunDirectionLocation, direction, ShaderUniformDataType.Vec3);
        SetShaderValue(_shader, _ambientLightLocation, _palette.AmbientLight, ShaderUniformDataType.Float);
        SetShaderValue(_shader, _sunIntensityLocation, _palette.SunIntensity, ShaderUniformDataType.Float);
    }

    private void SetCutShader(bool enabled, float height)
    {
        SetShaderValue(
            _shader,
            _cutEnabledLocation,
            enabled ? 1 : 0,
            ShaderUniformDataType.Int);
        SetShaderValue(
            _shader,
            _cutHeightLocation,
            height,
            ShaderUniformDataType.Float);
    }

    private static void ClearCutCap(ChunkEntry entry)
    {
        foreach (Mesh mesh in entry.CutCap)
            UnloadMesh(mesh);
        entry.CutCap = Array.Empty<Mesh>();
        entry.CutLayer = int.MinValue;
        entry.CutDirty = true;
    }

    private void EvictUnusedChunks()
    {
        long threshold = _frame - 240;
        List<ChunkKey>? remove = null;

        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
        {
            if (entry.LastSeenFrame >= threshold)
                continue;

            (remove ??= new List<ChunkKey>()).Add(key);
        }

        if (remove == null)
            return;

        foreach (ChunkKey key in remove)
        {
            _chunks[key].Dispose();
            _chunks.Remove(key);
        }
    }

    private void EnsureFieldSubscription(SparseVoxelField field)
    {
        if (ReferenceEquals(_observedField, field))
            return;

        if (_observedField != null)
            _observedField.Changed -= OnVoxelFieldChanged;

        _observedField = field;
        _observedField.Changed += OnVoxelFieldChanged;
    }

    private void OnVoxelFieldChanged(SparseVoxelField.VoxelBounds bounds)
    {
        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
        {
            int span = ChunkCells * key.Step;
            int firstX = key.X * span - key.Step;
            int firstY = key.Y * span - key.Step;
            int firstZ = key.Z * span - key.Step;
            int lastX = (key.X * ChunkCells + ChunkCells + 1) * key.Step + 1;
            int lastY = (key.Y * ChunkCells + ChunkCells + 1) * key.Step + 1;
            int lastZ = (key.Z * ChunkCells + ChunkCells + 1) * key.Step + 1;

            bool intersectsX = Intersects(firstX, lastX, bounds.MinX, bounds.MaxX);
            bool intersectsY = Intersects(firstY, lastY, bounds.MinY, bounds.MaxY);
            bool intersectsZ = Intersects(firstZ, lastZ, bounds.MinZ, bounds.MaxZ);

            if (intersectsX && intersectsY && intersectsZ)
                entry.SurfaceDirty = true;

            if (entry.CutLayer != int.MinValue && intersectsX && intersectsZ)
            {
                int cutSample = entry.CutLayer - 1;
                if (bounds.MinY <= cutSample + 1 && bounds.MaxY > cutSample)
                    entry.CutDirty = true;
            }
        }
    }

    private static bool Intersects(int minA, int maxA, int minB, int maxB) =>
        minA < maxB && minB < maxA;

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_observedField != null)
            _observedField.Changed -= OnVoxelFieldChanged;
        _observedField = null;

        foreach (ChunkEntry entry in _chunks.Values)
            entry.Dispose();
        _chunks.Clear();
        // UnloadMaterial also releases the non-default shader assigned to it.
        UnloadMaterial(_material);
        _disposed = true;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
