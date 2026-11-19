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
                "[Raylib] Static cave demo: 80m winding through-tunnel with two portals, " +
                "sealed upper passage, chambers and rising connector. Press C for cutaway.");

            while (!WindowShouldClose())
            {
                float deltaTime = Math.Clamp(GetFrameTime(), 0f, 0.05f);

                if (IsKeyPressed(KeyboardKey.C))
                {
                    cutawayMode = !cutawayMode;

                    if (cutawayMode)
                    {
                        camera.FocusOn(
                            new System.Numerics.Vector3(
                                terrain.WorldWidth * 0.5f + 58f,
                                16f,
                                terrain.WorldDepth * 0.5f - 9f),
                            38f,
                            pitchRadians: 0.68f);
                    }
                    else
                    {
                        terrain.DisableCutaway();
                        renderer.Synchronize(terrain, forceRebuild: true);
                        camera.ResetTopDownView();
                    }
                }

                camera.Update(deltaTime);

                bool terrainChanged = terrain.RefreshFromWorld(worldMap);

                if (cutawayMode)
                {
                    // The horizontal section is anchored to the camera's
                    // look-at height, quantized to one-metre steps. Q/E move
                    // continuously; PageUp/PageDown step by one metre.
                    const float cutStepMeters = 0.5f;
                    int layersPerCutStep = (int)MathF.Round(
                        cutStepMeters * WorldMap.HeightUnitsPerMeter);
                    int cutLayer = Math.Clamp(
                        (int)MathF.Round(
                            camera.Camera.Target.Y / cutStepMeters) *
                            layersPerCutStep,
                        0,
                        worldMap.LayerCount - 1);

                    bool cutChanged = terrain.SetCutawayLayer(cutLayer);

                    if (terrainChanged || cutChanged)
                        renderer.SynchronizeCutaway(terrain);
                }
                else if (terrainChanged)
                {
                    renderer.Synchronize(terrain);
                }

                BeginDrawing();
                ClearBackground(new Color(0, 0, 0, 255));

                renderer.Draw(
                    terrain,
                    camera.Camera,
                    GetScreenWidth(),
                    GetScreenHeight());

                DrawHud(terrain, renderer, cutawayMode);
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
        RaylibTerrainRenderer renderer,
        bool cutawayMode)
    {
        DrawRectangle(12, 12, 448, 178, new Color(0, 0, 0, 235));

        DrawText(
            cutawayMode
                ? $"RIMCLONE / CUTAWAY @ {terrain.CutawayHeightMeters:0.0} M"
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
            cutawayMode
                ? "C surface view | Q/E cut | PgUp/PgDn +/-1m | Shift+Wheel +/-1m | RMB orbit"
                : "C visual cut at focus height | Q/E or PgUp/PgDn adjust | Shift+Wheel cut | T top view",
            24,
            146,
            14,
            Color.RayWhite);

        DrawText(
            cutawayMode
                ? "WASD move | RMB orbit | MMB pan | Wheel zoom | Shift+Wheel slice | Q/E +/-height | PgUp/PgDn +/-1m | C surface | Esc exit"
                : "WASD move | RMB orbit | MMB pan | Wheel zoom | Shift+Wheel slice | Q/E height | PgUp/PgDn +/-1m | C cut | T fit | Esc exit",
            24,
            196,
            14,
            Color.RayWhite);
    }
}
