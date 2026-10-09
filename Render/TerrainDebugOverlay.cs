using System;
using System.Collections.Generic;
using System.IO;
using Core.Map;
using SFML.Graphics;
using SFML.System;
using SFML.Window;

namespace RimClone.Render;

public enum TerrainOverlayActionType
{
    ToggleWindow,
    CloseWindow,
    SetBaseGray,
    SetHeightContrast,
    SetDepthShade,
    SetLayerOffset,
    SetLayer,
    ToggleGrid,
    ToggleExtrema,
    ResetSettings
}

public readonly record struct TerrainOverlayAction(
    TerrainOverlayActionType Type,
    float Value = 0f,
    int Layer = 0);

public readonly record struct TerrainSettingsSnapshot(
    int VisibleMaxLayer,
    bool ShowGrid,
    bool ShowExtrema,
    float BaseGray,
    float HeightContrast,
    float DepthShade,
    float LayerOffset);

public sealed class TerrainDebugOverlay : IDisposable
{
    private const float ToolbarX = 14f;
    private const float ToolbarY = 12f;
    private const float ToolbarWidth = 218f;
    private const float ToolbarHeight = 30f;

    private const float PanelX = 14f;
    private const float PanelY = 50f;
    private const float PanelWidth = 392f;
    private const float PanelHeight = 548f;

    private readonly Font? _font;
    private readonly List<Text> _texts = new();
    private readonly List<SliderControl> _sliders = new();

