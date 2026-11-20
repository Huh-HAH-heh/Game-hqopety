using System.Numerics;
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

        RaylibViewerSettings settings = RaylibViewerSettings.Load();
        TerrainRenderSnapshot terrain =
            TerrainRenderSnapshot.Create(worldMap, settings.TerrainMode);
        TerrainRenderSnapshot? coarseVoxelTerrain =
            settings.TerrainMode == TerrainRenderMode.Voxel
                ? TerrainRenderSnapshot.CreateVoxelLod(worldMap, cellSizeInMeters: 4)
                : null;

        ConfigFlags windowFlags = ConfigFlags.ResizableWindow;
        if (settings.VSync)
            windowFlags |= ConfigFlags.VSyncHint;
        if (settings.Msaa4x)
            windowFlags |= ConfigFlags.Msaa4xHint;

        SetConfigFlags(windowFlags);
        InitWindow(
            settings.WindowWidth,
            settings.WindowHeight,
            "RimClone | Raylib Terrain Viewer");

        // Monitor queries require an initialized window/graphics context.
        int monitor = Math.Max(0, GetCurrentMonitor());
        int monitorWidth = Math.Max(960, GetMonitorWidth(monitor));
        int monitorHeight = Math.Max(540, GetMonitorHeight(monitor));

        if (settings.Fullscreen)
        {
            settings.WindowWidth = monitorWidth;
            settings.WindowHeight = monitorHeight;
            SetWindowSize(monitorWidth, monitorHeight);
            ToggleFullscreen();
        }
        else
        {
            settings.WindowWidth = Math.Clamp(
                settings.WindowWidth, 960, Math.Max(960, monitorWidth - 32));
            settings.WindowHeight = Math.Clamp(
                settings.WindowHeight, 540, Math.Max(540, monitorHeight - 64));
            SetWindowSize(settings.WindowWidth, settings.WindowHeight);
            SetWindowMinSize(
                Math.Min(960, settings.WindowWidth),
                Math.Min(540, settings.WindowHeight));
        }

        settings.Save();

        SetTargetFPS(settings.TargetFps);
        SetExitKey(KeyboardKey.Null);

        OrbitCameraController camera = new(terrain);
        camera.ApplySettings(settings);
        RaylibPauseMenu menu = new(settings);
        RaylibLayerSlicePanel layerPanel = new();
        RaylibTerrainRenderer? renderer = null;
        bool cutawayMode = settings.CutawayEnabled;
        float maximumCutHeight = Math.Max(
            0f,
            terrain.VerticalWorldExtentMeters -
            WorldMap.LayerHeightUnits * 0.1f);
        settings.CutawayHeightMeters = Math.Clamp(
            settings.CutawayHeightMeters,
            0f,
            maximumCutHeight);
        bool cutHeightKeyWasHeld = false;
        string focusName = cutawayMode ? "LOWER TUNNEL" : "SURFACE";

        if (cutawayMode)
            FocusLowerTunnel(camera, terrain);

        try
        {
            renderer = new RaylibTerrainRenderer(
                terrain,
                settings.Palette,
                settings.TerrainMode,
                coarseVoxelTerrain);
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

                if (menu.IsOpen)
                {
                    layerPanel.CancelInteraction();
                    if (layerPanel.ConsumeSettingsCommit())
                        menu.SaveSettings();
                }
                else
                {
                    layerPanel.Update(menu.Settings, maximumCutHeight);
                    if (menu.Settings.CutawayEnabled != cutawayMode)
                    {
                        cutawayMode = menu.Settings.CutawayEnabled;
                        if (cutawayMode)
                        {
                            focusName = "HORIZONTAL SECTION";
                        }
                        else
                        {
                            focusName = "SURFACE";
                            terrain.DisableCutaway();
                            coarseVoxelTerrain?.DisableCutaway();
                            renderer?.Synchronize(terrain, coarseVoxelTerrain);
                        }
                    }

                    if (layerPanel.ConsumeSettingsCommit())
                        menu.SaveSettings();
                }

                bool pointerOverLayerPanel =
                    layerPanel.IsPointerOverPanel(GetMousePosition());
                float mouseWheel = menu.IsOpen || pointerOverLayerPanel
                    ? 0f
                    : GetMouseWheelMove();

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
                            coarseVoxelTerrain?.DisableCutaway();
                        renderer.Synchronize(terrain, coarseVoxelTerrain);
                        camera.ResetTopDownView();
                        break;

                    case RaylibMenuAction.FocusLowerTunnel:
                        cutawayMode = true;
                        menu.CutawayEnabled = true;
                        menu.Settings.CutawayHeightMeters = 17f;
                        menu.SaveSettings();
                        focusName = "LOWER TUNNEL";
                        FocusLowerTunnel(camera, terrain);
                        break;

                    case RaylibMenuAction.FocusUpperPassage:
                        cutawayMode = true;
                        menu.CutawayEnabled = true;
                        menu.Settings.CutawayHeightMeters = 28f;
                        menu.SaveSettings();
                        focusName = "UPPER PASSAGE";
                        FocusUpperPassage(camera, terrain);
                        break;

                    case RaylibMenuAction.RebuildTerrainMode:
                        renderer.Dispose();
                        terrain = TerrainRenderSnapshot.Create(
                            worldMap,
                            menu.Settings.TerrainMode);
                        coarseVoxelTerrain = menu.Settings.TerrainMode == TerrainRenderMode.Voxel
                            ? TerrainRenderSnapshot.CreateVoxelLod(worldMap, cellSizeInMeters: 4)
                            : null;
                        if (cutawayMode)
                        {
                            int currentCutLayer = Math.Clamp(
                                (int)MathF.Round(
                                    menu.Settings.CutawayHeightMeters /
                                    (WorldMap.LayerHeightUnits * 0.1f)),
                                0,
                                worldMap.LayerCount - 1);
                            terrain.SetCutawayLayer(currentCutLayer);
                            coarseVoxelTerrain?.SetCutawayLayer(currentCutLayer);
                        }

                        renderer = new RaylibTerrainRenderer(
                            terrain,
                            menu.Settings.Palette,
                            menu.Settings.TerrainMode,
                            coarseVoxelTerrain);
                        appliedPaletteRevision = menu.PaletteRevision;
                        break;

                    case RaylibMenuAction.ToggleCutaway:
                        cutawayMode = menu.CutawayEnabled;
                        menu.SaveSettings();

                        if (cutawayMode)
                        {
                            menu.Settings.CutawayHeightMeters = 17f;
                            menu.SaveSettings();
                            focusName = "LOWER TUNNEL";
                            FocusLowerTunnel(camera, terrain);
                        }
                        else
                        {
                            focusName = "SURFACE";
                            terrain.DisableCutaway();
                            coarseVoxelTerrain?.DisableCutaway();
                            renderer.Synchronize(terrain, coarseVoxelTerrain);
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
                    if (cutHeightKeyWasHeld)
                    {
                        menu.SaveSettings();
                        cutHeightKeyWasHeld = false;
                    }
                }
                else
                {

                    if (IsKeyPressed(KeyboardKey.F1))
                    {
                        cutawayMode = true;
                        menu.CutawayEnabled = true;
                        menu.Settings.CutawayHeightMeters = 17f;
                        menu.SaveSettings();
                        focusName = "LOWER TUNNEL";
                        FocusLowerTunnel(camera, terrain);
                    }

                    if (IsKeyPressed(KeyboardKey.F2))
                    {
                        cutawayMode = true;
                        menu.CutawayEnabled = true;
                        menu.Settings.CutawayHeightMeters = 28f;
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
                            menu.Settings.CutawayHeightMeters = 17f;
                            menu.SaveSettings();
                            focusName = "LOWER TUNNEL";
                            FocusLowerTunnel(camera, terrain);
                        }
                        else
                        {
                            focusName = "SURFACE";
                            terrain.DisableCutaway();
                            coarseVoxelTerrain?.DisableCutaway();
                            renderer.Synchronize(terrain, coarseVoxelTerrain);
                            camera.ResetTopDownView();
                        }
                    }

                    bool shiftDown =
                        IsKeyDown(KeyboardKey.LeftShift) ||
                        IsKeyDown(KeyboardKey.RightShift);
                    bool sliceKeysDown =
                        IsKeyDown(KeyboardKey.Q) ||
                        IsKeyDown(KeyboardKey.E);
                    bool sliceChanged = cutawayMode && UpdateCutawayInput(
                        menu.Settings,
                        camera.VerticalSpeedMultiplier,
                        deltaTime,
                        mouseWheel,
                        shiftDown,
                        maximumCutHeight);

                    if (sliceChanged && !sliceKeysDown)
                        menu.SaveSettings();

                    if (sliceKeysDown)
                    {
                        cutHeightKeyWasHeld = true;
                    }
                    else if (cutHeightKeyWasHeld)
                    {
                        menu.SaveSettings();
                        cutHeightKeyWasHeld = false;
                    }

                    float cameraWheel = cutawayMode && shiftDown
                        ? 0f
                        : mouseWheel;
                    camera.Update(
                        deltaTime,
                        cameraWheel,
                        mouseInputEnabled: !layerPanel.IsPointerOverPanel(GetMousePosition()));
                }

                bool terrainChanged = terrain.RefreshFromWorld(worldMap);
                bool coarseTerrainChanged =
                    coarseVoxelTerrain?.RefreshFromWorld(worldMap) ?? false;

                if (cutawayMode)
                {
                    const float layerHeightMeters =
                        WorldMap.LayerHeightUnits * 0.1f;
                    int cutLayer = Math.Clamp(
                        (int)MathF.Round(
                            menu.Settings.CutawayHeightMeters /
                            layerHeightMeters),
                        0,
                        worldMap.LayerCount - 1);

                    terrain.SetCutawayLayer(cutLayer);
                    coarseVoxelTerrain?.SetCutawayLayer(cutLayer);
                }
                else
                {
                    terrain.DisableCutaway();
                    coarseVoxelTerrain?.DisableCutaway();
                }

                if (terrainChanged || coarseTerrainChanged)
                    renderer.Synchronize(terrain, coarseVoxelTerrain);

                BeginDrawing();
                ClearBackground(menu.Settings.Palette.Background.ToRaylibColor());

                renderer.Draw(
                    terrain,
                    camera.Camera,
                    GetScreenWidth(),
                    GetScreenHeight(),
                    menu.Settings.RenderDistanceMeters);

                if (menu.ShowHud)
                    DrawHud(
                        terrain,
                        renderer,
                        cutawayMode,
                        focusName,
                        menu.Settings,
                        menu.HudFont,
                        camera);

                if (!menu.IsOpen)
                    layerPanel.Draw(menu.Settings, maximumCutHeight, menu.HudFont);

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
        RaylibViewerSettings settings,
        Font font,
        OrbitCameraController camera)
    {
        int screenWidth = Math.Max(1, GetScreenWidth());
        int screenHeight = Math.Max(1, GetScreenHeight());

        // Scale the HUD from the real client viewport, not from a fixed
        // 1600x900 layout. Text is also fitted to the panel's actual width.
        float scale = Math.Clamp(
            MathF.Min(screenWidth / 1600f, screenHeight / 900f),
            0.72f,
            1.35f);
        float margin = MathF.Round(12f * scale);
        float padding = MathF.Round(12f * scale);
        float lineGap = MathF.Max(15f, 22f * scale);
        float panelWidth = Math.Min(
            720f * scale,
            Math.Max(240f, screenWidth - margin * 2f));

        List<(string Text, float Size, Color Color)> lines = new();
        lines.Add((
            cutawayMode
                ? $"RIMCLONE / {settings.TerrainMode} / {focusName} / CUT @ {terrain.CutawayHeightMeters:0.0} M"
                : settings.TerrainMode == TerrainRenderMode.Voxel
                    ? "RIMCLONE / VOXEL TERRAIN"
                    : "RIMCLONE / HEIGHTMAP FALLBACK",
            18f,
            Color.RayWhite));

        if (settings.ShowPerformanceStats)
        {
            lines.Add((
                $"FPS {GetFPS()}   FRAME {GetFrameTime() * 1000f:0.00} ms",
                16f,
                Color.RayWhite));
            lines.Add((
                settings.TerrainMode == TerrainRenderMode.Voxel
                    ? $"VOXEL LOD {renderer.ActiveRenderCellSize}x{renderer.ActiveRenderCellSize} m | Z 0.1 m"
                    : $"HEIGHTMAP CELL {renderer.ActiveRenderCellSize}x{renderer.ActiveRenderCellSize} m",
                13f,
                Color.LightGray));
            lines.Add((
                $"RENDER REGIONS {terrain.RegionsX}x{terrain.RegionsY} @ {terrain.RegionSize}x{terrain.RegionSize}",
                13f,
                Color.LightGray));
            lines.Add((
                $"VISIBLE GPU BATCHES {renderer.VisibleRegionsLastFrame}/{terrain.StorageRegionCount}   DRAWS {renderer.DrawCallsLastFrame}",
                13f,
                Color.LightGray));
            lines.Add((
                $"BASE MESH BUILDS {renderer.RebuiltRegions}   CUT CAP UPDATES {renderer.CutSurfaceRebuilds}",
                13f,
                Color.LightGray));
        }

        if (settings.ShowControls)
        {
            lines.Add((
                "F1 lower tunnel | F2 upper passage | C surface | Tab layers | T top-down view",
                13f,
                Color.RayWhite));
        }

        if (settings.ShowCaveCoordinates)
        {
            lines.Add((
                "LOWER: X 1076-1228 | Z ~1140-1164 | HEIGHT 13-21 m",
                13f,
                Color.LightGray));
            lines.Add((
                "UPPER: near X 1152 | Z 1136-1168 | HEIGHT 25-33 m",
                13f,
                Color.LightGray));
        }

        if (settings.ShowControls)
        {
            lines.Add((
                $"WASD {camera.CurrentMovementSpeedMetersPerSecond:0} m/s | RMB orbit | MMB pan | Shift+MMB speed",
                13f,
                Color.RayWhite));
            lines.Add((
                "Wheel zoom | Q/E faster slice | PgUp/PgDn 0.1m | Shift+Wheel 0.1m",
                13f,
                Color.RayWhite));
        }

        float panelHeight = padding * 2f + lines.Count * lineGap;
        panelHeight = Math.Min(panelHeight, Math.Max(80f, screenHeight - margin * 2f));

        DrawRectangle(
            (int)margin,
            (int)margin,
            (int)panelWidth,
            (int)panelHeight,
            new Color(8, 12, 18, 224));

        DrawRectangle(
            (int)margin,
            (int)margin,
            3,
            (int)panelHeight,
            new Color(75, 92, 112, 235));

        float textX = margin + padding;
        float textY = margin + padding * 0.65f;
        float maxTextWidth = panelWidth - padding * 2f - 4f;

        foreach ((string text, float baseSize, Color color) in lines)
        {
            float fontSize = MathF.Max(9f, baseSize * scale);
            DrawHudText(
                font,
                text,
                textX,
                textY,
                fontSize,
                maxTextWidth,
                color);
            textY += lineGap;
        }
    }

    private static void DrawHudText(
        Font font,
        string text,
        float x,
        float y,
        float fontSize,
        float maximumWidth,
        Color color)
    {
        string fittedText = text;
        float minimumSize = 9f;

        while (fontSize > minimumSize &&
            MeasureTextEx(font, fittedText, fontSize, 0.4f).X > maximumWidth)
        {
            fontSize = Math.Max(minimumSize, fontSize - 0.5f);
        }

        // A narrow viewport should never allow text to bleed outside the HUD.
        while (fittedText.Length > 4 &&
            MeasureTextEx(font, fittedText, fontSize, 0.4f).X > maximumWidth)
        {
            fittedText = fittedText[..^4] + "...";
        }

        DrawTextEx(
            font,
            fittedText,
            new Vector2(MathF.Round(x), MathF.Round(y)),
            fontSize,
            0.4f,
            color);
    }

    private static bool UpdateCutawayInput(
        RaylibViewerSettings settings,
        float speedMultiplier,
        float deltaTime,
        float mouseWheel,
        bool shiftDown,
        float maximumCutHeight)
    {
        const float layerStepMeters = WorldMap.LayerHeightUnits * 0.1f;
        float change = 0f;

        if (IsKeyPressed(KeyboardKey.PageUp))
            change += layerStepMeters;
        if (IsKeyPressed(KeyboardKey.PageDown))
            change -= layerStepMeters;

        if (shiftDown && mouseWheel != 0f)
            change += mouseWheel * layerStepMeters;

        float verticalInput = 0f;
        if (IsKeyDown(KeyboardKey.E))
            verticalInput += 1f;
        if (IsKeyDown(KeyboardKey.Q))
            verticalInput -= 1f;

        if (verticalInput != 0f)
            change += verticalInput * 1.8f * speedMultiplier * deltaTime;

        if (change == 0f)
            return false;

        float nextHeight = Math.Clamp(
            settings.CutawayHeightMeters + change,
            0f,
            maximumCutHeight);
        if (MathF.Abs(nextHeight - settings.CutawayHeightMeters) < 0.0001f)
            return false;

        settings.CutawayHeightMeters = nextHeight;
        return true;
    }
}
