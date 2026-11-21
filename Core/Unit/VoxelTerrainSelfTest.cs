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
        const int materialSampleY = 43;
        ushort bridgeMaterial = source.GetMaterialId(30, materialSampleY, 30);

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

        int restoredChunkCount = restored.ChunkCount;
        float restoredCarvedDensity = restored.GetDensity(30, 12, 30);
        ushort restoredBridgeMaterial = materialSampleY >= 0
            ? restored.GetMaterialId(30, materialSampleY, 30)
            : (ushort)0;
        float materialDensity = materialSampleY >= 0
            ? source.GetDensity(30, materialSampleY, 30)
            : 0f;
        bool chunksMatch = restoredChunkCount == source.ChunkCount;
        bool densityMatches = MathF.Abs(restoredCarvedDensity - carvedDensity) < 0.001f;
        bool materialMatches = restoredBridgeMaterial == bridgeMaterial &&
            bridgeMaterial == BridgeMaterial;
        bool surfacesMatch = surfaceCount >= 3;

        if (!chunksMatch || !densityMatches || !materialMatches || !surfacesMatch)
        {
            Console.WriteLine(
                $"[DETAIL] voxel save: chunks={restoredChunkCount}/{source.ChunkCount}, " +
                $"density={restoredCarvedDensity:0.000}/{carvedDensity}, " +
                $"material={restoredBridgeMaterial}/{bridgeMaterial}, " +
                $"materialY={materialSampleY}, materialDensity={materialDensity:0.000}, " +
                $"nearby=" +
                $"{source.GetMaterialId(30, 40, 30)}/" +
                $"{source.GetMaterialId(30, 41, 30)}/" +
                $"{source.GetMaterialId(30, 42, 30)}/" +
                $"{source.GetMaterialId(30, 43, 30)}, surfaces={surfaceCount}.");
        }

        return chunksMatch && densityMatches && materialMatches && surfacesMatch;
    }

    public static bool FlattenPlaneChangesLocalSurface()
    {
        SparseVoxelField field = CreateField();
        field.FlattenPlane(
            centerMeters: new Vector3(3f, 1.5f, 3f),
            normal: Vector3.UnitY,
            radiusMeters: 1.5f,
            materialId: BridgeMaterial,
            strength: 1f);

        Span<SparseVoxelField.WalkableSurface> surfaces =
            stackalloc SparseVoxelField.WalkableSurface[16];
        int count = field.FindWalkableSurfacesAt(
            3f, 3f, minimumUpNormal: 0.72f, surfaces);
        for (int i = 0; i < count; i++)
        {
            if (MathF.Abs(surfaces[i].HeightMeters - 1.5f) <= 0.12f)
                return true;
        }

        return false;
    }

    public static bool BoxBrushChangesOnlyIts3DVolume()
    {
        SparseVoxelField field = CreateField();
        float insideBefore = field.GetDensity(30, 15, 30);
        float outsideBefore = field.GetDensity(80, 15, 30);

        field.CarveBox(
            new Vector3(2.5f, 1.4f, 2.5f),
            new Vector3(3.5f, 2.2f, 3.5f));

        float insideAfter = field.GetDensity(30, 15, 30);
        float outsideAfter = field.GetDensity(80, 15, 30);
        return insideBefore > 0f &&
               insideAfter <= 0f &&
               MathF.Abs(outsideAfter - outsideBefore) < 0.001f;
    }

    public static bool SaveRejectsDifferentBaseWorld()
    {
        SparseVoxelField source = CreateField();
        source.CarveBox(
            new Vector3(1f, 0.5f, 1f),
            new Vector3(5f, 1.8f, 5f));
        using MemoryStream save = new();
        source.SaveEdits(save, worldSignature: 0xA17B_02UL);

        SparseVoxelField destination = CreateField();
        destination.AddSphere(new Vector3(3f, 2f, 3f), 0.4f, BridgeMaterial);
        int originalChunkCount = destination.ChunkCount;
        float originalDensity = destination.GetDensity(30, 20, 30);
        save.Position = 0;

        bool rejected = false;
        try
        {
            destination.LoadEdits(save, expectedWorldSignature: 0xBEEF_91UL);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        return rejected &&
               destination.ChunkCount == originalChunkCount &&
               MathF.Abs(destination.GetDensity(30, 20, 30) - originalDensity) < 0.001f;
    }

    public static bool UndoRedoRestoresSdf()
    {
        SparseVoxelField field = CreateField();
        float original = field.GetDensity(30, 12, 30);

        using (field.BeginUndoableEdit())
        {
            field.CarveBox(
                new Vector3(1f, 0.5f, 1f),
                new Vector3(5f, 1.8f, 5f));
        }

        float carved = field.GetDensity(30, 12, 30);
        bool canUndo = field.CanUndo && field.UndoCount == 1;
        bool didUndo = field.Undo();
        bool undoRestored = canUndo && didUndo &&
            MathF.Abs(field.GetDensity(30, 12, 30) - original) < 0.001f &&
            field.CanRedo;
        bool didRedo = field.Redo();
        bool redoRestored = didRedo &&
            MathF.Abs(field.GetDensity(30, 12, 30) - carved) < 0.001f &&
            field.CanUndo;

        return original > 0f && carved < 0f &&
               undoRestored && redoRestored;
    }

    public static bool VerticalGravityFindsTunnelFloor()
    {
        WorldMap worldMap = new(regionsX: 1, regionsY: 1, layerCount: 64);
        for (int y = 0; y < worldMap.TileHeight; y++)
        for (int x = 0; x < worldMap.TileWidth; x++)
            worldMap.SetSolidHeight(x, y, 20, Rock);

        worldMap.Voxels.CarveBox(
            new Vector3(2f, 0.5f, 2f),
            new Vector3(4f, 2.5f, 4f));

        Vector3 position = new(3f, 3f, 3f);
        float verticalVelocity = 0f;
        for (int i = 0; i < 180; i++)
            VoxelGroundPhysics.Step(
                worldMap,
                ref position,
                ref verticalVelocity,
                1f / 60f,
                radiusMeters: 0.2f,
                bodyHeightMeters: 1.1f);

        return MathF.Abs(position.Z - 0.5f) <= 0.12f &&
               MathF.Abs(verticalVelocity) <= 0.01f &&
               worldMap.IsVoxelCapsuleClear(
                   position.X, position.Z, position.Y, 0.2f, 1.1f);
    }

    public static bool DistantVoxelEditsKeepLocalNavCache()
    {
        WorldMap worldMap = new(regionsX: 9, regionsY: 1, layerCount: 64);
        for (int y = 0; y < worldMap.TileHeight; y++)
        for (int x = 0; x < worldMap.TileWidth; x++)
            worldMap.SetSolidHeight(x, y, 20, Rock);

        BuildTunnelAndBridge(worldMap.Voxels);
        VoxelNavigationSystem navigation = new();
        UnitId unit = new(1, 1);
        Vector3 start = new(2.25f, 3.25f, 0.5f);
        Vector3 goal = new(4.25f, 3.25f, 0.5f);

        if (!navigation.TryGetWaypoint(
                worldMap, unit, start, goal, 1.1f,
                out bool ownsVoxelRegion, out _) ||
            !ownsVoxelRegion ||
            navigation.CachedRegionCount != 1 ||
            navigation.CachedRouteCount != 1)
            return false;

        // The second edit lives in the next 128 m navigation region.
        worldMap.CarveVoxelSphere(new Vector3(132f, 1f, 3f), 0.6f);
        if (navigation.CachedRegionCount != 1 ||
            navigation.CachedRouteCount != 1)
            return false;

        return navigation.TryGetWaypoint(
                   worldMap, unit, start, goal, 1.1f,
                   out ownsVoxelRegion, out _) &&
               ownsVoxelRegion &&
               navigation.CachedRegionCount == 1 &&
               navigation.CachedRouteCount == 1;
    }

    public static bool NavigationCrossesRegionPortals()
    {
        WorldMap worldMap = new(regionsX: 9, regionsY: 1, layerCount: 64);
        for (int y = 0; y < worldMap.TileHeight; y++)
        for (int x = 0; x < worldMap.TileWidth; x++)
            worldMap.SetSolidHeight(x, y, 20, Rock);

        // Tunnel spans the 128 m boundary and has enough body clearance.
        worldMap.Voxels.CarveBox(
            new Vector3(126f, 0.5f, 2f),
            new Vector3(130f, 1.8f, 5f));

        VoxelNavigationSystem navigation = new();
        bool found = navigation.TryGetWaypoint(
            worldMap,
            new UnitId(2, 1),
            new Vector3(126.25f, 3.25f, 0.5f),
            new Vector3(129.25f, 3.25f, 0.5f),
            bodyHeight: 1.1f,
            out bool ownsVoxelRegion,
            out Vector3 waypoint);

        if (!found || !ownsVoxelRegion ||
            MathF.Abs(waypoint.Z - 0.5f) > 0.2f ||
            navigation.CachedRegionCount < 2 ||
            navigation.CachedRouteCount != 1)
            return false;

        return navigation.TryGetWaypoint(
                   worldMap,
                   new UnitId(2, 1),
                   new Vector3(126.25f, 3.25f, 0.5f),
                   new Vector3(129.25f, 3.25f, 0.5f),
                   bodyHeight: 1.1f,
                   out ownsVoxelRegion,
                   out _) &&
               ownsVoxelRegion &&
               navigation.CachedRouteCount == 1;
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

        bool lowerFloorSelected = found &&
            ownsVoxelRegion &&
            MathF.Abs(waypoint.Z - 0.5f) <= 0.2f;
        if (!lowerFloorSelected)
        {
            Console.WriteLine(
                $"[DETAIL] voxel route: found={found}, owns={ownsVoxelRegion}, " +
                $"waypoint=({waypoint.X:0.00},{waypoint.Y:0.00},{waypoint.Z:0.00}), " +
                $"terrainVersion={worldMap.TerrainVersion}, chunks={worldMap.Voxels.ChunkCount}.");
        }
        return lowerFloorSelected;
    }
}
