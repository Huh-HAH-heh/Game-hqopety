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
    private const int MaximumBaseMeshBuildsPerFrame = 6;
    private const int MaximumCutSurfaceBuildsPerFrame = 12;

    private const string VoxelVertexShader = """
        #version 330
        in vec3 vertexPosition;
        in vec4 vertexColor;

        uniform mat4 mvp;
        uniform mat4 matModel;

        out vec3 fragWorldPosition;
        out vec4 fragColor;

        void main()
        {
            vec4 worldPosition = matModel * vec4(vertexPosition, 1.0);
            fragWorldPosition = worldPosition.xyz;
            fragColor = vertexColor;
            gl_Position = mvp * vec4(vertexPosition, 1.0);
        }
        """;

    private const string VoxelFragmentShader = """
        #version 330
        in vec3 fragWorldPosition;
        in vec4 fragColor;

        uniform int uCutEnabled;
        uniform float uCutHeight;

        out vec4 finalColor;

        void main()
        {
            if (uCutEnabled != 0 && fragWorldPosition.y >= uCutHeight - 0.0001)
                discard;

            finalColor = fragColor;
        }
        """;

    private readonly RegionGpuMesh?[] _regionMeshes;
    private readonly RegionGpuMesh?[] _visibleRegionMeshes;
    private readonly Material _material;
    private readonly Shader _voxelShader;
    private readonly bool _hasVoxelShader;
    private readonly int _cutEnabledLocation;
    private readonly int _cutHeightLocation;
    private readonly TerrainRenderMode _mode;

    private TerrainPalette _palette;
    private int _paletteVersion;
    private bool _disposed;

    public int DrawCallsLastFrame { get; private set; }
    public int VisibleRegionsLastFrame { get; private set; }
    public int RebuiltRegions { get; private set; }
    public int CutSurfaceRebuilds { get; private set; }

    public RaylibTerrainRenderer(
        TerrainRenderSnapshot terrain,
        TerrainPalette palette,
        TerrainRenderMode mode)
    {
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));
        _mode = mode;

        _regionMeshes = new RegionGpuMesh?[terrain.StorageRegionCount];
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
            if (_cutEnabledLocation < 0 || _cutHeightLocation < 0)
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
            _hasVoxelShader = false;
        }

        _material = material;

        // Mesh creation is deliberately lazy. Only visible storage regions are
        // meshed, with a bounded per-frame upload budget.
        Synchronize(terrain);
    }

    public void Synchronize(TerrainRenderSnapshot terrain, bool forceRebuild = false)
    {
        ThrowIfDisposed();

        for (int i = 0; i < _regionMeshes.Length; i++)
        {
            RegionGpuMesh? existing = _regionMeshes[i];
            long version = terrain.SourceRegionVersions[i];

            if (existing == null ||
                (!forceRebuild && existing.RegionVersion == version))
            {
                continue;
            }

            existing.Dispose();
            _regionMeshes[i] = null;
        }
    }

    public void Draw(
        TerrainRenderSnapshot terrain,
        Camera3D camera,
        int viewportWidth,
        int viewportHeight,
        float maximumViewDistanceMeters)
    {
        ThrowIfDisposed();

        int visibleRegionCount = 0;
        int visibleDrawCalls = 0;
        int baseBuildsThisFrame = 0;
        int cutBuildsThisFrame = 0;
        int cutLayer = terrain.CutawayEnabled ? terrain.CutawayLayer : -1;

        for (int i = 0; i < _regionMeshes.Length; i++)
        {
            RegionGpuMesh? region = _regionMeshes[i];
            GetRegionBounds(terrain, i, out Vector3 center, out float radius);

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
                region.RegionVersion != terrain.SourceRegionVersions[i] ||
                region.PaletteVersion != _paletteVersion ||
                (_mode == TerrainRenderMode.HeightmapFallback &&
                 region.CutawayLayer != cutLayer);

            if (baseIsStale)
            {
                if (baseBuildsThisFrame >= MaximumBaseMeshBuildsPerFrame)
                    continue;

                region?.Dispose();

                int regionX = i % terrain.StorageRegionsX;
                int regionY = i / terrain.StorageRegionsX;
                region = _mode == TerrainRenderMode.Voxel
                    ? RegionMeshBuilder.BuildVoxel(
                        terrain, regionX, regionY, _palette, _paletteVersion)
                    : RegionMeshBuilder.Build(
                        terrain, regionX, regionY, _palette, _paletteVersion);

                _regionMeshes[i] = region;
                RebuiltRegions++;
                baseBuildsThisFrame++;
            }

            if (region == null || !region.IsUploaded)
                continue;

            if (_mode == TerrainRenderMode.Voxel && terrain.CutawayEnabled)
            {
                if (region.CutSurfaceLayer != cutLayer &&
                    cutBuildsThisFrame < MaximumCutSurfaceBuildsPerFrame)
                {
                    ClearCutMeshes(region);
                    int regionX = i % terrain.StorageRegionsX;
                    int regionY = i / terrain.StorageRegionsX;

                    region.CutMeshes = RegionMeshBuilder.BuildVoxelCutSurface(
                        terrain,
                        regionX,
                        regionY,
                        cutLayer,
                        _palette);
                    region.CutSurfaceLayer = cutLayer;
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
            enabled: _mode == TerrainRenderMode.Voxel && terrain.CutawayEnabled,
            height: terrain.CutawayHeightMeters);

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

        if (_mode == TerrainRenderMode.Voxel && terrain.CutawayEnabled)
        {
            // The base volume is clipped in the GPU. Cut caps are separate
            // meshes, rendered without clipping and rebuilt independently.
            SetVoxelCutShader(enabled: false, height: terrain.CutawayHeightMeters);

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
            terrain.MaximumHeightMeters * 0.5f,
            (regionY + 0.5f) * terrain.StorageRegionSize);
        radius = MathF.Sqrt(
            terrain.StorageRegionSize * terrain.StorageRegionSize * 0.5f +
            terrain.MaximumHeightMeters * terrain.MaximumHeightMeters * 0.25f);
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
        _paletteVersion++;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (RegionGpuMesh? region in _regionMeshes)
            region?.Dispose();

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
