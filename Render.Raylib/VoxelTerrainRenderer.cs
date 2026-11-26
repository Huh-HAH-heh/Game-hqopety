using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Core.Map;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// Large volumetric render regions meshed from the signed density field with Surface Nets.
/// Render regions contain 64^3 cells and use 1/2/4 sample spacing by camera distance.
/// </summary>
internal sealed class VoxelTerrainRenderer : IDisposable
{
    private const int ChunkCells = VoxelChunkBuildInput.DefaultCellsPerAxis;
    private const int MaximumPendingMeshJobs = 128;
    private const int MaximumMeshBuildsPerFrame = 4;
    private const int MaximumGpuMeshUploadsPerFrame = 4;
    private const double MeshBuildBudgetMilliseconds = 4.0;
    private const double GpuUploadBudgetMilliseconds = 3.0;
    private const int CutCapSettleFrames = 8;

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

    private readonly record struct ChunkKey(int X, int Y, int Z);

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
        public required int RequestRevision { get; init; }
        public required long Generation { get; init; }
        public required int PaletteVersion { get; init; }
        public required long BaseVersion { get; init; }
        public required bool BuildSurface { get; init; }
        public required bool BuildCutCap { get; init; }
        public required int CutLayer { get; init; }
        public required TerrainPalette Palette { get; init; }
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
    private readonly Dictionary<ChunkKey, ChunkGpuHashes> _gpuMeshHashCache = new();

    private readonly Queue<ReadyUpload> _readyUploadQueue = new();
    private readonly Queue<BuildRequest> _buildQueue = new();
    private readonly Queue<BuildCompletion> _completedBuildQueue = new();
    private int _activeStep = 2;

    private readonly List<Candidate> _candidateScratch = new();
    private readonly List<(Candidate Candidate, ChunkEntry Entry)> _candidateEntriesScratch = new();
    private readonly List<ChunkKey> _outsideRadiusScratch = new();
    private readonly List<(ChunkKey Key, ChunkEntry Entry)> _visible = new();
    private readonly HashSet<ChunkKey> _visibleCandidateKeys = new();

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

    public int ActiveRenderCellSize => _activeStep;
    public float ActiveVoxelSizeMeters => SparseVoxelField.VoxelSizeMeters * _activeStep;
    public float RenderChunkSizeMeters =>
        ChunkCells * _activeStep * SparseVoxelField.VoxelSizeMeters;
    public bool IsUsingCoarseLod => _activeStep > 1;
    public int Step1ChunksLastFrame { get; private set; }
    public int Step4ChunksLastFrame { get; private set; }
    public int Step16ChunksLastFrame => 0;
    public int DrawCallsLastFrame { get; private set; }
    public int VisibleChunksLastFrame { get; private set; }
    public int RebuiltChunks { get; private set; }
    public int RebuiltCutCaps { get; private set; }
    public int LodPrewarmBuilds => 0;

    public double CpuCaptureMillisecondsLastFrame { get; private set; }
    public double GpuUploadMillisecondsLastFrame { get; private set; }
    public int RunningMeshJobs => 0;
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

        _logger.Write(
            "RENDERER_START",
            $"resolution=SurfaceNets(1,2,4);chunkCells={ChunkCells};step=auto;chunkMeters={RenderChunkSizeMeters:0.0};storageChunkCells={SparseVoxelField.ChunkSize};workers=0;build=main-thread-budget;meshBudgetMs={MeshBuildBudgetMilliseconds:0.0};gpuUploadBudgetMs={GpuUploadBudgetMilliseconds:0.0};mesher=surface-nets-density");
        _logger.Write(RenderLogMode.ErrorsOnly, "VOXEL_RENDERER_READY", $"logPath=\"{_logger.LogPath}\" workers=0 build=main-thread");
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

        long voxelFrameStarted = Stopwatch.GetTimestamp();
        _dynamicFragments.Update(field, GetFrameTime());
        SetRenderStep(SelectRenderStep(camera));

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

        List<Candidate> candidates = _candidateScratch;
        candidates.Clear();

        int frustumTests = 0;
        int macroFrustumTests = 0;
        int visibleMacroRegions = 0;
        CollectVisibleCandidates(
            field,
            camera,
            viewportWidth,
            viewportHeight,
            maximumViewDistanceMeters,
            candidates,
            ref frustumTests,
            ref macroFrustumTests,
            ref visibleMacroRegions);

        candidates.Sort(static (a, b) =>
            a.DistanceSquared.CompareTo(b.DistanceSquared));

