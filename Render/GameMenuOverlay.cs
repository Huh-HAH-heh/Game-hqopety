using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Core.Map;
using SFML.Graphics;
using SFML.System;
using SFML.Window;

namespace RimClone.Render;

public enum GameMenuPage
{
    Closed,
    Main,
    Settings
}

public enum GameMenuActionType
{
    OpenMainMenu,
    Resume,
    OpenSettings,
    BackToMainMenu,
    Exit,
    PreviousResolution,
    NextResolution,
    ApplyResolution,
    SetBaseGray,
    SetHeightContrast,
    SetLayer,
    ToggleGrid,
    ToggleExtrema,
    ToggleHeightContours,
    ResetTerrainSettings
}

public readonly record struct GameMenuAction(
    GameMenuActionType Type,
    float Value = 0f,
    int Layer = 0);

public readonly record struct GameSettingsSnapshot(
    int VisibleMaxLayer,
    bool ShowGrid,
    bool ShowExtrema,
    bool ShowHeightContours,
    float BaseGray,
    float HeightContrast,
    int LayerCount,
    int SelectedResolutionIndex,
    uint CurrentWidth,
    uint CurrentHeight);

public readonly record struct GameResolution(
    uint Width,
    uint Height)
{
    public override string ToString() => $"{Width} × {Height}";
}

public sealed class GameMenuOverlay : IDisposable
{
    private static readonly GameResolution[] SupportedResolutions =
        BuildSupportedResolutions();

    private static GameResolution[] BuildSupportedResolutions()
    {
        var modes = new HashSet<GameResolution>();

        uint desktopWidth = 1920;
        uint desktopHeight = 1080;

        try
        {
            VideoMode desktopMode = VideoMode.DesktopMode;
            desktopWidth = desktopMode.Size.X;
            desktopHeight = desktopMode.Size.Y;

            // SFML provides the actual display modes reported by the active
            // monitor. Use these instead of a capped hard-coded list.
            VideoMode[] fullscreenModes = VideoMode.FullscreenModes;

            for (int i = 0; i < fullscreenModes.Length; i++)
            {
                uint width = fullscreenModes[i].Size.X;
                uint height = fullscreenModes[i].Size.Y;

                if (IsUsableResolution(width, height, desktopWidth, desktopHeight))
                    modes.Add(new GameResolution(width, height));
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"[Display] Could not query native display modes; using desktop-based window sizes: {exception.Message}");
        }

        // Always expose the display's current native resolution, even if
        // a graphics driver leaves it out of its mode list.
        if (IsUsableResolution(
                desktopWidth,
                desktopHeight,
                desktopWidth,
                desktopHeight))
        {
            modes.Add(new GameResolution(desktopWidth, desktopHeight));
        }

        // Add common windowed sizes even if a driver omits them from the
        // fullscreen mode list. Never offer a size larger than the desktop.
        GameResolution[] common =
        {
            new(1024, 768),
            new(1280, 720),
            new(1280, 800),
            new(1366, 768),
            new(1440, 900),
            new(1600, 900),
            new(1680, 1050),
            new(1920, 1080),
            new(1920, 1200),
            new(2048, 1080),
            new(2560, 1080),
            new(2560, 1440),
            new(3440, 1440),
            new(3840, 2160)
        };

        for (int i = 0; i < common.Length; i++)
        {
            GameResolution resolution = common[i];

            if (IsUsableResolution(
                    resolution.Width,
                    resolution.Height,
                    desktopWidth,
                    desktopHeight))
            {
                modes.Add(resolution);
            }
        }

        // Always keep a sensible fallback if display mode enumeration failed.
        if (modes.Count == 0)
        {
            modes.Add(new GameResolution(1024, 768));
            modes.Add(new GameResolution(1280, 720));
        }

        var result = new List<GameResolution>(modes);
        result.Sort(static (left, right) =>
        {
            int widthOrder = left.Width.CompareTo(right.Width);
            return widthOrder != 0
                ? widthOrder
                : left.Height.CompareTo(right.Height);
        });

        return result.ToArray();
    }

    private static bool IsUsableResolution(
        uint width,
        uint height,
        uint desktopWidth,
        uint desktopHeight)
    {
        return width >= 960 &&
               height >= 540 &&
               width <= desktopWidth &&
               height <= desktopHeight;
    }

    private const float MenuButtonSize = 38f;
    private const float SettingsWidth = 620f;
    private const float SettingsHeight = 620f;
    private const float MainWidth = 390f;
    private const float MainHeight = 310f;

    private sealed class HeightContourLevel : IDisposable
    {
        public int HeightUnits { get; }
        public VertexArray? FallbackVertices { get; }
        public List<Text> Labels { get; } = new();

        public HeightContourLevel(int heightUnits, bool useFallback)
        {
            HeightUnits = heightUnits;

            if (useFallback)
                FallbackVertices = new VertexArray(PrimitiveType.Lines);
        }

        public void Dispose()
        {
            FallbackVertices?.Dispose();

            for (int i = 0; i < Labels.Count; i++)
                Labels[i].Dispose();

            Labels.Clear();
        }
    }

    private sealed class HeightContourChunk : IDisposable
    {
        public readonly VertexArray Vertices = new VertexArray(PrimitiveType.Lines);
        public readonly List<HeightContourLevel> Levels = new();
        public long LastUsedFrame;

        public void Dispose()
        {
            Vertices.Dispose();

            for (int i = 0; i < Levels.Count; i++)
                Levels[i].Dispose();

            Levels.Clear();
        }
    }

    private const int MaxContourChunksBuiltPerFrame = 3;

    private const string ContourVertexShaderSource =
        @"void main()
{
    gl_Position = gl_ModelViewProjectionMatrix * gl_Vertex;
    gl_TexCoord[0] = gl_MultiTexCoord0;
    gl_FrontColor = gl_Color;
}";

    private const string ContourFragmentShaderSource =
        @"uniform float uVisibleLayer;
void main()
{
    if (gl_TexCoord[0].x <= uVisibleLayer)
        discard;

    gl_FragColor = gl_Color;
}";

    private Shader? _contourShader;

    private readonly Dictionary<int, HeightContourChunk> _contourChunks = new();
    private readonly List<HeightContourChunk> _visibleContourChunks = new();
    private long _contourTerrainVersion = long.MinValue;
    private int _contourLodStep = -1;
    private long _contourFrame;

    private readonly Font? _font;
    private readonly List<Text> _texts = new();
    private readonly List<SliderControl> _sliders = new();
    private Text?[] _mainMenuTexts = Array.Empty<Text?>();
    private Text?[] _settingsTexts = Array.Empty<Text?>();
    private int _lastSelectedResolutionIndex = -1;
    private uint _lastCurrentWidth;
    private uint _lastCurrentHeight;

    private readonly RectangleShape _backdrop =
        new RectangleShape(new Vector2f(1280f, 720f))
        {
            FillColor = new Color(0, 0, 0, 165)
        };

