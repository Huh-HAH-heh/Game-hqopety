using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
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
    private const int LodNearStep = 1;
    private const int LodMiddleStep = 4;
    private const int LodFarStep = 16;
    private const float LodNearRadiusMeters = 64f;
    private const float LodMiddleRadiusMeters = 256f;
    private const float LodNearRadiusSquared = LodNearRadiusMeters * LodNearRadiusMeters;
    private const float LodMiddleRadiusSquared = LodMiddleRadiusMeters * LodMiddleRadiusMeters;
    private const int MaximumCachedChunks = 6000;
    private const float LodNearPrewarmStartMeters = 55f;
    private const float LodNearPrewarmStartSquared =
        LodNearPrewarmStartMeters * LodNearPrewarmStartMeters;
    private const int MaximumGpuMeshUploadsPerFrame = 4;

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
        public long LastAccessFrame;
        public bool Built;

        public int RequestRevision;
        public long BuildGeneration;
        public int FailedRevision = int.MinValue;
        public bool BuildQueued;
        public BuildRequest? RunningBuild;
        public VoxelMeshBuildResult? ReadyBuild;
        public BuildRequest? ReadyRequest;
        public List<Mesh> UploadingSurface = new();
        public List<Mesh> UploadingCutCap = new();
        public int SurfaceUploadIndex;
        public int CutUploadIndex;
        public bool UploadQueued;
        public long UploadQueueGeneration;
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
            ReadyBuild = null;
            Built = false;
        }
    }

    private sealed class BuildRequest
    {
        public required ChunkKey Key { get; init; }
        public required ChunkEntry Entry { get; init; }
        public required IVoxelFieldSource Source { get; init; }
        public required int RequestRevision { get; init; }
        public required long Generation { get; init; }
        public required int PaletteVersion { get; init; }
        public required long BaseVersion { get; init; }
        public required bool BuildSurface { get; init; }
        public required bool BuildCutCap { get; init; }
        public required int CutLayer { get; init; }
        public required TerrainPalette Palette { get; init; }
        public long EnqueuedTimestamp { get; init; }
        public int Started;
        public CancellationTokenSource Cancellation { get; } = new();
    }

    private readonly record struct VoxelMeshBuildResult(
        VoxelMeshData[] Surface,
        VoxelMeshData[] CutCap,
        bool HasSurface,
        bool HasCutCap,
        bool SurfaceWasEmpty,
        int CutLayer,
        double WorkerMilliseconds,
        int VertexCount,
        int TriangleCount,
        ulong SurfaceHash,
        ulong CutCapHash);

    private readonly record struct BuildCompletion(
        BuildRequest Request,
        VoxelMeshBuildResult? Result,
        Exception? Error,
        bool Cancelled,
        long WorkerStartedTimestamp,
        long WorkerCompletedTimestamp);

    private readonly record struct BuildIdentity(
        ChunkKey Key,
        int RequestRevision,
        long BaseVersion,
        int PaletteVersion,
        bool BuildSurface,
        bool BuildCutCap,
        int CutLayer);

    private readonly record struct ChunkGpuHashes(
        bool HasSurface,
        ulong SurfaceHash,
        bool HasCutCap,
        ulong CutCapHash,
        int CutLayer);

    private readonly record struct Candidate(
        ChunkKey Key,
        Vector3 Center,
        float Radius,
        float DistanceSquared);

    private readonly record struct CacheEvictionCandidate(
        ChunkKey Key,
        long LastAccessFrame,
        long LastSeenFrame,
        float DistanceSquared);

    private struct ChildGroupState
    {
        public int Count;
        public bool Ready;
    }

    private readonly record struct ReadyUpload(
        ChunkKey Key,
        ChunkEntry Entry,
        long Generation);

    private readonly Dictionary<ChunkKey, ChunkEntry> _chunks = new();
    private readonly Dictionary<ChunkKey, ChunkGpuHashes> _gpuMeshHashCache = new();

    // Render-thread-owned round-robin queue. A generation token prevents
    // stale records from uploading a removed or recreated LOD chunk.
    private readonly ConcurrentQueue<ReadyUpload> _readyUploadQueue = new();
    private readonly List<Candidate> _candidateScratch = new();
    private readonly List<Candidate> _step16FallbackScratch = new();
    private readonly List<Candidate> _step4FallbackScratch = new();
    private readonly List<Candidate> _step1ParentScratch = new();
    private readonly List<Candidate> _prewarmParentScratch = new();
    private readonly List<Candidate> _prewarmChunkScratch = new(64);
    private readonly HashSet<ChunkKey> _preparedCandidateKeys = new();
    private readonly HashSet<ChunkKey> _cacheProtectedKeys = new();
    private readonly HashSet<ChunkKey> _step1ParentKeys = new();
    private readonly Dictionary<ChunkKey, ChildGroupState> _step16Coverage = new();
    private readonly Dictionary<ChunkKey, ChildGroupState> _step4Coverage = new();
    private readonly List<CacheEvictionCandidate> _cacheEvictionScratch = new();
    private readonly ConcurrentQueue<BuildRequest> _buildQueue = new();
    private readonly ConcurrentQueue<BuildCompletion> _completedBuildQueue = new();
    private readonly SemaphoreSlim _buildSignal = new(0);
    private readonly CancellationTokenSource _workerShutdown = new();
    private readonly Thread[] _workerThreads;
    private static readonly int MaximumMeshWorkers =
        Math.Clamp(Environment.ProcessorCount - 2, 2, 6);
    private const int MaximumPendingMeshJobs = 128;
    private const double GpuUploadBudgetMilliseconds = 3.0;
    private int _activeMeshWorkers;
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
    private long _totalGpuUnloads;
    private long _totalIdenticalSurfaceSkips;
    private long _totalIdenticalCutSkips;
    private long _totalEmptyChunks;
    private SparseVoxelField? _observedField;
    private readonly List<(ChunkKey Key, ChunkEntry Entry)> _visible = new();
    private readonly List<(ChunkKey Key, ChunkEntry Entry)> _previousVisible = new();
    private readonly HashSet<ChunkKey> _visibleCandidateKeys = new();
    private readonly HashSet<ChunkKey> _farFallbackHeldKeys = new();
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
    private int _activeStep = LodFarStep;
    private bool _isUsingCoarseLod;
    private int _requestedCutLayer = int.MinValue;
    private int _stableCutLayer = int.MinValue;
    private int _cutLayerStableFrames;

    // A moving section plane changes every 0.1 m. Rebuilding the cap meshes
    // for each intermediate layer creates continuous CPU/GPU work while the
    // user drags the slice control. Wait until the selected layer settles.
    private const int CutCapSettleFrames = 8;

    public int ActiveRenderCellSize => _activeStep;
    public float ActiveVoxelSizeMeters => _activeStep * SparseVoxelField.VoxelSizeMeters;
    public bool IsUsingCoarseLod => _isUsingCoarseLod;
    public int Step1ChunksLastFrame { get; private set; }
    public int Step4ChunksLastFrame { get; private set; }
    public int Step16ChunksLastFrame { get; private set; }
    public int DrawCallsLastFrame { get; private set; }
    public int VisibleChunksLastFrame { get; private set; }
    public int RebuiltChunks { get; private set; }
    public int RebuiltCutCaps { get; private set; }
    public int LodPrewarmBuilds { get; private set; }
    public double CpuCaptureMillisecondsLastFrame { get; private set; }
    public double GpuUploadMillisecondsLastFrame { get; private set; }
    public int RunningMeshJobs => Volatile.Read(ref _activeMeshWorkers);
    public double CpuCaptureMilliseconds => CpuCaptureMillisecondsLastFrame;
    public double GpuUploadMilliseconds => GpuUploadMillisecondsLastFrame;
    public int PendingMeshJobs => _buildQueue.Count;
    public int CompletedMeshResultsPending => _completedBuildQueue.Count;
    public double WorkerBuildMillisecondsLastFrame { get; private set; }
    public double LastWorkerBuildMilliseconds { get; private set; }
    public double LastWorkerQueueWaitMilliseconds { get; private set; }
    public double LastWorkerResultDeliveryMilliseconds { get; private set; }
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
    public long TotalGpuUnloads => _totalGpuUnloads;
    public long TotalIdenticalSurfaceSkips => _totalIdenticalSurfaceSkips;
    public long TotalIdenticalCutSkips => _totalIdenticalCutSkips;
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
        _workerThreads = new Thread[MaximumMeshWorkers];
        for (int i = 0; i < _workerThreads.Length; i++)
        {
            _workerThreads[i] = new Thread(ProcessBuildQueue)
            {
                IsBackground = true,
                Name = $"RimClone voxel mesh {i + 1}"
            };
            _workerThreads[i].Start();
        }

        _logger.Write(
            "RENDERER_START",
            $"lodSteps={LodNearStep},{LodMiddleStep},{LodFarStep};lodRadii={LodNearRadiusMeters:0}/{LodMiddleRadiusMeters:0};workers={MaximumMeshWorkers};queue={MaximumPendingMeshJobs};gpuUploadBudgetMs={GpuUploadBudgetMilliseconds:0.0};queue=ConcurrentQueue;signal=SemaphoreSlim");
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
        CpuCaptureMillisecondsLastFrame = 0d;
        WorkerBuildMillisecondsLastFrame = 0d;
        BuildsCompletedLastFrame = 0;
        GpuUploadsLastFrame = 0;

        // Consume CPU results before visibility selection and new build requests.
        // GPU uploads remain on the render thread later in this frame.
        CollectCompletedBuilds(field);
        float cameraDistance = Vector3.Distance(camera.Position, camera.Target);

        List<Candidate> candidates = _candidateScratch;
        candidates.Clear();

        List<Candidate> step16Fallbacks = _step16FallbackScratch;
        step16Fallbacks.Clear();

        List<Candidate> step4Fallbacks = _step4FallbackScratch;
        step4Fallbacks.Clear();

        List<Candidate> step1Parents = _step1ParentScratch;
        step1Parents.Clear();

        List<Candidate> prewarmParents = _prewarmParentScratch;
        prewarmParents.Clear();

        _preparedCandidateKeys.Clear();
        _cacheProtectedKeys.Clear();

        int frustumTests = 0;
        CollectLodCandidates(
            field,
            camera,
            viewportWidth,
            viewportHeight,
            maximumViewDistanceMeters,
            candidates,
            step16Fallbacks,
            step4Fallbacks,
            step1Parents,
            prewarmParents,
            ref frustumTests);

        candidates.Sort(static (a, b) =>
            a.DistanceSquared.CompareTo(b.DistanceSquared));
        step16Fallbacks.Sort(static (a, b) =>
            a.DistanceSquared.CompareTo(b.DistanceSquared));
        step4Fallbacks.Sort(static (a, b) =>
            a.DistanceSquared.CompareTo(b.DistanceSquared));
        step1Parents.Sort(static (a, b) =>
            a.DistanceSquared.CompareTo(b.DistanceSquared));
        prewarmParents.Sort(static (a, b) =>
            a.DistanceSquared.CompareTo(b.DistanceSquared));

        CandidateOverflowLastFrame = 0;
        Step1ChunksLastFrame = 0;
        Step4ChunksLastFrame = 0;
        Step16ChunksLastFrame = 0;
        _activeStep = LodFarStep;

        foreach (Candidate candidate in candidates)
        {
            switch (candidate.Key.Step)
            {
                case LodNearStep:
                    Step1ChunksLastFrame++;
                    _activeStep = LodNearStep;
                    break;

                case LodMiddleStep:
                    Step4ChunksLastFrame++;
                    if (_activeStep > LodMiddleStep)
                        _activeStep = LodMiddleStep;
                    break;

                case LodFarStep:
                    Step16ChunksLastFrame++;
                    break;
            }
        }

        _isUsingCoarseLod =
            Step4ChunksLastFrame > 0 ||
            Step16ChunksLastFrame > 0;

        _previousVisible.Clear();
        foreach ((ChunkKey key, ChunkEntry entry) in _visible)
        {
            _previousVisible.Add((key, entry));
            _cacheProtectedKeys.Add(key);
        }

        _visible.Clear();
        _visibleCandidateKeys.Clear();

        int cutSampleY = (int)MathF.Floor(
            cutHeightMeters / SparseVoxelField.VoxelSizeMeters - 0.5f);
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

        // Build parent fallbacks before detailed children. This ensures a coarse
        // cover is already resident while workers prepare the replacement hierarchy.
        PrepareCandidateBuilds(
            field, step16Fallbacks, activeLeaves: false,
            cutEnabled, cutSampleY, cutLayer);
        PrepareCandidateBuilds(
            field, step4Fallbacks, activeLeaves: false,
            cutEnabled, cutSampleY, cutLayer);
        PrepareCandidateBuilds(
            field, candidates, activeLeaves: true,
            cutEnabled, cutSampleY, cutLayer);

        QueuePrewarmBuilds(
            field,
            prewarmParents,
            camera,
            viewportWidth,
            viewportHeight,
            maximumViewDistanceMeters,
            ref frustumTests);

        EvictUnusedChunks(camera.Position);
        UploadReadyMeshes();
        SelectVisibleLodMeshes(
            field,
            candidates,
            step16Fallbacks,
            step4Fallbacks,
            step1Parents,
            camera,
            viewportWidth,
            viewportHeight,
            maximumViewDistanceMeters,
            ref frustumTests);
        FrustumCandidatesLastFrame = frustumTests;
        double remainingUploadBudget = Math.Max(
            0d,
            GpuUploadBudgetMilliseconds - GpuUploadMillisecondsLastFrame);
        if (remainingUploadBudget > 0d)
        {
            GpuUploadMillisecondsLastFrame +=
                _dynamicFragments.UploadPendingMeshes(remainingUploadBudget);
        }
        // Worker threads pull jobs immediately from the signalled queue.
        // The render thread only drains completed CPU results and owns GPU uploads.

        _dynamicFragments.ReleaseBakedBodies(
            bounds => AreCachedMeshesCurrent(bounds, field));

        if ((_frame % 120) == 0)
        {
            string summary =
                $"frame={_frame} lodMinStep={_activeStep} lod1={Step1ChunksLastFrame} lod4={Step4ChunksLastFrame} lod16={Step16ChunksLastFrame} frustumTests={FrustumCandidatesLastFrame} selected={_visibleCandidateKeys.Count} visible={_visible.Count} cache={_chunks.Count} emptyChunks={_totalEmptyChunks} " +
                $"snapshotMs={CpuCaptureMillisecondsLastFrame:0.000} workerMs={WorkerBuildMillisecondsLastFrame:0.000} " +
                $"lastWorkerMs={LastWorkerBuildMilliseconds:0.000} queueMs={LastWorkerQueueWaitMilliseconds:0.000} deliveryMs={LastWorkerResultDeliveryMilliseconds:0.000} " +
                $"gpuMs={GpuUploadMillisecondsLastFrame:0.000} gpuPackets={GpuUploadsLastFrame} prewarmQueued={LodPrewarmBuilds} " +
                $"fallback16={step16Fallbacks.Count} fallback4={step4Fallbacks.Count} " +
                $"workers={RunningMeshJobs} queue={_buildQueue.Count} resultsReady={_completedBuildQueue.Count} gpuReady={_readyUploadQueue.Count} " +
                $"requests={_totalBuildRequests} started={_totalBuildStarted} completed={_totalBuildCompleted} " +
                $"cancelled={_totalBuildCancelled} failed={_totalBuildFailed} stale={_totalStaleResults} " +
                $"repeat={_totalRepeatedBuilds} duplicateReq={_totalDuplicateRequestAttempts} queueFull={_totalQueueSaturated} " +
                $"gpuUploads={_totalGpuUploads} gpuUnloads={_totalGpuUnloads} surfaceCacheHits={_totalIdenticalSurfaceSkips} capCacheHits={_totalIdenticalCutSkips} " +
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

    private void CollectLodCandidates(
        SparseVoxelField field,
        Camera3D camera,
        int viewportWidth,
        int viewportHeight,
        float maximumViewDistanceMeters,
        List<Candidate> candidates,
        List<Candidate> step16Fallbacks,
        List<Candidate> step4Fallbacks,
        List<Candidate> step1Parents,
        List<Candidate> prewarmParents,
        ref int frustumTests)
    {
        const float sphereScale = 0.8660254038f;
        float voxelSize = SparseVoxelField.VoxelSizeMeters;
        Vector3 cameraPosition = camera.Position;

        int farSamples = ChunkCells * LodFarStep;
        int middleSamples = ChunkCells * LodMiddleStep;
        int nearSamples = ChunkCells * LodNearStep;
        float farSizeMeters = farSamples * voxelSize;
        float farSphereRadius = farSizeMeters * sphereScale;

        // Any visible chunk must have its parent sphere intersect this
        // camera-distance envelope. This broad phase avoids scanning distant
        // Step=16 cells elsewhere in the world.
        float scanRadius = MathF.Max(0f, maximumViewDistanceMeters) + farSphereRadius;
        int chunksX = DivideRoundUp(field.Width, farSamples);
        int chunksY = DivideRoundUp(field.Height, farSamples);
        int chunksZ = DivideRoundUp(field.Depth, farSamples);

        int firstChunkX = Math.Clamp(
            (int)MathF.Floor((cameraPosition.X - scanRadius) / farSizeMeters),
            0, chunksX - 1);
        int lastChunkX = Math.Clamp(
            (int)MathF.Floor((cameraPosition.X + scanRadius) / farSizeMeters),
            0, chunksX - 1);
        int firstChunkY = Math.Clamp(
            (int)MathF.Floor((cameraPosition.Y - scanRadius) / farSizeMeters),
            0, chunksY - 1);
        int lastChunkY = Math.Clamp(
            (int)MathF.Floor((cameraPosition.Y + scanRadius) / farSizeMeters),
            0, chunksY - 1);
        int firstChunkZ = Math.Clamp(
            (int)MathF.Floor((cameraPosition.Z - scanRadius) / farSizeMeters),
            0, chunksZ - 1);
        int lastChunkZ = Math.Clamp(
            (int)MathF.Floor((cameraPosition.Z + scanRadius) / farSizeMeters),
            0, chunksZ - 1);

        // Advance directly by the Step=16 parent grid; refine only parents whose
        // center enters the 256 m band. Each child level is aligned to its parent.
        for (int chunkZ = firstChunkZ; chunkZ <= lastChunkZ; chunkZ++)
        for (int chunkY = firstChunkY; chunkY <= lastChunkY; chunkY++)
        for (int chunkX = firstChunkX; chunkX <= lastChunkX; chunkX++)
        {
            int originX = chunkX * farSamples;
            int originY = chunkY * farSamples;
            int originZ = chunkZ * farSamples;
            Vector3 farCenter = new(
                (originX + farSamples * 0.5f) * voxelSize,
                (originY + farSamples * 0.5f) * voxelSize,
                (originZ + farSamples * 0.5f) * voxelSize);

            frustumTests++;
            if (!TerrainFrustum.IsSphereVisible(
                camera,
                farCenter,
                farSizeMeters * sphereScale,
                viewportWidth,
                viewportHeight,
                maximumViewDistanceMeters))
                continue;

            if (BlockCenterDistanceSquared(
                cameraPosition,
                originX,
                originY,
                originZ,
                farSamples) > LodMiddleRadiusSquared)
            {
                AddCandidate(
                    candidates,
                    originX,
                    originY,
                    originZ,
                    LodFarStep,
                    cameraPosition);
                continue;
            }

            AddCandidate(
                step16Fallbacks,
                originX,
                originY,
                originZ,
                LodFarStep,
                cameraPosition);

            int farEndX = Math.Min(originX + farSamples, field.Width);
            int farEndY = Math.Min(originY + farSamples, field.Height);
            int farEndZ = Math.Min(originZ + farSamples, field.Depth);

            // Step=4 grid is nested inside the Step=16 parent, so origins remain
            // aligned and neighboring LOD blocks cover the field exactly once.
            for (int middleZ = originZ; middleZ < farEndZ; middleZ += middleSamples)
            for (int middleY = originY; middleY < farEndY; middleY += middleSamples)
            for (int middleX = originX; middleX < farEndX; middleX += middleSamples)
            {
                float middleSizeMeters = middleSamples * voxelSize;
                Vector3 middleCenter = new(
                    (middleX + middleSamples * 0.5f) * voxelSize,
                    (middleY + middleSamples * 0.5f) * voxelSize,
                    (middleZ + middleSamples * 0.5f) * voxelSize);

                frustumTests++;
                if (!TerrainFrustum.IsSphereVisible(
                    camera,
                    middleCenter,
                    middleSizeMeters * sphereScale,
                    viewportWidth,
                    viewportHeight,
                    maximumViewDistanceMeters))
                    continue;

                AddCandidate(
                    step4Fallbacks,
                    middleX,
                    middleY,
                    middleZ,
                    LodMiddleStep,
                    cameraPosition);

                float middleDistanceSquared = BlockCenterDistanceSquared(
                    cameraPosition,
                    middleX,
                    middleY,
                    middleZ,
                    middleSamples);

                if (middleDistanceSquared > LodNearRadiusSquared)
                {
                    AddCandidate(
                        candidates,
                        middleX,
                        middleY,
                        middleZ,
                        LodMiddleStep,
                        cameraPosition);

                    // Begin warming the Step=1 children as soon as the nearest
                    // face of this Step=4 block enters the 55..64 m shell.
                    // The visible LOD still changes only at the existing
                    // Step=4 parent-center threshold (64 m).
                    float nearestDistanceSquared = BlockNearestDistanceSquared(
                        cameraPosition,
                        middleX,
                        middleY,
                        middleZ,
                        middleSamples,
                        field);

                    if (nearestDistanceSquared >= LodNearPrewarmStartSquared &&
                        nearestDistanceSquared <= LodNearRadiusSquared)
                    {
                        AddCandidate(
                            prewarmParents,
                            middleX,
                            middleY,
                            middleZ,
                            LodMiddleStep,
                            cameraPosition);
                    }

                    continue;
                }

                AddCandidate(
                    step1Parents,
                    middleX,
                    middleY,
                    middleZ,
                    LodMiddleStep,
                    cameraPosition);

                int middleEndX = Math.Min(middleX + middleSamples, field.Width);
                int middleEndY = Math.Min(middleY + middleSamples, field.Height);
                int middleEndZ = Math.Min(middleZ + middleSamples, field.Depth);

                // Only the 64 m inner band is expanded to Step=1.
                for (int nearZ = middleZ; nearZ < middleEndZ; nearZ += nearSamples)
                for (int nearY = middleY; nearY < middleEndY; nearY += nearSamples)
                for (int nearX = middleX; nearX < middleEndX; nearX += nearSamples)
                {
                    float nearSizeMeters = nearSamples * voxelSize;
                    Vector3 nearCenter = new(
                        (nearX + nearSamples * 0.5f) * voxelSize,
                        (nearY + nearSamples * 0.5f) * voxelSize,
                        (nearZ + nearSamples * 0.5f) * voxelSize);

                    frustumTests++;
                    if (!TerrainFrustum.IsSphereVisible(
                        camera,
                        nearCenter,
                        nearSizeMeters * sphereScale,
                        viewportWidth,
                        viewportHeight,
                        maximumViewDistanceMeters))
                        continue;

                    AddCandidate(
                        candidates,
                        nearX,
                        nearY,
                        nearZ,
                        LodNearStep,
                        cameraPosition);
                }
            }
        }
    }

    private void PrepareCandidateBuilds(
        SparseVoxelField field,
        List<Candidate> candidates,
        bool activeLeaves,
        bool cutEnabled,
        int cutSampleY,
        int cutLayer)
    {
        foreach (Candidate candidate in candidates)
        {
            ChunkKey key = candidate.Key;

            if (activeLeaves)
                _visibleCandidateKeys.Add(key);

            _cacheProtectedKeys.Add(key);

            if (!_preparedCandidateKeys.Add(key))
                continue;

            if (!_chunks.TryGetValue(key, out ChunkEntry? entry))
            {
                entry = new ChunkEntry();
                _chunks.Add(key, entry);
            }

            entry.LastAccessFrame = _frame;

            bool surfaceStale = !entry.Built ||
                entry.SurfaceDirty ||
                entry.BaseVersion != field.BaseVersion ||
                entry.PaletteVersion != _paletteVersion;

            int chunkMinY = key.Y * ChunkCells * key.Step;
            int chunkMaxY = chunkMinY + ChunkCells * key.Step;
            bool containsCutPlane =
                cutSampleY >= chunkMinY &&
                cutSampleY < chunkMaxY;

            bool needsCutCap = cutEnabled &&
                _cutLayerStableFrames >= CutCapSettleFrames &&
                containsCutPlane &&
                (entry.CutLayer != cutLayer || entry.CutDirty);

            if ((surfaceStale || needsCutCap) &&
                !HasPendingWork(entry) &&
                _buildQueue.Count < MaximumPendingMeshJobs)
            {
                RequestBuild(
                    field,
                    key,
                    entry,
                    surfaceStale,
                    needsCutCap,
                    cutLayer);
            }

            if (!cutEnabled && entry.CutCap.Length > 0)
                ClearCutCap(key, entry);
        }
    }

    private void SelectVisibleLodMeshes(
        SparseVoxelField field,
        List<Candidate> activeLeaves,
        List<Candidate> step16Fallbacks,
        List<Candidate> step4Fallbacks,
        List<Candidate> step1Parents,
        Camera3D camera,
        int viewportWidth,
        int viewportHeight,
        float maximumViewDistanceMeters,
        ref int frustumTests)
    {
        _step16Coverage.Clear();
        _step4Coverage.Clear();
        _step1ParentKeys.Clear();
        _farFallbackHeldKeys.Clear();

        // Build readiness maps in linear passes over the currently visible
        // candidate sets. A child counts as covered only after GPU upload.
        AccumulateChildCoverage(
            activeLeaves,
            LodNearStep,
            field,
            _step4Coverage);

        AccumulateChildCoverage(
            step4Fallbacks,
            LodMiddleStep,
            field,
            _step16Coverage);

        foreach (Candidate parent in step1Parents)
            _step1ParentKeys.Add(parent.Key);

        foreach (Candidate candidate in activeLeaves)
        {
            if (candidate.Key.Step != LodFarStep)
                continue;

            if (!AddCachedFallback(candidate.Key))
            {
                AddPreviousVisibleDescendants(
                    candidate.Key,
                    camera,
                    viewportWidth,
                    viewportHeight,
                    maximumViewDistanceMeters,
                    ref frustumTests);
            }
        }

        // Keep the previous frame's rendered descendants when the new hierarchy
        // is not yet ready and its coarse fallback is not resident (cold cache).
        foreach (Candidate farParent in step16Fallbacks)
        {
            if (IsCoverageReady(_step16Coverage, farParent.Key))
                continue;

            if (AddCachedFallback(farParent.Key) ||
                AddPreviousVisibleDescendants(
                    farParent.Key,
                    camera,
                    viewportWidth,
                    viewportHeight,
                    maximumViewDistanceMeters,
                    ref frustumTests))
            {
                _farFallbackHeldKeys.Add(farParent.Key);
            }
        }

        foreach (Candidate middle in step4Fallbacks)
        {
            ChunkKey farParent = GetParentKey(middle.Key, LodFarStep);
            if (_farFallbackHeldKeys.Contains(farParent))
                continue;

            bool wantsNear = _step1ParentKeys.Contains(middle.Key);
            bool nearChildrenReady =
                wantsNear &&
                IsCoverageReady(_step4Coverage, middle.Key);

            if (nearChildrenReady)
                continue;

            if (AddCachedFallback(middle.Key) ||
                AddPreviousVisibleDescendants(
                    middle.Key,
                    camera,
                    viewportWidth,
                    viewportHeight,
                    maximumViewDistanceMeters,
                    ref frustumTests))
            {
                continue;
            }

            // Last-resort streaming fallback: show already resident Step1
            // children while missing siblings are still being built.
            AddAvailableChildren(middle.Key, activeLeaves);
        }

        foreach (Candidate near in activeLeaves)
        {
            if (near.Key.Step != LodNearStep)
                continue;

            ChunkKey middleParent = GetParentKey(near.Key, LodMiddleStep);
            ChunkKey farParent = GetParentKey(middleParent, LodFarStep);

            if (_farFallbackHeldKeys.Contains(farParent))
                continue;

            if (!_step1ParentKeys.Contains(middleParent) ||
                !IsCoverageReady(_step4Coverage, middleParent))
                continue;

            AddCachedFallback(near.Key);
        }
    }

    private void AccumulateChildCoverage(
        List<Candidate> children,
        int childStep,
        SparseVoxelField field,
        Dictionary<ChunkKey, ChildGroupState> coverage)
    {
        foreach (Candidate child in children)
        {
            if (child.Key.Step != childStep)
                continue;

            ChunkKey parent = GetParentKey(child.Key, childStep * 4);
            bool ready =
                _chunks.TryGetValue(child.Key, out ChunkEntry? entry) &&
                IsCurrentMeshReady(entry, field);

            if (coverage.TryGetValue(parent, out ChildGroupState state))
            {
                state.Count++;
                state.Ready &= ready;
            }
            else
            {
                state.Count = 1;
                state.Ready = ready;
            }

            coverage[parent] = state;
        }
    }

    private static bool IsChildOf(ChunkKey child, ChunkKey parent)
    {
        if (child.Step >= parent.Step || parent.Step % child.Step != 0)
            return false;

        int ratio = parent.Step / child.Step;
        return child.X / ratio == parent.X &&
            child.Y / ratio == parent.Y &&
            child.Z / ratio == parent.Z;
    }

    private static ChunkKey GetParentKey(ChunkKey child, int parentStep)
    {
        int ratio = parentStep / child.Step;
        return new ChunkKey(
            parentStep,
            child.X / ratio,
            child.Y / ratio,
            child.Z / ratio);
    }

    private static bool IsCoverageReady(
        Dictionary<ChunkKey, ChildGroupState> coverage,
        ChunkKey parent)
    {
        return coverage.TryGetValue(parent, out ChildGroupState state) &&
            state.Count > 0 &&
            state.Ready;
    }

    private void AddCachedFallback(ChunkKey key)
    {
        if (_chunks.TryGetValue(key, out ChunkEntry? entry) &&
            IsRenderable(entry))
        {
            AddVisible(key, entry);
        }
    }

    private bool AddPreviousVisibleDescendants(
        ChunkKey parent,
        Camera3D camera,
        int viewportWidth,
        int viewportHeight,
        float maximumViewDistanceMeters,
        ref int frustumTests)
    {
        bool added = false;
        const float sphereScale = 0.8660254038f;
        float voxelSize = SparseVoxelField.VoxelSizeMeters;

        foreach ((ChunkKey key, ChunkEntry previousEntry) in _previousVisible)
        {
            if (!IsChildOf(key, parent) ||
                !_chunks.TryGetValue(key, out ChunkEntry? currentEntry) ||
                !ReferenceEquals(currentEntry, previousEntry) ||
                !IsRenderable(currentEntry))
                continue;

            int span = ChunkCells * key.Step;
            Vector3 center = new(
                (key.X * span + span * 0.5f) * voxelSize,
                (key.Y * span + span * 0.5f) * voxelSize,
                (key.Z * span + span * 0.5f) * voxelSize);

            frustumTests++;
            if (!TerrainFrustum.IsSphereVisible(
                camera,
                center,
                span * voxelSize * sphereScale,
                viewportWidth,
                viewportHeight,
                maximumViewDistanceMeters))
                continue;

            AddVisible(key, currentEntry);
            added = true;
        }

        return added;
    }

    private void AddAvailableChildren(
        ChunkKey parent,
        List<Candidate> children)
    {
        foreach (Candidate child in children)
        {
            if (child.Key.Step >= parent.Step ||
                !IsChildOf(child.Key, parent))
                continue;

            if (_chunks.TryGetValue(child.Key, out ChunkEntry? entry) &&
                IsRenderable(entry))
            {
                AddVisible(child.Key, entry);
            }
        }
    }

    private bool AddCachedFallback(ChunkKey key)
    {
        if (!_chunks.TryGetValue(key, out ChunkEntry? entry) ||
            !IsRenderable(entry))
            return false;

        AddVisible(key, entry);
        return true;
    }

    private static bool IsRenderable(ChunkEntry entry) =>
        entry.Built && entry.Surface.Length > 0;

    private void QueuePrewarmBuilds(
        SparseVoxelField field,
        List<Candidate> prewarmParents,
        Camera3D camera,
        int viewportWidth,
        int viewportHeight,
        float maximumViewDistanceMeters,
        ref int frustumTests)
    {
        if (prewarmParents.Count == 0)
            return;

        const float sphereScale = 0.8660254038f;
        const int nearSamples = ChunkCells * LodNearStep;
        float nearSizeMeters = nearSamples * SparseVoxelField.VoxelSizeMeters;
        float nearRadius = nearSizeMeters * sphereScale;

        List<Candidate> children = _prewarmChunkScratch;

        foreach (Candidate parent in prewarmParents)
        {
            children.Clear();

            int parentOriginX = parent.Key.X * ChunkCells * LodMiddleStep;
            int parentOriginY = parent.Key.Y * ChunkCells * LodMiddleStep;
            int parentOriginZ = parent.Key.Z * ChunkCells * LodMiddleStep;
            int parentEndX = Math.Min(
                parentOriginX + ChunkCells * LodMiddleStep, field.Width);
            int parentEndY = Math.Min(
                parentOriginY + ChunkCells * LodMiddleStep, field.Height);
            int parentEndZ = Math.Min(
                parentOriginZ + ChunkCells * LodMiddleStep, field.Depth);

            // A Step=4 parent contains a 4x4x4 Step=1 child group.
            for (int z = parentOriginZ; z < parentEndZ; z += nearSamples)
            for (int y = parentOriginY; y < parentEndY; y += nearSamples)
            for (int x = parentOriginX; x < parentEndX; x += nearSamples)
            {
                Vector3 center = new(
                    (x + nearSamples * 0.5f) * SparseVoxelField.VoxelSizeMeters,
                    (y + nearSamples * 0.5f) * SparseVoxelField.VoxelSizeMeters,
                    (z + nearSamples * 0.5f) * SparseVoxelField.VoxelSizeMeters);

                frustumTests++;
                if (!TerrainFrustum.IsSphereVisible(
                    camera,
                    center,
                    nearRadius,
                    viewportWidth,
                    viewportHeight,
                    maximumViewDistanceMeters))
                    continue;

                AddCandidate(children, x, y, z, LodNearStep, camera.Position);
            }

            // The closest child chunks are queued first; worker queue capacity
            // is the only admission limit.
            children.Sort(static (a, b) =>
                a.DistanceSquared.CompareTo(b.DistanceSquared));

            foreach (Candidate candidate in children)
            {
                if (_buildQueue.Count >= MaximumPendingMeshJobs)
                    return;

                ChunkKey key = candidate.Key;
                if (_visibleCandidateKeys.Contains(key))
                    continue;

                if (!_chunks.TryGetValue(key, out ChunkEntry? entry))
                {
                    entry = new ChunkEntry();
                    _chunks.Add(key, entry);
                }

                entry.LastAccessFrame = _frame;
                _cacheProtectedKeys.Add(key);

                bool surfaceStale = !entry.Built ||
                    entry.SurfaceDirty ||
                    entry.BaseVersion != field.BaseVersion ||
                    entry.PaletteVersion != _paletteVersion;

                if (!surfaceStale ||
                    HasPendingWork(entry) ||
                    entry.FailedRevision == entry.RequestRevision)
                    continue;

                RequestBuild(
                    field,
                    key,
                    entry,
                    buildSurface: true,
                    buildCutCap: false,
                    cutLayer: -1);

                if (entry.RunningBuild is BuildRequest request &&
                    request.Generation == entry.BuildGeneration)
                {
                    LodPrewarmBuilds++;
                }
            }
        }
    }

    private static float BlockNearestDistanceSquared(
        Vector3 point,
        int originX,
        int originY,
        int originZ,
        int sizeSamples,
        SparseVoxelField field)
    {
        float voxelSize = SparseVoxelField.VoxelSizeMeters;

        float minX = originX * voxelSize;
        float minY = originY * voxelSize;
        float minZ = originZ * voxelSize;
        float maxX = Math.Min(originX + sizeSamples, field.Width) * voxelSize;
        float maxY = Math.Min(originY + sizeSamples, field.Height) * voxelSize;
        float maxZ = Math.Min(originZ + sizeSamples, field.Depth) * voxelSize;

        float dx = point.X < minX ? minX - point.X :
            point.X > maxX ? point.X - maxX : 0f;
        float dy = point.Y < minY ? minY - point.Y :
            point.Y > maxY ? point.Y - maxY : 0f;
        float dz = point.Z < minZ ? minZ - point.Z :
            point.Z > maxZ ? point.Z - maxZ : 0f;

        return dx * dx + dy * dy + dz * dz;
    }

    private static void AddCandidate(
        List<Candidate> candidates,
        int originX,
        int originY,
        int originZ,
        int step,
        Vector3 cameraPosition)
    {
        float sizeMeters =
            ChunkCells * step * SparseVoxelField.VoxelSizeMeters;
        Vector3 center = new(
            (originX + ChunkCells * step * 0.5f) * SparseVoxelField.VoxelSizeMeters,
            (originY + ChunkCells * step * 0.5f) * SparseVoxelField.VoxelSizeMeters,
            (originZ + ChunkCells * step * 0.5f) * SparseVoxelField.VoxelSizeMeters);

        int chunkSize = ChunkCells * step;
        ChunkKey key = new(
            step,
            originX / chunkSize,
            originY / chunkSize,
            originZ / chunkSize);

        candidates.Add(new Candidate(
            key,
            center,
            sizeMeters * 0.8660254038f,
            Vector3.DistanceSquared(center, cameraPosition)));
    }

    private static float BlockCenterDistanceSquared(
        Vector3 point,
        int originX,
        int originY,
        int originZ,
        int sizeSamples)
    {
        float centerX =
            (originX + sizeSamples * 0.5f) * SparseVoxelField.VoxelSizeMeters;
        float centerY =
            (originY + sizeSamples * 0.5f) * SparseVoxelField.VoxelSizeMeters;
        float centerZ =
            (originZ + sizeSamples * 0.5f) * SparseVoxelField.VoxelSizeMeters;

        float dx = point.X - centerX;
        float dy = point.Y - centerY;
        float dz = point.Z - centerZ;
        return dx * dx + dy * dy + dz * dz;
    }

    private void RemoveCachedChunk(ChunkKey key, string reason)
    {
        if (!_chunks.Remove(key, out ChunkEntry? entry))
            return;

        _gpuMeshHashCache.Remove(key);

        int residentMeshes = entry.Surface.Length + entry.CutCap.Length;
        int partialMeshes = entry.UploadingSurface.Count + entry.UploadingCutCap.Count;

        // Cancels work, returns any staged CPU arrays and unloads partial uploads.
        // Resident meshes are unloaded by Dispose immediately below.
        InvalidatePendingBuild(entry);

        _totalGpuUnloads += residentMeshes;
        _logger.Write(
            "MESH_GPU_EVICT",
            $"reason={reason} step={key.Step} chunk=({key.X},{key.Y},{key.Z}) meshes={residentMeshes + partialMeshes}");

        entry.Dispose();
    }

    private void RequestBuild(
        SparseVoxelField field,
        ChunkKey key,
        ChunkEntry entry,
        bool buildSurface,
        bool buildCutCap,
        int cutLayer)
    {
        if (_disposed || entry.FailedRevision == entry.RequestRevision)
            return;

        if (entry.RunningBuild != null ||
            entry.BuildQueued ||
            entry.ReadyBuild.HasValue)
        {
            _totalDuplicateRequestAttempts++;
            return;
        }

        if (_buildQueue.Count >= MaximumPendingMeshJobs)
        {
            _totalQueueSaturated++;
            return;
        }

        Stopwatch snapshotTimer = Stopwatch.StartNew();
        IVoxelFieldSource source = field.CreateChunkSnapshot(
            key.X, key.Y, key.Z, key.Step);
        snapshotTimer.Stop();
        CpuCaptureMillisecondsLastFrame += snapshotTimer.Elapsed.TotalMilliseconds;

        BuildRequest request = new()
        {
            Key = key,
            Entry = entry,
            Source = source,
            RequestRevision = entry.RequestRevision,
            Generation = ++entry.BuildGeneration,
            PaletteVersion = _paletteVersion,
            BaseVersion = field.BaseVersion,
            BuildSurface = buildSurface,
            BuildCutCap = buildCutCap,
            CutLayer = buildCutCap ? cutLayer : int.MinValue,
            Palette = _palette,
            EnqueuedTimestamp = Stopwatch.GetTimestamp()
        };

        entry.BuildQueued = true;
        entry.RunningBuild = request;
        _buildQueue.Enqueue(request);
        _totalBuildRequests++;
        _buildSignal.Release();
    }

    private void ProcessBuildQueue()
    {
        CancellationToken shutdownToken = _workerShutdown.Token;

        // One semaphore permit corresponds to one queued BuildRequest.
        // Wait blocks this dedicated background thread; it never blocks the frame thread.
        while (true)
        {
            try
            {
                _buildSignal.Wait(shutdownToken);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                return;
            }

            if (shutdownToken.IsCancellationRequested)
                return;

            // Never split Count/TryDequeue: only TryDequeue is safe under contention.
            if (!_buildQueue.TryDequeue(out BuildRequest? request))
                continue;

            Interlocked.Exchange(ref request.Started, 1);
            Interlocked.Increment(ref _activeMeshWorkers);
            Interlocked.Increment(ref _totalBuildStarted);
            long workerStarted = Stopwatch.GetTimestamp();
            VoxelMeshData[] surface = Array.Empty<VoxelMeshData>();
            VoxelMeshData[] cutCap = Array.Empty<VoxelMeshData>();
            bool completionQueued = false;

            try
            {
                CancellationToken token = request.Cancellation.Token;
                token.ThrowIfCancellationRequested();

                Stopwatch workerTimer = Stopwatch.StartNew();
                VoxelChunkBuildInput input = new(
                    request.Source,
                    request.Key.X,
                    request.Key.Y,
                    request.Key.Z,
                    request.Key.Step);

                while (!input.DensityCaptureComplete)
                {
                    token.ThrowIfCancellationRequested();
                    input.CaptureNext(request.Source, 512, captureMaterials: false);
                }

                bool hasSurface = request.BuildSurface && input.HasPotentialSurface;
                if (hasSurface || request.BuildCutCap)
                {
                    while (!input.IsComplete)
                    {
                        token.ThrowIfCancellationRequested();
                        input.CaptureNext(request.Source, 512, captureMaterials: true);
                    }
                }

                surface = hasSurface
                    ? VoxelChunkMesher.BuildSurfaceData(input, request.Palette, token)
                    : Array.Empty<VoxelMeshData>();
                cutCap = request.BuildCutCap
                    ? VoxelChunkMesher.BuildCutCapData(
                        input,
                        request.CutLayer * SparseVoxelField.VoxelSizeMeters,
                        request.Palette,
                        token)
                    : Array.Empty<VoxelMeshData>();

                int vertices = surface.Sum(static mesh => mesh.VertexCount) +
                    cutCap.Sum(static mesh => mesh.VertexCount);
                int triangles = surface.Sum(static mesh => mesh.TriangleCount) +
                    cutCap.Sum(static mesh => mesh.TriangleCount);
                bool hasSurfaceMesh = surface.Length > 0;
                ulong surfaceHash = hasSurfaceMesh ? ComputeMeshHash(surface) : 0UL;
                ulong cutCapHash = request.BuildCutCap ? ComputeMeshHash(cutCap) : 0UL;
                workerTimer.Stop();

                VoxelMeshBuildResult result = new(
                    surface,
                    cutCap,
                    hasSurfaceMesh,
                    request.BuildCutCap,
                    request.BuildSurface && !hasSurfaceMesh,
                    request.CutLayer,
                    workerTimer.Elapsed.TotalMilliseconds,
                    vertices,
                    triangles,
                    surfaceHash,
                    cutCapHash);

                _completedBuildQueue.Enqueue(new BuildCompletion(
                    request,
                    result,
                    null,
                    false,
                    workerStarted,
                    Stopwatch.GetTimestamp()));
                completionQueued = true;
                surface = Array.Empty<VoxelMeshData>();
                cutCap = Array.Empty<VoxelMeshData>();
            }
            catch (OperationCanceledException)
            {
                ReturnMeshData(surface);
                ReturnMeshData(cutCap);

                _completedBuildQueue.Enqueue(new BuildCompletion(
                    request,
                    null,
                    null,
                    true,
                    workerStarted,
                    Stopwatch.GetTimestamp()));
            }
            catch (Exception exception)
            {
                ReturnMeshData(surface);
                ReturnMeshData(cutCap);

                _completedBuildQueue.Enqueue(new BuildCompletion(
                    request,
                    null,
                    exception,
                    false,
                    workerStarted,
                    Stopwatch.GetTimestamp()));
            }
            finally
            {
                if (!completionQueued)
                {
                    ReturnMeshData(surface);
                    ReturnMeshData(cutCap);
                }

                Interlocked.Decrement(ref _activeMeshWorkers);
            }
        }
    }

    private void CollectCompletedBuilds(SparseVoxelField field)
    {
        // Drain only the completions that were present at the start of this pass.
        // New worker publications are handled next frame; no head-of-line Peek/Break.
        int completionsToProcess = _completedBuildQueue.Count;

        for (int completionIndex = 0;
             completionIndex < completionsToProcess;
             completionIndex++)
        {
            if (!_completedBuildQueue.TryDequeue(out BuildCompletion completion))
                break;

            BuildRequest request = completion.Request;
            ChunkEntry entry = request.Entry;
            request.Cancellation.Dispose();

            bool wasCurrentRequest = ReferenceEquals(entry.RunningBuild, request);
            if (wasCurrentRequest)
            {
                entry.RunningBuild = null;
                entry.BuildQueued = false;
            }

            if (completion.Cancelled)
            {
                _totalBuildCancelled++;
                _logger.Write(
                    "MESH_CANCELLED",
                    $"step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) rev={request.RequestRevision} generation={request.Generation}");
                continue;
            }

            bool isCurrentRequest = wasCurrentRequest &&
                _chunks.TryGetValue(request.Key, out ChunkEntry? current) &&
                ReferenceEquals(current, entry) &&
                request.Generation == entry.BuildGeneration &&
                request.RequestRevision == entry.RequestRevision &&
                request.PaletteVersion == _paletteVersion &&
                request.BaseVersion == field.BaseVersion;

            if (!isCurrentRequest)
            {
                if (completion.Result is VoxelMeshBuildResult staleBuild)
                    ReturnBuildCpuData(staleBuild);

                _totalStaleResults++;
                _logger.Write(
                    "MESH_STALE",
                    $"step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) requestRev={request.RequestRevision}/{entry.RequestRevision} generation={request.Generation}/{entry.BuildGeneration} base={request.BaseVersion}/{field.BaseVersion}");
                continue;
            }

            if (completion.Error != null)
            {
                _totalBuildFailed++;
                entry.FailedRevision = request.RequestRevision;

                _logger.Write(
                    "MESH_FAILED",
                    $"step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) generation={request.Generation} error=\"{completion.Error.Message}\"");
                continue;
            }

            if (!completion.Result.HasValue)
            {
                _totalBuildFailed++;
                entry.FailedRevision = request.RequestRevision;
                _logger.Write(
                    "MESH_FAILED",
                    $"step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) error=\"missing worker result\"");
                continue;
            }

            VoxelMeshBuildResult build = completion.Result.Value;

            // The invariant is one current staged result per chunk. A duplicate
            // completion must never block the global completion queue.
            if (entry.ReadyBuild.HasValue)
            {
                ReturnBuildCpuData(build);
                _totalRepeatedBuilds++;
                _logger.Write(
                    "MESH_DUPLICATE_COMPLETION",
                    $"step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) rev={request.RequestRevision}");
                continue;
            }

            BuildIdentity identity = new(
                request.Key,
                request.RequestRevision,
                request.BaseVersion,
                request.PaletteVersion,
                request.BuildSurface,
                request.BuildCutCap,
                request.CutLayer);

            int sameRevisionBuilds = _completedBuildCounts.TryGetValue(
                identity,
                out int previousBuilds)
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
                Stopwatch.GetElapsedTime(
                    request.EnqueuedTimestamp,
                    completion.WorkerStartedTimestamp).TotalMilliseconds);

            LastWorkerResultDeliveryMilliseconds = Math.Max(
                0d,
                Stopwatch.GetElapsedTime(
                    completion.WorkerCompletedTimestamp,
                    Stopwatch.GetTimestamp()).TotalMilliseconds);

            LastBuildStep = request.Key.Step;
            LastBuildVertices = build.VertexCount;
            LastBuildTriangles = build.TriangleCount;

            bool uploadSurface = build.HasSurface;
            bool uploadCutCap = build.HasCutCap;

            bool hasGpuHashes = _gpuMeshHashCache.TryGetValue(
                request.Key,
                out ChunkGpuHashes gpuHashes);

            // An empty surface is already a complete result: no GPU slot needed.
            if (build.SurfaceWasEmpty)
            {
                if (entry.Surface.Length > 0)
                {
                    _logger.Write(
                        "MESH_GPU_EVICT",
                        $"reason=surface-became-empty step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) meshes={entry.Surface.Length}");

                    foreach (Mesh mesh in entry.Surface)
                    {
                        UnloadMesh(mesh);
                        _totalGpuUnloads++;
                    }
                }

                entry.Surface = Array.Empty<Mesh>();
                SetSurfaceGpuHash(request.Key, 0UL);
                entry.BaseVersion = request.BaseVersion;
                entry.PaletteVersion = request.PaletteVersion;
                entry.SurfaceDirty = false;
                entry.Built = true;
                uploadSurface = false;

                _totalEmptyChunks++;
                RebuiltChunks++;
            }
            else if (uploadSurface &&
                entry.Built &&
                hasGpuHashes &&
                gpuHashes.HasSurface &&
                gpuHashes.SurfaceHash == build.SurfaceHash)
            {
                // Identical geometry stays resident in VRAM; only the temporary
                // CPU buffers from this build are returned.
                ReturnMeshData(build.Surface);
                build = build with { Surface = Array.Empty<VoxelMeshData>() };

                entry.BaseVersion = request.BaseVersion;
                entry.PaletteVersion = request.PaletteVersion;
                entry.SurfaceDirty = false;
                uploadSurface = false;
                _totalIdenticalSurfaceSkips++;

                _logger.Write(
                    "MESH_GPU_CACHE_HIT",
                    $"kind=surface step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) hash={build.SurfaceHash:X16}");
            }

            if (uploadCutCap &&
                hasGpuHashes &&
                gpuHashes.HasCutCap &&
                gpuHashes.CutLayer == build.CutLayer &&
                gpuHashes.CutCapHash == build.CutCapHash)
            {
                ReturnMeshData(build.CutCap);
                build = build with { CutCap = Array.Empty<VoxelMeshData>() };

                entry.CutDirty = false;
                uploadCutCap = false;
                _totalIdenticalCutSkips++;

                _logger.Write(
                    "MESH_GPU_CACHE_HIT",
                    $"kind=cut-cap step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) layer={build.CutLayer} hash={build.CutCapHash:X16}");
            }

            _logger.Write(
                "MESH_BUILT",
                $"step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) rev={request.RequestRevision} base={request.BaseVersion} workerMs={build.WorkerMilliseconds:0.000} queueMs={LastWorkerQueueWaitMilliseconds:0.000} deliveryMs={LastWorkerResultDeliveryMilliseconds:0.000} vertices={build.VertexCount} triangles={build.TriangleCount} uploadSurface={uploadSurface} uploadCap={uploadCutCap}");

            // All remaining CPU buffers are either returned on cache hit or
            // kept only for the products that actually need a GPU upload.
            if (!uploadSurface && !uploadCutCap)
            {
                ReturnBuildCpuData(build);
                continue;
            }

            entry.ReadyBuild = build with
            {
                Surface = uploadSurface ? build.Surface : Array.Empty<VoxelMeshData>(),
                CutCap = uploadCutCap ? build.CutCap : Array.Empty<VoxelMeshData>(),
                HasSurface = uploadSurface,
                HasCutCap = uploadCutCap
            };

            entry.ReadyRequest = request;
            entry.SurfaceUploadIndex = 0;
            entry.CutUploadIndex = 0;
            entry.UploadingSurface.Clear();
            entry.UploadingCutCap.Clear();

            EnqueueReadyUpload(request.Key, entry);
        }
    }

    private static ulong ComputeMeshHash(VoxelMeshData[] meshes)
    {
        const ulong offset = 14695981039346656037UL;
        ulong hash = offset;

        AddHashUInt(ref hash, (uint)meshes.Length);

        foreach (VoxelMeshData mesh in meshes)
        {
            AddHashUInt(ref hash, (uint)mesh.VertexCount);
            AddHashUInt(ref hash, (uint)mesh.IndexCount);

            for (int i = 0; i < mesh.VertexCount; i++)
            {
                Vector3 vertex = mesh.Vertices[i];
                AddHashUInt(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(vertex.X)));
                AddHashUInt(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(vertex.Y)));
                AddHashUInt(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(vertex.Z)));
            }

            for (int i = 0; i < mesh.VertexCount; i++)
            {
                Vector3 normal = mesh.Normals[i];
                AddHashUInt(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(normal.X)));
                AddHashUInt(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(normal.Y)));
                AddHashUInt(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(normal.Z)));
            }

            for (int i = 0; i < mesh.VertexCount * 4; i++)
                AddHashByte(ref hash, mesh.Colors[i]);

            for (int i = 0; i < mesh.IndexCount; i++)
                AddHashUInt(ref hash, mesh.Indices[i]);
        }

        return hash;
    }

    private static void AddHashUInt(ref ulong hash, uint value)
    {
        AddHashByte(ref hash, (byte)value);
        AddHashByte(ref hash, (byte)(value >> 8));
        AddHashByte(ref hash, (byte)(value >> 16));
        AddHashByte(ref hash, (byte)(value >> 24));
    }

    private static void AddHashByte(ref ulong hash, byte value)
    {
        unchecked
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
    }

    private void SetSurfaceGpuHash(ChunkKey key, ulong hash)
    {
        _gpuMeshHashCache.TryGetValue(key, out ChunkGpuHashes current);
        _gpuMeshHashCache[key] = current with
        {
            HasSurface = true,
            SurfaceHash = hash
        };
    }

    private void SetCutCapGpuHash(ChunkKey key, ulong hash, int cutLayer)
    {
        _gpuMeshHashCache.TryGetValue(key, out ChunkGpuHashes current);
        _gpuMeshHashCache[key] = current with
        {
            HasCutCap = true,
            CutCapHash = hash,
            CutLayer = cutLayer
        };
    }

    private static void ReturnMeshData(VoxelMeshData[] meshes)
    {
        foreach (VoxelMeshData mesh in meshes)
            mesh.ReturnCpuArrays();
    }

    private static void ReturnBuildCpuData(VoxelMeshBuildResult build)
    {
        ReturnMeshData(build.Surface);
        ReturnMeshData(build.CutCap);
    }

    private void EnqueueReadyUpload(ChunkKey key, ChunkEntry entry)
    {
        if (entry.UploadQueued)
            return;

        entry.UploadQueued = true;
        long generation = ++entry.UploadQueueGeneration;
        _readyUploadQueue.Enqueue(new ReadyUpload(key, entry, generation));
    }

    private void UploadReadyMeshes()
    {
        Stopwatch timer = Stopwatch.StartNew();
        GpuUploadMillisecondsLastFrame = 0d;

        // Process at most the queue length present at frame start. Each key gets
        // one turn; unfinished chunks are appended to the tail, never pinned at head.
        int readyAtFrameStart = _readyUploadQueue.Count;
        int visited = 0;

        while (visited < readyAtFrameStart)
        {
            if (GpuUploadsLastFrame >= MaximumGpuMeshUploadsPerFrame ||
                timer.Elapsed.TotalMilliseconds >= GpuUploadBudgetMilliseconds)
                break;

            if (!_readyUploadQueue.TryDequeue(out ReadyUpload queued))
                break;

            visited++;
            ChunkKey key = queued.Key;

            if (!_chunks.TryGetValue(key, out ChunkEntry? currentEntry) ||
                !ReferenceEquals(currentEntry, queued.Entry) ||
                queued.Generation != queued.Entry.UploadQueueGeneration)
                continue;

            ChunkEntry entry = queued.Entry;
            entry.UploadQueued = false;

            if (!entry.ReadyBuild.HasValue)
                continue;

            VoxelMeshBuildResult result = entry.ReadyBuild.Value;
            BuildRequest? request = entry.ReadyRequest;

            if (request == null ||
                request.Generation != entry.BuildGeneration ||
                request.RequestRevision != entry.RequestRevision ||
                request.PaletteVersion != _paletteVersion)
            {
                InvalidatePendingBuild(entry);
                continue;
            }

            // One real mesh upload per turn. This guarantees a very large chunk
            // cannot consume every frame's budget while later chunks wait forever.
            if (result.HasSurface &&
                entry.SurfaceUploadIndex < result.Surface.Length)
            {
                int meshIndex = entry.SurfaceUploadIndex;
                VoxelMeshData source = result.Surface[meshIndex];
                Mesh mesh;

                try
                {
                    mesh = VoxelChunkMesher.UploadOne(source);
                }
                catch (Exception exception)
                {
                    _totalBuildFailed++;
                    entry.FailedRevision = request.RequestRevision;
                    _logger.Write(
                        "MESH_GPU_UPLOAD_FAILED",
                        $"kind=surface step={key.Step} chunk=({key.X},{key.Y},{key.Z}) error=\"{exception.Message}\"");
                    InvalidatePendingBuild(entry);
                    continue;
                }

                source.ReturnCpuArrays();
                entry.SurfaceUploadIndex = meshIndex + 1;
                entry.UploadingSurface.Add(mesh);

                GpuUploadsLastFrame++;
                _totalGpuUploads++;

                _logger.Write(
                    "MESH_GPU_UPLOAD",
                    $"kind=surface step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) vertices={source.VertexCount}");
            }
            else if (result.HasCutCap &&
                entry.CutUploadIndex < result.CutCap.Length)
            {
                int meshIndex = entry.CutUploadIndex;
                VoxelMeshData source = result.CutCap[meshIndex];
                Mesh mesh;

                try
                {
                    mesh = VoxelChunkMesher.UploadOne(source);
                }
                catch (Exception exception)
                {
                    _totalBuildFailed++;
                    entry.FailedRevision = request.RequestRevision;
                    _logger.Write(
                        "MESH_GPU_UPLOAD_FAILED",
                        $"kind=cut-cap step={key.Step} chunk=({key.X},{key.Y},{key.Z}) error=\"{exception.Message}\"");
                    InvalidatePendingBuild(entry);
                    continue;
                }

                source.ReturnCpuArrays();
                entry.CutUploadIndex = meshIndex + 1;
                entry.UploadingCutCap.Add(mesh);

                GpuUploadsLastFrame++;
                _totalGpuUploads++;

                _logger.Write(
                    "MESH_GPU_UPLOAD",
                    $"kind=cut-cap step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) layer={result.CutLayer} vertices={source.VertexCount}");
            }

            bool surfaceComplete =
                !result.HasSurface ||
                entry.SurfaceUploadIndex >= result.Surface.Length;

            bool cutCapComplete =
                !result.HasCutCap ||
                entry.CutUploadIndex >= result.CutCap.Length;

            if (!surfaceComplete || !cutCapComplete)
            {
                // Round-robin tail insertion: all other ready chunks get a turn
                // before this chunk can receive another upload.
                EnqueueReadyUpload(key, entry);
                continue;
            }

            // Empty products and cache-only products can reach finalization
            // without spending a GPU upload slot.
            if (result.HasSurface)
            {
                Mesh[] previousSurface = entry.Surface;
                entry.Surface = entry.UploadingSurface.ToArray();
                entry.UploadingSurface.Clear();

                foreach (Mesh oldMesh in previousSurface)
                {
                    UnloadMesh(oldMesh);
                    _totalGpuUnloads++;
                }

                SetSurfaceGpuHash(key, result.SurfaceHash);
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

                foreach (Mesh oldMesh in previousCut)
                {
                    UnloadMesh(oldMesh);
                    _totalGpuUnloads++;
                }

                SetCutCapGpuHash(key, result.CutCapHash, result.CutLayer);
                entry.CutLayer = result.CutLayer;
                entry.CutDirty = false;
                RebuiltCutCaps++;
            }

            entry.ReadyBuild = null;
            entry.ReadyRequest = null;
            entry.SurfaceUploadIndex = 0;
            entry.CutUploadIndex = 0;

            // A completed upload turn never gets re-enqueued.
        }

        GpuUploadMillisecondsLastFrame = timer.Elapsed.TotalMilliseconds;
    }

    private void InvalidatePendingBuild(ChunkEntry entry)
    {
        BuildRequest? obsolete = entry.RunningBuild;
        if (obsolete != null)
        {
            obsolete.Cancellation.Cancel();
            entry.RunningBuild = null;
        }
        entry.BuildQueued = false;
        entry.BuildGeneration++;
        entry.UploadQueued = false;
        entry.UploadQueueGeneration++;
        if (entry.ReadyBuild is VoxelMeshBuildResult readyBuild)
            ReturnBuildCpuData(readyBuild);
        entry.ReadyBuild = null;
        entry.ReadyRequest = null;
        entry.SurfaceUploadIndex = 0;
        entry.CutUploadIndex = 0;

        foreach (Mesh mesh in entry.UploadingSurface)
        {
            UnloadMesh(mesh);
            _totalGpuUnloads++;
        }
        foreach (Mesh mesh in entry.UploadingCutCap)
        {
            UnloadMesh(mesh);
            _totalGpuUnloads++;
        }
        entry.UploadingSurface.Clear();
        entry.UploadingCutCap.Clear();
    }

    private bool IsCurrentMeshReady(ChunkEntry entry, SparseVoxelField field) =>
        entry.Built &&
        !entry.SurfaceDirty &&
        entry.BaseVersion == field.BaseVersion &&
        entry.PaletteVersion == _paletteVersion;

    private void AddVisible(ChunkKey key, ChunkEntry entry)
    {
        if (entry.Built)
        {
            entry.LastSeenFrame = _frame;
            entry.LastAccessFrame = _frame;
            _visible.Add((key, entry));
        }
    }

    private bool AreCachedMeshesCurrent(
        SparseVoxelField.VoxelBounds bounds,
        SparseVoxelField field)
    {
        foreach (ChunkKey key in _visibleCandidateKeys)
        {
            int span = ChunkCells * key.Step;
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

    private void ClearCutCap(ChunkKey key, ChunkEntry entry)
    {
        foreach (Mesh mesh in entry.CutCap)
        {
            UnloadMesh(mesh);
            _totalGpuUnloads++;
        }
        entry.CutCap = Array.Empty<Mesh>();
        _gpuMeshHashCache.TryGetValue(key, out ChunkGpuHashes current);
        _gpuMeshHashCache[key] = current with
        {
            HasCutCap = false,
            CutCapHash = 0UL,
            CutLayer = int.MinValue
        };
        entry.CutLayer = int.MinValue;
        entry.CutDirty = true;
    }

    private static bool HasPendingWork(ChunkEntry entry) =>
        entry.RunningBuild != null ||
        entry.BuildQueued ||
        entry.ReadyBuild.HasValue ||
        entry.UploadingSurface.Count > 0 ||
        entry.UploadingCutCap.Count > 0;

    private void EvictUnusedChunks(Vector3 cameraPosition)
    {
        if (_chunks.Count <= MaximumCachedChunks)
            return;

        List<CacheEvictionCandidate> eviction = _cacheEvictionScratch;
        eviction.Clear();

        // Keep active LOD candidates. Pending builds and prewarmed entries are
        // still eligible when the cache is under pressure: RemoveCachedChunk
        // invalidates their generations and safely cancels/releases their work.
        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
        {
            if (_cacheProtectedKeys.Contains(key))
                continue;

            int span = ChunkCells * key.Step;
            float distanceSquared = BlockCenterDistanceSquared(
                cameraPosition,
                key.X * span,
                key.Y * span,
                key.Z * span,
                span);

            eviction.Add(new CacheEvictionCandidate(
                key,
                entry.LastAccessFrame,
                entry.LastSeenFrame,
                distanceSquared));
        }

        eviction.Sort(static (left, right) =>
        {
            int byAccess = left.LastAccessFrame.CompareTo(right.LastAccessFrame);
            if (byAccess != 0)
                return byAccess;

            int byLastDraw = left.LastSeenFrame.CompareTo(right.LastSeenFrame);
            if (byLastDraw != 0)
                return byLastDraw;

            // For equally old cache entries, evict the farthest first.
            return right.DistanceSquared.CompareTo(left.DistanceSquared);
        });

        int excess = _chunks.Count - MaximumCachedChunks;
        int removeCount = Math.Min(excess, eviction.Count);

        for (int i = 0; i < removeCount; i++)
            RemoveCachedChunk(eviction[i].Key, "lru-cache-limit");
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
            int firstX = key.X * span - key.Step * 2;
            int firstY = key.Y * span - key.Step * 2;
            int firstZ = key.Z * span - key.Step * 2;
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

        _disposed = true;
        if (_observedField != null)
            _observedField.Changed -= OnVoxelFieldChanged;
        _observedField = null;

        // Stop producers first, then let in-flight CPU work observe cancellation.
        foreach (ChunkEntry entry in _chunks.Values)
            entry.RunningBuild?.Cancellation.Cancel();

        // CancellationToken wakes every worker blocked in SemaphoreSlim.Wait.
        // Join is used only during renderer shutdown, never during a frame.
        _workerShutdown.Cancel();
        foreach (Thread worker in _workerThreads)
            worker.Join();

        while (_buildQueue.TryDequeue(out BuildRequest? queued))
            queued.Cancellation.Dispose();
        while (_completedBuildQueue.TryDequeue(out BuildCompletion completed))
        {
            completed.Request.Cancellation.Dispose();
            if (completed.Result is VoxelMeshBuildResult result)
                ReturnBuildCpuData(result);
        }

        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
        {
            entry.RunningBuild = null;
            if (entry.ReadyBuild is VoxelMeshBuildResult readyBuild)
                ReturnBuildCpuData(readyBuild);

            int gpuMeshCount =
                entry.Surface.Length +
                entry.CutCap.Length +
                entry.UploadingSurface.Count +
                entry.UploadingCutCap.Count;
            _totalGpuUnloads += gpuMeshCount;
            entry.Dispose();
        }

        _dynamicFragments.Dispose();
        _chunks.Clear();
        _gpuMeshHashCache.Clear();
        _readyUploadQueue.Clear();
        _buildSignal.Dispose();
        _workerShutdown.Dispose();

        // UnloadMaterial also releases the non-default shader assigned to it.
        UnloadMaterial(_material);
        _logger.Write(
            "RENDERER_STOP",
            $"requests={_totalBuildRequests} completed={_totalBuildCompleted} cancelled={_totalBuildCancelled} failed={_totalBuildFailed} stale={_totalStaleResults} repeats={_totalRepeatedBuilds} gpuUploads={_totalGpuUploads} gpuUnloads={_totalGpuUnloads} identicalSurfaceSkips={_totalIdenticalSurfaceSkips} identicalCutSkips={_totalIdenticalCutSkips}");
        _logger.Dispose();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
