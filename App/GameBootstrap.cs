using Core.Map;
using RimClone.Render;
using World;

namespace RimClone.App;

public static class GameBootstrap
{
    public static void Run()
    {
        Console.WriteLine(
            "Запуск RimClone...");

        WorldMap worldMap =
            new WorldMap(
                regionsX: 16,
                regionsY: 16);

        WorldGenerator.Generate(
            worldMap);

        GameRenderer renderer =
            new GameRenderer(
                worldMap);

        renderer.Run();
    }
}