        FrustumCandidatesLastFrame = frustumTests;
        MacroFrustumTestsLastFrame = macroFrustumTests;
        VisibleMacroRegionsLastFrame = visibleMacroRegions;
        CandidateOverflowLastFrame = 0;
        Step1ChunksLastFrame = _activeStep == 1 ? candidates.Count : 0;
        Step4ChunksLastFrame = _activeStep == 4 ? candidates.Count : 0;
        _visibleCandidateKeys.Clear();

        List<(Candidate Candidate, ChunkEntry Entry)> candidateEntries =
            _candidateEntriesScratch;
        candidateEntries.Clear();

        int stableCutLayer = cutEnabled &&
            _cutLayerStableFrames >= CutCapSettleFrames
                ? _stableCutLayer
                : -1;

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
            bool surfaceStale = !entry.Built ||
                entry.SurfaceDirty ||
                entry.BaseVersion != field.BaseVersion ||
                entry.PaletteVersion != _paletteVersion;

            int targetCutLayer = cutEnabled && stableCutLayer >= 0
                ? stableCutLayer
                : int.MinValue;

            bool cutStateStale = entry.CutLayer != targetCutLayer;
            bool needsSurfaceBuild =
                surfaceStale ||
                cutStateStale ||
                entry.CutDirty;


            if (needsSurfaceBuild && !HasPendingWork(entry))
            {
                RequestBuild(
                    field,
                    key,
                    entry,
                    buildSurface: true,
                    buildCutCap: false,
                    cutLayer: targetCutLayer);
            }

            if (!cutEnabled && entry.CutCap.Length > 0)
                ClearCutCap(key, entry);

