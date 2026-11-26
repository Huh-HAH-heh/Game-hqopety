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
    private const int MaximumCoarseMeshBuildsPerFrame = 6;
    private const int MaximumCutSurfaceBuildsPerFrame = 144;

    private const string VoxelVertexShader = """
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

    private const string VoxelFragmentShader = """
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
            finalColor = vec4(
                fragColor.rgb * illumination,
                fragColor.a);
        }
        """;

    private readonly RegionGpuMesh?[] _regionMeshes;
    private readonly RegionGpuMesh?[] _visibleRegionMeshes;
    private int _activeVoxelCellSize = 1;
    private int _regionBuildCursor;
    private readonly Material _material;
    private readonly Shader _voxelShader;
    private readonly bool _hasVoxelShader;
    private readonly int _cutEnabledLocation;
    private readonly int _cutHeightLocation;
    private readonly int _sunDirectionLocation;
    private readonly int _ambientLightLocation;
    private readonly int _sunIntensityLocation;
    private readonly TerrainRenderMode _mode;
    private readonly VoxelTerrainRenderer? _densityVoxelRenderer;
    private readonly TerrainPipelineLogger _logger;
    private readonly bool _ownsLogger;

    private TerrainPalette _palette;
    private int _paletteVersion;
    private SparseVoxelField? _observedVoxelField;
    private int _worldTileWidth;
    private int _worldTileDepth;
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
        TerrainRenderSnapshot? coarseTerrain = null,
        TerrainPipelineLogger? logger = null)
    {
        _logger = logger ?? new TerrainPipelineLogger(false, RenderLogMode.Detailed);
        _ownsLogger = logger == null;
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));
        _mode = mode;
        _densityVoxelRenderer = mode == TerrainRenderMode.Voxel
            ? new VoxelTerrainRenderer(palette, _logger)
            : null;
        _regionMeshes = new RegionGpuMesh?[terrain.StorageRegionCount];
        _visibleRegionMeshes = new RegionGpuMesh?[terrain.StorageRegionCount];

        Material material = LoadMaterialDefault();
        Shader voxelShader = default;
        int cutEnabledLocation = -1;
        int cutHeightLocation = -1;
        int sunDirectionLocation = -1;
        int ambientLightLocation = -1;
        int sunIntensityLocation = -1;
        bool hasVoxelShader = false;

        if (mode == TerrainRenderMode.Voxel)
        {
            voxelShader = LoadShaderFromMemory(VoxelVertexShader, VoxelFragmentShader);
            if (!IsShaderValid(voxelShader))
                throw new InvalidOperationException("The static voxel terrain shader could not be created.");

            cutEnabledLocation = GetShaderLocation(voxelShader, "uCutEnabled");
            cutHeightLocation = GetShaderLocation(voxelShader, "uCutHeight");
            sunDirectionLocation = GetShaderLocation(voxelShader, "uSunDirection");
            ambientLightLocation = GetShaderLocation(voxelShader, "uAmbientLight");
            sunIntensityLocation = GetShaderLocation(voxelShader, "uSunIntensity");

            if (cutEnabledLocation < 0 || cutHeightLocation < 0 ||
                sunDirectionLocation < 0 || ambientLightLocation < 0 ||
                sunIntensityLocation < 0)
            {
                UnloadShader(voxelShader);
                throw new InvalidOperationException("The static voxel terrain shader is missing a required uniform.");
            }

            material.Shader = voxelShader;
            hasVoxelShader = true;
        }

        _voxelShader = voxelShader;
        _cutEnabledLocation = cutEnabledLocation;
        _cutHeightLocation = cutHeightLocation;
        _sunDirectionLocation = sunDirectionLocation;
        _ambientLightLocation = ambientLightLocation;
        _sunIntensityLocation = sunIntensityLocation;
        _hasVoxelShader = hasVoxelShader;
        _material = material;

        Synchronize(terrain, null);

        if (mode == TerrainRenderMode.Voxel)
        {
            Stopwatch buildTimer = Stopwatch.StartNew();
            _logger.Write(RenderLogMode.Detailed, "VOXEL_BASE_BUILD_START",
                $"regions={terrain.StorageRegionCount} regionSize={terrain.StorageRegionSize} resolution=1m indexedSurface=true");

            try
            {
                for (int i = 0; i < _regionMeshes.Length; i++)
                {
                    int regionX = i % terrain.StorageRegionsX;
                    int regionY = i / terrain.StorageRegionsX;
                    _regionMeshes[i] = RegionMeshBuilder.BuildVoxel(
                        terrain, regionX, regionY, _palette, _paletteVersion);
                }
            }
            catch
            {
                DisposeMeshes(_regionMeshes);
                _densityVoxelRenderer?.Dispose();
                UnloadMaterial(_material);
                if (_hasVoxelShader)
                    UnloadShader(_voxelShader);
                throw;
            }

            _logger.Write(RenderLogMode.Detailed, "VOXEL_BASE_BUILD_COMPLETE",
                $"regions={_regionMeshes.Length} built={_regionMeshes.Length} elapsedMs={buildTimer.Elapsed.TotalMilliseconds:0.000} mode=all-at-once");
        }

        _logger.Write(RenderLogMode.Detailed, "TERRAIN_RENDERER_CREATED",
            $"mode={_mode} regions={terrain.StorageRegionCount} staticVoxelBase={mode == TerrainRenderMode.Voxel} editedVoxelBackend={_densityVoxelRenderer != null}");
    }

    public void Synchronize(
        TerrainRenderSnapshot terrain,
        TerrainRenderSnapshot? coarseTerrain,
        bool forceRebuild = false)
    {
        ThrowIfDisposed();
        EnsureVoxelSubscription(terrain);
        _logger.Write(RenderLogMode.Detailed, "TERRAIN_SYNCHRONIZE", $"forceRebuild={forceRebuild} regionCount={terrain.StorageRegionCount} mode={_mode}");
        InvalidateMeshes(_regionMeshes, forceRebuild);
    }

    private void EnsureVoxelSubscription(TerrainRenderSnapshot terrain)
    {
        if (_mode != TerrainRenderMode.Voxel)
            return;

        if (!ReferenceEquals(_observedVoxelField, terrain.Voxels))
        {
            if (_observedVoxelField != null)
                _observedVoxelField.Changed -= OnVoxelFieldChanged;

            _observedVoxelField = terrain.Voxels;
            _observedVoxelField.Changed += OnVoxelFieldChanged;
        }

        _worldTileWidth = terrain.RegionsX;
        _worldTileDepth = terrain.RegionsY;
    }

    private void OnVoxelFieldChanged(SparseVoxelField.VoxelBounds bounds)
    {
        const int samplesPerMeter = 10;
        int minX = Math.Max(0, bounds.MinX);
        int minZ = Math.Max(0, bounds.MinZ);
        int maxX = Math.Min(_worldTileWidth * samplesPerMeter, bounds.MaxX);
        int maxZ = Math.Min(_worldTileDepth * samplesPerMeter, bounds.MaxZ);
        if (maxX <= minX || maxZ <= minZ)
            return;

        int firstRegionX = (minX / samplesPerMeter) / TerrainRegion.TilesPerSide;
        int lastRegionX = ((maxX - 1) / samplesPerMeter) / TerrainRegion.TilesPerSide;
        int firstRegionZ = (minZ / samplesPerMeter) / TerrainRegion.TilesPerSide;
        int lastRegionZ = ((maxZ - 1) / samplesPerMeter) / TerrainRegion.TilesPerSide;

        for (int regionZ = firstRegionZ; regionZ <= lastRegionZ; regionZ++)
        for (int regionX = firstRegionX; regionX <= lastRegionX; regionX++)
        {
            int index = regionX + regionZ * (int)Math.Ceiling(
                _worldTileWidth / (double)TerrainRegion.TilesPerSide);
            if ((uint)index >= (uint)_regionMeshes.Length)
                continue;

            if (_regionMeshes[index] is RegionGpuMesh region)
                region.RegionVersion = long.MinValue;
        }
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
        int cutBuildsThisFrame = 0;
        int maximumBaseBuilds = _mode == TerrainRenderMode.Voxel
            ? int.MaxValue
            : MaximumCoarseMeshBuildsPerFrame;
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
                (_mode == TerrainRenderMode.HeightmapFallback &&
                 region.CutawayLayer != cutLayer);

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
                    RegionGpuMesh rebuilt = _mode == TerrainRenderMode.Voxel
                        ? RegionMeshBuilder.BuildVoxel(
                            drawTerrain, regionX, regionY, _palette, _paletteVersion)
                        : RegionMeshBuilder.Build(
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

            if (_mode == TerrainRenderMode.Voxel && drawTerrain.CutawayEnabled)
            {
                if (region.CutSurfaceLayer != cutLayer &&
                    cutBuildsThisFrame < MaximumCutSurfaceBuildsPerFrame)
                {
                    int regionX = i % terrain.StorageRegionsX;
                    int regionY = i / terrain.StorageRegionsX;
                    Mesh[] rebuiltCutMeshes = RegionMeshBuilder.BuildVoxelCutSurface(
                        drawTerrain,
                        regionX,
                        regionY,
                        cutLayer,
                        _palette);

                    // Build first and swap second. Never unload a valid cut cap
                    // before its replacement has finished being generated.
                    Mesh[] previousCutMeshes = region.CutMeshes;
                    region.CutMeshes = rebuiltCutMeshes;
                    region.CutSurfaceLayer = cutLayer;
                    foreach (Mesh mesh in previousCutMeshes)
                        UnloadMesh(mesh);

                    cutBuildsThisFrame++;
                    CutSurfaceRebuilds++;
                    _logger.Write(RenderLogMode.Detailed, "REGION_CUT_MESH_REBUILT", $"index={i} region=({regionX},{regionY}) layer={cutLayer} meshes={rebuiltCutMeshes.Length}");
                }
            }
            else if (region.CutMeshes.Length > 0)
            {
                ClearCutMeshes(region);
            }

            _visibleRegionMeshes[visibleRegionCount++] = region;
        }

        SetVoxelCutShader(
            enabled: _mode == TerrainRenderMode.Voxel && drawTerrain.CutawayEnabled,
            height: drawTerrain.CutawayHeightMeters);
        SetVoxelLighting();

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

        if (_mode == TerrainRenderMode.Voxel && drawTerrain.CutawayEnabled)
        {
            // The base volume is clipped in the GPU. Cut caps are separate
            // meshes, rendered without clipping and rebuilt independently.
            SetVoxelCutShader(enabled: false, height: drawTerrain.CutawayHeightMeters);

            for (int i = 0; i < visibleRegionCount; i++)
            {
                RegionGpuMesh region = _visibleRegionMeshes[i]!;
                if (region.CutSurfaceLayer != cutLayer)
                    continue;

                foreach (Mesh mesh in region.CutMeshes)
                {
                    DrawMesh(mesh, _material, Matrix4x4.Identity);
                    visibleDrawCalls++;
                }
            }
        }

        EndMode3D();

        Array.Clear(_visibleRegionMeshes, 0, visibleRegionCount);
        if (regionCount > 0)
            _regionBuildCursor = (scanStart + 1) % regionCount;

        int editedVoxelDrawCalls = 0;
        int editedVoxelVisible = 0;
        if (_densityVoxelRenderer != null)
        {
            _densityVoxelRenderer.Draw(
                terrain.Voxels,
                camera,
                viewportWidth,
                viewportHeight,
                maximumViewDistanceMeters,
                terrain.CutawayEnabled,
                terrain.CutawayHeightMeters);

            editedVoxelDrawCalls = _densityVoxelRenderer.DrawCallsLastFrame;
            editedVoxelVisible = _densityVoxelRenderer.VisibleChunksLastFrame;
            RebuiltRegions += _densityVoxelRenderer.RebuiltChunks;
            CutSurfaceRebuilds += _densityVoxelRenderer.RebuiltCutCaps;
        }

        DrawCallsLastFrame = visibleDrawCalls + editedVoxelDrawCalls;
        VisibleRegionsLastFrame = visibleRegionCount + editedVoxelVisible;
        if (_logger.IsEnabled(RenderLogMode.FrameSummary))
            _logger.Write(RenderLogMode.FrameSummary, "TERRAIN_DRAW_END",
                $"backend=static-height-surface+edited-surface-nets elapsedMs={Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds:0.000} visibleRegions={visibleRegionCount} editedVoxelChunks={editedVoxelVisible} draws={DrawCallsLastFrame} regionBuilds={baseBuildsThisFrame} cutBuilds={cutBuildsThisFrame}");
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

    private void SetVoxelCutShader(bool enabled, float height)
    {
        if (!_hasVoxelShader)
            return;

        int cutEnabled = enabled ? 1 : 0;
        SetShaderValue(
            _voxelShader,
            _cutEnabledLocation,
            cutEnabled,
            ShaderUniformDataType.Int);
        SetShaderValue(
            _voxelShader,
            _cutHeightLocation,
            height,
            ShaderUniformDataType.Float);
    }

    private void SetVoxelLighting()
    {
        if (!_hasVoxelShader)
            return;

        Vector3 direction = _palette.GetSunDirection();
        SetShaderValue(
            _voxelShader,
            _sunDirectionLocation,
            direction,
            ShaderUniformDataType.Vec3);
        SetShaderValue(
            _voxelShader,
            _ambientLightLocation,
            _palette.AmbientLight,
            ShaderUniformDataType.Float);
        SetShaderValue(
            _voxelShader,
            _sunIntensityLocation,
            _palette.SunIntensity,
            ShaderUniformDataType.Float);
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
        if (_observedVoxelField != null)
            _observedVoxelField.Changed -= OnVoxelFieldChanged;
        _observedVoxelField = null;
        _densityVoxelRenderer?.Dispose();
        DisposeMeshes(_regionMeshes);

        UnloadMaterial(_material);
        if (_hasVoxelShader)
            UnloadShader(_voxelShader);
        _disposed = true;
        if (_ownsLogger)
            _logger.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
