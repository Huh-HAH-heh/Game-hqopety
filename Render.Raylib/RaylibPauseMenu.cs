using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

internal enum RaylibMenuAction : byte
{
    None,
    Exit,
    ResetCamera,
    FocusLowerTunnel,
    FocusUpperPassage,
    ToggleCutaway
}

internal sealed class RaylibPauseMenu : IDisposable
{
    private enum MenuPage : byte
    {
        Closed,
        Main,
        Settings
    }

    private enum SettingsSection : byte
    {
        Display,
        Camera,
        Terrain,
        Environment,
        Interface,
        Performance
    }

    private enum SliderId : byte
    {
        None,
        CameraMove,
        CameraRotation,
        CameraPan,
        CameraZoom,
        CameraVertical,
        PaletteRed,
        PaletteGreen,
        PaletteBlue,
        RenderDistance
    }

    private static readonly (int Width, int Height)[] Resolutions =
    {
        (1024, 768),
        (1280, 720),
        (1280, 800),
        (1366, 768),
        (1440, 900),
        (1600, 900),
        (1680, 1050),
        (1920, 1080),
        (2560, 1440)
    };

    private static readonly int[] FrameRateLimits =
    {
        30, 45, 60, 90, 120, 144, 165, 240, 0
    };

    private static readonly string[] SectionNames =
    {
        "ЭКРАН И ГРАФИКА",
        "КАМЕРА И УПРАВЛЕНИЕ",
        "РЕЛЬЕФ И СРЕЗЫ",
        "ЦВЕТА ОКРУЖЕНИЯ",
        "ИНТЕРФЕЙС",
        "ПРОИЗВОДИТЕЛЬНОСТЬ"
    };

    private static readonly string[] SectionDescriptions =
    {
        "Окно, разрешение и качество сглаживания",
        "Режим обзора и скорость перемещения",
        "Тоннели, срезы и геологическая штриховка",
        "Палитра поверхности, скал и подземных слоёв",
        "Информационные панели и подсказки",
        "Частота кадров и дистанция отрисовки"
    };

    private static readonly string[] ColorNames =
    {
        "Низины",
        "Возвышенности",
        "Обнажённые скалы",
        "Поверхность среза",
        "Геологическая штриховка",
        "Фон окружения"
    };

    private const float MainWidth = 460f;
    private const float MainHeight = 340f;
    private const float SettingsWidth = 1080f;
    private const float SettingsHeight = 780f;

    private static readonly int[] FontCodepoints = BuildFontCodepoints();

    private readonly Font _font;
    private readonly bool _ownsFont;

    private MenuPage _page;
    private SettingsSection? _expandedSection = SettingsSection.Camera;
    private TerrainColorSlot _selectedColorSlot = TerrainColorSlot.Lowlands;

    private Vector2 _mainPosition;
    private Vector2 _settingsPosition;
    private Vector2 _dragOffset;
    private bool _hasPanelPositions;
    private bool _draggingMain;
    private bool _draggingSettings;
    private SliderId _activeSlider;

    public RaylibViewerSettings Settings { get; }
    public bool IsOpen => _page != MenuPage.Closed;
    public bool ShowHud => Settings.ShowHud;
    public bool ShowControls => Settings.ShowControls;
    public bool CutawayEnabled
    {
        get => Settings.CutawayEnabled;
        set => Settings.CutawayEnabled = value;
    }

    public float CameraSensitivity => Settings.CameraRotationSpeed;
    public int TargetFrameRate => Settings.TargetFps;
    public int PaletteRevision { get; private set; }

    public RaylibPauseMenu(RaylibViewerSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Settings.Normalize();

        _font = LoadMenuFont(out bool ownsFont);
        _ownsFont = ownsFont;

        SetTargetFPS(Settings.TargetFps);
    }

    public RaylibMenuAction Update()
    {
        if (IsKeyPressed(KeyboardKey.Escape))
        {
            _page = _page switch
            {
                MenuPage.Closed => MenuPage.Main,
                MenuPage.Main => MenuPage.Closed,
                MenuPage.Settings => MenuPage.Main,
                _ => MenuPage.Closed
            };

            _activeSlider = SliderId.None;
            _draggingMain = false;
            _draggingSettings = false;
            SaveSettings();
            return RaylibMenuAction.None;
        }

        if (_page == MenuPage.Closed)
            return RaylibMenuAction.None;

        Vector2 mouse = GetMousePosition();

        if (_activeSlider != SliderId.None)
        {
            if (IsMouseButtonDown(MouseButton.Left))
            {
                UpdateSliderFromMouse(_activeSlider, mouse, GetPanelBounds());
            }
            else
            {
                _activeSlider = SliderId.None;
                SaveSettings();
            }

            return RaylibMenuAction.None;
        }

        if (_draggingMain || _draggingSettings)
        {
            if (IsMouseButtonDown(MouseButton.Left))
            {
                Vector2 position = mouse - _dragOffset;
                if (_draggingMain)
                    _mainPosition = ClampPosition(position, MainWidth, MainHeight);
                else
                    _settingsPosition = ClampPosition(position, SettingsWidth, SettingsHeight);
            }
            else
            {
                _draggingMain = false;
                _draggingSettings = false;
            }

            return RaylibMenuAction.None;
        }

        if (!IsMouseButtonPressed(MouseButton.Left))
            return RaylibMenuAction.None;

        Rectangle panel = GetPanelBounds();
        Rectangle header = new(panel.X, panel.Y, panel.Width, 62f);
        if (CheckCollisionPointRec(mouse, header) &&
            !(_page == MenuPage.Settings && HitCloseButton(mouse, panel)))
        {
            _draggingMain = _page == MenuPage.Main;
            _draggingSettings = _page == MenuPage.Settings;
            _dragOffset = mouse - new Vector2(panel.X, panel.Y);
            return RaylibMenuAction.None;
        }

        return _page == MenuPage.Main
            ? HandleMainClick(mouse, panel)
            : HandleSettingsClick(mouse, panel);
    }

