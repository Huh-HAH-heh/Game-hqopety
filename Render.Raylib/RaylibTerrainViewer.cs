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
        bool cutawayMode = false;
        string focusName = "SURFACE";

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

            Console.WriteLine(
                "[Raylib] Cave locations: lower tunnel X 1134-1286, Z 1127-1159, H 14-20m; " +
                "upper passage X 1200-1220, Z 1127-1159, H 26-34m. F1/F2 focus, C surface.");

            while (!WindowShouldClose())
            {
                float deltaTime = Math.Clamp(GetFrameTime(), 0f, 0.05f);

                if (IsKeyPressed(KeyboardKey.F1))
                {
                    cutawayMode = true;
                    focusName = "LOWER TUNNEL";
                    FocusLowerTunnel(camera, terrain);
                }

                if (IsKeyPressed(KeyboardKey.F2))
                {
                    cutawayMode = true;
                    focusName = "UPPER PASSAGE";
                    FocusUpperPassage(camera, terrain);
                }

                if (IsKeyPressed(KeyboardKey.C))
                {
                    cutawayMode = !cutawayMode;

                    if (cutawayMode)
                    {
                        focusName = "LOWER TUNNEL";
                        FocusLowerTunnel(camera, terrain);
                    }
                    else
                    {
                        focusName = "SURFACE";
                        terrain.DisableCutaway();
                        renderer.Synchronize(terrain);
                        camera.ResetTopDownView();
                    }
                }

                camera.Update(deltaTime);

                bool terrainChanged = terrain.RefreshFromWorld(worldMap);

                if (cutawayMode)
                {
                    // The horizontal cut plane follows the camera focus,
                    // quantized to 0.5 m. Q/E move continuously; PageUp/PageDown
                    // and Shift+Wheel step by one metre.
                    const float cutStepMeters = 0.5f;
                    int layersPerCutStep = (int)MathF.Round(
                        cutStepMeters * WorldMap.HeightUnitsPerMeter);
                    int cutLayer = Math.Clamp(
                        (int)MathF.Round(
                            camera.Camera.Target.Y / cutStepMeters) *
                            layersPerCutStep,
                        0,
                        worldMap.LayerCount - 1);

                    terrain.SetCutawayLayer(cutLayer);
                }

                if (terrainChanged)
                    renderer.Synchronize(terrain);

                BeginDrawing();
                ClearBackground(new Color(0, 0, 0, 255));

                renderer.Draw(
                    terrain,
                    camera.Camera,
                    GetScreenWidth(),
                    GetScreenHeight());

                DrawHud(terrain, renderer, cutawayMode, focusName);
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

    private static void FocusLowerTunnel(
        OrbitCameraController camera,
        TerrainRenderSnapshot terrain)
    {
        camera.FocusOn(
            new System.Numerics.Vector3(
                terrain.WorldWidth * 0.5f + 58f,
                22f,
                terrain.WorldDepth * 0.5f - 9f),
            42f,
            pitchRadians: 0.82f);
    }

    private static void FocusUpperPassage(
        OrbitCameraController camera,
        TerrainRenderSnapshot terrain)
    {
        camera.FocusOn(
            new System.Numerics.Vector3(
                terrain.WorldWidth * 0.5f + 58f,
                35f,
                terrain.WorldDepth * 0.5f - 9f),
            34f,
            pitchRadians: 0.82f);
    }

    private static void DrawHud(
        TerrainRenderSnapshot terrain,
        RaylibTerrainRenderer renderer,
        bool cutawayMode,
        string focusName)
    {
        DrawRectangle(12, 12, 640, 244, new Color(0, 0, 0, 235));

        DrawText(
            cutawayMode
                ? $"RIMCLONE / {focusName} / CUT @ {terrain.CutawayHeightMeters:0.0} M"
                : "RIMCLONE / TOP-DOWN TERRAIN",
            24,
            22,
            18,
            Color.RayWhite);
        DrawText(
            $"FPS {GetFPS()}   FRAME {GetFrameTime() * 1000f:0.00} ms",
            24,
            50,
            17,
            Color.RayWhite);

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
            "F1 lower tunnel | F2 upper passage | C surface | T top-down view",
            24,
            146,
            14,
            Color.RayWhite);

        DrawText(
            "LOWER: X 1134-1286 | Z 1127-1159 | HEIGHT 14-20 m",
            24,
            170,
            14,
            Color.LightGray);

        DrawText(
            "UPPER: X 1200-1220 | Z 1127-1159 | HEIGHT 26-34 m",
            24,
            192,
            14,
            Color.LightGray);

        DrawText(
            "WASD move | RMB drag tilt/orbit | MMB pan | Wheel zoom | Q/E height | PgUp/PgDn 1m | Shift+Wheel",
            24,
            218,
            14,
            Color.RayWhite);
    }
}
