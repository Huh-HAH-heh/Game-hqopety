using System;
using System.Collections.Generic;
using System.IO;
using Core.Map;
using SFML.Graphics;
using SFML.System;
using SFML.Window;

namespace RimClone.Render;

public sealed class TerrainDebugOverlay : IDisposable
{
    private const int LegendBands = 5;

    private readonly Font? _font;
    private readonly RectangleShape _panel =
        new RectangleShape(new Vector2f(610f, 229f))
        {
            Position = new Vector2f(12f, 12f),
            FillColor = new Color(8, 12, 17, 232),
            OutlineColor = new Color(110, 130, 145, 220),
            OutlineThickness = 1f
        };

    private readonly RectangleShape[] _legendSwatches =
        new RectangleShape[LegendBands];

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

    private readonly List<Text> _texts = new();

    private Text? _titleText;
    private Text? _modeText;
    private Text? _minimumText;
    private Text? _maximumText;
    private Text? _cursorText;
    private Text? _meaningText;
    private Text? _controlsText;
    private readonly Text[] _legendTexts = new Text[LegendBands];
    private Text? _minimumMapLabel;
    private Text? _maximumMapLabel;

    private long _cachedTerrainVersion = long.MinValue;
    private int _minimumX;
    private int _minimumY;
    private int _maximumX;
    private int _maximumY;
    private ushort _minimumHeightUnits;
    private ushort _maximumHeightUnits;

    private string _lastMode = string.Empty;
    private string _lastMinimum = string.Empty;
    private string _lastMaximum = string.Empty;
    private string _lastCursor = string.Empty;
    private string _lastMeaning = string.Empty;
    private string _lastControls = string.Empty;
    private string _lastMinimumMapLabel = string.Empty;
    private string _lastMaximumMapLabel = string.Empty;

    private int _lastMouseCellX = int.MinValue;
    private int _lastMouseCellY = int.MinValue;
    private long _lastMouseTerrainVersion = long.MinValue;

    public TerrainDebugOverlay()
    {
        _font = LoadFont();

        _minimumPin.Origin = new Vector2f(4.5f, 4.5f);
        _maximumPin.Origin = new Vector2f(4.5f, 4.5f);

        for (int i = 0; i < LegendBands; i++)
        {
            _legendSwatches[i] =
                new RectangleShape(new Vector2f(88f, 11f))
                {
                    Position = new Vector2f(24f + i * 112f, 82f),
                    FillColor = TerrainHeightPalette.GetBandColor(i, WorldMap.DefaultTerrainLayerCount),
                    OutlineColor = new Color(235, 240, 245, 180),
                    OutlineThickness = 1f
                };
        }

        if (_font == null)
        {
            Console.WriteLine(
                "[TerrainDebugOverlay] Не найден системный шрифт. Цветовая карта и маркеры останутся доступны.");
            return;
        }

        _titleText = CreateText("ТЕСТ РЕЛЬЕФА", 17, new Color(245, 248, 250));
        _titleText.Style = Text.Styles.Bold;
        _modeText = CreateText("", 14, new Color(225, 235, 245));
        _minimumText = CreateText("", 14, new Color(110, 190, 255));
        _maximumText = CreateText("", 14, new Color(255, 120, 125));
        _cursorText = CreateText("", 14, Color.White);
        _meaningText = CreateText("", 14, new Color(235, 215, 150));
        _controlsText = CreateText("", 13, new Color(190, 205, 218));
        _minimumMapLabel = CreateText("", 13, Color.White);
        _maximumMapLabel = CreateText("", 13, Color.White);

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
            _legendTexts[i] =
                CreateText(labels[i], 13, new Color(225, 235, 242));
        }

        SetText(
            _controlsText,
            ref _lastControls,
            "H — карта высот / объёмный срез   |   PgUp/PgDn — уровень Z   |   колесо — масштаб");
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

        float markerRadius = Math.Clamp(4.5f * zoomLevel, 1.5f, 22f);
        _minimumPin.Radius = markerRadius;
        _maximumPin.Radius = markerRadius;
        _minimumPin.Origin = new Vector2f(markerRadius, markerRadius);
        _maximumPin.Origin = new Vector2f(markerRadius, markerRadius);

        Vector2f minimumPosition = new Vector2f(
            (_minimumX + 0.5f) * tilePixelSize,
            (_minimumY + 0.5f) * tilePixelSize);

        Vector2f maximumPosition = new Vector2f(
            (_maximumX + 0.5f) * tilePixelSize,
            (_maximumY + 0.5f) * tilePixelSize);

        _minimumPin.Position = minimumPosition;
        _maximumPin.Position = maximumPosition;

        window.Draw(_minimumPin);
        window.Draw(_maximumPin);

        if (_font == null ||
            _minimumMapLabel == null ||
            _maximumMapLabel == null)
        {
            return;
        }

        uint characterSize =
            (uint)Math.Clamp(
                Math.Round(13f * zoomLevel),
                1d,
                96d);

        _minimumMapLabel.CharacterSize = characterSize;
        _maximumMapLabel.CharacterSize = characterSize;

        SetText(
            _minimumMapLabel,
            ref _lastMinimumMapLabel,
            $"НИЗИНА {_minimumHeightUnits * 0.1f:0.#} м");

        SetText(
            _maximumMapLabel,
            ref _lastMaximumMapLabel,
            $"ВЕРШИНА {_maximumHeightUnits * 0.1f:0.#} м");