    public void Draw()
    {
        if (_page == MenuPage.Closed)
            return;

        EnsurePanelPositions();

        DrawRectangle(
            0,
            0,
            GetScreenWidth(),
            GetScreenHeight(),
            new Color(0, 0, 0, 184));

        Vector2 mouse = GetMousePosition();
        Rectangle panel = GetPanelBounds();
        DrawPanel(panel);

        if (_page == MenuPage.Main)
            DrawMainMenu(panel, mouse);
        else
            DrawSettings(panel, mouse);
    }

    public void SaveSettings() => Settings.Save();

    private RaylibMenuAction HandleMainClick(Vector2 mouse, Rectangle panel)
    {
        if (HitMainButton(mouse, panel, 104f))
        {
            _page = MenuPage.Closed;
            return RaylibMenuAction.None;
        }

        if (HitMainButton(mouse, panel, 162f))
        {
            _page = MenuPage.Settings;
            return RaylibMenuAction.None;
        }

        if (HitMainButton(mouse, panel, 220f))
            return RaylibMenuAction.Exit;

        return RaylibMenuAction.None;
    }

    private RaylibMenuAction HandleSettingsClick(Vector2 mouse, Rectangle panel)
    {
        if (HitCloseButton(mouse, panel) || HitBackButton(mouse, panel))
        {
            _page = MenuPage.Main;
            _activeSlider = SliderId.None;
            SaveSettings();
            return RaylibMenuAction.None;
        }

        for (int i = 0; i < SectionNames.Length; i++)
        {
            if (!CheckCollisionPointRec(mouse, GetSectionBounds(panel, i)))
                continue;

            SettingsSection section = (SettingsSection)i;
            _expandedSection = _expandedSection == section ? null : section;
            _activeSlider = SliderId.None;
            return RaylibMenuAction.None;
        }

        if (!_expandedSection.HasValue)
            return RaylibMenuAction.None;

        RaylibMenuAction action = _expandedSection.Value switch
        {
            SettingsSection.Display => HandleDisplayClick(mouse, panel),
            SettingsSection.Camera => HandleCameraClick(mouse, panel),
            SettingsSection.Terrain => HandleTerrainClick(mouse, panel),
            SettingsSection.Environment => HandleEnvironmentClick(mouse, panel),
            SettingsSection.Interface => HandleInterfaceClick(mouse, panel),
            SettingsSection.Performance => HandlePerformanceClick(mouse, panel),
            _ => RaylibMenuAction.None
        };

        return action;
    }

    private RaylibMenuAction HandleDisplayClick(Vector2 mouse, Rectangle panel)
    {
        if (HitActionButton(mouse, panel, 164f))
        {
            ToggleFullscreenMode();
            SaveSettings();
        }
        else if (HitActionButton(mouse, panel, 234f))
        {
            CycleResolution();
            SaveSettings();
        }
        else if (HitActionButton(mouse, panel, 304f))
        {
            Settings.VSync = !Settings.VSync;
            SaveSettings();
        }
        else if (HitActionButton(mouse, panel, 374f))
        {
            Settings.Msaa4x = !Settings.Msaa4x;
            SaveSettings();
        }

        return RaylibMenuAction.None;
    }

    private RaylibMenuAction HandleCameraClick(Vector2 mouse, Rectangle panel)
    {
        if (HitActionButton(mouse, panel, 164f))
        {
            Settings.CameraMode = (RaylibCameraMode)(((int)Settings.CameraMode + 1) % 3);
            SaveSettings();
            if (Settings.CameraMode == RaylibCameraMode.TopDown)
                return RaylibMenuAction.ResetCamera;
        }
        else if (HitSlider(mouse, panel, SliderId.CameraMove, 234f))
            BeginSlider(SliderId.CameraMove, mouse, panel);
        else if (HitSlider(mouse, panel, SliderId.CameraRotation, 312f))
            BeginSlider(SliderId.CameraRotation, mouse, panel);
        else if (HitSlider(mouse, panel, SliderId.CameraPan, 390f))
            BeginSlider(SliderId.CameraPan, mouse, panel);
        else if (HitSlider(mouse, panel, SliderId.CameraZoom, 468f))
            BeginSlider(SliderId.CameraZoom, mouse, panel);
        else if (HitSlider(mouse, panel, SliderId.CameraVertical, 546f))
            BeginSlider(SliderId.CameraVertical, mouse, panel);
        else if (HitActionButton(mouse, panel, 624f))
            return RaylibMenuAction.ResetCamera;
        else if (HitHalfActionButton(mouse, panel, 650f, left: true))
            return RaylibMenuAction.FocusLowerTunnel;
        else if (HitHalfActionButton(mouse, panel, 650f, left: false))
            return RaylibMenuAction.FocusUpperPassage;

        SaveSettings();
        return RaylibMenuAction.None;
    }

    private RaylibMenuAction HandleTerrainClick(Vector2 mouse, Rectangle panel)
    {
        if (HitActionButton(mouse, panel, 164f))
        {
            Settings.CutawayEnabled = !Settings.CutawayEnabled;
            SaveSettings();
            return RaylibMenuAction.ToggleCutaway;
        }

        if (HitActionButton(mouse, panel, 234f))
        {
            Settings.Palette.ShowCutHatching = !Settings.Palette.ShowCutHatching;
            PaletteRevision++;
            SaveSettings();
        }
        else if (HitHalfActionButton(mouse, panel, 304f, left: true))
            return RaylibMenuAction.FocusLowerTunnel;
        else if (HitHalfActionButton(mouse, panel, 304f, left: false))
            return RaylibMenuAction.FocusUpperPassage;
        else if (HitActionButton(mouse, panel, 374f))
        {
            Settings.CutawayEnabled = false;
            SaveSettings();
            return RaylibMenuAction.ResetCamera;
        }

        return RaylibMenuAction.None;
    }

