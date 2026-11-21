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
    private const int MaximumFineMeshBuildsPerFrame = 2;
    private const int MaximumCutSurfaceBuildsPerFrame = 144;
    private const float FineLodEnterDistanceMeters = 220f;
    private const float FineLodExitDistanceMeters = 290f;

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
    private readonly RegionGpuMesh?[] _coarseRegionMeshes;
    private readonly RegionGpuMesh?[] _visibleRegionMeshes;
    private readonly TerrainRenderSnapshot? _coarseTerrain;
    private bool _usingCoarseLod = true;
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
    private readonly TerrainRenderMode _mode;\n    private readonly VoxelTerrainRenderer? _densityVoxelRenderer;

    private TerrainPalette _palette;
    private int _paletteVersion;
    private bool _disposed;

    public int DrawCallsLastFrame { get; private set; }
    public int VisibleRegionsLastFrame { get; private set; }
    public int RebuiltRegions { get; private set; }
    public int CutSurfaceRebuilds { get; private set; }
    public int ActiveRenderCellSize => _activeVoxelCellSize;
    public bool IsUsingCoarseVoxelLod => _mode == TerrainRenderMode.Voxel &&
        _coarseTerrain != null &&
        _usingCoarseLod;

    public RaylibTerrainRenderer(
        TerrainRenderSnapshot terrain,
        TerrainPalette palette,
        TerrainRenderMode mode,
        TerrainRenderSnapshot? coarseTerrain = null)
    {
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));
        _mode = mode;\n        _densityVoxelRenderer = mode == TerrainRenderMode.Voxel\n            ? new VoxelTerrainRenderer(palette)\n            : null;
        _coarseTerrain = mode == TerrainRenderMode.Voxel &&
            coarseTerrain?.IsVoxelMode == true
                ? coarseTerrain
                : null;

        _regionMeshes = new RegionGpuMesh?[terrain.StorageRegionCount];
        _coarseRegionMeshes = _coarseTerrain != null
            ? new RegionGpuMesh?[terrain.StorageRegionCount]
            : Array.Empty<RegionGpuMesh?>();
        _visibleRegionMeshes = new RegionGpuMesh?[terrain.StorageRegionCount];

        Material material = LoadMaterialDefault();
        if (_mode == TerrainRenderMode.Voxel)
        {
            _voxelShader = LoadShaderFromMemory(
                VoxelVertexShader,
                VoxelFragmentShader);
            if (!IsShaderValid(_voxelShader))
            {
                UnloadMaterial(material);
                throw new InvalidOperationException(
                    "The voxel cutaway shader could not be created by the active graphics backend.");
            }

            _cutEnabledLocation = GetShaderLocation(_voxelShader, "uCutEnabled");
            _cutHeightLocation = GetShaderLocation(_voxelShader, "uCutHeight");
            _sunDirectionLocation = GetShaderLocation(_voxelShader, "uSunDirection");
            _ambientLightLocation = GetShaderLocation(_voxelShader, "uAmbientLight");
            _sunIntensityLocation = GetShaderLocation(_voxelShader, "uSunIntensity");
            if (_cutEnabledLocation < 0 ||
                _cutHeightLocation < 0 ||
                _sunDirectionLocation < 0 ||
                _ambientLightLocation < 0 ||
                _sunIntensityLocation < 0)
            {
                UnloadShader(_voxelShader);
                UnloadMaterial(material);
                throw new InvalidOperationException(
                    "The voxel cutaway shader is missing a required uniform.");
            }

            material.Shader = _voxelShader;
            _hasVoxelShader = true;
        }
        else
        {
            _voxelShader = default;
            _cutEnabledLocation = -1;
            _cutHeightLocation = -1;
            _sunDirectionLocation = -1;
            _ambientLightLocation = -1;
            _sunIntensityLocation = -1;
            _hasVoxelShader = false;
        }

        _material = material;

        // Mesh creation is deliberately lazy. Only visible storage regions are
        // meshed, with a bounded per-frame upload budget.
        Synchronize(terrain, _coarseTerrain);
    }

    public void Synchronize(
        TerrainRenderSnapshot terrain,
        TerrainRenderSnapshot? coarseTerrain,
        bool forceRebuild = false)
    {
        ThrowIfDisposed();
        InvalidateMeshes(_regionMeshes, forceRebuild);

        if (_coarseRegionMeshes.Length > 0)
            InvalidateMeshes(_coarseRegionMeshes, forceRebuild);
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

            DrawCallsLastFrame = _densityVoxelRenderer.DrawCallsLastFrame;
            VisibleRegionsLastFrame = _densityVoxelRenderer.VisibleChunksLastFrame;
            RebuiltRegions = _densityVoxelRenderer.RebuiltChunks;
            CutSurfaceRebuilds = _densityVoxelRenderer.RebuiltCutCaps;
            _activeVoxelCellSize = _densityVoxelRenderer.ActiveRenderCellSize;
            return;
        }

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

        bool canUseCoarseLod = _mode == TerrainRenderMode.Voxel &&
            _coarseTerrain != null;
        if (canUseCoarseLod)
        {
            bool nextCoarseState = _usingCoarseLod
                ? cameraDistance > FineLodEnterDistanceMeters
                : cameraDistance >= FineLodExitDistanceMeters;

            // Keep the fine cache when switching to coarse LOD. Re-entering
            // close view can then reuse existing meshes instead of blanking and
            // rebuilding the visible terrain every time the zoom crosses LOD.
            _usingCoarseLod = nextCoarseState;
        }

        TerrainRenderSnapshot drawTerrain =
            _usingCoarseLod && canUseCoarseLod
                ? _coarseTerrain!
                : terrain;
        RegionGpuMesh?[] drawMeshes =
            _usingCoarseLod && canUseCoarseLod
                ? _coarseRegionMeshes
                : _regionMeshes;

        _activeVoxelCellSize = drawTerrain.RegionSize;

        int visibleRegionCount = 0;
        int visibleDrawCalls = 0;
        int baseBuildsThisFrame = 0;
        int cutBuildsThisFrame = 0;
        int maximumBaseBuilds = canUseCoarseLod && !_usingCoarseLod
            ? MaximumFineMeshBuildsPerFrame
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
        DrawCallsLastFrame = visibleDrawCalls;
        VisibleRegionsLastFrame = visibleRegionCount;
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

    private static void ClearCutMeshes(RegionGpuMesh region)
    {
        foreach (Mesh mesh in region.CutMeshes)
            UnloadMesh(mesh);

        region.CutMeshes = Array.Empty<Mesh>();
        region.CutSurfaceLayer = int.MinValue;
    }

    public void SetPalette(TerrainPalette palette)
    {
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));
        _paletteVersion++;\n        _densityVoxelRenderer?.SetPalette(palette);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _densityVoxelRenderer?.Dispose();\n        DisposeMeshes(_regionMeshes);
        DisposeMeshes(_coarseRegionMeshes);

        UnloadMaterial(_material);
        if (_hasVoxelShader)
            UnloadShader(_voxelShader);
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