    private readonly RectangleShape _menuButton =
        new RectangleShape(new Vector2f(MenuButtonSize, MenuButtonSize))
        {
            Position = new Vector2f(14f, 12f),
            FillColor = new Color(19, 25, 34, 245),
            OutlineColor = new Color(80, 93, 108),
            OutlineThickness = 1f
        };

    private readonly RectangleShape _panel =
        new RectangleShape(new Vector2f(SettingsWidth, SettingsHeight))
        {
            FillColor = new Color(16, 21, 29, 252),
            OutlineColor = new Color(83, 96, 113),
            OutlineThickness = 1f
        };

    private readonly RectangleShape _accent =
        new RectangleShape(new Vector2f(SettingsWidth - 2f, 3f))
        {
            FillColor = new Color(143, 153, 166)
        };

    private readonly RectangleShape _mainPanel =
        new RectangleShape(new Vector2f(MainWidth, MainHeight))
        {
            FillColor = new Color(16, 21, 29, 252),
            OutlineColor = new Color(83, 96, 113),
            OutlineThickness = 1f
        };

    private readonly RectangleShape _mainAccent =
        new RectangleShape(new Vector2f(MainWidth - 2f, 3f))
        {
            FillColor = new Color(143, 153, 166)
        };

    private readonly RectangleShape _resumeButton = ButtonShape();
    private readonly RectangleShape _settingsButton = ButtonShape();
    private readonly RectangleShape _exitButton = ButtonShape();

    private readonly RectangleShape _closeButton = ButtonShape(34f, 32f);
    private readonly RectangleShape _backButton = ButtonShape(128f, 34f);
    private readonly RectangleShape _resetButton = ButtonShape(144f, 34f);
    private readonly RectangleShape _resolutionPrevious = ButtonShape(34f, 36f);
    private readonly RectangleShape _resolutionNext = ButtonShape(34f, 36f);
    private readonly RectangleShape _applyResolution = ButtonShape(168f, 36f);
    private readonly RectangleShape _gridButton = ButtonShape(180f, 34f);
    private readonly RectangleShape _extremaButton = ButtonShape(180f, 34f);
    private readonly RectangleShape _contoursButton = ButtonShape(180f, 34f);

    private readonly RectangleShape _resolutionBox =
        new RectangleShape(new Vector2f(232f, 36f))
        {
            FillColor = new Color(24, 31, 42),
            OutlineColor = new Color(77, 91, 109),
            OutlineThickness = 1f
        };

    private readonly RectangleShape _divider =
        new RectangleShape(new Vector2f(SettingsWidth - 36f, 1f))
        {
            FillColor = new Color(54, 64, 77)
        };

    private readonly RectangleShape[] _toneSwatches = new RectangleShape[5];

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

    private readonly Text? _minimumElevationLabel;
    private readonly Text? _maximumElevationLabel;

    private long _cachedTerrainVersion = long.MinValue;
    private ushort _minimumHeightUnits;
    private ushort _maximumHeightUnits;
    private int _minimumX;
    private int _minimumY;
    private int _maximumX;
    private int _maximumY;

    private readonly Text? _menuButtonLabel;
    private readonly Text? _mainTitle;
    private readonly Text? _mainSubtitle;
    private readonly Text? _resumeLabel;
    private readonly Text? _settingsLabel;
    private readonly Text? _exitLabel;

    private readonly Text? _settingsTitle;
    private readonly Text? _settingsSubtitle;
    private readonly Text? _closeLabel;
    private readonly Text? _resolutionHeading;
    private readonly Text? _resolutionValue;
    private readonly Text? _resolutionCurrent;
    private readonly Text? _previousLabel;
    private readonly Text? _nextLabel;
    private readonly Text? _applyResolutionLabel;
    private readonly Text? _gridLabel;
    private readonly Text? _extremaLabel;
    private readonly Text? _contoursLabel;
    private readonly Text? _toneHeading;
    private readonly Text? _toneLowLabel;
    private readonly Text? _toneHighLabel;
    private readonly Text? _resetLabel;
    private readonly Text? _backLabel;
    private readonly Text? _keyboardHelp;

    private string _lastGridLabel = string.Empty;
    private string _lastExtremaLabel = string.Empty;
    private string _lastContoursLabel = string.Empty;
    private string _lastResolution = string.Empty;
    private string _lastAppliedResolution = string.Empty;

    public static int ResolutionCount => SupportedResolutions.Length;

    public static GameResolution GetResolution(int index) =>
        SupportedResolutions[Math.Clamp(index, 0, SupportedResolutions.Length - 1)];

    public static int FindResolutionIndex(uint width, uint height)
    {
        for (int i = 0; i < SupportedResolutions.Length; i++)
        {
            if (SupportedResolutions[i].Width == width &&
                SupportedResolutions[i].Height == height)
            {
                return i;
            }
        }

        int nearestIndex = 0;
        ulong nearestDistance = ulong.MaxValue;

        for (int i = 0; i < SupportedResolutions.Length; i++)
        {
            GameResolution resolution = SupportedResolutions[i];

            long widthDelta = (long)resolution.Width - width;
            long heightDelta = (long)resolution.Height - height;
            ulong distance = (ulong)(widthDelta * widthDelta + heightDelta * heightDelta);

            if (distance >= nearestDistance)
                continue;

            nearestDistance = distance;
            nearestIndex = i;
        }

        return nearestIndex;
    }

    public GameMenuOverlay()
    {
        _font = LoadFont();

        if (Shader.IsAvailable)
        {
            try
            {
                _contourShader = Shader.FromString(
                    ContourVertexShaderSource,
                    null,
                    ContourFragmentShaderSource);
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    $"[TerrainContours] Shader disabled; using per-level fallback: {exception.Message}");
            }
        }

        for (int i = 0; i < _toneSwatches.Length; i++)
        {
            _toneSwatches[i] = new RectangleShape(new Vector2f(88f, 13f))
            {
                FillColor = TerrainHeightPalette.GetHeightPreviewColor(i, 48f, 14f),
                OutlineColor = new Color(115, 124, 136),
                OutlineThickness = 1f
            };
        }

        _sliders.Add(new SliderControl(
            _font, GameMenuActionType.SetBaseGray,
            "Базовый тон грунта", 8f, 96f));
        _sliders.Add(new SliderControl(
            _font, GameMenuActionType.SetHeightContrast,
            "Контраст перепада высот", 0f, 32f));
        _sliders.Add(new SliderControl(
            _font, GameMenuActionType.SetLayer,
            "Высота среза Z", 0f, WorldMap.DefaultTerrainLayerCount - 1));

        if (_font == null)
        {
            Console.WriteLine(
                "[GameMenu] Не найден системный шрифт. Кнопки останутся, но подписи будут недоступны.");
            return;
        }