    private RaylibMenuAction HandleEnvironmentClick(Vector2 mouse, Rectangle panel)
    {
        for (int i = 0; i < 6; i++)
        {
            if (!CheckCollisionPointRec(mouse, GetColorSlotBounds(panel, i)))
                continue;

            _selectedColorSlot = (TerrainColorSlot)i;
            return RaylibMenuAction.None;
        }

        if (HitActionButton(mouse, panel, 164f))
        {
            ApplyNextPalettePreset();
            PaletteRevision++;
            SaveSettings();
        }
        else if (HitSlider(mouse, panel, SliderId.PaletteRed, 442f))
            BeginSlider(SliderId.PaletteRed, mouse, panel);
        else if (HitSlider(mouse, panel, SliderId.PaletteGreen, 520f))
            BeginSlider(SliderId.PaletteGreen, mouse, panel);
        else if (HitSlider(mouse, panel, SliderId.PaletteBlue, 598f))
            BeginSlider(SliderId.PaletteBlue, mouse, panel);

        return RaylibMenuAction.None;
    }

    private RaylibMenuAction HandleInterfaceClick(Vector2 mouse, Rectangle panel)
    {
        if (HitActionButton(mouse, panel, 164f))
            Settings.ShowHud = !Settings.ShowHud;
        else if (HitActionButton(mouse, panel, 234f))
            Settings.ShowControls = !Settings.ShowControls;
        else if (HitActionButton(mouse, panel, 304f))
            Settings.ShowPerformanceStats = !Settings.ShowPerformanceStats;
        else if (HitActionButton(mouse, panel, 374f))
            Settings.ShowCaveCoordinates = !Settings.ShowCaveCoordinates;
        else
            return RaylibMenuAction.None;

        SaveSettings();
        return RaylibMenuAction.None;
    }

    private RaylibMenuAction HandlePerformanceClick(Vector2 mouse, Rectangle panel)
    {
        if (HitActionButton(mouse, panel, 164f))
        {
            int index = Array.IndexOf(FrameRateLimits, Settings.TargetFps);
            Settings.TargetFps = FrameRateLimits[(index + 1 + FrameRateLimits.Length) % FrameRateLimits.Length];
            SetTargetFPS(Settings.TargetFps);
            SaveSettings();
        }
        else if (HitSlider(mouse, panel, SliderId.RenderDistance, 164f))
            BeginSlider(SliderId.RenderDistance, mouse, panel);

        return RaylibMenuAction.None;
    }

    private void BeginSlider(SliderId slider, Vector2 mouse, Rectangle panel)
    {
        _activeSlider = slider;
        UpdateSliderFromMouse(slider, mouse, panel);
    }

    private void UpdateSliderFromMouse(
        SliderId slider,
        Vector2 mouse,
        Rectangle panel)
    {
        Rectangle bounds = GetSliderBounds(panel, slider);
        float amount = Math.Clamp((mouse.X - bounds.X) / Math.Max(1f, bounds.Width), 0f, 1f);

        switch (slider)
        {
            case SliderId.CameraMove:
                Settings.CameraMoveSpeed = Lerp(0.25f, 4f, amount);
                break;
            case SliderId.CameraRotation:
                Settings.CameraRotationSpeed = Lerp(0.25f, 4f, amount);
                break;
            case SliderId.CameraPan:
                Settings.CameraPanSpeed = Lerp(0.25f, 4f, amount);
                break;
            case SliderId.CameraZoom:
                Settings.CameraZoomSpeed = Lerp(0.25f, 4f, amount);
                break;
            case SliderId.CameraVertical:
                Settings.CameraVerticalSpeed = Lerp(0.25f, 4f, amount);
                break;
            case SliderId.RenderDistance:
                Settings.RenderDistanceMeters = Lerp(500f, 18000f, amount);
                break;
            case SliderId.PaletteRed:
                GetSelectedColor().R = (int)MathF.Round(amount * 255f);
                PaletteRevision++;
                break;
            case SliderId.PaletteGreen:
                GetSelectedColor().G = (int)MathF.Round(amount * 255f);
                PaletteRevision++;
                break;
            case SliderId.PaletteBlue:
                GetSelectedColor().B = (int)MathF.Round(amount * 255f);
                PaletteRevision++;
                break;
        }
    }

    private void ApplyNextPalettePreset()
    {
        _palettePreset = (_palettePreset + 1) % 4;
        TerrainPalette palette = Settings.Palette;

        switch (_palettePreset)
        {
            case 0:
                palette.Lowlands = new RgbColorSetting(7, 8, 10);
                palette.Highlands = new RgbColorSetting(41, 42, 45);
                palette.CliffFaces = new RgbColorSetting(22, 23, 26);
                palette.CutSurface = new RgbColorSetting(14, 15, 18);
                palette.CutHatching = new RgbColorSetting(125, 132, 142);
                palette.Background = new RgbColorSetting(0, 0, 0);
                break;
            case 1:
                palette.Lowlands = new RgbColorSetting(16, 20, 25);
                palette.Highlands = new RgbColorSetting(67, 80, 96);
                palette.CliffFaces = new RgbColorSetting(24, 34, 47);
                palette.CutSurface = new RgbColorSetting(19, 29, 42);
                palette.CutHatching = new RgbColorSetting(134, 152, 171);
                palette.Background = new RgbColorSetting(4, 8, 14);
                break;
            case 2:
                palette.Lowlands = new RgbColorSetting(14, 24, 17);
                palette.Highlands = new RgbColorSetting(65, 78, 48);
                palette.CliffFaces = new RgbColorSetting(22, 34, 19);
                palette.CutSurface = new RgbColorSetting(18, 27, 16);
                palette.CutHatching = new RgbColorSetting(125, 151, 111);
                palette.Background = new RgbColorSetting(2, 7, 4);
                break;
            default:
                palette.Lowlands = new RgbColorSetting(38, 31, 22);
                palette.Highlands = new RgbColorSetting(105, 89, 62);
                palette.CliffFaces = new RgbColorSetting(43, 35, 24);
                palette.CutSurface = new RgbColorSetting(57, 47, 32);
                palette.CutHatching = new RgbColorSetting(163, 145, 112);
                palette.Background = new RgbColorSetting(13, 10, 6);
                break;
        }
    }

