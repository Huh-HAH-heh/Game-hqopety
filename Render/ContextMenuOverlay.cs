using System;
using System.IO;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public sealed class ContextMenuOverlay : IDisposable
{
    private const float Width = 196f;
    private const float Height = 58f;

    private readonly RectangleShape _panel = new(new Vector2f(Width, Height))
    {
        FillColor = new Color(19, 25, 34, 250),
        OutlineColor = new Color(83, 101, 124, 245),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _header = new(new Vector2f(Width - 2f, 24f))
    {
        FillColor = new Color(34, 43, 56, 255)
    };

    private readonly RectangleShape _accent = new(new Vector2f(2f, Height - 2f))
    {
        FillColor = new Color(125, 170, 220, 220)
    };

    private readonly Font? _font;
    private readonly Text? _title;
    private readonly Text? _emptyLabel;

    public bool IsOpen { get; private set; }

    public ContextMenuOverlay()
    {
        _font = LoadFont();

        if (_font == null)
            return;

        _title = new Text(_font, "Контекстное меню", 12)
        {
            Position = new Vector2f(10f, 5f),
            FillColor = new Color(232, 237, 244)
        };

        _emptyLabel = new Text(_font, "Пока пусто", 11)
        {
            Position = new Vector2f(10f, 32f),
            FillColor = new Color(148, 159, 174)
        };
    }

    public void ToggleAt(Vector2i position, uint windowWidth, uint windowHeight)
    {
        if (IsOpen && _panel.GetGlobalBounds().Contains(position))
        {
            Close();
            return;
        }

        OpenAt(position, windowWidth, windowHeight);
    }

    public void OpenAt(Vector2i position, uint windowWidth, uint windowHeight)
    {
        float x = Math.Clamp(position.X, 0f, Math.Max(0f, windowWidth - Width));
        float y = Math.Clamp(position.Y, 0f, Math.Max(0f, windowHeight - Height));

        _panel.Position = new Vector2f(x, y);
        _header.Position = new Vector2f(x + 1f, y + 1f);
        _accent.Position = new Vector2f(x + 1f, y + 1f);
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void Draw(RenderWindow window, uint windowWidth, uint windowHeight)
    {
        if (!IsOpen)
            return;

        // Keep the popup on-screen if the window is resized while it is open.
        Vector2f position = _panel.Position;
        OpenAt(
            new Vector2i((int)position.X, (int)position.Y),
            windowWidth,
            windowHeight);

        window.Draw(_panel);
        window.Draw(_header);
        window.Draw(_accent);

        if (_title != null)
            window.Draw(_title);
        if (_emptyLabel != null)
            window.Draw(_emptyLabel);
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
                // Try the next installed font.
            }
        }

        return null;
    }

    public void Dispose()
    {
        _title?.Dispose();
        _emptyLabel?.Dispose();
        _font?.Dispose();
        _panel.Dispose();
        _header.Dispose();
        _accent.Dispose();
    }
}