        float labelOffset = 8f * zoomLevel;
        _minimumMapLabel.Position = minimumPosition + new Vector2f(labelOffset, -labelOffset);
        _maximumMapLabel.Position = maximumPosition + new Vector2f(labelOffset, -labelOffset);

        window.Draw(_minimumMapLabel);
        window.Draw(_maximumMapLabel);
    }

    public void DrawScreen(
        RenderWindow window,
        WorldMap worldMap,
        View cameraView,
        float tilePixelSize,
        float zoomLevel,
        int visibleMaxLayer,
        bool heightMapMode)
    {
        RefreshExtremes(worldMap);

        if (_font == null)
            return;

        SetText(
            _modeText!,
            ref _lastMode,
            heightMapMode
                ? "Режим: КАРТА ВЫСОТ — цвет показывает поверхность целиком."
                : $"Режим: ОБЪЁМНЫЙ СРЕЗ до Z={visibleMaxLayer + 1} м.");

        SetText(
            _minimumText!,
            ref _lastMinimum,
            $"НИЖНЯЯ ТОЧКА: {_minimumHeightUnits * 0.1f:0.#} м   |   X={_minimumX}, Y={_minimumY}");

        SetText(
            _maximumText!,
            ref _lastMaximum,
            $"ВЕРХНЯЯ ТОЧКА: {_maximumHeightUnits * 0.1f:0.#} м   |   X={_maximumX}, Y={_maximumY}");

        SetText(
            _meaningText!,
            ref _lastMeaning,
            heightMapMode
                ? "СИНИЙ = низко, КРАСНЫЙ = высоко. Чёрный цвет не используется для обозначения высоты."
                : "Цвет = высота поверхности; потемнение = слой глубже под поверхностью. Тёмное ≠ низина.");

        UpdateCursorText(
            window,
            worldMap,
            cameraView,
            tilePixelSize,
            heightMapMode);

        window.Draw(_panel);

        for (int i = 0; i < LegendBands; i++)
        {
            window.Draw(_legendSwatches[i]);
            window.Draw(_legendTexts[i]);
        }

        for (int i = 0; i < _texts.Count; i++)
        {
            Text text = _texts[i];

            if (text == _minimumMapLabel ||
                text == _maximumMapLabel)
            {
                continue;
            }

            text.Position = text == _titleText
                ? new Vector2f(24f, 18f)
                : text == _modeText
                    ? new Vector2f(24f, 44f)
                    : text == _minimumText
                        ? new Vector2f(24f, 119f)
                        : text == _maximumText
                            ? new Vector2f(24f, 141f)
                            : text == _cursorText
                                ? new Vector2f(24f, 163f)
                                : text == _meaningText
                                    ? new Vector2f(24f, 185f)
                                    : text == _controlsText
                                        ? new Vector2f(24f, 207f)
                                        : new Vector2f(24f, 18f);

            if (text != null)
                window.Draw(text);
        }
    }

    private void UpdateCursorText(
        RenderWindow window,
        WorldMap worldMap,
        View cameraView,
        float tilePixelSize,
        bool heightMapMode)
    {
        if (_cursorText == null)
            return;

        if (!heightMapMode)
        {
            SetText(
                _cursorText,
                ref _lastCursor,
                "Высота под курсором доступна в режиме «Карта высот» (H).");
            return;
        }

        Vector2i mouse = Mouse.GetPosition(window);
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

        if (x < 0 || y < 0 || x >= worldMap.TileWidth || y >= worldMap.TileHeight)
        {
            SetText(
                _cursorText,
                ref _lastCursor,
                "Курсор: за пределами карты.");
            return;
        }

        float heightMeters =
            worldMap.GetSurfaceHeightUnits(x, y) * 0.1f;

        SetText(
            _cursorText,
            ref _lastCursor,
            $"КУРСОР: X={x}, Y={y}   |   ВЫСОТА ПОВЕРХНОСТИ: {heightMeters:0.#} м");
    }

    private void RefreshExtremes(WorldMap worldMap)
    {
        if (_cachedTerrainVersion == worldMap.TerrainVersion)
            return;

        _minimumHeightUnits = ushort.MaxValue;
        _maximumHeightUnits = 0;

        for (int y = 0; y < worldMap.TileHeight; y++)
        {
            for (int x = 0; x < worldMap.TileWidth; x++)
            {
                ushort height =
                    worldMap.GetSurfaceHeightUnits(x, y);

                if (height == 0)
                    continue;

                if (height < _minimumHeightUnits)
                {
                    _minimumHeightUnits = height;
                    _minimumX = x;
                    _minimumY = y;
                }

                if (height > _maximumHeightUnits)
                {
                    _maximumHeightUnits = height;
                    _maximumX = x;
                    _maximumY = y;
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
        Color color)
    {
        Text text = new Text(_font!, value, characterSize)
        {
            FillColor = color,
            OutlineColor = new Color(0, 0, 0, 230),
            OutlineThickness = 1f
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
            "arial.ttf",
            "segoeui.ttf",
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
                // Try the next installed system font.
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

        for (int i = 0; i < _legendSwatches.Length; i++)
            _legendSwatches[i].Dispose();

        for (int i = 0; i < _legendTexts.Length; i++)
            _legendTexts[i]?.Dispose();

        _minimumPin.Dispose();
        _maximumPin.Dispose();
    }
}