    private int _palettePreset;

    private RgbColorSetting GetSelectedColor() => GetColor(_selectedColorSlot);

    private RgbColorSetting GetColor(TerrainColorSlot slot) => slot switch
    {
        TerrainColorSlot.Lowlands => Settings.Palette.Lowlands,
        TerrainColorSlot.Highlands => Settings.Palette.Highlands,
        TerrainColorSlot.CliffFaces => Settings.Palette.CliffFaces,
        TerrainColorSlot.CutSurface => Settings.Palette.CutSurface,
        TerrainColorSlot.CutHatching => Settings.Palette.CutHatching,
        _ => Settings.Palette.Background
    };

    private void ToggleFullscreenMode()
    {
        if (IsWindowFullscreen())
        {
            ToggleFullscreen();
            Settings.Fullscreen = false;
            SetWindowSize(Settings.WindowWidth, Settings.WindowHeight);
        }
        else
        {
            Settings.WindowWidth = GetScreenWidth();
            Settings.WindowHeight = GetScreenHeight();
            ToggleFullscreen();
            Settings.Fullscreen = true;
        }
    }

    private void CycleResolution()
    {
        if (IsWindowFullscreen())
            ToggleFullscreenMode();

        int monitor = GetCurrentMonitor();
        int maxWidth = GetMonitorWidth(monitor);
        int maxHeight = GetMonitorHeight(monitor);
        int current = Array.FindIndex(
            Resolutions,
            size => size.Width == GetScreenWidth() && size.Height == GetScreenHeight());

        for (int attempt = 0; attempt < Resolutions.Length; attempt++)
        {
            current = (current + 1) % Resolutions.Length;
            (int width, int height) = Resolutions[current];

            if (width > maxWidth || height > maxHeight)
                continue;

            Settings.WindowWidth = width;
            Settings.WindowHeight = height;
            SetWindowSize(width, height);
            return;
        }
    }

    private void DrawMainMenu(Rectangle panel, Vector2 mouse)
    {
        DrawLabel("ГЛАВНОЕ МЕНЮ", panel.X + 28f, panel.Y + 23f, 22f,
            new Color(241, 245, 250));
        DrawLabel("RIMCLONE  •  ПРОСМОТР РЕЛЬЕФА", panel.X + 28f, panel.Y + 54f, 13f,
            new Color(154, 168, 185));

        DrawButton(panel, mouse, 104f, 42f, "ПРОДОЛЖИТЬ");
        DrawButton(panel, mouse, 162f, 42f, "НАСТРОЙКИ");
        DrawButton(panel, mouse, 220f, 42f, "ВЫХОД");
    }

    private void DrawSettings(Rectangle panel, Vector2 mouse)
    {
        DrawLabel("НАСТРОЙКИ", panel.X + 28f, panel.Y + 20f, 22f,
            new Color(241, 245, 250));
        DrawLabel("РАЗДЕЛЫ РАЗВЁРТЫВАЮТСЯ СЛЕВА", panel.X + 28f, panel.Y + 50f, 12f,
            new Color(154, 168, 185));

        DrawRectangle(
            (int)(panel.X + 270f),
            (int)(panel.Y + 82f),
            1,
            (int)(panel.Height - 106f),
            new Color(54, 64, 77));

        for (int i = 0; i < SectionNames.Length; i++)
            DrawSectionButton(panel, mouse, i);

        if (_expandedSection.HasValue)
            DrawExpandedSection(panel, mouse, _expandedSection.Value);
        else
            DrawLabel(
                "ВЫБЕРИ РАЗДЕЛ, ЧТОБЫ РАСКРЫТЬ ЕГО НАСТРОЙКИ",
                panel.X + 310f,
                panel.Y + 122f,
                15f,
                new Color(170, 184, 201));

        DrawCloseButton(panel, mouse);
        DrawBackButton(panel, mouse);
    }

    private void DrawSectionButton(Rectangle panel, Vector2 mouse, int index)
    {
        Rectangle bounds = GetSectionBounds(panel, index);
        bool expanded = _expandedSection == (SettingsSection)index;
        bool hovered = CheckCollisionPointRec(mouse, bounds);

        DrawRectangle(
            (int)bounds.X,
            (int)bounds.Y,
            (int)bounds.Width,
            (int)bounds.Height,
            expanded
                ? new Color(49, 62, 78)
                : hovered
                    ? new Color(36, 46, 59)
                    : new Color(25, 32, 42));
        DrawRectangleLinesEx(bounds, 1f, new Color(74, 88, 106));

        DrawLabel(
            expanded ? "▼" : "›",
            bounds.X + 12f,
            bounds.Y + 10f,
            19f,
            new Color(219, 226, 235));
        DrawLabel(
            SectionNames[index],
            bounds.X + 42f,
            bounds.Y + 8f,
            12f,
            new Color(241, 245, 250));
        DrawLabel(
            SectionDescriptions[index],
            bounds.X + 42f,
            bounds.Y + 29f,
            10f,
            new Color(154, 168, 185));
    }

