using System;
using System.IO;
using System.Numerics;
using Core.Map;

namespace Core.Unit;

/// <summary>Regression tests for the 3D voxel representation and its consumers.</summary>
internal static class VoxelTerrainSelfTest
{
    private const ushort Rock = 1;
    private const ushort BridgeMaterial = 2;

    private static SparseVoxelField CreateField() =>
        new(
            width: 64,
            height: 64,
            depth: 64,
            baseDensity: static (_, y, _) => 20f - (y + 0.5f),
            baseMaterial: static (_, y, _) => y < 20 ? Rock : (ushort)0);

    private static void BuildTunnelAndBridge(SparseVoxelField field)
    {
        field.CarveBox(
            new Vector3(1f, 0.5f, 1f),
            new Vector3(5f, 1.8f, 5f));
        field.FillBox(
            new Vector3(1f, 4f, 1f),
            new Vector3(5f, 4.4f, 5f),
            BridgeMaterial);
    }

    public static bool StackedSurfacesAreFound()
    {
        SparseVoxelField field = CreateField();
        BuildTunnelAndBridge(field);
        Span<SparseVoxelField.WalkableSurface> surfaces =
            stackalloc SparseVoxelField.WalkableSurface[16];

        int count = field.FindWalkableSurfacesAt(
            3f, 3f, minimumUpNormal: 0.72f, surfaces);
        if (count < 3)
            return false;

        bool tunnelFloor = false;
        bool originalSurface = false;
        bool bridgeTop = false;
        for (int i = 0; i < count; i++)
        {
            float height = surfaces[i].HeightMeters;
            tunnelFloor |= MathF.Abs(height - 0.5f) <= 0.12f;
            originalSurface |= MathF.Abs(height - 2f) <= 0.12f;
            bridgeTop |= MathF.Abs(height - 4.4f) <= 0.12f;
        }

        return tunnelFloor && originalSurface && bridgeTop;
    }

    public static bool CapsuleUsesSdfClearance()
    {
        SparseVoxelField field = CreateField();
        BuildTunnelAndBridge(field);

        bool tunnelIsPassable = field.IsCapsuleClear(
            xMeters: 3f,
            feetHeightMeters: 0.5f,
            zMeters: 3f,
            radiusMeters: 0.2f,
            heightMeters: 1.1f);

        bool solidGroundBlocksPawn = !field.IsCapsuleClear(
            xMeters: 0.4f,
            feetHeightMeters: 0.5f,
            zMeters: 3f,
            radiusMeters: 0.2f,
            heightMeters: 1.1f);

        bool bridgeHasClearSpaceAbove = field.IsCapsuleClear(
            xMeters: 3f,
            feetHeightMeters: 4.4f,
            zMeters: 3f,
            radiusMeters: 0.2f,
            heightMeters: 1.1f);

        return tunnelIsPassable && solidGroundBlocksPawn && bridgeHasClearSpaceAbove;
    }

    public static bool EditSaveRoundTrips()
    {
        SparseVoxelField source = CreateField();
        BuildTunnelAndBridge(source);
        float carvedDensity = source.GetDensity(30, 12, 30);
        ushort bridgeMaterial = source.GetMaterialId(30, 43, 30);

        using MemoryStream save = new();
        source.SaveEdits(save);
        if (save.Length == 0)
            return false;

        SparseVoxelField restored = CreateField();
        save.Position = 0;
        restored.LoadEdits(save);

        Span<SparseVoxelField.WalkableSurface> surfaces =
            stackalloc SparseVoxelField.WalkableSurface[16];
        int surfaceCount = restored.FindWalkableSurfacesAt(
            3f, 3f, minimumUpNormal: 0.72f, surfaces);

        return restored.ChunkCount == source.ChunkCount &&
               MathF.Abs(restored.GetDensity(30, 12, 30) - carvedDensity) < 0.001f &&
               restored.GetMaterialId(30, 43, 30) == bridgeMaterial &&
               bridgeMaterial == BridgeMaterial &&
               surfaceCount >= 3;
    }

    public static bool NavigationKeepsLowerFloor()
    {
        WorldMap worldMap = new(regionsX: 1, regionsY: 1, layerCount: 64);
        for (int y = 0; y < worldMap.TileHeight; y++)
        for (int x = 0; x < worldMap.TileWidth; x++)
            worldMap.SetSolidHeight(x, y, 20, Rock);

        BuildTunnelAndBridge(worldMap.Voxels);
        VoxelNavigationSystem navigation = new();
        bool found = navigation.TryGetWaypoint(
            worldMap,
            new UnitId(0, 1),
            new Vector3(2.25f, 3.25f, 0.5f),
            new Vector3(4.25f, 3.25f, 0.5f),
            bodyHeight: 1.1f,
            out bool ownsVoxelRegion,
            out Vector3 waypoint);

        return found &&
               ownsVoxelRegion &&
               MathF.Abs(waypoint.Z - 0.5f) <= 0.2f;
    }
}
