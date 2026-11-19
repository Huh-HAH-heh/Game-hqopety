using Core.Map;
using RimClone.Render;
using RimClone.Render.RaylibBackend;
using World;

namespace RimClone.App;

public static class GameBootstrap
{
    public static void Run(string[]? args = null)
    {
        Console.WriteLine("Запуск RimClone...");

        WorldMap worldMap =
            new WorldMap(
                regionsX: 12,
                regionsY: 12);

        WorldGenerator.Generate(worldMap);

        bool useLegacyRenderer = Array.Exists(
            args ?? Array.Empty<string>(),
            arg => string.Equals(
                arg,
                "--legacy-renderer",
                StringComparison.OrdinalIgnoreCase));

        if (useLegacyRenderer)
        {
            Console.WriteLine("[Render] Запуск резервного SFML-рендера.");
            GameRenderer legacyRenderer = new GameRenderer(worldMap);
            legacyRenderer.Run();
            return;
        }

        Console.WriteLine("[Render] Запуск нового Raylib terrain renderer.");
        RaylibWorldViewer.Run(worldMap);
    }
}
