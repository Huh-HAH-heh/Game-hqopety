using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Core.Map;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// Main-thread voxel renderer. Meshes are built and uploaded synchronously;
/// unchanged GPU meshes stay resident in a bounded cache.
/// </summary>
internal sealed class VoxelTerrainRenderer : IDisposable
{
    private const int ChunkCells = SparseVoxelField.ChunkSize;
    private const int MaximumCachedChunks = 4096;
    private const int MaximumBuildsPerFrame = 8;
    private const double MeshBuildBudgetMilliseconds = 4.0;
    private const int CutSettleFrames = 8;

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
            float faceAO = n.y > 0.5 ? 1.0 :
                n.y < -0.5 ? 0.5 :
                abs(n.z) > 0.5 ? 0.75 : 0.85;
            float diffuse = max(dot(n, normalize(uSunDirection)), 0.0);
            float lighting = max(uAmbientLight + diffuse * uSunIntensity, 0.05);
            finalColor = vec4(fragColor.rgb * faceAO * lighting, fragColor.a);
        }
        """;

    private readonly record struct ChunkKey(int X, int Y, int Z);
    private readonly record struct Candidate(
        ChunkKey Key,
        Vector3 Center,
        float Radius,
        float DistanceSquared);

    private sealed class ChunkEntry
    {
        public Mesh[] Surface = Array.Empty<Mesh>();
        public long BaseVersion = long.MinValue;
        public int PaletteVersion = int.MinValue;
        public int CutLayer = int.MinValue;
        public long LastSeenFrame;
        public int Revision;
        public int FailedRevision = int.MinValue;
        public bool Dirty = true;
        public bool Built;
    }

    private readonly Dictionary<ChunkKey, ChunkEntry> _chunks = new();
    private readonly List<Candidate> _candidates = new(1024);
    private readonly List<(ChunkKey Key, ChunkEntry Entry)> _visible = new(1024);
    private readonly HashSet<ChunkKey> _visibleKeys = new();
    private readonly DynamicVoxelFragmentRenderer _dynamicFragments = new();
    private readonly TerrainPipelineLogger _logger;

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
    private long _totalBuildFailed;
    private long _totalGpuUploads;
    private long _totalGpuUnloads;
    private long _totalIdenticalSurfaceSkips;
    private long _totalEmptyChunks;

    public int ActiveRenderCellSize => 1;
    public float ActiveVoxelSizeMeters => SparseVoxelField.VoxelSizeMeters;
    public float RenderChunkSizeMeters => ChunkCells * SparseVoxelField.VoxelSizeMeters;
    public bool IsUsingCoarseLod => false;
    public int Step1ChunksLastFrame { get; private set; }
    public int Step4ChunksLastFrame => 0;
    public int Step16ChunksLastFrame => 0;
    public int DrawCallsLastFrame { get; private set; }
    public int VisibleChunksLastFrame { get; private set; }
    public int RebuiltChunks { get; private set; }
    public int RebuiltCutCaps => 0;
    public int LodPrewarmBuilds => 0;

    public double CpuCaptureMillisecondsLastFrame { get; private set; }
    public double GpuUploadMillisecondsLastFrame { get; private set; }
    public int RunningMeshJobs => 0;
    public double CpuCaptureMilliseconds => CpuCaptureMillisecondsLastFrame;
    public double GpuUploadMilliseconds => GpuUploadMillisecondsLastFrame;
    public int PendingMeshJobs => 0;
    public int CompletedMeshResultsPending => 0;
    public double WorkerBuildMillisecondsLastFrame { get; private set; }
    public double LastWorkerBuildMilliseconds { get; private set; }
    public double LastWorkerQueueWaitMilliseconds => 0d;
    public double LastWorkerResultDeliveryMilliseconds => 0d;
    public int BuildsCompletedLastFrame { get; private set; }
    public int LastBuildStep { get; private set; } = 1;
    public int LastBuildVertices { get; private set; }
    public int LastBuildTriangles { get; private set; }
    public int GpuUploadsLastFrame { get; private set; }

    public long TotalBuildRequests => _totalBuildRequests;
    public long TotalBuildStarted => _totalBuildStarted;
    public long TotalBuildCompleted => _totalBuildCompleted;
    public long TotalBuildCancelled => 0;
    public long TotalBuildFailed => _totalBuildFailed;
    public long TotalStaleResults => 0;
    public long TotalDuplicateRequestAttempts => 0;
    public long TotalQueueSaturated => 0;
    public long TotalRepeatedBuilds => 0;
    public long TotalGpuUploads => _totalGpuUploads;
    public long TotalGpuUnloads => _totalGpuUnloads;
    public long TotalIdenticalSurfaceSkips => _totalIdenticalSurfaceSkips;
    public long TotalIdenticalCutSkips => 0;
    public long TotalEmptyChunks => _totalEmptyChunks;

    public int FrustumCandidatesLastFrame { get; private set; }
    public int MacroFrustumTestsLastFrame { get; private set; }
    public int VisibleMacroRegionsLastFrame { get; private set; }
    public int CandidateOverflowLastFrame => 0;
    public string DiagnosticsLogPath => _logger.LogPath;
    public int VisibleCandidatesLastFrame => _visibleKeys.Count;
    public int CachedChunks => _chunks.Count;
    public int DynamicFragmentCount => _dynamicFragments.Count;
    public bool IsMergingFragments => _dynamicFragments.IsMerging;

    public VoxelTerrainRenderer(TerrainPalette palette)
    {
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));

        _shader = LoadShaderFromMemory(VertexShader, FragmentShader);
        if (!IsShaderValid(_shader))
            throw new InvalidOperationException("The voxel surface shader could not be created.");

        _cutEnabledLocation = GetShaderLocation(_shader, "uCutEnabled");
        _cutHeightLocation = GetShaderLocation(_shader, "uCutHeight");
        _sunDirectionLocation = GetShaderLocation(_shader, "uSunDirection");
        _ambientLightLocation = GetShaderLocation(_shader, "uAmbientLight");
        _sunIntensityLocation = GetShaderLocation(_shader, "uSunIntensity");

        if (_cutEnabledLocation < 0 || _cutHeightLocation < 0 ||
            _sunDirectionLocation < 0 || _ambientLightLocation < 0 ||
            _sunIntensityLocation < 0)
        {
            UnloadShader(_shader);
            throw new InvalidOperationException("The voxel shader is missing a required uniform.");
        }

        _material = LoadMaterialDefault();
        _material.Shader = _shader;
        _logger = new TerrainPipelineLogger();
        _logger.Write(
            "RENDERER_START",
            $"mode=main-thread;chunk={ChunkCells};cache={MaximumCachedChunks};buildsPerFrame={MaximumBuildsPerFrame};buildBudgetMs={MeshBuildBudgetMilliseconds:0.0}");
        Console.WriteLine($"[TERRAIN] Pipeline diagnostics: {_logger.LogPath}");
    }

    public void SetPalette(TerrainPalette palette)
    {
        ThrowIfDisposed();
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));
        _paletteVersion++;

        foreach (ChunkEntry entry in _chunks.Values)
        {
            entry.Dirty = true;
            entry.Revision++;
            entry.FailedRevision = int.MinValue;
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
        CpuCaptureMillisecondsLastFrame = 0d;
        GpuUploadMillisecondsLastFrame = 0d;
        WorkerBuildMillisecondsLastFrame = 0d;
        BuildsCompletedLastFrame = 0;
        GpuUploadsLastFrame = 0;
        LastBuildVertices = 0;
        LastBuildTriangles = 0;

        int cutLayer = cutEnabled
            ? (int)MathF.Round(cutHeightMeters / SparseVoxelField.VoxelSizeMeters)
            : -1;
        UpdateStableCutLayer(cutEnabled, cutLayer);
        int targetCutLayer = cutEnabled && _cutLayerStableFrames >= CutSettleFrames
            ? _stableCutLayer
            : int.MinValue;

        _candidates.Clear();
        int frustumTests = 0;
        int macroTests = 0;
        int visibleMacros = 0;
        CollectVisibleCandidates(
            field,
            camera,
            viewportWidth,
            viewportHeight,
            maximumViewDistanceMeters,
            _candidates,
            ref frustumTests,
            ref macroTests,
            ref visibleMacros);
        _candidates.Sort(static (a, b) => a.DistanceSquared.CompareTo(b.DistanceSquared));

        FrustumCandidatesLastFrame = frustumTests;
        MacroFrustumTestsLastFrame = macroTests;
        VisibleMacroRegionsLastFrame = visibleMacros;
        Step1ChunksLastFrame = _candidates.Count;

        _visibleKeys.Clear();
        foreach (Candidate candidate in _candidates)
            _visibleKeys.Add(candidate.Key);

        _visible.Clear();
        Stopwatch buildBudget = Stopwatch.StartNew();
        int buildsThisFrame = 0;

        foreach (Candidate candidate in _candidates)
        {
            if (!TryGetOrCreateEntry(candidate.Key, out ChunkEntry? entry))
                continue;

            entry.LastSeenFrame = _frame;
            bool stale = !entry.Built || entry.Dirty ||
                entry.BaseVersion != field.BaseVersion ||
                entry.PaletteVersion != _paletteVersion ||
                entry.CutLayer != targetCutLayer;

            if (!stale)
            {
                _totalIdenticalSurfaceSkips++;
            }
            else if (entry.FailedRevision != entry.Revision &&
                     (buildsThisFrame == 0 ||
                      (buildsThisFrame < MaximumBuildsPerFrame &&
                       buildBudget.Elapsed.TotalMilliseconds < MeshBuildBudgetMilliseconds)))
            {
                buildsThisFrame++;
                BuildChunk(field, candidate.Key, entry, targetCutLayer);
            }

            if (entry.Built && entry.Surface.Length > 0 &&
                !entry.Dirty &&
                entry.BaseVersion == field.BaseVersion &&
                entry.PaletteVersion == _paletteVersion &&
                entry.CutLayer == targetCutLayer)
            {
                _visible.Add((candidate.Key, entry));
            }
        }

        _dynamicFragments.ReleaseBakedBodies(bounds => AreCachedMeshesCurrent(bounds, field));

        if ((_frame % 120) == 0)
        {
            string summary =
                $"frame={_frame} candidates={_candidates.Count} visible={_visible.Count} cache={_chunks.Count} " +
                $"builds={BuildsCompletedLastFrame} meshMs={WorkerBuildMillisecondsLastFrame:0.000} " +
                $"captureMs={CpuCaptureMillisecondsLastFrame:0.000} gpuMs={GpuUploadMillisecondsLastFrame:0.000} " +
                $"gpuUploads={GpuUploadsLastFrame} totalUploads={_totalGpuUploads} totalUnloads={_totalGpuUnloads} " +
                $"empty={_totalEmptyChunks} vertices={LastBuildVertices} triangles={LastBuildTriangles}";
            _logger.Write("FRAME_SUMMARY", summary);
            Console.WriteLine($"[TERRAIN][PERF] {summary}");
        }

        float renderCutHeight = cutEnabled && targetCutLayer >= 0
            ? targetCutLayer * SparseVoxelField.VoxelSizeMeters
            : cutHeightMeters;
        SetLighting();
        SetCutShader(cutEnabled, renderCutHeight);

        float worldRadius = 0.5f * MathF.Sqrt(
            field.Width * field.Width +
            field.Depth * field.Depth +
            field.Height * field.Height) * SparseVoxelField.VoxelSizeMeters;
        float cameraDistance = Vector3.Distance(camera.Position, camera.Target);
        Rlgl.SetClipPlanes(
            Math.Clamp(cameraDistance * 0.002f, 0.02f, 2f),
            MathF.Max(1000f, camera.Position.Length() + worldRadius + 100f));

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

        SetCutShader(cutEnabled, renderCutHeight);
        _dynamicFragments.Draw(_material);
        EndMode3D();

        DrawCallsLastFrame = draws;
        VisibleChunksLastFrame = _visible.Count;
    }

    private void BuildChunk(
        SparseVoxelField field,
        ChunkKey key,
        ChunkEntry entry,
        int cutLayer)
    {
        _totalBuildRequests++;
        _totalBuildStarted++;
        Stopwatch buildTimer = Stopwatch.StartNew();
        VoxelMeshData[] data = Array.Empty<VoxelMeshData>();
        List<Mesh> nextMeshes = new(2);
        bool uploaded = false;

        try
        {
            using VoxelChunkBuildInput input = new(
                field,
                key.X,
                key.Y,
                key.Z,
                cutLayer);

            Stopwatch captureTimer = Stopwatch.StartNew();
            input.CaptureAll(field);
            captureTimer.Stop();
            CpuCaptureMillisecondsLastFrame += captureTimer.Elapsed.TotalMilliseconds;

            Stopwatch meshTimer = Stopwatch.StartNew();
            data = input.HasPotentialSurface
                ? VoxelChunkMesher.BuildSurfaceData(input, _palette)
                : Array.Empty<VoxelMeshData>();
            meshTimer.Stop();
            WorkerBuildMillisecondsLastFrame += meshTimer.Elapsed.TotalMilliseconds;
            LastWorkerBuildMilliseconds = meshTimer.Elapsed.TotalMilliseconds;

            int vertices = 0;
            int triangles = 0;
            Stopwatch uploadTimer = Stopwatch.StartNew();
            foreach (VoxelMeshData meshData in data)
            {
                vertices += meshData.VertexCount;
                triangles += meshData.TriangleCount;
                nextMeshes.Add(VoxelChunkMesher.UploadOne(meshData));
                GpuUploadsLastFrame++;
                _totalGpuUploads++;
            }
            uploadTimer.Stop();
            GpuUploadMillisecondsLastFrame += uploadTimer.Elapsed.TotalMilliseconds;

            Mesh[] previous = entry.Surface;
            entry.Surface = nextMeshes.ToArray();
            nextMeshes.Clear();
            entry.BaseVersion = field.BaseVersion;
            entry.PaletteVersion = _paletteVersion;
            entry.CutLayer = cutLayer;
            entry.Dirty = false;
            entry.Built = true;
            entry.FailedRevision = int.MinValue;

            foreach (Mesh mesh in previous)
            {
                UnloadMesh(mesh);
                _totalGpuUnloads++;
            }

            LastBuildVertices = vertices;
            LastBuildTriangles = triangles;
            RebuiltChunks++;
            BuildsCompletedLastFrame++;
            _totalBuildCompleted++;
            uploaded = true;

            if (entry.Surface.Length == 0)
                _totalEmptyChunks++;
        }
        catch (Exception exception)
        {
            entry.FailedRevision = entry.Revision;
            _totalBuildFailed++;
            _logger.Write("MESH_BUILD_FAILED",
                $"chunk={key.X},{key.Y},{key.Z};error={exception.GetType().Name}:{exception.Message}");
            Console.WriteLine($"[TERRAIN] Mesh build failed at {key}: {exception.Message}");
        }
        finally
        {
            foreach (VoxelMeshData meshData in data)
                meshData.ReturnCpuArrays();

            if (!uploaded)
            {
                foreach (Mesh mesh in nextMeshes)
                {
                    UnloadMesh(mesh);
                    _totalGpuUnloads++;
                }
            }
        }

        buildTimer.Stop();
        if (buildTimer.Elapsed.TotalMilliseconds > 0d)
            WorkerBuildMillisecondsLastFrame += 0d;
    }

    private bool TryGetOrCreateEntry(ChunkKey key, out ChunkEntry? entry)
    {
        if (_chunks.TryGetValue(key, out entry))
            return true;

        if (_chunks.Count >= MaximumCachedChunks && !EvictLeastRecentlySeen())
        {
            entry = null;
            return false;
        }

        entry = new ChunkEntry();
        _chunks.Add(key, entry);
        return true;
    }

    private bool EvictLeastRecentlySeen()
    {
        ChunkKey oldestKey = default;
        ChunkEntry? oldest = null;

        foreach ((ChunkKey key, ChunkEntry entry) in _chunks)
        {
            if (_visibleKeys.Contains(key) || entry.LastSeenFrame == _frame)
                continue;

            if (oldest == null || entry.LastSeenFrame < oldest.LastSeenFrame)
            {
                oldest = entry;
                oldestKey = key;
            }
        }

        if (oldest == null)
            return false;

        foreach (Mesh mesh in oldest.Surface)
        {
            UnloadMesh(mesh);
            _totalGpuUnloads++;
        }

        _chunks.Remove(oldestKey);
        return true;
    }

    private void UpdateStableCutLayer(bool cutEnabled, int cutLayer)
    {
        if (!cutEnabled)
        {
            _requestedCutLayer = -1;
            _stableCutLayer = -1;
            _cutLayerStableFrames = CutSettleFrames;
            return;
        }

        if (_requestedCutLayer != cutLayer)
        {
            _requestedCutLayer = cutLayer;
            _cutLayerStableFrames = 0;
            return;
        }

        if (_cutLayerStableFrames < CutSettleFrames)
            _cutLayerStableFrames++;

        if (_cutLayerStableFrames >= CutSettleFrames)
            _stableCutLayer = cutLayer;
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
        const int macroChunkCount = 4;
        const float sphereScale = 0.8660254038f;
        float voxelSize = SparseVoxelField.VoxelSizeMeters;
        float chunkSizeMeters = ChunkCells * voxelSize;
        float chunkRadius = chunkSizeMeters * sphereScale;
        float viewDistance = float.IsFinite(maximumViewDistanceMeters)
            ? MathF.Max(0f, maximumViewDistanceMeters)
            : 0f;
        float viewDistanceSquared = viewDistance * viewDistance;
        float scanRadius = viewDistance + chunkRadius;
        Vector3 cameraPosition = camera.Position;

        int chunksX = DivideRoundUp(field.Width, ChunkCells);
        int chunksY = DivideRoundUp(field.Height, ChunkCells);
        int chunksZ = DivideRoundUp(field.Depth, ChunkCells);
        int firstX = Math.Clamp((int)MathF.Floor((cameraPosition.X - scanRadius) / chunkSizeMeters), 0, chunksX - 1);
        int lastX = Math.Clamp((int)MathF.Floor((cameraPosition.X + scanRadius) / chunkSizeMeters), 0, chunksX - 1);
        int firstY = Math.Clamp((int)MathF.Floor((cameraPosition.Y - scanRadius) / chunkSizeMeters), 0, chunksY - 1);
        int lastY = Math.Clamp((int)MathF.Floor((cameraPosition.Y + scanRadius) / chunkSizeMeters), 0, chunksY - 1);
        int firstZ = Math.Clamp((int)MathF.Floor((cameraPosition.Z - scanRadius) / chunkSizeMeters), 0, chunksZ - 1);
        int lastZ = Math.Clamp((int)MathF.Floor((cameraPosition.Z + scanRadius) / chunkSizeMeters), 0, chunksZ - 1);

        int macroCells = ChunkCells * macroChunkCount;
        for (int macroZ = firstZ / macroChunkCount; macroZ <= lastZ / macroChunkCount; macroZ++)
        for (int macroY = firstY / macroChunkCount; macroY <= lastY / macroChunkCount; macroY++)
        for (int macroX = firstX / macroChunkCount; macroX <= lastX / macroChunkCount; macroX++)
        {
            int firstChunkX = macroX * macroChunkCount;
            int firstChunkY = macroY * macroChunkCount;
            int firstChunkZ = macroZ * macroChunkCount;
            int endChunkX = Math.Min(firstChunkX + macroChunkCount, chunksX);
            int endChunkY = Math.Min(firstChunkY + macroChunkCount, chunksY);
            int endChunkZ = Math.Min(firstChunkZ + macroChunkCount, chunksZ);
            int originX = firstChunkX * ChunkCells;
            int originY = firstChunkY * ChunkCells;
            int originZ = firstChunkZ * ChunkCells;
            int sizeX = Math.Min(macroCells, field.Width - originX);
            int sizeY = Math.Min(macroCells, field.Height - originY);
            int sizeZ = Math.Min(macroCells, field.Depth - originZ);

            if (BoundsNearestDistanceSquared(cameraPosition, originX, originY, originZ, sizeX, sizeY, sizeZ) > viewDistanceSquared)
                continue;

            float halfX = sizeX * voxelSize * 0.5f;
            float halfY = sizeY * voxelSize * 0.5f;
            float halfZ = sizeZ * voxelSize * 0.5f;
            Vector3 macroCenter = new(originX * voxelSize + halfX, originY * voxelSize + halfY, originZ * voxelSize + halfZ);
            float macroRadius = MathF.Sqrt(halfX * halfX + halfY * halfY + halfZ * halfZ);
            macroFrustumTests++;

            if (!TerrainFrustum.IsSphereVisible(camera, macroCenter, macroRadius, viewportWidth, viewportHeight, viewDistance))
                continue;

            visibleMacroRegions++;
            int childFirstX = Math.Max(firstX, firstChunkX);
            int childLastX = Math.Min(lastX, endChunkX - 1);
            int childFirstY = Math.Max(firstY, firstChunkY);
            int childLastY = Math.Min(lastY, endChunkY - 1);
            int childFirstZ = Math.Max(firstZ, firstChunkZ);
            int childLastZ = Math.Min(lastZ, endChunkZ - 1);

            for (int z = childFirstZ; z <= childLastZ; z++)
            for (int y = childFirstY; y <= childLastY; y++)
            for (int x = childFirstX; x <= childLastX; x++)
            {
                int chunkX = x * ChunkCells;
                int chunkY = y * ChunkCells;
                int chunkZ = z * ChunkCells;
                if (BoundsNearestDistanceSquared(cameraPosition, chunkX, chunkY, chunkZ, ChunkCells, ChunkCells, ChunkCells) > viewDistanceSquared)
                    continue;

                Vector3 center = new(
                    (chunkX + ChunkCells * 0.5f) * voxelSize,
                    (chunkY + ChunkCells * 0.5f) * voxelSize,
                    (chunkZ + ChunkCells * 0.5f) * voxelSize);
                frustumTests++;

                if (!TerrainFrustum.IsSphereVisible(camera, center, chunkRadius, viewportWidth, viewportHeight, viewDistance))
                    continue;

                candidates.Add(new Candidate(
                    new ChunkKey(x, y, z),
                    center,
                    chunkRadius,
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
        float size = SparseVoxelField.VoxelSizeMeters;
        float minX = originX * size;
        float minY = originY * size;
        float minZ = originZ * size;
        float maxX = (originX + sizeX) * size;
        float maxY = (originY + sizeY) * size;
        float maxZ = (originZ + sizeZ) * size;
        float dx = point.X < minX ? minX - point.X : point.X > maxX ? point.X - maxX : 0f;
        float dy = point.Y < minY ? minY - point.Y : point.Y > maxY ? point.Y - maxY : 0f;
        float dz = point.Z < minZ ? minZ - point.Z : point.Z > maxZ ? point.Z - maxZ : 0f;
        return dx * dx + dy * dy + dz * dz;
    }

    private bool AreCachedMeshesCurrent(SparseVoxelField.VoxelBounds bounds, SparseVoxelField field)
    {
        GetAffectedChunkRange(field, bounds, out int minX, out int minY, out int minZ, out int maxX, out int maxY, out int maxZ);
        for (int z = minZ; z <= maxZ; z++)
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            if (!_chunks.TryGetValue(new ChunkKey(x, y, z), out ChunkEntry? entry))
                continue;

            if (entry.Dirty || !entry.Built ||
                entry.BaseVersion != field.BaseVersion ||
                entry.PaletteVersion != _paletteVersion)
                return false;
        }

        return true;
    }

    private static void GetAffectedChunkRange(
        SparseVoxelField field,
        SparseVoxelField.VoxelBounds bounds,
        out int minX,
        out int minY,
        out int minZ,
        out int maxX,
        out int maxY,
        out int maxZ)
    {
        int chunksX = DivideRoundUp(field.Width, ChunkCells);
        int chunksY = DivideRoundUp(field.Height, ChunkCells);
        int chunksZ = DivideRoundUp(field.Depth, ChunkCells);
        minX = Math.Clamp(Math.Max(0, bounds.MinX - 1) / ChunkCells, 0, chunksX - 1);
        minY = Math.Clamp(Math.Max(0, bounds.MinY - 1) / ChunkCells, 0, chunksY - 1);
        minZ = Math.Clamp(Math.Max(0, bounds.MinZ - 1) / ChunkCells, 0, chunksZ - 1);
        maxX = Math.Clamp(bounds.MaxX / ChunkCells, 0, chunksX - 1);
        maxY = Math.Clamp(bounds.MaxY / ChunkCells, 0, chunksY - 1);
        maxZ = Math.Clamp(bounds.MaxZ / ChunkCells, 0, chunksZ - 1);
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
        if (_observedField == null)
            return;

        GetAffectedChunkRange(
            _observedField,
            bounds,
            out int minX,
            out int minY,
            out int minZ,
            out int maxX,
            out int maxY,
            out int maxZ);

        for (int z = minZ; z <= maxZ; z++)
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            if (!_chunks.TryGetValue(new ChunkKey(x, y, z), out ChunkEntry? entry))
                continue;

            entry.Dirty = true;
            entry.Revision++;
            entry.FailedRevision = int.MinValue;
        }
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
        int cutValue = enabled ? 1 : 0;
        SetShaderValue(_shader, _cutEnabledLocation, cutValue, ShaderUniformDataType.Int);
        SetShaderValue(_shader, _cutHeightLocation, height, ShaderUniformDataType.Float);
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
                detached++;
            else
                field.RestoreFragment(fragment);
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

        foreach (ChunkEntry entry in _chunks.Values)
        {
            foreach (Mesh mesh in entry.Surface)
            {
                UnloadMesh(mesh);
                _totalGpuUnloads++;
            }
        }

        _chunks.Clear();
        _dynamicFragments.Dispose();
        UnloadMaterial(_material);
        UnloadShader(_shader);
        _logger.Write(
            "RENDERER_STOP",
            $"builds={_totalBuildCompleted};failed={_totalBuildFailed};gpuUploads={_totalGpuUploads};gpuUnloads={_totalGpuUnloads}");
        _logger.Dispose();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