    private void DrawExpandedSection(
        Rectangle panel,
        Vector2 mouse,
        SettingsSection section)
    {
        float x = panel.X + 304f;
        float y = panel.Y + 100f;

        DrawLabel(
            SectionNames[(int)section],
            x,
            y,
            18f,
            new Color(232, 238, 245));
        DrawLabel(
            SectionDescriptions[(int)section],
            x,
            y + 26f,
            11f,
            new Color(154, 168, 185));

        switch (section)
        {
            case SettingsSection.Display:
                DrawDisplaySettings(panel, mouse);
                break;
            case SettingsSection.Camera:
                DrawCameraSettings(panel, mouse);
                break;
            case SettingsSection.Terrain:
                DrawTerrainSettings(panel, mouse);
                break;
            case SettingsSection.Environment:
                DrawEnvironmentSettings(panel, mouse);
                break;
            case SettingsSection.Interface:
                DrawInterfaceSettings(panel, mouse);
                break;
            case SettingsSection.Performance:
                DrawPerformanceSettings(panel, mouse);
                break;
        }
    }

    private void DrawDisplaySettings(Rectangle panel, Vector2 mouse)
    {
        float y = panel.Y + 164f;
        DrawOptionRow(panel, mouse, y, "РЕЖИМ ОКНА",
            Settings.Fullscreen ? "ПОЛНЫЙ ЭКРАН" : "ОКОННЫЙ", 88f, Settings.Fullscreen);
        DrawOptionRow(panel, mouse, y + 70f, "РАЗРЕШЕНИЕ",
            $"{GetScreenWidth()} × {GetScreenHeight()}", 158f);
        DrawOptionRow(panel, mouse, y + 140f, "ВЕРТИКАЛЬНАЯ СИНХРОНИЗАЦИЯ",
            Settings.VSync ? "ВКЛ" : "ВЫКЛ", 228f, Settings.VSync,
            "Изменение применяется после перезапуска приложения");
        DrawOptionRow(panel, mouse, y + 210f, "СГЛАЖИВАНИЕ MSAA 4×",
            Settings.Msaa4x ? "ВКЛ" : "ВЫКЛ", 298f, Settings.Msaa4x,
            "Изменение применяется после перезапуска приложения");
    }

    private void DrawCameraSettings(Rectangle panel, Vector2 mouse)
    {
        float y = panel.Y + 164f;
        string mode = Settings.CameraMode switch
        {
            RaylibCameraMode.Rts => "RTS / СТРАТЕГИЯ",
            RaylibCameraMode.Orbit => "ORBIT / СВОБОДНЫЙ ОБЗОР",
            _ => "TOP-DOWN / СВЕРХУ"
        };

        DrawOptionRow(panel, mouse, y, "РЕЖИМ КАМЕРЫ", mode, 88f);
        DrawSliderRow(panel, mouse, SliderId.CameraMove, y + 70f,
            "СКОРОСТЬ ПЕРЕМЕЩЕНИЯ", Settings.CameraMoveSpeed, "×");
        DrawSliderRow(panel, mouse, SliderId.CameraRotation, y + 148f,
            "СКОРОСТЬ ПОВОРОТА", Settings.CameraRotationSpeed, "×");
        DrawSliderRow(panel, mouse, SliderId.CameraPan, y + 226f,
            "СКОРОСТЬ ПАНОРАМИРОВАНИЯ", Settings.CameraPanSpeed, "×");
        DrawSliderRow(panel, mouse, SliderId.CameraZoom, y + 304f,
            "СКОРОСТЬ МАСШТАБИРОВАНИЯ", Settings.CameraZoomSpeed, "×");
        DrawSliderRow(panel, mouse, SliderId.CameraVertical, y + 382f,
            "СКОРОСТЬ ИЗМЕНЕНИЯ ВЫСОТЫ", Settings.CameraVerticalSpeed, "×");
        DrawOptionRow(panel, mouse, y + 460f, "ТОЧКА ОБЗОРА",
            "СБРОСИТЬ КАМЕРУ", 560f);
        DrawLabel("БЫСТРОЕ ПЕРЕМЕЩЕНИЕ", panel.X + 304f, y + 530f, 14f,
            new Color(218, 226, 236));
        DrawHalfActionPair(panel, mouse, y + 486f, "НИЖНИЙ ТОННЕЛЬ", "ВЕРХНИЙ ПРОХОД");
    }

    private void DrawTerrainSettings(Rectangle panel, Vector2 mouse)
    {
        float y = panel.Y + 164f;
        DrawOptionRow(panel, mouse, y, "ГОРИЗОНТАЛЬНЫЙ СРЕЗ",
            Settings.CutawayEnabled ? "ВКЛ" : "ВЫКЛ", 88f,
            Settings.CutawayEnabled);
        DrawOptionRow(panel, mouse, y + 70f, "ШТРИХОВКА ГЕОЛОГИИ",
            Settings.Palette.ShowCutHatching ? "ВКЛ" : "ВЫКЛ", 158f,
            Settings.Palette.ShowCutHatching);
        DrawLabel("ФОКУСИРОВКА", panel.X + 304f, y + 150f, 14f,
            new Color(218, 226, 236));
        DrawHalfActionPair(panel, mouse, y + 140f, "НИЖНИЙ ТОННЕЛЬ", "ВЕРХНИЙ ПРОХОД");
        DrawOptionRow(panel, mouse, y + 210f, "ВИД ПОВЕРХНОСТИ",
            "СБРОСИТЬ СРЕЗ И КАМЕРУ", 298f);
        DrawLabel(
            "Срез двигается вместе с высотой фокуса камеры: Q/E и PgUp/PgDn.",
            panel.X + 304f,
            y + 300f,
            12f,
            new Color(170, 184, 201));
    }

