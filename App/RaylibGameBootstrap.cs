using Core.Map;
using RimClone.Render.Raylib;
using World;

namespace RimClone.App;

public static class RaylibGameBootstrap
{
    public static void Run()
    {
        Console.WriteLine("[Game] Preparing world for Raylib renderer...");

        WorldMap worldMap = new(
            regionsX: 12,
            regionsY: 12);

        WorldGenerator.Generate(worldMap);
        RaylibTerrainViewer.Run(worldMap);
    }
}
