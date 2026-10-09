using System;
using System.Collections.Generic;
using System.IO;
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
    SetDepthShade,
    SetLayerOffset,
    SetLayer,
    ToggleGrid,
    ToggleExtrema,
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
    float BaseGray,
    float HeightContrast,
    float DepthShade,
    float LayerOffset,
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
    {
        new(1280, 720),
        new(1366, 768),
        new(1600, 900),
        new(1920, 1080),
        new(2560, 1440)
    };

    private const float MenuButtonSize = 38f;
    private const float SettingsWidth = 620f;
    private const float SettingsHeight = 620f;
    private const float MainWidth = 390f;
    private const float MainHeight = 310f;

    private readonly Font? _font;
    private readonly List<Text> _texts = new();
    private readonly List<SliderControl> _sliders = new();

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
    private readonly RectangleShape _gridButton = ButtonShape(226f, 34f);
    private readonly RectangleShape _extremaButton = ButtonShape(226f, 34f);

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
    private readonly Text? _toneHeading;
    private readonly Text? _toneLowLabel;
    private readonly Text? _toneHighLabel;
    private readonly Text? _resetLabel;
    private readonly Text? _backLabel;
    private readonly Text? _keyboardHelp;

    private string _lastGridLabel = string.Empty;
    private string _lastExtremaLabel = string.Empty;
    private string _lastResolution = string.Empty;
    private string _lastAppliedResolution = string.Empty;
    private string _lastTerrainLow = string.Empty;
    private string _lastTerrainHigh = string.Empty;

    public static int ResolutionCount => SupportedResolutions.Length;

    public static GameResolution GetResolution(int index) =>
        SupportedResolutions[Math.Clamp(index, 0, SupportedResolutions.Length - 1)];

    public GameMenuOverlay()
    {
        _font = LoadFont();

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
            _font, GameMenuActionType.SetDepthShade,
            "Затемнение нижних слоёв", 0f, 50f));
        _sliders.Add(new SliderControl(
            _font, GameMenuActionType.SetLayerOffset,
            "Расстояние между Z-слоями", 0f, 3f));
        _sliders.Add(new SliderControl(
            _font, GameMenuActionType.SetLayer,
            "Верхняя граница среза Z", 0f, 49f));

        if (_font == null)
        {
            Console.WriteLine(
                "[GameMenu] Не найден системный шрифт. Кнопки останутся, но подписи будут недоступны.");
            return;
        }

        _menuButtonLabel = CreateText(
            "•••", 16, Color.White, new Vector2f(21f, 17f), Text.Styles.Bold);

        _mainTitle = CreateText(
            "ГЛАВНОЕ МЕНЮ", 19, new Color(241, 245, 250),
            Vector2f.Zero, Text.Styles.Bold);
        _mainSubtitle = CreateText(
            "RIMCLONE  •  ТЕСТ РЕЛЬЕФА", 11, new Color(154, 168, 185),
            Vector2f.Zero);
        _resumeLabel = CreateText(
            "ПРОДОЛЖИТЬ", 13, Color.White, Vector2f.Zero, Text.Styles.Bold);
        _settingsLabel = CreateText(
            "НАСТРОЙКИ", 13, Color.White, Vector2f.Zero, Text.Styles.Bold);
        _exitLabel = CreateText(
            "ВЫХОД", 13, Color.White, Vector2f.Zero, Text.Styles.Bold);

        _settingsTitle = CreateText(
            "НАСТРОЙКИ", 19, new Color(241, 245, 250),
            Vector2f.Zero, Text.Styles.Bold);
        _settingsSubtitle = CreateText(
            "Графика и окно игры", 11, new Color(154, 168, 185), Vector2f.Zero);
        _closeLabel = CreateText(
            "×", 19, Color.White, Vector2f.Zero);
        _resolutionHeading = CreateText(
            "РАЗРЕШЕНИЕ ОКНА", 12, new Color(216, 224, 234),
            Vector2f.Zero, Text.Styles.Bold);
        _resolutionValue = CreateText(
            "", 13, Color.White, Vector2f.Zero, Text.Styles.Bold);
        _resolutionCurrent = CreateText(
            "", 10, new Color(154, 168, 185), Vector2f.Zero);
        _previousLabel = CreateText(
            "‹", 22, Color.White, Vector2f.Zero);
        _nextLabel = CreateText(
            "›", 22, Color.White, Vector2f.Zero);
        _applyResolutionLabel = CreateText(
            "ПРИМЕНИТЬ", 11, Color.White, Vector2f.Zero, Text.Styles.Bold);
        _gridLabel = CreateText(
            "", 11, Color.White, Vector2f.Zero, Text.Styles.Bold);
        _extremaLabel = CreateText(
            "", 11, Color.White, Vector2f.Zero, Text.Styles.Bold);
        _toneHeading = CreateText(
            "ПРЕВЬЮ МОНОХРОМНОГО ТОНА", 11,
            new Color(195, 204, 215), Vector2f.Zero, Text.Styles.Bold);
        _toneLowLabel = CreateText(
            "НИЗИНА", 10, new Color(157, 168, 182), Vector2f.Zero);
        _toneHighLabel = CreateText(
            "ВЫСОТА", 10, new Color(205, 213, 222), Vector2f.Zero);
        _resetLabel = CreateText(
            "СБРОСИТЬ ГРАФИКУ", 11, Color.White, Vector2f.Zero, Text.Styles.Bold);
        _backLabel = CreateText(
            "НАЗАД", 11, Color.White, Vector2f.Zero, Text.Styles.Bold);
        _keyboardHelp = CreateText(
            "F2 — меню настроек     Колесо — масштаб     WASD — камера",
            10, new Color(145, 157, 173), Vector2f.Zero);
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
        int selectedResolutionIndex,
        int visibleMaxLayer,
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

    public bool IsModalOpen(GameMenuPage page) =>
        page != GameMenuPage.Closed;

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
        float[] trackYs =
        {
            settingsY + 214f,
            settingsY + 264f,
            settingsY + 314f,
            settingsY + 364f,
            settingsY + 414f
        };

        for (int i = 0; i < _sliders.Count; i++)
            _sliders[i].Layout(sliderStartX, trackYs[i], sliderWidth);

        _gridButton.Position = new Vector2f(settingsX + 22f, settingsY + 442f);
        _extremaButton.Position = new Vector2f(settingsX + 260f, settingsY + 442f);
        _gridLabel!.Position = new Vector2f(settingsX + 56f, settingsY + 451f);
        _extremaLabel!.Position = new Vector2f(settingsX + 289f, settingsY + 451f);

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
        _keyboardHelp!.Position = new Vector2f(settingsX + 22f, settingsY + SettingsHeight - 25f);

        SetText(
            _resolutionValue,
            ref _lastResolution,
            GetResolution(settings.SelectedResolutionIndex).ToString());

        SetText(
            _resolutionCurrent,
            ref _lastAppliedResolution,
            $"Текущее окно: {settings.CurrentWidth} × {settings.CurrentHeight}");

        SetText(
            _gridLabel,
            ref _lastGridLabel,
            settings.ShowGrid ? "СЕТКА: ВКЛ" : "СЕТКА: ВЫКЛ");

        SetText(
            _extremaLabel,
            ref _lastExtremaLabel,
            settings.ShowExtrema ? "МАРКЕРЫ: ВКЛ" : "МАРКЕРЫ: ВЫКЛ");

        for (int i = 0; i < _toneSwatches.Length; i++)
        {
            _toneSwatches[i].FillColor =
                TerrainHeightPalette.GetHeightPreviewColor(
                    i, settings.BaseGray, settings.HeightContrast);
        }

        if (_minimumText != null && _maximumText != null)
        {
            SetText(
                _minimumText,
                ref _lastTerrainLow,
                $"НИЗИНА  {_minimumHeightUnits * 0.1f:0.#} м  •  X={_minimumX}, Y={_minimumY}");

            SetText(
                _maximumText,
                ref _lastTerrainHigh,
                $"ВЕРШИНА  {_maximumHeightUnits * 0.1f:0.#} м  •  X={_maximumX}, Y={_maximumY}");
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

        DrawText(window, _mainTitle, _mainSubtitle, _resumeLabel, _settingsLabel, _exitLabel);
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
        UpdateHover(_resetButton, mouse);
        UpdateHover(_backButton, mouse);

        _gridButton.FillColor = settings.ShowGrid
            ? new Color(63, 75, 90)
            : new Color(27, 34, 44);
        _extremaButton.FillColor = settings.ShowExtrema
            ? new Color(63, 75, 90)
            : new Color(27, 34, 44);

        window.Draw(_panel);
        window.Draw(_accent);
        window.Draw(_closeButton);
        window.Draw(_resolutionHeading!);
        window.Draw(_resolutionPrevious);
        window.Draw(_resolutionBox);
        window.Draw(_resolutionNext);
        window.Draw(_applyResolution);
        window.Draw(_divider);
        window.Draw(_gridButton);
        window.Draw(_extremaButton);
        window.Draw(_resetButton);
        window.Draw(_backButton);

        for (int i = 0; i < _sliders.Count; i++)
        {
            float current = _sliders[i].ActionType switch
            {
                GameMenuActionType.SetBaseGray => settings.BaseGray,
                GameMenuActionType.SetHeightContrast => settings.HeightContrast,
                GameMenuActionType.SetDepthShade => settings.DepthShade,
                GameMenuActionType.SetLayerOffset => settings.LayerOffset,
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

        DrawText(
            window,
            _settingsTitle,
            _settingsSubtitle,
            _closeLabel,
            _resolutionValue,
            _resolutionCurrent,
            _previousLabel,
            _nextLabel,
            _applyResolutionLabel,
            _gridLabel,
            _extremaLabel,
            _toneHeading,
            _toneLowLabel,
            _toneHighLabel,
            _minimumText,
            _maximumText,
            _resetLabel,
            _backLabel,
            _keyboardHelp);
    }

    public bool IsMenuButtonHit(Vector2i point) =>
        _menuButton.GetGlobalBounds().Contains(point);

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
        _resolutionBox.Dispose();
        _divider.Dispose();

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
            if (_value == null)
                return;

            _value.DisplayedString = ActionType switch
            {
                GameMenuActionType.SetBaseGray => $"{current:0} / 96",
                GameMenuActionType.SetHeightContrast => $"{current:0} / 32",
                GameMenuActionType.SetDepthShade => $"{current:0} / 50",
                GameMenuActionType.SetLayerOffset => $"{current:0.0} px/Z",
                GameMenuActionType.SetLayer => $"{(int)current + 1} / {layerCount} м",
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
