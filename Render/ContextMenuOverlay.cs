using System;
using System.IO;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public sealed class ContextMenuOverlay : IDisposable
{
    private const float Width = 196f;
    private const float EmptyHeight = 58f;
    private const float UnitHeight = 62f;

    private readonly RectangleShape _panel = new(new Vector2f(Width, EmptyHeight))
    {
        FillColor = new Color(19, 25, 34, 250),
        OutlineColor = new Color(83, 101, 124, 245),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _header = new(new Vector2f(Width - 2f, 24f))
    {
        FillColor = new Color(34, 43, 56, 255)
    };

    private readonly RectangleShape _accent = new(new Vector2f(2f, UnitHeight - 2f))
    {
        FillColor = new Color(125, 170, 220, 220)
    };

    private readonly RectangleShape _unitCardButton = new(new Vector2f(Width - 12f, 28f))
    {
        FillColor = new Color(28, 36, 47, 255)
    };

    private readonly Font? _font;
    private readonly Text? _title;
    private readonly Text? _emptyLabel;
    private readonly Text? _unitCardLabel;

    private bool _hasUnitAction;

    public bool IsOpen { get; private set; }

    public ContextMenuOverlay()
    {
        _font = LoadFont();

        if (_font == null)
            return;

        _title = new Text(_font, "Контекстное меню", 12)
        {
            FillColor = new Color(232, 237, 244)
        };

        _emptyLabel = new Text(_font, "Пока пусто", 11)
        {
            FillColor = new Color(148, 159, 174)
        };

        _unitCardLabel = new Text(_font, "Карточка юнита", 11)
        {
            FillColor = new Color(224, 233, 244)
        };
    }

    public void ToggleAt(
        Vector2i position,
        uint windowWidth,
        uint windowHeight,
        bool hasUnit)
    {
        if (IsOpen && _panel.GetGlobalBounds().Contains(position))
        {
            Close();
            return;
        }

        OpenAt(position, windowWidth, windowHeight, hasUnit);
    }

    public void OpenAt(
        Vector2i position,
        uint windowWidth,
        uint windowHeight,
        bool hasUnit)
    {
        _hasUnitAction = hasUnit;
        float height = hasUnit ? UnitHeight : EmptyHeight;
        float x = Math.Clamp(position.X, 0f, Math.Max(0f, windowWidth - Width));
        float y = Math.Clamp(position.Y, 0f, Math.Max(0f, windowHeight - height));

        _panel.Position = new Vector2f(x, y);
        _panel.Size = new Vector2f(Width, height);
        _header.Position = new Vector2f(x + 1f, y + 1f);
        _accent.Position = new Vector2f(x + 1f, y + 1f);
        _accent.Size = new Vector2f(2f, height - 2f);
        _unitCardButton.Position = new Vector2f(x + 6f, y + 29f);

        if (_title != null)
            _title.Position = new Vector2f(x + 10f, y + 5f);
        if (_emptyLabel != null)
            _emptyLabel.Position = new Vector2f(x + 10f, y + 32f);
        if (_unitCardLabel != null)
            _unitCardLabel.Position = new Vector2f(x + 13f, y + 35f);

        IsOpen = true;
    }

    public bool HandleClick(Vector2i position, out bool openUnitCard)
    {
        openUnitCard = false;
        if (!IsOpen)
            return false;

        if (_panel.GetGlobalBounds().Contains(position))
        {
            if (_hasUnitAction && _unitCardButton.GetGlobalBounds().Contains(position))
                openUnitCard = true;

            Close();
            return true;
        }

        Close();
        return false;
    }

    public void Close()
    {
        IsOpen = false;
        _hasUnitAction = false;
    }

    public void Draw(RenderWindow window, uint windowWidth, uint windowHeight)
    {
        if (!IsOpen)
            return;

        Vector2f position = _panel.Position;
        OpenAt(
            new Vector2i((int)position.X, (int)position.Y),
            windowWidth,
            windowHeight,
            _hasUnitAction);

        window.Draw(_panel);
        window.Draw(_header);
        window.Draw(_accent);

        if (_hasUnitAction)
            window.Draw(_unitCardButton);

        if (_title != null)
            window.Draw(_title);

        if (_hasUnitAction)
        {
            if (_unitCardLabel != null)
                window.Draw(_unitCardLabel);
        }
        else if (_emptyLabel != null)
        {
            window.Draw(_emptyLabel);
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
                // Try the next installed font.
            }
        }

        return null;
    }

    public void Dispose()
    {
        _title?.Dispose();
        _emptyLabel?.Dispose();
        _unitCardLabel?.Dispose();
        _font?.Dispose();
        _panel.Dispose();
        _header.Dispose();
        _accent.Dispose();
        _unitCardButton.Dispose();
    }
}
