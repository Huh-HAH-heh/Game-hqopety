using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// Owns GPU resources and submits only visible, revision-valid storage-region
/// batches. All GPU work is restricted to the render thread.
/// </summary>
internal sealed class RaylibTerrainRenderer : IDisposable
{
    private readonly RegionGpuMesh[] _regionMeshes;
    private readonly RegionGpuMesh?[] _visibleRegionMeshes;
    private readonly Material _material;
    private TerrainPalette _palette;
    private int _paletteVersion;
    private bool _disposed;

    public int DrawCallsLastFrame { get; private set; }
    public int VisibleRegionsLastFrame { get; private set; }
    public int RebuiltRegions { get; private set; }

    public RaylibTerrainRenderer(TerrainRenderSnapshot terrain, TerrainPalette palette)
    {
        _palette = palette;
        _regionMeshes = new RegionGpuMesh[terrain.StorageRegionCount];
        _visibleRegionMeshes = new RegionGpuMesh?[terrain.StorageRegionCount];
        _material = LoadMaterialDefault();
        Synchronize(terrain);
    }

    public void Synchronize(
        TerrainRenderSnapshot terrain,
        bool forceRebuild = false)
    {
        ThrowIfDisposed();

        for (int regionY = 0; regionY < terrain.StorageRegionsY; regionY++)
        {
            for (int regionX = 0; regionX < terrain.StorageRegionsX; regionX++)
            {
                int index = regionX + regionY * terrain.StorageRegionsX;
                RegionGpuMesh? existing = _regionMeshes[index];
                long version = terrain.SourceRegionVersions[index];

                if (!forceRebuild &&
                    existing != null &&
                    existing.RegionVersion == version)
                {
                    continue;
                }

                existing?.Dispose();
                _regionMeshes[index] = RegionMeshBuilder.Build(
                    terrain,
                    regionX,
                    regionY,
                    _palette,
                    _paletteVersion);

                RebuiltRegions++;
            }
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
        int cutawayLayer = terrain.CutawayEnabled
            ? terrain.CutawayLayer
            : -1;

        // Rebuild only batches visible to the current camera. Moving the cut
        // plane therefore does not upload the entire 2304x2304 world every step.
        for (int i = 0; i < _regionMeshes.Length; i++)
        {
            RegionGpuMesh? region = _regionMeshes[i];
            if (region == null)
                continue;

            if (!TerrainFrustum.IsSphereVisible(
                camera,
                region.BoundsCenter,
                region.BoundsRadius,
                viewportWidth,
                viewportHeight,
                maximumViewDistanceMeters))
            {
                continue;
            }

            if (region.RegionVersion != terrain.SourceRegionVersions[i] ||
                region.CutawayLayer != cutawayLayer ||
                region.PaletteVersion != _paletteVersion)
            {
                region.Dispose();

                int regionX = i % terrain.StorageRegionsX;
                int regionY = i / terrain.StorageRegionsX;
                region = RegionMeshBuilder.Build(
                    terrain,
                    regionX,
                    regionY,
                    _palette,
                    _paletteVersion);
                _regionMeshes[i] = region;
                RebuiltRegions++;
            }

            if (!region.IsUploaded)
                continue;

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

            _visibleRegionMeshes[i] = null;
        }

        EndMode3D();

        DrawCallsLastFrame = visibleDrawCalls;
        VisibleRegionsLastFrame = visibleRegionCount;
    }

    public void SetPalette(TerrainPalette palette)
    {
        _palette = palette;
        _paletteVersion++;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (RegionGpuMesh? region in _regionMeshes)
            region?.Dispose();

        UnloadMaterial(_material);
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
