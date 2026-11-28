using System;
using System.Buffers;
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
    // GPU pages keep indexed vertices. Each buffer stays within ushort addressing.
    private const int MaximumWorldMeshVertices = 48_000_000;
    private const int MaximumTotalGpuPageVertices = 64_000_000;
    private const int MaximumWorldMeshIndices = 72_000_000;
    private const int MaximumTotalGpuPageIndices = 96_000_000;
    private const int GpuPageBufferVertices = VoxelChunkMesher.MaximumVerticesPerMesh;
    private const int GpuPageBufferIndices = VoxelChunkMesher.MaximumIndicesPerMesh;
    private const int FallbackLodStep = MaximumRenderStep;
    // Use the available physical cores for density capture and Surface Nets builds.
    private static int WorkerCount => Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
    private const double GpuUploadBudgetMilliseconds = 3.0;
    private const int CutCapSettleFrames = 8;
    private const int MaximumRenderStep = 64;
    private const int BaseLodStep = 16;
    private const int MaximumAdaptiveNodes = 8192;
    private const int CacheEvictionScanBudget = 4096;
    private const int MaximumCachedChunks = 4096;
    private const float LodFadeDurationSeconds = 0.25f;
    private const double LodTargetSettleMilliseconds = 750d;
    private const double LodMinimumResidenceMilliseconds = 1100d;

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
        uniform sampler2D uCutMask;
        uniform int uCutOverlayPass;
        uniform vec2 uCutWorldSize;
        uniform vec4 uCutColor;
        uniform vec3 uSunDirection;
        uniform float uAmbientLight;
        uniform float uSunIntensity;
        uniform float uLodFade;
        uniform int uLodRole;
        
        out vec4 finalColor;
        void main()
        {
            if (uCutOverlayPass != 0)
            {
                vec2 uv = fragWorldPosition.xz / uCutWorldSize;
                if (any(lessThan(uv, vec2(0.0))) ||
                    any(greaterThan(uv, vec2(1.0))) ||
                    texture(uCutMask, uv).r < 0.5)
                    discard;

                finalColor = uCutColor;
                return;
            }

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
        int VertexOffset,
        int VertexCapacity,
        int IndexOffset,
        int IndexCapacity,
        int VertexCount,
        int IndexCount);
    private readonly record struct GpuFreeRange(int Offset, int Capacity);
    private readonly record struct GpuSourceGroup(
        int FirstSource,
        int SourceCount,
        int VertexCount,
        int IndexCount);
    private readonly record struct GpuDrawRange(
        GpuMeshBuffer Buffer,
        int Step,
        int IndexOffset,
        int IndexCount);
    private readonly record struct CutMaskBuildResult(
        long Generation,
        int Layer,
        int Width,
        int Depth,
        long FieldVersion,
        byte[] Pixels);

    private sealed class GpuMeshBuffer : IDisposable
    {
        public Mesh Mesh;
        public readonly int VertexCapacity;
        public readonly int IndexCapacity;
        public readonly List<GpuFreeRange> FreeVertexRanges = new(4);
        public readonly List<GpuFreeRange> FreeIndexRanges = new(4);
        public int ReservedVertexCount;
        public int ReservedIndexCount;

        public GpuMeshBuffer(Mesh mesh, int vertexCapacity, int indexCapacity)
        {
            Mesh = mesh;
            VertexCapacity = vertexCapacity;
            IndexCapacity = indexCapacity;
        }

        public void Dispose()
        {
            if (Mesh.VaoId != 0)
                UnloadMesh(Mesh);
            Mesh = default;
            ReservedVertexCount = 0;
            ReservedIndexCount = 0;
            FreeVertexRanges.Clear();
            FreeIndexRanges.Clear();
        }
    }

    // A page owns compact indexed buffers. Regions retain their own vertex
    // and index ranges, so visible regions can be drawn without expanding indices.
    private sealed class GpuMeshPage : IDisposable
    {
        public readonly List<GpuMeshBuffer> Buffers = new(4);
        public readonly Dictionary<ChunkKey, GpuMeshRegion[]> Regions = new();
        public int ReservedVertexCount;
        public int AllocatedVertexCount;
        public int ReservedIndexCount;
        public int AllocatedIndexCount;
        public long CapacityVersion;
        public bool Dirty;
        public bool HasMesh => Buffers.Count > 0;

        public void Dispose()
        {
            foreach (GpuMeshBuffer buffer in Buffers)
                buffer.Dispose();
            Buffers.Clear();
            Regions.Clear();
            ReservedVertexCount = 0;
            AllocatedVertexCount = 0;
            ReservedIndexCount = 0;
            AllocatedIndexCount = 0;
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
        public long GpuUploadRetryFrame;
        public long GpuUploadRetryVersion = long.MinValue;

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
    private readonly List<GpuDrawRange> _visibleGpuRanges = new(1024);
    private readonly List<GpuSourceGroup> _gpuSourceGroupsScratch = new(16);
    private readonly List<ChunkKey> _inactiveLodChunkScratch = new();
    private readonly List<Candidate> _coarseCandidateScratch = new();
    private readonly List<(Candidate Candidate, ChunkEntry Entry)> _coarseCandidateEntriesScratch = new();
    private readonly List<(GpuPageKey Key, GpuMeshPage Page)> _coarseVisiblePages = new();
    private readonly HashSet<ChunkKey> _coarseCandidateKeys = new();
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
    private readonly List<Candidate> _adaptiveNodeScratch = new(1024);
    private readonly List<Candidate> _baseCandidateScratch = new();
    private readonly List<(Candidate Candidate, ChunkEntry Entry)> _baseCandidateEntriesScratch = new();
    private readonly HashSet<ChunkKey> _baseCandidateKeys = new();
    private readonly HashSet<ChunkKey> _splitChunkKeys = new();
    private readonly HashSet<ChunkKey> _nextSplitChunkKeys = new();
    private readonly HashSet<ChunkKey> _visibleChunkKeys = new();
    private readonly Dictionary<ChunkKey, int> _baseCandidateIndices = new(256);
    private long[] _detailCoverageVolumes = new long[256];
    private bool[] _detailCoverageReady = new bool[256];
    private readonly List<(Candidate Candidate, ChunkEntry Entry)> _candidateEntriesScratch = new();
    private readonly List<Candidate> _transitionCandidateScratch = new();
    private readonly List<(Candidate Candidate, ChunkEntry Entry)> _transitionEntriesScratch = new();
    private readonly HashSet<ChunkKey> _transitionCandidateKeys = new();
    private readonly Queue<(ChunkKey Key, ChunkEntry Entry)> _evictionQueue = new();
    private readonly List<(ChunkKey Key, ChunkEntry Entry)> _visible = new();
    private readonly List<(ChunkKey Key, ChunkEntry Entry)> _transitionVisible = new();
    private readonly HashSet<ChunkKey> _visibleCandidateKeys = new();
    private int _displayStep;
    private int _minimumVisibleStep = BaseLodStep;
    private long _fallbackRetiredBaseVersion = long.MinValue;
    private int _previousDisplayStep;
    private long _lastLodSwitchTimestamp;
    private bool _lodTransitionActive;
    private float _lodFade;

    private readonly DynamicVoxelFragmentRenderer _dynamicFragments;
    private readonly TerrainPipelineLogger _logger;
    private readonly Dictionary<BuildIdentity, int> _completedBuildCounts = new();

    private readonly Material _material;
    private readonly Shader _shader;
    private readonly int _cutEnabledLocation;
    private readonly int _cutHeightLocation;
    private readonly int _cutMaskLocation;
    private readonly int _cutOverlayPassLocation;
    private readonly int _cutWorldSizeLocation;
    private readonly int _cutColorLocation;
    private readonly int _sunDirectionLocation;
    private readonly int _ambientLightLocation;
    private readonly int _sunIntensityLocation;
    private readonly int _lodFadeLocation;
    private readonly int _lodRoleLocation;
    private readonly int _mvpLocation;
    private readonly int _modelLocation;

    private TerrainPalette _palette;
    private SparseVoxelField? _observedField;
    private int _paletteVersion;
    private long _frame;
    private bool _disposed;

    private int _requestedCutLayer = int.MinValue;
    private int _stableCutLayer = int.MinValue;
    private int _cutLayerStableFrames;
    private int _cutMaskRequestedLayer = int.MinValue;
    private int _cutMaskReadyLayer = int.MinValue;
    private int _activeCutLayer = int.MinValue;
    private int _cutMaskDirty;
    private int _cutMaskWidth;
    private int _cutMaskDepth;
    private long _cutMaskGeneration;
    private CancellationTokenSource? _cutMaskCancellation;
    private readonly ConcurrentQueue<CutMaskBuildResult> _cutMaskCompletions = new();
    private Texture2D _cutMaskTexture;
    private Mesh _cutPlaneMesh;
    private float _cutPlaneWorldWidth;
    private float _cutPlaneWorldDepth;

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
    private long _totalGpuUploadFailures;
    private long _gpuCapacityVersion;
    private long _totalIdenticalSurfaceSkips;
    private long _totalIdenticalCutSkips;
    private long _totalEmptyChunks;

    public int ActiveRenderCellSize => _minimumVisibleStep;
    public float ActiveVoxelSizeMeters => SparseVoxelField.VoxelSizeMeters * _minimumVisibleStep;
    public float RenderChunkSizeMeters =>
        ChunkCells * _minimumVisibleStep * SparseVoxelField.VoxelSizeMeters;
    public bool IsUsingCoarseLod => _minimumVisibleStep > 1;
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
        _cutMaskLocation = GetShaderLocation(_shader, "uCutMask");
        _cutOverlayPassLocation = GetShaderLocation(_shader, "uCutOverlayPass");
        _cutWorldSizeLocation = GetShaderLocation(_shader, "uCutWorldSize");
        _cutColorLocation = GetShaderLocation(_shader, "uCutColor");
        _sunDirectionLocation = GetShaderLocation(_shader, "uSunDirection");
        _ambientLightLocation = GetShaderLocation(_shader, "uAmbientLight");
        _sunIntensityLocation = GetShaderLocation(_shader, "uSunIntensity");
        _lodFadeLocation = GetShaderLocation(_shader, "uLodFade");
        _lodRoleLocation = GetShaderLocation(_shader, "uLodRole");
        _mvpLocation = GetShaderLocation(_shader, "mvp");
        _modelLocation = GetShaderLocation(_shader, "matModel");

        if (_cutEnabledLocation < 0 ||
            _cutHeightLocation < 0 ||
            _cutMaskLocation < 0 ||
            _cutOverlayPassLocation < 0 ||
            _cutWorldSizeLocation < 0 ||
            _cutColorLocation < 0 ||
            _sunDirectionLocation < 0 ||
            _ambientLightLocation < 0 ||
            _sunIntensityLocation < 0 ||
            _lodFadeLocation < 0 ||
            _lodRoleLocation < 0 ||
            _mvpLocation < 0 ||
            _modelLocation < 0)
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
            $"resolution=0.1m;lod=per-chunk;fallbackStep={FallbackLodStep};baseStep={BaseLodStep};detailSteps=8,4,2,1;gpuMesh=one-page-per-step;cameraCulling=false;chunkCells={ChunkCells};workers={workerCount};build=background-worker-pool;pendingLimit={MaximumPendingMeshJobs};mesher=surface-nets-density");
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

        _displayStep = BaseLodStep;
        _renderStep = BaseLodStep;
        _lodTransitionActive = false;
        _lodFade = 0f;

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
        _minimumVisibleStep = BaseLodStep;

        CollectCompletedBuilds(field);

        int cutLayer = cutEnabled
            ? Math.Clamp(
                (int)MathF.Round(cutHeightMeters / SparseVoxelField.VoxelSizeMeters),
                0, field.Height)
            : -1;
        UpdateStableCutLayer(cutEnabled, cutLayer);
        int stableCutLayer = cutEnabled &&
            _cutLayerStableFrames >= CutCapSettleFrames
                ? _stableCutLayer
                : -1;

        if (stableCutLayer >= 0 &&
            (stableCutLayer != _cutMaskRequestedLayer ||
             Interlocked.Exchange(ref _cutMaskDirty, 0) != 0))
            RequestCutMaskBuild(field, stableCutLayer);

        ProcessCutMaskCompletions(field);
        bool activeCut = cutEnabled &&
            _activeCutLayer >= 0 &&
            _cutMaskTexture.Id != 0;
        float activeCutHeightMeters = activeCut
            ? _activeCutLayer * SparseVoxelField.VoxelSizeMeters
            : 0f;

        int frustumTests = 0;
        int macroFrustumTests = 0;
        int visibleMacroRegions = 0;

        // Step 64 is the complete safety layer; step 16 is the regular world base.
        _coarseCandidateScratch.Clear();
        _coarseCandidateEntriesScratch.Clear();
        _coarseCandidateKeys.Clear();
        if (_fallbackRetiredBaseVersion != field.BaseVersion)
        {
            CollectVisibleCandidates(
                field, camera, FallbackLodStep,
                _coarseCandidateScratch,
                ref frustumTests, ref macroFrustumTests, ref visibleMacroRegions);
            _coarseCandidateScratch.Sort(static (a, b) =>
                a.DistanceSquared.CompareTo(b.DistanceSquared));
            PrepareCandidateEntries(
                field, _coarseCandidateScratch,
                _coarseCandidateEntriesScratch, _coarseCandidateKeys);
        }

        _baseCandidateScratch.Clear();
        CollectVisibleCandidates(
            field, camera, BaseLodStep,
            _baseCandidateScratch,
            ref frustumTests, ref macroFrustumTests, ref visibleMacroRegions);
        _baseCandidateScratch.Sort(static (a, b) =>
            a.DistanceSquared.CompareTo(b.DistanceSquared));

        _candidateScratch.Clear();
        CollectAdaptiveCandidates(
            field, camera, viewportHeight,
            _baseCandidateScratch, _candidateScratch);

        // The stable base enters the worker queue before regional detail.
        PrepareCandidateEntries(
            field, _baseCandidateScratch,
            _baseCandidateEntriesScratch, _baseCandidateKeys);
        PrepareCandidateEntries(
            field, _candidateScratch,
            _candidateEntriesScratch, _visibleCandidateKeys);
        _visibleCandidateKeys.UnionWith(_baseCandidateKeys);
        _visibleCandidateKeys.UnionWith(_coarseCandidateKeys);

        CancelUnseenBuilds();
        SignalBuildWorkers();

        FrustumCandidatesLastFrame = frustumTests;
        MacroFrustumTestsLastFrame = macroFrustumTests;
        VisibleMacroRegionsLastFrame = visibleMacroRegions;
        CandidateOverflowLastFrame = Math.Max(0, _adaptiveNodeScratch.Count - MaximumAdaptiveNodes);

        Step1ChunksLastFrame = 0;
        Step2ChunksLastFrame = 0;
        Step4ChunksLastFrame = 0;
        Step8ChunksLastFrame = 0;
        Step16ChunksLastFrame = _baseCandidateScratch.Count;
        Step32ChunksLastFrame = 0;
        Step64ChunksLastFrame = _coarseCandidateScratch.Count;
        foreach (Candidate candidate in _candidateScratch)
        {
            switch (candidate.Key.Step)
            {
                case 1: Step1ChunksLastFrame++; break;
                case 2: Step2ChunksLastFrame++; break;
                case 4: Step4ChunksLastFrame++; break;
                case 8: Step8ChunksLastFrame++; break;
            }
        }

        EvictInactiveLodChunks();
        CollectCompletedBuilds(field);
        UploadReadyMeshes();
        RetryPendingGpuUploads();
        UploadDirtyGpuPages();

        bool baseCoverageReady = HasCompleteCachedLod(
            _baseCandidateEntriesScratch, field, BaseLodStep, out int baseReadyCount);
        bool basePageReady = baseCoverageReady &&
            _gpuPages.TryGetValue(new GpuPageKey(BaseLodStep), out GpuMeshPage? basePage) &&
            basePage.HasMesh && !basePage.Dirty;
        if (basePageReady && _fallbackRetiredBaseVersion != field.BaseVersion)
        {
            RetireFallbackLod();
            _fallbackRetiredBaseVersion = field.BaseVersion;
        }

        int coarseReadyCount = 0;
        bool coarseCoverageReady = !basePageReady && HasCompleteCachedLod(
            _coarseCandidateEntriesScratch, field, FallbackLodStep, out coarseReadyCount);

        _visible.Clear();
        _visibleChunkKeys.Clear();
        _baseCandidateIndices.Clear();

        int baseCount = _baseCandidateEntriesScratch.Count;
        if (_detailCoverageVolumes.Length < baseCount)
        {
            int capacity = Math.Max(baseCount, _detailCoverageVolumes.Length * 2);
            Array.Resize(ref _detailCoverageVolumes, capacity);
            Array.Resize(ref _detailCoverageReady, capacity);
        }

        for (int i = 0; i < baseCount; i++)
        {
            ChunkKey parentKey = _baseCandidateEntriesScratch[i].Candidate.Key;
            _baseCandidateIndices[parentKey] = i;
            _detailCoverageVolumes[i] = 0;
            _detailCoverageReady[i] = true;
        }

        // Adaptive candidates are non-overlapping leaves. A parent is replaced
        // only after every unit of its volume is covered by current child meshes.
        foreach ((Candidate candidate, ChunkEntry entry) in _candidateEntriesScratch)
        {
            ChunkKey key = candidate.Key;
            if (key.Step >= BaseLodStep)
                continue;

            ChunkKey parentKey = GetBaseAncestor(key);
            if (!_baseCandidateIndices.TryGetValue(parentKey, out int parentIndex))
                continue;

            _detailCoverageVolumes[parentIndex] += GetChunkVolume(field, key);
            if (!IsCurrentMeshReady(entry, field) || !HasCachedSurface(key))
                _detailCoverageReady[parentIndex] = false;
        }

        for (int i = 0; i < baseCount; i++)
        {
            (Candidate candidate, ChunkEntry entry) = _baseCandidateEntriesScratch[i];
            long expectedVolume = GetChunkVolume(field, candidate.Key);
            bool childrenReady = expectedVolume > 0 &&
                _detailCoverageVolumes[i] == expectedVolume &&
                _detailCoverageReady[i];

            if (childrenReady)
                continue;

            // Keep the parent as the sole visible representation while any
            // child is missing, stale, queued, or awaiting GPU upload.
            if (!entry.Built || !HasCachedSurface(candidate.Key))
                continue;

            _visibleChunkKeys.Add(candidate.Key);
            AddVisible(candidate.Key, entry);
            _minimumVisibleStep = Math.Min(_minimumVisibleStep, candidate.Key.Step);
        }

        foreach ((Candidate candidate, ChunkEntry entry) in _candidateEntriesScratch)
        {
            ChunkKey key = candidate.Key;
            if (key.Step >= BaseLodStep ||
                !_baseCandidateIndices.TryGetValue(GetBaseAncestor(key), out int parentIndex))
                continue;

            long expectedVolume = GetChunkVolume(
                field, _baseCandidateEntriesScratch[parentIndex].Candidate.Key);
            if (_detailCoverageVolumes[parentIndex] != expectedVolume ||
                !_detailCoverageReady[parentIndex] ||
                !HasCachedSurface(key))
                continue;

            _visibleChunkKeys.Add(key);
            AddVisible(key, entry);
            _minimumVisibleStep = Math.Min(_minimumVisibleStep, key.Step);
        }

        double remainingUploadBudget = Math.Max(
            0d, GpuUploadBudgetMilliseconds - GpuUploadMillisecondsLastFrame);
        if (remainingUploadBudget > 0d)
            GpuUploadMillisecondsLastFrame +=
                _dynamicFragments.UploadPendingMeshes(remainingUploadBudget);

        _dynamicFragments.ReleaseBakedBodies(
            bounds => AreCachedMeshesCurrent(bounds, field));

        if ((_frame % 600) == 0)
        {
            GetGpuVertexUsage(
                out long gpuPhysicalVertices,
                out long gpuLiveVertices,
                out long gpuPhysicalIndices,
                out long gpuLiveIndices);
            string summary =
                $"frame={_frame} base={BaseLodStep}:{baseReadyCount}/{_baseCandidateScratch.Count} " +
                $"detail=1:{Step1ChunksLastFrame},2:{Step2ChunksLastFrame},4:{Step4ChunksLastFrame},8:{Step8ChunksLastFrame} " +
                $"visible={_visible.Count} fallback64={coarseReadyCount}/{_coarseCandidateScratch.Count} " +
                $"gpuV={gpuLiveVertices / 1_000_000d:0.0}/{gpuPhysicalVertices / 1_000_000d:0.0}M " +
                $"gpuI={gpuLiveIndices / 1_000_000d:0.0}/{gpuPhysicalIndices / 1_000_000d:0.0}M " +
                $"builds={_totalBuildCompleted}/{_totalBuildRequests} pending={_buildQueue.Count} " +
                $"uploadFail={_totalGpuUploadFailures}";
            _logger.Write("FRAME_SUMMARY", summary);
            Console.WriteLine($"[TERRAIN][PERF] {summary}");
        }

        float renderCutHeightMeters = activeCutHeightMeters;

        float worldRadius = 0.5f * MathF.Sqrt(
            field.Width * field.Width * SparseVoxelField.VoxelSizeMeters * SparseVoxelField.VoxelSizeMeters +
            field.Depth * field.Depth * SparseVoxelField.VoxelSizeMeters * SparseVoxelField.VoxelSizeMeters +
            field.Height * field.Height * SparseVoxelField.VoxelSizeMeters * SparseVoxelField.VoxelSizeMeters);
        float distanceToOrigin = camera.Position.Length();
        float cameraDistance = Vector3.Distance(camera.Position, camera.Target);
        float nearPlane = Math.Clamp(cameraDistance * 0.002f, 0.02f, 2f);
        Rlgl.SetClipPlanes(
            nearPlane,
            MathF.Max(1000f, distanceToOrigin + worldRadius + 100f));

        _visibleGpuRanges.Clear();
        bool drawCoarseFallback = coarseCoverageReady && !basePageReady;
        if (drawCoarseFallback)
        {
            foreach ((Candidate candidate, ChunkEntry entry) in _coarseCandidateEntriesScratch)
                AddVisibleGpuRanges(candidate.Key, entry);
        }

        if (coarseCoverageReady || basePageReady)
        {
            foreach ((ChunkKey key, ChunkEntry entry) in _visible)
            {
                if (key.Step == FallbackLodStep ||
                    (key.Step == BaseLodStep && !basePageReady))
                    continue;
                AddVisibleGpuRanges(key, entry);
            }
        }

        BeginMode3D(camera);
        BeginShaderMode(_shader);
        Matrix4x4 view = Rlgl.GetMatrixModelview();
        Matrix4x4 projection = Rlgl.GetMatrixProjection();
        SetShaderValueMatrix(
            _shader,
            _mvpLocation,
            Raymath.MatrixMultiply(view, projection));
        SetShaderValueMatrix(_shader, _modelLocation, Matrix4x4.Identity);
        SetLighting();
        SetCutShader(activeCut, renderCutHeightMeters);
        SetLodShader(-1, 0f);

        int draws = DrawVisibleGpuRanges();
        EndShaderMode();

        if (activeCut)
        {
            DrawCutOverlay(field, renderCutHeightMeters);
            draws++;
        }

        _dynamicFragments.Draw(_material);
        EndMode3D();

        DrawCallsLastFrame = draws;
        VisibleChunksLastFrame = _visible.Count +
            (drawCoarseFallback ? _coarseCandidateScratch.Count : 0);

        if (_logger.IsEnabled(RenderLogMode.FrameSummary))
            _logger.Write(RenderLogMode.FrameSummary, "VOXEL_FRAME",
                $"elapsedMs={Stopwatch.GetElapsedTime(voxelFrameStarted).TotalMilliseconds:0.000} " +
                $"base={BaseLodStep}:{baseReadyCount}/{_baseCandidateScratch.Count} " +
                $"lod=1:{Step1ChunksLastFrame},2:{Step2ChunksLastFrame},4:{Step4ChunksLastFrame},8:{Step8ChunksLastFrame},16:{Step16ChunksLastFrame} " +
                $"visible={VisibleChunksLastFrame} draws={DrawCallsLastFrame} cache={_chunks.Count} " +
                $"rebuilt={RebuiltChunks} uploads={GpuUploadsLastFrame} buildQueue={PendingMeshJobs} " +
                $"captureMs={CpuCaptureMillisecondsLastFrame:0.00} gpuMs={GpuUploadMillisecondsLastFrame:0.00}");
    }

    private void CollectAdaptiveCandidates(
        SparseVoxelField field,
        Camera3D camera,
        int viewportHeight,
        List<Candidate> roots,
        List<Candidate> leaves)
    {
        leaves.Clear();
        _adaptiveNodeScratch.Clear();
        _nextSplitChunkKeys.Clear();
        foreach (Candidate root in roots)
            _adaptiveNodeScratch.Add(root);

        for (int nodeIndex = 0; nodeIndex < _adaptiveNodeScratch.Count; nodeIndex++)
        {
            Candidate node = _adaptiveNodeScratch[nodeIndex];
            ChunkKey key = node.Key;
            if (key.Step <= 1)
            {
                if (key.Step < BaseLodStep) leaves.Add(node);
                continue;
            }

            float distance = DistanceToChunkBounds(camera.Position, field, key);
            float projectedVoxel = GetPixelsPerVoxel(camera, viewportHeight, distance) * key.Step;
            bool wasSplit = _splitChunkKeys.Contains(key);
            bool split = projectedVoxel > (wasSplit ? 0.62f : 0.95f);
            if (!split)
            {
                if (key.Step < BaseLodStep) leaves.Add(node);
                continue;
            }

            int childStep = key.Step >> 1;
            int childSpan = ChunkCells * childStep;
            int firstChildX = key.X * 2;
            int firstChildY = key.Y * 2;
            int firstChildZ = key.Z * 2;
            int childCount = 0;
            for (int dy = 0; dy < 2; dy++)
            for (int dz = 0; dz < 2; dz++)
            for (int dx = 0; dx < 2; dx++)
            {
                int x = firstChildX + dx;
                int y = firstChildY + dy;
                int z = firstChildZ + dz;
                if (x * childSpan < field.Width &&
                    y * childSpan < field.Height &&
                    z * childSpan < field.Depth)
                    childCount++;
            }

            if (_adaptiveNodeScratch.Count + childCount > MaximumAdaptiveNodes)
            {
                if (key.Step < BaseLodStep) leaves.Add(node);
                continue;
            }

            _nextSplitChunkKeys.Add(key);
            for (int dy = 0; dy < 2; dy++)
            for (int dz = 0; dz < 2; dz++)
            for (int dx = 0; dx < 2; dx++)
            {
                ChunkKey child = new(
                    firstChildX + dx,
                    firstChildY + dy,
                    firstChildZ + dz,
                    childStep);
                if (child.X * childSpan >= field.Width ||
                    child.Y * childSpan >= field.Height ||
                    child.Z * childSpan >= field.Depth)
                    continue;

                _adaptiveNodeScratch.Add(CreateCandidate(field, child, camera.Position));
            }
        }

        _splitChunkKeys.Clear();
        foreach (ChunkKey key in _nextSplitChunkKeys)
            _splitChunkKeys.Add(key);

        leaves.Sort(static (a, b) => a.DistanceSquared.CompareTo(b.DistanceSquared));
    }

    private static ChunkKey GetBaseAncestor(ChunkKey key)
    {
        int scale = BaseLodStep / key.Step;
        return new ChunkKey(
            key.X / scale,
            key.Y / scale,
            key.Z / scale,
            BaseLodStep);
    }

    private static long GetChunkVolume(SparseVoxelField field, ChunkKey key)
    {
        int span = ChunkCells * key.Step;
        int originX = key.X * span;
        int originY = key.Y * span;
        int originZ = key.Z * span;
        int sizeX = Math.Clamp(field.Width - originX, 0, span);
        int sizeY = Math.Clamp(field.Height - originY, 0, span);
        int sizeZ = Math.Clamp(field.Depth - originZ, 0, span);
        return (long)sizeX * sizeY * sizeZ;
    }

    private static Candidate CreateCandidate(
        SparseVoxelField field,
        ChunkKey key,
        Vector3 cameraPosition)
    {
        int span = ChunkCells * key.Step;
        int originX = key.X * span;
        int originY = key.Y * span;
        int originZ = key.Z * span;
        int sizeX = Math.Min(span, field.Width - originX);
        int sizeY = Math.Min(span, field.Height - originY);
        int sizeZ = Math.Min(span, field.Depth - originZ);
        float voxelSize = SparseVoxelField.VoxelSizeMeters;
        Vector3 center = new(
            (originX + sizeX * 0.5f) * voxelSize,
            (originY + sizeY * 0.5f) * voxelSize,
            (originZ + sizeZ * 0.5f) * voxelSize);
        float halfX = sizeX * voxelSize * 0.5f;
        float halfY = sizeY * voxelSize * 0.5f;
        float halfZ = sizeZ * voxelSize * 0.5f;
        float radius = MathF.Sqrt(halfX * halfX + halfY * halfY + halfZ * halfZ);
        return new Candidate(key, center, radius, Vector3.DistanceSquared(center, cameraPosition));
    }

    private static float DistanceToChunkBounds(
        Vector3 point,
        SparseVoxelField field,
        ChunkKey key)
    {
        int span = ChunkCells * key.Step;
        float voxelSize = SparseVoxelField.VoxelSizeMeters;
        float minX = key.X * span * voxelSize;
        float minY = key.Y * span * voxelSize;
        float minZ = key.Z * span * voxelSize;
        float maxX = Math.Min((key.X + 1) * span, field.Width) * voxelSize;
        float maxY = Math.Min((key.Y + 1) * span, field.Height) * voxelSize;
        float maxZ = Math.Min((key.Z + 1) * span, field.Depth) * voxelSize;
        float dx = MathF.Max(MathF.Max(minX - point.X, 0f), point.X - maxX);
        float dy = MathF.Max(MathF.Max(minY - point.Y, 0f), point.Y - maxY);
        float dz = MathF.Max(MathF.Max(minZ - point.Z, 0f), point.Z - maxZ);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static float GetPixelsPerVoxel(Camera3D camera, int viewportHeight, float distance)
    {
        float fovy = float.IsFinite(camera.FovY)
            ? Math.Clamp(camera.FovY, 1f, 175f)
            : 45f;
        float pixelsPerMeter = Math.Max(1, viewportHeight) /
            (2f * MathF.Max(distance, 12f) * MathF.Tan(fovy * (MathF.PI / 360f)));
        return pixelsPerMeter * SparseVoxelField.VoxelSizeMeters;
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
            if (key.Step != renderStep || !entry.Built)
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

    private static int GetGpuRegionVertexCapacity(int vertexCount)
    {
        if (vertexCount <= 0) return 0;
        int reserve = Math.Max(192, vertexCount / 8);
        return Math.Min(GpuPageBufferVertices, checked(vertexCount + reserve));
    }

    private static int GetGpuRegionIndexCapacity(int indexCount)
    {
        if (indexCount <= 0) return 0;
        int reserve = Math.Max(192, indexCount / 8);
        int capacity = Math.Min(
            GpuPageBufferIndices,
            checked(indexCount + reserve));
        return checked(((capacity + 2) / 3) * 3);
    }

    private bool HasCachedSurface(ChunkKey key)
    {
        GpuPageKey pageKey = GetGpuPageKey(key);
        return _gpuPages.TryGetValue(pageKey, out GpuMeshPage? page) &&
            page.Regions.TryGetValue(key, out GpuMeshRegion[]? regions) &&
            regions.Length > 0;
    }

    private static int ExpectedChunkCount(SparseVoxelField field, int step)
    {
        int span = ChunkCells * step;
        long count = (long)DivideRoundUp(field.Width, span) *
            DivideRoundUp(field.Height, span) *
            DivideRoundUp(field.Depth, span);
        return checked((int)count);
    }

    private bool HasCompleteCachedLod(
        List<(Candidate Candidate, ChunkEntry Entry)> entries,
        SparseVoxelField field,
        int step,
        out int readyCount)
    {
        readyCount = 0;
        int expected = ExpectedChunkCount(field, step);
        foreach ((Candidate _, ChunkEntry entry) in entries)
            if (entry.Built)
                readyCount++;
        return entries.Count == expected && readyCount == expected;
    }

    private bool AreLodCandidatesReady(
        List<(Candidate Candidate, ChunkEntry Entry)> entries,
        SparseVoxelField field,
        out int readyCount)
    {
        readyCount = 0;
        int expected = ExpectedChunkCount(field, _renderStep);
        foreach ((Candidate candidate, ChunkEntry entry) in entries)
        {
            bool ready = entry.Built &&
                !entry.SurfaceDirty &&
                !entry.CutDirty &&
                !entry.HasPendingSurface &&
                !entry.BuildQueued &&
                entry.RunningBuild == null &&
                !entry.ReadyBuild.HasValue &&
                entry.BaseVersion == field.BaseVersion &&
                entry.PaletteVersion == _paletteVersion &&
                entry.FailedRevision != entry.RequestRevision;
            if (ready) readyCount++;
        }
        return entries.Count == expected && readyCount == expected;
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

    private static GpuMeshBuffer CreateGpuBuffer(int vertexCapacity, int indexCapacity)
    {
        Mesh mesh = new(vertexCapacity, indexCapacity / 3);
        try
        {
            mesh.AllocVertices();
            mesh.AllocNormals();
            mesh.AllocColors();
            mesh.AllocIndices();
            mesh.VerticesAs<Vector3>().Clear();
            mesh.NormalsAs<Vector3>().Clear();
            mesh.ColorsAs<byte>().Clear();
            mesh.IndicesAs<ushort>().Clear();
            UploadMesh(ref mesh, false);
            return new GpuMeshBuffer(mesh, vertexCapacity, indexCapacity);
        }
        catch
        {
            if (mesh.VaoId != 0) UnloadMesh(mesh);
            throw;
        }
    }

    private static int GetGpuBufferCapacity(
        int required,
        int available,
        int minimum,
        bool indices)
    {
        if (required <= 0 || available < required)
            return 0;

        int capacity = Math.Min(available, minimum);
        while (capacity < required)
            capacity = capacity <= available / 2 ? capacity * 2 : available;

        if (indices)
            capacity = Math.Min(available, checked(((capacity + 2) / 3) * 3));

        return capacity >= required ? capacity : 0;
    }

    private static bool CanReserveRange(
        List<GpuFreeRange> freeRanges,
        int reservedCount,
        int totalCapacity,
        int required)
    {
        if (required <= 0) return false;
        if (reservedCount <= totalCapacity - required)
            return true;

        for (int i = 0; i < freeRanges.Count; i++)
            if (freeRanges[i].Capacity >= required)
                return true;

        return false;
    }

    private static int ReserveRange(
        List<GpuFreeRange> freeRanges,
        ref int reservedCount,
        int required)
    {
        int bestIndex = -1;
        int bestCapacity = int.MaxValue;
        for (int i = 0; i < freeRanges.Count; i++)
        {
            int capacity = freeRanges[i].Capacity;
            if (capacity >= required && capacity < bestCapacity)
            {
                bestIndex = i;
                bestCapacity = capacity;
            }
        }

        if (bestIndex >= 0)
        {
            GpuFreeRange range = freeRanges[bestIndex];
            int offset = range.Offset;
            if (range.Capacity == required)
                freeRanges.RemoveAt(bestIndex);
            else
                freeRanges[bestIndex] = new GpuFreeRange(
                    range.Offset + required,
                    range.Capacity - required);
            return offset;
        }

        int tail = reservedCount;
        reservedCount = checked(reservedCount + required);
        return tail;
    }

    private static void ReleaseRange(
        List<GpuFreeRange> freeRanges,
        ref int reservedCount,
        int offset,
        int capacity)
    {
        if (capacity <= 0) return;

        int insert = 0;
        while (insert < freeRanges.Count && freeRanges[insert].Offset < offset)
            insert++;

        if (insert > 0)
        {
            GpuFreeRange left = freeRanges[insert - 1];
            if (left.Offset + left.Capacity == offset)
            {
                offset = left.Offset;
                capacity += left.Capacity;
                freeRanges.RemoveAt(--insert);
            }
        }

        if (insert < freeRanges.Count)
        {
            GpuFreeRange right = freeRanges[insert];
            if (offset + capacity == right.Offset)
            {
                capacity += right.Capacity;
                freeRanges.RemoveAt(insert);
            }
        }

        freeRanges.Insert(insert, new GpuFreeRange(offset, capacity));

        while (freeRanges.Count > 0)
        {
            int lastIndex = freeRanges.Count - 1;
            GpuFreeRange last = freeRanges[lastIndex];
            if (last.Offset + last.Capacity != reservedCount)
                break;
            reservedCount = last.Offset;
            freeRanges.RemoveAt(lastIndex);
        }
    }

    private GpuMeshRegion AllocateGpuRegion(
        GpuMeshPage page,
        int vertexCapacity,
        int indexCapacity,
        int vertexCount,
        int indexCount)
    {
        if (vertexCapacity <= 0 || indexCapacity <= 0 ||
            vertexCapacity > GpuPageBufferVertices ||
            indexCapacity > GpuPageBufferIndices)
            throw new InvalidOperationException("Indexed mesh region exceeds GPU buffer limits.");

        if ((long)page.ReservedVertexCount + vertexCapacity > MaximumWorldMeshVertices ||
            (long)page.ReservedIndexCount + indexCapacity > MaximumWorldMeshIndices)
            throw new InvalidOperationException(
                $"LOD page exceeds indexed geometry budget ({MaximumWorldMeshVertices:N0} vertices).");

        for (int bufferIndex = 0; bufferIndex < page.Buffers.Count; bufferIndex++)
        {
            GpuMeshBuffer buffer = page.Buffers[bufferIndex];
            if (!CanReserveRange(
                    buffer.FreeVertexRanges,
                    buffer.ReservedVertexCount,
                    buffer.VertexCapacity,
                    vertexCapacity) ||
                !CanReserveRange(
                    buffer.FreeIndexRanges,
                    buffer.ReservedIndexCount,
                    buffer.IndexCapacity,
                    indexCapacity))
                continue;

            int vertexOffset = ReserveRange(
                buffer.FreeVertexRanges,
                ref buffer.ReservedVertexCount,
                vertexCapacity);
            int indexOffset = ReserveRange(
                buffer.FreeIndexRanges,
                ref buffer.ReservedIndexCount,
                indexCapacity);

            page.ReservedVertexCount += vertexCapacity;
            page.ReservedIndexCount += indexCapacity;
            return new GpuMeshRegion(
                bufferIndex,
                vertexOffset,
                vertexCapacity,
                indexOffset,
                indexCapacity,
                vertexCount,
                indexCount);
        }

        int pageVertexAvailable = MaximumWorldMeshVertices - page.AllocatedVertexCount;
        int pageIndexAvailable = MaximumWorldMeshIndices - page.AllocatedIndexCount;
        GetGpuVertexUsage(
            out long totalPhysicalVertices,
            out _,
            out long totalPhysicalIndices,
            out _);
        int globalVertexAvailable = (int)Math.Clamp(
            MaximumTotalGpuPageVertices - totalPhysicalVertices,
            0L, int.MaxValue);
        int globalIndexAvailable = (int)Math.Clamp(
            MaximumTotalGpuPageIndices - totalPhysicalIndices,
            0L, int.MaxValue);

        int vertexBufferCapacity = GetGpuBufferCapacity(
            vertexCapacity,
            Math.Min(GpuPageBufferVertices,
                Math.Min(pageVertexAvailable, globalVertexAvailable)),
            4096,
            indices: false);
        int indexBufferCapacity = GetGpuBufferCapacity(
            indexCapacity,
            Math.Min(GpuPageBufferIndices,
                Math.Min(pageIndexAvailable, globalIndexAvailable)),
            6144,
            indices: true);

        if (vertexBufferCapacity < vertexCapacity ||
            indexBufferCapacity < indexCapacity)
        {
            bool pageLimited = pageVertexAvailable < vertexCapacity ||
                pageIndexAvailable < indexCapacity;
            throw new InvalidOperationException(
                pageLimited
                    ? "No reusable GPU page range or physical capacity remains."
                    : "No global GPU page capacity remains.");
        }

        GpuMeshBuffer nextBuffer = CreateGpuBuffer(
            vertexBufferCapacity,
            indexBufferCapacity);
        page.Buffers.Add(nextBuffer);
        page.AllocatedVertexCount += vertexBufferCapacity;
        page.AllocatedIndexCount += indexBufferCapacity;
        page.CapacityVersion++;
        _gpuCapacityVersion++;

        nextBuffer.ReservedVertexCount = vertexCapacity;
        nextBuffer.ReservedIndexCount = indexCapacity;
        page.ReservedVertexCount += vertexCapacity;
        page.ReservedIndexCount += indexCapacity;
        GpuUploadsLastFrame++;
        _totalGpuUploads++;

        return new GpuMeshRegion(
            page.Buffers.Count - 1,
            0,
            vertexCapacity,
            0,
            indexCapacity,
            vertexCount,
            indexCount);
    }

    private void BuildGpuSourceGroups(VoxelMeshData[] data)
    {
        _gpuSourceGroupsScratch.Clear();

        int firstSource = -1;
        int sourceCount = 0;
        int vertexCount = 0;
        int indexCount = 0;

        for (int i = 0; i < data.Length; i++)
        {
            VoxelMeshData source = data[i];
            if (source.VertexCount <= 0 || source.IndexCount <= 0)
                continue;

            if (source.VertexCount > GpuPageBufferVertices ||
                source.IndexCount > GpuPageBufferIndices)
                throw new InvalidOperationException(
                    "Surface packet exceeds indexed GPU page limits.");

            if (sourceCount > 0 &&
                (vertexCount + source.VertexCount > GpuPageBufferVertices ||
                 indexCount + source.IndexCount > GpuPageBufferIndices))
            {
                _gpuSourceGroupsScratch.Add(new GpuSourceGroup(
                    firstSource, sourceCount, vertexCount, indexCount));
                firstSource = -1;
                sourceCount = 0;
                vertexCount = 0;
                indexCount = 0;
            }

            if (sourceCount == 0)
                firstSource = i;

            sourceCount++;
            vertexCount += source.VertexCount;
            indexCount += source.IndexCount;
        }

        if (sourceCount > 0)
            _gpuSourceGroupsScratch.Add(new GpuSourceGroup(
                firstSource, sourceCount, vertexCount, indexCount));
    }

    private bool CanUpdateGpuRegions(
        GpuMeshRegion[] regions,
        List<GpuSourceGroup> groups)
    {
        if (regions.Length != groups.Count)
            return false;

        for (int i = 0; i < groups.Count; i++)
        {
            GpuSourceGroup group = groups[i];
            GpuMeshRegion region = regions[i];
            if (group.VertexCount > region.VertexCapacity ||
                group.IndexCount > region.IndexCapacity)
                return false;
        }

        return true;
    }

    private GpuMeshRegion[] AppendGpuRegions(
        GpuMeshPage page,
        VoxelMeshData[] data)
    {
        List<GpuSourceGroup> groups = _gpuSourceGroupsScratch;
        GpuMeshRegion[] regions = new GpuMeshRegion[groups.Count];
        int allocatedCount = 0;

        try
        {
            for (int i = 0; i < groups.Count; i++)
            {
                GpuSourceGroup group = groups[i];
                int vertexCapacity = GetGpuRegionVertexCapacity(group.VertexCount);
                int indexCapacity = GetGpuRegionIndexCapacity(group.IndexCount);
                GpuMeshRegion region = AllocateGpuRegion(
                    page,
                    vertexCapacity,
                    indexCapacity,
                    group.VertexCount,
                    group.IndexCount);
                regions[i] = region;
                allocatedCount++;

                GpuMeshBuffer buffer = page.Buffers[region.BufferIndex];
                VoxelChunkMesher.UpdateIndexedMesh(
                    ref buffer.Mesh,
                    region.VertexOffset,
                    region.VertexCapacity,
                    region.IndexOffset,
                    region.IndexCapacity,
                    data,
                    group.FirstSource,
                    group.SourceCount);
            }

            return regions;
        }
        catch
        {
            for (int i = allocatedCount - 1; i >= 0; i--)
                ReleaseGpuRegion(page, regions[i]);
            throw;
        }
    }

    private void ReleaseGpuRegion(GpuMeshPage page, GpuMeshRegion region)
    {
        if (region.VertexCapacity <= 0 ||
            (uint)region.BufferIndex >= (uint)page.Buffers.Count)
            return;

        GpuMeshBuffer buffer = page.Buffers[region.BufferIndex];
        ReleaseRange(
            buffer.FreeVertexRanges,
            ref buffer.ReservedVertexCount,
            region.VertexOffset,
            region.VertexCapacity);
        ReleaseRange(
            buffer.FreeIndexRanges,
            ref buffer.ReservedIndexCount,
            region.IndexOffset,
            region.IndexCapacity);

        page.ReservedVertexCount -= region.VertexCapacity;
        page.ReservedIndexCount -= region.IndexCapacity;
        page.CapacityVersion++;
        _gpuCapacityVersion++;
    }

    private void ReleaseGpuRegions(GpuMeshPage page, GpuMeshRegion[] regions)
    {
        for (int i = 0; i < regions.Length; i++)
            ReleaseGpuRegion(page, regions[i]);
    }

    private void AddVisibleGpuRanges(ChunkKey key, ChunkEntry entry)
    {
        if (!entry.Built ||
            !_gpuPages.TryGetValue(GetGpuPageKey(key), out GpuMeshPage? page) ||
            page.Dirty ||
            !page.Regions.TryGetValue(key, out GpuMeshRegion[]? regions))
            return;

        foreach (GpuMeshRegion region in regions)
        {
            if (region.IndexCount <= 0 ||
                (uint)region.BufferIndex >= (uint)page.Buffers.Count)
                continue;

            _visibleGpuRanges.Add(new GpuDrawRange(
                page.Buffers[region.BufferIndex],
                key.Step,
                region.IndexOffset,
                region.IndexCount));
        }
    }

    private unsafe int DrawVisibleGpuRanges()
    {
        if (_visibleGpuRanges.Count == 0)
            return 0;

        _visibleGpuRanges.Sort(static (a, b) =>
        {
            int bufferOrder = a.Buffer.Mesh.VaoId.CompareTo(b.Buffer.Mesh.VaoId);
            return bufferOrder != 0
                ? bufferOrder
                : a.IndexOffset.CompareTo(b.IndexOffset);
        });

        bool vertexArrayBound = false;
        uint currentVao = 0;
        int draws = 0;

        try
        {
            int index = 0;
            while (index < _visibleGpuRanges.Count)
            {
                GpuDrawRange first = _visibleGpuRanges[index];
                uint vao = first.Buffer.Mesh.VaoId;
                if (!vertexArrayBound || currentVao != vao)
                {
                    if (vertexArrayBound)
                        Rlgl.DisableVertexArray();

                    if (!Rlgl.EnableVertexArray(vao))
                        throw new InvalidOperationException(
                            $"Could not bind indexed terrain VAO {vao}.");

                    vertexArrayBound = true;
                    currentVao = vao;
                }

                    int start = first.IndexOffset;
                int count = first.IndexCount;
                index++;

                while (index < _visibleGpuRanges.Count)
                {
                    GpuDrawRange next = _visibleGpuRanges[index];
                    if (next.Buffer.Mesh.VaoId != vao ||
                        next.Step != first.Step ||
                        next.IndexOffset != start + count)
                        break;

                    count += next.IndexCount;
                    index++;
                }

                Rlgl.DrawVertexArrayElements(start, count, null);
                draws++;
            }
        }
        finally
        {
            if (vertexArrayBound)
                Rlgl.DisableVertexArray();
            }

        return draws;
    }

    internal static void FillCutMask(
        IVoxelFieldSource field,
        int layer,
        Span<byte> pixels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(field);
        if ((uint)layer > (uint)field.Height)
            throw new ArgumentOutOfRangeException(nameof(layer));

        int required = checked(field.Width * field.Depth);
        if (pixels.Length < required)
            throw new ArgumentException("Cut mask destination is too small.", nameof(pixels));

        int lowerY = layer - 1;
        int upperY = layer;
        for (int z = 0; z < field.Depth; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = z * field.Width;
            for (int x = 0; x < field.Width; x++)
            {
                float density = (field.GetDensity(x, lowerY, z) +
                    field.GetDensity(x, upperY, z)) * 0.5f;
                pixels[row + x] = density > 0f ? (byte)255 : (byte)0;
            }
        }
    }

    private void RequestCutMaskBuild(SparseVoxelField field, int layer)
    {
        if (layer < 0 || layer > field.Height)
            return;

        if (_cutMaskCancellation != null)
        {
            try { _cutMaskCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        _cutMaskRequestedLayer = layer;
        long generation = Interlocked.Increment(ref _cutMaskGeneration);
        int width = field.Width;
        int depth = field.Depth;
        IVoxelFieldSource snapshot = GetWorldBuildSnapshot(field);
        long fieldVersion = field.Version;
        CancellationTokenSource cancellation = new();
        CancellationToken token = cancellation.Token;
        _cutMaskCancellation = cancellation;

        _ = Task.Run(() =>
        {
            byte[]? pixels = null;
            try
            {
                pixels = ArrayPool<byte>.Shared.Rent(checked(width * depth));
                FillCutMask(
                    snapshot,
                    layer,
                    pixels.AsSpan(0, width * depth),
                    token);

                if (generation != Volatile.Read(ref _cutMaskGeneration))
                    return;

                _cutMaskCompletions.Enqueue(new CutMaskBuildResult(
                    generation, layer, width, depth, fieldVersion, pixels));
                pixels = null;
            }
            catch (Exception exception) when (
                exception is OperationCanceledException ||
                exception is ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                _logger.Error(
                    "CUT_MASK_BUILD_FAILED",
                    $"layer={layer} generation={generation} error={exception}");
            }
            finally
            {
                if (pixels != null)
                    ArrayPool<byte>.Shared.Return(pixels, clearArray: false);
                cancellation.Dispose();
            }
        });
    }

    private unsafe void ProcessCutMaskCompletions(SparseVoxelField field)
    {
        while (_cutMaskCompletions.TryDequeue(out CutMaskBuildResult result))
        {
            if (result.Generation != Volatile.Read(ref _cutMaskGeneration) ||
                result.Layer != _cutMaskRequestedLayer ||
                result.FieldVersion != field.Version)
            {
                ArrayPool<byte>.Shared.Return(result.Pixels, clearArray: false);
                if (result.FieldVersion != field.Version)
                    Interlocked.Exchange(ref _cutMaskDirty, 1);
                continue;
            }

            try
            {
                if (_cutMaskTexture.Id == 0 ||
                    _cutMaskWidth != result.Width ||
                    _cutMaskDepth != result.Depth)
                {
                    fixed (byte* pixels = result.Pixels)
                    {
                        Image image = new()
                        {
                            Data = pixels,
                            Width = result.Width,
                            Height = result.Depth,
                            Mipmaps = 1,
                            Format = PixelFormat.UncompressedGrayscale
                        };
                        Texture2D nextTexture = LoadTextureFromImage(image);
                        if (nextTexture.Id == 0)
                            throw new InvalidOperationException(
                                "Could not upload voxel cut mask texture.");

                        SetTextureFilter(nextTexture, TextureFilter.Bilinear);
                        SetTextureWrap(nextTexture, TextureWrap.Clamp);

                        if (_cutMaskTexture.Id != 0)
                            UnloadTexture(_cutMaskTexture);
                        _cutMaskTexture = nextTexture;
                    }

                    _cutMaskWidth = result.Width;
                    _cutMaskDepth = result.Depth;
                }
                else
                {
                    fixed (byte* pixels = result.Pixels)
                        UpdateTexture(_cutMaskTexture, pixels);
                }

                EnsureCutPlaneMesh(result.Width, result.Depth);
                _cutMaskReadyLayer = result.Layer;
                _activeCutLayer = result.Layer;

                _logger.Write(
                    RenderLogMode.FrameSummary,
                    "CUT_MASK_READY",
                    $"layer={result.Layer} size={result.Width}x{result.Depth} fieldVersion={result.FieldVersion}");
            }
            catch (Exception exception)
            {
                _logger.Error(
                    "CUT_MASK_UPLOAD_FAILED",
                    $"layer={result.Layer} generation={result.Generation} error={exception}");
                Interlocked.Exchange(ref _cutMaskDirty, 1);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(result.Pixels, clearArray: false);
            }
        }
    }

    private void EnsureCutPlaneMesh(int width, int depth)
    {
        float worldWidth = width * SparseVoxelField.VoxelSizeMeters;
        float worldDepth = depth * SparseVoxelField.VoxelSizeMeters;
        if (_cutPlaneMesh.VaoId != 0 &&
            _cutPlaneWorldWidth == worldWidth &&
            _cutPlaneWorldDepth == worldDepth)
            return;

        if (_cutPlaneMesh.VaoId != 0)
            UnloadMesh(_cutPlaneMesh);

        Mesh mesh = new(4, 2);
        mesh.AllocVertices();
        mesh.AllocNormals();
        mesh.AllocColors();
        mesh.AllocIndices();

        Span<Vector3> vertices = mesh.VerticesAs<Vector3>();
        vertices[0] = new Vector3(0f, 0f, 0f);
        vertices[1] = new Vector3(0f, 0f, worldDepth);
        vertices[2] = new Vector3(worldWidth, 0f, worldDepth);
        vertices[3] = new Vector3(worldWidth, 0f, 0f);

        Span<Vector3> normals = mesh.NormalsAs<Vector3>();
        normals.Fill(Vector3.UnitY);

        Span<byte> colors = mesh.ColorsAs<byte>();
        colors.Fill(255);

        Span<ushort> indices = mesh.IndicesAs<ushort>();
        indices[0] = 0;
        indices[1] = 1;
        indices[2] = 2;
        indices[3] = 0;
        indices[4] = 2;
        indices[5] = 3;

        UploadMesh(ref mesh, false);
        _cutPlaneMesh = mesh;
        _cutPlaneWorldWidth = worldWidth;
        _cutPlaneWorldDepth = worldDepth;
    }

    private void DrawCutOverlay(SparseVoxelField field, float heightMeters)
    {
        if (_cutMaskTexture.Id == 0 || _cutPlaneMesh.VaoId == 0)
            return;

        SetShaderValueTexture(_shader, _cutMaskLocation, _cutMaskTexture);
        SetShaderValue(
            _shader,
            _cutOverlayPassLocation,
            1,
            ShaderUniformDataType.Int);
        SetShaderValue(
            _shader,
            _cutWorldSizeLocation,
            new Vector2(
                field.Width * SparseVoxelField.VoxelSizeMeters,
                field.Depth * SparseVoxelField.VoxelSizeMeters),
            ShaderUniformDataType.Vec2);
        SetShaderValue(
            _shader,
            _cutColorLocation,
            new Vector4(0.31f, 0.23f, 0.16f, 1f),
            ShaderUniformDataType.Vec4);

        DrawMesh(
            _cutPlaneMesh,
            _material,
            Raymath.MatrixTranslate(0f, heightMeters, 0f));

        SetShaderValue(
            _shader,
            _cutOverlayPassLocation,
            0,
            ShaderUniformDataType.Int);
    }

    private void GetGpuVertexUsage(
        out long physicalVertices,
        out long liveVertices,
        out long physicalIndices,
        out long liveIndices)
    {
        physicalVertices = 0;
        liveVertices = 0;
        physicalIndices = 0;
        liveIndices = 0;

        foreach (GpuMeshPage page in _gpuPages.Values)
        {
            physicalVertices += page.AllocatedVertexCount;
            liveVertices += page.ReservedVertexCount;
            physicalIndices += page.AllocatedIndexCount;
            liveIndices += page.ReservedIndexCount;
        }
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
                int vertexCount = VoxelChunkMesher.CountVertices(pending);
                int indexCount = VoxelChunkMesher.CountIndices(pending);

                try
                {
                    bool hadRegions = page.Regions.TryGetValue(
                        key, out GpuMeshRegion[]? oldRegions);

                    if (vertexCount == 0 || indexCount == 0)
                    {
                        if (hadRegions)
                            ReleaseGpuRegions(page, oldRegions!);
                        page.Regions.Remove(key);
                    }
                    else
                    {
                        BuildGpuSourceGroups(pending);
                        if (_gpuSourceGroupsScratch.Count == 0)
                        {
                            if (hadRegions)
                                ReleaseGpuRegions(page, oldRegions!);
                            page.Regions.Remove(key);
                        }
                        else if (hadRegions &&
                            CanUpdateGpuRegions(oldRegions!, _gpuSourceGroupsScratch))
                        {
                            for (int groupIndex = 0;
                                 groupIndex < _gpuSourceGroupsScratch.Count;
                                 groupIndex++)
                            {
                                GpuSourceGroup group = _gpuSourceGroupsScratch[groupIndex];
                                GpuMeshRegion oldRegion = oldRegions![groupIndex];
                                GpuMeshBuffer buffer = page.Buffers[oldRegion.BufferIndex];

                                VoxelChunkMesher.UpdateIndexedMesh(
                                    ref buffer.Mesh,
                                    oldRegion.VertexOffset,
                                    oldRegion.VertexCapacity,
                                    oldRegion.IndexOffset,
                                    oldRegion.IndexCapacity,
                                    pending,
                                    group.FirstSource,
                                    group.SourceCount);

                                oldRegions[groupIndex] = oldRegion with
                                {
                                    VertexCount = group.VertexCount,
                                    IndexCount = group.IndexCount
                                };
                            }

                            page.Regions[key] = oldRegions!;
                        }
                        else
                        {
                            // Upload the replacement first. The old indexed ranges remain
                            // valid until every new packet has reached its GPU buffer.
                            GpuMeshRegion[] nextRegions = AppendGpuRegions(page, pending);
                            if (hadRegions)
                                ReleaseGpuRegions(page, oldRegions!);
                            page.Regions[key] = nextRegions;
                        }
                    }

                    entry.PendingSurface = Array.Empty<VoxelMeshData>();
                    entry.HasPendingSurface = false;
                    entry.BaseVersion = entry.PendingSurfaceBaseVersion;
                    entry.PaletteVersion = entry.PendingSurfacePaletteVersion;
                    entry.CutLayer = entry.PendingSurfaceCutLayer;
                    entry.SurfaceDirty = false;
                    entry.CutDirty = false;
                    entry.Built = true;
                    entry.GpuUploadRetryFrame = 0;
                    entry.GpuUploadRetryVersion = long.MinValue;
                    DiscardLegacyCutCap(key, entry);
                    SetSurfaceGpuHash(key, entry.PendingSurfaceHash);
                    ReturnMeshData(pending);

                    RebuiltChunks++;
                    if (entry.PendingSurfaceCutLayer != int.MinValue)
                        RebuiltCutCaps++;
                    GpuUploadsLastFrame++;
                    _totalGpuUploads++;
                }
                catch (Exception exception)
                {
                    bool capacityBlocked = exception is InvalidOperationException &&
                        (exception.Message.StartsWith("No reusable GPU page range", StringComparison.Ordinal) ||
                         exception.Message.StartsWith("No global GPU page capacity", StringComparison.Ordinal) ||
                         exception.Message.StartsWith("LOD page exceeds", StringComparison.Ordinal));
                    entry.GpuUploadRetryFrame = capacityBlocked ? long.MaxValue : _frame + 60;
                    entry.GpuUploadRetryVersion = capacityBlocked
                        ? _gpuCapacityVersion
                        : long.MinValue;
                    entry.SurfaceDirty = true;
                    _totalGpuUploadFailures++;
                    _logger.Error(
                        "WORLD_MESH_REGION_UPDATE_FAILED",
                        $"chunk=({key.X},{key.Y},{key.Z}) step={key.Step} retryFrame={entry.GpuUploadRetryFrame} error={exception}");
                }
            }
        }

        _dirtyGpuPageKeys.RemoveRange(0, dirtyCount);
        GpuUploadMillisecondsLastFrame += timer.Elapsed.TotalMilliseconds;
    }

    private void RetryPendingGpuUploads()
    {
        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
        {
            if (!entry.HasPendingSurface) continue;

            if (entry.GpuUploadRetryVersion != long.MinValue)
            {
                if (_gpuCapacityVersion == entry.GpuUploadRetryVersion)
                    continue;

                entry.GpuUploadRetryVersion = long.MinValue;
                entry.GpuUploadRetryFrame = 0;
            }

            if (_frame < entry.GpuUploadRetryFrame) continue;
            entry.GpuUploadRetryFrame = 0;
            MarkGpuPageDirty(key);
        }
    }

    private void PrepareCandidateEntries(
        SparseVoxelField field,
        List<Candidate> candidates,
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

            if ((surfaceStale || entry.CutDirty) && !HasPendingWork(entry))
            {
                RequestBuild(
                    field, key, entry,
                    buildSurface: true, buildCutCap: false,
                    cutLayer: int.MinValue);
            }

            if (entry.CutCap.Length > 0)
                ClearCutCap(key, entry);

            destination.Add((candidate, entry));
        }
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
            // Start from one coarse world representation. Detail is prepared
            // in the background and replaces it only after full map coverage.
            _displayStep = FallbackLodStep;
            _renderStep = FallbackLodStep;
            _pendingRenderStep = 0;
            _pendingRenderStepTimestamp = 0;
            _lastLodSwitchTimestamp = Stopwatch.GetTimestamp();
            _logger.Write(
                "VOXEL_LOD_INITIAL",
                $"display={_displayStep} desired={desiredStep} voxelMeters={ActiveVoxelSizeMeters:0.0} distance={distance:0.0} pixelsPerVoxel={pixelsPerVoxel:0.000}");
            return;
        }

        float currentVoxelPixels = pixelsPerVoxel * _displayStep;
        if (desiredStep > _displayStep && currentVoxelPixels > 0.60f)
            desiredStep = _displayStep;
        else if (desiredStep < _displayStep && currentVoxelPixels < 1.45f)
            desiredStep = _displayStep;

        desiredStep = Math.Max(desiredStep, worldStep);
        if (desiredStep != _displayStep && worldStep <= _displayStep &&
            _lastLodSwitchTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastLodSwitchTimestamp).TotalMilliseconds < LodMinimumResidenceMilliseconds)
            desiredStep = _displayStep;

        int nextTargetStep = desiredStep;

        // Do not abandon an expensive world build for another nearby LOD target.
        // The only allowed retarget is a return to the currently displayed level.
        if (_renderStep != _displayStep && nextTargetStep != _displayStep)
            return;

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
                $"display={_displayStep} target={_renderStep} desired={nextTargetStep} distance={distance:0.0} pixelsPerVoxel={pixelsPerVoxel:0.000} currentPixels={currentVoxelPixels:0.000} voxelMeters={SparseVoxelField.VoxelSizeMeters * _renderStep:0.0} chunkMeters={ChunkCells * SparseVoxelField.VoxelSizeMeters * _renderStep:0.0}");
        else if (previousTargetStep != _displayStep)
            _logger.Write(
                "VOXEL_LOD_CANCEL",
                $"display={_displayStep} abandonedTarget={previousTargetStep}");
    }

    private void RetireFallbackLod()
    {
        _inactiveLodChunkScratch.Clear();
        foreach (ChunkKey key in _chunks.Keys)
            if (key.Step == FallbackLodStep)
                _inactiveLodChunkScratch.Add(key);

        foreach (ChunkKey key in _inactiveLodChunkScratch)
        {
            if (!_chunks.Remove(key, out ChunkEntry? entry)) continue;
            InvalidatePendingBuild(entry);
            entry.Dispose();
            _gpuMeshHashCache.Remove(key);
        }

        GpuPageKey fallbackKey = new(FallbackLodStep);
        if (_gpuPages.Remove(fallbackKey, out GpuMeshPage? fallbackPage))
        {
            _totalGpuUnloads += fallbackPage.Buffers.Count;
            fallbackPage.Dispose();
            _gpuCapacityVersion++;
        }

        _dirtyGpuPageKeys.RemoveAll(key => key.Step == FallbackLodStep);
        _visibleCandidateKeys.ExceptWith(_coarseCandidateKeys);
        _coarseCandidateKeys.Clear();
        _inactiveLodChunkScratch.Clear();
    }

    private void ReleaseInactiveLodData()
    {
        _inactiveLodChunkScratch.Clear();
        if (_chunks.Count > MaximumCachedChunks)
            _previousDisplayStep = 0;

        foreach (ChunkKey key in _chunks.Keys)
            if (key.Step != _displayStep && key.Step != _previousDisplayStep && key.Step != FallbackLodStep)
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
            if (key.Step != _displayStep && key.Step != _previousDisplayStep && key.Step != FallbackLodStep)
                _inactiveLodPageScratch.Add(key);

        foreach (GpuPageKey key in _inactiveLodPageScratch)
        {
            if (!_gpuPages.Remove(key, out GpuMeshPage? page)) continue;
            _totalGpuUnloads += page.Buffers.Count;
            page.Dispose();
        }

        _dirtyGpuPageKeys.RemoveAll(key =>
            key.Step != _displayStep && key.Step != _previousDisplayStep &&
            key.Step != FallbackLodStep);
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

            bool inactiveLod = key.Step != BaseLodStep &&
                key.Step != FallbackLodStep &&
                entry.LastSeenFrame + 2 < _frame;
            bool expired = inactiveLod && entry.LastSeenFrame + 360 < _frame;
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
            if (page.Regions.Remove(key, out GpuMeshRegion[]? regions))
                ReleaseGpuRegions(page, regions);

            // A fully unused detail page must return its physical VBO allocation
            // to the global budget so another LOD can claim it.
            if (page.Regions.Count == 0 &&
                pageKey.Step != BaseLodStep &&
                pageKey.Step != FallbackLodStep)
            {
                _gpuPages.Remove(pageKey);
                _dirtyGpuPageKeys.RemoveAll(dirtyKey => dirtyKey.Equals(pageKey));
                _totalGpuUnloads += page.Buffers.Count;
                page.Dispose();
                _gpuCapacityVersion++;
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
                request.Key.Step, ChunkCells);

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
        entry.GpuUploadRetryFrame = 0;
        entry.GpuUploadRetryVersion = long.MinValue;

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
        Interlocked.Exchange(ref _cutMaskDirty, 1);
    }

    private void OnVoxelFieldChanged(SparseVoxelField.VoxelBounds bounds)
    {
        int activeLayer = _activeCutLayer;
        int requestedLayer = _cutMaskRequestedLayer;
        if ((activeLayer >= 0 &&
             bounds.MinY <= activeLayer && bounds.MaxY > activeLayer - 1) ||
            (requestedLayer >= 0 &&
             bounds.MinY <= requestedLayer && bounds.MaxY > requestedLayer - 1))
            Interlocked.Exchange(ref _cutMaskDirty, 1);

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
            bool surfaceChanged =
                Intersects(firstX, lastX, bounds.MinX, bounds.MaxX) &&
                Intersects(firstY, lastY, bounds.MinY, bounds.MaxY) &&
                Intersects(firstZ, lastZ, bounds.MinZ, bounds.MaxZ);
            if (!surfaceChanged)
                continue;

            entry.SurfaceDirty = true;
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

        Interlocked.Increment(ref _cutMaskGeneration);
        if (_cutMaskCancellation != null)
        {
            try { _cutMaskCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            _cutMaskCancellation = null;
        }
        while (_cutMaskCompletions.TryDequeue(out CutMaskBuildResult cutMask))
            ArrayPool<byte>.Shared.Return(cutMask.Pixels, clearArray: false);

        if (_cutMaskTexture.Id != 0)
        {
            UnloadTexture(_cutMaskTexture);
            _cutMaskTexture = default;
        }
        if (_cutPlaneMesh.VaoId != 0)
        {
            UnloadMesh(_cutPlaneMesh);
            _cutPlaneMesh = default;
        }

        foreach (ChunkEntry entry in _chunks.Values)
        {
            InvalidatePendingBuild(entry);
            if (entry.ReadyBuild is VoxelMeshBuildResult readyBuild)
                ReturnBuildCpuData(readyBuild);
            entry.Dispose();
        }

        foreach (GpuMeshPage page in _gpuPages.Values)
        {
            _totalGpuUnloads += page.Buffers.Count;
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
        _adaptiveNodeScratch.Clear();
        _baseCandidateScratch.Clear();
        _baseCandidateEntriesScratch.Clear();
        _baseCandidateKeys.Clear();
        _splitChunkKeys.Clear();
        _nextSplitChunkKeys.Clear();
        _visibleChunkKeys.Clear();
        _baseCandidateIndices.Clear();
        _visiblePages.Clear();
        _transitionVisiblePages.Clear();
        _visiblePageKeys.Clear();
        _transitionVisiblePageKeys.Clear();
        _inactiveLodChunkScratch.Clear();
        _coarseCandidateScratch.Clear();
        _coarseCandidateEntriesScratch.Clear();
        _coarseVisiblePages.Clear();
        _coarseCandidateKeys.Clear();
        _inactiveLodPageScratch.Clear();

        UnloadMaterial(_material);
        _logger.Write(RenderLogMode.ErrorsOnly, "VOXEL_RENDERER_STOP",
            $"requests={_totalBuildRequests} started={_totalBuildStarted} completed={_totalBuildCompleted} cancelled={_totalBuildCancelled} failed={_totalBuildFailed} stale={_totalStaleResults} gpuUploads={_totalGpuUploads} gpuUnloads={_totalGpuUnloads} cache={_chunks.Count}");
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
