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
    SelectHeightMap,
    SelectVolumeSlice,
    SetLayer
}

public readonly record struct TerrainOverlayAction(
    TerrainOverlayActionType Type,
    int Layer = 0);

public sealed class TerrainDebugOverlay : IDisposable
{
    private const float PanelX = 16f;
    private const float PanelY = 16f;
    private const float PanelWidth = 500f;
    private const float PanelHeight = 294f;

    private const float SliderStartX = 82f;
    private const float SliderWidth = 340f;
    private const float SliderY = 130f;
    private const int LegendBands = 5;

    private readonly Font? _font;
    private readonly List<Text> _texts = new();

    private readonly RectangleShape _panel =
        new RectangleShape(new Vector2f(PanelWidth, PanelHeight))
        {
            Position = new Vector2f(PanelX, PanelY),
            FillColor = new Color(13, 19, 28, 246),
            OutlineColor = new Color(56, 76, 98, 245),
            OutlineThickness = 1f
        };

    private readonly RectangleShape _accent =
        new RectangleShape(new Vector2f(PanelWidth - 2f, 3f))
        {
            Position = new Vector2f(PanelX + 1f, PanelY + 1f),
            FillColor = new Color(67, 145, 255)
        };

    private readonly RectangleShape _heightModeButton =
        new RectangleShape(new Vector2f(220f, 34f))
        {
            Position = new Vector2f(PanelX + 14f, PanelY + 38f),
            OutlineThickness = 1f
        };

    private readonly RectangleShape _sliceModeButton =
        new RectangleShape(new Vector2f(232f, 34f))
        {
            Position = new Vector2f(PanelX + 242f, PanelY + 38f),
            OutlineThickness = 1f
        };

    private readonly RectangleShape _minusButton =
        new RectangleShape(new Vector2f(36f, 30f))
        {
            Position = new Vector2f(PanelX + 14f, PanelY + 100f),
            FillColor = new Color(31, 43, 57),
            OutlineColor = new Color(76, 96, 118),
            OutlineThickness = 1f
        };

    private readonly RectangleShape _plusButton =
        new RectangleShape(new Vector2f(36f, 30f))
        {
            Position = new Vector2f(PanelX + 418f, PanelY + 100f),
            FillColor = new Color(31, 43, 57),
            OutlineColor = new Color(76, 96, 118),
            OutlineThickness = 1f
        };

    private readonly RectangleShape _sliderTrack =
        new RectangleShape(new Vector2f(SliderWidth, 4f))
        {
            Position = new Vector2f(PanelX + SliderStartX, PanelY + SliderY),
            FillColor = new Color(65, 78, 93)
        };

    private readonly RectangleShape _sliderProgress =
        new RectangleShape(new Vector2f(0f, 4f))
        {
            Position = new Vector2f(PanelX + SliderStartX, PanelY + SliderY),
            FillColor = new Color(67, 145, 255)
        };

    private readonly CircleShape _sliderKnob = new CircleShape(7f, 24)
    {
        Origin = new Vector2f(7f, 7f),
        FillColor = new Color(238, 246, 255),
        OutlineColor = new Color(67, 145, 255),
        OutlineThickness = 2f
    };

    private readonly RectangleShape[] _legendSwatches =
        new RectangleShape[LegendBands];

    private readonly Text? _titleText;
    private readonly Text? _subtitleText;
    private readonly Text? _heightModeLabel;
    private readonly Text? _sliceModeLabel;
    private readonly Text? _layerLabel;
    private readonly Text? _minusLabel;
    private readonly Text? _plusLabel;
    private readonly Text? _layerMinTick;
    private readonly Text? _layerMidTick;
    private readonly Text? _layerMaxTick;
    private readonly Text? _layerValue;
    private readonly Text? _modeDescription;
    private readonly Text? _minimumText;
    private readonly Text? _maximumText;
    private readonly Text? _cursorText;
    private readonly Text? _helpText;
    private readonly Text[] _legendTexts = new Text[LegendBands];

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
    private string _lastLayerValue = string.Empty;

