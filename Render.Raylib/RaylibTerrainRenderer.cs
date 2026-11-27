using System.Diagnostics;
using Core.Map;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// Caches full terrain meshes per storage region. Voxel cutaway uses a GPU clip
/// plane, so moving the plane never invalidates the underlying terrain meshes.
/// Only the lightweight horizontal cut surfaces are rebuilt.
/// </summary>
internal sealed class RaylibTerrainRenderer : IDisposable
{
    private const int MaximumHeightmapMeshBuildsPerFrame = 6;

    private readonly RegionGpuMesh?[] _regionMeshes;
    private readonly RegionGpuMesh?[] _visibleRegionMeshes;
    private int _activeVoxelCellSize = 1;
    private int _regionBuildCursor;
    private readonly Material _material;
    private readonly TerrainRenderMode _mode;
    private readonly VoxelTerrainRenderer? _densityVoxelRenderer;
    private readonly TerrainPipelineLogger _logger;
    private readonly bool _ownsLogger;

    private TerrainPalette _palette;
    private int _paletteVersion;
    private bool _disposed;

    public int DrawCallsLastFrame { get; private set; }
    public int VisibleRegionsLastFrame { get; private set; }
    public int RebuiltRegions { get; private set; }
    public int CutSurfaceRebuilds { get; private set; }
    public int ActiveRenderCellSize => _activeVoxelCellSize;
    public float ActiveVoxelSizeMeters =>
        _densityVoxelRenderer?.ActiveVoxelSizeMeters ?? _activeVoxelCellSize;
    public float VoxelRenderChunkSizeMeters =>
        _densityVoxelRenderer?.RenderChunkSizeMeters ?? 0f;
    public int CachedVoxelChunks => _densityVoxelRenderer?.CachedChunks ?? 0;
    public int VoxelLodPrewarmBuilds => _densityVoxelRenderer?.LodPrewarmBuilds ?? 0;
    public double VoxelCpuCaptureMilliseconds => _densityVoxelRenderer?.CpuCaptureMilliseconds ?? 0d;
    public double VoxelGpuUploadMilliseconds => _densityVoxelRenderer?.GpuUploadMilliseconds ?? 0d;
    public int VoxelMeshWorkers => _densityVoxelRenderer?.RunningMeshJobs ?? 0;
    public int VoxelPendingMeshJobs => _densityVoxelRenderer?.PendingMeshJobs ?? 0;
    public int VoxelCompletedMeshResultsPending => _densityVoxelRenderer?.CompletedMeshResultsPending ?? 0;
    public double VoxelWorkerBuildMilliseconds => _densityVoxelRenderer?.WorkerBuildMillisecondsLastFrame ?? 0d;
    public double VoxelLastWorkerBuildMilliseconds => _densityVoxelRenderer?.LastWorkerBuildMilliseconds ?? 0d;
    public double VoxelLastQueueWaitMilliseconds => _densityVoxelRenderer?.LastWorkerQueueWaitMilliseconds ?? 0d;
    public double VoxelLastResultDeliveryMilliseconds => _densityVoxelRenderer?.LastWorkerResultDeliveryMilliseconds ?? 0d;
    public int VoxelBuildsCompletedLastFrame => _densityVoxelRenderer?.BuildsCompletedLastFrame ?? 0;
    public int VoxelLastBuildStep => _densityVoxelRenderer?.LastBuildStep ?? 0;
    public int VoxelLastBuildVertices => _densityVoxelRenderer?.LastBuildVertices ?? 0;
    public int VoxelLastBuildTriangles => _densityVoxelRenderer?.LastBuildTriangles ?? 0;
    public int VoxelGpuUploadsLastFrame => _densityVoxelRenderer?.GpuUploadsLastFrame ?? 0;
    public long VoxelTotalBuildCompleted => _densityVoxelRenderer?.TotalBuildCompleted ?? 0;
    public long VoxelTotalBuildCancelled => _densityVoxelRenderer?.TotalBuildCancelled ?? 0;
    public long VoxelTotalBuildFailed => _densityVoxelRenderer?.TotalBuildFailed ?? 0;
    public long VoxelTotalStaleResults => _densityVoxelRenderer?.TotalStaleResults ?? 0;
    public long VoxelTotalRepeatedBuilds => _densityVoxelRenderer?.TotalRepeatedBuilds ?? 0;
    public long VoxelTotalDuplicateRequests => _densityVoxelRenderer?.TotalDuplicateRequestAttempts ?? 0;
    public long VoxelTotalQueueSaturated => _densityVoxelRenderer?.TotalQueueSaturated ?? 0;
    public long VoxelTotalGpuUnloads => _densityVoxelRenderer?.TotalGpuUnloads ?? 0;
    public long VoxelTotalIdenticalSurfaceSkips => _densityVoxelRenderer?.TotalIdenticalSurfaceSkips ?? 0;
    public long VoxelTotalIdenticalCutSkips => _densityVoxelRenderer?.TotalIdenticalCutSkips ?? 0;
    public string VoxelDiagnosticsLogPath => _densityVoxelRenderer?.DiagnosticsLogPath ?? string.Empty;
    public int VoxelVisibleCandidates => _densityVoxelRenderer?.VisibleCandidatesLastFrame ?? 0;
    public int VoxelFrustumCandidates => _densityVoxelRenderer?.FrustumCandidatesLastFrame ?? 0;
    public int VoxelCandidateOverflow => _densityVoxelRenderer?.CandidateOverflowLastFrame ?? 0;
    public long VoxelEmptyChunks => _densityVoxelRenderer?.TotalEmptyChunks ?? 0;
    public int DynamicFragmentCount => _densityVoxelRenderer?.DynamicFragmentCount ?? 0;
    public bool IsMergingFragments => _densityVoxelRenderer?.IsMergingFragments ?? false;
    public bool IsUsingCoarseVoxelLod => false;

