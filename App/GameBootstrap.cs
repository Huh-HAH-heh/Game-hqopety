using Core.AI;
using Core.Items;
using Core.Map;
using Core.Unit;
using RimClone.Render;
using System;
using Units;
using World;

namespace RimClone.App;

public static class GameBootstrap
{
    public static void Run()
    {
        Console.WriteLine(
            "Запуск многоэтажной симуляции RimClone...");

        WorldMap worldMap =
            new WorldMap(
                minZ: -3,
                maxZ: 3);

        EdificeStore edificeStore =
            new EdificeStore(
                maxEdifices: 2000,
                maxConfigs: 50);

        WorldGenerator.Generate(
            worldMap,
            edificeStore);

        UnitSpatialGrid spatialGrid =
            new UnitSpatialGrid(
                maxUnits: 800,
                regionsX: 16,
                regionsY: 16,
                subDivision: 3);

        UnitStore unitStore =
            new UnitStore(
                maxUnits: 800);

        TestUnitSpawner.Spawn(
            unitStore,
            spatialGrid);

        VectorRenderer renderer =
            new VectorRenderer();

        renderer.InitializeAndRun(
            worldMap,
            unitStore,
            spatialGrid,
            edificeStore);
    }
}