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

        RaylibViewerSettings settings = RaylibViewerSettings.Load();

        ConfigFlags windowFlags = ConfigFlags.ResizableWindow;
        if (settings.VSync)
            windowFlags |= ConfigFlags.VSyncHint;
        if (settings.Msaa4x)
            windowFlags |= ConfigFlags.Msaa4xHint;

        SetConfigFlags(windowFlags);
        InitWindow(settings.WindowWidth, settings.WindowHeight, "RimClone | Raylib Terrain Viewer");
        if (settings.Fullscreen)
            ToggleFullscreen();

        SetTargetFPS(settings.TargetFps);
        SetExitKey(KeyboardKey.Null);

        OrbitCameraController camera = new(terrain);
        camera.ApplySettings(settings);
        RaylibPauseMenu menu = new(settings);
        RaylibTerrainRenderer? renderer = null;
        bool cutawayMode = settings.CutawayEnabled;
        string focusName = cutawayMode ? "LOWER TUNNEL" : "SURFACE";

        if (cutawayMode)
            FocusLowerTunnel(camera, terrain);

        try
        {
            renderer = new RaylibTerrainRenderer(terrain, settings.Palette);
            int appliedPaletteRevision = menu.PaletteRevision;

            Console.WriteLine(
                "[Raylib] Terrain viewer started. " +
                $"render regions: {terrain.RegionsX}x{terrain.RegionsY} " +
                $"({terrain.RegionSize}x{terrain.RegionSize} tiles), " +
                $"GPU batches: {terrain.StorageRegionsX}x{terrain.StorageRegionsY}, " +
                $"world: {terrain.WorldWidth:0}x{terrain.WorldDepth:0} m, " +
                $"maximum height: {terrain.MaximumHeightMeters:0.0} m.");

            Console.WriteLine(
                $"[Settings] Saved viewer profile: {RaylibViewerSettings.GetSettingsPath()}");

            Console.WriteLine(
                "[Raylib] Cave at map centre (1152,1152): lower tunnel X 1076-1228, ",
                "Z about 1140-1164, H 13-21m; upper passage near X 1152, Z 1136-1168, " +
                "H 25-33m. F1/F2 focus, C surface.");

            bool exitRequested = false;

            while (!WindowShouldClose() && !exitRequested)
            {
                float deltaTime = Math.Clamp(GetFrameTime(), 0f, 0.05f);
                RaylibMenuAction menuAction = menu.Update();

                switch (menuAction)
                {
                    case RaylibMenuAction.Exit:
                        exitRequested = true;
                        break;

                    case RaylibMenuAction.ResetCamera:
                        cutawayMode = false;
                        menu.CutawayEnabled = false;
                        menu.SaveSettings();
                        focusName = "SURFACE";
                        terrain.DisableCutaway();
                        renderer.Synchronize(terrain);
                        camera.ResetTopDownView();
                        break;

                    case RaylibMenuAction.FocusLowerTunnel:
                        cutawayMode = true;
                        menu.CutawayEnabled = true;
                        menu.SaveSettings();
                        focusName = "LOWER TUNNEL";
                        FocusLowerTunnel(camera, terrain);
                        break;

                    case RaylibMenuAction.FocusUpperPassage:
                        cutawayMode = true;
                        menu.CutawayEnabled = true;
                        menu.SaveSettings();
                        focusName = "UPPER PASSAGE";
                        FocusUpperPassage(camera, terrain);
                        break;

                    case RaylibMenuAction.ToggleCutaway:
                        cutawayMode = menu.CutawayEnabled;
                        menu.SaveSettings();

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
                        break;
                }

                camera.ApplySettings(menu.Settings);

                if (menu.PaletteRevision != appliedPaletteRevision)
                {
                    renderer.SetPalette(menu.Settings.Palette);
                    appliedPaletteRevision = menu.PaletteRevision;
                }

                if (menu.IsOpen)
                {
                    camera.ReleaseMouse();
                }
                else
                {

                    if (IsKeyPressed(KeyboardKey.F1))
                    {
                        cutawayMode = true;
                        menu.CutawayEnabled = true;
                        menu.SaveSettings();
                        focusName = "LOWER TUNNEL";
                        FocusLowerTunnel(camera, terrain);
                    }

                    if (IsKeyPressed(KeyboardKey.F2))
                    {
                        cutawayMode = true;
                        menu.CutawayEnabled = true;
                        menu.SaveSettings();
                        focusName = "UPPER PASSAGE";
                        FocusUpperPassage(camera, terrain);
                    }

                    if (IsKeyPressed(KeyboardKey.C))
                    {
                        cutawayMode = !cutawayMode;
                        menu.CutawayEnabled = cutawayMode;
                        menu.SaveSettings();

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
                }

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
                ClearBackground(menu.Settings.Palette.Background.ToRaylibColor());

                renderer.Draw(
                    terrain,
                    camera.Camera,
                    GetScreenWidth(),
                    GetScreenHeight(),
                    menu.Settings.RenderDistanceMeters);

                if (menu.ShowHud)
                    DrawHud(terrain, renderer, cutawayMode, focusName, menu.Settings);

                menu.Draw();
                EndDrawing();
            }
        }
        finally
        {
            camera.ReleaseMouse();
            menu.Dispose();
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
                terrain.WorldWidth * 0.5f,
                17f,
                terrain.WorldDepth * 0.5f),
            108f,
            pitchRadians: 0.82f);
    }

    private static void FocusUpperPassage(
        OrbitCameraController camera,
        TerrainRenderSnapshot terrain)
    {
        camera.FocusOn(
            new System.Numerics.Vector3(
                terrain.WorldWidth * 0.5f,
                28f,
                terrain.WorldDepth * 0.5f),
            34f,
            pitchRadians: 0.82f);
    }

    private static void DrawHud(
        TerrainRenderSnapshot terrain,
        RaylibTerrainRenderer renderer,
        bool cutawayMode,
        string focusName,
        RaylibViewerSettings settings)
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
        if (settings.ShowPerformanceStats)
        {
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
        }

        if (settings.ShowControls)
        {
            DrawText(
                "F1 lower tunnel | F2 upper passage | C surface | T top-down view",
                24,
                146,
                14,
                Color.RayWhite);
        }

        if (settings.ShowCaveCoordinates)
        {
            DrawText(
                "LOWER: X 1076-1228 | Z ~1140-1164 | HEIGHT 13-21 m",
                24,
                170,
                14,
                Color.LightGray);

            DrawText(
                "UPPER: near X 1152 | Z 1136-1168 | HEIGHT 25-33 m",
                24,
                192,
                14,
                Color.LightGray);
        }

        if (settings.ShowControls)
        {
            DrawText(
                "WASD move | RMB drag tilt/orbit | MMB pan | Wheel zoom | Q/E height | PgUp/PgDn 1m | Shift+Wheel",
                24,
                218,
                14,
                Color.RayWhite);
        }
    }
}