    private void DrawEnvironmentSettings(Rectangle panel, Vector2 mouse)
    {
        float y = panel.Y + 164f;
        DrawOptionRow(panel, mouse, y, "ГОТОВЫЕ ПАЛИТРЫ",
            "СМЕНИТЬ ПАЛИТРУ", 88f);

        for (int i = 0; i < 6; i++)
        {
            Rectangle bounds = GetColorSlotBounds(panel, i);
            bool active = _selectedColorSlot == (TerrainColorSlot)i;
            RgbColorSetting color = GetColor((TerrainColorSlot)i);
            Color fill = color.ToRaylibColor();

            DrawRectangle(
                (int)bounds.X,
                (int)bounds.Y,
                (int)bounds.Width,
                (int)bounds.Height,
                active ? new Color(51, 65, 82) : new Color(27, 35, 46));
            DrawRectangleLinesEx(
                bounds,
                active ? 2f : 1f,
                active ? new Color(184, 198, 216) : new Color(74, 88, 106));
            DrawRectangle(
                (int)(bounds.X + 10f),
                (int)(bounds.Y + 9f),
                26,
                26,
                fill);
            DrawLabel(
                ColorNames[i],
                bounds.X + 46f,
                bounds.Y + 13f,
                11f,
                new Color(241, 245, 250));
        }

        RgbColorSetting selected = GetSelectedColor();
        DrawLabel(
            $"ЦВЕТ: {ColorNames[(int)_selectedColorSlot]}  RGB({selected.R}, {selected.G}, {selected.B})",
            panel.X + 304f,
            y + 252f,
            13f,
            new Color(211, 219, 228));

        DrawSliderRow(panel, mouse, SliderId.PaletteRed, y + 278f,
            "КРАСНЫЙ КАНАЛ", selected.R, "R");
        DrawSliderRow(panel, mouse, SliderId.PaletteGreen, y + 356f,
            "ЗЕЛЁНЫЙ КАНАЛ", selected.G, "G");
        DrawSliderRow(panel, mouse, SliderId.PaletteBlue, y + 434f,
            "СИНИЙ КАНАЛ", selected.B, "B");

        DrawRectangle(
            (int)(panel.X + 304f),
            (int)(y + 498f),
            70,
            36,
            selected.ToRaylibColor());
        DrawLabel(
            "Цвет применяется к мешам видимой местности по мере их обновления.",
            panel.X + 388f,
            y + 508f,
            11f,
            new Color(154, 168, 185));
    }

    private void DrawInterfaceSettings(Rectangle panel, Vector2 mouse)
    {
        float y = panel.Y + 164f;
        DrawOptionRow(panel, mouse, y, "ИНФОРМАЦИОННАЯ ПАНЕЛЬ",
            Settings.ShowHud ? "ВКЛ" : "ВЫКЛ", 88f, Settings.ShowHud);
        DrawOptionRow(panel, mouse, y + 70f, "ПОДСКАЗКИ УПРАВЛЕНИЯ",
            Settings.ShowControls ? "ВКЛ" : "ВЫКЛ", 158f, Settings.ShowControls);
        DrawOptionRow(panel, mouse, y + 140f, "СТАТИСТИКА РЕНДЕРА",
            Settings.ShowPerformanceStats ? "ВКЛ" : "ВЫКЛ", 228f,
            Settings.ShowPerformanceStats);
        DrawOptionRow(panel, mouse, y + 210f, "КООРДИНАТЫ ТОННЕЛЕЙ",
            Settings.ShowCaveCoordinates ? "ВКЛ" : "ВЫКЛ", 298f,
            Settings.ShowCaveCoordinates);
    }

    private void DrawPerformanceSettings(Rectangle panel, Vector2 mouse)
    {
        float y = panel.Y + 164f;
        string fps = Settings.TargetFps == 0
            ? "БЕЗ ОГРАНИЧЕНИЯ"
            : $"{Settings.TargetFps} FPS";

        DrawOptionRow(panel, mouse, y, "ЛИМИТ КАДРОВ",
            fps, 88f);
        DrawSliderRow(panel, mouse, SliderId.RenderDistance, y + 70f,
            "ДИСТАНЦИЯ ОТРИСОВКИ", Settings.RenderDistanceMeters, " м");

        DrawLabel(
            "Уменьши дистанцию, если нужно снизить нагрузку на GPU.",
            panel.X + 304f,
            y + 160f,
            12f,
            new Color(170, 184, 201));
        DrawLabel(
            "VSync и MSAA находятся в разделе «Экран и графика».",
            panel.X + 304f,
            y + 188f,
            12f,
            new Color(170, 184, 201));
    }

    private void DrawOptionRow(
        Rectangle panel,
        Vector2 mouse,
        float y,
        string label,
        string value,
        float actionOffset,
        bool active = false,
        string? help = null)
    {
        float x = panel.X + 304f;
        DrawLabel(label, x, y + 10f, 14f, new Color(218, 226, 236));

        Rectangle button = GetActionBounds(panel, y - panel.Y);
        DrawButtonAt(button, mouse, value, active);

        if (!string.IsNullOrWhiteSpace(help))
            DrawLabel(help, x, y + 39f, 10f, new Color(145, 157, 173));
    }