    private readonly RectangleShape _toolbar = new RectangleShape(
        new Vector2f(ToolbarWidth, ToolbarHeight))
    {
        Position = new Vector2f(ToolbarX, ToolbarY),
        FillColor = new Color(20, 28, 38, 245),
        OutlineColor = new Color(78, 94, 112, 245),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _panel = new RectangleShape(
        new Vector2f(PanelWidth, PanelHeight))
    {
        Position = new Vector2f(PanelX, PanelY),
        FillColor = new Color(15, 20, 28, 250),
        OutlineColor = new Color(72, 86, 103, 255),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _accent = new RectangleShape(
        new Vector2f(PanelWidth - 2f, 3f))
    {
        Position = new Vector2f(PanelX + 1f, PanelY + 1f),
        FillColor = new Color(145, 155, 167)
    };

    private readonly RectangleShape _closeButton = new RectangleShape(
        new Vector2f(28f, 26f))
    {
        Position = new Vector2f(PanelX + PanelWidth - 40f, PanelY + 10f),
        FillColor = new Color(32, 40, 51),
        OutlineColor = new Color(83, 98, 116),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _gridButton = new RectangleShape(
        new Vector2f(160f, 30f))
    {
        Position = new Vector2f(PanelX + 16f, PanelY + 284f),
        FillColor = new Color(29, 36, 46),
        OutlineColor = new Color(75, 90, 107),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _extremaButton = new RectangleShape(
        new Vector2f(178f, 30f))
    {
        Position = new Vector2f(PanelX + 190f, PanelY + 284f),
        FillColor = new Color(29, 36, 46),
        OutlineColor = new Color(75, 90, 107),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _resetButton = new RectangleShape(
        new Vector2f(145f, 30f))
    {
        Position = new Vector2f(PanelX + 16f, PanelY + 507f),
        FillColor = new Color(35, 41, 50),
        OutlineColor = new Color(88, 99, 112),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _doneButton = new RectangleShape(
        new Vector2f(100f, 30f))
    {
        Position = new Vector2f(PanelX + PanelWidth - 116f, PanelY + 507f),
        FillColor = new Color(52, 62, 74),
        OutlineColor = new Color(112, 127, 143),
        OutlineThickness = 1f
    };

    private readonly RectangleShape[] _previewSwatches =
        new RectangleShape[5];

    private readonly CircleShape _minimumPin = new CircleShape(4f, 20)
    {
        FillColor = new Color(225, 229, 235),
        OutlineColor = new Color(20, 24, 30),
        OutlineThickness = 1.5f
    };

    private readonly CircleShape _maximumPin = new CircleShape(4f, 20)
    {
        FillColor = new Color(38, 42, 48),
        OutlineColor = new Color(235, 239, 245),
        OutlineThickness = 1.5f
    };

    private readonly Text? _toolbarLabel;
    private readonly Text? _title;
    private readonly Text? _subtitle;
    private readonly Text? _closeLabel;
    private readonly Text? _gridLabel;
    private readonly Text? _extremaLabel;
    private readonly Text? _previewTitle;
    private readonly Text? _previewLowLabel;
    private readonly Text? _previewHighLabel;
    private readonly Text? _minimumText;
    private readonly Text? _maximumText;
    private readonly Text? _cursorText;
    private readonly Text? _resetLabel;
    private readonly Text? _doneLabel;
    private readonly Text? _helpText;

    private long _cachedTerrainVersion = long.MinValue;
    private ushort _minimumHeightUnits;
    private ushort _maximumHeightUnits;
    private int _minimumX;
    private int _minimumY;
    private int _maximumX;
    private int _maximumY;

    private int _lastMouseCellX = int.MinValue;
    private int _lastMouseCellY = int.MinValue;
    private long _lastMouseTerrainVersion = long.MinValue;
    private string _lastMinimum = string.Empty;
    private string _lastMaximum = string.Empty;
    private string _lastCursor = string.Empty;

    public TerrainDebugOverlay()
    {
        _font = LoadFont();

        for (int i = 0; i < _previewSwatches.Length; i++)
        {
            _previewSwatches[i] = new RectangleShape(
                new Vector2f(65f, 12f))
            {
                Position = new Vector2f(PanelX + 18f + i * 70f, PanelY + 346f),
                OutlineColor = new Color(122, 132, 145),
                OutlineThickness = 1f
            };
        }

        _sliders.Add(new SliderControl(
            TerrainOverlayActionType.SetBaseGray,
            "Базовый тон грунта",
            8f,
            96f,
            PanelX + 18f,
            PanelY + 82f,
            PanelWidth - 36f));

        _sliders.Add(new SliderControl(
            TerrainOverlayActionType.SetHeightContrast,
            "Разница тона по высоте",
            0f,
            32f,
            PanelX + 18f,
            PanelY + 126f,
            PanelWidth - 36f));

        _sliders.Add(new SliderControl(
            TerrainOverlayActionType.SetDepthShade,
            "Затемнение нижних слоёв",
            0f,
            50f,
            PanelX + 18f,
            PanelY + 170f,
            PanelWidth - 36f));

        _sliders.Add(new SliderControl(
            TerrainOverlayActionType.SetLayerOffset,
            "Расстояние между слоями",
            0f,
            3f,
            PanelX + 18f,
            PanelY + 214f,
            PanelWidth - 36f));

        _sliders.Add(new SliderControl(
            TerrainOverlayActionType.SetLayer,
            "Верхняя граница среза Z",
            0f,
            49f,
            PanelX + 70f,
            PanelY + 258f,
            PanelWidth - 140f));

        if (_font == null)
        {
            Console.WriteLine(
                "[TerrainSettings] System font unavailable; settings can still be changed by clicking the controls.");
            return;
        }

        _toolbarLabel = CreateText(
            "НАСТРОЙКИ РЕЛЬЕФА   F2",
            12,
            new Color(237, 241, 246),
            new Vector2f(ToolbarX + 13f, ToolbarY + 7f),
            Text.Styles.Bold);

        _title = CreateText(
            "ОТОБРАЖЕНИЕ ТЕРРЕНА",
            16,
            new Color(240, 243, 248),
            new Vector2f(PanelX + 16f, PanelY + 11f),
            Text.Styles.Bold);

        _subtitle = CreateText(
            "Монохромный материал • перепады — тоном",
            11,
            new Color(155, 166, 180),
            new Vector2f(PanelX + 16f, PanelY + 34f));

        _closeLabel = CreateText(
            "×",
            19,
            Color.White,
            new Vector2f(PanelX + PanelWidth - 33f, PanelY + 9f));

        _gridLabel = CreateText(
            "СЕТКА",
            11,
            Color.White,
            new Vector2f(PanelX + 57f, PanelY + 292f),
            Text.Styles.Bold);

        _extremaLabel = CreateText(
            "КРАЙНИЕ ТОЧКИ",
            11,
            Color.White,
            new Vector2f(PanelX + 217f, PanelY + 292f),
            Text.Styles.Bold);

        _previewTitle = CreateText(
            "ТОН ОТ НИЗИНЫ К ВЫСОТЕ",
            11,
            new Color(195, 204, 215),
            new Vector2f(PanelX + 16f, PanelY + 325f),
            Text.Styles.Bold);

        _previewLowLabel = CreateText(
            "НИЗИНА",
            10,
            new Color(161, 171, 184),
            new Vector2f(PanelX + 17f, PanelY + 363f));

        _previewHighLabel = CreateText(
            "ВЫСОТА",
            10,
            new Color(205, 212, 221),
            new Vector2f(PanelX + PanelWidth - 75f, PanelY + 363f));

        _minimumText = CreateText(
            "",
            11,
            new Color(204, 213, 224),
            new Vector2f(PanelX + 16f, PanelY + 390f));

        _maximumText = CreateText(
            "",
            11,
            new Color(204, 213, 224),
            new Vector2f(PanelX + 16f, PanelY + 408f));

        _cursorText = CreateText(
            "",
            11,
            new Color(240, 243, 247),
            new Vector2f(PanelX + 16f, PanelY + 432f));

        _helpText = CreateText(
            "Срез показывает слои до Z. Колесо — масштаб, WASD — камера.",
            10,
            new Color(145, 157, 173),
            new Vector2f(PanelX + 16f, PanelY + 474f));

        _resetLabel = CreateText(
            "СБРОСИТЬ",
            11,
            Color.White,
            new Vector2f(PanelX + 42f, PanelY + 515f),
            Text.Styles.Bold);

        _doneLabel = CreateText(
            "ГОТОВО",
            11,
            Color.White,
            new Vector2f(PanelX + PanelWidth - 91f, PanelY + 515f),
            Text.Styles.Bold);
    }

    public bool IsSliderHit(
        Vector2i point,
        out TerrainOverlayActionType sliderType)
    {
        for (int i = 0; i < _sliders.Count; i++)
        {
            if (!_sliders[i].HitTest(point))
                continue;

            sliderType = _sliders[i].ActionType;
            return true;
        }

        sliderType = default;
        return false;
    }

    public TerrainOverlayAction GetSliderAction(
        TerrainOverlayActionType sliderType,
        int screenX,
        int layerCount)
    {
        for (int i = 0; i < _sliders.Count; i++)
        {
            SliderControl slider = _sliders[i];

            if (slider.ActionType != sliderType)
                continue;

            float maximum = slider.MaxValue;

            if (sliderType == TerrainOverlayActionType.SetLayer)
                maximum = Math.Max(0, layerCount - 1);

            float value = slider.ValueFromX(screenX, maximum);

            if (sliderType == TerrainOverlayActionType.SetLayer)
            {
                return new TerrainOverlayAction(
                    sliderType,
                    Layer: (int)MathF.Round(value));
            }

            return new TerrainOverlayAction(
                sliderType,
                Value: value);
        }

        return new TerrainOverlayAction(sliderType);
    }

    public TerrainOverlayAction? HandleClick(
        Vector2i point,
        bool settingsOpen,
        int visibleMaxLayer,
        int layerCount)
    {
        if (_toolbar.GetGlobalBounds().Contains(point))
            return new TerrainOverlayAction(TerrainOverlayActionType.ToggleWindow);

        if (!settingsOpen)
            return null;

        if (_closeButton.GetGlobalBounds().Contains(point) ||
            _doneButton.GetGlobalBounds().Contains(point))
        {
            return new TerrainOverlayAction(TerrainOverlayActionType.CloseWindow);
        }

        if (_resetButton.GetGlobalBounds().Contains(point))
            return new TerrainOverlayAction(TerrainOverlayActionType.ResetSettings);

        if (_gridButton.GetGlobalBounds().Contains(point))
            return new TerrainOverlayAction(TerrainOverlayActionType.ToggleGrid);

        if (_extremaButton.GetGlobalBounds().Contains(point))
            return new TerrainOverlayAction(TerrainOverlayActionType.ToggleExtrema);

        if (IsSliderHit(point, out TerrainOverlayActionType sliderType))
        {
            return GetSliderAction(
                sliderType,
                point.X,
                layerCount);
        }

        return null;
    }

    public void DrawWorldMarkers(
        RenderWindow window,
        WorldMap worldMap,
        float tilePixelSize,
        float zoomLevel,
        bool showExtrema)
    {
        if (!showExtrema)
            return;

        RefreshExtremes(worldMap);

        float radius = Math.Clamp(4f * zoomLevel, 0.75f, 12f);
        _minimumPin.Radius = radius;
        _maximumPin.Radius = radius;
        _minimumPin.Origin = new Vector2f(radius, radius);
        _maximumPin.Origin = new Vector2f(radius, radius);

        _minimumPin.Position = new Vector2f(
            (_minimumX + 0.5f) * tilePixelSize,
            (_minimumY + 0.5f) * tilePixelSize);

        _maximumPin.Position = new Vector2f(
            (_maximumX + 0.5f) * tilePixelSize,
            (_maximumY + 0.5f) * tilePixelSize);

        window.Draw(_minimumPin);
        window.Draw(_maximumPin);
    }

    public void DrawScreen(
        RenderWindow window,
        WorldMap worldMap,
        View cameraView,
        float tilePixelSize,
        TerrainSettingsSnapshot settings,
        bool settingsOpen,
        Vector2i uiMousePosition)
    {
        window.Draw(_toolbar);

        if (_font != null && _toolbarLabel != null)
            window.Draw(_toolbarLabel);

        if (!settingsOpen)
            return;

        RefreshExtremes(worldMap);
        RefreshStyle(settings, worldMap.LayerCount, uiMousePosition);

        window.Draw(_panel);
        window.Draw(_accent);
        window.Draw(_closeButton);
        window.Draw(_gridButton);
        window.Draw(_extremaButton);
        window.Draw(_resetButton);
        window.Draw(_doneButton);

        for (int i = 0; i < _sliders.Count; i++)
        {
            float current = _sliders[i].ActionType switch
            {
                TerrainOverlayActionType.SetBaseGray => settings.BaseGray,
                TerrainOverlayActionType.SetHeightContrast => settings.HeightContrast,
                TerrainOverlayActionType.SetDepthShade => settings.DepthShade,
                TerrainOverlayActionType.SetLayerOffset => settings.LayerOffset,
                TerrainOverlayActionType.SetLayer => settings.VisibleMaxLayer,
                _ => 0f
            };

            float maximum = _sliders[i].MaxValue;
            if (_sliders[i].ActionType == TerrainOverlayActionType.SetLayer)
                maximum = Math.Max(0, worldMap.LayerCount - 1);

            _sliders[i].Draw(window, current, maximum);
        }

        for (int i = 0; i < _previewSwatches.Length; i++)
        {
            _previewSwatches[i].FillColor =
                TerrainHeightPalette.GetHeightPreviewColor(
                    i,
                    settings.BaseGray,
                    settings.HeightContrast);
            window.Draw(_previewSwatches[i]);
        }

        if (_font == null)
            return;

        for (int i = 0; i < _texts.Count; i++)
            window.Draw(_texts[i]);

        UpdateValueTexts(settings, worldMap.LayerCount);
        UpdateCursorText(window, worldMap, cameraView, tilePixelSize, uiMousePosition);
    }

    private void RefreshStyle(
        TerrainSettingsSnapshot settings,
        int layerCount,
        Vector2i mouse)
    {
        bool toolbarHovered = _toolbar.GetGlobalBounds().Contains(mouse);
        _toolbar.FillColor = toolbarHovered
            ? new Color(34, 45, 59, 250)
            : new Color(20, 28, 38, 245);

        bool closeHovered = _closeButton.GetGlobalBounds().Contains(mouse);
        _closeButton.FillColor = closeHovered
            ? new Color(74, 46, 49)
            : new Color(32, 40, 51);

        _gridButton.FillColor = settings.ShowGrid
            ? new Color(62, 73, 87)
            : new Color(29, 36, 46);
        _gridButton.OutlineColor = settings.ShowGrid
            ? new Color(165, 177, 190)
            : new Color(75, 90, 107);

        _extremaButton.FillColor = settings.ShowExtrema
            ? new Color(62, 73, 87)
            : new Color(29, 36, 46);
        _extremaButton.OutlineColor = settings.ShowExtrema
            ? new Color(165, 177, 190)
            : new Color(75, 90, 107);

        _resetButton.FillColor =
            _resetButton.GetGlobalBounds().Contains(mouse)
                ? new Color(54, 62, 73)
                : new Color(35, 41, 50);

        _doneButton.FillColor =
            _doneButton.GetGlobalBounds().Contains(mouse)
                ? new Color(74, 87, 103)
                : new Color(52, 62, 74);
    }

    private void UpdateValueTexts(
        TerrainSettingsSnapshot settings,
        int layerCount)
    {
        for (int i = 0; i < _sliders.Count; i++)
        {
            float current = _sliders[i].ActionType switch
            {
                TerrainOverlayActionType.SetBaseGray => settings.BaseGray,
                TerrainOverlayActionType.SetHeightContrast => settings.HeightContrast,
                TerrainOverlayActionType.SetDepthShade => settings.DepthShade,
                TerrainOverlayActionType.SetLayerOffset => settings.LayerOffset,
                TerrainOverlayActionType.SetLayer => settings.VisibleMaxLayer,
                _ => 0f
            };

            _sliders[i].UpdateValue(current, layerCount);
        }

        if (_minimumHeightUnits == 0)
            return;

        SetText(
            _minimumText,
            ref _lastMinimum,
            $"НИЗИНА  {_minimumHeightUnits * 0.1f:0.#} м    X={_minimumX}, Y={_minimumY}");

        SetText(
            _maximumText,
            ref _lastMaximum,
            $"ВЕРШИНА  {_maximumHeightUnits * 0.1f:0.#} м    X={_maximumX}, Y={_maximumY}");
    }

    private void UpdateCursorText(
        RenderWindow window,
        WorldMap worldMap,
        View cameraView,
        float tilePixelSize,
        Vector2i uiMousePosition)
    {
        if (_cursorText == null)
            return;

        Vector2i mouse = Mouse.GetPosition(window);
        Vector2u windowSize = window.Size;

        if (_panel.GetGlobalBounds().Contains(uiMousePosition) ||
            windowSize.X == 0 || windowSize.Y == 0 ||
            mouse.X < 0 || mouse.Y < 0 ||
            mouse.X >= windowSize.X || mouse.Y >= windowSize.Y)
        {
            SetText(
                _cursorText,
                ref _lastCursor,
                "КУРСОР  —  наведите на карту для высоты");
            _lastMouseCellX = int.MinValue;
            _lastMouseCellY = int.MinValue;
            return;
        }

        Vector2f worldPoint =
            window.MapPixelToCoords(mouse, cameraView);

        int x = (int)MathF.Floor(worldPoint.X / tilePixelSize);
        int y = (int)MathF.Floor(worldPoint.Y / tilePixelSize);

        if (x == _lastMouseCellX &&
            y == _lastMouseCellY &&
            _lastMouseTerrainVersion == worldMap.TerrainVersion)
        {
            return;
        }

        _lastMouseCellX = x;
        _lastMouseCellY = y;
        _lastMouseTerrainVersion = worldMap.TerrainVersion;

        if (x < 0 || y < 0 || x >= worldMap.TileWidth || y >= worldMap.TileHeight)
        {
            SetText(
                _cursorText,
                ref _lastCursor,
                "КУРСОР  —  вне карты");
            return;
        }

        float height = worldMap.GetSurfaceHeightUnits(x, y) * 0.1f;
        SetText(
            _cursorText,
            ref _lastCursor,
            $"КУРСОР  X={x}, Y={y}   ВЫСОТА {height:0.#} м");
    }

    private void RefreshExtremes(WorldMap worldMap)
    {
        if (_cachedTerrainVersion == worldMap.TerrainVersion)
            return;

        _minimumHeightUnits = ushort.MaxValue;
        _maximumHeightUnits = 0;

        long centerX = worldMap.TileWidth / 2;
        long centerY = worldMap.TileHeight / 2;
        long nearestMinimumDistance = long.MaxValue;
        long nearestMaximumDistance = long.MaxValue;

        for (int y = 0; y < worldMap.TileHeight; y++)
        {
            for (int x = 0; x < worldMap.TileWidth; x++)
            {
                ushort height = worldMap.GetSurfaceHeightUnits(x, y);
                if (height == 0)
                    continue;

                long dx = x - centerX;
                long dy = y - centerY;
                long distance = dx * dx + dy * dy;

                if (height < _minimumHeightUnits ||
                    (height == _minimumHeightUnits &&
                     distance < nearestMinimumDistance))
                {
                    _minimumHeightUnits = height;
                    _minimumX = x;
                    _minimumY = y;
                    nearestMinimumDistance = distance;
                }

                if (height > _maximumHeightUnits ||
                    (height == _maximumHeightUnits &&
                     distance < nearestMaximumDistance))
                {
                    _maximumHeightUnits = height;
                    _maximumX = x;
                    _maximumY = y;
                    nearestMaximumDistance = distance;
                }
            }
        }

        if (_minimumHeightUnits == ushort.MaxValue)
            _minimumHeightUnits = 0;

        _cachedTerrainVersion = worldMap.TerrainVersion;
    }

    private Text CreateText(
        string value,
        uint characterSize,
        Color color,
        Vector2f position,
        Text.Styles style = Text.Styles.Regular)
    {
        Text text = new Text(_font!, value, characterSize)
        {
            Position = position,
            FillColor = color,
            OutlineColor = new Color(0, 0, 0, 180),
            OutlineThickness = 0.5f,
            Style = style
        };

        _texts.Add(text);
        return text;
    }

    private static void SetText(
        Text? text,
        ref string previous,
        string value)
    {
        if (text == null || previous == value)
            return;

        text.DisplayedString = value;
        previous = value;
    }

    private static Font? LoadFont()
    {
        string fontsDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        string[] candidates = { "segoeui.ttf", "arial.ttf", "tahoma.ttf" };

        for (int i = 0; i < candidates.Length; i++)
        {
            string path = Path.Combine(fontsDirectory, candidates[i]);
            if (!File.Exists(path))
                continue;

            try
            {
                return new Font(path);
            }
            catch
            {
                // Try the next installed system font.
            }
        }

        return null;
    }

    public void Dispose()
    {
        for (int i = 0; i < _texts.Count; i++)
            _texts[i].Dispose();

        for (int i = 0; i < _sliders.Count; i++)
            _sliders[i].Dispose();

        _font?.Dispose();
        _toolbar.Dispose();
        _panel.Dispose();
        _accent.Dispose();
        _closeButton.Dispose();
        _gridButton.Dispose();
        _extremaButton.Dispose();
        _resetButton.Dispose();
        _doneButton.Dispose();
        _minimumPin.Dispose();
        _maximumPin.Dispose();

        for (int i = 0; i < _previewSwatches.Length; i++)
            _previewSwatches[i].Dispose();
    }

    private sealed class SliderControl : IDisposable
    {
        private readonly RectangleShape _track;
        private readonly RectangleShape _fill;
        private readonly CircleShape _knob;
        private readonly Text? _label;
        private readonly Text? _value;

        private readonly float _startX;
        private readonly float _width;
        private readonly float _trackY;

        public TerrainOverlayActionType ActionType { get; }
        public float MinValue { get; }
        public float MaxValue { get; }

        public SliderControl(
            TerrainOverlayActionType actionType,
            string label,
            float minValue,
            float maxValue,
            float startX,
            float trackY,
            float width)
        {
            ActionType = actionType;
            MinValue = minValue;
            MaxValue = maxValue;
            _startX = startX;
            _trackY = trackY;
            _width = width;

            _track = new RectangleShape(new Vector2f(width, 4f))
            {
                Position = new Vector2f(startX, trackY),
                FillColor = new Color(67, 76, 88)
            };

            _fill = new RectangleShape(new Vector2f(0f, 4f))
            {
                Position = new Vector2f(startX, trackY),
                FillColor = new Color(180, 188, 198)
            };

            _knob = new CircleShape(6f, 24)
            {
                Origin = new Vector2f(6f, 6f),
                FillColor = new Color(241, 244, 248),
                OutlineColor = new Color(107, 121, 137),
                OutlineThickness = 1.5f
            };

            _label = LoadFontForControl() is Font font
                ? new Text(font, label, 12)
                {
                    Position = new Vector2f(PanelX + 18f, trackY - PanelY + PanelY - 21f),
                    FillColor = new Color(209, 217, 226)
                }
                : null;

            // Value is rendered at the right end of the label row.
            _value = _label != null
                ? new Text(_label.Font, "", 12)
                {
                    Position = new Vector2f(PanelX + PanelWidth - 98f, trackY - 21f),
                    FillColor = new Color(232, 237, 243),
                    Style = Text.Styles.Bold
                }
                : null;

            if (_label != null)
                SharedTextRegistry.Add(_label);

            if (_value != null)
                SharedTextRegistry.Add(_value);
        }

        public bool HitTest(Vector2i point)
        {
            FloatRect bounds = new FloatRect(
                new Vector2f(_startX - 5f, _trackY - 13f),
                new Vector2f(_width + 10f, 31f));

            return bounds.Contains(point);
        }

        public float ValueFromX(int screenX, float maximum)
        {
            if (maximum <= MinValue)
                return MinValue;

            float amount = Math.Clamp(
                (screenX - _startX) / _width,
                0f,
                1f);

            return MinValue + (maximum - MinValue) * amount;
        }

        public void Draw(RenderWindow window, float current, float maximum)
        {
            float range = Math.Max(0.0001f, maximum - MinValue);
            float amount = Math.Clamp((current - MinValue) / range, 0f, 1f);
            _fill.Size = new Vector2f(amount * _width, 4f);
            _knob.Position = new Vector2f(
                _startX + amount * _width,
                _trackY + 2f);

            window.Draw(_track);
            window.Draw(_fill);
            window.Draw(_knob);
        }

        public void UpdateValue(float current, int layerCount)
        {
            if (_value == null)
                return;

            string text = ActionType switch
            {
                TerrainOverlayActionType.SetBaseGray => $"{current:0} / 96",
                TerrainOverlayActionType.SetHeightContrast => $"{current:0} тонов",
                TerrainOverlayActionType.SetDepthShade => $"{current:0} тонов",
                TerrainOverlayActionType.SetLayerOffset => $"{current:0.0} px/Z",
                TerrainOverlayActionType.SetLayer => $"{(int)current + 1} / {layerCount} м",
                _ => current.ToString("0.0")
            };

            _value.DisplayedString = text;
        }

        public void Dispose()
        {
            _track.Dispose();
            _fill.Dispose();
            _knob.Dispose();
            // Slider texts are disposed by the overlay's shared text list.
        }

        private static Font? LoadFontForControl()
        {
            return SharedTextRegistry.Font;
        }
    }

    private static class SharedTextRegistry
    {
        public static Font? Font { get; set; }
        private static readonly List<Text> Items = new();

        public static void Add(Text text) => Items.Add(text);

        public static void Dispose()
        {
            for (int i = 0; i < Items.Count; i++)
                Items[i].Dispose();

            Items.Clear();
        }
    }
}