            candidateEntries.Add((candidate, entry));
        }

        EvictChunksOutsideRadius(
            field,
            camera.Position,
            MathF.Max(0f, maximumViewDistanceMeters));

        ProcessBuildQueueOnMainThread(field);
        CollectCompletedBuilds(field);
        UploadReadyMeshes();
        if (_logger.IsEnabled(RenderLogMode.Detailed))
            _logger.Write(RenderLogMode.Detailed, "VOXEL_VISIBILITY", $"frame={_frame} candidates={candidates.Count} macroTests={MacroFrustumTestsLastFrame} macroVisible={VisibleMacroRegionsLastFrame} chunkTests={FrustumCandidatesLastFrame} cache={_chunks.Count} visibleCandidates={_visibleCandidateKeys.Count} packets={GpuUploadsLastFrame} uploadMs={GpuUploadMillisecondsLastFrame:0.000}");

        _visible.Clear();
        foreach ((Candidate candidate, ChunkEntry entry) in candidateEntries)
        {
            bool hasVisibleSurface = entry.Surface.Length > 0;
            bool hasVisibleCutCap = cutEnabled &&
                stableCutLayer >= 0 &&
                entry.CutLayer == stableCutLayer &&
                entry.CutCap.Length > 0;

            if (entry.Built && (hasVisibleSurface || hasVisibleCutCap))
                AddVisible(candidate.Key, entry);
        }

        double remainingUploadBudget = Math.Max(
            0d,
            GpuUploadBudgetMilliseconds - GpuUploadMillisecondsLastFrame);

        if (remainingUploadBudget > 0d)
        {
            GpuUploadMillisecondsLastFrame +=
                _dynamicFragments.UploadPendingMeshes(remainingUploadBudget);
        }

        _dynamicFragments.ReleaseBakedBodies(
            bounds => AreCachedMeshesCurrent(bounds, field));

        if ((_frame % 120) == 0)
        {
            string summary =
                $"frame={_frame} resolution=Step{_activeStep} chunks={_visibleCandidateKeys.Count} visible={_visible.Count} cache={_chunks.Count} " +
                $"macroFrustum={MacroFrustumTestsLastFrame} macroVisible={VisibleMacroRegionsLastFrame} chunkFrustum={FrustumCandidatesLastFrame} " +
                $"emptyChunks={_totalEmptyChunks} mainThreadCopyMs={CpuCaptureMillisecondsLastFrame:0.000} " +
                $"meshBuildMs={WorkerBuildMillisecondsLastFrame:0.000} lastBuildMs={LastWorkerBuildMilliseconds:0.000} " +
                $"queueMs={LastWorkerQueueWaitMilliseconds:0.000} deliveryMs={LastWorkerResultDeliveryMilliseconds:0.000} " +
                $"gpuMs={GpuUploadMillisecondsLastFrame:0.000} gpuPackets={GpuUploadsLastFrame} " +
                $"workers=0 buildQueue={_buildQueue.Count} resultsReady={_completedBuildQueue.Count} gpuReady={_readyUploadQueue.Count} " +
                $"requests={_totalBuildRequests} started={_totalBuildStarted} completed={_totalBuildCompleted} " +
                $"cancelled={_totalBuildCancelled} failed={_totalBuildFailed} stale={_totalStaleResults} " +
                $"repeat={_totalRepeatedBuilds} duplicateReq={_totalDuplicateRequestAttempts} queueFull={_totalQueueSaturated} " +
                $"gpuUploads={_totalGpuUploads} gpuUnloads={_totalGpuUnloads} " +
                $"surfaceCacheHits={_totalIdenticalSurfaceSkips} capCacheHits={_totalIdenticalCutSkips} " +
                $"vertices={LastBuildVertices} triangles={LastBuildTriangles}";
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

        BeginMode3D(camera);

        int draws = 0;
        foreach ((ChunkKey key, ChunkEntry entry) in _visible)
        {
            foreach (Mesh mesh in entry.Surface)
            {
                DrawMesh(mesh, _material, Matrix4x4.Identity);
                draws++;
            }
        }

        SetCutShader(cutEnabled, renderCutHeightMeters);
        _dynamicFragments.Draw(_material);
        EndMode3D();

        DrawCallsLastFrame = draws;
        VisibleChunksLastFrame = _visible.Count;
        if (_logger.IsEnabled(RenderLogMode.FrameSummary))
            _logger.Write(RenderLogMode.FrameSummary, "VOXEL_FRAME", $"elapsedMs={Stopwatch.GetElapsedTime(voxelFrameStarted).TotalMilliseconds:0.000} step={_activeStep} candidates={VisibleCandidatesLastFrame} visible={VisibleChunksLastFrame} draws={DrawCallsLastFrame} cache={CachedChunks} rebuilt={RebuiltChunks} cutRebuilt={RebuiltCutCaps} completed={BuildsCompletedLastFrame} uploads={GpuUploadsLastFrame} workers=0 buildQueue={PendingMeshJobs} results={CompletedMeshResultsPending} uploadQueue={_readyUploadQueue.Count} captureMs={CpuCaptureMillisecondsLastFrame:0.000} meshBuildMs={WorkerBuildMillisecondsLastFrame:0.000} gpuMs={GpuUploadMillisecondsLastFrame:0.000}");
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
            _requestedCutLayer = cutLayer;
            _cutLayerStableFrames = 0;
            return;
        }

        if (_cutLayerStableFrames < CutCapSettleFrames)
            _cutLayerStableFrames++;

        if (_cutLayerStableFrames >= CutCapSettleFrames)
            _stableCutLayer = cutLayer;
    }

    private static int SelectRenderStep(Camera3D camera)
    {
        float distance = Vector3.Distance(camera.Position, camera.Target);
        return distance < 64f ? 1 : distance < 192f ? 2 : 4;
    }

    private void SetRenderStep(int step)
    {
        if (step == _activeStep)
            return;

        _outsideRadiusScratch.Clear();
        foreach (ChunkKey key in _chunks.Keys)
            _outsideRadiusScratch.Add(key);
        foreach (ChunkKey key in _outsideRadiusScratch)
            RemoveCachedChunk(key, "lod-step-changed");

        _buildQueue.Clear();
        while (_completedBuildQueue.TryDequeue(out BuildCompletion completion))
            if (completion.Result is VoxelMeshBuildResult result)
                ReturnBuildCpuData(result);
        _readyUploadQueue.Clear();
        _gpuMeshHashCache.Clear();

        _activeStep = step;
        _logger.Write(RenderLogMode.Detailed, "VOXEL_LOD_CHANGED",
            $"step={_activeStep} voxelSize={ActiveVoxelSizeMeters:0.000}m chunk={RenderChunkSizeMeters:0.0}m cells={ChunkCells}^3 mesher=surface-nets");
    }

    private bool HasPotentialRenderChunkY(
        SparseVoxelField field,
        int macroX,
        int macroZ,
        int renderChunkY,
        int macroCells,
        ulong potentialYMask)
    {
        int firstStorageChunkY = renderChunkY * _activeStep;
        int lastStorageChunkY = firstStorageChunkY + _activeStep;
        for (int storageChunkY = firstStorageChunkY; storageChunkY < lastStorageChunkY; storageChunkY++)
        {
            bool potential = storageChunkY < 64
                ? (potentialYMask & (1UL << storageChunkY)) != 0
                : field.HasPotentialVerticalChunk(macroX, macroZ, storageChunkY, macroCells);
            if (potential)
                return true;
        }

        return false;
    }

    private void CollectVisibleCandidates(
        SparseVoxelField field,
        Camera3D camera,
        int viewportWidth,
        int viewportHeight,
        float maximumViewDistanceMeters,
        List<Candidate> candidates,
        ref int frustumTests,
        ref int macroFrustumTests,
        ref int visibleMacroRegions)
    {
        const float sphereScale = 0.8660254038f;
        const int macroCells = SparseVoxelField.SpatialMacroSize;
        int chunkSpan = ChunkCells * _activeStep;
        int macroPerChunk = chunkSpan / macroCells;
        int storageChunksPerChunk = chunkSpan / SparseVoxelField.ChunkSize;

        float voxelSize = SparseVoxelField.VoxelSizeMeters;
        float chunkSizeMeters = chunkSpan * voxelSize;
        float chunkRadiusMeters = chunkSizeMeters * sphereScale;
        float viewDistance = float.IsFinite(maximumViewDistanceMeters)
            ? MathF.Max(0f, maximumViewDistanceMeters)
            : 0f;
        float viewDistanceSquared = viewDistance * viewDistance;
        float scanRadius = viewDistance + chunkRadiusMeters;
        Vector3 cameraPosition = camera.Position;

        int chunksX = DivideRoundUp(field.Width, chunkSpan);
        int chunksY = DivideRoundUp(field.Height, chunkSpan);
        int chunksZ = DivideRoundUp(field.Depth, chunkSpan);
        if (chunksX == 0 || chunksY == 0 || chunksZ == 0)
            return;

        int firstX = Math.Clamp((int)MathF.Floor((cameraPosition.X - scanRadius) / chunkSizeMeters), 0, chunksX - 1);
        int lastX = Math.Clamp((int)MathF.Floor((cameraPosition.X + scanRadius) / chunkSizeMeters), 0, chunksX - 1);
        int firstY = Math.Clamp((int)MathF.Floor((cameraPosition.Y - scanRadius) / chunkSizeMeters), 0, chunksY - 1);
        int lastY = Math.Clamp((int)MathF.Floor((cameraPosition.Y + scanRadius) / chunkSizeMeters), 0, chunksY - 1);
        int firstZ = Math.Clamp((int)MathF.Floor((cameraPosition.Z - scanRadius) / chunkSizeMeters), 0, chunksZ - 1);
        int lastZ = Math.Clamp((int)MathF.Floor((cameraPosition.Z + scanRadius) / chunkSizeMeters), 0, chunksZ - 1);

        Span<ulong> masks = stackalloc ulong[16];
        Span<int> macroXs = stackalloc int[16];
        Span<int> macroZs = stackalloc int[16];

        for (int z = firstZ; z <= lastZ; z++)
        for (int x = firstX; x <= lastX; x++)
        {
            int maskCount = 0;
            int firstMacroX = x * macroPerChunk;
            int firstMacroZ = z * macroPerChunk;

            for (int mz = 0; mz < macroPerChunk; mz++)
            for (int mx = 0; mx < macroPerChunk; mx++)
            {
                int macroX = firstMacroX + mx;
                int macroZ = firstMacroZ + mz;
                macroXs[maskCount] = macroX;
                macroZs[maskCount] = macroZ;
                masks[maskCount] = field.GetPotentialVerticalChunkMask(
                    macroX, macroZ, macroCells);
                maskCount++;
            }

            for (int y = firstY; y <= lastY; y++)
            {
                int firstStorageY = y * storageChunksPerChunk;
                int endStorageY = firstStorageY + storageChunksPerChunk;
                bool hasPotential = false;

                for (int m = 0; m < maskCount && !hasPotential; m++)
                {
                    ulong mask = masks[m];
                    for (int storageY = firstStorageY; storageY < endStorageY; storageY++)
                    {
                        bool present = storageY < 64
                            ? (mask & (1UL << storageY)) != 0
                            : field.HasPotentialVerticalChunk(
                                macroXs[m], macroZs[m], storageY, macroCells);
                        if (!present)
                            continue;

                        hasPotential = true;
                        break;
                    }
                }

                if (!hasPotential)
                    continue;

                int originX = x * chunkSpan;
                int originY = y * chunkSpan;
                int originZ = z * chunkSpan;
                int sizeX = Math.Min(chunkSpan, field.Width - originX);
                int sizeY = Math.Min(chunkSpan, field.Height - originY);
                int sizeZ = Math.Min(chunkSpan, field.Depth - originZ);

                float nearestDistanceSquared = BoundsNearestDistanceSquared(
                    cameraPosition, originX, originY, originZ, sizeX, sizeY, sizeZ);
                if (nearestDistanceSquared > viewDistanceSquared)
                    continue;

                Vector3 center = new(
                    (originX + sizeX * 0.5f) * voxelSize,
                    (originY + sizeY * 0.5f) * voxelSize,
                    (originZ + sizeZ * 0.5f) * voxelSize);
                float halfX = sizeX * voxelSize * 0.5f;
                float halfY = sizeY * voxelSize * 0.5f;
                float halfZ = sizeZ * voxelSize * 0.5f;
                float radius = MathF.Sqrt(halfX * halfX + halfY * halfY + halfZ * halfZ);

                macroFrustumTests++;
                frustumTests++;
                if (!TerrainFrustum.IsSphereVisible(
                    camera, center, radius,
                    viewportWidth, viewportHeight, viewDistance))
                    continue;

                visibleMacroRegions++;
                candidates.Add(new Candidate(
                    new ChunkKey(x, y, z),
                    center,
                    radius,
                    Vector3.DistanceSquared(center, cameraPosition)));
            }
        }
    }

    private static float BoundsNearestDistanceSquared(
        Vector3 point,
        int originX,
        int originY,
        int originZ,
        int sizeX,
        int sizeY,
        int sizeZ)
    {
        float voxelSize = SparseVoxelField.VoxelSizeMeters;

        float minX = originX * voxelSize;
        float minY = originY * voxelSize;
        float minZ = originZ * voxelSize;
        float maxX = (originX + sizeX) * voxelSize;
        float maxY = (originY + sizeY) * voxelSize;
        float maxZ = (originZ + sizeZ) * voxelSize;

        float dx = point.X < minX ? minX - point.X :
            point.X > maxX ? point.X - maxX : 0f;
        float dy = point.Y < minY ? minY - point.Y :
            point.Y > maxY ? point.Y - maxY : 0f;
        float dz = point.Z < minZ ? minZ - point.Z :
            point.Z > maxZ ? point.Z - maxZ : 0f;

        return dx * dx + dy * dy + dz * dz;
    }

    private void EvictChunksOutsideRadius(
        SparseVoxelField field,
        Vector3 cameraPosition,
        float maximumViewDistanceMeters)
    {
        float radiusSquared = maximumViewDistanceMeters * maximumViewDistanceMeters;
        int chunkSpan = ChunkCells * _activeStep;
        _outsideRadiusScratch.Clear();

        foreach (ChunkKey key in _chunks.Keys)
        {
            int originX = key.X * chunkSpan;
            int originY = key.Y * chunkSpan;
            int originZ = key.Z * chunkSpan;
            float distanceSquared = BoundsNearestDistanceSquared(
                cameraPosition,
                originX, originY, originZ,
                Math.Min(chunkSpan, field.Width - originX),
                Math.Min(chunkSpan, field.Height - originY),
                Math.Min(chunkSpan, field.Depth - originZ));
            if (distanceSquared > radiusSquared)
                _outsideRadiusScratch.Add(key);
        }

        foreach (ChunkKey key in _outsideRadiusScratch)
            RemoveCachedChunk(key, "outside-view-radius");
    }

    private void RemoveCachedChunk(ChunkKey key, string reason)
    {
        if (!_chunks.Remove(key, out ChunkEntry? entry))
            return;

        _gpuMeshHashCache.Remove(key);

        int residentMeshes = entry.Surface.Length + entry.CutCap.Length;
        int partialMeshes = entry.UploadingSurface.Count + entry.UploadingCutCap.Count;

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
        if (_buildQueue.Count >= MaximumPendingMeshJobs)
        {
            _totalQueueSaturated++;
            return;
        }

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
            EnqueuedTimestamp = Stopwatch.GetTimestamp()
        };

        entry.BuildQueued = true;
        entry.RunningBuild = request;
        _buildQueue.Enqueue(request);
        _totalBuildRequests++;
        if (_logger.IsEnabled(RenderLogMode.Detailed))
            _logger.Write(RenderLogMode.Detailed, "MESH_BUILD_REQUEST",
                $"chunk=({key.X},{key.Y},{key.Z}) step={_activeStep} generation={request.Generation} revision={request.RequestRevision} base={request.BaseVersion} palette={request.PaletteVersion} cut={cutLayer} queued={_buildQueue.Count}");
    }

    private void ProcessBuildQueueOnMainThread(SparseVoxelField field)
    {
        Stopwatch budget = Stopwatch.StartNew();
        int built = 0;
        int pendingAtStart = _buildQueue.Count;

        while (built < MaximumMeshBuildsPerFrame &&
               pendingAtStart-- > 0 &&
               (built == 0 || budget.Elapsed.TotalMilliseconds < MeshBuildBudgetMilliseconds))
        {
            if (!_buildQueue.TryDequeue(out BuildRequest? request))
                break;

            ChunkEntry entry = request.Entry;
            bool currentRequest =
                ReferenceEquals(entry.RunningBuild, request) &&
                _chunks.TryGetValue(request.Key, out ChunkEntry? cached) &&
                ReferenceEquals(cached, entry) &&
                request.Generation == entry.BuildGeneration &&
                request.RequestRevision == entry.RequestRevision &&
                request.PaletteVersion == _paletteVersion &&
                request.BaseVersion == field.BaseVersion;

            if (!currentRequest)
            {
                if (ReferenceEquals(entry.RunningBuild, request))
                {
                    entry.RunningBuild = null;
                    entry.BuildQueued = false;
                }
                _totalStaleResults++;
                continue;
            }

            long buildStarted = Stopwatch.GetTimestamp();
            _totalBuildStarted++;
            if (_logger.IsEnabled(RenderLogMode.Detailed))
                _logger.Write(RenderLogMode.Detailed, "MESH_BUILD_STARTED",
                    $"mode=main-thread chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) step={_activeStep} queuedMs={Stopwatch.GetElapsedTime(request.EnqueuedTimestamp, buildStarted).TotalMilliseconds:0.000}");

            VoxelMeshData[] surface = Array.Empty<VoxelMeshData>();
            VoxelMeshData[] cutCap = Array.Empty<VoxelMeshData>();
            try
            {
                IVoxelFieldSource source = field.CreateChunkSnapshot(
                    request.Key.X, request.Key.Y, request.Key.Z, _activeStep, ChunkCells);
                using VoxelChunkBuildInput input = new(
                    source,
                    request.Key.X,
                    request.Key.Y,
                    request.Key.Z,
                    request.CutLayer,
                    _activeStep,
                    ChunkCells);

                long captureStarted = Stopwatch.GetTimestamp();
                input.CaptureAll(source);
                CpuCaptureMillisecondsLastFrame += Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds;

                bool hasPotentialSurface = request.BuildSurface && input.HasPotentialSurface;
                surface = hasPotentialSurface
                    ? VoxelChunkMesher.BuildSurfaceData(input, request.Palette)
                    : Array.Empty<VoxelMeshData>();

                int vertices = SumVertices(surface);
                int triangles = SumTriangles(surface);
                bool hasSurfaceMesh = surface.Length > 0;
                ulong surfaceHash = hasSurfaceMesh ? ComputeMeshHash(surface) : 0UL;
                double buildMs = Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds;

                VoxelMeshBuildResult result = new(
                    surface, cutCap, hasSurfaceMesh, false,
                    request.BuildSurface && !hasSurfaceMesh, request.CutLayer,
                    buildMs, vertices, triangles, surfaceHash, 0UL);

                _completedBuildQueue.Enqueue(new BuildCompletion(
                    request, result, null, false, buildStarted, Stopwatch.GetTimestamp()));
                if (_logger.IsEnabled(RenderLogMode.Detailed))
                    _logger.Write(RenderLogMode.Detailed, "MESH_BUILD_COMPLETED",
                        $"mode=main-thread chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) step={_activeStep} ms={buildMs:0.000} meshes={surface.Length} vertices={vertices} triangles={triangles} hash={surfaceHash:X16}");

                surface = Array.Empty<VoxelMeshData>();
                built++;
            }
            catch (Exception exception)
            {
                ReturnMeshData(surface);
                ReturnMeshData(cutCap);
                _completedBuildQueue.Enqueue(new BuildCompletion(
                    request, null, exception, false, buildStarted, Stopwatch.GetTimestamp()));
                built++;
            }
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

            LastBuildStep = _activeStep;
            LastBuildVertices = build.VertexCount;
            LastBuildTriangles = build.TriangleCount;

            bool uploadSurface = build.HasSurface;
            bool uploadCutCap = build.HasCutCap;

            bool hasGpuHashes = _gpuMeshHashCache.TryGetValue(
                request.Key,
                out ChunkGpuHashes gpuHashes);

            if (build.SurfaceWasEmpty)
            {
                if (entry.Surface.Length > 0)
                {
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
                entry.CutLayer = request.CutLayer;
                entry.CutDirty = false;
                entry.Built = true;
                DiscardLegacyCutCap(request.Key, entry);
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
            entry.SurfaceUploadIndex = 0;
            entry.CutUploadIndex = 0;
            entry.UploadingSurface.Clear();
            entry.UploadingCutCap.Clear();

            EnqueueReadyUpload(request.Key, entry);
        }
    }

    private void UploadReadyMeshes()
    {
        Stopwatch timer = Stopwatch.StartNew();
        GpuUploadMillisecondsLastFrame = 0d;

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

            if (!_chunks.TryGetValue(queued.Key, out ChunkEntry? currentEntry) ||
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

            if (result.HasSurface &&
                entry.SurfaceUploadIndex < result.Surface.Length)
            {
                VoxelMeshData source = result.Surface[entry.SurfaceUploadIndex];
                Mesh mesh;

                try
                {
                    long uploadStarted = Stopwatch.GetTimestamp();
                    mesh = VoxelChunkMesher.UploadOne(source);
                    if (_logger.IsEnabled(RenderLogMode.Detailed))
                        _logger.Write(RenderLogMode.Detailed, "GPU_MESH_UPLOAD", $"chunk=({queued.Key.X},{queued.Key.Y},{queued.Key.Z}) meshIndex={entry.SurfaceUploadIndex + entry.CutUploadIndex} vertices={source.VertexCount} triangles={source.IndexCount / 3} elapsedMs={Stopwatch.GetElapsedTime(uploadStarted).TotalMilliseconds:0.000}");
                }
                catch (Exception exception)
                {
                    _totalBuildFailed++;
                    entry.FailedRevision = request.RequestRevision;
                    _logger.Error("MESH_GPU_UPLOAD_FAILED", $"kind=surface chunk=({queued.Key.X},{queued.Key.Y},{queued.Key.Z}) error={exception}");
                    InvalidatePendingBuild(entry);
                    continue;
                }

                source.ReturnCpuArrays();
                entry.SurfaceUploadIndex++;
                entry.UploadingSurface.Add(mesh);
                GpuUploadsLastFrame++;
                _totalGpuUploads++;
            }
            else if (result.HasCutCap &&
                entry.CutUploadIndex < result.CutCap.Length)
            {
                VoxelMeshData source = result.CutCap[entry.CutUploadIndex];
                Mesh mesh;

                try
                {
                    long uploadStarted = Stopwatch.GetTimestamp();
                    mesh = VoxelChunkMesher.UploadOne(source);
                    if (_logger.IsEnabled(RenderLogMode.Detailed))
                        _logger.Write(RenderLogMode.Detailed, "GPU_MESH_UPLOAD", $"chunk=({queued.Key.X},{queued.Key.Y},{queued.Key.Z}) meshIndex={entry.SurfaceUploadIndex + entry.CutUploadIndex} vertices={source.VertexCount} triangles={source.IndexCount / 3} elapsedMs={Stopwatch.GetElapsedTime(uploadStarted).TotalMilliseconds:0.000}");
                }
                catch (Exception exception)
                {
                    _totalBuildFailed++;
                    entry.FailedRevision = request.RequestRevision;
                    _logger.Error("MESH_GPU_UPLOAD_FAILED", $"kind=cut-cap chunk=({queued.Key.X},{queued.Key.Y},{queued.Key.Z}) error={exception}");
                    InvalidatePendingBuild(entry);
                    continue;
                }

                source.ReturnCpuArrays();
                entry.CutUploadIndex++;
                entry.UploadingCutCap.Add(mesh);
                GpuUploadsLastFrame++;
                _totalGpuUploads++;
            }

            bool surfaceComplete =
                !result.HasSurface ||
                entry.SurfaceUploadIndex >= result.Surface.Length;
            bool cutComplete =
                !result.HasCutCap ||
                entry.CutUploadIndex >= result.CutCap.Length;

            if (!surfaceComplete || !cutComplete)
            {
                EnqueueReadyUpload(queued.Key, entry);
                continue;
            }

            if (result.HasSurface)
            {
                Mesh[] oldMeshes = entry.Surface;
                entry.Surface = entry.UploadingSurface.ToArray();
                entry.UploadingSurface.Clear();

                if (oldMeshes.Length > 0)
                    _logger.Write(RenderLogMode.Detailed, "GPU_MESH_UNLOAD", $"kind=surface chunk=({queued.Key.X},{queued.Key.Y},{queued.Key.Z}) count={oldMeshes.Length} reason=surface-replaced");
                foreach (Mesh oldMesh in oldMeshes)
                {
                    UnloadMesh(oldMesh);
                    _totalGpuUnloads++;
                }

                SetSurfaceGpuHash(queued.Key, result.SurfaceHash);
                entry.BaseVersion = request.BaseVersion;
                entry.PaletteVersion = request.PaletteVersion;
                entry.SurfaceDirty = false;
                entry.CutLayer = request.CutLayer;
                entry.CutDirty = false;
                entry.Built = true;

                DiscardLegacyCutCap(queued.Key, entry);

                RebuiltChunks++;
            }

            if (result.HasCutCap)
            {
                Mesh[] oldMeshes = entry.CutCap;
                entry.CutCap = entry.UploadingCutCap.ToArray();
                entry.UploadingCutCap.Clear();

                if (oldMeshes.Length > 0)
                    _logger.Write(RenderLogMode.Detailed, "GPU_MESH_UNLOAD", $"kind=cut-cap chunk=({queued.Key.X},{queued.Key.Y},{queued.Key.Z}) count={oldMeshes.Length} reason=cut-cap-replaced");
                foreach (Mesh oldMesh in oldMeshes)
                {
                    UnloadMesh(oldMesh);
                    _totalGpuUnloads++;
                }

                SetCutCapGpuHash(queued.Key, result.CutCapHash, result.CutLayer);
                entry.CutLayer = result.CutLayer;
                entry.CutDirty = false;
                RebuiltCutCaps++;
            }

            entry.ReadyBuild = null;
            entry.ReadyRequest = null;
            entry.SurfaceUploadIndex = 0;
            entry.CutUploadIndex = 0;
        }

        GpuUploadMillisecondsLastFrame = timer.Elapsed.TotalMilliseconds;
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
        entry.RunningBuild = null;

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

        if (entry.UploadingSurface.Count + entry.UploadingCutCap.Count > 0)
            _logger.Write(RenderLogMode.Detailed, "GPU_MESH_UNLOAD", $"partialSurface={entry.UploadingSurface.Count} partialCutCap={entry.UploadingCutCap.Count} reason=build-invalidated");
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

    private static bool HasPendingWork(ChunkEntry entry) =>
        entry.RunningBuild != null ||
        entry.BuildQueued ||
        entry.ReadyBuild.HasValue ||
        entry.UploadingSurface.Count > 0 ||
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
        int chunkSpan = ChunkCells * _activeStep;
        foreach (ChunkKey key in _visibleCandidateKeys)
        {
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
        int chunkSpan = ChunkCells * _activeStep;
        int halo = _activeStep;
        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
        {
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
        if (_disposed)
            return;

        _disposed = true;
        if (_observedField != null)
            _observedField.Changed -= OnVoxelFieldChanged;
        _observedField = null;

        _buildQueue.Clear();
        while (_completedBuildQueue.TryDequeue(out BuildCompletion completed))
            if (completed.Result is VoxelMeshBuildResult result)
                ReturnBuildCpuData(result);

        foreach (ChunkEntry entry in _chunks.Values)
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
            if (gpuMeshCount > 0)
                _logger.Write(RenderLogMode.Detailed, "GPU_MESH_UNLOAD", $"reason=renderer-dispose surface={entry.Surface.Length} cut={entry.CutCap.Length} partialSurface={entry.UploadingSurface.Count} partialCut={entry.UploadingCutCap.Count}");
            entry.Dispose();
        }

        _dynamicFragments.Dispose();
        _chunks.Clear();
        _gpuMeshHashCache.Clear();
        _readyUploadQueue.Clear();

        UnloadMaterial(_material);

        _logger.Write(RenderLogMode.ErrorsOnly, "VOXEL_RENDERER_STOP",
            $"requests={_totalBuildRequests} started={_totalBuildStarted} completed={_totalBuildCompleted} cancelled={_totalBuildCancelled} failed={_totalBuildFailed} stale={_totalStaleResults} gpuUploads={_totalGpuUploads} gpuUnloads={_totalGpuUnloads} cache={_chunks.Count}");
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
