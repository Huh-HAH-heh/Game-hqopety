using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Core.Map;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// Signed-density volumetric renderer. Screen-space LOD changes the sampling
/// interval while preserving the full-resolution editable density field.
/// All chunks in one frame share the same step, keeping seams watertight.
/// </summary>
internal sealed class VoxelTerrainRenderer : IDisposable
{
    // Keep mesh ownership local, but dispatch the full visible set as one build batch.
    private const int ChunkCells = VoxelChunkBuildInput.DefaultCellsPerAxis * 2;
    // Requests now hold metadata + one shared immutable field snapshot; density arrays
    // are rented only by active workers, so the queue can cover the whole visible set.
    private const int MaximumPendingMeshJobs = 4096;
    private const int MaximumWorldSamplesPerAxis = 4096;
    private const int MaximumWorldMeshVertices = 64_000_000;
    private const int GpuPageBufferVertices = 2_000_000;
    private const int FallbackLodStep = MaximumRenderStep;
    // Use the available physical cores for density capture and Surface Nets builds.
    private static int WorkerCount => Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
    private const double GpuUploadBudgetMilliseconds = 3.0;
    private const int CutCapSettleFrames = 8;
    private const int MaximumRenderStep = 64;
    private const int CacheEvictionScanBudget = 4096;
    private const int MaximumCachedChunks = 4096;
    private const float LodFadeDurationSeconds = 0.25f;
    private const double LodTargetSettleMilliseconds = 350d;

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
        uniform float uLodFade;
        uniform int uLodRole;
        out vec4 finalColor;
        void main()
        {
            if (uCutEnabled != 0 && fragWorldPosition.y > uCutHeight + 0.0001)
                discard;

            if (uLodRole >= 0)
            {
                float lodNoise = fract(sin(dot(
                    floor(gl_FragCoord.xy),
                    vec2(12.9898, 78.233))) * 43758.5453);
                if ((uLodRole == 0 && lodNoise < uLodFade) ||
                    (uLodRole == 1 && lodNoise >= uLodFade))
                    discard;
            }

            vec3 n = normalize(fragWorldNormal);
            float faceAO = 0.85;

            if (n.y > 0.5)
                faceAO = 1.0;
            else if (n.y < -0.5)
                faceAO = 0.5;
            else if (abs(n.z) > 0.5)
                faceAO = 0.75;

            float diffuse = max(dot(n, normalize(uSunDirection)), 0.0);
            float lighting = max(
                uAmbientLight + diffuse * uSunIntensity,
                0.05);

            finalColor = vec4(
                fragColor.rgb * faceAO * lighting,
                fragColor.a);
        }
        """;

    private readonly record struct ChunkKey(int X, int Y, int Z, int Step);
    private readonly record struct GpuPageKey(int Step);
    private readonly record struct GpuMeshRegion(
        int BufferIndex,
        int Offset,
        int Capacity,
        int VertexCount);

    private sealed class GpuMeshBuffer : IDisposable
    {
        public Mesh Mesh;
        public readonly int Capacity;
        public int ReservedVertexCount;
        public int DrawVertexCount;

        public GpuMeshBuffer(Mesh mesh, int capacity)
        {
            Mesh = mesh;
            Capacity = capacity;
        }

        public void Dispose()
        {
            if (Mesh.VaoId != 0)
                UnloadMesh(Mesh);
            Mesh = default;
            ReservedVertexCount = 0;
            DrawVertexCount = 0;
        }
    }

    // One logical world page per LOD, split into append-only GPU buffers.
    // New chunks append to free ranges; existing chunks update only their own
    // VBO range. A new result never forces an unchanged chunk to be re-uploaded.
    private sealed class GpuMeshPage : IDisposable
    {
        public readonly List<GpuMeshBuffer> Buffers = new(4);
        public readonly Dictionary<ChunkKey, GpuMeshRegion> Regions = new();
        public int ReservedVertexCount;
        public bool Dirty;
        public bool HasMesh => Buffers.Count > 0;

        public void Dispose()
        {
            foreach (GpuMeshBuffer buffer in Buffers)
                buffer.Dispose();
            Buffers.Clear();
            Regions.Clear();
            ReservedVertexCount = 0;
            Dirty = false;
        }
    }

    private sealed class ChunkEntry : IDisposable
    {
        public Mesh[] CutCap = Array.Empty<Mesh>();
        public long BaseVersion = long.MinValue;
        public bool SurfaceDirty = true;
        public bool CutDirty = true;
        public int PaletteVersion = int.MinValue;
        public int CutLayer = int.MinValue;
        public long LastSeenFrame;
        public bool Built;

        public VoxelMeshData[] PendingSurface = Array.Empty<VoxelMeshData>();
        public bool HasPendingSurface;
        public ulong PendingSurfaceHash;
        public long PendingSurfaceBaseVersion;
        public int PendingSurfacePaletteVersion;
        public int PendingSurfaceCutLayer;

        public int RequestRevision;
        public long BuildGeneration;
        public int FailedRevision = int.MinValue;
        public int QueueRejectedRevision = int.MinValue;
        public bool BuildQueued;
        public BuildRequest? RunningBuild;
        public VoxelMeshBuildResult? ReadyBuild;
        public BuildRequest? ReadyRequest;
        public List<Mesh> UploadingCutCap = new();
        public int CutUploadIndex;
        public bool UploadQueued;
        public long UploadQueueGeneration;

        public void Dispose()
        {
            ReturnMeshData(PendingSurface);
            foreach (Mesh mesh in CutCap) UnloadMesh(mesh);
            foreach (Mesh mesh in UploadingCutCap) UnloadMesh(mesh);
            PendingSurface = Array.Empty<VoxelMeshData>();
            CutCap = Array.Empty<Mesh>();
            UploadingCutCap.Clear();
            ReadyBuild = null;
            Built = false;
        }
    }

    private sealed class BuildRequest
    {
        public required ChunkKey Key { get; init; }
        public required ChunkEntry Entry { get; init; }
        public required int RequestRevision { get; init; }
        public required long Generation { get; init; }
        public required int PaletteVersion { get; init; }
        public required long BaseVersion { get; init; }
        public required bool BuildSurface { get; init; }
        public required bool BuildCutCap { get; init; }
        public required int CutLayer { get; init; }
        public required TerrainPalette Palette { get; init; }
        public required IVoxelFieldSource Source { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public long EnqueuedTimestamp { get; init; }
    }

    private readonly record struct VoxelMeshBuildResult(
        VoxelMeshData[] Surface,
        VoxelMeshData[] CutCap,
        bool HasSurface,
        bool HasCutCap,
        bool SurfaceWasEmpty,
        int CutLayer,
        double WorkerMilliseconds,
        double CaptureMilliseconds,
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

    private readonly record struct ReadyUpload(
        ChunkKey Key,
        ChunkEntry Entry,
        long Generation);

    private readonly Dictionary<ChunkKey, ChunkEntry> _chunks = new();
    private readonly Dictionary<GpuPageKey, GpuMeshPage> _gpuPages = new();
    private readonly List<GpuPageKey> _dirtyGpuPageKeys = new();
    private readonly List<(GpuPageKey Key, GpuMeshPage Page)> _visiblePages = new();
    private readonly List<(GpuPageKey Key, GpuMeshPage Page)> _transitionVisiblePages = new();
    private readonly HashSet<GpuPageKey> _visiblePageKeys = new();
    private readonly HashSet<GpuPageKey> _transitionVisiblePageKeys = new();
    private readonly List<ChunkKey> _inactiveLodChunkScratch = new();
    private readonly List<Candidate> _coarseCandidateScratch = new();
    private readonly List<(Candidate Candidate, ChunkEntry Entry)> _coarseCandidateEntriesScratch = new();
    private readonly List<(ChunkKey Key, ChunkEntry Entry)> _coarseVisible = new();
    private readonly List<(GpuPageKey Key, GpuMeshPage Page)> _coarseVisiblePages = new();
    private readonly HashSet<ChunkKey> _coarseCandidateKeys = new();
    private readonly HashSet<GpuPageKey> _coarseVisiblePageKeys = new();
    private readonly List<GpuPageKey> _inactiveLodPageScratch = new();
    // One immutable snapshot represents the entire sparse voxel field and is reused
    // until the field version changes. Per-chunk density buffers are created by workers.
    private IVoxelFieldSource? _worldBuildSnapshot;
    private SparseVoxelField? _snapshotField;
    private long _worldBuildSnapshotVersion = long.MinValue;
    private long _worldBuildSnapshotBaseVersion = long.MinValue;
    private readonly Dictionary<ChunkKey, ChunkGpuHashes> _gpuMeshHashCache = new();

    private readonly Queue<ReadyUpload> _readyUploadQueue = new();
    private readonly ConcurrentQueue<BuildRequest> _buildQueue = new();
    private readonly ConcurrentQueue<BuildCompletion> _completedBuildQueue = new();
    private readonly SemaphoreSlim _buildSignal = new(0);
    private int _batchWakePending;
    private int _pendingBatchWakeTokens;
    private readonly Task[] _workerTasks;
    private int _activeWorkers;
    private int _runningMeshJobs;
    private int _workerStopping;
    private int _renderStep = 1;
    private int _pendingRenderStep;
    private long _pendingRenderStepTimestamp;
    private int CurrentDisplayStep => _displayStep > 0 ? _displayStep : _renderStep;

    private readonly List<Candidate> _candidateScratch = new();
    private readonly List<(Candidate Candidate, ChunkEntry Entry)> _candidateEntriesScratch = new();
    private readonly List<Candidate> _transitionCandidateScratch = new();
    private readonly List<(Candidate Candidate, ChunkEntry Entry)> _transitionEntriesScratch = new();
    private readonly HashSet<ChunkKey> _transitionCandidateKeys = new();
    private readonly Queue<(ChunkKey Key, ChunkEntry Entry)> _evictionQueue = new();
    private readonly List<(ChunkKey Key, ChunkEntry Entry)> _visible = new();
    private readonly List<(ChunkKey Key, ChunkEntry Entry)> _transitionVisible = new();
    private readonly HashSet<ChunkKey> _visibleCandidateKeys = new();
    private int _displayStep;
    private bool _lodTransitionActive;
    private float _lodFade;

    private readonly DynamicVoxelFragmentRenderer _dynamicFragments;
    private readonly TerrainPipelineLogger _logger;
    private readonly Dictionary<BuildIdentity, int> _completedBuildCounts = new();

    private readonly Material _material;
    private readonly Shader _shader;
    private readonly int _cutEnabledLocation;
    private readonly int _cutHeightLocation;
    private readonly int _sunDirectionLocation;
    private readonly int _ambientLightLocation;
    private readonly int _sunIntensityLocation;
    private readonly int _lodFadeLocation;
    private readonly int _lodRoleLocation;

    private TerrainPalette _palette;
    private SparseVoxelField? _observedField;
    private int _paletteVersion;
    private long _frame;
    private bool _disposed;

    private int _requestedCutLayer = int.MinValue;
    private int _stableCutLayer = int.MinValue;
    private int _cutLayerStableFrames;

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

    public int ActiveRenderCellSize => CurrentDisplayStep;
    public float ActiveVoxelSizeMeters => SparseVoxelField.VoxelSizeMeters * CurrentDisplayStep;
    public float RenderChunkSizeMeters =>
        ChunkCells * CurrentDisplayStep * SparseVoxelField.VoxelSizeMeters;
    public bool IsUsingCoarseLod => CurrentDisplayStep > 1;
    public int Step1ChunksLastFrame { get; private set; }
    public int Step2ChunksLastFrame { get; private set; }
    public int Step4ChunksLastFrame { get; private set; }
    public int Step8ChunksLastFrame { get; private set; }
    public int Step16ChunksLastFrame { get; private set; }
    public int Step32ChunksLastFrame { get; private set; }
    public int Step64ChunksLastFrame { get; private set; }
    public int DrawCallsLastFrame { get; private set; }
    public int VisibleChunksLastFrame { get; private set; }
    public int RebuiltChunks { get; private set; }
    public int RebuiltCutCaps { get; private set; }
    public int LodPrewarmBuilds => 0;

    public double CpuCaptureMillisecondsLastFrame { get; private set; }
    public double GpuUploadMillisecondsLastFrame { get; private set; }
    public int RunningMeshJobs => Volatile.Read(ref _runningMeshJobs);
    public double CpuCaptureMilliseconds => CpuCaptureMillisecondsLastFrame;
    public double GpuUploadMilliseconds => GpuUploadMillisecondsLastFrame;
    public int PendingMeshJobs => _buildQueue.Count;
    public int CompletedMeshResultsPending => _completedBuildQueue.Count;
    public double WorkerBuildMillisecondsLastFrame { get; private set; }
    public double LastWorkerBuildMilliseconds { get; private set; }
    public double LastWorkerQueueWaitMilliseconds { get; private set; }
    public double LastWorkerResultDeliveryMilliseconds { get; private set; }
    public int BuildsCompletedLastFrame { get; private set; }
    public int LastBuildStep { get; private set; } = 1;
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
    public int MacroFrustumTestsLastFrame { get; private set; }
    public int VisibleMacroRegionsLastFrame { get; private set; }
    public int CandidateOverflowLastFrame { get; private set; }
    public string DiagnosticsLogPath => _logger.LogPath;
    public int VisibleCandidatesLastFrame => _visibleCandidateKeys.Count;
    public int CachedChunks => _chunks.Count;
    public int DynamicFragmentCount => _dynamicFragments.Count;
    public bool IsMergingFragments => _dynamicFragments.IsMerging;

    public VoxelTerrainRenderer(TerrainPalette palette, TerrainPipelineLogger logger)
    {
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dynamicFragments = new DynamicVoxelFragmentRenderer(_logger);

        _shader = LoadShaderFromMemory(VertexShader, FragmentShader);
        if (!IsShaderValid(_shader))
            throw new InvalidOperationException("The 3D voxel surface shader could not be created.");

        _cutEnabledLocation = GetShaderLocation(_shader, "uCutEnabled");
        _cutHeightLocation = GetShaderLocation(_shader, "uCutHeight");
        _sunDirectionLocation = GetShaderLocation(_shader, "uSunDirection");
        _ambientLightLocation = GetShaderLocation(_shader, "uAmbientLight");
        _sunIntensityLocation = GetShaderLocation(_shader, "uSunIntensity");
        _lodFadeLocation = GetShaderLocation(_shader, "uLodFade");
        _lodRoleLocation = GetShaderLocation(_shader, "uLodRole");

        if (_cutEnabledLocation < 0 ||
            _cutHeightLocation < 0 ||
            _sunDirectionLocation < 0 ||
            _ambientLightLocation < 0 ||
            _sunIntensityLocation < 0 ||
            _lodFadeLocation < 0 ||
            _lodRoleLocation < 0)
        {
            UnloadShader(_shader);
            throw new InvalidOperationException("The 3D voxel shader is missing a required uniform.");
        }

        _material = LoadMaterialDefault();
        _material.Shader = _shader;

        int workerCount = WorkerCount;
        _workerTasks = new Task[workerCount];
        _activeWorkers = workerCount;
        for (int i = 0; i < workerCount; i++)
            _workerTasks[i] = Task.Run(WorkerLoopAsync);

        _logger.Write(
            "RENDERER_START",
            $"resolution=0.1m;scope=full-world-density-field;gpuMesh=single-global-per-lod;cameraCulling=false;chunkCells={ChunkCells};chunkMeters={RenderChunkSizeMeters:0.0};storageChunkCells={SparseVoxelField.ChunkSize};workers={workerCount};build=background-worker-pool;pendingLimit={MaximumPendingMeshJobs};mesher=surface-nets-density");
        _logger.Write(RenderLogMode.ErrorsOnly, "VOXEL_RENDERER_READY", $"logPath=\"{_logger.LogPath}\" workers={workerCount} build=background");
    }

    public void SetPalette(TerrainPalette palette)
    {
        ThrowIfDisposed();
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
        UpdateRenderStep(field, camera, viewportHeight);

        long voxelFrameStarted = Stopwatch.GetTimestamp();
        float frameTime = Math.Clamp(GetFrameTime(), 0f, 0.05f);
        _dynamicFragments.Update(field, frameTime);

        _frame++;
        RebuiltChunks = 0;
        RebuiltCutCaps = 0;
        CpuCaptureMillisecondsLastFrame = 0d;
        WorkerBuildMillisecondsLastFrame = 0d;
        BuildsCompletedLastFrame = 0;
        GpuUploadsLastFrame = 0;

        CollectCompletedBuilds(field);

        int cutLayer = cutEnabled
            ? (int)MathF.Round(cutHeightMeters / SparseVoxelField.VoxelSizeMeters)
            : -1;
        UpdateStableCutLayer(cutEnabled, cutLayer);
        int stableCutLayer = cutEnabled &&
            _cutLayerStableFrames >= CutCapSettleFrames
                ? _stableCutLayer
                : -1;
        int targetCutLayer = cutEnabled && stableCutLayer >= 0
            ? stableCutLayer
            : int.MinValue;

        // Step 64 is the permanent coarse world representation. Queue it first
        // so it appears before the camera-selected detail level is complete.
        _coarseCandidateScratch.Clear();
        _coarseCandidateEntriesScratch.Clear();
        _coarseCandidateKeys.Clear();
        bool coarseIsPrimary = _displayStep == FallbackLodStep ||
            _renderStep == FallbackLodStep;
        if (!coarseIsPrimary)
        {
            int coarseFrustumTests = 0;
            int coarseMacroTests = 0;
            int coarseRegions = 0;
            CollectVisibleCandidates(
                field, camera, FallbackLodStep,
                _coarseCandidateScratch,
                ref coarseFrustumTests,
                ref coarseMacroTests,
                ref coarseRegions);
            _coarseCandidateScratch.Sort(static (a, b) =>
                a.DistanceSquared.CompareTo(b.DistanceSquared));
            PrepareCandidateEntries(
                field,
                _coarseCandidateScratch,
                int.MinValue,
                _coarseCandidateEntriesScratch,
                _coarseCandidateKeys);
        }

        List<Candidate> displayCandidates = _candidateScratch;
        displayCandidates.Clear();
        int frustumTests = 0;
        int macroFrustumTests = 0;
        int visibleMacroRegions = 0;

        List<Candidate> transitionCandidates = _transitionCandidateScratch;
        List<(Candidate Candidate, ChunkEntry Entry)> transitionEntries =
            _transitionEntriesScratch;
        transitionCandidates.Clear();
        transitionEntries.Clear();
        _transitionCandidateKeys.Clear();

        if (_renderStep == _displayStep)
        {
            CollectVisibleCandidates(
                field, camera, _displayStep,
                displayCandidates,
                ref frustumTests, ref macroFrustumTests, ref visibleMacroRegions);
            displayCandidates.Sort(static (a, b) =>
                a.DistanceSquared.CompareTo(b.DistanceSquared));

            PrepareCandidateEntries(
                field,
                displayCandidates,
                _displayStep == FallbackLodStep ? int.MinValue : targetCutLayer,
                _candidateEntriesScratch,
                _visibleCandidateKeys);
        }
        else
        {
            // During a LOD change, keep only already-resident meshes from the
            // old level. Never schedule thousands of obsolete fine meshes.
            CollectCachedVisibleCandidates(
                field, camera, _displayStep,
                displayCandidates, _candidateEntriesScratch,
                _visibleCandidateKeys);

            // Build the requested level directly. Do not walk through every
            // intermediate LOD: a distant camera can jump from step 1 to 64.
            CollectVisibleCandidates(
                field, camera, _renderStep,
                transitionCandidates,
                ref frustumTests, ref macroFrustumTests, ref visibleMacroRegions);
            transitionCandidates.Sort(static (a, b) =>
                a.DistanceSquared.CompareTo(b.DistanceSquared));

            PrepareCandidateEntries(
                field,
                transitionCandidates,
                _renderStep == FallbackLodStep ? int.MinValue : targetCutLayer,
                transitionEntries,
                _transitionCandidateKeys);
        }

        // Retire invalid work before waking workers. Full-world candidates stay requested.
        // Worker queue records remain lightweight until a worker captures density data.
        CancelUnseenBuilds();

        // All world-region requests are queued before waking workers.
        // The worker pool receives one batch wake-up, not one signal per chunk.
        SignalBuildWorkers();

        FrustumCandidatesLastFrame = frustumTests;
        MacroFrustumTestsLastFrame = macroFrustumTests;
        VisibleMacroRegionsLastFrame = visibleMacroRegions;
        CandidateOverflowLastFrame = 0;
        Step1ChunksLastFrame = _displayStep == 1 ? displayCandidates.Count : 0;
        Step2ChunksLastFrame = _displayStep == 2 ? displayCandidates.Count : 0;
        Step4ChunksLastFrame = _displayStep == 4 ? displayCandidates.Count : 0;
        Step8ChunksLastFrame = _displayStep == 8 ? displayCandidates.Count : 0;
        Step16ChunksLastFrame = _displayStep == 16 ? displayCandidates.Count : 0;
        Step32ChunksLastFrame = _displayStep == 32 ? displayCandidates.Count : 0;
        Step64ChunksLastFrame = _displayStep == 64 ? displayCandidates.Count : 0;

        EvictInactiveLodChunks();
        CollectCompletedBuilds(field);
        UploadReadyMeshes();
        UploadDirtyGpuPages();
        if (_renderStep != _displayStep &&
            !_lodTransitionActive &&
            HasRenderableLodPage(_renderStep))
        {
            // Switch as soon as the first target chunk reaches the GPU.
            // The remaining chunks continue building without blocking this page.
            _lodTransitionActive = true;
            _lodFade = 1f;
            _logger.Write(
                "VOXEL_LOD_PARTIAL_SWITCH",
                $"display={_displayStep} target={_renderStep} ready={_transitionVisible.Count} requested={transitionEntries.Count}");
        }

        if (_lodTransitionActive)
            _lodFade = Math.Clamp(
                _lodFade + frameTime / LodFadeDurationSeconds, 0f, 1f);

        _visible.Clear();
        foreach ((Candidate candidate, ChunkEntry entry) in _candidateEntriesScratch)
        {
            bool hasVisibleSurface = HasCachedSurface(candidate.Key);
            bool hasVisibleCutCap = cutEnabled && stableCutLayer >= 0 &&
                entry.CutLayer == stableCutLayer && entry.CutCap.Length > 0;
            if (entry.Built && (hasVisibleSurface || hasVisibleCutCap))
                AddVisible(candidate.Key, entry);
        }

        _transitionVisible.Clear();
        if (_lodTransitionActive)
        {
            foreach ((Candidate candidate, ChunkEntry entry) in transitionEntries)
            {
                bool hasVisibleSurface = HasCachedSurface(candidate.Key);
                bool hasVisibleCutCap = cutEnabled && stableCutLayer >= 0 &&
                    entry.CutLayer == stableCutLayer && entry.CutCap.Length > 0;
                if (entry.Built && (hasVisibleSurface || hasVisibleCutCap))
                    _transitionVisible.Add((candidate.Key, entry));
            }
        }

        double remainingUploadBudget = Math.Max(
            0d, GpuUploadBudgetMilliseconds - GpuUploadMillisecondsLastFrame);
        if (remainingUploadBudget > 0d)
            GpuUploadMillisecondsLastFrame +=
                _dynamicFragments.UploadPendingMeshes(remainingUploadBudget);

        _dynamicFragments.ReleaseBakedBodies(
            bounds => AreCachedMeshesCurrent(bounds, field));

        if ((_frame % 300) == 0)
        {
            string summary =
                $"frame={_frame} displayStep={_displayStep} buildStep={_renderStep} fade={_lodFade:0.00} " +
                $"candidates={_visibleCandidateKeys.Count} transitionCandidates={transitionCandidates.Count} " +
                $"visible={_visible.Count} transitionVisible={_transitionVisible.Count} cache={_chunks.Count} empty={_totalEmptyChunks} " +
                $"buildMs={WorkerBuildMillisecondsLastFrame:0.00} queueMs={LastWorkerQueueWaitMilliseconds:0.0} gpuMs={GpuUploadMillisecondsLastFrame:0.00} " +
                $"packets={GpuUploadsLastFrame} builds={_totalBuildCompleted}/{_totalBuildRequests} pending={_buildQueue.Count} " +
                $"failed={_totalBuildFailed} stale={_totalStaleResults} deferred={_totalQueueSaturated} repeat={_totalRepeatedBuilds} " +
                $"uploads={_totalGpuUploads} unloads={_totalGpuUnloads} cacheHits={_totalIdenticalSurfaceSkips} " +
                $"gpuPages={_gpuPages.Count} dirtyPages={_dirtyGpuPageKeys.Count}";
            _logger.Write("FRAME_SUMMARY", summary);
            Console.WriteLine($"[TERRAIN][PERF] {summary}");
        }

        float renderCutHeightMeters = cutEnabled && stableCutLayer >= 0
            ? stableCutLayer * SparseVoxelField.VoxelSizeMeters
            : cutHeightMeters;
        SetLighting();
        SetCutShader(cutEnabled, renderCutHeightMeters);

        float worldRadius = 0.5f * MathF.Sqrt(
            field.Width * field.Width * SparseVoxelField.VoxelSizeMeters * SparseVoxelField.VoxelSizeMeters +
            field.Depth * field.Depth * SparseVoxelField.VoxelSizeMeters * SparseVoxelField.VoxelSizeMeters +
            field.Height * field.Height * SparseVoxelField.VoxelSizeMeters * SparseVoxelField.VoxelSizeMeters);
        float distanceToOrigin = camera.Position.Length();
        float cameraDistance = Vector3.Distance(camera.Position, camera.Target);
        Rlgl.SetClipPlanes(
            Math.Clamp(cameraDistance * 0.002f, 0.02f, 2f),
            MathF.Max(1000f, distanceToOrigin + worldRadius + 100f));

        CollectVisiblePages(_visible, _visiblePages, _visiblePageKeys);
        CollectVisiblePages(_transitionVisible, _transitionVisiblePages, _transitionVisiblePageKeys);

        _coarseVisiblePages.Clear();
        _coarseVisiblePageKeys.Clear();
        bool hasCurrentLodMesh = _visiblePages.Count > 0 ||
            (_lodTransitionActive && _transitionVisiblePages.Count > 0);
        if (!hasCurrentLodMesh &&
            _displayStep != FallbackLodStep &&
            _renderStep != FallbackLodStep &&
            _gpuPages.TryGetValue(new GpuPageKey(FallbackLodStep), out GpuMeshPage? coarsePage) &&
            coarsePage.HasMesh && !coarsePage.Dirty)
        {
            _coarseVisiblePages.Add((new GpuPageKey(FallbackLodStep), coarsePage));
        }

        BeginMode3D(camera);
        int draws = 0;
        if (_lodTransitionActive)
        {
            SetLodShader(0, _lodFade);
            foreach ((GpuPageKey key, GpuMeshPage page) in _visiblePages)
                draws += DrawGpuPage(page, _material);

            SetLodShader(1, _lodFade);
            foreach ((GpuPageKey key, GpuMeshPage page) in _transitionVisiblePages)
                draws += DrawGpuPage(page, _material);
        }
        else
        {
            SetLodShader(-1, 0f);
            foreach ((GpuPageKey key, GpuMeshPage page) in _visiblePages)
                draws += DrawGpuPage(page, _material);

            // Until any camera-selected chunk is ready, keep drawing the
            // permanent coarse LOD instead of presenting a black world.
            foreach ((GpuPageKey key, GpuMeshPage page) in _coarseVisiblePages)
                draws += DrawGpuPage(page, _material);
        }

        SetLodShader(-1, 0f);
        SetCutShader(cutEnabled, renderCutHeightMeters);
        _dynamicFragments.Draw(_material);
        EndMode3D();

        DrawCallsLastFrame = draws;
        VisibleChunksLastFrame = _visible.Count +
            (_lodTransitionActive ? _transitionVisible.Count : 0);
        if (_lodTransitionActive && _lodFade >= 1f)
        {
            int previousDisplayStep = _displayStep;
            _displayStep = _renderStep;
            _lodTransitionActive = false;
            _lodFade = 0f;
            ReleaseInactiveLodData();
            _logger.Write(
                "VOXEL_LOD_SWITCHED",
                $"from={previousDisplayStep} to={_displayStep} transition=0.25s chunks={_transitionVisible.Count}");
        }

        if (_logger.IsEnabled(RenderLogMode.FrameSummary))
            _logger.Write(RenderLogMode.FrameSummary, "VOXEL_FRAME",
                $"elapsedMs={Stopwatch.GetElapsedTime(voxelFrameStarted).TotalMilliseconds:0.000} displayStep={_displayStep} buildStep={_renderStep} " +
                $"surfaceCandidates={VisibleCandidatesLastFrame} visibleChunks={VisibleChunksLastFrame} draws={DrawCallsLastFrame} " +
                $"chunkCache={CachedChunks} rebuilt={RebuiltChunks} cutRebuilt={RebuiltCutCaps} completed={BuildsCompletedLastFrame} " +
                $"uploads={GpuUploadsLastFrame} workers={_workerTasks.Length} buildQueue={PendingMeshJobs} results={CompletedMeshResultsPending} " +
                $"uploadQueue={_readyUploadQueue.Count} captureMs={CpuCaptureMillisecondsLastFrame:0.000} " +
                $"meshBuildMs={WorkerBuildMillisecondsLastFrame:0.000} gpuMs={GpuUploadMillisecondsLastFrame:0.000}");
    }

    private void CollectCachedVisibleCandidates(
        SparseVoxelField field,
        Camera3D camera,
        int renderStep,
        List<Candidate> candidates,
        List<(Candidate Candidate, ChunkEntry Entry)> entries,
        HashSet<ChunkKey> keys)
    {
        candidates.Clear();
        entries.Clear();
        keys.Clear();

        float voxelSize = SparseVoxelField.VoxelSizeMeters;
        Vector3 cameraPosition = camera.Position;

        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
        {
            if (key.Step != renderStep || !entry.Built || !HasCachedSurface(key))
                continue;

            int chunkSpan = ChunkCells * renderStep;
            int originX = key.X * chunkSpan;
            int originY = key.Y * chunkSpan;
            int originZ = key.Z * chunkSpan;
            int sizeX = Math.Min(chunkSpan, field.Width - originX);
            int sizeY = Math.Min(chunkSpan, field.Height - originY);
            int sizeZ = Math.Min(chunkSpan, field.Depth - originZ);

            Vector3 center = new(
                (originX + sizeX * 0.5f) * voxelSize,
                (originY + sizeY * 0.5f) * voxelSize,
                (originZ + sizeZ * 0.5f) * voxelSize);
            float halfX = sizeX * voxelSize * 0.5f;
            float halfY = sizeY * voxelSize * 0.5f;
            float halfZ = sizeZ * voxelSize * 0.5f;
            float radius = MathF.Sqrt(
                halfX * halfX + halfY * halfY + halfZ * halfZ);

            Candidate candidate = new(
                key, center, radius,
                Vector3.DistanceSquared(center, cameraPosition));
            entries.Add((candidate, entry));
            keys.Add(key);
            entry.LastSeenFrame = _frame;
        }

        entries.Sort(static (a, b) =>
            a.Candidate.DistanceSquared.CompareTo(b.Candidate.DistanceSquared));
        candidates.Clear();
        foreach ((Candidate candidate, ChunkEntry _) in entries)
            candidates.Add(candidate);
    }

    

    private static GpuPageKey GetGpuPageKey(ChunkKey key) => new(key.Step);

    private static int GetGpuRegionCapacity(int vertexCount)
    {
        if (vertexCount <= 0) return 0;
        int reserve = Math.Max(192, vertexCount / 8);
        return checked(((vertexCount + reserve + 2) / 3) * 3);
    }

    private bool HasCachedSurface(ChunkKey key)
    {
        GpuPageKey pageKey = GetGpuPageKey(key);
        return _gpuPages.TryGetValue(pageKey, out GpuMeshPage? page) &&
            page.Regions.TryGetValue(key, out GpuMeshRegion region) &&
            region.VertexCount > 0;
    }

    private void MarkGpuPageDirty(ChunkKey key)
    {
        GpuPageKey pageKey = GetGpuPageKey(key);
        if (!_gpuPages.TryGetValue(pageKey, out GpuMeshPage? page))
        {
            page = new GpuMeshPage();
            _gpuPages.Add(pageKey, page);
        }

        if (page.Dirty) return;
        page.Dirty = true;
        _dirtyGpuPageKeys.Add(pageKey);
    }

    private void CollectVisiblePages(
        List<(ChunkKey Key, ChunkEntry Entry)> chunks,
        List<(GpuPageKey Key, GpuMeshPage Page)> pages,
        HashSet<GpuPageKey> keys)
    {
        pages.Clear();
        keys.Clear();
        foreach ((ChunkKey chunkKey, ChunkEntry _) in chunks)
        {
            GpuPageKey key = GetGpuPageKey(chunkKey);
            if (!_gpuPages.TryGetValue(key, out GpuMeshPage? page) ||
                !page.HasMesh || page.Dirty || !keys.Add(key))
                continue;
            pages.Add((key, page));
        }
    }

    private void QueueSurfacePageUpdate(
        ChunkKey key,
        ChunkEntry entry,
        VoxelMeshData[] surface,
        ulong hash,
        BuildRequest request)
    {
        if (entry.HasPendingSurface) ReturnMeshData(entry.PendingSurface);
        entry.PendingSurface = surface;
        entry.HasPendingSurface = true;
        entry.PendingSurfaceHash = hash;
        entry.PendingSurfaceBaseVersion = request.BaseVersion;
        entry.PendingSurfacePaletteVersion = request.PaletteVersion;
        entry.PendingSurfaceCutLayer = request.CutLayer;
        entry.SurfaceDirty = true;
        MarkGpuPageDirty(key);
    }

    private static GpuMeshBuffer CreateGpuBuffer(int capacity)
    {
        Mesh mesh = new(capacity, capacity / 3);
        try
        {
            mesh.AllocVertices();
            mesh.AllocNormals();
            mesh.AllocColors();
            mesh.VerticesAs<Vector3>().Clear();
            mesh.NormalsAs<Vector3>().Clear();
            mesh.ColorsAs<byte>().Clear();
            UploadMesh(ref mesh, false);
            return new GpuMeshBuffer(mesh, capacity);
        }
        catch
        {
            if (mesh.VaoId != 0) UnloadMesh(mesh);
            throw;
        }
    }

    private GpuMeshRegion AppendGpuRegion(
        GpuMeshPage page,
        VoxelMeshData[] data,
        int vertexCount)
    {
        int capacity = GetGpuRegionCapacity(vertexCount);
        if (capacity <= 0)
            return default;

        if ((long)page.ReservedVertexCount + capacity > MaximumWorldMeshVertices)
            throw new InvalidOperationException(
                $"LOD page exceeds {MaximumWorldMeshVertices:N0} reserved vertices.");

        int bufferIndex = page.Buffers.Count - 1;
        GpuMeshBuffer? buffer = bufferIndex >= 0
            ? page.Buffers[bufferIndex]
            : null;

        if (buffer == null || buffer.ReservedVertexCount + capacity > buffer.Capacity)
        {
            int remaining = MaximumWorldMeshVertices - page.ReservedVertexCount;
            int bufferCapacity = Math.Min(
                Math.Max(GpuPageBufferVertices, capacity),
                remaining);
            if (bufferCapacity < capacity)
                throw new InvalidOperationException("No GPU page capacity remains.");

            buffer = CreateGpuBuffer(bufferCapacity);
            page.Buffers.Add(buffer);
            bufferIndex = page.Buffers.Count - 1;
            GpuUploadsLastFrame++;
            _totalGpuUploads++;
        }

        int offset = buffer.ReservedVertexCount;
        VoxelChunkMesher.UpdateTriangleSoup(
            ref buffer.Mesh, offset, capacity, data);
        buffer.ReservedVertexCount += capacity;
        page.ReservedVertexCount += capacity;
        return new GpuMeshRegion(bufferIndex, offset, capacity, vertexCount);
    }

    private static void RecalculateGpuDrawCounts(GpuMeshPage page)
    {
        foreach (GpuMeshBuffer buffer in page.Buffers)
            buffer.DrawVertexCount = 0;

        foreach (GpuMeshRegion region in page.Regions.Values)
        {
            if (region.VertexCount <= 0 ||
                (uint)region.BufferIndex >= (uint)page.Buffers.Count)
                continue;

            GpuMeshBuffer buffer = page.Buffers[region.BufferIndex];
            buffer.DrawVertexCount = Math.Max(
                buffer.DrawVertexCount,
                region.Offset + region.VertexCount);
        }
    }

    private static int DrawGpuPage(GpuMeshPage page, Material material)
    {
        int draws = 0;
        foreach (GpuMeshBuffer buffer in page.Buffers)
        {
            if (buffer.DrawVertexCount <= 0)
                continue;

            Mesh drawMesh = buffer.Mesh;
            drawMesh.VertexCount = buffer.DrawVertexCount;
            drawMesh.TriangleCount = buffer.DrawVertexCount / 3;
            DrawMesh(drawMesh, material, Matrix4x4.Identity);
            draws++;
        }

        return draws;
    }

    private void UploadDirtyGpuPages()
    {
        int dirtyCount = _dirtyGpuPageKeys.Count;
        if (dirtyCount == 0) return;
        Stopwatch timer = Stopwatch.StartNew();

        for (int dirtyIndex = 0; dirtyIndex < dirtyCount; dirtyIndex++)
        {
            GpuPageKey pageKey = _dirtyGpuPageKeys[dirtyIndex];
            if (!_gpuPages.TryGetValue(pageKey, out GpuMeshPage? page))
                continue;

            page.Dirty = false;
            foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
            {
                if (key.Step != pageKey.Step || !entry.HasPendingSurface)
                    continue;

                VoxelMeshData[] pending = entry.PendingSurface;
                int vertexCount = VoxelChunkMesher.CountTriangleVertices(pending);

                try
                {
                    bool hadRegion = page.Regions.TryGetValue(
                        key, out GpuMeshRegion oldRegion);

                    if (vertexCount == 0)
                    {
                        if (hadRegion && oldRegion.Capacity > 0)
                        {
                            VoxelChunkMesher.UpdateTriangleSoup(
                                ref page.Buffers[oldRegion.BufferIndex].Mesh,
                                oldRegion.Offset,
                                oldRegion.Capacity,
                                Array.Empty<VoxelMeshData>());
                        }

                        page.Regions[key] = hadRegion
                            ? oldRegion with { VertexCount = 0 }
                            : default;
                    }
                    else if (hadRegion && vertexCount <= oldRegion.Capacity)
                    {
                        VoxelChunkMesher.UpdateTriangleSoup(
                            ref page.Buffers[oldRegion.BufferIndex].Mesh,
                            oldRegion.Offset,
                            oldRegion.Capacity,
                            pending);
                        page.Regions[key] = oldRegion with
                        {
                            VertexCount = vertexCount
                        };
                    }
                    else
                    {
                        // Allocate and upload the replacement first. The old
                        // region remains valid if allocation or upload fails.
                        GpuMeshRegion nextRegion = AppendGpuRegion(
                            page, pending, vertexCount);

                        if (hadRegion && oldRegion.Capacity > 0)
                        {
                            VoxelChunkMesher.UpdateTriangleSoup(
                                ref page.Buffers[oldRegion.BufferIndex].Mesh,
                                oldRegion.Offset,
                                oldRegion.Capacity,
                                Array.Empty<VoxelMeshData>());
                        }

                        page.Regions[key] = nextRegion;
                    }

                    entry.PendingSurface = Array.Empty<VoxelMeshData>();
                    entry.HasPendingSurface = false;
                    entry.BaseVersion = entry.PendingSurfaceBaseVersion;
                    entry.PaletteVersion = entry.PendingSurfacePaletteVersion;
                    entry.CutLayer = entry.PendingSurfaceCutLayer;
                    entry.SurfaceDirty = false;
                    entry.CutDirty = false;
                    entry.Built = true;
                    DiscardLegacyCutCap(key, entry);
                    SetSurfaceGpuHash(key, entry.PendingSurfaceHash);
                    ReturnMeshData(pending);

                    RebuiltChunks++;
                    GpuUploadsLastFrame++;
                    _totalGpuUploads++;
                }
                catch (Exception exception)
                {
                    // One failed region must not hold up the rest of the LOD page.
                    entry.FailedRevision = entry.RequestRevision;
                    entry.PendingSurface = Array.Empty<VoxelMeshData>();
                    entry.HasPendingSurface = false;
                    entry.SurfaceDirty = true;
                    ReturnMeshData(pending);
                    _totalBuildFailed++;
                    _logger.Error(
                        "WORLD_MESH_REGION_UPDATE_FAILED",
                        $"chunk=({key.X},{key.Y},{key.Z}) step={key.Step} error={exception}");
                }
            }

            RecalculateGpuDrawCounts(page);
        }

        _dirtyGpuPageKeys.RemoveRange(0, dirtyCount);
        GpuUploadMillisecondsLastFrame += timer.Elapsed.TotalMilliseconds;
    }

    private void PrepareCandidateEntries(
        SparseVoxelField field,
        List<Candidate> candidates,
        int targetCutLayer,
        List<(Candidate Candidate, ChunkEntry Entry)> destination,
        HashSet<ChunkKey>? keys)
    {
        destination.Clear();
        keys?.Clear();
        foreach (Candidate candidate in candidates)
        {
            ChunkKey key = candidate.Key;
            keys?.Add(key);
            if (!_chunks.TryGetValue(key, out ChunkEntry? entry))
            {
                entry = new ChunkEntry();
                _chunks.Add(key, entry);
                _evictionQueue.Enqueue((key, entry));
            }

            entry.LastSeenFrame = _frame;
            bool surfaceStale = !entry.Built ||
                entry.SurfaceDirty ||
                entry.BaseVersion != field.BaseVersion ||
                entry.PaletteVersion != _paletteVersion;
            bool cutStateStale = entry.CutLayer != targetCutLayer;

            if ((entry.RunningBuild is { } pendingBuild &&
                    pendingBuild.CutLayer != targetCutLayer) ||
                (entry.ReadyRequest is { } readyRequest &&
                    readyRequest.CutLayer != targetCutLayer))
                InvalidatePendingBuild(entry);

            if ((surfaceStale || cutStateStale || entry.CutDirty) &&
                !HasPendingWork(entry))
            {
                RequestBuild(
                    field, key, entry,
                    buildSurface: true, buildCutCap: false,
                    cutLayer: targetCutLayer);
            }

            if (targetCutLayer == int.MinValue && entry.CutCap.Length > 0)
                ClearCutCap(key, entry);
            destination.Add((candidate, entry));
        }
    }

    private bool AreCandidateEntriesReady(
        List<(Candidate Candidate, ChunkEntry Entry)> entries,
        SparseVoxelField field,
        int targetCutLayer)
    {
        foreach ((Candidate candidate, ChunkEntry entry) in entries)
        {
            if (!entry.Built ||
                entry.SurfaceDirty ||
                entry.CutDirty ||
                entry.BaseVersion != field.BaseVersion ||
                entry.PaletteVersion != _paletteVersion ||
                entry.CutLayer != targetCutLayer)
                return false;
        }
        return true;
    }

        

    private void UpdateStableCutLayer(bool cutEnabled, int cutLayer)
    {
        if (!cutEnabled)
        {
            _requestedCutLayer = int.MinValue;
            _stableCutLayer = int.MinValue;
            _cutLayerStableFrames = 0;
            return;
        }

        if (cutLayer != _requestedCutLayer)
        {
            bool firstCutRequest = _requestedCutLayer == int.MinValue;
            _requestedCutLayer = cutLayer;
            _cutLayerStableFrames = firstCutRequest ? CutCapSettleFrames : 0;
            if (firstCutRequest)
                _stableCutLayer = cutLayer;
            return;
        }

        if (_cutLayerStableFrames < CutCapSettleFrames)
            _cutLayerStableFrames++;

        if (_cutLayerStableFrames >= CutCapSettleFrames)
            _stableCutLayer = cutLayer;
    }

    private void CollectVisibleCandidates(
        SparseVoxelField field,
        Camera3D camera,
        int renderStep,
        List<Candidate> candidates,
        ref int frustumTests,
        ref int macroFrustumTests,
        ref int visibleMacroRegions)
    {
        candidates.Clear();
        int chunkSpan = ChunkCells * renderStep;
        float voxelSize = SparseVoxelField.VoxelSizeMeters;
        Vector3 cameraPosition = camera.Position;

        int chunksX = DivideRoundUp(field.Width, chunkSpan);
        int chunksY = DivideRoundUp(field.Height, chunkSpan);
        int chunksZ = DivideRoundUp(field.Depth, chunkSpan);
        if (chunksX <= 0 || chunksY <= 0 || chunksZ <= 0)
            return;

        for (int y = 0; y < chunksY; y++)
        for (int z = 0; z < chunksZ; z++)
        for (int x = 0; x < chunksX; x++)
        {
            int originX = x * chunkSpan;
            int originY = y * chunkSpan;
            int originZ = z * chunkSpan;
            int sizeX = Math.Min(chunkSpan, field.Width - originX);
            int sizeY = Math.Min(chunkSpan, field.Height - originY);
            int sizeZ = Math.Min(chunkSpan, field.Depth - originZ);

            Vector3 center = new(
                (originX + sizeX * 0.5f) * voxelSize,
                (originY + sizeY * 0.5f) * voxelSize,
                (originZ + sizeZ * 0.5f) * voxelSize);
            float halfX = sizeX * voxelSize * 0.5f;
            float halfY = sizeY * voxelSize * 0.5f;
            float halfZ = sizeZ * voxelSize * 0.5f;
            float radius = MathF.Sqrt(
                halfX * halfX + halfY * halfY + halfZ * halfZ);

            candidates.Add(new Candidate(
                new ChunkKey(x, y, z, renderStep),
                center, radius,
                Vector3.DistanceSquared(center, cameraPosition)));
        }

        visibleMacroRegions = candidates.Count;
        frustumTests = 0;
        macroFrustumTests = 0;
    }

    private void UpdateRenderStep(SparseVoxelField field, Camera3D camera, int viewportHeight)
    {
        if (_lodTransitionActive)
            return;

        float distance = Vector3.Distance(camera.Position, camera.Target);
        float fovy = float.IsFinite(camera.FovY)
            ? Math.Clamp(camera.FovY, 1f, 175f)
            : 45f;
        float pixelsPerMeter = Math.Max(1, viewportHeight) /
            (2f * MathF.Max(distance, 1f) * MathF.Tan(fovy * (MathF.PI / 360f)));
        float pixelsPerVoxel = pixelsPerMeter * SparseVoxelField.VoxelSizeMeters;

        int desiredStep = 1;
        while (desiredStep < MaximumRenderStep &&
            pixelsPerVoxel * desiredStep < 0.85f)
            desiredStep <<= 1;

        int worldExtent = Math.Max(field.Width, Math.Max(field.Height, field.Depth));
        int worldStep = 1;
        while (worldStep < MaximumRenderStep &&
            DivideRoundUp(worldExtent, worldStep) > MaximumWorldSamplesPerAxis)
            worldStep <<= 1;
        desiredStep = Math.Max(desiredStep, worldStep);

        if (_displayStep == 0)
        {
            _displayStep = desiredStep;
            _renderStep = desiredStep;
            _pendingRenderStep = 0;
            _pendingRenderStepTimestamp = 0;
            _logger.Write(
                "VOXEL_LOD_INITIAL",
                $"display={_displayStep} voxelMeters={ActiveVoxelSizeMeters:0.0}");
            return;
        }

        float currentVoxelPixels = pixelsPerVoxel * _displayStep;
        if (desiredStep > _displayStep && currentVoxelPixels > 0.65f)
            desiredStep = _displayStep;
        else if (desiredStep < _displayStep && currentVoxelPixels < 1.25f)
            desiredStep = _displayStep;

        desiredStep = Math.Max(desiredStep, worldStep);
        int nextTargetStep = desiredStep;

        if (nextTargetStep == _renderStep)
        {
            _pendingRenderStep = 0;
            _pendingRenderStepTimestamp = 0;
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_pendingRenderStep != nextTargetStep)
        {
            _pendingRenderStep = nextTargetStep;
            _pendingRenderStepTimestamp = now;
            return;
        }

        if (Stopwatch.GetElapsedTime(_pendingRenderStepTimestamp, now).TotalMilliseconds <
            LodTargetSettleMilliseconds)
            return;

        _pendingRenderStep = 0;
        _pendingRenderStepTimestamp = 0;
        int previousTargetStep = _renderStep;
        if (previousTargetStep != _displayStep)
            CancelPendingBuildsForStep(previousTargetStep);

        _renderStep = nextTargetStep;
        _transitionCandidateScratch.Clear();
        _transitionEntriesScratch.Clear();
        _transitionCandidateKeys.Clear();

        if (_renderStep != _displayStep)
            _logger.Write(
                "VOXEL_LOD_PREPARE",
                $"display={_displayStep} target={_renderStep} voxelMeters={SparseVoxelField.VoxelSizeMeters * _renderStep:0.0} chunkMeters={ChunkCells * SparseVoxelField.VoxelSizeMeters * _renderStep:0.0}");
        else if (previousTargetStep != _displayStep)
            _logger.Write(
                "VOXEL_LOD_CANCEL",
                $"display={_displayStep} abandonedTarget={previousTargetStep}");
    }

    private void ReleaseInactiveLodData()
    {
        _inactiveLodChunkScratch.Clear();
        foreach (ChunkKey key in _chunks.Keys)
            if (key.Step != _displayStep)
                _inactiveLodChunkScratch.Add(key);

        foreach (ChunkKey key in _inactiveLodChunkScratch)
        {
            if (!_chunks.Remove(key, out ChunkEntry? entry)) continue;
            InvalidatePendingBuild(entry);
            entry.Dispose();
            _gpuMeshHashCache.Remove(key);
        }

        _inactiveLodPageScratch.Clear();
        foreach (GpuPageKey key in _gpuPages.Keys)
            if (key.Step != _displayStep)
                _inactiveLodPageScratch.Add(key);

        foreach (GpuPageKey key in _inactiveLodPageScratch)
        {
            if (!_gpuPages.Remove(key, out GpuMeshPage? page)) continue;
            if (page.HasMesh) _totalGpuUnloads++;
            page.Dispose();
        }

        _dirtyGpuPageKeys.RemoveAll(key => key.Step != _displayStep);
        _inactiveLodChunkScratch.Clear();
        _inactiveLodPageScratch.Clear();
    }

    private void CancelPendingBuildsForStep(int step)
    {
        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
            if (key.Step == step && HasPendingWork(entry))
                InvalidatePendingBuild(entry);
    }

    

        

    private void EvictInactiveLodChunks()
    {
        int scanCount = Math.Min(_evictionQueue.Count, CacheEvictionScanBudget);
        for (int i = 0; i < scanCount; i++)
        {
            (ChunkKey key, ChunkEntry queuedEntry) = _evictionQueue.Dequeue();
            if (!_chunks.TryGetValue(key, out ChunkEntry? entry) ||
                !ReferenceEquals(entry, queuedEntry))
                continue;

            bool inactiveLod = key.Step != _displayStep && key.Step != _renderStep;
            bool expired = inactiveLod && entry.LastSeenFrame + 120 < _frame;
            bool cacheOverBudget = inactiveLod && _chunks.Count > MaximumCachedChunks;
            if (expired || cacheOverBudget)
            {
                RemoveCachedChunk(key, cacheOverBudget ? "lod-cache-budget" : "inactive-lod");
                continue;
            }

            _evictionQueue.Enqueue((key, entry));
        }
    }

    private void RemoveCachedChunk(ChunkKey key, string reason)
    {
        if (!_chunks.Remove(key, out ChunkEntry? entry))
            return;

        _gpuMeshHashCache.Remove(key);
        GpuPageKey pageKey = GetGpuPageKey(key);
        if (_gpuPages.TryGetValue(pageKey, out GpuMeshPage? page))
        {
            if (page.CpuMeshes.Remove(key, out VoxelMeshData[]? cached))
                ReturnMeshData(cached);
            if (page.HasMesh && page.Regions.TryGetValue(key, out GpuMeshRegion region) &&
                region.Capacity > 0)
            {
                VoxelChunkMesher.UpdateTriangleSoup(
                    ref page.Mesh, region.Offset, region.Capacity,
                    Array.Empty<VoxelMeshData>());
                page.Regions[key] = region with { VertexCount = 0 };
                GpuUploadsLastFrame++;
                _totalGpuUploads++;
            }
        }

        int residentMeshes = entry.CutCap.Length;
        int partialMeshes = entry.UploadingCutCap.Count;
        InvalidatePendingBuild(entry);
        _totalGpuUnloads += residentMeshes;

        _logger.Write(RenderLogMode.Detailed, "MESH_GPU_EVICT",
            $"reason={reason} chunk=({key.X},{key.Y},{key.Z}) resident={residentMeshes} partial={partialMeshes}");

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
        if (entry.RunningBuild != null || entry.BuildQueued || entry.ReadyBuild.HasValue)
        {
            _totalDuplicateRequestAttempts++;
            return;
        }
        // Queue every visible region before the single batch wake-up. The requests
        // contain no density buffers; workers allocate only their active inputs.
        if (_buildQueue.Count >= MaximumPendingMeshJobs)
        {
            // Count one deferred revision, not every per-frame retry while the queue stays full.
            if (entry.QueueRejectedRevision != entry.RequestRevision)
            {
                entry.QueueRejectedRevision = entry.RequestRevision;
                _totalQueueSaturated++;
            }
            return;
        }

        CancellationTokenSource? cancellation = null;
        try
        {
            IVoxelFieldSource source = GetWorldBuildSnapshot(field);
            cancellation = new CancellationTokenSource();

            BuildRequest request = new()
            {
                Key = key,
                Entry = entry,
                RequestRevision = entry.RequestRevision,
                Generation = ++entry.BuildGeneration,
                PaletteVersion = _paletteVersion,
                BaseVersion = field.BaseVersion,
                BuildSurface = buildSurface,
                BuildCutCap = buildCutCap,
                CutLayer = cutLayer,
                Palette = _palette,
                Source = source,
                Cancellation = cancellation,
                EnqueuedTimestamp = Stopwatch.GetTimestamp()
            };

            entry.QueueRejectedRevision = int.MinValue;
            entry.BuildQueued = true;
            entry.RunningBuild = request;
            _buildQueue.Enqueue(request);
            _totalBuildRequests++;
            if (_logger.IsEnabled(RenderLogMode.Detailed))
                _logger.Write(RenderLogMode.Detailed, "MESH_BUILD_REQUEST",
                    $"chunk=({key.X},{key.Y},{key.Z}) snapshot=world step={key.Step} generation={request.Generation} revision={request.RequestRevision} base={request.BaseVersion} palette={request.PaletteVersion} cut={cutLayer} queued={_buildQueue.Count}");
        }
        catch (Exception exception)
        {
            cancellation?.Dispose();
            entry.FailedRevision = entry.RequestRevision;
            _totalBuildFailed++;
            _logger.Error("MESH_REQUEST_FAILED",
                $"chunk=({key.X},{key.Y},{key.Z}) error={exception}");
        }
    }

    private IVoxelFieldSource GetWorldBuildSnapshot(SparseVoxelField field)
    {
        if (_worldBuildSnapshot == null ||
            !ReferenceEquals(_snapshotField, field) ||
            _worldBuildSnapshotVersion != field.Version ||
            _worldBuildSnapshotBaseVersion != field.BaseVersion)
        {
            long captureStarted = Stopwatch.GetTimestamp();
            _worldBuildSnapshot = field.CreateWorldSnapshot();
            double captureMilliseconds =
                Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds;
            _snapshotField = field;
            _worldBuildSnapshotVersion = field.Version;
            _worldBuildSnapshotBaseVersion = field.BaseVersion;
            _logger.Write(RenderLogMode.FrameSummary, "VOXEL_WORLD_SNAPSHOT",
                $"version={field.Version} base={field.BaseVersion} storageChunks={field.ChunkCount} captureMs={captureMilliseconds:0.000}");
        }

        return _worldBuildSnapshot;
    }

    private void CancelUnseenBuilds()
    {
        long oldestVisibleFrame = _frame - 2;
        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
        {
            if (entry.LastSeenFrame >= oldestVisibleFrame || !HasPendingWork(entry))
                continue;

            InvalidatePendingBuild(entry);
        }
    }

    private void SignalBuildWorkers()
    {
        if (_buildQueue.IsEmpty ||
            Interlocked.CompareExchange(ref _batchWakePending, 1, 0) != 0)
            return;

        Volatile.Write(ref _pendingBatchWakeTokens, _workerTasks.Length);
        for (int i = 0; i < _workerTasks.Length; i++)
            _buildSignal.Release();

        if (_logger.IsEnabled(RenderLogMode.FrameSummary))
            _logger.Write(RenderLogMode.FrameSummary, "MESH_BUILD_BATCH_SIGNAL",
                $"workers={_workerTasks.Length} queued={_buildQueue.Count}");
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            await _buildSignal.WaitAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _workerStopping) != 0)
                break;

            while (_buildQueue.TryDequeue(out BuildRequest? request))
            {
                // Invalidation leaves a lightweight record in ConcurrentQueue.
                // Drop it immediately instead of letting obsolete LOD/view work
                // consume a worker slot or wait behind current visible terrain.
                if (Volatile.Read(ref request.Entry.BuildGeneration) != request.Generation)
                {
                    request.Cancellation.Dispose();
                    Interlocked.Increment(ref _totalBuildCancelled);
                    continue;
                }

                Interlocked.Increment(ref _runningMeshJobs);
                try
                {
                    ProcessBuildRequest(request);
                }
                finally
                {
                    Interlocked.Decrement(ref _runningMeshJobs);
                }
            }

            if (Interlocked.Decrement(ref _pendingBatchWakeTokens) == 0)
            {
                Volatile.Write(ref _batchWakePending, 0);
                if (!_buildQueue.IsEmpty)
                    SignalBuildWorkers();
            }
        }

        if (Interlocked.Decrement(ref _activeWorkers) == 0)
            DrainCompletedBuildQueue();
    }

    private void ProcessBuildRequest(BuildRequest request)
    {
        long buildStarted = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _totalBuildStarted);
        if (_logger.IsEnabled(RenderLogMode.Detailed))
            _logger.Write(RenderLogMode.Detailed, "MESH_BUILD_STARTED",
                $"mode=worker chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) step={request.Key.Step} queuedMs={Stopwatch.GetElapsedTime(request.EnqueuedTimestamp, buildStarted).TotalMilliseconds:0.000}");

        VoxelMeshData[] surface = Array.Empty<VoxelMeshData>();
        VoxelChunkBuildInput? input = null;
        try
        {
            CancellationToken token = request.Cancellation.Token;
            token.ThrowIfCancellationRequested();

            input = new VoxelChunkBuildInput(
                request.Source,
                request.Key.X, request.Key.Y, request.Key.Z,
                request.CutLayer, request.Key.Step, ChunkCells);

            long captureStarted = Stopwatch.GetTimestamp();
            input.CaptureAll(token);
            double captureMs = Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds;

            token.ThrowIfCancellationRequested();
            if (request.BuildSurface && input.HasPotentialSurface)
                surface = VoxelChunkMesher.BuildSurfaceData(
                    input, request.Palette, token);

            token.ThrowIfCancellationRequested();
            int vertices = SumVertices(surface);
            int triangles = SumTriangles(surface);
            bool hasSurface = surface.Length > 0;
            ulong surfaceHash = hasSurface ? ComputeMeshHash(surface) : 0UL;
            double buildMs = Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds;

            VoxelMeshBuildResult result = new(
                surface, Array.Empty<VoxelMeshData>(), hasSurface, false,
                request.BuildSurface && !hasSurface, request.CutLayer,
                buildMs, captureMs, vertices, triangles, surfaceHash, 0UL);

            surface = Array.Empty<VoxelMeshData>();
            _completedBuildQueue.Enqueue(new BuildCompletion(
                request, result, null, false, buildStarted, Stopwatch.GetTimestamp()));

            if (_logger.IsEnabled(RenderLogMode.Detailed))
                _logger.Write(RenderLogMode.Detailed, "MESH_BUILD_COMPLETED",
                    $"mode=worker chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) step={request.Key.Step} ms={buildMs:0.000} captureMs={captureMs:0.000} meshes={result.Surface.Length} vertices={vertices} triangles={triangles} hash={surfaceHash:X16}");
        }
        catch (OperationCanceledException)
        {
            ReturnMeshData(surface);
            _completedBuildQueue.Enqueue(new BuildCompletion(
                request, null, null, true, buildStarted, Stopwatch.GetTimestamp()));
        }
        catch (Exception exception)
        {
            ReturnMeshData(surface);
            _completedBuildQueue.Enqueue(new BuildCompletion(
                request, null, exception, false, buildStarted, Stopwatch.GetTimestamp()));
        }
        finally
        {
            input?.Dispose();
        }
    }

    private void DrainCompletedBuildQueue()
    {
        while (_completedBuildQueue.TryDequeue(out BuildCompletion completion))
        {
            if (completion.Result is VoxelMeshBuildResult result)
                ReturnBuildCpuData(result);
            completion.Request.Cancellation.Dispose();
        }
    }

    private void CollectCompletedBuilds(SparseVoxelField field)
    {
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
                _logger.Write(RenderLogMode.Detailed, "MESH_COMPLETION_CANCELLED", $"chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) generation={request.Generation}");
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
                _logger.Write(RenderLogMode.Detailed, "MESH_RESULT_STALE", $"chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) generation={request.Generation} current={wasCurrentRequest} revision={request.RequestRevision}/{entry.RequestRevision} palette={request.PaletteVersion}/{_paletteVersion}");
                continue;
            }

            if (completion.Error != null)
            {
                _totalBuildFailed++;
                entry.FailedRevision = request.RequestRevision;
                _logger.Error("MESH_FAILED", $"chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) error={completion.Error}");
                continue;
            }

            if (!completion.Result.HasValue)
            {
                _totalBuildFailed++;
                entry.FailedRevision = request.RequestRevision;
                _logger.Error("MESH_FAILED", $"chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) error=missing build result");
                continue;
            }

            VoxelMeshBuildResult build = completion.Result.Value;

            if (entry.ReadyBuild.HasValue)
            {
                ReturnBuildCpuData(build);
                _totalRepeatedBuilds++;
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

            int repeated = _completedBuildCounts.TryGetValue(identity, out int previous)
                ? previous + 1
                : 1;
            _completedBuildCounts[identity] = repeated;

            if (_completedBuildCounts.Count > 4096)
                _completedBuildCounts.Clear();

            if (repeated > 1)
                _totalRepeatedBuilds++;

            _totalBuildCompleted++;
            BuildsCompletedLastFrame++;
            if (_logger.IsEnabled(RenderLogMode.Detailed))
                _logger.Write(RenderLogMode.Detailed, "MESH_RESULT_ACCEPTED", $"chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) generation={request.Generation} buildMs={build.WorkerMilliseconds:0.000} vertices={build.VertexCount} triangles={build.TriangleCount} surfacePackets={build.Surface.Length} cutPackets={build.CutCap.Length}");
            WorkerBuildMillisecondsLastFrame += build.WorkerMilliseconds;
            CpuCaptureMillisecondsLastFrame += build.CaptureMilliseconds;
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

            if (build.SurfaceWasEmpty)
            {
                QueueSurfacePageUpdate(
                    request.Key, entry, Array.Empty<VoxelMeshData>(), 0UL, request);
                DiscardLegacyCutCap(request.Key, entry);
                uploadSurface = false;
                _totalEmptyChunks++;
            }
            else if (uploadSurface &&
                entry.Built &&
                hasGpuHashes &&
                gpuHashes.HasSurface &&
                gpuHashes.SurfaceHash == build.SurfaceHash)
            {
                ReturnMeshData(build.Surface);
                build = build with { Surface = Array.Empty<VoxelMeshData>() };
                entry.BaseVersion = request.BaseVersion;
                entry.PaletteVersion = request.PaletteVersion;
                entry.SurfaceDirty = false;
                entry.CutLayer = request.CutLayer;
                entry.CutDirty = false;
                DiscardLegacyCutCap(request.Key, entry);
                uploadSurface = false;
                _totalIdenticalSurfaceSkips++;
            }
            else if (uploadSurface)
            {
                VoxelMeshData[] data = build.Surface;
                build = build with { Surface = Array.Empty<VoxelMeshData>() };
                QueueSurfacePageUpdate(
                    request.Key, entry, data, build.SurfaceHash, request);
                uploadSurface = false;
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
            }

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
            entry.CutUploadIndex = 0;
            entry.UploadingCutCap.Clear();

            EnqueueReadyUpload(request.Key, entry);
        }
    }

    private void UploadReadyMeshes()
    {
        Stopwatch timer = Stopwatch.StartNew();
        GpuUploadMillisecondsLastFrame = 0d;
        int readyAtFrameStart = _readyUploadQueue.Count;

        for (int visited = 0; visited < readyAtFrameStart; visited++)
        {
            if (!_readyUploadQueue.TryDequeue(out ReadyUpload queued)) break;
            if (!_chunks.TryGetValue(queued.Key, out ChunkEntry? currentEntry) ||
                !ReferenceEquals(currentEntry, queued.Entry) ||
                queued.Generation != queued.Entry.UploadQueueGeneration)
                continue;

            ChunkEntry entry = queued.Entry;
            entry.UploadQueued = false;
            if (!entry.ReadyBuild.HasValue) continue;

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

            try
            {
                while (result.HasCutCap && entry.CutUploadIndex < result.CutCap.Length)
                {
                    VoxelMeshData source = result.CutCap[entry.CutUploadIndex];
                    Mesh mesh = VoxelChunkMesher.UploadOne(source);
                    source.ReturnCpuArrays();
                    entry.CutUploadIndex++;
                    entry.UploadingCutCap.Add(mesh);
                    GpuUploadsLastFrame++;
                    _totalGpuUploads++;
                }

                if (result.HasCutCap)
                {
                    foreach (Mesh oldMesh in entry.CutCap)
                    {
                        UnloadMesh(oldMesh);
                        _totalGpuUnloads++;
                    }
                    entry.CutCap = entry.UploadingCutCap.ToArray();
                    entry.UploadingCutCap.Clear();
                    SetCutCapGpuHash(queued.Key, result.CutCapHash, result.CutLayer);
                    entry.CutLayer = result.CutLayer;
                    entry.CutDirty = false;
                    RebuiltCutCaps++;
                }

                entry.ReadyBuild = null;
                entry.ReadyRequest = null;
                entry.CutUploadIndex = 0;
            }
            catch (Exception exception)
            {
                _totalBuildFailed++;
                entry.FailedRevision = request.RequestRevision;
                _logger.Error("MESH_GPU_UPLOAD_FAILED",
                    $"kind=cut-cap chunk=({queued.Key.X},{queued.Key.Y},{queued.Key.Z}) error={exception}");
                InvalidatePendingBuild(entry);
            }
        }

        GpuUploadMillisecondsLastFrame += timer.Elapsed.TotalMilliseconds;
    }

    private void EnqueueReadyUpload(ChunkKey key, ChunkEntry entry)
    {
        if (entry.UploadQueued)
            return;

        entry.UploadQueued = true;
        long generation = ++entry.UploadQueueGeneration;
        _readyUploadQueue.Enqueue(new ReadyUpload(key, entry, generation));
    }

    private void InvalidatePendingBuild(ChunkEntry entry)
    {
        entry.RunningBuild?.Cancellation.Cancel();
        entry.RunningBuild = null;

        entry.BuildQueued = false;
        entry.BuildGeneration++;
        entry.UploadQueued = false;
        entry.UploadQueueGeneration++;

        if (entry.ReadyBuild is VoxelMeshBuildResult readyBuild)
            ReturnBuildCpuData(readyBuild);

        if (entry.HasPendingSurface) ReturnMeshData(entry.PendingSurface);
        entry.PendingSurface = Array.Empty<VoxelMeshData>();
        entry.HasPendingSurface = false;

        entry.ReadyBuild = null;
        entry.ReadyRequest = null;
        entry.CutUploadIndex = 0;

        foreach (Mesh mesh in entry.UploadingCutCap)
        {
            UnloadMesh(mesh);
            _totalGpuUnloads++;
        }
        entry.UploadingCutCap.Clear();
    }

    private static bool HasPendingWork(ChunkEntry entry) =>
        entry.RunningBuild != null ||
        entry.BuildQueued ||
        entry.HasPendingSurface ||
        entry.ReadyBuild.HasValue ||
        entry.UploadingCutCap.Count > 0;

    private bool IsCurrentMeshReady(ChunkEntry entry, SparseVoxelField field) =>
        entry.Built &&
        !entry.SurfaceDirty &&
        entry.BaseVersion == field.BaseVersion &&
        entry.PaletteVersion == _paletteVersion;

    private void AddVisible(ChunkKey key, ChunkEntry entry)
    {
        if (!entry.Built)
            return;

        entry.LastSeenFrame = _frame;
        _visible.Add((key, entry));
    }

    private bool AreCachedMeshesCurrent(
        SparseVoxelField.VoxelBounds bounds,
        SparseVoxelField field)
    {
        foreach (ChunkKey key in _visibleCandidateKeys)
        {
            int chunkSpan = ChunkCells * key.Step;
            int minX = key.X * chunkSpan;
            int minY = key.Y * chunkSpan;
            int minZ = key.Z * chunkSpan;
            int maxX = minX + chunkSpan;
            int maxY = minY + chunkSpan;
            int maxZ = minZ + chunkSpan;
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

    private void SetLighting()
    {
        Vector3 direction = _palette.GetSunDirection();
        SetShaderValue(
            _shader, _sunDirectionLocation,
            direction, ShaderUniformDataType.Vec3);
        SetShaderValue(
            _shader, _ambientLightLocation,
            _palette.AmbientLight, ShaderUniformDataType.Float);
        SetShaderValue(
            _shader, _sunIntensityLocation,
            _palette.SunIntensity, ShaderUniformDataType.Float);
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

    private void SetLodShader(int role, float fade)
    {
        SetShaderValue(
            _shader, _lodRoleLocation,
            role, ShaderUniformDataType.Int);
        SetShaderValue(
            _shader, _lodFadeLocation,
            fade, ShaderUniformDataType.Float);
    }

    private void DiscardLegacyCutCap(ChunkKey key, ChunkEntry entry)
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
            int chunkSpan = ChunkCells * key.Step;
            int halo = key.Step;
            int firstX = key.X * chunkSpan - halo;
            int firstY = key.Y * chunkSpan - halo;
            int firstZ = key.Z * chunkSpan - halo;
            int lastX = (key.X + 1) * chunkSpan + halo;
            int lastY = (key.Y + 1) * chunkSpan + halo;
            int lastZ = (key.Z + 1) * chunkSpan + halo;
            bool intersectsX = Intersects(firstX, lastX, bounds.MinX, bounds.MaxX);
            bool intersectsY = Intersects(firstY, lastY, bounds.MinY, bounds.MaxY);
            bool intersectsZ = Intersects(firstZ, lastZ, bounds.MinZ, bounds.MaxZ);
            bool surfaceChanged = intersectsX && intersectsY && intersectsZ;
            bool cutChanged = false;
            if (entry.CutLayer != int.MinValue && intersectsX && intersectsZ)
            {
                int cutSample = entry.CutLayer - 1;
                cutChanged = bounds.MinY <= cutSample + halo && bounds.MaxY > cutSample - halo;
            }
            if (!surfaceChanged && !cutChanged)
                continue;
            if (surfaceChanged) entry.SurfaceDirty = true;
            if (cutChanged) entry.CutDirty = true;
            entry.RequestRevision++;
            entry.FailedRevision = int.MinValue;
            InvalidatePendingBuild(entry);
        }
    }

    private static bool Intersects(int minA, int maxA, int minB, int maxB) =>
        minA < maxB && minB < maxA;

    private static int DivideRoundUp(int value, int divisor) =>
        (value + divisor - 1) / divisor;

    private static int SumVertices(VoxelMeshData[] meshes)
    {
        int total = 0;
        foreach (VoxelMeshData mesh in meshes)
            total += mesh.VertexCount;
        return total;
    }

    private static int SumTriangles(VoxelMeshData[] meshes)
    {
        int total = 0;
        foreach (VoxelMeshData mesh in meshes)
            total += mesh.TriangleCount;
        return total;
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

            foreach (Vector3 vertex in mesh.Vertices)
            {
                AddHashUInt(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(vertex.X)));
                AddHashUInt(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(vertex.Y)));
                AddHashUInt(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(vertex.Z)));
            }

            foreach (Vector3 normal in mesh.Normals)
            {
                AddHashUInt(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(normal.X)));
                AddHashUInt(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(normal.Y)));
                AddHashUInt(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(normal.Z)));
            }

            foreach (byte color in mesh.Colors)
                AddHashByte(ref hash, color);

            foreach (ushort index in mesh.Indices)
                AddHashUInt(ref hash, index);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_observedField != null) _observedField.Changed -= OnVoxelFieldChanged;
        _observedField = null;

        foreach (ChunkEntry entry in _chunks.Values)
        {
            InvalidatePendingBuild(entry);
            if (entry.ReadyBuild is VoxelMeshBuildResult readyBuild)
                ReturnBuildCpuData(readyBuild);
            entry.Dispose();
        }

        foreach (GpuMeshPage page in _gpuPages.Values)
        {
            if (page.HasMesh) _totalGpuUnloads++;
            page.Dispose();
        }

        Interlocked.Exchange(ref _workerStopping, 1);
        for (int i = 0; i < _workerTasks.Length; i++) _buildSignal.Release();
        while (_buildQueue.TryDequeue(out BuildRequest? pending))
        {
            pending.Cancellation.Cancel();
            pending.Cancellation.Dispose();
        }

        DrainCompletedBuildQueue();
        _dynamicFragments.Dispose();

        _chunks.Clear();
        _gpuPages.Clear();
        _worldBuildSnapshot = null;
        _snapshotField = null;
        _gpuMeshHashCache.Clear();
        _readyUploadQueue.Clear();
        _dirtyGpuPageKeys.Clear();
        _evictionQueue.Clear();
        _transitionCandidateScratch.Clear();
        _transitionEntriesScratch.Clear();
        _transitionCandidateKeys.Clear();
        _transitionVisible.Clear();
        _visiblePages.Clear();
        _transitionVisiblePages.Clear();
        _visiblePageKeys.Clear();
        _transitionVisiblePageKeys.Clear();
        _worldRegionScratch.Clear();
        _inactiveLodChunkScratch.Clear();
        _inactiveLodPageScratch.Clear();

        UnloadMaterial(_material);
        _logger.Write(RenderLogMode.ErrorsOnly, "VOXEL_RENDERER_STOP",
            $"requests={_totalBuildRequests} started={_totalBuildStarted} completed={_totalBuildCompleted} cancelled={_totalBuildCancelled} failed={_totalBuildFailed} stale={_totalStaleResults} gpuUploads={_totalGpuUploads} gpuUnloads={_totalGpuUnloads} cache={_chunks.Count}");
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
