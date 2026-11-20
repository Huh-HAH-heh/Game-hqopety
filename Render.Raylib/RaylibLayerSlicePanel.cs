using System;
using System.Numerics;
using Core.Map;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// Small, collapsible right-side control for the active horizontal terrain
/// section. It changes only the render cut height; WorldMap data is untouched.
/// </summary>
internal sealed class RaylibLayerSlicePanel
{
    private const float LayerHeightMeters =
        WorldMap.LayerHeightUnits * 0.1f;

    private const float PanelWidth = 232f;
    private const float PanelHeight = 326f;

    private bool _isOpen;
    private bool _draggingSlider;
    private bool _settingsCommitRequested;

    public bool IsOpen => _isOpen;

    public void Update(
        RaylibViewerSettings settings,
        float maximumCutHeight)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settingsCommitRequested = false;

        if (IsKeyPressed(KeyboardKey.Tab))
        {
            if (_draggingSlider)
                _settingsCommitRequested = true;

            _isOpen = !_isOpen;
            _draggingSlider = false;
        }

        Vector2 mouse = GetMousePosition();

        if (_isOpen && CheckCollisionPointRec(mouse, GetSliderHitBounds()))
        {
            float wheel = GetMouseWheelMove();
            if (MathF.Abs(wheel) > 0.001f)
            {
                int maximumLayer = Math.Max(
                    0,
                    (int)MathF.Round(maximumCutHeight / LayerHeightMeters));
                int currentLayer = Math.Clamp(
                    (int)MathF.Round(settings.CutawayHeightMeters / LayerHeightMeters),
                    0,
                    maximumLayer);
                int wheelSteps = Math.Sign(wheel) *
                    Math.Max(1, (int)MathF.Round(MathF.Abs(wheel)));
                int nextLayer = Math.Clamp(currentLayer + wheelSteps, 0, maximumLayer);

                settings.CutawayEnabled = true;
                settings.CutawayHeightMeters = nextLayer * LayerHeightMeters;
                _settingsCommitRequested = true;
            }
        }

        if (IsMouseButtonPressed(MouseButton.Left))
        {
            if (CheckCollisionPointRec(mouse, GetToggleBounds()))
            {
                _isOpen = !_isOpen;
                _draggingSlider = false;
                return;
            }

            if (_isOpen && CheckCollisionPointRec(mouse, GetCutawayButtonBounds()))
            {
                settings.CutawayEnabled = !settings.CutawayEnabled;
                _settingsCommitRequested = true;
                return;
            }

            if (_isOpen && CheckCollisionPointRec(mouse, GetSliderHitBounds()))
            {
                _draggingSlider = true;
                ApplySlider(settings, mouse, maximumCutHeight);
            }
        }

        if (!_draggingSlider)
            return;

        if (IsMouseButtonDown(MouseButton.Left))
        {
            ApplySlider(settings, mouse, maximumCutHeight);
            return;
        }

