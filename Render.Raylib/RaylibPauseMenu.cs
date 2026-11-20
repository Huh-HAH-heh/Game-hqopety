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

    private static readonly (int Width, int Height)[] Resolutions =
    {
        (1280, 720),
        (1366, 768),
        (1600, 900),
        (1920, 1080)
    };

    private static readonly int[] FrameRateLimits = { 60, 90, 120, 144, 0 };
    private static readonly int[] FontCodepoints = BuildFontCodepoints();

    private const float MainWidth = 460f;
    private const float MainHeight = 340f;
    private const float SettingsWidth = 700f;
    private const float SettingsHeight = 640f;

    private readonly Font _font;
    private readonly bool _ownsFont;

    private MenuPage _page;
    private Vector2 _mainPosition;
    private Vector2 _settingsPosition;
    private Vector2 _dragOffset;
    private bool _hasPanelPositions;
    private bool _draggingMain;
    private bool _draggingSettings;

    private int _resolutionIndex = 2;
    private int _frameRateIndex = 3;
    private int _windowedWidth;
    private int _windowedHeight;

    public bool IsOpen => _page != MenuPage.Closed;
    public bool ShowHud { get; private set; } = true;
    public bool ShowControls { get; private set; } = true;
    public bool CutawayEnabled { get; set; }
    public float CameraSensitivity { get; private set; } = 1f;
    public int TargetFrameRate => FrameRateLimits[_frameRateIndex];

    public RaylibPauseMenu()
    {
        _windowedWidth = GetScreenWidth();
        _windowedHeight = GetScreenHeight();
        _resolutionIndex = FindResolutionIndex(_windowedWidth, _windowedHeight);

        _font = LoadMenuFont(out bool ownsFont);
        _ownsFont = ownsFont;

        SetTargetFPS(TargetFrameRate);
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

            _draggingMain = false;
            _draggingSettings = false;
            return RaylibMenuAction.None;
        }

        if (_page == MenuPage.Closed)
            return RaylibMenuAction.None;

        Vector2 mouse = GetMousePosition();

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
            new Color(0, 0, 0, 174));

        Vector2 mouse = GetMousePosition();
        Rectangle panel = GetPanelBounds();
        DrawPanel(panel);

        if (_page == MenuPage.Main)
            DrawMainMenu(panel, mouse);
        else
            DrawSettings(panel, mouse);
    }

    private RaylibMenuAction HandleMainClick(Vector2 mouse, Rectangle panel)
    {
        if (HitButton(mouse, panel, 104f, 42f))
        {
            _page = MenuPage.Closed;
            return RaylibMenuAction.None;
        }

        if (HitButton(mouse, panel, 162f, 42f))
        {
            _page = MenuPage.Settings;
            return RaylibMenuAction.None;
        }

        if (HitButton(mouse, panel, 220f, 42f))
            return RaylibMenuAction.Exit;

        return RaylibMenuAction.None;
    }

    private RaylibMenuAction HandleSettingsClick(Vector2 mouse, Rectangle panel)
    {
        if (HitButton(mouse, panel, 78f, 36f))
        {
            ToggleFullscreenMode();
            return RaylibMenuAction.None;
        }

        if (HitButton(mouse, panel, 120f, 36f))
        {
            CycleResolution();
            return RaylibMenuAction.None;
        }

        if (HitButton(mouse, panel, 162f, 36f))
        {
            _frameRateIndex = (_frameRateIndex + 1) % FrameRateLimits.Length;
            SetTargetFPS(TargetFrameRate);
            return RaylibMenuAction.None;
        }

        if (HitButton(mouse, panel, 204f, 36f))
        {
            ShowHud = !ShowHud;
            return RaylibMenuAction.None;
        }

        if (HitButton(mouse, panel, 246f, 36f))
        {
            ShowControls = !ShowControls;
            return RaylibMenuAction.None;
        }

        if (HitButton(mouse, panel, 288f, 36f))
        {
            CutawayEnabled = !CutawayEnabled;
            return RaylibMenuAction.ToggleCutaway;
        }

        Rectangle slider = GetSliderBounds(panel);
        if (CheckCollisionPointRec(mouse, slider))
        {
            float amount = Math.Clamp(
                (mouse.X - slider.X) / Math.Max(1f, slider.Width),
                0f,
                1f);
            CameraSensitivity = 0.5f + amount * 1.5f;
            return RaylibMenuAction.None;
        }

        if (HitButton(mouse, panel, 405f, 36f))
            return RaylibMenuAction.ResetCamera;

        if (HitButton(mouse, panel, 449f, 36f, leftHalf: true))
            return RaylibMenuAction.FocusLowerTunnel;

        if (HitButton(mouse, panel, 449f, 36f, rightHalf: true))
            return RaylibMenuAction.FocusUpperPassage;

        if (HitButton(mouse, panel, 493f, 36f))
            return ResetSettings();

        if (HitButton(mouse, panel, 545f, 38f))
        {
            _page = MenuPage.Main;
            return RaylibMenuAction.None;
        }

        if (HitCloseButton(mouse, panel))
        {
            _page = MenuPage.Main;
            return RaylibMenuAction.None;
        }

        return RaylibMenuAction.None;
    }

    private RaylibMenuAction ResetSettings()
    {
        if (IsWindowFullscreen())
        {
            ToggleFullscreen();
            SetWindowSize(_windowedWidth, _windowedHeight);
        }

        _resolutionIndex = FindResolutionIndex(1600, 900);
        _windowedWidth = 1600;
        _windowedHeight = 900;
        SetWindowSize(_windowedWidth, _windowedHeight);

        _frameRateIndex = 3;
        SetTargetFPS(TargetFrameRate);
        ShowHud = true;
        ShowControls = true;
        CutawayEnabled = false;
        CameraSensitivity = 1f;

        return RaylibMenuAction.ResetCamera;
    }

    private void ToggleFullscreenMode()
    {
        if (IsWindowFullscreen())
        {
            ToggleFullscreen();
            SetWindowSize(_windowedWidth, _windowedHeight);
            return;
        }

        _windowedWidth = GetScreenWidth();
        _windowedHeight = GetScreenHeight();
        ToggleFullscreen();
    }

    private void CycleResolution()
    {
        if (IsWindowFullscreen())
            ToggleFullscreenMode();

        int monitor = GetCurrentMonitor();
        int maxWidth = GetMonitorWidth(monitor);
        int maxHeight = GetMonitorHeight(monitor);

        for (int attempt = 0; attempt < Resolutions.Length; attempt++)
        {
            _resolutionIndex = (_resolutionIndex + 1) % Resolutions.Length;
            (int width, int height) = Resolutions[_resolutionIndex];

            if (width > maxWidth || height > maxHeight)
                continue;

            _windowedWidth = width;
            _windowedHeight = height;
            SetWindowSize(width, height);
            return;
        }

        SetWindowSize(_windowedWidth, _windowedHeight);
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
        DrawLabel("НАСТРОЙКИ", panel.X + 28f, panel.Y + 21f, 22f,
            new Color(241, 245, 250));
        DrawLabel("ГРАФИКА, КАМЕРА И УПРАВЛЕНИЕ", panel.X + 28f, panel.Y + 51f, 13f,
            new Color(154, 168, 185));
        DrawButton(panel, mouse, 78f, 36f,
            $"РЕЖИМ ЭКРАНА: {(IsWindowFullscreen() ? "ПОЛНЫЙ ЭКРАН" : "ОКНО")}");
        DrawButton(panel, mouse, 120f, 36f,
            $"РАЗРЕШЕНИЕ: {GetScreenWidth()} × {GetScreenHeight()}");
        DrawButton(panel, mouse, 162f, 36f,
            $"ЛИМИТ КАДРОВ: {(TargetFrameRate == 0 ? "БЕЗ ЛИМИТА" : $"{TargetFrameRate} FPS")}");
        DrawButton(panel, mouse, 204f, 36f,
            $"ИНФОПАНЕЛЬ: {(ShowHud ? "ВКЛ" : "ВЫКЛ")}", active: ShowHud);
        DrawButton(panel, mouse, 246f, 36f,
            $"ПОДСКАЗКИ: {(ShowControls ? "ВКЛ" : "ВЫКЛ")}", active: ShowControls);
        DrawButton(panel, mouse, 288f, 36f,
            $"СРЕЗ РЕЛЬЕФА: {(CutawayEnabled ? "ВКЛ" : "ВЫКЛ")}", active: CutawayEnabled);

        DrawLabel(
            $"ЧУВСТВИТЕЛЬНОСТЬ ПОВОРОТА КАМЕРЫ: {CameraSensitivity:0.00}×",
            panel.X + 28f,
            panel.Y + 340f,
            14f,
            new Color(211, 219, 228));

        Rectangle slider = GetSliderBounds(panel);
        DrawRectangle(
            (int)slider.X,
            (int)slider.Y,
            (int)slider.Width,
            (int)slider.Height,
            new Color(57, 67, 81));
        float amount = (CameraSensitivity - 0.5f) / 1.5f;
        DrawRectangle(
            (int)slider.X,
            (int)slider.Y,
            (int)(slider.Width * amount),
            (int)slider.Height,
            new Color(174, 185, 198));
        DrawCircleV(
            new Vector2(slider.X + slider.Width * amount, slider.Y + slider.Height * 0.5f),
            8f,
            new Color(242, 245, 249));

        DrawButton(panel, mouse, 405f, 36f, "СБРОСИТЬ КАМЕРУ");
        DrawButton(panel, mouse, 449f, 36f, "НИЖНИЙ ТОННЕЛЬ", leftHalf: true);
        DrawButton(panel, mouse, 449f, 36f, "ВЕРХНИЙ ПРОХОД", rightHalf: true);
        DrawButton(panel, mouse, 493f, 36f, "СБРОСИТЬ НАСТРОЙКИ");
        DrawButton(panel, mouse, 545f, 38f, "НАЗАД");

        DrawLabel(
            "WASD — перемещение  •  ПКМ — поворот  •  СКМ — панорама  •  Колесо — масштаб",
            panel.X + 28f,
            panel.Y + 594f,
            11f,
            new Color(145, 157, 173));

        DrawLabel(
            "Q/E — высота  •  F1/F2 — тоннели  •  C — срез  •  T — вид сверху  •  ESC — меню",
            panel.X + 28f,
            panel.Y + 612f,
            11f,
            new Color(145, 157, 173));

        DrawCloseButton(panel, mouse);
    }

    private void DrawPanel(Rectangle panel)
    {
        DrawRectangle(
            (int)panel.X,
            (int)panel.Y,
            (int)panel.Width,
            (int)panel.Height,
            new Color(16, 21, 29, 250));
        DrawRectangleLinesEx(panel, 1f, new Color(83, 96, 113, 255));
        DrawRectangle(
            (int)panel.X + 1,
            (int)panel.Y + 1,
            (int)panel.Width - 2,
            3,
            new Color(143, 153, 166, 255));
    }

    private void DrawButton(
        Rectangle panel,
        Vector2 mouse,
        float offsetY,
        float height,
        string text,
        bool active = false,
        bool leftHalf = false,
        bool rightHalf = false)
    {
        Rectangle bounds = GetButtonBounds(
            panel,
            offsetY,
            height,
            leftHalf,
            rightHalf);

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
        DrawCenteredLabel(text, bounds, 15f, new Color(241, 245, 250));
    }

    private void DrawCloseButton(Rectangle panel, Vector2 mouse)
    {
        Rectangle bounds = new(
            panel.X + panel.Width - 48f,
            panel.Y + 14f,
            32f,
            30f);
        bool hovered = CheckCollisionPointRec(mouse, bounds);

        DrawRectangle(
            (int)bounds.X,
            (int)bounds.Y,
            (int)bounds.Width,
            (int)bounds.Height,
            hovered ? new Color(62, 43, 46, 255) : new Color(35, 38, 45, 255));
        DrawRectangleLinesEx(bounds, 1f, new Color(83, 96, 113, 255));
        DrawCenteredLabel("×", bounds, 19f, Color.RayWhite);
    }

    private void DrawLabel(
        string text,
        float x,
        float y,
        float fontSize,
        Color color)
    {
        DrawTextEx(_font, text, new Vector2(x, y), fontSize, 1f, color);
    }

    private void DrawCenteredLabel(
        string text,
        Rectangle bounds,
        float fontSize,
        Color color)
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
        float width = main ? MainWidth : SettingsWidth;
        float height = main ? MainHeight : SettingsHeight;
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

    private static Rectangle GetButtonBounds(
        Rectangle panel,
        float offsetY,
        float height,
        bool leftHalf = false,
        bool rightHalf = false)
    {
        if (leftHalf)
            return new Rectangle(panel.X + 28f, panel.Y + offsetY, (panel.Width - 68f) * 0.5f, height);

        if (rightHalf)
            return new Rectangle(
                panel.X + panel.Width * 0.5f + 6f,
                panel.Y + offsetY,
                (panel.Width - 68f) * 0.5f,
                height);

        return new Rectangle(
            panel.X + 28f,
            panel.Y + offsetY,
            panel.Width - 56f,
            height);
    }

    private static bool HitButton(
        Vector2 mouse,
        Rectangle panel,
        float offsetY,
        float height,
        bool leftHalf = false,
        bool rightHalf = false)
    {
        return CheckCollisionPointRec(
            mouse,
            GetButtonBounds(panel, offsetY, height, leftHalf, rightHalf));
    }

    private static Rectangle GetSliderBounds(Rectangle panel) =>
        new(panel.X + 28f, panel.Y + 372f, panel.Width - 56f, 12f);

    private static bool HitCloseButton(Vector2 mouse, Rectangle panel)
    {
        Rectangle bounds = new(
            panel.X + panel.Width - 48f,
            panel.Y + 14f,
            32f,
            30f);
        return CheckCollisionPointRec(mouse, bounds);
    }

    private static int FindResolutionIndex(int width, int height)
    {
        for (int i = 0; i < Resolutions.Length; i++)
        {
            if (Resolutions[i].Width == width &&
                Resolutions[i].Height == height)
            {
                return i;
            }
        }

        return 2;
    }

    private static int[] BuildFontCodepoints()
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
                return LoadFontEx(
                    path,
                    24,
                    FontCodepoints,
                    FontCodepoints.Length);
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