    private void DrawSliderRow(
        Rectangle panel,
        Vector2 mouse,
        SliderId id,
        float y,
        string label,
        float value,
        string unit)
    {
        Rectangle bounds = GetSliderBounds(panel, id);
        DrawLabel(
            $"{label}: {FormatSliderValue(id, value)}{unit}",
            bounds.X,
            y,
            13f,
            new Color(218, 226, 236));

        DrawRectangle(
            (int)bounds.X,
            (int)bounds.Y,
            (int)bounds.Width,
            (int)bounds.Height,
            new Color(57, 67, 81));

        float amount = GetSliderAmount(id, value);
        float fillWidth = bounds.Width * amount;
        DrawRectangle(
            (int)bounds.X,
            (int)bounds.Y,
            (int)fillWidth,
            (int)bounds.Height,
            new Color(164, 178, 195));
        DrawCircleV(
            new Vector2(bounds.X + fillWidth, bounds.Y + bounds.Height * 0.5f),
            7f,
            new Color(242, 245, 249));
    }

    private void DrawPanel(Rectangle panel)
    {
        DrawRectangle(
            (int)panel.X,
            (int)panel.Y,
            (int)panel.Width,
            (int)panel.Height,
            new Color(16, 21, 29, 252));
        DrawRectangleLinesEx(panel, 1f, new Color(83, 96, 113, 255));
        DrawRectangle(
            (int)panel.X + 1,
            (int)panel.Y + 1,
            (int)panel.Width - 2,
            3,
            new Color(143, 153, 166, 255));
    }

    private void DrawButton(Rectangle panel, Vector2 mouse, float offsetY, float height, string text)
    {
        Rectangle bounds = new(
            panel.X + 28f,
            panel.Y + offsetY,
            panel.Width - 56f,
            height);
        DrawButtonAt(bounds, mouse, text);
    }

    private void DrawHalfActionPair(
        Rectangle panel,
        Vector2 mouse,
        float y,
        string leftText,
        string rightText)
    {
        DrawButtonAt(GetHalfActionBounds(panel, y - panel.Y, left: true), mouse, leftText);
        DrawButtonAt(GetHalfActionBounds(panel, y - panel.Y, left: false), mouse, rightText);
    }

    private void DrawButtonAt(
        Rectangle bounds,
        Vector2 mouse,
        string text,
        bool active = false)
    {
        bool hovered = CheckCollisionPointRec(mouse, bounds);
        Color fill = active
            ? new Color(57, 70, 87, 255)
            : hovered
                ? new Color(48, 60, 76, 255)
                : new Color(28, 36, 47, 255);

        DrawRectangle(
            (int)bounds.X,
            (int)bounds.Y,
            (int)bounds.Width,
            (int)bounds.Height,
            fill);
        DrawRectangleLinesEx(bounds, 1f, new Color(74, 88, 106, 255));
        DrawCenteredLabel(text, bounds, 13f, new Color(241, 245, 250));
    }

    private void DrawCloseButton(Rectangle panel, Vector2 mouse)
    {
        Rectangle bounds = new(
            panel.X + panel.Width - 48f,
            panel.Y + 14f,
            32f,
            30f);
        DrawButtonAt(bounds, mouse, "×");
    }

    private void DrawBackButton(Rectangle panel, Vector2 mouse)
    {
        Rectangle bounds = new(
            panel.X + 24f,
            panel.Y + panel.Height - 48f,
            126f,
            32f);
        DrawButtonAt(bounds, mouse, "НАЗАД");
    }

    private void DrawLabel(string text, float x, float y, float fontSize, Color color)
    {
        DrawTextEx(_font, text, new Vector2(x, y), fontSize, 1f, color);
    }

    private void DrawCenteredLabel(string text, Rectangle bounds, float fontSize, Color color)
    {
        Vector2 measured = MeasureTextEx(_font, text, fontSize, 1f);
        Vector2 position = new(
            bounds.X + (bounds.Width - measured.X) * 0.5f,
            bounds.Y + (bounds.Height - measured.Y) * 0.5f - 1f);

        DrawTextEx(_font, text, position, fontSize, 1f, color);
    }

    private Rectangle GetPanelBounds()
    {
        EnsurePanelPositions();

        bool main = _page == MenuPage.Main;
        float width = main
            ? MathF.Min(MainWidth, GetScreenWidth() - 16f)
            : MathF.Min(SettingsWidth, GetScreenWidth() - 16f);
        float height = main
            ? MathF.Min(MainHeight, GetScreenHeight() - 16f)
            : MathF.Min(SettingsHeight, GetScreenHeight() - 16f);
        Vector2 position = main ? _mainPosition : _settingsPosition;
        position = ClampPosition(position, width, height);

        if (main)
            _mainPosition = position;
        else
            _settingsPosition = position;

        return new Rectangle(position.X, position.Y, width, height);
    }

    private void EnsurePanelPositions()
    {
        if (_hasPanelPositions)
            return;

        _mainPosition = new Vector2(
            (GetScreenWidth() - MainWidth) * 0.5f,
            (GetScreenHeight() - MainHeight) * 0.5f);
        _settingsPosition = new Vector2(
            (GetScreenWidth() - SettingsWidth) * 0.5f,
            (GetScreenHeight() - SettingsHeight) * 0.5f);
        _hasPanelPositions = true;
    }

    private static Vector2 ClampPosition(Vector2 position, float width, float height)
    {
        return new Vector2(
            Math.Clamp(position.X, 8f, Math.Max(8f, GetScreenWidth() - width - 8f)),
            Math.Clamp(position.Y, 8f, Math.Max(8f, GetScreenHeight() - height - 8f)));
    }

    private static Rectangle GetSectionBounds(Rectangle panel, int index) =>
        new(panel.X + 22f, panel.Y + 100f + index * 72f, 230f, 58f);

    private static float SettingsContentWidth(Rectangle panel) =>
        panel.Width - 330f;

    private static Rectangle GetActionBounds(Rectangle panel, float offsetY) =>
        new(
            panel.X + panel.Width - 270f,
            panel.Y + offsetY,
            236f,
            42f);

    private static bool HitActionButton(Vector2 mouse, Rectangle panel, float offsetY) =>
        CheckCollisionPointRec(mouse, GetActionBounds(panel, offsetY));

