using System;
using System.IO;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

/// <summary>
/// One clear squad-order toggle: Move at will or Hold position.
/// It applies to the current player selection; explicit move orders remain authoritative.
/// </summary>
public sealed class UnitMovementModeOverlay : IDisposable
{
    private const float Width = 270f;
    private const float Height = 62f;

    private readonly RectangleShape _panel = new(new Vector2f(Width, Height))
    {
        FillColor = new Color(12, 18, 25, 238),
        OutlineColor = new Color(83, 104, 126, 245),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _button = new(new Vector2f(Width - 12f, 29f))
    {
        FillColor = new Color(38, 72, 56, 255),
        OutlineColor = new Color(91, 125, 103, 240),
        OutlineThickness = 1f
    };

    private readonly Font? _font;
    private readonly Text? _title;
    private readonly Text? _mode;
    private float _x = 12f;
    private float _y;
    private bool _visible;

    public UnitMovementModeOverlay()
    {
        _font = LoadFont();
        if (_font == null)
            return;

        _title = new Text(_font, "", 10)
        {
            FillColor = new Color(176, 191, 207),
            Style = Text.Styles.Bold
        };
        _mode = new Text(_font, "", 12)
        {
            FillColor = new Color(235, 242, 248),
            Style = Text.Styles.Bold
        };
    }

    public bool Contains(Vector2i position) =>
        _visible && _panel.GetGlobalBounds().Contains(position);

    public bool HandleMousePressed(
        Vector2i position,
        bool hasSelection,
        Action toggle)
    {
        if (!hasSelection || !_visible ||
            !_panel.GetGlobalBounds().Contains(position))
        {
            return false;
        }

        toggle();
        return true;
    }

    public void Draw(
        RenderWindow window,
        uint windowWidth,
        uint windowHeight,
        int selectedCount,
        bool holdPosition,
        Vector2i mousePosition)
    {
        _visible = selectedCount > 0 && _font != null &&
            _title != null && _mode != null;
        if (!_visible)
            return;

        Layout(windowWidth, windowHeight);
        bool hovered = _panel.GetGlobalBounds().Contains(mousePosition);

        _panel.FillColor = hovered
            ? new Color(24, 34, 45, 248)
            : new Color(12, 18, 25, 238);
        _button.FillColor = holdPosition
            ? hovered ? new Color(105, 82, 44, 255) : new Color(75, 60, 37, 255)
            : hovered ? new Color(53, 105, 79, 255) : new Color(38, 72, 56, 255);
        _button.OutlineColor = hovered
            ? new Color(159, 190, 216, 255)
            : new Color(91, 125, 103, 240);

        _title!.DisplayedString = $"РЕЖИМ ДВИЖЕНИЯ  ·  ВЫБРАНО: {selectedCount}";
        _title.Position = new Vector2f(_x + 10f, _y + 5f);

        _mode!.DisplayedString = holdPosition
            ? "[F]  УДЕРЖИВАТЬ ПОЗИЦИЮ"
            : "[F]  СВОБОДНОЕ ПЕРЕДВИЖЕНИЕ";
        FloatRect bounds = _mode.GetLocalBounds();
        _mode.Position = new Vector2f(
            _button.Position.X + (_button.Size.X - bounds.Width) * 0.5f - bounds.Left,
            _button.Position.Y + (_button.Size.Y - bounds.Height) * 0.5f - bounds.Top - 1f);

        window.Draw(_panel);
        window.Draw(_button);
        window.Draw(_title);
        window.Draw(_mode);
    }

    private void Layout(uint windowWidth, uint windowHeight)
    {
        _x = Math.Clamp(_x, 8f, Math.Max(8f, windowWidth - Width - 8f));
        _y = Math.Clamp(windowHeight - Height - 12f, 8f,
            Math.Max(8f, windowHeight - Height - 8f));
        _panel.Position = new Vector2f(_x, _y);
        _button.Position = new Vector2f(_x + 6f, _y + 27f);
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
                // Try the next installed system font.
            }
        }

        return null;
    }

    public void Dispose()
    {
        _mode?.Dispose();
        _title?.Dispose();
        _font?.Dispose();
        _button.Dispose();
        _panel.Dispose();
    }
}
