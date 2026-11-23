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
    private const int FixedRenderStep = 4;
    private const int MaximumCachedChunks = 6000;
    private const bool EnableAutomaticChunkCacheEviction = false;
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
        public float PriorityDistanceSquared = float.PositiveInfinity;
        public bool Built;

        public int RequestRevision;
        public int FailedRevision = int.MinValue;
        public bool BuildQueued;
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

    private readonly Dictionary<ChunkKey, ChunkEntry> _chunks = new();
    private readonly Dictionary<ChunkKey, ChunkGpuHashes> _gpuMeshHashCache = new();
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
    private int _activeStep = FixedRenderStep;
    private int _requestedCutLayer = int.MinValue;
    private int _stableCutLayer = int.MinValue;
    private int _cutLayerStableFrames;

    // A moving section plane changes every 0.1 m. Rebuilding the cap meshes
    // for each intermediate layer creates continuous CPU/GPU work while the
    // user drags the slice control. Wait until the selected layer settles.
    private const int CutCapSettleFrames = 8;

    public int ActiveRenderCellSize => _activeStep;
    public float ActiveVoxelSizeMeters => _activeStep * SparseVoxelField.VoxelSizeMeters;
    public bool IsUsingCoarseLod => false;
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
            $"fixedStep={FixedRenderStep};workers={MaximumMeshWorkers};queue={MaximumPendingMeshJobs};gpuUploadBudgetMs={GpuUploadBudgetMilliseconds:0.0};queue=ConcurrentQueue;signal=SemaphoreSlim");
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
        const int step = FixedRenderStep;
        _activeStep = step;

        float chunkSizeMeters = ChunkCells * step * SparseVoxelField.VoxelSizeMeters;
        int chunksX = DivideRoundUp(field.Width, ChunkCells * step);
        int chunksY = DivideRoundUp(field.Height, ChunkCells * step);
        int chunksZ = DivideRoundUp(field.Depth, ChunkCells * step);

        // Do not cap candidate count or pre-trim by an artificial camera radius.
        // Traverse the complete chunk grid and let the real camera frustum decide visibility.
        List<Candidate> candidates = new();
        int frustumCandidateCount = 0;
        float chunkRadius = chunkSizeMeters * 0.8660254f;

        for (int chunkZ = 0; chunkZ < chunksZ; chunkZ++)
        for (int chunkY = 0; chunkY < chunksY; chunkY++)
        for (int chunkX = 0; chunkX < chunksX; chunkX++)
        {
            Vector3 center = new(
                (chunkX + 0.5f) * chunkSizeMeters,
                (chunkY + 0.5f) * chunkSizeMeters,
                (chunkZ + 0.5f) * chunkSizeMeters);

            if (!TerrainFrustum.IsSphereVisible(
                camera,
                center,
                chunkRadius,
                viewportWidth,
                viewportHeight,
                maximumViewDistanceMeters))
                continue;

            frustumCandidateCount++;

            float dx = center.X - camera.Target.X;
            float dz = center.Z - camera.Target.Z;
            float priorityY = cutEnabled
                ? cutHeightMeters
                : Math.Clamp(
                    camera.Target.Y,
                    0f,
                    field.Height * SparseVoxelField.VoxelSizeMeters);
            float dy = center.Y - priorityY;
            float priorityDistanceSquared = dx * dx + dy * dy + dz * dz;

            candidates.Add(new Candidate(
                new ChunkKey(step, chunkX, chunkY, chunkZ),
                center,
                chunkRadius,
                priorityDistanceSquared));
        }

        // Nearest visible chunks enter the bounded worker queue first.
        // The candidate list itself is never truncated.
        candidates.Sort(static (a, b) =>
            a.DistanceSquared.CompareTo(b.DistanceSquared));

        FrustumCandidatesLastFrame = frustumCandidateCount;
        CandidateOverflowLastFrame = 0;
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

            bool hasPendingWork = HasPendingWork(entry);
            bool hasQueueCapacity =
                _buildQueue.Count < MaximumPendingMeshJobs;
            if ((surfaceStale || needsCutCap) &&
                !hasPendingWork &&
                hasQueueCapacity)
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

            candidateEntries.Add((candidate, entry));
        }

        CancelQueuedBuildsOutsideView();

        foreach ((Candidate candidate, ChunkEntry entry) in candidateEntries)
        {
            if (entry.Built)
                AddVisible(candidate.Key, entry, visibleKeys);
        }

        UploadReadyMeshes();
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

        // Only enforce the hard cache ceiling under actual cache pressure.
        // Camera movement alone never unloads an otherwise unchanged VAO.
        if (EnableAutomaticChunkCacheEviction &&
            _chunks.Count > MaximumCachedChunks &&
            (_frame % 30) == 0)
        {
            EvictUnusedChunks();
        }

        _dynamicFragments.ReleaseBakedBodies(
            bounds => AreCachedMeshesCurrent(bounds, field, step));

        if ((_frame % 120) == 0)
        {
            string summary =
                $"frame={_frame} fixedStep={step} frustumCandidates={FrustumCandidatesLastFrame} selected={_visibleCandidateKeys.Count} overflow={CandidateOverflowLastFrame} visible={_visible.Count} cache={_chunks.Count} emptyChunks={_totalEmptyChunks} " +
                $"snapshotMs={CpuCaptureMillisecondsLastFrame:0.000} workerMs={WorkerBuildMillisecondsLastFrame:0.000} " +
                $"lastWorkerMs={LastWorkerBuildMilliseconds:0.000} queueMs={LastWorkerQueueWaitMilliseconds:0.000} deliveryMs={LastWorkerResultDeliveryMilliseconds:0.000} " +
                $"gpuMs={GpuUploadMillisecondsLastFrame:0.000} gpuPackets={GpuUploadsLastFrame} " +
                $"workers={RunningMeshJobs} queue={_buildQueue.Count} resultsReady={_completedBuildQueue.Count} " +
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
        while (_completedBuildQueue.TryPeek(out BuildCompletion pending))
        {
            BuildRequest pendingRequest = pending.Request;
            bool pendingIsCurrent =
                _chunks.TryGetValue(pendingRequest.Key, out ChunkEntry? pendingEntry) &&
                ReferenceEquals(pendingEntry, pendingRequest.Entry) &&
                pendingRequest.RequestRevision == pendingRequest.Entry.RequestRevision &&
                pendingRequest.PaletteVersion == _paletteVersion &&
                pendingRequest.BaseVersion == field.BaseVersion;

            // A chunk can own only one staged build. Leave later completions in
            // the concurrent queue until its current upload finishes on a later frame.
            if (pending.Result.HasValue &&
                pendingIsCurrent &&
                pendingRequest.Entry.ReadyBuild.HasValue)
                break;

            if (!_completedBuildQueue.TryDequeue(out BuildCompletion completion))
                continue;

            BuildRequest request = completion.Request;
            ChunkEntry entry = request.Entry;
            request.Cancellation.Dispose();

            if (ReferenceEquals(entry.RunningBuild, request))
            {
                entry.RunningBuild = null;
                entry.BuildQueued = false;
            }

            if (completion.Cancelled)
            {
                _totalBuildCancelled++;
                _logger.Write(
                    "MESH_CANCELLED",
                    $"step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) rev={request.RequestRevision}");
                continue;
            }

            if (completion.Error != null)
            {
                _totalBuildFailed++;
                if (request.RequestRevision == entry.RequestRevision)
                    entry.FailedRevision = request.RequestRevision;
                _logger.Write(
                    "MESH_FAILED",
                    $"step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) error=\"{completion.Error.Message}\"");
                continue;
            }

            if (!_chunks.TryGetValue(request.Key, out ChunkEntry? current) ||
                !ReferenceEquals(current, entry) ||
                request.RequestRevision != entry.RequestRevision ||
                request.PaletteVersion != _paletteVersion ||
                request.BaseVersion != field.BaseVersion)
            {
                if (completion.Result is VoxelMeshBuildResult staleBuild)
                {
                    ReturnMeshData(staleBuild.Surface);
                    ReturnMeshData(staleBuild.CutCap);
                }

                _totalStaleResults++;
                _logger.Write(
                    "MESH_STALE",
                    $"step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) requestRev={request.RequestRevision} currentRev={entry.RequestRevision} base={request.BaseVersion}/{field.BaseVersion}");
                continue;
            }

            VoxelMeshBuildResult build = completion.Result!.Value;
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
                _totalEmptyChunks++;
                RebuiltChunks++;
            }
            else if (uploadSurface &&
                entry.Built &&
                hasGpuHashes &&
                gpuHashes.HasSurface &&
                gpuHashes.SurfaceHash == build.SurfaceHash)
            {
                // Identical geometry is already resident in VRAM.
                // Release only this build's temporary CPU buffers.
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

            if (!uploadSurface && !uploadCutCap)
            {
                // Idempotent ReturnCpuArrays makes this a final ownership guard:
                // cache-hit buffers are returned above, and no build buffer is retained.
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

    private void UploadReadyMeshes()
    {
        Stopwatch timer = Stopwatch.StartNew();
        GpuUploadMillisecondsLastFrame = 0d;

        // Visible chunks are uploaded first; there is no speculative LOD prewarm.
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
            if (GpuUploadsLastFrame >= MaximumGpuMeshUploadsPerFrame ||
                timer.Elapsed.TotalMilliseconds >= GpuUploadBudgetMilliseconds)
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
                while (entry.SurfaceUploadIndex < result.Surface.Length &&
                    GpuUploadsLastFrame < MaximumGpuMeshUploadsPerFrame &&
                    timer.Elapsed.TotalMilliseconds < GpuUploadBudgetMilliseconds)
                {
                    int meshIndex = entry.SurfaceUploadIndex;
                    VoxelMeshData source = result.Surface[meshIndex];
                    Mesh mesh = VoxelChunkMesher.UploadOne(source);
                    source.ReturnCpuArrays();
                    entry.SurfaceUploadIndex = meshIndex + 1;
                    entry.UploadingSurface.Add(mesh);
                    GpuUploadsLastFrame++;
                    _totalGpuUploads++;
                    _logger.Write(
                        "MESH_GPU_UPLOAD",
                        $"kind=surface step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) vertices={result.Surface[entry.SurfaceUploadIndex - 1].VertexCount}");
                    if (timer.Elapsed.TotalMilliseconds >= GpuUploadBudgetMilliseconds)
                        break;
                }

                if (entry.SurfaceUploadIndex < result.Surface.Length ||
                    timer.Elapsed.TotalMilliseconds >= GpuUploadBudgetMilliseconds)
                    break;
            }

            if (result.HasCutCap)
            {
                while (entry.CutUploadIndex < result.CutCap.Length &&
                    GpuUploadsLastFrame < MaximumGpuMeshUploadsPerFrame &&
                    timer.Elapsed.TotalMilliseconds < GpuUploadBudgetMilliseconds)
                {
                    int meshIndex = entry.CutUploadIndex;
                    VoxelMeshData source = result.CutCap[meshIndex];
                    Mesh mesh = VoxelChunkMesher.UploadOne(source);
                    source.ReturnCpuArrays();
                    entry.CutUploadIndex = meshIndex + 1;
                    entry.UploadingCutCap.Add(mesh);
                    GpuUploadsLastFrame++;
                    _totalGpuUploads++;
                    _logger.Write(
                        "MESH_GPU_UPLOAD",
                        $"kind=cut-cap step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) layer={result.CutLayer} vertices={result.CutCap[entry.CutUploadIndex - 1].VertexCount}");
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
                if (previousSurface.Length > 0)
                {
                    _logger.Write(
                        "MESH_GPU_EVICT",
                        $"reason=surface-replaced step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) meshes={previousSurface.Length}");
                }
                foreach (Mesh mesh in previousSurface)
                {
                    UnloadMesh(mesh);
                    _totalGpuUnloads++;
                }

                SetSurfaceGpuHash(request.Key, result.SurfaceHash);
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
                if (previousCut.Length > 0)
                {
                    _logger.Write(
                        "MESH_GPU_EVICT",
                        $"reason=cut-cap-replaced step={request.Key.Step} chunk=({request.Key.X},{request.Key.Y},{request.Key.Z}) layer={result.CutLayer} meshes={previousCut.Length}");
                }
                foreach (Mesh mesh in previousCut)
                {
                    UnloadMesh(mesh);
                    _totalGpuUnloads++;
                }

                SetCutCapGpuHash(request.Key, result.CutCapHash, result.CutLayer);
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

    private void InvalidatePendingBuild(ChunkEntry entry)
    {
        BuildRequest? obsolete = entry.RunningBuild;
        if (obsolete != null)
        {
            obsolete.Cancellation.Cancel();
            entry.RunningBuild = null;
        }
        entry.BuildQueued = false;
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

    private void CancelQueuedBuildsOutsideView()
    {
        foreach (ChunkEntry entry in _chunks.Values)
        {
            BuildRequest? request = entry.RunningBuild;
            if (request == null ||
                Volatile.Read(ref request.Started) != 0 ||
                _visibleCandidateKeys.Contains(request.Key))
                continue;

            InvalidatePendingBuild(entry);
        }
    }

    private static bool HasPendingWork(ChunkEntry entry) =>
        entry.RunningBuild != null ||
        entry.BuildQueued ||
        entry.ReadyBuild.HasValue ||
        entry.UploadingSurface.Count > 0 ||
        entry.UploadingCutCap.Count > 0;

    private void EvictUnusedChunks()
    {
        // Keep all unchanged GPU meshes resident until actual cache pressure.
        // Age alone is never a reason to unload a chunk: scrolling away and
        // back must not cause a VAO upload cycle for unchanged voxel data.
        if (_chunks.Count <= MaximumCachedChunks)
            return;

        HashSet<ChunkKey> protectedKeys = new(_visibleCandidateKeys);
        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
        {
            if (HasPendingWork(entry))
                protectedKeys.Add(key);
        }

        int excess = _chunks.Count - MaximumCachedChunks;
        foreach ((ChunkKey key, ChunkEntry entry) in _chunks
            .Where(pair => !protectedKeys.Contains(pair.Key))
            .OrderBy(pair => pair.Value.LastSeenFrame)
            .Take(excess)
            .ToArray())
        {
            if (!_chunks.Remove(key))
                continue;

            _gpuMeshHashCache.Remove(key);

            if (entry.ReadyBuild is VoxelMeshBuildResult readyBuild)
                ReturnBuildCpuData(readyBuild);

            int gpuMeshCount =
                entry.Surface.Length +
                entry.CutCap.Length +
                entry.UploadingSurface.Count +
                entry.UploadingCutCap.Count;
            _totalGpuUnloads += gpuMeshCount;
            _logger.Write(
                "MESH_GPU_EVICT",
                $"reason=hard-cache-limit step={key.Step} chunk=({key.X},{key.Y},{key.Z}) meshes={gpuMeshCount}");
            entry.Dispose();
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