    private bool? _lastHeightMapMode;
    private int _lastVisibleLayer = -1;
    private int _lastLayerCount = -1;

    public TerrainDebugOverlay()
    {
        _font = LoadFont();

        for (int i = 0; i < LegendBands; i++)
        {
            _legendSwatches[i] =
                new RectangleShape(new Vector2f(84f, 12f))
                {
                    Position = new Vector2f(
                        PanelX + 14f + i * 94f,
                        PanelY + 168f),
                    OutlineColor = new Color(220, 230, 242, 175),
                    OutlineThickness = 1f
                };
        }

        if (_font == null)
        {
            Console.WriteLine(
                "[TerrainDebugOverlay] System font unavailable; terrain controls will still be clickable.");
            return;
        }

        _titleText = CreateText(
            "РЕЛЬЕФ",
            18,
            new Color(245, 248, 252),
            new Vector2f(PanelX + 14f, PanelY + 9f),
            Text.Styles.Bold);

        _subtitleText = CreateText(
            "ТЕСТ ВЫСОТ  •  1–50 м",
            12,
            new Color(151, 169, 190),
            new Vector2f(PanelX + 112f, PanelY + 14f));

        _heightModeLabel = CreateText(
            "КАРТА ВЫСОТ",
            13,
            Color.White,
            new Vector2f(PanelX + 67f, PanelY + 48f),
            Text.Styles.Bold);

        _sliceModeLabel = CreateText(
            "ОБЪЁМНЫЙ СРЕЗ",
            13,
            Color.White,
            new Vector2f(PanelX + 289f, PanelY + 48f),
            Text.Styles.Bold);

        _layerLabel = CreateText(
            "УРОВЕНЬ СРЕЗА Z",
            12,
            new Color(192, 207, 223),
            new Vector2f(PanelX + 14f, PanelY + 79f),
            Text.Styles.Bold);

        _minusLabel = CreateText(
            "−",
            19,
            Color.White,
            new Vector2f(PanelX + 26f, PanelY + 101f));

        _plusLabel = CreateText(
            "+",
            18,
            Color.White,
            new Vector2f(PanelX + 429f, PanelY + 101f));

        _layerMinTick = CreateText(
            "1 м",
            11,
            new Color(149, 166, 185),
            new Vector2f(PanelX + SliderStartX, PanelY + 135f));

        _layerMidTick = CreateText(
            "25 м",
            11,
            new Color(149, 166, 185),
            new Vector2f(PanelX + SliderStartX + SliderWidth * 0.49f, PanelY + 135f));

        _layerMaxTick = CreateText(
            "50 м",
            11,
            new Color(149, 166, 185),
            new Vector2f(PanelX + SliderStartX + SliderWidth - 28f, PanelY + 135f));

        _layerValue = CreateText(
            "",
            12,
            new Color(106, 181, 255),
            new Vector2f(PanelX + 360f, PanelY + 79f),
            Text.Styles.Bold);

        _modeDescription = CreateText(
            "",
            12,
            new Color(193, 207, 221),
            new Vector2f(PanelX + 14f, PanelY + 151f));

        _minimumText = CreateText(
            "",
            12,
            new Color(113, 187, 255),
            new Vector2f(PanelX + 14f, PanelY + 205f));

        _maximumText = CreateText(
            "",
            12,
            new Color(255, 130, 139),
            new Vector2f(PanelX + 14f, PanelY + 221f));

        _cursorText = CreateText(
            "",
            12,
            Color.White,
            new Vector2f(PanelX + 14f, PanelY + 238f));

        _helpText = CreateText(
            "H — карта/срез   •   PgUp/PgDn — уровень   •   колесо — масштаб",
            11,
            new Color(155, 173, 192),
            new Vector2f(PanelX + 14f, PanelY + 260f));

        string[] labels =
        {
            "1–10 м",
            "11–20 м",
            "21–30 м",
            "31–40 м",
            "41–50 м"
        };

        for (int i = 0; i < LegendBands; i++)
        {
            _legendTexts[i] = CreateText(
                labels[i],
                11,
                new Color(225, 234, 244),
                new Vector2f(PanelX + 14f + i * 94f, PanelY + 183f));
        }
    }

