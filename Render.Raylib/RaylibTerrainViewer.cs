using System;
using System.Diagnostics;
using System.IO;
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
    private enum VoxelEditMode : byte
    {
        Off,
        CarveSphere,
        AddSphere,
        PaintSphere,
        SmoothSphere,
        CarveCapsule,
        AddCapsule,
        CarveBox,
        AddBox,
        CarveRamp,
        AddRamp,
        FlattenPlane
    }

    public static void Run(WorldMap worldMap)
    {
        ArgumentNullException.ThrowIfNull(worldMap);

        RaylibViewerSettings settings = RaylibViewerSettings.Load();
        using TerrainPipelineLogger renderLogger = new(
            settings.RenderLoggingEnabled,
            settings.LogMode);
        renderLogger.Write(RenderLogMode.ErrorsOnly, "VIEWER_START",
            $"terrainMode={settings.TerrainMode} enabled={settings.RenderLoggingEnabled} mode={settings.LogMode} path=\"{renderLogger.LogPath}\"");

        string voxelSavePath = GetVoxelSavePath();
        try
        {
            if (worldMap.LoadVoxelEdits(voxelSavePath))
                Console.WriteLine($"[Voxel] Restored edited chunks: {voxelSavePath}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[Voxel] Could not load terrain edits from '{voxelSavePath}': {exception.Message}");
            renderLogger.Error("VOXEL_SAVE_LOAD_FAILED", exception.ToString());
        }

        long savedVoxelVersion = worldMap.Voxels.Version;
        double lastVoxelSaveTime = GetTime();
        long snapshotStarted = Stopwatch.GetTimestamp();
        TerrainRenderSnapshot terrain =
            TerrainRenderSnapshot.Create(worldMap, settings.TerrainMode);
        renderLogger.Write(RenderLogMode.Detailed, "WORLD_SNAPSHOT_READY",
            $"mode={settings.TerrainMode} regions={terrain.RegionsX}x{terrain.RegionsY} storage={terrain.StorageRegionsX}x{terrain.StorageRegionsY} world={terrain.WorldWidth:0.0}x{terrain.WorldDepth:0.0}m height={terrain.VerticalWorldExtentMeters:0.0}m elapsedMs={Stopwatch.GetElapsedTime(snapshotStarted).TotalMilliseconds:0.000}");
        TerrainRenderSnapshot?         ConfigFlags windowFlags = ConfigFlags.ResizableWindow;
        if (settings.VSync)
            windowFlags |= ConfigFlags.VSyncHint;
        if (settings.Msaa4x)
            windowFlags |= ConfigFlags.Msaa4xHint;

        renderLogger.Write(RenderLogMode.Detailed, "WINDOW_CONFIG", $"flags={windowFlags} width={settings.WindowWidth} height={settings.WindowHeight} vsync={settings.VSync} msaa={settings.Msaa4x} fullscreen={settings.Fullscreen}");
        SetConfigFlags(windowFlags);
        InitWindow(
            settings.WindowWidth,
            settings.WindowHeight,
            "RimClone | Raylib Terrain Viewer");
        renderLogger.Write(RenderLogMode.ErrorsOnly, "WINDOW_READY", $"width={GetScreenWidth()} height={GetScreenHeight()} monitor={GetCurrentMonitor()}");

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
        VoxelEditMode voxelEditMode = VoxelEditMode.Off;
        float voxelBrushRadiusMeters = 1.5f;
        ushort voxelBrushMaterial = 1;
        bool voxelStrokeActive = false;
        Vector3 voxelStrokeStart = default;

        if (cutawayMode)
            FocusLowerTunnel(camera, terrain);

        try
        {
            renderer = new RaylibTerrainRenderer(
                terrain,
                settings.Palette,
                settings.TerrainMode,
                renderLogger);
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
            long frameNumber = 0;

            while (!WindowShouldClose() && !exitRequested)
            {
                float deltaTime = Math.Clamp(GetFrameTime(), 0f, 0.05f);
                long frameStarted = Stopwatch.GetTimestamp();
                renderLogger.SetFrame(++frameNumber);

                bool controlDown =
                    IsKeyDown(KeyboardKey.LeftControl) ||
                    IsKeyDown(KeyboardKey.RightControl);
                if (controlDown && IsKeyPressed(KeyboardKey.F8))
                {
                    menu.Settings.RenderLoggingEnabled = !menu.Settings.RenderLoggingEnabled;
                    renderLogger.SetEnabled(menu.Settings.RenderLoggingEnabled);
                    menu.SaveSettings();
                }

                if (controlDown && IsKeyPressed(KeyboardKey.F9))
                {
                    menu.Settings.LogMode = NextRenderLogMode(menu.Settings.LogMode);
                    renderLogger.SetMode(menu.Settings.LogMode);
                    menu.SaveSettings();
                }


                RaylibMenuAction menuAction = menu.Update();
                if (menuAction != RaylibMenuAction.None)
                    renderLogger.Write(RenderLogMode.Detailed, "MENU_ACTION", $"action={menuAction}");

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
                            renderer?.Synchronize(terrain);
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
                        renderer.Synchronize(terrain);
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
                        if (cutawayMode)
                        {
                            int currentCutLayer = Math.Clamp(
                                (int)MathF.Round(
                                    menu.Settings.CutawayHeightMeters /
                                    (WorldMap.LayerHeightUnits * 0.1f)),
                                0,
                                worldMap.LayerCount - 1);
                            terrain.SetCutawayLayer(currentCutLayer);
                        }

                        renderer = new RaylibTerrainRenderer(
                            terrain,
                            menu.Settings.Palette,
                            menu.Settings.TerrainMode,
                            renderLogger);
                        appliedPaletteRevision = menu.PaletteRevision;
                        break;

                    case RaylibMenuAction.ToggleRenderLogging:
                        menu.Settings.RenderLoggingEnabled = !menu.Settings.RenderLoggingEnabled;
                        renderLogger.SetEnabled(menu.Settings.RenderLoggingEnabled);
                        menu.SaveSettings();
                        break;

                    case RaylibMenuAction.CycleRenderLogMode:
                        menu.Settings.LogMode = NextRenderLogMode(menu.Settings.LogMode);
                        renderLogger.SetMode(menu.Settings.LogMode);
                        menu.SaveSettings();
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
                            renderer.Synchronize(terrain);
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

                    if (menu.Settings.TerrainMode == TerrainRenderMode.Voxel)
                    {
                        if (IsKeyPressed(KeyboardKey.F3))
                        {
                            voxelEditMode = (VoxelEditMode)(
                                ((int)voxelEditMode + 1) % 12);
                        }

                        if (IsKeyPressed(KeyboardKey.F4))
                            voxelBrushMaterial = (ushort)(voxelBrushMaterial % 3 + 1);

                        if (IsKeyPressed(KeyboardKey.F5))
                            voxelBrushRadiusMeters = Math.Max(
                                0.1f, voxelBrushRadiusMeters - 0.25f);

                        if (IsKeyPressed(KeyboardKey.F6))
                            voxelBrushRadiusMeters = Math.Min(
                                4f, voxelBrushRadiusMeters + 0.25f);

                        if (IsKeyPressed(KeyboardKey.F7) && !renderer.IsMergingFragments)
                        {
                            if (worldMap.Voxels.Undo())
                                renderer.UndoVoxelEdit();
                            else
                                Console.WriteLine("[Voxel] Nothing to undo.");
                        }

                        if (IsKeyPressed(KeyboardKey.F8) && !controlDown && !renderer.IsMergingFragments)
                        {
                            if (worldMap.Voxels.Redo())
                                renderer.RedoVoxelEdit();
                            else
                                Console.WriteLine("[Voxel] Nothing to redo.");
                        }

                        if (voxelEditMode != VoxelEditMode.Off &&
                            !renderer.IsMergingFragments &&
                            IsMouseButtonPressed(MouseButton.Left) &&
                            !layerPanel.IsPointerOverPanel(GetMousePosition()))
                        {
                            Ray editRay = GetMouseRay(GetMousePosition(), camera.Camera);
                            if (worldMap.Voxels.TryRaycast(
                                editRay.Position,
                                editRay.Direction,
                                menu.Settings.RenderDistanceMeters,
                                out Vector3 hitPosition,
                                out Vector3 hitNormal))
                            {
                                if (voxelEditMode is VoxelEditMode.CarveCapsule or VoxelEditMode.AddCapsule)
                                {
                                    voxelStrokeStart = hitPosition -
                                        hitNormal * voxelBrushRadiusMeters * 0.2f;
                                    voxelStrokeActive = true;
                                }
                                else if (voxelEditMode is VoxelEditMode.CarveBox or VoxelEditMode.AddBox or
                                         VoxelEditMode.CarveRamp or VoxelEditMode.AddRamp)
                                {
                                    voxelStrokeStart = hitPosition;
                                    voxelStrokeActive = true;
                                }
                                else
                                {
                                    ApplyVoxelBrush(
                                        worldMap,
                                        renderer,
                                        voxelEditMode,
                                        hitPosition,
                                        hitNormal,
                                        voxelBrushRadiusMeters,
                                        voxelBrushMaterial);
                                }
                            }
                        }

                        if (renderer.IsMergingFragments)
                            voxelStrokeActive = false;

                        if (voxelStrokeActive &&
                            !renderer.IsMergingFragments &&
                            IsMouseButtonReleased(MouseButton.Left))
                        {
                            Ray editRay = GetMouseRay(GetMousePosition(), camera.Camera);
                            if (worldMap.Voxels.TryRaycast(
                                editRay.Position,
                                editRay.Direction,
                                menu.Settings.RenderDistanceMeters,
                                out Vector3 hitPosition,
                                out Vector3 hitNormal))
                            {
                                Vector3 end = hitPosition -
                                    hitNormal * voxelBrushRadiusMeters * 0.2f;
                                if (voxelEditMode == VoxelEditMode.CarveCapsule)
                                {
                                    long editVersionBefore = worldMap.Voxels.Version;
                                    renderer.BeginVoxelEdit();
                                    using (worldMap.Voxels.BeginUndoableEdit())
                                    {
                                        worldMap.CarveVoxelCapsule(
                                            voxelStrokeStart,
                                            end,
                                            voxelBrushRadiusMeters);
                                        Vector3 fragmentCenter = (voxelStrokeStart + end) * 0.5f;
                                        Vector3 fragmentHalfExtents =
                                            Vector3.Abs(end - voxelStrokeStart) * 0.5f +
                                            new Vector3(voxelBrushRadiusMeters + 1f);
                                        LogDetachedFragments(renderer.DetachLooseFragments(
                                            worldMap.Voxels, fragmentCenter, fragmentHalfExtents));
                }
                                    renderer.EndVoxelEdit(worldMap.Voxels.Version != editVersionBefore);
                                }
                                else if (voxelEditMode == VoxelEditMode.AddCapsule)
                                {
                                    long editVersionBefore = worldMap.Voxels.Version;
                                    renderer.BeginVoxelEdit();
                                    using (worldMap.Voxels.BeginUndoableEdit())
                                    {
                                        worldMap.AddVoxelCapsule(
                                            voxelStrokeStart,
                                            end,
                                            voxelBrushRadiusMeters,
                                            voxelBrushMaterial);
                }
                                    renderer.EndVoxelEdit(worldMap.Voxels.Version != editVersionBefore);
                                }
                                else if (voxelEditMode is VoxelEditMode.CarveBox or VoxelEditMode.AddBox)
                                {
                                    Vector3 minimum = Vector3.Min(voxelStrokeStart, hitPosition);
                                    Vector3 maximum = Vector3.Max(voxelStrokeStart, hitPosition);
                                    minimum.Z -= voxelBrushRadiusMeters;
                                    maximum.Z += voxelBrushRadiusMeters;
                                    if (maximum.X - minimum.X < 0.2f)
                                    {
                                        minimum.X -= voxelBrushRadiusMeters;
                                        maximum.X += voxelBrushRadiusMeters;
                }
                                    if (maximum.Y - minimum.Y < 0.2f)
                                    {
                                        minimum.Y -= voxelBrushRadiusMeters;
                                        maximum.Y += voxelBrushRadiusMeters;
                }
                                    long editVersionBefore = worldMap.Voxels.Version;
                                    renderer.BeginVoxelEdit();
                                    using (worldMap.Voxels.BeginUndoableEdit())
                                    {
                                        if (voxelEditMode == VoxelEditMode.CarveBox)
                                        {
                                            worldMap.CarveVoxelBox(minimum, maximum);
                                            Vector3 fragmentCenter = (minimum + maximum) * 0.5f;
                                            Vector3 fragmentHalfExtents =
                                                (maximum - minimum) * 0.5f + Vector3.One;
                                            LogDetachedFragments(renderer.DetachLooseFragments(
                                                worldMap.Voxels, fragmentCenter, fragmentHalfExtents));
                                        }
                                        else
                                        {
                                            worldMap.AddVoxelBox(
                                                minimum, maximum, voxelBrushMaterial);
                                        }
                }
                                    renderer.EndVoxelEdit(worldMap.Voxels.Version != editVersionBefore);
                                }
                                else if (voxelEditMode is VoxelEditMode.CarveRamp or VoxelEditMode.AddRamp)
                                {
                                    long editVersionBefore = worldMap.Voxels.Version;
                                    renderer.BeginVoxelEdit();
                                    using (worldMap.Voxels.BeginUndoableEdit())
                                    {
                                        if (voxelEditMode == VoxelEditMode.CarveRamp)
                                        {
                                            worldMap.CarveVoxelRamp(
                                                voxelStrokeStart,
                                                hitPosition,
                                                voxelBrushRadiusMeters * 2f,
                                                voxelBrushRadiusMeters);
                                            Vector3 fragmentCenter = (voxelStrokeStart + hitPosition) * 0.5f;
                                            Vector3 fragmentHalfExtents =
                                                Vector3.Abs(hitPosition - voxelStrokeStart) * 0.5f +
                                                new Vector3(voxelBrushRadiusMeters + 1f);
                                            LogDetachedFragments(renderer.DetachLooseFragments(
                                                worldMap.Voxels, fragmentCenter, fragmentHalfExtents));
                                        }
                                        else
                                        {
                                            worldMap.AddVoxelRamp(
                                                voxelStrokeStart,
                                                hitPosition,
                                                voxelBrushRadiusMeters * 2f,
                                                voxelBrushRadiusMeters,
                                                voxelBrushMaterial);
                                        }
                }
                                    renderer.EndVoxelEdit(worldMap.Voxels.Version != editVersionBefore);
                                }
                            }
                            voxelStrokeActive = false;
                        }
                    }
                }

                long syncStarted = Stopwatch.GetTimestamp();
                bool terrainChanged = terrain.RefreshFromWorld(worldMap);
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
                }
                else
                {
                    terrain.DisableCutaway();
                }
                if (terrainChanged)
                    renderer.Synchronize(terrain);

                if (renderLogger.IsEnabled(RenderLogMode.Detailed))
                    renderLogger.Write(RenderLogMode.Detailed, "WORLD_SYNC",
                        $"terrainChanged={terrainChanged} voxelVersion={worldMap.Voxels.Version} elapsedMs={Stopwatch.GetElapsedTime(syncStarted).TotalMilliseconds:0.000}");

                long currentVoxelVersion = worldMap.Voxels.Version;
                if (currentVoxelVersion != savedVoxelVersion &&
                    GetTime() - lastVoxelSaveTime >= 1.25d)
                {
                    try
                    {
                        worldMap.SaveVoxelEdits(voxelSavePath);
                        savedVoxelVersion = currentVoxelVersion;
                        lastVoxelSaveTime = GetTime();
                        renderLogger.Write(RenderLogMode.Detailed, "VOXEL_AUTOSAVE",
                            $"version={currentVoxelVersion} path=\"{voxelSavePath}\"");
                    }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine(
                            $"[Voxel] Autosave failed: {exception.Message}");
                        renderLogger.Error("VOXEL_AUTOSAVE_FAILED", exception.ToString());
                        lastVoxelSaveTime = GetTime();
                    }
                }

                BeginDrawing();
                ClearBackground(menu.Settings.Palette.Background.ToRaylibColor());

                long terrainDrawStarted = Stopwatch.GetTimestamp();
                renderer.Draw(
                    terrain,
                    camera.Camera,
                    GetScreenWidth(),
                    GetScreenHeight(),
                    menu.Settings.RenderDistanceMeters);
                double terrainDrawMs = Stopwatch.GetElapsedTime(terrainDrawStarted).TotalMilliseconds;
                if (renderLogger.IsEnabled(RenderLogMode.Detailed))
                    renderLogger.Write(RenderLogMode.Detailed, "RENDER_PASS",
                        $"name=terrain-3d elapsedMs={terrainDrawMs:0.000} draws={renderer.DrawCallsLastFrame} visible={renderer.VisibleRegionsLastFrame} rebuilt={renderer.RebuiltRegions}");

                double hudMs = 0d;
                if (menu.ShowHud)
                {
                    long hudStarted = Stopwatch.GetTimestamp();
                    DrawHud(
                        terrain,
                        renderer,
                        cutawayMode,
                        focusName,
                        menu.Settings,
                        menu.HudFont,
                        camera,
                        GetVoxelToolStatus(
                            voxelEditMode,
                            voxelBrushRadiusMeters,
                            voxelBrushMaterial));
                    hudMs = Stopwatch.GetElapsedTime(hudStarted).TotalMilliseconds;
                    if (renderLogger.IsEnabled(RenderLogMode.Detailed))
                        renderLogger.Write(RenderLogMode.Detailed, "RENDER_PASS", $"name=hud elapsedMs={hudMs:0.000}");
                }

                double layerPanelMs = 0d;
                if (!menu.IsOpen)
                {
                    long layerStarted = Stopwatch.GetTimestamp();
                    layerPanel.Draw(menu.Settings, maximumCutHeight, menu.HudFont);
                    layerPanelMs = Stopwatch.GetElapsedTime(layerStarted).TotalMilliseconds;
                    if (renderLogger.IsEnabled(RenderLogMode.Detailed))
                        renderLogger.Write(RenderLogMode.Detailed, "RENDER_PASS", $"name=layer-panel elapsedMs={layerPanelMs:0.000}");
                }

                long menuStarted = Stopwatch.GetTimestamp();
                menu.Draw();
                double menuMs = Stopwatch.GetElapsedTime(menuStarted).TotalMilliseconds;
                if (menu.IsOpen && renderLogger.IsEnabled(RenderLogMode.Detailed))
                    renderLogger.Write(RenderLogMode.Detailed, "RENDER_PASS", $"name=pause-menu elapsedMs={menuMs:0.000}");

                EndDrawing();

                double frameMs = Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds;
                if (renderLogger.IsEnabled(RenderLogMode.Detailed))
                    renderLogger.Write(RenderLogMode.Detailed, "FRAME_PHASES",
                        $"terrain3dMs={terrainDrawMs:0.000} hudMs={hudMs:0.000} layerPanelMs={layerPanelMs:0.000} menuMs={menuMs:0.000} totalMs={frameMs:0.000}");
                if (renderLogger.IsEnabled(RenderLogMode.FrameSummary))
                    renderLogger.Write(RenderLogMode.FrameSummary, "FRAME",
                        $"fps={GetFPS()} dtMs={deltaTime * 1000f:0.000} ms={frameMs:0.000} viewport={GetScreenWidth()}x{GetScreenHeight()} mode={menu.Settings.TerrainMode} cameraMode={menu.Settings.CameraMode} camera=({camera.Camera.Position.X:0.0},{camera.Camera.Position.Y:0.0},{camera.Camera.Position.Z:0.0}) target=({camera.Camera.Target.X:0.0},{camera.Camera.Target.Y:0.0},{camera.Camera.Target.Z:0.0}) cutaway={cutawayMode}:{menu.Settings.CutawayHeightMeters:0.0} visible={renderer.VisibleRegionsLastFrame} draws={renderer.DrawCallsLastFrame} rebuilt={renderer.RebuiltRegions} chunks={renderer.CachedVoxelChunks} workers={renderer.VoxelMeshWorkers} queue={renderer.VoxelPendingMeshJobs} results={renderer.VoxelCompletedMeshResultsPending} uploads={renderer.VoxelGpuUploadsLastFrame} gpuMs={renderer.VoxelGpuUploadMilliseconds:0.000}");
            }
        }
        catch (Exception exception)
        {
            renderLogger.Error("VIEWER_FATAL", exception.ToString());
            throw;
        }
        finally
        {
            renderLogger.Write(RenderLogMode.ErrorsOnly, "VIEWER_STOP", $"voxelVersion={worldMap.Voxels.Version}");
            if (worldMap.Voxels.Version != savedVoxelVersion)
            {
                try
                {
                    worldMap.SaveVoxelEdits(voxelSavePath);
                    Console.WriteLine($"[Voxel] Saved edited chunks: {voxelSavePath}");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"[Voxel] Final save failed: {exception.Message}");
                    renderLogger.Error("VOXEL_FINAL_SAVE_FAILED", exception.ToString());
                }
            }

            camera.ReleaseMouse();
            menu.Dispose();
            renderer?.Dispose();
            CloseWindow();
        }
    }

    private static RenderLogMode NextRenderLogMode(RenderLogMode mode) =>
        (RenderLogMode)(((int)mode + 1) % 4);

    private static string GetVoxelSavePath()
    {
        string? configuredPath =
            Environment.GetEnvironmentVariable("RIMCLONE_VOXEL_SAVE_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath);

        string localData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        string root = string.IsNullOrWhiteSpace(localData)
            ? AppContext.BaseDirectory
            : localData;
        return Path.Combine(root, "RimClone", "voxel-terrain.rvx");
    }

    private static void LogDetachedFragments(int detachedCount)
    {
        if (detachedCount > 0)
            Console.WriteLine($"[Voxel] Detached dynamic rock fragments: {detachedCount}");
    }

    private static string GetVoxelToolStatus(
        VoxelEditMode mode,
        float radiusMeters,
        ushort materialId)
    {
        string name = mode switch
        {
            VoxelEditMode.CarveSphere => "CARVE SPHERE",
            VoxelEditMode.AddSphere => "ADD SPHERE",
            VoxelEditMode.PaintSphere => "PAINT SPHERE",
            VoxelEditMode.SmoothSphere => "SMOOTH SPHERE",
            VoxelEditMode.CarveCapsule => "CARVE CAPSULE",
            VoxelEditMode.AddCapsule => "ADD CAPSULE",
            VoxelEditMode.CarveBox => "CARVE BOX",
            VoxelEditMode.AddBox => "ADD BOX",
            VoxelEditMode.CarveRamp => "CARVE RAMP",
            VoxelEditMode.AddRamp => "ADD RAMP",
            VoxelEditMode.FlattenPlane => "FLATTEN PLANE",
            _ => "EDIT OFF"
        };

        return $"VOXEL TOOL {name} | RADIUS {radiusMeters:0.00}m | MATERIAL {materialId} | F7 UNDO / F8 REDO";
    }

    private static void ApplyVoxelBrush(
        WorldMap worldMap,
        RaylibTerrainRenderer renderer,
        VoxelEditMode mode,
        Vector3 hitPosition,
        Vector3 hitNormal,
        float radiusMeters,
        ushort materialId)
    {
        long editVersionBefore = worldMap.Voxels.Version;
        renderer.BeginVoxelEdit();
        try
        {
            using IDisposable edit = worldMap.Voxels.BeginUndoableEdit();
            Vector3 carveCenter = hitPosition - hitNormal * radiusMeters * 0.2f;
            switch (mode)
        {
            case VoxelEditMode.CarveSphere:
                worldMap.CarveVoxelSphere(carveCenter, radiusMeters);
                LogDetachedFragments(renderer.DetachLooseFragments(
                    worldMap.Voxels,
                    hitPosition,
                    new Vector3(radiusMeters + 1f)));
                break;

            case VoxelEditMode.AddSphere:
                worldMap.AddVoxelSphere(
                    hitPosition + hitNormal * radiusMeters * 0.25f,
                    radiusMeters,
                    materialId);
                break;

            case VoxelEditMode.PaintSphere:
                worldMap.PaintVoxelSphere(carveCenter, radiusMeters, materialId);
                break;

            case VoxelEditMode.SmoothSphere:
                worldMap.SmoothVoxelSphere(carveCenter, radiusMeters, 0.55f);
                break;
            case VoxelEditMode.FlattenPlane:
                worldMap.FlattenVoxelPlane(
                    hitPosition,
                    hitNormal,
                    radiusMeters,
                    materialId,
                    strength: 0.9f);
                break;
            }
        }
        finally
        {
            renderer.EndVoxelEdit(worldMap.Voxels.Version != editVersionBefore);
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
        OrbitCameraController camera,
        string voxelToolStatus)
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
        float lineGap = MathF.Max(23f, 26f * scale);
        float panelWidth = Math.Min(
            980f,
            Math.Max(240f, screenWidth - margin * 2f));

        List<(string Text, float Size, Color Color)> lines = new();
        lines.Add((
            cutawayMode
                ? $"RIMCLONE / {settings.TerrainMode} / {focusName} / CUT @ {terrain.CutawayHeightMeters:0.0} M"
                : settings.TerrainMode == TerrainRenderMode.Voxel
                    ? "RIMCLONE / VOXEL TERRAIN"
                    : "RIMCLONE / HEIGHTMAP FALLBACK",
            20f,
            Color.RayWhite));

        if (settings.ShowPerformanceStats)
        {
            lines.Add((
                $"FPS {GetFPS()}   FRAME {GetFrameTime() * 1000f:0.00} ms",
                18f,
                Color.RayWhite));
            lines.Add((
                settings.TerrainMode == TerrainRenderMode.Voxel
                    ? $"VOXEL STEP {renderer.ActiveVoxelSizeMeters:0.0}m | CHUNK {renderer.VoxelRenderChunkSizeMeters:0.0}m | CHUNKS {renderer.CachedVoxelChunks} | ROCK FRAGMENTS {renderer.DynamicFragmentCount}"
                    : $"HEIGHTMAP CELL {renderer.ActiveRenderCellSize}x{renderer.ActiveRenderCellSize} m",
                17f,
                Color.LightGray));
            if (settings.TerrainMode == TerrainRenderMode.Voxel)
            {
                lines.Add((
                    $"VOXEL CACHE {renderer.CachedVoxelChunks} | FRUSTUM {renderer.VoxelFrustumCandidates} | SELECTED {renderer.VoxelVisibleCandidates}",
                    13f,
                    Color.LightGray));
                lines.Add((
                    $"OVERFLOW {renderer.VoxelCandidateOverflow} | EMPTY {renderer.VoxelEmptyChunks} | VISIBLE {renderer.VisibleRegionsLastFrame}",
                    13f,
                    Color.LightGray));
            }
            else
            {
                lines.Add((
                    $"RENDER REGIONS {terrain.RegionsX}x{terrain.RegionsY} @ {terrain.RegionSize}x{terrain.RegionSize}",
                    13f,
                    Color.LightGray));
                lines.Add((
                    $"VISIBLE REGIONS {renderer.VisibleRegionsLastFrame}   DRAWS {renderer.DrawCallsLastFrame}",
                    13f,
                    Color.LightGray));
            }

            lines.Add((
                $"MESH SWAPS/FRAME {renderer.RebuiltRegions} | CUT CAPS {renderer.CutSurfaceRebuilds}",
                13f,
                Color.LightGray));
            if (settings.TerrainMode == TerrainRenderMode.Voxel)
            {
                lines.Add((
                    $"CPU CAPTURE {renderer.VoxelCpuCaptureMilliseconds:0.00} ms | CPU BUILD {renderer.VoxelWorkerBuildMilliseconds:0.00} ms | GPU UPLOAD {renderer.VoxelGpuUploadMilliseconds:0.00} ms",
                    13f,
                    Color.LightGray));
                lines.Add((
                    $"WORKERS {renderer.VoxelMeshWorkers} | QUEUED {renderer.VoxelPendingMeshJobs} | READY {renderer.VoxelCompletedMeshResultsPending} | BUILT {renderer.VoxelBuildsCompletedLastFrame}/FRAME | GPU PACKETS {renderer.VoxelGpuUploadsLastFrame}",
                    13f,
                    Color.LightGray));
                lines.Add((
                    $"LAST MESH STEP {renderer.VoxelLastBuildStep} ({renderer.VoxelLastBuildStep * 0.1f:0.00} m) | VERTICES {renderer.VoxelLastBuildVertices} | TRIANGLES {renderer.VoxelLastBuildTriangles} | BUILD {renderer.VoxelLastWorkerBuildMilliseconds:0.00} ms | QUEUE {renderer.VoxelLastQueueWaitMilliseconds:0.00} ms | DELIVERY {renderer.VoxelLastResultDeliveryMilliseconds:0.00} ms",
                    13f,
                    Color.LightGray));
                lines.Add((
                    $"GPU CACHE HITS {renderer.VoxelTotalIdenticalSurfaceSkips} SURFACE / {renderer.VoxelTotalIdenticalCutSkips} CAP | UNLOADS {renderer.VoxelTotalGpuUnloads}",
                    13f,
                    Color.LightGray));
                lines.Add((
                    $"TOTAL BUILDS {renderer.VoxelTotalBuildCompleted} | REPEATS {renderer.VoxelTotalRepeatedBuilds} | CANCEL {renderer.VoxelTotalBuildCancelled} | STALE {renderer.VoxelTotalStaleResults} | FAIL {renderer.VoxelTotalBuildFailed}",
                    13f,
                    Color.LightGray));
                lines.Add((
                    $"DUP REQUESTS {renderer.VoxelTotalDuplicateRequests} | QUEUE FULL {renderer.VoxelTotalQueueSaturated}",
                    13f,
                    Color.LightGray));
            }
        }

        if (settings.ShowControls)
        {
            lines.Add((
                "F1 lower tunnel | F2 upper passage | C surface | Tab layers | T top-down view",
                18f,
                Color.RayWhite));
        }

        if (settings.ShowCaveCoordinates)
        {
            lines.Add((
                "LOWER: X 1076-1228 | Z ~1140-1164 | HEIGHT 13-21 m",
                18f,
                Color.LightGray));
            lines.Add((
                "UPPER: near X 1152 | Z 1136-1168 | HEIGHT 25-33 m",
                16f,
                Color.LightGray));
        }

        if (settings.ShowControls)
        {
            lines.Add((
                $"WASD {camera.CurrentMovementSpeedMetersPerSecond:0} m/s | Shift+RMB orbit | MMB pan | Shift+MMB speed",
                16f,
                Color.RayWhite));
            lines.Add((
                "Wheel zoom | Q/E faster slice | PgUp/PgDn 0.1m | Shift+Wheel 0.1m",
                16f,
                Color.RayWhite));

            if (settings.TerrainMode == TerrainRenderMode.Voxel)
            {
                lines.Add((voxelToolStatus, 15f, Color.LightGray));
                lines.Add((
                    "F3 tool | LMB click/drag | F4 material | F5/F6 radius",
                    15f,
                    Color.LightGray));
            }
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
            float fontSize = MathF.Round(Math.Clamp(baseSize, 17f, 20f));
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
        const float minimumSize = 16f;

        while (fontSize > minimumSize &&
            MeasureTextEx(font, fittedText, fontSize, 0f).X > maximumWidth)
        {
            fontSize -= 1f;
        }

        // Remove complete trailing words rather than cutting glyph sequences.
        while (fittedText.Length > 4 &&
            MeasureTextEx(font, fittedText, fontSize, 0f).X > maximumWidth)
        {
            int lastSpace = fittedText.LastIndexOf(' ');
            if (lastSpace <= 0)
            {
                fittedText = fittedText[..^1];
                continue;
            }

            fittedText = fittedText[..lastSpace].TrimEnd() + "...";
        }

        DrawTextEx(
            font,
            fittedText,
            new Vector2(MathF.Round(x), MathF.Round(y)),
            fontSize,
            0f,
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