        _draggingSlider = false;
        _settingsCommitRequested = true;
    }

    public void CancelInteraction()
    {
        if (!_draggingSlider)
            return;

        _draggingSlider = false;
        _settingsCommitRequested = true;
    }

    public bool ConsumeSettingsCommit()
    {
        bool requested = _settingsCommitRequested;
        _settingsCommitRequested = false;
        return requested;
    }

    public bool IsPointerOverPanel(Vector2 mouse)
    {
        return CheckCollisionPointRec(mouse, GetToggleBounds()) ||
            (_isOpen && CheckCollisionPointRec(mouse, GetPanelBounds()));
    }

    public void Draw(
        RaylibViewerSettings settings,
        float maximumCutHeight,
        Font font)
    {
        Vector2 mouse = GetMousePosition();
        if (_isOpen)
            DrawPanel(settings, maximumCutHeight, font, mouse);

        Rectangle toggle = GetToggleBounds();
        bool hovered = CheckCollisionPointRec(mouse, toggle);
        DrawRectangle(
            (int)toggle.X,
            (int)toggle.Y,
            (int)toggle.Width,
            (int)toggle.Height,
            _isOpen
                ? new Color(45, 57, 72, 235)
                : hovered
                    ? new Color(38, 48, 61, 220)
                    : new Color(20, 27, 36, 195));
        DrawRectangleLinesEx(toggle, 1f, new Color(104, 119, 137, 175));
        DrawCenteredText(
            font,
            _isOpen ? "СЛОИ  ×" : "СЛОИ  +",
            toggle,
            12f,
            new Color(224, 231, 239, 245));
    }

    private static void ApplySlider(
        RaylibViewerSettings settings,
        Vector2 mouse,
        float maximumCutHeight)
    {
        Rectangle track = GetSliderTrackBounds();
        float amount = Math.Clamp(
            1f - (mouse.Y - track.Y) / Math.Max(1f, track.Height),
            0f,
            1f);

        int maximumLayer = Math.Max(
            0,
            (int)MathF.Round(maximumCutHeight / LayerHeightMeters));
        int selectedLayer = (int)MathF.Round(amount * maximumLayer);
        float nextHeight = Math.Clamp(
            selectedLayer * LayerHeightMeters,
            0f,
            maximumCutHeight);

        settings.CutawayEnabled = true;
        settings.CutawayHeightMeters = nextHeight;
    }

    private void DrawPanel(
        RaylibViewerSettings settings,
        float maximumCutHeight,
        Font font,
        Vector2 mouse)
    {
        Rectangle panel = GetPanelBounds();
        DrawRectangle(
            (int)panel.X,
            (int)panel.Y,
            (int)panel.Width,
            (int)panel.Height,
            new Color(8, 12, 18, 218));
        DrawRectangleLinesEx(
            panel,
            1f,
            new Color(98, 113, 131, 185));

        DrawTextEx(
            font,
            "ГОРИЗОНТАЛЬНЫЙ СРЕЗ",
            new Vector2(panel.X + 14f, panel.Y + 14f),
            13f,
            0.4f,
            new Color(227, 234, 242, 245));

        Rectangle toggleCut = GetCutawayButtonBounds();
        bool toggleHovered = CheckCollisionPointRec(mouse, toggleCut);
        DrawRectangle(
            (int)toggleCut.X,
            (int)toggleCut.Y,
            (int)toggleCut.Width,
            (int)toggleCut.Height,
            settings.CutawayEnabled
                ? new Color(45, 61, 76, 230)
                : toggleHovered
                    ? new Color(35, 43, 54, 220)
                    : new Color(22, 29, 38, 205));
        DrawRectangleLinesEx(
            toggleCut,
            1f,
            new Color(87, 103, 121, 190));
        DrawCenteredText(
            font,
            settings.CutawayEnabled ? "СЕЧЕНИЕ ВКЛЮЧЕНО" : "СЕЧЕНИЕ ВЫКЛЮЧЕНО",
            toggleCut,
            11f,
            new Color(223, 230, 238, 245));

        Rectangle track = GetSliderTrackBounds();
        int maxLayer = Math.Max(
            0,
            (int)MathF.Round(maximumCutHeight / LayerHeightMeters));
        int currentLayer = Math.Clamp(
            (int)MathF.Round(settings.CutawayHeightMeters / LayerHeightMeters),
            0,
            maxLayer);
        float amount = maxLayer > 0
            ? currentLayer / (float)maxLayer
            : 0f;
        float thumbY = track.Y + track.Height * (1f - amount);

        DrawRectangle(
            (int)track.X,
            (int)track.Y,
            (int)track.Width,
            (int)track.Height,
            new Color(48, 58, 70, 235));

        float filledHeight = Math.Max(0f, track.Y + track.Height - thumbY);
        DrawRectangle(
            (int)track.X,
            (int)thumbY,
            (int)track.Width,
            (int)filledHeight,
            new Color(132, 151, 171, 225));

        DrawCircleV(
            new Vector2(track.X + track.Width * 0.5f, thumbY),
            8f,
            new Color(221, 229, 237, 250));
        DrawCircleV(
            new Vector2(track.X + track.Width * 0.5f, thumbY),
            3f,
            new Color(45, 56, 69, 255));

        DrawTextEx(
            font,
            $"{maximumCutHeight:0.0} м",
            new Vector2(panel.X + 58f, track.Y - 3f),
            11f,
            0.3f,
            new Color(157, 171, 188, 230));
        DrawTextEx(
            font,
            "0,0 м",
            new Vector2(panel.X + 58f, track.Y + track.Height - 10f),
            11f,
            0.3f,
            new Color(157, 171, 188, 230));

        DrawTextEx(
            font,
            $"{currentLayer * LayerHeightMeters:0.0} м",
            new Vector2(panel.X + 94f, panel.Y + 144f),
            22f,
            0.3f,
            new Color(239, 243, 248, 255));
        DrawTextEx(
            font,
            $"СЛОЙ {currentLayer} / {maxLayer}",
            new Vector2(panel.X + 94f, panel.Y + 176f),
            11f,
            0.3f,
            new Color(168, 181, 197, 235));

        DrawTextEx(
            font,
            "Колесо над ползунком: шаг 0,1 м.",
            new Vector2(panel.X + 14f, panel.Y + 282f),
            10f,
            0.25f,
            new Color(151, 164, 180, 225));
        DrawTextEx(
            font,
            "Объём выше плоскости скрывается.",
            new Vector2(panel.X + 14f, panel.Y + 299f),
            10f,
            0.25f,
            new Color(151, 164, 180, 225));
    }

    private static void DrawCenteredText(
        Font font,
        string text,
        Rectangle bounds,
        float fontSize,
        Color color)
    {
        Vector2 measured = MeasureTextEx(font, text, fontSize, 0.4f);
        DrawTextEx(
            font,
            text,
            new Vector2(
                MathF.Round(bounds.X + (bounds.Width - measured.X) * 0.5f),
                MathF.Round(bounds.Y + (bounds.Height - measured.Y) * 0.5f)),
            fontSize,
            0.4f,
            color);
    }

    private static Rectangle GetToggleBounds() =>
        new(
            Math.Max(8f, GetScreenWidth() - 72f),
            116f,
            58f,
            34f);

    private static Rectangle GetPanelBounds() =>
        new(
            Math.Max(8f, GetScreenWidth() - PanelWidth - 14f),
            Math.Clamp(
                (GetScreenHeight() - PanelHeight) * 0.5f,
                8f,
                Math.Max(8f, GetScreenHeight() - PanelHeight - 8f)),
            PanelWidth,
            PanelHeight);

    private static Rectangle GetCutawayButtonBounds()
    {
        Rectangle panel = GetPanelBounds();
        return new Rectangle(
            panel.X + 14f,
            panel.Y + 46f,
            panel.Width - 28f,
            32f);
    }

    private static Rectangle GetSliderTrackBounds()
    {
        Rectangle panel = GetPanelBounds();
        return new Rectangle(
            panel.X + 28f,
            panel.Y + 112f,
            8f,
            156f);
    }

    private static Rectangle GetSliderHitBounds()
    {
        Rectangle track = GetSliderTrackBounds();
        return new Rectangle(
            track.X - 14f,
            track.Y - 12f,
            track.Width + 28f,
            track.Height + 24f);
    }
}