    public TerrainOverlayAction? HandleClick(
        Vector2i point,
        int currentLayer,
        int layerCount)
    {
        if (_heightModeButton.GetGlobalBounds().Contains(point.X, point.Y))
        {
            return new TerrainOverlayAction(
                TerrainOverlayActionType.SelectHeightMap);
        }

        if (_sliceModeButton.GetGlobalBounds().Contains(point.X, point.Y))
        {
            return new TerrainOverlayAction(
                TerrainOverlayActionType.SelectVolumeSlice);
        }

        if (_minusButton.GetGlobalBounds().Contains(point.X, point.Y))
        {
            return new TerrainOverlayAction(
                TerrainOverlayActionType.SetLayer,
                Math.Clamp(currentLayer - 1, 0, layerCount - 1));
        }

        if (_plusButton.GetGlobalBounds().Contains(point.X, point.Y))
        {
            return new TerrainOverlayAction(
                TerrainOverlayActionType.SetLayer,
                Math.Clamp(currentLayer + 1, 0, layerCount - 1));
        }

        // The generous hit area includes the slider and its tick labels.
        float sliderLeft = PanelX + SliderStartX - 8f;
        float sliderRight = PanelX + SliderStartX + SliderWidth + 8f;
        float sliderTop = PanelY + 109f;
        float sliderBottom = PanelY + 147f;

        if (point.X < sliderLeft ||
            point.X > sliderRight ||
            point.Y < sliderTop ||
            point.Y > sliderBottom ||
            layerCount <= 1)
        {
            return null;
        }

        float amount = Math.Clamp(
            (point.X - (PanelX + SliderStartX)) / SliderWidth,
            0f,
            1f);

        int selectedLayer = (int)MathF.Round(
            amount * (layerCount - 1));

        return new TerrainOverlayAction(
            TerrainOverlayActionType.SetLayer,
            selectedLayer);
    }

