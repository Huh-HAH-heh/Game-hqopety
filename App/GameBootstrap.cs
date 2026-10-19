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
                regionsX: 48,
                regionsY: 48);

        WorldGenerator.Generate(
            worldMap);

        GameRenderer renderer =
            new GameRenderer(
                worldMap);

        renderer.Run();
    }
}
