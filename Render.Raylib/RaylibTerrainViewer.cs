using Core.Map;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// Standalone presentation loop. World generation and simulation remain outside
/// this assembly; the viewer consumes a data-oriented terrain snapshot.
/// </summary>
public static class RaylibTerrainViewer
{
    public static void Run(WorldMap worldMap)
    {
        ArgumentNullException.ThrowIfNull(worldMap);

        TerrainRenderSnapshot terrain =
            TerrainRenderSnapshot.Create(worldMap);

        SetConfigFlags(
            ConfigFlags.ResizableWindow |
            ConfigFlags.VSyncHint |
            ConfigFlags.Msaa4xHint);

        InitWindow(1600, 900, "RimClone | Raylib Terrain Viewer");
        SetTargetFPS(144);
        SetExitKey(KeyboardKey.Escape);

        OrbitCameraController camera = new(terrain);
        RaylibTerrainRenderer? renderer = null;

        try
        {
            renderer = new RaylibTerrainRenderer(terrain);

            Console.WriteLine(
                "[Raylib] Terrain viewer started. " +
                $"render regions: {terrain.RegionsX}x{terrain.RegionsY} " +
                $"({terrain.RegionSize}x{terrain.RegionSize} tiles), " +
                $"GPU batches: {terrain.StorageRegionsX}x{terrain.StorageRegionsY}, " +
                $"world: {terrain.WorldWidth:0}x{terrain.WorldDepth:0} m, " +
                $"maximum height: {terrain.MaximumHeightMeters:0.0} m.");

            while (!WindowShouldClose())
            {
                float deltaTime = Math.Clamp(GetFrameTime(), 0f, 0.05f);

                camera.Update(deltaTime);

                if (terrain.RefreshFromWorld(worldMap))
                    renderer.Synchronize(terrain);

                BeginDrawing();
                ClearBackground(new Color(0, 0, 0, 255));

                renderer.Draw(
                    terrain,
                    camera.Camera,
                    GetScreenWidth(),
                    GetScreenHeight());

                DrawHud(terrain, renderer);
                EndDrawing();
            }
        }
        finally
        {
            camera.ReleaseMouse();
            renderer?.Dispose();
            CloseWindow();
        }
    }

    private static void DrawHud(
        TerrainRenderSnapshot terrain,
        RaylibTerrainRenderer renderer)
    {
        DrawRectangle(12, 12, 448, 178, new Color(0, 0, 0, 235));

        DrawText("RIMCLONE / TOP-DOWN TERRAIN", 24, 22, 18, Color.RayWhite);
        DrawText(
            $"FPS {GetFPS()}   FRAME {GetFrameTime() * 1000f:0.00} ms",
            24,
            50,
            17,
            Color.Lime);

        DrawText(
            $"RENDER REGIONS {terrain.RegionsX}x{terrain.RegionsY} @ {terrain.RegionSize}x{terrain.RegionSize}",
            24,
            76,
            14,
            Color.LightGray);

        DrawText(
            $"VISIBLE GPU BATCHES {renderer.VisibleRegionsLastFrame}/{terrain.StorageRegionCount}   DRAWS {renderer.DrawCallsLastFrame}",
            24,
            98,
            14,
            Color.LightGray);

        DrawText(
            $"GPU MESH REBUILDS {renderer.RebuiltRegions}",
            24,
            120,
            14,
            Color.LightGray);

        DrawText(
            "WASD move | RMB orbit | MMB pan | Wheel zoom | Shift boost | Q/E altitude | Esc exit",
            24,
            196,
            14,
            Color.RayWhite);
    }
}