    public void DrawWorldMarkers(
        RenderWindow window,
        WorldMap worldMap,
        float tilePixelSize,
        float zoomLevel,
        bool heightMapMode)
    {
        RefreshExtremes(worldMap);

        if (!heightMapMode)
            return;

        float radius = Math.Clamp(4.5f * zoomLevel, 0.25f, 22f);

        // The coordinate card gives the exact location; these pins only mark
        // the low and high extrema on the map without adding large map labels.
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

    private readonly CircleShape _minimumPin = new CircleShape(4.5f, 20)
    {
        FillColor = new Color(35, 145, 255),
        OutlineColor = Color.White,
        OutlineThickness = 1.5f
    };

    private readonly CircleShape _maximumPin = new CircleShape(4.5f, 20)
    {
        FillColor = new Color(245, 55, 65),
        OutlineColor = Color.White,
        OutlineThickness = 1.5f
    };

    public void DrawScreen(
        RenderWindow window,
        WorldMap worldMap,
        View cameraView,
        float tilePixelSize,
        bool heightMapMode,
        int visibleMaxLayer)
    {
        RefreshExtremes(worldMap);
        RefreshStyle(
            heightMapMode,
            visibleMaxLayer,
            worldMap.LayerCount,
            Mouse.GetPosition(window));

        window.Draw(_panel);
        window.Draw(_accent);

        window.Draw(_heightModeButton);
        window.Draw(_sliceModeButton);
        window.Draw(_minusButton);
        window.Draw(_plusButton);

        window.Draw(_sliderTrack);
        window.Draw(_sliderProgress);
        window.Draw(_sliderKnob);

        for (int i = 0; i < LegendBands; i++)
            window.Draw(_legendSwatches[i]);

        if (_font != null)
        {
            for (int i = 0; i < LegendBands; i++)
                window.Draw(_legendTexts[i]);

            UpdateCursorText(
                window,
                worldMap,
                cameraView,
                tilePixelSize);

            for (int i = 0; i < _texts.Count; i++)
                window.Draw(_texts[i]);
        }
    }

    private void RefreshStyle(
        bool heightMapMode,
        int visibleMaxLayer,
        int layerCount,
        Vector2i mouse)
    {
        bool mapHovered = _heightModeButton.GetGlobalBounds().Contains(
            mouse.X,
            mouse.Y);

        bool sliceHovered = _sliceModeButton.GetGlobalBounds().Contains(
            mouse.X,
            mouse.Y);

        bool minusHovered = _minusButton.GetGlobalBounds().Contains(
            mouse.X,
            mouse.Y);

        bool plusHovered = _plusButton.GetGlobalBounds().Contains(
            mouse.X,
            mouse.Y);
        bool heightSelected = heightMapMode;
        bool sliceSelected = !heightMapMode;

        _heightModeButton.FillColor = heightSelected
            ? new Color(35, 91, 170)
            : mapHovered
                ? new Color(35, 48, 64)
                : new Color(23, 32, 44);

        _heightModeButton.OutlineColor = heightSelected
            ? new Color(104, 171, 255)
            : new Color(61, 79, 99);

        _sliceModeButton.FillColor = sliceSelected
            ? new Color(35, 91, 170)
            : sliceHovered
                ? new Color(35, 48, 64)
                : new Color(23, 32, 44);

        _sliceModeButton.OutlineColor = sliceSelected
            ? new Color(104, 171, 255)
            : new Color(61, 79, 99);

        _minusButton.FillColor = minusHovered
            ? new Color(51, 68, 88)
            : new Color(31, 43, 57);

        _plusButton.FillColor = plusHovered
            ? new Color(51, 68, 88)
            : new Color(31, 43, 57);

        _minusButton.OutlineColor = visibleMaxLayer > 0
            ? new Color(76, 96, 118)
            : new Color(46, 55, 66);

        _plusButton.OutlineColor = visibleMaxLayer < layerCount - 1
            ? new Color(76, 96, 118)
            : new Color(46, 55, 66);

        int safeLayerCount = Math.Max(1, layerCount);
        visibleMaxLayer = Math.Clamp(visibleMaxLayer, 0, safeLayerCount - 1);
        float amount = safeLayerCount <= 1
            ? 0f
            : visibleMaxLayer / (float)(safeLayerCount - 1);

        float knobX = PanelX + SliderStartX + amount * SliderWidth;
        _sliderProgress.Size = new Vector2f(amount * SliderWidth, 4f);
        _sliderKnob.Position = new Vector2f(knobX, PanelY + SliderY + 2f);

        SetText(
            _layerValue,
            ref _lastLayerValue,
            $"Z = {visibleMaxLayer + 1} м");

        if (_lastHeightMapMode != heightMapMode)
        {
            _lastHeightMapMode = heightMapMode;
            SetText(
                _modeDescription,
                ref _lastModeDescription,
                heightMapMode
                    ? "Высота поверхности: СИНИЙ = низко  →  КРАСНЫЙ = высоко"
                    : "Срез: цвет = высота поверхности; сдвиг вниз = глубже в грунте");
        }

        if (_lastLayerCount != layerCount)
        {
            _lastLayerCount = layerCount;

            for (int i = 0; i < LegendBands; i++)
            {
                _legendSwatches[i].FillColor =
                    TerrainHeightPalette.GetBandColor(i, layerCount);
            }

            if (_layerMinTick != null)
                _layerMinTick.DisplayedString = "1 м";

            if (_layerMidTick != null)
                _layerMidTick.DisplayedString = $"{(layerCount + 1) / 2} м";

            if (_layerMaxTick != null)
                _layerMaxTick.DisplayedString = $"{layerCount} м";


        }

        if (_minimumHeightUnits > 0)
        {
            SetText(
                _minimumText,
                ref _lastMinimum,
                $"● НИЗИНА  {_minimumHeightUnits * 0.1f:0.#} м    X={_minimumX}  Y={_minimumY}");

            SetText(
                _maximumText,
                ref _lastMaximum,
                $"● ВЕРШИНА  {_maximumHeightUnits * 0.1f:0.#} м    X={_maximumX}  Y={_maximumY}");
        }
    }

    private string _lastModeDescription = string.Empty;

    private void UpdateCursorText(
        RenderWindow window,
        WorldMap worldMap,
        View cameraView,
        float tilePixelSize)
    {
        if (_cursorText == null)
            return;

        Vector2i mouse = Mouse.GetPosition(window);

        if (_panel.GetGlobalBounds().Contains(mouse.X, mouse.Y))
        {
            SetText(
                _cursorText,
                ref _lastCursor,
                "КУРСОР  —  наведите на карту, чтобы узнать высоту");
            _lastMouseCellX = int.MinValue;
            _lastMouseCellY = int.MinValue;
            return;
        }

        Vector2u windowSize = window.Size;

        if (mouse.X < 0 ||
            mouse.Y < 0 ||
            mouse.X >= windowSize.X ||
            mouse.Y >= windowSize.Y ||
            windowSize.X == 0 ||
            windowSize.Y == 0)
        {
            SetText(
                _cursorText,
                ref _lastCursor,
                "Курсор: наведите на карту, чтобы узнать высоту.");
            _lastMouseCellX = int.MinValue;
            _lastMouseCellY = int.MinValue;
            return;
        }

        Vector2f center = cameraView.Center;
        Vector2f viewSize = cameraView.Size;

        float worldX =
            center.X - viewSize.X * 0.5f +
            mouse.X * viewSize.X / windowSize.X;

        float worldY =
            center.Y - viewSize.Y * 0.5f +
            mouse.Y * viewSize.Y / windowSize.Y;

        int x = (int)MathF.Floor(worldX / tilePixelSize);
        int y = (int)MathF.Floor(worldY / tilePixelSize);

        if (x == _lastMouseCellX &&
            y == _lastMouseCellY &&
            _lastMouseTerrainVersion == worldMap.TerrainVersion)
        {
            return;
        }

        _lastMouseCellX = x;
        _lastMouseCellY = y;
        _lastMouseTerrainVersion = worldMap.TerrainVersion;

        if (x < 0 || y < 0 ||
            x >= worldMap.TileWidth ||
            y >= worldMap.TileHeight)
        {
            SetText(
                _cursorText,
                ref _lastCursor,
                "КУРСОР  —  ВНЕ ПРЕДЕЛОВ КАРТЫ");
            return;
        }

        float heightMeters =
            worldMap.GetSurfaceHeightUnits(x, y) * 0.1f;

        SetText(
            _cursorText,
            ref _lastCursor,
            $"КУРСОР  X={x}  Y={y}   •   ВЫСОТА {heightMeters:0.#} м");
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

        string[] candidates =
        {
            "segoeui.ttf",
            "arial.ttf",
            "tahoma.ttf"
        };

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
                // Fall through to the next installed font.
            }
        }

        return null;
    }

    public void Dispose()
    {
        for (int i = 0; i < _texts.Count; i++)
            _texts[i].Dispose();

        _font?.Dispose();
        _panel.Dispose();
        _accent.Dispose();
        _heightModeButton.Dispose();
        _sliceModeButton.Dispose();
        _minusButton.Dispose();
        _plusButton.Dispose();
        _sliderTrack.Dispose();
        _sliderProgress.Dispose();
        _sliderKnob.Dispose();
        _minimumPin.Dispose();
        _maximumPin.Dispose();

        for (int i = 0; i < _legendSwatches.Length; i++)
            _legendSwatches[i].Dispose();
    }
}
