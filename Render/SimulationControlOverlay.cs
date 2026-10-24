using System;
using System.IO;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public enum SimulationControlAction
{
    TogglePause,
    Step,
    Slower,
    Normal,
    Faster
}

public sealed class SimulationControlOverlay : IDisposable
{
    private const float Width = 430f;
    private const float Height = 108f;
    private const float HeaderHeight = 28f;
    private const float ButtonHeight = 26f;
    private const float ButtonGap = 5f;

    private static readonly float[] ButtonWidths = { 100f, 55f, 78f, 48f, 78f };
    private static readonly SimulationControlAction[] Actions =
    {
        SimulationControlAction.TogglePause,
        SimulationControlAction.Step,
        SimulationControlAction.Slower,
        SimulationControlAction.Normal,
        SimulationControlAction.Faster
    };

    private readonly RectangleShape _panel = new(new Vector2f(Width, Height))
    {
        FillColor = new Color(7, 12, 19, 238),
        OutlineColor = new Color(76, 99, 123, 245),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _header = new(new Vector2f(Width - 2f, HeaderHeight))
    {
        FillColor = new Color(27, 39, 52, 255)
    };
    private readonly RectangleShape[] _buttons = new RectangleShape[5];
    private readonly Text?[] _buttonLabels = new Text?[5];
    private readonly Font? _font;
    private readonly Text? _title;
    private readonly Text? _status;

    private float _x = 548f;
    private float _y = 12f;
    private float _dragOffsetX;
    private float _dragOffsetY;
    private bool _hasPosition;
    private bool _dragging;

    public SimulationControlOverlay()
    {
        _font = LoadFont();
        if (_font == null)
            return;

        _title = new Text(_font, "ВРЕМЯ СИМУЛЯЦИИ", 12)
        {
            FillColor = new Color(239, 245, 252),
            Style = Text.Styles.Bold
        };
        _status = new Text(_font, "", 10)
        {
            FillColor = new Color(181, 199, 218)
        };

        string[] labels = { "ПАУЗА", "ШАГ", "МЕДЛЕННЕЕ", "1×", "БЫСТРЕЕ" };
        for (int i = 0; i < _buttons.Length; i++)
        {
            _buttons[i] = new RectangleShape(new Vector2f(ButtonWidths[i], ButtonHeight))
            {
                FillColor = new Color(32, 43, 56, 255),
                OutlineColor = new Color(78, 100, 123, 230),
                OutlineThickness = 0.8f
            };
            _buttonLabels[i] = new Text(_font, labels[i], 10)
            {
                FillColor = new Color(226, 234, 244, 255),
                Style = Text.Styles.Bold
            };
        }
    }

    public bool Contains(Vector2i position) =>
        _panel.GetGlobalBounds().Contains(position);

    public bool HandleMousePressed(
        Vector2i position,
        uint windowWidth,
        uint windowHeight,
        bool paused,
        Action<SimulationControlAction> activate)
    {
        if (_font == null || _title == null || _status == null)
            return false;

        Layout(windowWidth, windowHeight);
        if (!_panel.GetGlobalBounds().Contains(position))
            return false;

        if (_header.GetGlobalBounds().Contains(position))
        {
            _dragging = true;
            _dragOffsetX = position.X - _panel.Position.X;
            _dragOffsetY = position.Y - _panel.Position.Y;
            return true;
        }

        for (int i = 0; i < _buttons.Length; i++)
        {
            if (!_buttons[i].GetGlobalBounds().Contains(position))
                continue;

            if (i != 1 || paused)
                activate(Actions[i]);
            return true;
        }

        return true;
    }

    public bool HandleMouseMoved(Vector2i position, uint windowWidth, uint windowHeight)
    {
        if (!_dragging)
            return false;

        _x = Math.Clamp(position.X - _dragOffsetX, 8f, Math.Max(8f, windowWidth - Width - 8f));
        _y = Math.Clamp(position.Y - _dragOffsetY, 8f, Math.Max(8f, windowHeight - Height - 8f));
        Layout(windowWidth, windowHeight);
        return true;
    }

    public bool HandleMouseReleased()
    {
        if (!_dragging)
            return false;

        _dragging = false;
        return true;
    }

    public void Draw(
        RenderWindow window,
        uint windowWidth,
        uint windowHeight,
        bool paused,
        float speed,
        double simulationRate,
        double updatesPerSecond,
        Vector2i mousePosition)
    {
        if (_font == null || _title == null || _status == null)
            return;

        Layout(windowWidth, windowHeight);
        _title.Position = new Vector2f(_panel.Position.X + 12f, _panel.Position.Y + 7f);
        _status.DisplayedString =
            $"{(paused ? "ПАУЗА" : "РАБОТА")}  ·  скорость {speed:0.00}×  ·  темп {simulationRate:0.00}×/с  ·  {updatesPerSecond:0} UPS";
        _status.FillColor = paused
            ? new Color(242, 205, 124, 255)
            : new Color(166, 211, 187, 255);
        _status.Position = new Vector2f(_panel.Position.X + 12f, _panel.Position.Y + 38f);

        window.Draw(_panel);
        window.Draw(_header);
        window.Draw(_title);
        window.Draw(_status);

        string[] labels =
        {
            paused ? "ПРОДОЛЖИТЬ" : "ПАУЗА",
            "ШАГ",
            "МЕДЛЕННЕЕ",
            "1×",
            "БЫСТРЕЕ"
        };

        for (int i = 0; i < _buttons.Length; i++)
        {
            bool hovered = _buttons[i].GetGlobalBounds().Contains(mousePosition);
            bool disabled = i == 1 && !paused;
            _buttons[i].FillColor = disabled
                ? new Color(25, 30, 37, 255)
                : hovered
                    ? new Color(54, 76, 98, 255)
                    : i == 0 && paused
                        ? new Color(40, 76, 60, 255)
                        : new Color(32, 43, 56, 255);
            _buttons[i].OutlineColor = hovered && !disabled
                ? new Color(128, 174, 213, 255)
                : new Color(78, 100, 123, 230);
            window.Draw(_buttons[i]);

            Text? label = _buttonLabels[i];
            if (label == null)
                continue;

            label.DisplayedString = labels[i];
            label.FillColor = disabled
                ? new Color(111, 121, 133, 255)
                : new Color(226, 234, 244, 255);
            FloatRect bounds = label.GetLocalBounds();
            label.Position = new Vector2f(
                _buttons[i].Position.X + (_buttons[i].Size.X - bounds.Width) * 0.5f - bounds.Left,
                _buttons[i].Position.Y + (_buttons[i].Size.Y - bounds.Height) * 0.5f - bounds.Top - 1f);
            window.Draw(label);
        }
    }

    private void Layout(uint windowWidth, uint windowHeight)
    {
        if (!_hasPosition)
        {
            _x = Math.Clamp(_x, 8f, Math.Max(8f, windowWidth - Width - 8f));
            _y = Math.Clamp(_y, 8f, Math.Max(8f, windowHeight - Height - 8f));
            _hasPosition = true;
        }

        _x = Math.Clamp(_x, 8f, Math.Max(8f, windowWidth - Width - 8f));
        _y = Math.Clamp(_y, 8f, Math.Max(8f, windowHeight - Height - 8f));
        _panel.Position = new Vector2f(_x, _y);
        _header.Position = new Vector2f(_x + 1f, _y + 1f);

        float x = _x + 12f;
        float y = _y + 70f;
        for (int i = 0; i < _buttons.Length; i++)
        {
            _buttons[i].Position = new Vector2f(x, y);
            x += ButtonWidths[i] + ButtonGap;
        }
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
                // Try another installed font.
            }
        }

        return null;
    }

    public void Dispose()
    {
        for (int i = 0; i < _buttonLabels.Length; i++)
        {
            _buttonLabels[i]?.Dispose();
            _buttons[i]?.Dispose();
        }

        _status?.Dispose();
        _title?.Dispose();
        _font?.Dispose();
        _header.Dispose();
        _panel.Dispose();
    }
}
