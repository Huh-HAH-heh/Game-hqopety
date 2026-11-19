using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// Owns GPU resources and submits only visible, revision-valid region meshes.
/// All GPU creation, upload, drawing and disposal happen on the render thread.
/// </summary>
internal sealed class RaylibTerrainRenderer : IDisposable
{
    private readonly RegionGpuMesh[] _regionMeshes;
    private readonly Material _material;
    private bool _disposed;

    public int DrawCallsLastFrame { get; private set; }
    public int VisibleRegionsLastFrame { get; private set; }
    public int RebuiltRegions { get; private set; }

    public RaylibTerrainRenderer(TerrainRenderSnapshot terrain)
    {
        _regionMeshes = new RegionGpuMesh[terrain.RegionCount];
        _material = LoadMaterialDefault();
        Synchronize(terrain);
    }

    public void Synchronize(TerrainRenderSnapshot terrain)
    {
        ThrowIfDisposed();

        for (int regionY = 0; regionY < terrain.RegionsY; regionY++)
        {
            for (int regionX = 0; regionX < terrain.RegionsX; regionX++)
            {
                int index = regionX + regionY * terrain.RegionsX;
                RegionGpuMesh? existing = _regionMeshes[index];
                long version = terrain.RegionVersions[index];

                if (existing != null && existing.RegionVersion == version)
                    continue;

                existing?.Dispose();
                _regionMeshes[index] = RegionMeshBuilder.Build(
                    terrain,
                    regionX,
                    regionY);

                RebuiltRegions++;
            }
        }
    }

    public void Draw(
        TerrainRenderSnapshot terrain,
        Camera3D camera,
        int viewportWidth,
        int viewportHeight)
    {
        ThrowIfDisposed();

        int drawCalls = 0;
        int visibleRegions = 0;

        BeginMode3D(camera);

        for (int i = 0; i < _regionMeshes.Length; i++)
        {
            RegionGpuMesh? region = _regionMeshes[i];
            if (region == null || !region.IsUploaded)
                continue;

            if (!TerrainFrustum.IsSphereVisible(
                camera,
                region.BoundsCenter,
                region.BoundsRadius,
                viewportWidth,
                viewportHeight))
            {
                continue;
            }

            DrawMesh(region.Mesh, _material, Matrix4x4.Identity);
            drawCalls++;
            visibleRegions++;
        }

        EndMode3D();

        DrawCallsLastFrame = drawCalls;
        VisibleRegionsLastFrame = visibleRegions;
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