    public RaylibTerrainRenderer(
        TerrainRenderSnapshot terrain,
        TerrainPalette palette,
        TerrainRenderMode mode,
        TerrainPipelineLogger? logger = null)
    {
        _logger = logger ?? new TerrainPipelineLogger(false, RenderLogMode.Detailed);
        _ownsLogger = logger == null;
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));
        _mode = mode;
        _densityVoxelRenderer = mode == TerrainRenderMode.Voxel
            ? new VoxelTerrainRenderer(palette, _logger)
            : null;
        _regionMeshes = mode == TerrainRenderMode.Voxel
            ? Array.Empty<RegionGpuMesh?>()
            : new RegionGpuMesh?[terrain.StorageRegionCount];
        _visibleRegionMeshes = mode == TerrainRenderMode.Voxel
            ? Array.Empty<RegionGpuMesh?>()
            : new RegionGpuMesh?[terrain.StorageRegionCount];

        _material = mode == TerrainRenderMode.Voxel
            ? default
            : LoadMaterialDefault();

        Synchronize(terrain);

        _logger.Write(RenderLogMode.Detailed, "TERRAIN_RENDERER_CREATED",
            $"mode={_mode} backend={(mode == TerrainRenderMode.Voxel ? "density-surface-nets-only" : "heightmap-regions")} staticVoxelBase=false");
    }

    public void Synchronize(
        TerrainRenderSnapshot terrain,
        bool forceRebuild = false)
    {
        ThrowIfDisposed();
        _logger.Write(RenderLogMode.Detailed, "TERRAIN_SYNCHRONIZE",
            $"forceRebuild={forceRebuild} regionCount={terrain.StorageRegionCount} mode={_mode}");
        if (_mode == TerrainRenderMode.HeightmapFallback)
            InvalidateMeshes(_regionMeshes, forceRebuild);
    }

    private static void InvalidateMeshes(
        RegionGpuMesh?[] meshes,
        bool forceRebuild)
    {
        if (!forceRebuild)
            return;

        // Keep the previous GPU mesh alive until its replacement has been
        // built successfully. Draw() will detect this version and rebuild it.
        foreach (RegionGpuMesh? existing in meshes)
        {
            if (existing != null)
                existing.RegionVersion = long.MinValue;
        }
    }

    private static void DisposeMeshes(RegionGpuMesh?[] meshes)
    {
        foreach (RegionGpuMesh? mesh in meshes)
            mesh?.Dispose();
        Array.Clear(meshes);
    }

    public void Draw(
        TerrainRenderSnapshot terrain,
        Camera3D camera,
        int viewportWidth,
        int viewportHeight,
        float maximumViewDistanceMeters)
    {
        ThrowIfDisposed();

        if (_mode == TerrainRenderMode.Voxel)
        {
            long voxelDrawStarted = Stopwatch.GetTimestamp();
            _densityVoxelRenderer!.Draw(
                terrain.Voxels,
                camera,
                viewportWidth,
                viewportHeight,
                maximumViewDistanceMeters,
                terrain.CutawayEnabled,
                terrain.CutawayHeightMeters);

            DrawCallsLastFrame = _densityVoxelRenderer.DrawCallsLastFrame;
            VisibleRegionsLastFrame = _densityVoxelRenderer.VisibleChunksLastFrame;
            RebuiltRegions = _densityVoxelRenderer.RebuiltChunks;
            CutSurfaceRebuilds = _densityVoxelRenderer.RebuiltCutCaps;

            if (_logger.IsEnabled(RenderLogMode.FrameSummary))
                _logger.Write(RenderLogMode.FrameSummary, "TERRAIN_DRAW_END",
                    $"backend=density-surface-nets-only elapsedMs={Stopwatch.GetElapsedTime(voxelDrawStarted).TotalMilliseconds:0.000} visibleChunks={VisibleRegionsLastFrame} draws={DrawCallsLastFrame} rebuilt={RebuiltRegions} sectionSlabs={CutSurfaceRebuilds}");
            return;
        }

        long drawStarted = Stopwatch.GetTimestamp();
        RebuiltRegions = 0;
        CutSurfaceRebuilds = 0;

        float cameraDistance = Vector3.Distance(camera.Position, camera.Target);
        Vector3 worldCenter = new(
            terrain.WorldWidth * 0.5f,
            terrain.VerticalWorldExtentMeters * 0.5f,
            terrain.WorldDepth * 0.5f);
        float worldRadius = 0.5f * MathF.Sqrt(
            terrain.WorldWidth * terrain.WorldWidth +
            terrain.WorldDepth * terrain.WorldDepth +
            terrain.VerticalWorldExtentMeters * terrain.VerticalWorldExtentMeters);
        float cameraToWorldCenter = Vector3.Distance(camera.Position, worldCenter);
        float farClip = MathF.Max(
            1000f,
            MathF.Max(
                maximumViewDistanceMeters + 100f,
                cameraToWorldCenter + worldRadius + 100f));
        float nearClip = Math.Clamp(cameraDistance * 0.01f, 0.05f, 8f);

        // Raylib's default far clip is designed for small scenes. Our world is
        // kilometres wide, so keep the camera's projection and depth culling
        // planes large enough for the active landscape.
        Rlgl.SetClipPlanes(nearClip, farClip);

        TerrainRenderSnapshot drawTerrain = terrain;
        RegionGpuMesh?[] drawMeshes = _regionMeshes;
        _activeVoxelCellSize = drawTerrain.RegionSize;

        int visibleRegionCount = 0;
        int visibleDrawCalls = 0;
        int baseBuildsThisFrame = 0;
        int maximumBaseBuilds = MaximumHeightmapMeshBuildsPerFrame;
        int cutLayer = drawTerrain.CutawayEnabled ? drawTerrain.CutawayLayer : -1;

        int regionCount = drawMeshes.Length;
        int scanStart = regionCount == 0 ? 0 : _regionBuildCursor % regionCount;
        for (int offset = 0; offset < regionCount; offset++)
        {
            int i = (scanStart + offset) % regionCount;
            RegionGpuMesh? region = drawMeshes[i];
            GetRegionBounds(drawTerrain, i, out Vector3 center, out float radius);

            if (!TerrainFrustum.IsSphereVisible(
                camera,
                region?.BoundsCenter ?? center,
                region?.BoundsRadius ?? radius,
                viewportWidth,
                viewportHeight,
                maximumViewDistanceMeters))
            {
                continue;
            }

            bool baseIsStale =
                region == null ||
                region.RegionVersion != drawTerrain.SourceRegionVersions[i] ||
                region.PaletteVersion != _paletteVersion ||
                region.CutawayLayer != cutLayer;

            if (baseIsStale)
            {
                if (baseBuildsThisFrame >= maximumBaseBuilds)
                {
                    // A stale mesh is still preferable to a black hole while
                    // the per-frame upload budget catches up.
                    if (region == null)
                        continue;
                }
                else
                {
                    int regionX = i % terrain.StorageRegionsX;
                    int regionY = i / terrain.StorageRegionsX;
                    RegionGpuMesh rebuilt = RegionMeshBuilder.Build(
                        drawTerrain, regionX, regionY, _palette, _paletteVersion);

                    // Swap only after a successful build/upload. The old mesh
                    // remains drawable if building the replacement throws.
                    drawMeshes[i] = rebuilt;
                    region?.Dispose();
                    region = rebuilt;
                    RebuiltRegions++;
                    baseBuildsThisFrame++;
                    _logger.Write(RenderLogMode.Detailed, "REGION_MESH_REBUILT", $"index={i} region=({regionX},{regionY}) mode={_mode} palette={_paletteVersion}");
                }
            }

            if (region == null || !region.IsUploaded)
                continue;

            if (region.CutMeshes.Length > 0)
                ClearCutMeshes(region);

            _visibleRegionMeshes[visibleRegionCount++] = region;
        }

        BeginMode3D(camera);

        for (int i = 0; i < visibleRegionCount; i++)
        {
            RegionGpuMesh region = _visibleRegionMeshes[i]!;
            foreach (Mesh mesh in region.Meshes)
            {
                DrawMesh(mesh, _material, Matrix4x4.Identity);
                visibleDrawCalls++;
            }
        }

        EndMode3D();

        Array.Clear(_visibleRegionMeshes, 0, visibleRegionCount);
        if (regionCount > 0)
            _regionBuildCursor = (scanStart + 1) % regionCount;

        DrawCallsLastFrame = visibleDrawCalls;
        VisibleRegionsLastFrame = visibleRegionCount;
        if (_logger.IsEnabled(RenderLogMode.FrameSummary))
            _logger.Write(RenderLogMode.FrameSummary, "TERRAIN_DRAW_END",
                $"backend=heightmap-regions elapsedMs={Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds:0.000} visibleRegions={visibleRegionCount} draws={DrawCallsLastFrame} regionBuilds={baseBuildsThisFrame}");
    }

    public int DetachLooseFragments(
        Core.Map.SparseVoxelField field,
        Vector3 centerMeters,
        Vector3 halfExtentsMeters)
    {
        ThrowIfDisposed();
        return _densityVoxelRenderer?.DetachLooseFragments(
            field, centerMeters, halfExtentsMeters) ?? 0;
    }

    public void BeginVoxelEdit()
    {
        ThrowIfDisposed();
        _densityVoxelRenderer?.BeginVoxelEdit();
    }

    public void EndVoxelEdit(bool commit)
    {
        ThrowIfDisposed();
        _densityVoxelRenderer?.EndVoxelEdit(commit);
    }

    public void UndoVoxelEdit()
    {
        ThrowIfDisposed();
        _densityVoxelRenderer?.UndoVoxelEdit();
    }

    public void RedoVoxelEdit()
    {
        ThrowIfDisposed();
        _densityVoxelRenderer?.RedoVoxelEdit();
    }

    public void ClearDynamicFragments()
    {
        ThrowIfDisposed();
        _densityVoxelRenderer?.ClearDynamicFragments();
    }

    private static void GetRegionBounds(
        TerrainRenderSnapshot terrain,
        int index,
        out Vector3 center,
        out float radius)
    {
        int regionX = index % terrain.StorageRegionsX;
        int regionY = index / terrain.StorageRegionsX;
        center = new Vector3(
            (regionX + 0.5f) * terrain.StorageRegionSize,
            terrain.VerticalWorldExtentMeters * 0.5f,
            (regionY + 0.5f) * terrain.StorageRegionSize);
        radius = MathF.Sqrt(
            terrain.StorageRegionSize * terrain.StorageRegionSize * 0.5f +
            terrain.VerticalWorldExtentMeters * terrain.VerticalWorldExtentMeters * 0.25f);
    }

    private void ClearCutMeshes(RegionGpuMesh region)
    {
        if (region.CutMeshes.Length > 0)
            _logger.Write(RenderLogMode.Detailed, "GPU_MESH_UNLOAD", $"kind=cut-surface count={region.CutMeshes.Length} reason=cut-disabled");
        foreach (Mesh mesh in region.CutMeshes)
            UnloadMesh(mesh);

        region.CutMeshes = Array.Empty<Mesh>();
        region.CutSurfaceLayer = int.MinValue;
    }

    public void SetPalette(TerrainPalette palette)
    {
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));
        _paletteVersion++;
        _logger.Write(RenderLogMode.Detailed, "PALETTE_CHANGED", $"version={_paletteVersion} ambient={palette.AmbientLight:0.000} sun={palette.SunIntensity:0.000} color=({palette.BaseRock.R},{palette.BaseRock.G},{palette.BaseRock.B})");
        _densityVoxelRenderer?.SetPalette(palette);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _logger.Write(RenderLogMode.ErrorsOnly, "TERRAIN_RENDERER_DISPOSE", $"mode={_mode} draws={DrawCallsLastFrame} visible={VisibleRegionsLastFrame}");
        _densityVoxelRenderer?.Dispose();
        DisposeMeshes(_regionMeshes);

        if (_mode == TerrainRenderMode.HeightmapFallback)
            UnloadMaterial(_material);
        _disposed = true;
        if (_ownsLogger)
            _logger.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