    private static Rectangle GetHalfActionBounds(
        Rectangle panel,
        float offsetY,
        bool left)
    {
        float contentX = panel.X + 304f;
        float contentWidth = panel.Width - 344f;
        float halfWidth = (contentWidth - 12f) * 0.5f;
        float x = contentX + (left ? 0f : halfWidth + 12f);
        return new Rectangle(x, panel.Y + offsetY, halfWidth, 42f);
    }

    private static bool HitHalfActionButton(
        Vector2 mouse,
        Rectangle panel,
        float offsetY,
        bool left) =>
        CheckCollisionPointRec(mouse, GetHalfActionBounds(panel, offsetY, left));

    private static Rectangle GetSliderBounds(Rectangle panel, SliderId id)
    {
        float y = id switch
        {
            SliderId.CameraMove => panel.Y + 164f + 70f + 38f,
            SliderId.CameraRotation => panel.Y + 164f + 148f + 38f,
            SliderId.CameraPan => panel.Y + 164f + 226f + 38f,
            SliderId.CameraZoom => panel.Y + 164f + 304f + 38f,
            SliderId.CameraVertical => panel.Y + 164f + 382f + 38f,
            SliderId.PaletteRed => panel.Y + 164f + 278f + 38f,
            SliderId.PaletteGreen => panel.Y + 164f + 356f + 38f,
            SliderId.PaletteBlue => panel.Y + 164f + 434f + 38f,
            SliderId.RenderDistance => panel.Y + 164f + 70f + 38f,
            _ => panel.Y
        };

        return new Rectangle(panel.X + 304f, y, panel.Width - 344f, 10f);
    }

    private static bool HitSlider(
        Vector2 mouse,
        Rectangle panel,
        SliderId id,
        float unusedRowOffset)
    {
        return CheckCollisionPointRec(mouse, GetSliderHitBounds(panel, id));
    }

    private static Rectangle GetSliderHitBounds(Rectangle panel, SliderId id)
    {
        Rectangle slider = GetSliderBounds(panel, id);
        return new Rectangle(slider.X - 6f, slider.Y - 20f, slider.Width + 12f, 40f);
    }

    private static Rectangle GetColorSlotBounds(Rectangle panel, int index)
    {
        const float gap = 10f;
        float slotWidth = (SettingsContentWidth(panel) - gap * 2f) / 3f;
        int column = index % 3;
        int row = index / 3;
        return new Rectangle(
            panel.X + 304f + column * (slotWidth + gap),
            panel.Y + 164f + 88f + row * 48f,
            slotWidth,
            42f);
    }

    private static bool HitCloseButton(Vector2 mouse, Rectangle panel)
    {
        Rectangle bounds = new(
            panel.X + panel.Width - 48f,
            panel.Y + 14f,
            32f,
            30f);
        return CheckCollisionPointRec(mouse, bounds);
    }

    private static bool HitBackButton(Vector2 mouse, Rectangle panel)
    {
        Rectangle bounds = new(
            panel.X + 24f,
            panel.Y + panel.Height - 48f,
            126f,
            32f);
        return CheckCollisionPointRec(mouse, bounds);
    }

    private static bool HitMainButton(Vector2 mouse, Rectangle panel, float offsetY)
    {
        Rectangle bounds = new(
            panel.X + 28f,
            panel.Y + offsetY,
            panel.Width - 56f,
            42f);
        return CheckCollisionPointRec(mouse, bounds);
    }

    private static float GetSliderAmount(SliderId id, float value)
    {
        (float min, float max) = id switch
        {
            SliderId.CameraMove or SliderId.CameraRotation or
            SliderId.CameraPan or SliderId.CameraZoom or
            SliderId.CameraVertical => (0.25f, 4f),
            SliderId.RenderDistance => (500f, 18000f),
            _ => (0f, 255f)
        };

        return Math.Clamp((value - min) / (max - min), 0f, 1f);
    }

    private static string FormatSliderValue(SliderId id, float value) =>
        id switch
        {
            SliderId.RenderDistance => $"{value:0}",
            SliderId.PaletteRed or SliderId.PaletteGreen or SliderId.PaletteBlue =>
                $"{value:0}",
            _ => $"{value:0.00}"
        };

    private static float Lerp(float a, float b, float amount) =>
        a + (b - a) * amount;

remove dummy methods    private static int[] BuildFontCodepoints()
    {
        List<int> codepoints = new();

        for (int codepoint = 32; codepoint <= 126; codepoint++)
            codepoints.Add(codepoint);
        for (int codepoint = 0x410; codepoint <= 0x44F; codepoint++)
            codepoints.Add(codepoint);

        codepoints.Add(0x401);
        codepoints.Add(0x451);
        codepoints.Add(0x2014);
        codepoints.Add(0x2022);
        codepoints.Add(0x00D7);
        codepoints.Add(0x25BE);
        codepoints.Add(0x203A);

        return codepoints.ToArray();
    }

    private static Font LoadMenuFont(out bool ownsFont)
    {
        string fontsFolder = Environment.GetFolderPath(
            Environment.SpecialFolder.Fonts);
        string[] candidates = { "segoeui.ttf", "arial.ttf", "tahoma.ttf" };

        foreach (string candidate in candidates)
        {
            string path = Path.Combine(fontsFolder, candidate);
            if (!File.Exists(path))
                continue;

            try
            {
                ownsFont = true;
                return LoadFontEx(path, 24, FontCodepoints, FontCodepoints.Length);
            }
            catch
            {
                // Fall back to Raylib's built-in font.
            }
        }

        ownsFont = false;
        return GetFontDefault();
    }

    public void Dispose()
    {
        if (_ownsFont)
            UnloadFont(_font);
    }
}