        _minimumElevationLabel = CreateText(
            "", 10, new Color(200, 200, 200), new Vector2f(0f, 0f), Text.Styles.Bold);
        _maximumElevationLabel = CreateText(
            "", 10, new Color(200, 200, 200), new Vector2f(0f, 0f), Text.Styles.Bold);

        _menuButtonLabel = CreateText(
            "•••", 16, Color.White, new Vector2f(21f, 17f), Text.Styles.Bold);

        _mainTitle = CreateText(
            "ГЛАВНОЕ МЕНЮ", 19, new Color(241, 245, 250),
            new Vector2f(0f, 0f), Text.Styles.Bold);
        _mainSubtitle = CreateText(
            "RIMCLONE  •  ТЕСТ РЕЛЬЕФА", 11, new Color(154, 168, 185),
            new Vector2f(0f, 0f));
        _resumeLabel = CreateText(
            "ПРОДОЛЖИТЬ", 13, Color.White, new Vector2f(0f, 0f), Text.Styles.Bold);
        _settingsLabel = CreateText(
            "НАСТРОЙКИ", 13, Color.White, new Vector2f(0f, 0f), Text.Styles.Bold);
        _exitLabel = CreateText(
            "ВЫХОД", 13, Color.White, new Vector2f(0f, 0f), Text.Styles.Bold);

        _settingsTitle = CreateText(
            "НАСТРОЙКИ", 19, new Color(241, 245, 250),
            new Vector2f(0f, 0f), Text.Styles.Bold);
        _settingsSubtitle = CreateText(
            "Графика и окно игры", 11, new Color(154, 168, 185), new Vector2f(0f, 0f));
        _closeLabel = CreateText(
            "×", 19, Color.White, new Vector2f(0f, 0f));
        _resolutionHeading = CreateText(
            "РАЗРЕШЕНИЕ ОКНА", 12, new Color(216, 224, 234),
            new Vector2f(0f, 0f), Text.Styles.Bold);
        _resolutionValue = CreateText(
            "", 13, Color.White, new Vector2f(0f, 0f), Text.Styles.Bold);
        _resolutionCurrent = CreateText(
            "", 10, new Color(154, 168, 185), new Vector2f(0f, 0f));
        _previousLabel = CreateText(
            "‹", 22, Color.White, new Vector2f(0f, 0f));
        _nextLabel = CreateText(
            "›", 22, Color.White, new Vector2f(0f, 0f));
        _applyResolutionLabel = CreateText(
            "ПРИМЕНИТЬ", 11, Color.White, new Vector2f(0f, 0f), Text.Styles.Bold);
        _gridLabel = CreateText(
            "", 11, Color.White, new Vector2f(0f, 0f), Text.Styles.Bold);
        _extremaLabel = CreateText(
            "", 11, Color.White, new Vector2f(0f, 0f), Text.Styles.Bold);
        _contoursLabel = CreateText(
            "", 11, Color.White, new Vector2f(0f, 0f), Text.Styles.Bold);
        _toneHeading = CreateText(
            "ПРЕВЬЮ МОНОХРОМНОГО ТОНА", 11,
            new Color(195, 204, 215), new Vector2f(0f, 0f), Text.Styles.Bold);
        _toneLowLabel = CreateText(
            "НИЗИНА", 10, new Color(157, 168, 182), new Vector2f(0f, 0f));
        _toneHighLabel = CreateText(
            "ВЫСОТА", 10, new Color(205, 213, 222), new Vector2f(0f, 0f));
        _resetLabel = CreateText(
            "СБРОСИТЬ ГРАФИКУ", 11, Color.White, new Vector2f(0f, 0f), Text.Styles.Bold);
        _backLabel = CreateText(
            "НАЗАД", 11, Color.White, new Vector2f(0f, 0f), Text.Styles.Bold);
        _keyboardHelp = CreateText(
            "F2 — настройки   PgUp/PgDn — срез Z   Shift+PgUp/PgDn — 1 м   Shift+колесо — Z   Колесо — масштаб   WASD — камера",
            10, new Color(145, 157, 173), new Vector2f(0f, 0f));

        _mainMenuTexts = new Text?[]
        {
            _mainTitle,
            _mainSubtitle,
            _resumeLabel,
            _settingsLabel,
            _exitLabel
        };

