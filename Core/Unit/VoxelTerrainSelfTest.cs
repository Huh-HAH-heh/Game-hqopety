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

    public static bool RampBrushCreatesSlopedVolume()
    {
        SparseVoxelField field = CreateField();
        field.AddRamp(
            startMeters: new Vector3(1f, 2f, 2f),
            endMeters: new Vector3(5f, 2f, 2f),
            widthMeters: 1f,
            thicknessMeters: 1f,
            materialId: BridgeMaterial);

        float lowRamp = field.GetDensity(14, 20, 20);
        float highRamp = field.GetDensity(48, 28, 20);
        float outside = field.GetDensity(14, 30, 20);
        ushort material = field.GetMaterialId(48, 28, 20);
        bool passed = lowRamp > 0f &&
            highRamp > 0f &&
            outside < 0f &&
            material == BridgeMaterial;

        if (!passed)
        {
            Console.WriteLine(
                $"[DETAIL] ramp add: low={lowRamp:0.000}, high={highRamp:0.000}, " +
                $"outside={outside:0.000}, material={material}.");
        }

        return passed;
    }

    public static bool RampBrushCarvesSlopedVolume()
    {
        SparseVoxelField field = new(
            width: 64,
            height: 64,
            depth: 64,
            baseDensity: static (_, y, _) => 30f - (y + 0.5f),
            baseMaterial: static (_, y, _) => y < 30 ? Rock : (ushort)0);

        float outsideBefore = field.GetDensity(48, 28, 30);
        field.CarveRamp(
            startMeters: new Vector3(1f, 3f, 2f),
            endMeters: new Vector3(5f, 3f, 2f),
            widthMeters: 1f,
            thicknessMeters: 1f);

        float rampInterior = field.GetDensity(48, 28, 20);
        float outsideAfter = field.GetDensity(48, 28, 30);
        return rampInterior < 0f &&
               outsideBefore > 0f &&
               MathF.Abs(outsideAfter - outsideBefore) < 0.001f;
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

    public static bool LegacyWorldMapQueriesUseVoxelField()
    {
        WorldMap map = new(regionsX: 1, regionsY: 1, layerCount: 64);
        for (int y = 0; y < map.TileHeight; y++)
        for (int x = 0; x < map.TileWidth; x++)
            map.SetSolidHeight(x, y, 20, Rock);

        map.Voxels.CarveBox(
            new Vector3(1f, 0.5f, 1f),
            new Vector3(5f, 1.8f, 5f));
        map.Voxels.FillBox(
            new Vector3(1f, 4f, 1f),
            new Vector3(5f, 4.4f, 5f),
            BridgeMaterial);

        float topHeight = map.GetSurfaceHeight(2, 2);
        ushort topHeightUnits = map.GetSurfaceHeightUnits(2, 2);
        int topLayer = map.GetSurfaceLayer(2, 2);
        int highestSolidLayer = map.FindTopOccupiedLayer(2, 2, 63);
        bool topQueriesSeeBridge =
            MathF.Abs(topHeight - 4.4f) <= 0.12f &&
            Math.Abs(topHeightUnits - 44) <= 1 &&
            topLayer >= 42 &&
            highestSolidLayer >= 42;

        bool bridgeSolid = map.IsColumnSolidBetween(2, 2, 40, 44);
        bool tunnelAir = !map.IsColumnSolidBetween(2, 2, 5, 17);
        bool bridgeFloorFound = map.TryFindWalkableSurface(
            2, 2, 0, 63, clearanceLayers: 8, out int floorLayer) &&
            floorLayer >= 43;
        int globalHighestLayer = map.GetHighestOccupiedLayer();
        bool globalMaximumSeesBridge = globalHighestLayer >= 43;

        map.Voxels.CarveBox(
            new Vector3(1f, 4f, 1f),
            new Vector3(5f, 4.4f, 5f));
        float restoredBaseHeight = map.GetSurfaceHeight(2, 2);

        return topQueriesSeeBridge &&
               bridgeSolid &&
               tunnelAir &&
               bridgeFloorFound &&
               globalMaximumSeesBridge &&
               MathF.Abs(restoredBaseHeight - 2f) <= 0.12f;
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

    public static bool ChunkBoundarySamplesMatchAcrossLods()
    {
        SparseVoxelField field = new(
            width: 1024,
            height: 1024,
            depth: 1024,
            baseDensity: static (_, y, _) => 1024f - (y + 0.5f),
            baseMaterial: static (_, _, _) => Rock);

        foreach (int step in new[] { 1, 5, 10, 40 })
        {
            for (int axis = 0; axis < 3; axis++)
            {
                int boundarySample = SparseVoxelField.ChunkSize * step;
                int previousSample = boundarySample - step;
                int nextSample = boundarySample + step;
                int fixedA = 40;
                int fixedB = 80;

                int centerX = axis == 0 ? boundarySample : fixedA;
                int centerY = axis == 1 ? boundarySample : fixedA;
                int centerZ = axis == 2 ? boundarySample : fixedB;

                field.CarveSphere(
                    new Vector3(
                        (centerX + 0.5f) * SparseVoxelField.VoxelSizeMeters,
                        (centerY + 0.5f) * SparseVoxelField.VoxelSizeMeters,
                        (centerZ + 0.5f) * SparseVoxelField.VoxelSizeMeters),
                    0.82f);

                int boundaryLeft = SparseVoxelField.GetChunkSampleCoordinate(0, 17, step);
                int boundaryRight = SparseVoxelField.GetChunkSampleCoordinate(1, 1, step);
                int previousLeft = SparseVoxelField.GetChunkSampleCoordinate(0, 16, step);
                int previousRight = SparseVoxelField.GetChunkSampleCoordinate(1, 0, step);
                int nextLeft = SparseVoxelField.GetChunkSampleCoordinate(0, 18, step);
                int nextRight = SparseVoxelField.GetChunkSampleCoordinate(1, 2, step);

                if (boundaryLeft != boundaryRight ||
                    previousLeft != previousRight ||
                    nextLeft != nextRight)
                    return false;

                int sampleX = axis == 0 ? boundaryLeft : fixedA;
                int sampleY = axis == 1 ? boundaryLeft : fixedA;
                int sampleZ = axis == 2 ? boundaryLeft : fixedB;

                int previousX = axis == 0 ? previousLeft : sampleX;
                int previousY = axis == 1 ? previousLeft : sampleY;
                int previousZ = axis == 2 ? previousLeft : sampleZ;
                int nextX = axis == 0 ? nextLeft : sampleX;
                int nextY = axis == 1 ? nextLeft : sampleY;
                int nextZ = axis == 2 ? nextLeft : sampleZ;

                float boundaryFromLeft = field.GetDensity(sampleX, sampleY, sampleZ);
                float boundaryFromRight = field.GetDensity(
                    axis == 0 ? boundaryRight : sampleX,
                    axis == 1 ? boundaryRight : sampleY,
                    axis == 2 ? boundaryRight : sampleZ);
                if (boundaryFromLeft >= 0f ||
                    MathF.Abs(boundaryFromLeft - boundaryFromRight) > 0.0001f)
                    return false;

                float previousFromLeft = field.GetDensity(previousX, previousY, previousZ);
                float previousFromRight = field.GetDensity(
                    axis == 0 ? previousRight : previousX,
                    axis == 1 ? previousRight : previousY,
                    axis == 2 ? previousRight : previousZ);
                float nextFromLeft = field.GetDensity(nextX, nextY, nextZ);
                float nextFromRight = field.GetDensity(
                    axis == 0 ? nextRight : nextX,
                    axis == 1 ? nextRight : nextY,
                    axis == 2 ? nextRight : nextZ);

                float gradientLeft = nextFromLeft - previousFromLeft;
                float gradientRight = nextFromRight - previousFromRight;
                if (MathF.Abs(gradientLeft - gradientRight) > 0.0001f)
                    return false;
            }
        }

        return field.ChunkCount >= 12;
    }

    public static bool NavigationKeepsBridgeFloor()
    {
        WorldMap worldMap = new(regionsX: 1, regionsY: 1, layerCount: 64);
        for (int y = 0; y < worldMap.TileHeight; y++)
        for (int x = 0; x < worldMap.TileWidth; x++)
            worldMap.SetSolidHeight(x, y, 20, Rock);

        BuildTunnelAndBridge(worldMap.Voxels);
        VoxelNavigationSystem navigation = new();
        bool found = navigation.TryGetWaypoint(
            worldMap,
            new UnitId(3, 1),
            new Vector3(2.25f, 3.25f, 4.4f),
            new Vector3(4.25f, 3.25f, 4.4f),
            bodyHeight: 1.1f,
            out bool ownsVoxelRegion,
            out Vector3 waypoint);

        return found && ownsVoxelRegion &&
            MathF.Abs(waypoint.Z - 4.4f) <= 0.2f;
    }

    public static bool NavigationRejectsInsufficientHeadroom()
    {
        WorldMap worldMap = new(regionsX: 1, regionsY: 1, layerCount: 64);
        for (int y = 0; y < worldMap.TileHeight; y++)
        for (int x = 0; x < worldMap.TileWidth; x++)
            worldMap.SetSolidHeight(x, y, 20, Rock);

        BuildTunnelAndBridge(worldMap.Voxels);
        VoxelNavigationSystem navigation = new();
        bool found = navigation.TryGetWaypoint(
            worldMap,
            new UnitId(4, 1),
            new Vector3(2.25f, 3.25f, 0.5f),
            new Vector3(4.25f, 3.25f, 0.5f),
            bodyHeight: 2.5f,
            out bool ownsVoxelRegion,
            out _);

        return ownsVoxelRegion && !found;
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
    public static bool SparseOctreeTracksEditedVoxelPages()
    {
        SparseVoxelField field = new(
            width: 512,
            height: 128,
            depth: 512,
            baseDensity: static (_, _, _) => -1f,
            baseMaterial: static (_, _, _) => (ushort)0);

        field.FillBox(
            new Vector3(1.1f, 1.1f, 1.1f),
            new Vector3(1.4f, 1.4f, 1.4f),
            Rock);
        field.FillBox(
            new Vector3(2.7f, 1.1f, 1.1f),
            new Vector3(3.0f, 1.4f, 1.4f),
            BridgeMaterial);

        if (field.GetBlockId(12, 12, 12) != Rock ||
            field.GetBlockId(28, 12, 12) != BridgeMaterial)
            return false;

        List<SparseVoxelField.ChunkCoordinate> edited = new();
        field.CollectEditedChunks(0, 0, 0, 1, 0, 0, edited);
        if (edited.Count != 2)
            return false;

        field.ResetBaseColumn(tileX: 1, tileZ: 1);

        field.CollectEditedChunks(0, 0, 0, 0, 0, 0, edited);
        if (edited.Count != 0)
            return false;

        field.CollectEditedChunks(1, 0, 0, 1, 0, 0, edited);
        if (edited.Count != 1 || edited[0].X != 1 || edited[0].Y != 0 || edited[0].Z != 0)
            return false;

        field.ClearEdits();
        field.CollectEditedChunks(0, 0, 0, 31, 7, 31, edited);
        return edited.Count == 0 && field.OctreeNodeCount == 0;
    }

}
