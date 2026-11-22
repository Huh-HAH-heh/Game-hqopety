using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
    private const int MaximumLodPrewarmBuildsPerFrame = 2;

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
        public float PriorityDistanceSquared = float.PositiveInfinity;
        public bool Built;

        public int RequestRevision;
        public int FailedRevision = int.MinValue;
        public int CaptureRevision;
        public int CapturePaletteVersion;
        public long CaptureBaseVersion;
        public bool CaptureCutCap;
        public int CaptureCutLayer = int.MinValue;
        public bool BuildQueued;
        public bool NeedsCapture;
        public VoxelChunkBuildInput? Capture;
        public BuildRequest? RunningBuild;
        public VoxelMeshBuildResult? ReadyBuild;
        public BuildRequest? ReadyRequest;
        public List<Mesh> UploadingSurface = new();
        public List<Mesh> UploadingCutCap = new();
        public int SurfaceUploadIndex;
        public int CutUploadIndex;

        public void Dispose()
        {
            foreach (Mesh mesh in Surface)
                UnloadMesh(mesh);
            foreach (Mesh mesh in CutCap)
                UnloadMesh(mesh);
            foreach (Mesh mesh in UploadingSurface)
                UnloadMesh(mesh);
            foreach (Mesh mesh in UploadingCutCap)
                UnloadMesh(mesh);
            Surface = Array.Empty<Mesh>();
            CutCap = Array.Empty<Mesh>();
            UploadingSurface.Clear();
            UploadingCutCap.Clear();
            Capture = null;
            ReadyBuild = null;
            Built = false;
        }
    }

    private sealed class BuildRequest
    {
        public required ChunkKey Key { get; init; }
        public required ChunkEntry Entry { get; init; }
        public required VoxelChunkBuildInput Input { get; init; }
        public required int RequestRevision { get; init; }
        public required int PaletteVersion { get; init; }
        public required long BaseVersion { get; init; }
        public required bool BuildCutCap { get; init; }
        public required int CutLayer { get; init; }
        public required TerrainPalette Palette { get; init; }
        public Task<VoxelMeshBuildResult>? Task { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
    }

    private readonly record struct VoxelMeshBuildResult(
        VoxelMeshData[] Surface,
        VoxelMeshData[] CutCap,
        bool HasCutCap,
        int CutLayer);

    private readonly record struct Candidate(
        ChunkKey Key,
        Vector3 Center,
        float Radius,
        float DistanceSquared);

    private readonly Dictionary<ChunkKey, ChunkEntry> _chunks = new();
    private readonly Queue<BuildRequest> _buildQueue = new();
    private readonly List<BuildRequest> _runningBuilds = new();
    private const int MaximumMeshWorkers = 2;
    private const int MaximumPendingMeshJobs = 48;
    private const double CaptureBudgetMilliseconds = 0.25;
    private const double GpuUploadBudgetMilliseconds = 0.55;
    private int _activeMeshWorkers;
    private int _pendingCaptures;
    private readonly DynamicVoxelFragmentRenderer _dynamicFragments = new();
    private SparseVoxelField? _observedField;
    private readonly List<(ChunkKey Key, ChunkEntry Entry)> _visible = new();
    private readonly HashSet<ChunkKey> _visibleCandidateKeys = new();
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
    private int _requestedCutLayer = int.MinValue;
    private int _stableCutLayer = int.MinValue;
    private int _cutLayerStableFrames;

    // A moving section plane changes every 0.1 m. Rebuilding the cap meshes
    // for each intermediate layer creates continuous CPU/GPU work while the
    // user drags the slice control. Wait until the selected layer settles.
    private const int CutCapSettleFrames = 8;

    public int ActiveRenderCellSize => _activeStep;
    public float ActiveVoxelSizeMeters => _activeStep * SparseVoxelField.VoxelSizeMeters;
    public bool IsUsingCoarseLod => _activeStep >= 40;
    public int DrawCallsLastFrame { get; private set; }
    public int VisibleChunksLastFrame { get; private set; }
    public int RebuiltChunks { get; private set; }
    public int RebuiltCutCaps { get; private set; }
    public int LodPrewarmBuilds { get; private set; }
    public double CpuCaptureMillisecondsLastFrame { get; private set; }
    public double GpuUploadMillisecondsLastFrame { get; private set; }
    public int RunningMeshJobs => _activeMeshWorkers;
    public double CpuCaptureMilliseconds => CpuCaptureMillisecondsLastFrame;
    public double GpuUploadMilliseconds => GpuUploadMillisecondsLastFrame;
    public int PendingMeshJobs => _buildQueue.Count + _pendingCaptures;
    public int VisibleCandidatesLastFrame => _visibleCandidateKeys.Count;
    public int CachedChunks => _chunks.Count;
    public int DynamicFragmentCount => _dynamicFragments.Count;
    public bool IsMergingFragments => _dynamicFragments.IsMerging;

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
            entry.RequestRevision++;
            entry.FailedRevision = int.MinValue;
            InvalidatePendingBuild(entry);
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

        _dynamicFragments.Update(field, GetFrameTime());

        _frame++;
        RebuiltChunks = 0;
        RebuiltCutCaps = 0;
        LodPrewarmBuilds = 0;
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
        _visibleCandidateKeys.Clear();
        HashSet<ChunkKey> visibleKeys = new();
        List<(Candidate Candidate, ChunkEntry Entry)> candidateEntries =
            new(candidates.Count);


        int cutSampleY = (int)MathF.Floor(
            cutHeightMeters / SparseVoxelField.VoxelSizeMeters - 0.5f);
        int cutChunkY = cutEnabled
            ? Math.Clamp(cutSampleY / (ChunkCells * step), 0, chunksY - 1)
            : -1;
        int cutLayer = cutEnabled
            ? (int)MathF.Round(cutHeightMeters / SparseVoxelField.VoxelSizeMeters)
            : -1;

        if (!cutEnabled)
        {
            _requestedCutLayer = int.MinValue;
            _stableCutLayer = int.MinValue;
            _cutLayerStableFrames = 0;
        }
        else if (cutLayer != _requestedCutLayer)
        {
            _requestedCutLayer = cutLayer;
            _cutLayerStableFrames = 0;
        }
        else if (_cutLayerStableFrames < CutCapSettleFrames)
        {
            _cutLayerStableFrames++;
        }

        bool cutCapReady = cutEnabled &&
            _cutLayerStableFrames >= CutCapSettleFrames &&
            _stableCutLayer != cutLayer;

        if (cutCapReady)
            _stableCutLayer = cutLayer;
        float effectiveCutHeight = cutEnabled
            ? cutLayer * SparseVoxelField.VoxelSizeMeters
            : cutHeightMeters;

        foreach (Candidate candidate in candidates)
        {
            ChunkKey key = candidate.Key;
            _visibleCandidateKeys.Add(key);
            if (!_chunks.TryGetValue(key, out ChunkEntry? entry))
            {
                entry = new ChunkEntry();
                _chunks.Add(key, entry);
            }

            entry.LastSeenFrame = _frame;
            entry.PriorityDistanceSquared = candidate.DistanceSquared;
            bool surfaceStale = !entry.Built ||
                entry.SurfaceDirty ||
                entry.BaseVersion != field.BaseVersion ||
                entry.PaletteVersion != _paletteVersion;
            bool needsCutCap = cutEnabled &&
                _cutLayerStableFrames >= CutCapSettleFrames &&
                key.Y == cutChunkY &&
                (entry.CutLayer != cutLayer || entry.CutDirty);

            if (surfaceStale || needsCutCap)
                RequestBuild(field, key, entry, needsCutCap, cutLayer);

            if (!cutEnabled && entry.CutCap.Length > 0)
                ClearCutCap(entry);

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

        PrepareAdjacentLodCache(
            field,
            camera,
            viewportWidth,
            viewportHeight,
            maximumViewDistanceMeters,
            step,
            cameraDistance);

        CollectCompletedBuilds(field);
        UploadReadyMeshes();
        double remainingUploadBudget = Math.Max(
            0d,
            GpuUploadBudgetMilliseconds - GpuUploadMillisecondsLastFrame);
        if (remainingUploadBudget > 0d)
        {
            GpuUploadMillisecondsLastFrame +=
                _dynamicFragments.UploadPendingMeshes(remainingUploadBudget);
        }
        ProcessCaptureJobs(field);
        StartQueuedBuilds(field);

        if (_chunks.Count > MaximumCachedChunks)
            EvictUnusedChunks();

        _dynamicFragments.ReleaseBakedBodies(
            bounds => AreCachedMeshesCurrent(bounds, field, step));

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

        SetCutShader(enabled: false, cutHeightMeters);
        _dynamicFragments.Draw(_material);

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

    public int DetachLooseFragments(
        SparseVoxelField field,
        Vector3 centerMeters,
        Vector3 halfExtentsMeters)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(field);
        if (_dynamicFragments.AvailableSlots == 0)
            return 0;

        List<VoxelFragmentSnapshot> fragments = field.DetachLooseComponents(
            centerMeters,
            halfExtentsMeters,
            maximumFragments: 1);

        int detached = 0;
        foreach (VoxelFragmentSnapshot fragment in fragments)
        {
            if (_dynamicFragments.TryAdd(fragment, centerMeters, _palette, field))
            {
                detached++;
            }
            else
            {
                field.RestoreFragment(fragment);
            }
        }

        return detached;
    }

    public void BeginVoxelEdit() => _dynamicFragments.BeginEdit();

    public void EndVoxelEdit(bool commit) => _dynamicFragments.EndEdit(commit);

    public void UndoVoxelEdit() => _dynamicFragments.UndoLastEdit();

    public void RedoVoxelEdit() => _dynamicFragments.RedoLastEdit();

    public void ClearDynamicFragments()
    {
        ThrowIfDisposed();
        _dynamicFragments.Clear();
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

    private void RequestBuild(
        SparseVoxelField field,
        ChunkKey key,
        ChunkEntry entry,
        bool buildCutCap,
        int cutLayer)
    {
        if (entry.RunningBuild != null ||
            entry.Capture != null ||
            entry.BuildQueued ||
            entry.ReadyBuild.HasValue ||
            entry.FailedRevision == entry.RequestRevision ||
            _pendingCaptures + _buildQueue.Count >= MaximumPendingMeshJobs)
            return;

        entry.NeedsCapture = true;
        entry.CaptureRevision = entry.RequestRevision;
        entry.CapturePaletteVersion = _paletteVersion;
        entry.CaptureBaseVersion = field.BaseVersion;
        entry.CaptureCutCap = buildCutCap;
        entry.CaptureCutLayer = buildCutCap ? cutLayer : int.MinValue;
        _pendingCaptures++;
    }

    private void ProcessCaptureJobs(SparseVoxelField field)
    {
        Stopwatch timer = Stopwatch.StartNew();
        CpuCaptureMillisecondsLastFrame = 0d;

        List<(ChunkKey Key, ChunkEntry Entry)> pending = new();
        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
        {
            if (entry.NeedsCapture || entry.Capture != null)
                pending.Add((key, entry));
        }
        pending.Sort(static (a, b) =>
            a.Entry.PriorityDistanceSquared.CompareTo(b.Entry.PriorityDistanceSquared));

        foreach ((ChunkKey key, ChunkEntry entry) in pending)
        {
            if (timer.Elapsed.TotalMilliseconds >= CaptureBudgetMilliseconds)
                break;

            if (entry.CaptureRevision != entry.RequestRevision ||
                entry.CapturePaletteVersion != _paletteVersion ||
                entry.CaptureBaseVersion != field.BaseVersion)
            {
                entry.Capture = null;
                entry.NeedsCapture = false;
                _pendingCaptures = Math.Max(0, _pendingCaptures - 1);
                continue;
            }

            VoxelChunkBuildInput? input = entry.Capture;
            if (input == null && entry.NeedsCapture)
            {
                input = new VoxelChunkBuildInput(
                    field, key.X, key.Y, key.Z, key.Step);
                entry.Capture = input;
            }
            if (input == null)
                continue;

            // Check the clock every 16 samples. Field sampling is the only
            // render-thread CPU work in this stage; it is deliberately sliced
            // across frames and never starts a complete chunk capture at once.
            while (!input.IsComplete &&
                timer.Elapsed.TotalMilliseconds < CaptureBudgetMilliseconds)
            {
                input.CaptureNext(field, 16);
            }

            if (!input.IsComplete)
                continue;

            BuildRequest request = new()
            {
                Key = new ChunkKey(input.Step, input.ChunkX, input.ChunkY, input.ChunkZ),
                Entry = entry,
                Input = input,
                RequestRevision = entry.CaptureRevision,
                PaletteVersion = entry.CapturePaletteVersion,
                BaseVersion = entry.CaptureBaseVersion,
                BuildCutCap = entry.CaptureCutCap,
                CutLayer = entry.CaptureCutLayer,
                Palette = _palette
            };
            entry.Capture = null;
            entry.NeedsCapture = false;
            _pendingCaptures--;
            entry.BuildQueued = true;
            _buildQueue.Enqueue(request);
        }

        CpuCaptureMillisecondsLastFrame = timer.Elapsed.TotalMilliseconds;
    }

    private void StartQueuedBuilds(SparseVoxelField field)
    {
        while (_activeMeshWorkers < MaximumMeshWorkers &&
            _buildQueue.Count > 0)
        {
            BuildRequest request = _buildQueue.Dequeue();
            ChunkEntry entry = request.Entry;
            if (!entry.BuildQueued ||
                entry.RequestRevision != request.RequestRevision ||
                entry.CapturePaletteVersion != request.PaletteVersion ||
                entry.CaptureBaseVersion != request.BaseVersion ||
                !_chunks.TryGetValue(request.Key, out ChunkEntry? current) ||
                !ReferenceEquals(current, entry))
                continue;

            entry.BuildQueued = false;
            if (request.BaseVersion != field.BaseVersion ||
                request.PaletteVersion != _paletteVersion)
                continue;

            entry.RunningBuild = request;
            request.Task = Task.Run(() =>
            {
                CancellationToken token = request.Cancellation.Token;
                VoxelMeshData[] surface =
                    VoxelChunkMesher.BuildSurfaceData(
                        request.Input, request.Palette, token);
                VoxelMeshData[] cutCap = request.BuildCutCap
                    ? VoxelChunkMesher.BuildCutCapData(
                        request.Input,
                        request.CutLayer * SparseVoxelField.VoxelSizeMeters,
                        request.Palette,
                        token)
                    : Array.Empty<VoxelMeshData>();
                return new VoxelMeshBuildResult(
                    surface, cutCap, request.BuildCutCap, request.CutLayer);
            });

            _runningBuilds.Add(request);
            _activeMeshWorkers++;
        }
    }

    private void CollectCompletedBuilds(SparseVoxelField field)
    {
        for (int i = _runningBuilds.Count - 1; i >= 0; i--)
        {
            BuildRequest request = _runningBuilds[i];
            Task<VoxelMeshBuildResult>? task = request.Task;
            if (task == null || !task.IsCompleted)
                continue;

            _runningBuilds.RemoveAt(i);
            _activeMeshWorkers--;
            request.Cancellation.Dispose();
            ChunkEntry entry = request.Entry;
            if (ReferenceEquals(entry.RunningBuild, request))
                entry.RunningBuild = null;

            if (task.IsFaulted || task.IsCanceled)
            {
                if (request.RequestRevision == entry.RequestRevision)
                    entry.FailedRevision = request.RequestRevision;
                continue;
            }

            if (!_chunks.TryGetValue(request.Key, out ChunkEntry? current) ||
                !ReferenceEquals(current, entry) ||
                request.RequestRevision != entry.RequestRevision ||
                request.PaletteVersion != _paletteVersion ||
                request.BaseVersion != field.BaseVersion)
                continue;

            entry.ReadyBuild = task.GetAwaiter().GetResult();
            entry.ReadyRequest = request;
            entry.SurfaceUploadIndex = 0;
            entry.CutUploadIndex = 0;
            entry.UploadingSurface.Clear();
            entry.UploadingCutCap.Clear();
        }
    }

    private void UploadReadyMeshes()
    {
        Stopwatch timer = Stopwatch.StartNew();
        GpuUploadMillisecondsLastFrame = 0d;

        // Visible chunks are uploaded before speculative prewarm chunks.
        List<ChunkEntry> ready = new();
        HashSet<ChunkEntry> seen = new();
        foreach ((ChunkKey _, ChunkEntry entry) in _visible)
        {
            if (entry.ReadyBuild.HasValue && seen.Add(entry))
                ready.Add(entry);
        }
        foreach (ChunkEntry entry in _chunks.Values)
        {
            if (entry.ReadyBuild.HasValue && seen.Add(entry))
                ready.Add(entry);
        }

        foreach (ChunkEntry entry in ready)
        {
            VoxelMeshBuildResult result = entry.ReadyBuild!.Value;
            BuildRequest? request = entry.ReadyRequest;
            if (request == null ||
                request.RequestRevision != entry.RequestRevision ||
                request.PaletteVersion != _paletteVersion)
            {
                InvalidatePendingBuild(entry);
                continue;
            }

            while (entry.SurfaceUploadIndex < result.Surface.Length)
            {
                Mesh mesh = VoxelChunkMesher.UploadOne(
                    result.Surface[entry.SurfaceUploadIndex++]);
                entry.UploadingSurface.Add(mesh);
                if (timer.Elapsed.TotalMilliseconds >= GpuUploadBudgetMilliseconds)
                    break;
            }

            if (entry.SurfaceUploadIndex < result.Surface.Length ||
                timer.Elapsed.TotalMilliseconds >= GpuUploadBudgetMilliseconds)
                break;

            if (result.HasCutCap)
            {
                while (entry.CutUploadIndex < result.CutCap.Length)
                {
                    Mesh mesh = VoxelChunkMesher.UploadOne(
                        result.CutCap[entry.CutUploadIndex++]);
                    entry.UploadingCutCap.Add(mesh);
                    if (timer.Elapsed.TotalMilliseconds >= GpuUploadBudgetMilliseconds)
                        break;
                }

                if (entry.CutUploadIndex < result.CutCap.Length ||
                    timer.Elapsed.TotalMilliseconds >= GpuUploadBudgetMilliseconds)
                    break;
            }

            Mesh[] previousSurface = entry.Surface;
            entry.Surface = entry.UploadingSurface.ToArray();
            entry.UploadingSurface.Clear();
            foreach (Mesh mesh in previousSurface)
                UnloadMesh(mesh);

            entry.BaseVersion = request.BaseVersion;
            entry.PaletteVersion = request.PaletteVersion;
            entry.SurfaceDirty = false;
            entry.Built = true;

            if (result.HasCutCap)
            {
                Mesh[] previousCut = entry.CutCap;
                entry.CutCap = entry.UploadingCutCap.ToArray();
                entry.UploadingCutCap.Clear();
                foreach (Mesh mesh in previousCut)
                    UnloadMesh(mesh);

                entry.CutLayer = result.CutLayer;
                entry.CutDirty = false;
                RebuiltCutCaps++;
            }

            RebuiltChunks++;
            if (request.Key.Step != _activeStep)
                LodPrewarmBuilds++;
            entry.ReadyBuild = null;
            entry.ReadyRequest = null;
            entry.SurfaceUploadIndex = 0;
            entry.CutUploadIndex = 0;

            if (timer.Elapsed.TotalMilliseconds >= GpuUploadBudgetMilliseconds)
                break;
        }

        GpuUploadMillisecondsLastFrame = timer.Elapsed.TotalMilliseconds;
    }

    private void InvalidatePendingBuild(ChunkEntry entry)
    {
        entry.RunningBuild?.Cancellation.Cancel();
        if (entry.NeedsCapture || entry.Capture != null)
        {
            entry.Capture = null;
            entry.NeedsCapture = false;
            _pendingCaptures = Math.Max(0, _pendingCaptures - 1);
        }
        entry.BuildQueued = false;
        entry.ReadyBuild = null;
        entry.ReadyRequest = null;
        entry.SurfaceUploadIndex = 0;
        entry.CutUploadIndex = 0;

        foreach (Mesh mesh in entry.UploadingSurface)
            UnloadMesh(mesh);
        foreach (Mesh mesh in entry.UploadingCutCap)
            UnloadMesh(mesh);
        entry.UploadingSurface.Clear();
        entry.UploadingCutCap.Clear();
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
        {
            entry.LastSeenFrame = _frame;
            _visible.Add((key, entry));
        }
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

    private bool AreCachedMeshesCurrent(
        SparseVoxelField.VoxelBounds bounds,
        SparseVoxelField field,
        int step)
    {
        int span = ChunkCells * step;
        foreach (ChunkKey key in _visibleCandidateKeys)
        {
            if (key.Step != step)
                continue;

            int minX = key.X * span;
            int minY = key.Y * span;
            int minZ = key.Z * span;
            int maxX = minX + span;
            int maxY = minY + span;
            int maxZ = minZ + span;
            bool overlaps =
                minX < bounds.MaxX && bounds.MinX < maxX &&
                minY < bounds.MaxY && bounds.MinY < maxY &&
                minZ < bounds.MaxZ && bounds.MinZ < maxZ;
            if (!overlaps)
                continue;

            if (!_chunks.TryGetValue(key, out ChunkEntry? entry) ||
                !IsCurrentMeshReady(entry, field))
                return false;
        }

        return true;
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
        HashSet<ChunkKey> protectedKeys = new();
        foreach ((ChunkKey key, ChunkEntry _) in _visible)
            protectedKeys.Add(key);

        long threshold = _frame - 240;
        List<ChunkKey>? remove = null;

        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
        {
            if (protectedKeys.Contains(key) || entry.LastSeenFrame >= threshold ||
                entry.RunningBuild != null || entry.Capture != null ||
                entry.ReadyBuild.HasValue)
                continue;
            (remove ??= new List<ChunkKey>()).Add(key);
        }

        if (remove != null)
        {
            foreach (ChunkKey key in remove)
            {
                if (_chunks.Remove(key, out ChunkEntry? entry))
                    entry.Dispose();
            }
        }

        // The previous implementation used the 1,800 entry value only as a
        // trigger for age-based eviction. Recent chunks from multiple LOD tiers
        // could therefore grow without bound (e.g. 2,851 cached chunks). Enforce
        // the actual budget with LRU eviction while pinning everything drawn.
        if (_chunks.Count <= MaximumCachedChunks)
            return;

        List<(ChunkKey Key, long LastSeen)> candidates = new();
        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
        {
            if (!protectedKeys.Contains(key))
                candidates.Add((key, entry.LastSeenFrame));
        }

        candidates.Sort(static (a, b) => a.LastSeen.CompareTo(b.LastSeen));
        int removeCount = _chunks.Count - MaximumCachedChunks;
        for (int i = 0; i < candidates.Count && removeCount > 0; i++)
        {
            ChunkKey key = candidates[i].Key;
            if (!_chunks.Remove(key, out ChunkEntry? entry))
                continue;
            entry.Dispose();
            removeCount--;
        }
    }

    private void PrepareAdjacentLodCache(
        SparseVoxelField field,
        Camera3D camera,
        int viewportWidth,
        int viewportHeight,
        float maximumViewDistanceMeters,
        int currentStep,
        float cameraDistance)
    {
        int targetStep = GetAdjacentPrewarmStep(currentStep, cameraDistance);
        if (targetStep == 0)
            return;

        float chunkSize = ChunkCells * targetStep * SparseVoxelField.VoxelSizeMeters;
        int chunksX = DivideRoundUp(field.Width, ChunkCells * targetStep);
        int chunksY = DivideRoundUp(field.Height, ChunkCells * targetStep);
        int chunksZ = DivideRoundUp(field.Depth, ChunkCells * targetStep);

        int centerX = (int)MathF.Floor(camera.Target.X / chunkSize);
        int centerY = (int)MathF.Floor(camera.Target.Y / chunkSize);
        int centerZ = (int)MathF.Floor(camera.Target.Z / chunkSize);

        List<Candidate> warmCandidates = new(27);
        for (int z = -1; z <= 1; z++)
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            int chunkX = centerX + x;
            int chunkY = centerY + y;
            int chunkZ = centerZ + z;
            if (chunkX < 0 || chunkY < 0 || chunkZ < 0 ||
                chunkX >= chunksX || chunkY >= chunksY || chunkZ >= chunksZ)
                continue;

            Vector3 center = new(
                (chunkX + 0.5f) * chunkSize,
                (chunkY + 0.5f) * chunkSize,
                (chunkZ + 0.5f) * chunkSize);
            float radius = chunkSize * 0.8660254f;
            if (!TerrainFrustum.IsSphereVisible(
                    camera, center, radius,
                    viewportWidth, viewportHeight,
                    maximumViewDistanceMeters))
                continue;

            warmCandidates.Add(new Candidate(
                new ChunkKey(targetStep, chunkX, chunkY, chunkZ),
                center,
                radius,
                Vector3.DistanceSquared(center, camera.Position)));
        }

        warmCandidates.Sort(static (a, b) =>
            a.DistanceSquared.CompareTo(b.DistanceSquared));

        int builds = 0;
        foreach (Candidate candidate in warmCandidates)
        {
            if (builds >= MaximumLodPrewarmBuildsPerFrame)
                break;

            ChunkKey key = candidate.Key;
            if (!_chunks.TryGetValue(key, out ChunkEntry? entry))
            {
                entry = new ChunkEntry();
                _chunks.Add(key, entry);
            }

            entry.LastSeenFrame = _frame;
            entry.PriorityDistanceSquared = candidate.DistanceSquared;
            bool stale = !entry.Built ||
                entry.SurfaceDirty ||
                entry.BaseVersion != field.BaseVersion ||
                entry.PaletteVersion != _paletteVersion;
            if (!stale)
                continue;

            RequestBuild(field, key, entry, buildCutCap: false, cutLayer: -1);
            builds++;
        }
    }

    private static int GetAdjacentPrewarmStep(int currentStep, float distance)
    {
        if (currentStep == 1)
            return distance >= 20f ? 5 : 0;

        if (currentStep == 5)
        {
            if (distance <= 32f)
                return 1;
            return distance >= 82f ? 10 : 0;
        }

        if (currentStep == 10)
        {
            if (distance <= 105f)
                return 5;
            return distance >= 370f ? 40 : 0;
        }

        if (currentStep == 40)
        {
            if (distance <= 32f)
                return 1;
            return distance <= 450f ? 10 : 0;
        }

        return 0;
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

            bool surfaceChanged = intersectsX && intersectsY && intersectsZ;
            bool cutChanged = false;
            if (entry.CutLayer != int.MinValue && intersectsX && intersectsZ)
            {
                int cutSample = entry.CutLayer - 1;
                cutChanged =
                    bounds.MinY <= cutSample + 1 &&
                    bounds.MaxY > cutSample;
            }

            if (surfaceChanged || cutChanged)
            {
                if (surfaceChanged)
                    entry.SurfaceDirty = true;
                if (cutChanged)
                    entry.CutDirty = true;

                entry.RequestRevision++;
                entry.FailedRevision = int.MinValue;
                InvalidatePendingBuild(entry);
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
        _dynamicFragments.Dispose();
        _chunks.Clear();
        // UnloadMaterial also releases the non-default shader assigned to it.
        UnloadMaterial(_material);
        _disposed = true;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