        _settingsTexts = new Text?[]
        {
            _settingsTitle,
            _settingsSubtitle,
            _closeLabel,
            _resolutionHeading,
            _resolutionValue,
            _resolutionCurrent,
            _previousLabel,
            _nextLabel,
            _applyResolutionLabel,
            _gridLabel,
            _extremaLabel,
            _contoursLabel,
            _toneHeading,
            _toneLowLabel,
            _toneHighLabel,
            _resetLabel,
            _backLabel,
            _keyboardHelp
        };
    }

    public bool IsSliderHit(
        GameMenuPage page,
        Vector2i point,
        out GameMenuActionType sliderType)
    {
        if (page != GameMenuPage.Settings)
        {
            sliderType = default;
            return false;
        }

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

    public GameMenuAction GetSliderAction(
        GameMenuActionType sliderType,
        int screenX,
        int layerCount)
    {
        for (int i = 0; i < _sliders.Count; i++)
        {
            SliderControl slider = _sliders[i];
            if (slider.ActionType != sliderType)
                continue;

            float maximum = slider.MaxValue;
            if (sliderType == GameMenuActionType.SetLayer)
                maximum = Math.Max(0, layerCount - 1);

            float value = slider.ValueFromX(screenX, maximum);
            if (sliderType == GameMenuActionType.SetLayer)
            {
                return new GameMenuAction(
                    sliderType,
                    Layer: (int)MathF.Round(value));
            }

            return new GameMenuAction(sliderType, Value: value);
        }

        return new GameMenuAction(sliderType);
    }

    public GameMenuAction? HandleClick(
        Vector2i point,
        GameMenuPage page,
        int layerCount)
    {
        if (_menuButton.GetGlobalBounds().Contains(point))
        {
            return new GameMenuAction(GameMenuActionType.OpenMainMenu);
        }

        if (page == GameMenuPage.Closed)
            return null;

        if (page == GameMenuPage.Main)
        {
            if (_resumeButton.GetGlobalBounds().Contains(point))
                return new GameMenuAction(GameMenuActionType.Resume);

            if (_settingsButton.GetGlobalBounds().Contains(point))
                return new GameMenuAction(GameMenuActionType.OpenSettings);

            if (_exitButton.GetGlobalBounds().Contains(point))
                return new GameMenuAction(GameMenuActionType.Exit);

            return null;
        }

        if (_closeButton.GetGlobalBounds().Contains(point) ||
            _backButton.GetGlobalBounds().Contains(point))
        {
            return new GameMenuAction(GameMenuActionType.BackToMainMenu);
        }

        if (_resolutionPrevious.GetGlobalBounds().Contains(point))
        {
            return new GameMenuAction(GameMenuActionType.PreviousResolution);
        }

        if (_resolutionNext.GetGlobalBounds().Contains(point))
        {
            return new GameMenuAction(GameMenuActionType.NextResolution);
        }

        if (_applyResolution.GetGlobalBounds().Contains(point))
        {
            return new GameMenuAction(GameMenuActionType.ApplyResolution);
        }

        if (_gridButton.GetGlobalBounds().Contains(point))
        {
            return new GameMenuAction(GameMenuActionType.ToggleGrid);
        }

        if (_extremaButton.GetGlobalBounds().Contains(point))
        {
            return new GameMenuAction(GameMenuActionType.ToggleExtrema);
        }

        if (_contoursButton.GetGlobalBounds().Contains(point))
        {
            return new GameMenuAction(GameMenuActionType.ToggleHeightContours);
        }

        if (_resetButton.GetGlobalBounds().Contains(point))
        {
            return new GameMenuAction(GameMenuActionType.ResetTerrainSettings);
        }

        if (IsSliderHit(page, point, out GameMenuActionType sliderType))
        {
            return GetSliderAction(sliderType, point.X, layerCount);
        }

        return null;
    }

    public void DrawWorldMarkers(
        RenderWindow window,
        WorldMap worldMap,
        float tilePixelSize,
        float zoomLevel,
        bool showExtrema,
        View cameraView,
        int visibleMaxLayer,
        bool showHeightContours)
    {
        if (showHeightContours)
        {
            DrawHeightContours(
                window,
                worldMap,
                tilePixelSize,
                zoomLevel,
                cameraView,
                visibleMaxLayer);
        }

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

        DrawElevationMarkerLabel(
            window,
            _minimumElevationLabel,
            _minimumPin.Position,
            radius,
            zoomLevel);

        DrawElevationMarkerLabel(
            window,
            _maximumElevationLabel,
            _maximumPin.Position,
            radius,
            zoomLevel);
    }

    private void DrawElevationMarkerLabel(
        RenderWindow window,
        Text? label,
        Vector2f position,
        float radius,
        float zoomLevel)
    {
        if (label == null)
            return;

        label.CharacterSize = (uint)Math.Clamp(
            (int)MathF.Round(10f * zoomLevel),
            1,
            80);

        label.Position = new Vector2f(
            position.X + radius + 2f * zoomLevel,
            position.Y - label.CharacterSize * 0.75f);

        window.Draw(label);
    }

    private void DrawHeightContours(
        RenderWindow window,
        WorldMap worldMap,
        float tilePixelSize,
        float zoomLevel,
        View cameraView,
        int visibleMaxLayer)
    {
        float screenMinX = cameraView.Center.X - cameraView.Size.X * 0.5f;
        float screenMaxX = cameraView.Center.X + cameraView.Size.X * 0.5f;
        float screenMinY = cameraView.Center.Y - cameraView.Size.Y * 0.5f;
        float screenMaxY = cameraView.Center.Y + cameraView.Size.Y * 0.5f;

        int lodStep = zoomLevel >= 4.5f
            ? 3
            : zoomLevel >= 2.5f
                ? 2
                : 1;

        int minTileX = Math.Max(
            0,
            (int)MathF.Floor(screenMinX / tilePixelSize) - 1);
        int maxTileX = Math.Min(
            worldMap.MaxTileX,
            (int)MathF.Ceiling(screenMaxX / tilePixelSize) + 1);
        int minTileY = Math.Max(
            0,
            (int)MathF.Floor(screenMinY / tilePixelSize) - 1);
        int maxTileY = Math.Min(
            worldMap.MaxTileY,
            (int)MathF.Ceiling(screenMaxY / tilePixelSize) + 1);

        if (_contourTerrainVersion != worldMap.TerrainVersion ||
            _contourLodStep != lodStep)
        {
            ClearContourChunkCache();
            _contourTerrainVersion = worldMap.TerrainVersion;
            _contourLodStep = lodStep;
        }

        int minRegionX = minTileX / TerrainRegion.TilesPerSide;
        int maxRegionX = maxTileX / TerrainRegion.TilesPerSide;
        int minRegionY = minTileY / TerrainRegion.TilesPerSide;
        int maxRegionY = maxTileY / TerrainRegion.TilesPerSide;
        int visibleCount =
            (maxRegionX - minRegionX + 1) *
            (maxRegionY - minRegionY + 1);

        _contourFrame++;
        _visibleContourChunks.Clear();

        int builtThisFrame = 0;
        long buildStarted = Stopwatch.GetTimestamp();

        for (int regionY = minRegionY; regionY <= maxRegionY; regionY++)
        {
            for (int regionX = minRegionX; regionX <= maxRegionX; regionX++)
            {
                int key = regionX + regionY * worldMap.RegionsX;

                if (!_contourChunks.TryGetValue(key, out HeightContourChunk? chunk))
                {
                    double buildMilliseconds =
                        Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds;

                    // Spread cache warm-up over several frames so panning into
                    // a new map area cannot trigger one large contour rebuild.
                    if (builtThisFrame >= MaxContourChunksBuiltPerFrame ||
                        (builtThisFrame > 0 && buildMilliseconds >= 2.0))
                    {
                        continue;
                    }

                    chunk = BuildHeightContourChunk(
                        worldMap,
                        tilePixelSize,
                        regionX,
                        regionY,
                        lodStep);

                    _contourChunks.Add(key, chunk);
                    builtThisFrame++;
                }

                chunk.LastUsedFrame = _contourFrame;
                _visibleContourChunks.Add(chunk);
            }
        }

        for (int i = 0; i < _visibleContourChunks.Count; i++)
        {
            HeightContourChunk chunk = _visibleContourChunks[i];

            for (int levelIndex = 0; levelIndex < chunk.Levels.Count; levelIndex++)
            {
                HeightContourLevel level = chunk.Levels[levelIndex];

                // Contours below the currently selected horizontal slice
                // are hidden, but the contour geometry itself stays cached.
                if (level.HeightUnits <= visibleMaxLayer)
                    continue;

                if (_contourShader == null &&
                    level.FallbackVertices != null &&
                    level.FallbackVertices.VertexCount > 0)
                {
                    window.Draw(level.FallbackVertices);
                }

                uint labelSize = (uint)Math.Clamp(
                    (int)MathF.Round(9f * zoomLevel),
                    1,
                    72);

                for (int labelIndex = 0; labelIndex < level.Labels.Count; labelIndex++)
                {
                    Text label = level.Labels[labelIndex];

                    if (label.CharacterSize != labelSize)
                        label.CharacterSize = labelSize;

                    window.Draw(label);
                }
            }
        }

        if (_contourShader != null)
        {
            _contourShader.SetUniform("uVisibleLayer", (float)visibleMaxLayer);
            RenderStates contourStates = new RenderStates(_contourShader);

            for (int i = 0; i < _visibleContourChunks.Count; i++)
            {
                HeightContourChunk chunk = _visibleContourChunks[i];

                if (chunk.Vertices.VertexCount > 0)
                    window.Draw(chunk.Vertices, contourStates);
            }
        }

        TrimContourChunkCache(
            worldMap,
            minRegionX,
            maxRegionX,
            minRegionY,
            maxRegionY,
            visibleCount + 8);
    }

    private HeightContourChunk BuildHeightContourChunk(
        WorldMap worldMap,
        float tilePixelSize,
        int regionX,
        int regionY,
        int lodStep)
    {
        HeightContourChunk chunk = new HeightContourChunk();

        int regionStartX = regionX * TerrainRegion.TilesPerSide;
        int regionStartY = regionY * TerrainRegion.TilesPerSide;
        int minX =
            ((regionStartX + lodStep - 1) / lodStep) * lodStep;
        int minY =
            ((regionStartY + lodStep - 1) / lodStep) * lodStep;
        int maxX = Math.Min(
            worldMap.MaxTileX - lodStep,
            regionStartX + TerrainRegion.TilesPerSide - 1);
        int maxY = Math.Min(
            worldMap.MaxTileY - lodStep,
            regionStartY + TerrainRegion.TilesPerSide - 1);

        if (maxX < minX || maxY < minY)
            return chunk;

        ushort maxHeightUnits = 0;

        for (int y = minY; y <= maxY + lodStep; y += lodStep)
        {
            for (int x = minX; x <= maxX + lodStep; x += lodStep)
            {
                ushort height = worldMap.GetSurfaceHeightUnits(x, y);

                if (height > maxHeightUnits)
                    maxHeightUnits = height;
            }
        }

        const int contourIntervalUnits = 50;
        bool allowLabels = _font != null && ((regionX + regionY * 2) % 3 == 0);
        Span<Vector2f> crossings = stackalloc Vector2f[4];

        for (int contourHeight = contourIntervalUnits;
             contourHeight <= maxHeightUnits;
             contourHeight += contourIntervalUnits)
        {
            HeightContourLevel level = new HeightContourLevel(
                contourHeight,
                _contourShader == null);

            bool isIndexContour =
                contourHeight % 100 == 0 ||
                contourHeight % 250 == 0;

            bool labelAdded = false;

            Color lineColor = isIndexContour
                ? new Color(105, 105, 105, 185)
                : new Color(48, 48, 48, 135);

            for (int y = minY; y <= maxY; y += lodStep)
            {
                for (int x = minX; x <= maxX; x += lodStep)
                {
                    ushort h00 = worldMap.GetSurfaceHeightUnits(x, y);
                    ushort h10 = worldMap.GetSurfaceHeightUnits(x + lodStep, y);
                    ushort h11 = worldMap.GetSurfaceHeightUnits(x + lodStep, y + lodStep);
                    ushort h01 = worldMap.GetSurfaceHeightUnits(x, y + lodStep);

                    float x0 = (x + 0.5f) * tilePixelSize;
                    float x1 = (x + lodStep + 0.5f) * tilePixelSize;
                    float y0 = (y + 0.5f) * tilePixelSize;
                    float y1 = (y + lodStep + 0.5f) * tilePixelSize;

                    int crossingCount = 0;

                    TryAddContourIntersection(
                        x0, y0, h00, x1, y0, h10, contourHeight,
                        crossings, ref crossingCount);
                    TryAddContourIntersection(
                        x1, y0, h10, x1, y1, h11, contourHeight,
                        crossings, ref crossingCount);
                    TryAddContourIntersection(
                        x1, y1, h11, x0, y1, h01, contourHeight,
                        crossings, ref crossingCount);
                    TryAddContourIntersection(
                        x0, y1, h01, x0, y0, h00, contourHeight,
                        crossings, ref crossingCount);

                    if (crossingCount < 2)
                        continue;

                    AppendContourSegment(
                        chunk,
                        level,
                        crossings[0],
                        crossings[1],
                        lineColor);

                    if (crossingCount >= 4)
                    {
                        AppendContourSegment(
                            chunk,
                            level,
                            crossings[2],
                            crossings[3],
                            lineColor);
                    }

                    if (!isIndexContour || !allowLabels || labelAdded)
                        continue;

                    Vector2f midpoint = new Vector2f(
                        (crossings[0].X + crossings[1].X) * 0.5f,
                        (crossings[0].Y + crossings[1].Y) * 0.5f);

                    Text label = new Text(
                        _font!,
                        $"{contourHeight * 0.1f:0} м",
                        9)
                    {
                        Position = midpoint,
                        FillColor = new Color(190, 190, 190, 235),
                        OutlineColor = Color.Black,
                        OutlineThickness = 1f,
                        Style = Text.Styles.Bold
                    };

                    level.Labels.Add(label);
                    labelAdded = true;
                }
            }

            if (level.Vertices.VertexCount > 0 || level.Labels.Count > 0)
                chunk.Levels.Add(level);
            else
                level.Dispose();
        }

        return chunk;
    }

    private void ClearContourChunkCache()
    {
        foreach (HeightContourChunk chunk in _contourChunks.Values)
            chunk.Dispose();

        _contourChunks.Clear();
        _visibleContourChunks.Clear();
    }

    private void TrimContourChunkCache(
        WorldMap worldMap,
        int minRegionX,
        int maxRegionX,
        int minRegionY,
        int maxRegionY,
        int targetCount)
    {
        while (_contourChunks.Count > targetCount)
        {
            int oldestKey = -1;
            long oldestFrame = long.MaxValue;

            foreach (KeyValuePair<int, HeightContourChunk> entry in _contourChunks)
            {
                int regionX = entry.Key % worldMap.RegionsX;
                int regionY = entry.Key / worldMap.RegionsX;

                if (regionX >= minRegionX && regionX <= maxRegionX &&
                    regionY >= minRegionY && regionY <= maxRegionY)
                {
                    continue;
                }

                if (entry.Value.LastUsedFrame < oldestFrame)
                {
                    oldestFrame = entry.Value.LastUsedFrame;
                    oldestKey = entry.Key;
                }
            }

            if (oldestKey < 0)
                break;

            _contourChunks[oldestKey].Dispose();
            _contourChunks.Remove(oldestKey);
        }
    }

    private static void TryAddContourIntersection(
        float xA,
        float yA,
        ushort heightA,
        float xB,
        float yB,
        ushort heightB,
        int contourHeight,
        Span<Vector2f> intersections,
        ref int count)
    {
        bool crosses =
            (heightA < contourHeight && heightB >= contourHeight) ||
            (heightB < contourHeight && heightA >= contourHeight);

        if (!crosses || heightA == heightB || count >= intersections.Length)
            return;

        float amount =
            (contourHeight - heightA) /
            (float)(heightB - heightA);

        Vector2f point = new Vector2f(
            xA + (xB - xA) * amount,
            yA + (yB - yA) * amount);

        for (int i = 0; i < count; i++)
        {
            if (MathF.Abs(intersections[i].X - point.X) < 0.001f &&
                MathF.Abs(intersections[i].Y - point.Y) < 0.001f)
            {
                return;
            }
        }

        intersections[count++] = point;
    }

    private void AppendContourSegment(
        HeightContourChunk chunk,
        HeightContourLevel level,
        Vector2f start,
        Vector2f end,
        Color color)
    {
        if (_contourShader != null)
        {
            Vector2f levelData = new Vector2f(level.HeightUnits, 0f);
            chunk.Vertices.Append(new Vertex(start, color, levelData));
            chunk.Vertices.Append(new Vertex(end, color, levelData));
        }

        if (level.FallbackVertices != null)
        {
            level.FallbackVertices.Append(new Vertex(start, color));
            level.FallbackVertices.Append(new Vertex(end, color));
        }
    }

    private void RefreshExtremes(WorldMap worldMap)
    {
        if (_cachedTerrainVersion == worldMap.TerrainVersion)
            return;

        _minimumHeightUnits = ushort.MaxValue;
        _maximumHeightUnits = 0;

        long centerX = worldMap.TileWidth / 2;
        long centerY = worldMap.TileHeight / 2;
        long minimumDistance = long.MaxValue;
        long maximumDistance = long.MaxValue;

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
                    (height == _minimumHeightUnits && distance < minimumDistance))
                {
                    _minimumHeightUnits = height;
                    _minimumX = x;
                    _minimumY = y;
                    minimumDistance = distance;
                }

                if (height > _maximumHeightUnits ||
                    (height == _maximumHeightUnits && distance < maximumDistance))
                {
                    _maximumHeightUnits = height;
                    _maximumX = x;
                    _maximumY = y;
                    maximumDistance = distance;
                }
            }
        }

        if (_minimumHeightUnits == ushort.MaxValue)
            _minimumHeightUnits = 0;

        if (_minimumElevationLabel != null)
        {
            _minimumElevationLabel.DisplayedString =
                $"MIN {_minimumHeightUnits * 0.1f:0.0} м";
        }

        if (_maximumElevationLabel != null)
        {
            _maximumElevationLabel.DisplayedString =
                $"MAX {_maximumHeightUnits * 0.1f:0.0} м";
        }

        _cachedTerrainVersion = worldMap.TerrainVersion;
    }

    public void Draw(
        RenderWindow window,
        uint width,
        uint height,
        GameMenuPage page,
        GameSettingsSnapshot settings,
        Vector2i mousePosition)
    {
        Layout(width, height, settings);

        _menuButton.FillColor = _menuButton.GetGlobalBounds().Contains(mousePosition)
            ? new Color(38, 47, 60, 250)
            : new Color(19, 25, 34, 245);

        window.Draw(_menuButton);
        if (_menuButtonLabel != null)
            window.Draw(_menuButtonLabel);

        if (page == GameMenuPage.Closed)
            return;

        _backdrop.Size = new Vector2f(width, height);
        window.Draw(_backdrop);

        if (page == GameMenuPage.Main)
        {
            DrawMainMenu(window, mousePosition);
            return;
        }

        DrawSettings(window, settings, mousePosition);
    }

    private void Layout(
        uint width,
        uint height,
        GameSettingsSnapshot settings)
    {
        if (_font == null)
            return;

        float centerX = width * 0.5f;
        float centerY = height * 0.5f;
        float mainX = centerX - MainWidth * 0.5f;
        float mainY = centerY - MainHeight * 0.5f;
        float settingsX = centerX - SettingsWidth * 0.5f;
        float settingsY = centerY - SettingsHeight * 0.5f;

        _mainPanel.Position = new Vector2f(mainX, mainY);
        _mainAccent.Position = new Vector2f(mainX + 1f, mainY + 1f);
        _mainTitle!.Position = new Vector2f(mainX + 24f, mainY + 23f);
        _mainSubtitle!.Position = new Vector2f(mainX + 24f, mainY + 51f);

        SetButtonLayout(_resumeButton, _resumeLabel!,
            mainX + 24f, mainY + 94f, MainWidth - 48f, 40f, "ПРОДОЛЖИТЬ");
        SetButtonLayout(_settingsButton, _settingsLabel!,
            mainX + 24f, mainY + 146f, MainWidth - 48f, 40f, "НАСТРОЙКИ");
        SetButtonLayout(_exitButton, _exitLabel!,
            mainX + 24f, mainY + 198f, MainWidth - 48f, 40f, "ВЫХОД");

        _panel.Position = new Vector2f(settingsX, settingsY);
        _accent.Position = new Vector2f(settingsX + 1f, settingsY + 1f);
        _settingsTitle!.Position = new Vector2f(settingsX + 22f, settingsY + 17f);
        _settingsSubtitle!.Position = new Vector2f(settingsX + 22f, settingsY + 46f);
        _closeButton.Position = new Vector2f(settingsX + SettingsWidth - 48f, settingsY + 15f);
        _closeLabel!.Position = new Vector2f(settingsX + SettingsWidth - 39f, settingsY + 13f);

        _resolutionHeading!.Position = new Vector2f(settingsX + 22f, settingsY + 80f);
        _resolutionPrevious.Position = new Vector2f(settingsX + 22f, settingsY + 102f);
        _resolutionBox.Position = new Vector2f(settingsX + 62f, settingsY + 102f);
        _resolutionNext.Position = new Vector2f(settingsX + 300f, settingsY + 102f);
        _applyResolution.Position = new Vector2f(settingsX + 356f, settingsY + 102f);
        _previousLabel!.Position = new Vector2f(settingsX + 31f, settingsY + 100f);
        _nextLabel!.Position = new Vector2f(settingsX + 309f, settingsY + 100f);
        _resolutionValue!.Position = new Vector2f(settingsX + 107f, settingsY + 111f);
        _applyResolutionLabel!.Position = new Vector2f(settingsX + 398f, settingsY + 113f);
        _resolutionCurrent!.Position = new Vector2f(settingsX + 22f, settingsY + 145f);

        _divider.Position = new Vector2f(settingsX + 18f, settingsY + 168f);

        float sliderStartX = settingsX + 22f;
        float sliderWidth = SettingsWidth - 44f;
        for (int i = 0; i < _sliders.Count; i++)
        {
            float trackY = settingsY + 214f + i * 50f;
            _sliders[i].Layout(sliderStartX, trackY, sliderWidth);
        }

        _gridButton.Position = new Vector2f(settingsX + 22f, settingsY + 442f);
        _extremaButton.Position = new Vector2f(settingsX + 210f, settingsY + 442f);
        _contoursButton.Position = new Vector2f(settingsX + 398f, settingsY + 442f);
        _gridLabel!.Position = new Vector2f(settingsX + 78f, settingsY + 451f);
        _extremaLabel!.Position = new Vector2f(settingsX + 258f, settingsY + 451f);
        _contoursLabel!.Position = new Vector2f(settingsX + 448f, settingsY + 451f);

        _toneHeading!.Position = new Vector2f(settingsX + 22f, settingsY + 488f);
        _toneLowLabel!.Position = new Vector2f(settingsX + 22f, settingsY + 520f);
        _toneHighLabel!.Position = new Vector2f(settingsX + SettingsWidth - 80f, settingsY + 520f);

        for (int i = 0; i < _toneSwatches.Length; i++)
        {
            _toneSwatches[i].Position = new Vector2f(
                settingsX + 22f + i * 112f,
                settingsY + 507f);
        }

        _resetButton.Position = new Vector2f(settingsX + 22f, settingsY + 565f);
        _backButton.Position = new Vector2f(settingsX + SettingsWidth - 150f, settingsY + 565f);
        _resetLabel!.Position = new Vector2f(settingsX + 38f, settingsY + 574f);
        _backLabel!.Position = new Vector2f(settingsX + SettingsWidth - 113f, settingsY + 574f);
        _keyboardHelp!.Position = new Vector2f(settingsX + 22f, settingsY + 544f);

        if (_lastSelectedResolutionIndex != settings.SelectedResolutionIndex)
        {
            _lastSelectedResolutionIndex = settings.SelectedResolutionIndex;
            SetText(
                _resolutionValue,
                ref _lastResolution,
                GetResolution(settings.SelectedResolutionIndex).ToString());
        }

        if (_lastCurrentWidth != settings.CurrentWidth ||
            _lastCurrentHeight != settings.CurrentHeight)
        {
            _lastCurrentWidth = settings.CurrentWidth;
            _lastCurrentHeight = settings.CurrentHeight;
            SetText(
                _resolutionCurrent,
                ref _lastAppliedResolution,
                $"Текущее окно: {settings.CurrentWidth} × {settings.CurrentHeight}");
        }

        SetText(
            _gridLabel,
            ref _lastGridLabel,
            settings.ShowGrid ? "СЕТКА: ВКЛ" : "СЕТКА: ВЫКЛ");

        SetText(
            _extremaLabel,
            ref _lastExtremaLabel,
            settings.ShowExtrema ? "МАРКЕРЫ: ВКЛ" : "МАРКЕРЫ: ВЫКЛ");

        SetText(
            _contoursLabel,
            ref _lastContoursLabel,
            settings.ShowHeightContours ? "ГОРИЗОНТАЛИ: ВКЛ" : "ГОРИЗОНТАЛИ: ВЫКЛ");

        for (int i = 0; i < _toneSwatches.Length; i++)
        {
            _toneSwatches[i].FillColor =
                TerrainHeightPalette.GetHeightPreviewColor(
                    i, settings.BaseGray, settings.HeightContrast);
        }

    }

    private void DrawMainMenu(RenderWindow window, Vector2i mouse)
    {
        UpdateHover(_resumeButton, mouse);
        UpdateHover(_settingsButton, mouse);
        UpdateHover(_exitButton, mouse);

        window.Draw(_mainPanel);
        window.Draw(_mainAccent);
        window.Draw(_resumeButton);
        window.Draw(_settingsButton);
        window.Draw(_exitButton);

        DrawText(window, _mainMenuTexts);
    }

    private void DrawSettings(
        RenderWindow window,
        GameSettingsSnapshot settings,
        Vector2i mouse)
    {
        UpdateHover(_closeButton, mouse);
        UpdateHover(_resolutionPrevious, mouse);
        UpdateHover(_resolutionNext, mouse);
        UpdateHover(_applyResolution, mouse);
        UpdateHover(_gridButton, mouse);
        UpdateHover(_extremaButton, mouse);
        UpdateHover(_contoursButton, mouse);
        UpdateHover(_resetButton, mouse);
        UpdateHover(_backButton, mouse);

        _gridButton.FillColor = settings.ShowGrid
            ? new Color(63, 75, 90)
            : new Color(27, 34, 44);
        _extremaButton.FillColor = settings.ShowExtrema
            ? new Color(63, 75, 90)
            : new Color(27, 34, 44);
        _contoursButton.FillColor = settings.ShowHeightContours
            ? new Color(63, 75, 90)
            : new Color(27, 34, 44);

        window.Draw(_panel);
        window.Draw(_accent);
        window.Draw(_closeButton);
        window.Draw(_resolutionPrevious);
        window.Draw(_resolutionBox);
        window.Draw(_resolutionNext);
        window.Draw(_applyResolution);
        window.Draw(_divider);
        window.Draw(_gridButton);
        window.Draw(_extremaButton);
        window.Draw(_contoursButton);
        window.Draw(_resetButton);
        window.Draw(_backButton);

        for (int i = 0; i < _sliders.Count; i++)
        {
            float current = _sliders[i].ActionType switch
            {
                GameMenuActionType.SetBaseGray => settings.BaseGray,
                GameMenuActionType.SetHeightContrast => settings.HeightContrast,
                GameMenuActionType.SetLayer => settings.VisibleMaxLayer,
                _ => 0f
            };

            float maximum = _sliders[i].MaxValue;
            if (_sliders[i].ActionType == GameMenuActionType.SetLayer)
                maximum = Math.Max(0, settings.LayerCount - 1);

            _sliders[i].UpdateValue(current, settings.LayerCount);

            _sliders[i].Draw(window, current, maximum);
        }

        for (int i = 0; i < _toneSwatches.Length; i++)
            window.Draw(_toneSwatches[i]);

        if (_font == null)
            return;

        DrawText(window, _settingsTexts);
    }

    public bool IsSettingsPanelHit(Vector2i point, GameMenuPage page) =>
        page == GameMenuPage.Settings && _panel.GetGlobalBounds().Contains(point);

    public void SetExtremes(
        ushort minimumHeightUnits,
        int minimumX,
        int minimumY,
        ushort maximumHeightUnits,
        int maximumX,
        int maximumY)
    {
        _minimumHeightUnits = minimumHeightUnits;
        _minimumX = minimumX;
        _minimumY = minimumY;
        _maximumHeightUnits = maximumHeightUnits;
        _maximumX = maximumX;
        _maximumY = maximumY;
    }

    private static RectangleShape ButtonShape(
        float width = 0f,
        float height = 0f)
    {
        if (width <= 0f)
            width = 220f;
        if (height <= 0f)
            height = 40f;

        return new RectangleShape(new Vector2f(width, height))
        {
            FillColor = new Color(28, 36, 47),
            OutlineColor = new Color(74, 88, 106),
            OutlineThickness = 1f
        };
    }

    private static void UpdateHover(RectangleShape button, Vector2i mouse)
    {
        if (button.GetGlobalBounds().Contains(mouse))
            button.FillColor = new Color(48, 60, 76);
        else
            button.FillColor = new Color(28, 36, 47);
    }

    private void SetButtonLayout(
        RectangleShape button,
        Text label,
        float x,
        float y,
        float width,
        float height,
        string text)
    {
        button.Position = new Vector2f(x, y);
        button.Size = new Vector2f(width, height);
        if (label.DisplayedString != text)
            label.DisplayedString = text;

        FloatRect bounds = label.GetLocalBounds();
        label.Position = new Vector2f(
            x + (width - bounds.Size.X) * 0.5f - bounds.Position.X,
            y + (height - bounds.Size.Y) * 0.5f - bounds.Position.Y - 1f);
    }

    private void DrawText(RenderWindow window, params Text?[] texts)
    {
        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i] != null)
                window.Draw(texts[i]!);
        }

    }

    private Text CreateText(
        string value,
        uint size,
        Color color,
        Vector2f position,
        Text.Styles style = Text.Styles.Regular)
    {
        Text text = new Text(_font!, value, size)
        {
            Position = position,
            FillColor = color,
            OutlineColor = new Color(0, 0, 0, 160),
            OutlineThickness = 0.5f,
            Style = style
        };

        _texts.Add(text);
        return text;
    }

    private static void SetText(Text? text, ref string previous, string value)
    {
        if (text == null || previous == value)
            return;

        text.DisplayedString = value;
        previous = value;
    }

    private static Font? LoadFont()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates = { "segoeui.ttf", "arial.ttf", "tahoma.ttf" };

        for (int i = 0; i < candidates.Length; i++)
        {
            string path = Path.Combine(folder, candidates[i]);
            if (!File.Exists(path))
                continue;

            try
            {
                return new Font(path);
            }
            catch
            {
                // Try next installed font.
            }
        }

        return null;
    }

    public void Dispose()
    {
        for (int i = 0; i < _texts.Count; i++)
            _texts[i].Dispose();

        ClearContourChunkCache();

        for (int i = 0; i < _sliders.Count; i++)
            _sliders[i].Dispose();

        _font?.Dispose();
        _backdrop.Dispose();
        _menuButton.Dispose();
        _panel.Dispose();
        _accent.Dispose();
        _mainPanel.Dispose();
        _mainAccent.Dispose();
        _resumeButton.Dispose();
        _settingsButton.Dispose();
        _exitButton.Dispose();
        _closeButton.Dispose();
        _backButton.Dispose();
        _resetButton.Dispose();
        _resolutionPrevious.Dispose();
        _resolutionNext.Dispose();
        _applyResolution.Dispose();
        _gridButton.Dispose();
        _extremaButton.Dispose();
        _contoursButton.Dispose();
        _resolutionBox.Dispose();
        _divider.Dispose();
        _minimumPin.Dispose();
        _maximumPin.Dispose();
        _contourShader?.Dispose();
        _contourShader = null;

        for (int i = 0; i < _toneSwatches.Length; i++)
            _toneSwatches[i].Dispose();
    }

    private sealed class SliderControl : IDisposable
    {
        private readonly RectangleShape _track =
            new RectangleShape(new Vector2f(100f, 4f))
            {
                FillColor = new Color(65, 74, 86)
            };

        private readonly RectangleShape _fill =
            new RectangleShape(new Vector2f(0f, 4f))
            {
                FillColor = new Color(175, 184, 196)
            };

        private readonly CircleShape _knob = new CircleShape(6f, 24)
        {
            Origin = new Vector2f(6f, 6f),
            FillColor = new Color(241, 244, 248),
            OutlineColor = new Color(106, 120, 137),
            OutlineThickness = 1.5f
        };

        private readonly Text? _label;
        private readonly Text? _value;
        private float _startX;
        private float _trackY;
        private float _width;
        private float _lastValue = float.NaN;
        private int _lastLayerCount = -1;

        public GameMenuActionType ActionType { get; }
        public float MinValue { get; }
        public float MaxValue { get; }

        public SliderControl(
            Font? font,
            GameMenuActionType actionType,
            string label,
            float minValue,
            float maxValue)
        {
            ActionType = actionType;
            MinValue = minValue;
            MaxValue = maxValue;

            _label = font == null ? null : new Text(font, label, 12)
            {
                FillColor = new Color(211, 219, 228)
            };

            _value = font == null ? null : new Text(font, "", 12)
            {
                FillColor = new Color(235, 239, 245),
                Style = Text.Styles.Bold
            };
        }

        public void Layout(float startX, float trackY, float width)
        {
            _startX = startX;
            _trackY = trackY;
            _width = width;
            _track.Position = new Vector2f(startX, trackY);
            _fill.Position = new Vector2f(startX, trackY);

            if (_label != null)
                _label.Position = new Vector2f(startX, trackY - 22f);

            if (_value != null)
            {
                FloatRect bounds = _value.GetLocalBounds();
                _value.Position = new Vector2f(
                    startX + width - bounds.Size.X,
                    trackY - 22f);
            }
        }

        public bool HitTest(Vector2i point)
        {
            FloatRect bounds = new FloatRect(
                new Vector2f(_startX - 8f, _trackY - 15f),
                new Vector2f(_width + 16f, 32f));

            return bounds.Contains(point);
        }

        public float ValueFromX(int screenX, float maximum)
        {
            if (maximum <= MinValue)
                return MinValue;

            float amount = Math.Clamp((screenX - _startX) / _width, 0f, 1f);
            return MinValue + (maximum - MinValue) * amount;
        }

        public void Draw(RenderWindow window, float current, float maximum)
        {
            float range = Math.Max(0.0001f, maximum - MinValue);
            float amount = Math.Clamp((current - MinValue) / range, 0f, 1f);
            _fill.Size = new Vector2f(amount * _width, 4f);
            _knob.Position = new Vector2f(_startX + amount * _width, _trackY + 2f);

            window.Draw(_track);
            window.Draw(_fill);
            window.Draw(_knob);

            if (_label != null)
                window.Draw(_label);
            if (_value != null)
                window.Draw(_value);
        }

        public void UpdateValue(float current, int layerCount)
        {
            if (_value == null ||
                (_lastValue == current && _lastLayerCount == layerCount))
            {
                return;
            }

            _lastValue = current;
            _lastLayerCount = layerCount;

            _value.DisplayedString = ActionType switch
            {
                GameMenuActionType.SetBaseGray => $"{current:0} / 96",
                GameMenuActionType.SetHeightContrast => $"{current:0} / 32",
                GameMenuActionType.SetLayer => $"{current * 0.1f:0.0} / {(layerCount - 1) * 0.1f:0.0} м",
                _ => current.ToString("0.0")
            };
        }

        public void Dispose()
        {
            _track.Dispose();
            _fill.Dispose();
            _knob.Dispose();
            _label?.Dispose();
            _value?.Dispose();
        }
    }
}
