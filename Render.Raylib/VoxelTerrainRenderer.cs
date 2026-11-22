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
    private const int MaximumCachedChunks = 2400;
    private const int MaximumVisibleCandidates = 1800;
    private const int MaximumLodPrewarmBuildsPerFrame = 1;

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
        public bool CaptureSurface;
        public bool CaptureCutCap;
        public int CaptureCutLayer = int.MinValue;
        public bool CaptureIsPrewarm;
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
            RunningBuild?.Cancellation.Cancel();
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
        public required bool BuildSurface { get; init; }
        public required bool BuildCutCap { get; init; }
        public required int CutLayer { get; init; }
        public required TerrainPalette Palette { get; init; }
        public Task<VoxelMeshBuildResult>? Task { get; set; }
        public long EnqueuedTimestamp { get; init; }
        public bool IsPrewarm { get; init; }
        public CancellationTokenSource Cancellation { get; } = new();
    }

    private readonly record struct VoxelMeshBuildResult(
        VoxelMeshData[] Surface,
        VoxelMeshData[] CutCap,
        bool HasSurface,
        bool HasCutCap,
        int CutLayer,
        double WorkerMilliseconds,
        int VertexCount,
        int TriangleCount);

    private readonly record struct BuildIdentity(
        ChunkKey Key,
        int RequestRevision,
        long BaseVersion,
        int PaletteVersion,
        bool BuildSurface,
        bool BuildCutCap,
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
    private const double CaptureBudgetMilliseconds = 1.25;
    private const double GpuUploadBudgetMilliseconds = 1.0;
    private int _activeMeshWorkers;
    private int _pendingCaptures;
    private readonly DynamicVoxelFragmentRenderer _dynamicFragments = new();
    private readonly TerrainPipelineLogger _logger;
    private readonly Dictionary<BuildIdentity, int> _completedBuildCounts = new();
    private long _totalBuildRequests;
    private long _totalBuildStarted;
    private long _totalBuildCompleted;
    private long _totalBuildCancelled;
    private long _totalBuildFailed;
    private long _totalStaleResults;
    private long _totalDuplicateRequestAttempts;
    private long _totalQueueSaturated;
    private long _totalRepeatedBuilds;
    private long _totalGpuUploads;
    private long _totalEmptyChunks;
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
    public bool IsUsingCoarseLod => _activeStep >= 8;
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
    public double WorkerBuildMillisecondsLastFrame { get; private set; }
    public double LastWorkerBuildMilliseconds { get; private set; }
    public double LastWorkerQueueWaitMilliseconds { get; private set; }
    public int BuildsCompletedLastFrame { get; private set; }
    public int LastBuildStep { get; private set; }
    public int LastBuildVertices { get; private set; }
    public int LastBuildTriangles { get; private set; }
    public int GpuUploadsLastFrame { get; private set; }
    public long TotalBuildRequests => _totalBuildRequests;
    public long TotalBuildStarted => _totalBuildStarted;
    public long TotalBuildCompleted => _totalBuildCompleted;
    public long TotalBuildCancelled => _totalBuildCancelled;
    public long TotalBuildFailed => _totalBuildFailed;
    public long TotalStaleResults => _totalStaleResults;
    public long TotalDuplicateRequestAttempts => _totalDuplicateRequestAttempts;
    public long TotalQueueSaturated => _totalQueueSaturated;
    public long TotalRepeatedBuilds => _totalRepeatedBuilds;
    public long TotalGpuUploads => _totalGpuUploads;
    public long TotalEmptyChunks => _totalEmptyChunks;
    public int FrustumCandidatesLastFrame { get; private set; }
    public int CandidateOverflowLastFrame { get; private set; }
    public string DiagnosticsLogPath => _logger.LogPath;
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

        _logger = new TerrainPipelineLogger();
        _logger.Write(
            "RENDERER_START",
            "lodSteps=1,2,4;legacyQualityProbes=8,40;workers=2;captureBudgetMs=0.25;gpuUploadBudgetMs=0.55");
        Console.WriteLine($"[TERRAIN] Pipeline diagnostics: {_logger.LogPath}");
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
        WorkerBuildMillisecondsLastFrame = 0d;
        BuildsCompletedLastFrame = 0;
        GpuUploadsLastFrame = 0;
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
                RequestBuild(field, key, entry, surfaceStale, needsCutCap, cutLayer, isPrewarm: false);

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

        // Cache pruning is deliberately infrequent. Chunks from the
        // previous camera position remain reusable for four seconds.
        if (_chunks.Count > MaximumCachedChunks && (_frame % 30) == 0)
            EvictUnusedChunks();

        _dynamicFragments.ReleaseBakedBodies(
            bounds => AreCachedMeshesCurrent(bounds, field, step));

        if ((_frame % 120) == 0)
        {
            string summary =
                $"frame={_frame} lod={step} candidates={_visibleCandidateKeys.Count} visible={_visible.Count} cache={_chunks.Count} " +
                $"captureMs={CpuCaptureMillisecondsLastFrame:0.000} workerMs={WorkerBuildMillisecondsLastFrame:0.000} " +
                $"lastWorkerMs={LastWorkerBuildMilliseconds:0.000} queueMs={LastWorkerQueueWaitMilliseconds:0.000} " +
                $"gpuMs={GpuUploadMillisecondsLastFrame:0.000} gpuPackets={GpuUploadsLastFrame} " +
                $"workers={_activeMeshWorkers} pending={_buildQueue.Count + _pendingCaptures} " +
                $"requests={_totalBuildRequests} started={_totalBuildStarted} completed={_totalBuildCompleted} " +
                $"cancelled={_totalBuildCancelled} failed={_totalBuildFailed} stale={_totalStaleResults} " +
                $"repeat={_totalRepeatedBuilds} duplicateReq={_totalDuplicateRequestAttempts} queueFull={_totalQueueSaturated} " +
                $"lastMeshStep={LastBuildStep} vertices={LastBuildVertices} triangles={LastBuildTriangles}";
            _logger.Write("FRAME_SUMMARY", summary);
            Console.WriteLine($"[TERRAIN][PERF] {summary}");
        }

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
        // LOD follows the screen footprint. A full 2.3 km overview cannot use
        // 0.2-0.4 m chunks: that produces hundreds of thousands of 3D candidates.
        return currentStep switch
        {
            1 => cameraDistance < 30f ? 1 :
                 cameraDistance < 80f ? 2 :
                 cameraDistance < 180f ? 4 :
                 cameraDistance < 520f ? 8 :
                 cameraDistance < 1250f ? 16 : 40,
            2 => cameraDistance < 24f ? 1 :
                 cameraDistance < 90f ? 2 :
                 cameraDistance < 190f ? 4 :
                 cameraDistance < 540f ? 8 :
                 cameraDistance < 1280f ? 16 : 40,
            4 => cameraDistance < 60f ? 2 :
                 cameraDistance < 205f ? 4 :
                 cameraDistance < 560f ? 8 :
                 cameraDistance < 1320f ? 16 : 40,
            8 => cameraDistance < 155f ? 4 :
                 cameraDistance < 590f ? 8 :
                 cameraDistance < 1380f ? 16 : 40,
            16 => cameraDistance < 500f ? 8 :
                  cameraDistance < 1450f ? 16 : 40,
            40 => cameraDistance < 1100f ? 16 :
                  cameraDistance < 430f ? 8 :
                  cameraDistance < 180f ? 4 :
                  cameraDistance < 80f ? 2 :
                  cameraDistance < 24f ? 1 : 40,
            _ => cameraDistance < 30f ? 1 :
                 cameraDistance < 90f ? 2 :
                 cameraDistance < 205f ? 4 :
                 cameraDistance < 590f ? 8 :
                 cameraDistance < 1450f ? 16 : 40
        };
    }

    private void RequestBuild(
        SparseVoxelField field,
        ChunkKey key,
        ChunkEntry entry,
        bool buildSurface,
        bool buildCutCap,
        int cutLayer,
        bool isPrewarm)
    {
        if (entry.FailedRevision == entry.RequestRevision)
            return;

        if (entry.RunningBuild != null ||
            entry.NeedsCapture ||
            entry.Capture != null ||
            entry.BuildQueued ||
            entry.ReadyBuild.HasValue)
        {
            _totalDuplicateRequestAttempts++;
            return;
        }

        if (_pendingCaptures + _buildQueue.Count >= MaximumPendingMeshJobs)
        {
            _totalQueueSaturated++;
            return;
        }

        entry.NeedsCapture = true;
        _totalBuildRequests++;
        entry.CaptureRevision = entry.RequestRevision;
        entry.CapturePaletteVersion = _paletteVersion;
        entry.CaptureBaseVersion = field.BaseVersion;
        entry.CaptureSurface = buildSurface;
        entry.CaptureCutCap = buildCutCap;
        entry.CaptureCutLayer = buildCutCap ? cutLayer : int.MinValue;
        entry.CaptureIsPrewarm = isPrewarm;
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
                BuildSurface = entry.CaptureSurface,
                BuildCutCap = entry.CaptureCutCap,
                CutLayer = entry.CaptureCutLayer,
                Palette = _palette,
                EnqueuedTimestamp = Stopwatch.GetTimestamp(),
                IsPrewarm = entry.CaptureIsPrewarm
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
                Stopwatch workerTimer = Stopwatch.StartNew();
                CancellationToken token = request.Cancellation.Token;
                VoxelMeshData[] surface = request.BuildSurface
                    ? VoxelChunkMesher.BuildSurfaceData(
                        request.Input, request.Palette, token)
                    : Array.Empty<VoxelMeshData>();
                VoxelMeshData[] cutCap = request.BuildCutCap
                    ? VoxelChunkMesher.BuildCutCapData(
                        request.Input,
                        request.CutLayer * SparseVoxelField.VoxelSizeMeters,
                        request.Palette,
                        token)
                    : Array.Empty<VoxelMeshData>();
                workerTimer.Stop();

                int vertices = surface.Sum(static mesh => mesh.VertexCount) +
                    cutCap.Sum(static mesh => mesh.VertexCount);
                int triangles = surface.Sum(static mesh => mesh.TriangleCount) +
                    cutCap.Sum(static mesh => mesh.TriangleCount);
                return new VoxelMeshBuildResult(
                    surface,
                    cutCap,
                    request.BuildSurface,
                    request.BuildCutCap,
                    request.CutLayer,
                    workerTimer.Elapsed.TotalMilliseconds,
                    vertices,
                    triangles);
            });

            _runningBuilds.Add(request);
            _activeMeshWorkers++;
            _totalBuildStarted++;
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

            if (task.IsCanceled)
            {
                _totalBuildCancelled++;
                if (request.RequestRevision == entry.RequestRevision)
                    entry.FailedRevision = request.RequestRevision;
                _logger.Write(
                    "MESH_CANCELLED",
                    $"step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) rev={request.RequestRevision}");
                continue;
            }

            if (task.IsFaulted)
            {
                _totalBuildFailed++;
                if (request.RequestRevision == entry.RequestRevision)
                    entry.FailedRevision = request.RequestRevision;
                _logger.Write(
                    "MESH_FAILED",
                    $"step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) error=\"{task.Exception?.GetBaseException().Message}\"");
                continue;
            }

            if (!_chunks.TryGetValue(request.Key, out ChunkEntry? current) ||
                !ReferenceEquals(current, entry) ||
                request.RequestRevision != entry.RequestRevision ||
                request.PaletteVersion != _paletteVersion ||
                request.BaseVersion != field.BaseVersion)
            {
                _totalStaleResults++;
                _logger.Write(
                    "MESH_STALE",
                    $"step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) requestRev={request.RequestRevision} currentRev={entry.RequestRevision} base={request.BaseVersion}/{field.BaseVersion}");
                continue;
            }

            VoxelMeshBuildResult build = task.GetAwaiter().GetResult();
            BuildIdentity identity = new(
                request.Key,
                request.RequestRevision,
                request.BaseVersion,
                request.PaletteVersion,
                request.BuildSurface,
                request.BuildCutCap,
                request.CutLayer);
            int sameRevisionBuilds = _completedBuildCounts.TryGetValue(
                identity, out int previousBuilds)
                    ? previousBuilds + 1
                    : 1;
            _completedBuildCounts[identity] = sameRevisionBuilds;
            if (_completedBuildCounts.Count > 4096)
                _completedBuildCounts.Clear();

            if (sameRevisionBuilds > 1)
            {
                _totalRepeatedBuilds++;
                _logger.Write(
                    "REBUILD_REPEAT",
                    $"count={sameRevisionBuilds} step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) base={request.BaseVersion} palette={request.PaletteVersion}");
            }

            _totalBuildCompleted++;
            BuildsCompletedLastFrame++;
            WorkerBuildMillisecondsLastFrame += build.WorkerMilliseconds;
            LastWorkerBuildMilliseconds = build.WorkerMilliseconds;
            LastWorkerQueueWaitMilliseconds = Math.Max(
                0d,
                Stopwatch.GetElapsedTime(request.EnqueuedTimestamp).TotalMilliseconds -
                    build.WorkerMilliseconds);
            LastBuildStep = request.Key.Step;
            LastBuildVertices = build.VertexCount;
            LastBuildTriangles = build.TriangleCount;
            _logger.Write(
                "MESH_BUILT",
                $"kind={(request.IsPrewarm ? "prewarm" : "visible")} surface={request.BuildSurface} cutCap={request.BuildCutCap} cutLayer={request.CutLayer} step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) rev={request.RequestRevision} base={request.BaseVersion} workerMs={build.WorkerMilliseconds:0.000} queueMs={LastWorkerQueueWaitMilliseconds:0.000} vertices={build.VertexCount} triangles={build.TriangleCount}");

            entry.ReadyBuild = build;
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
            if (timer.Elapsed.TotalMilliseconds >= GpuUploadBudgetMilliseconds)
                break;

            VoxelMeshBuildResult result = entry.ReadyBuild!.Value;
            BuildRequest? request = entry.ReadyRequest;
            if (request == null ||
                request.RequestRevision != entry.RequestRevision ||
                request.PaletteVersion != _paletteVersion)
            {
                InvalidatePendingBuild(entry);
                continue;
            }

            if (result.HasSurface)
            {
                while (entry.SurfaceUploadIndex < result.Surface.Length)
                {
                    Mesh mesh = VoxelChunkMesher.UploadOne(
                        result.Surface[entry.SurfaceUploadIndex++]);
                    entry.UploadingSurface.Add(mesh);
                    GpuUploadsLastFrame++;
                    _totalGpuUploads++;
                    if (timer.Elapsed.TotalMilliseconds >= GpuUploadBudgetMilliseconds)
                        break;
                }

                if (entry.SurfaceUploadIndex < result.Surface.Length ||
                    timer.Elapsed.TotalMilliseconds >= GpuUploadBudgetMilliseconds)
                    break;
            }

            if (result.HasCutCap)
            {
                while (entry.CutUploadIndex < result.CutCap.Length)
                {
                    Mesh mesh = VoxelChunkMesher.UploadOne(
                        result.CutCap[entry.CutUploadIndex++]);
                    entry.UploadingCutCap.Add(mesh);
                    GpuUploadsLastFrame++;
                    _totalGpuUploads++;
                    if (timer.Elapsed.TotalMilliseconds >= GpuUploadBudgetMilliseconds)
                        break;
                }

                if (entry.CutUploadIndex < result.CutCap.Length ||
                    timer.Elapsed.TotalMilliseconds >= GpuUploadBudgetMilliseconds)
                    break;
            }

            // Surface and cut cap are independent products. A cap-only refresh
            // must never replace or invalidate the already-correct base mesh.
            if (result.HasSurface)
            {
                Mesh[] previousSurface = entry.Surface;
                entry.Surface = entry.UploadingSurface.ToArray();
                entry.UploadingSurface.Clear();
                foreach (Mesh mesh in previousSurface)
                    UnloadMesh(mesh);

                entry.BaseVersion = request.BaseVersion;
                entry.PaletteVersion = request.PaletteVersion;
                entry.SurfaceDirty = false;
                entry.Built = true;
                RebuiltChunks++;
            }

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

            if (result.HasSurface && request.IsPrewarm)
                LodPrewarmBuilds++;

            entry.ReadyBuild = null;
            entry.ReadyRequest = null;
            entry.SurfaceUploadIndex = 0;
            entry.CutUploadIndex = 0;
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
            2 => 16f,
            4 => 32f,
            8 => 60f,
            16 => 120f,
            _ => 250f
        };
        float multiplier = step switch
        {
            1 => 0.9f,
            2 => 0.95f,
            4 => 1.0f,
            8 => 1.05f,
            16 => 1.15f,
            _ => 1.25f
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

    private static bool HasPendingWork(ChunkEntry entry) =>
        entry.RunningBuild != null ||
        entry.NeedsCapture ||
        entry.Capture != null ||
        entry.BuildQueued ||
        entry.ReadyBuild.HasValue ||
        entry.UploadingSurface.Count > 0 ||
        entry.UploadingCutCap.Count > 0;

    private void EvictUnusedChunks()
    {
        // Protect every chunk requested by the current view, not only
        // meshes that have already reached DrawMesh. Otherwise startup can
        // evict its own capture/worker jobs before the first mesh is uploaded.
        HashSet<ChunkKey> protectedKeys = new(_visibleCandidateKeys);
        foreach ((ChunkKey key, ChunkEntry _) in _visible)
            protectedKeys.Add(key);

        long threshold = _frame - 240;
        List<ChunkKey>? remove = null;

        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
        {
            if (protectedKeys.Contains(key) || entry.LastSeenFrame >= threshold ||
                HasPendingWork(entry))
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

        // MaximumCachedChunks is a high-water target, not a hard limit.
        // Recently visible LODs stay cached to avoid regenerating them during
        // ordinary zoom and pan reversals. Cold entries are removed above.
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

            RequestBuild(field, key, entry, buildSurface: true, buildCutCap: false, cutLayer: -1, isPrewarm: true);
            builds++;
        }
    }

    private static int GetAdjacentPrewarmStep(int currentStep, float distance)
    {
        if (currentStep == 1)
            return distance >= 26f ? 2 : 0;

        if (currentStep == 2)
        {
            if (distance <= 35f) return 1;
            return distance >= 85f ? 4 : 0;
        }

        if (currentStep == 4)
        {
            if (distance <= 65f) return 2;
            return distance >= 190f ? 8 : 0;
        }

        if (currentStep == 8)
        {
            if (distance <= 160f) return 4;
            return distance >= 550f ? 16 : 0;
        }

        if (currentStep == 16)
        {
            if (distance <= 470f) return 8;
            return distance >= 1300f ? 40 : 0;
        }

        if (currentStep == 40 && distance <= 1150f)
            return 16;

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
        _logger.Write(
            "RENDERER_STOP",
            $"requests={_totalBuildRequests} completed={_totalBuildCompleted} cancelled={_totalBuildCancelled} failed={_totalBuildFailed} stale={_totalStaleResults} repeats={_totalRepeatedBuilds} gpuUploads={_totalGpuUploads}");
        _logger.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
