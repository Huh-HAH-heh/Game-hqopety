using System;
using System.IO;
using SFML.Graphics;
using SFML.System;
using SFML.Window;

namespace RimClone.Render;

public enum DebugLayerType
{
    Vision,
    Hearing,
    AttackLinks,
    AiState,
    Suppression,
    Memory,
    UtilityAi
}

public sealed class DebugLayersOverlay : IDisposable
{
    private const float Width = 292f;
    private const float HeaderHeight = 34f;
    private const float RowHeight = 34f;
    private const int LayerCount = 7;
    private const float Height = HeaderHeight + LayerCount * RowHeight + 30f;

    private static readonly string[] LayerLabels =
    {
        "Зрение — конус и видимые зоны",
        "Слух — звуки и оценки бойцов",
        "Прицеливание — линии к цели",
        "ИИ — цели и маршруты",
        "Подавление — стресс и паника",
        "Память выбранной пешки",
        "Utility AI — оценки и причины выбора"
    };

    private readonly RectangleShape _panel = new(new Vector2f(Width, Height))
    {
        FillColor = new Color(15, 21, 30, 248),
        OutlineColor = new Color(82, 105, 131, 245),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _header = new(new Vector2f(Width - 2f, HeaderHeight))
    {
        FillColor = new Color(31, 42, 56, 255)
    };

    private readonly RectangleShape _accent = new(new Vector2f(3f, Height - 2f))
    {
        FillColor = new Color(90, 175, 225, 235)
    };

    private readonly RectangleShape _closeButton = new(new Vector2f(24f, 24f))
    {
        FillColor = new Color(45, 57, 72, 255),
        OutlineColor = new Color(98, 119, 144, 220),
        OutlineThickness = 0.7f
    };

    private readonly RectangleShape _rowButton = new(new Vector2f(Width - 16f, RowHeight - 2f))
    {
        FillColor = new Color(25, 33, 44, 255),
        OutlineColor = new Color(64, 81, 101, 180),
        OutlineThickness = 0.5f
    };

    private readonly RectangleShape _checkbox = new(new Vector2f(15f, 15f))
    {
        FillColor = new Color(18, 24, 33, 255),
        OutlineColor = new Color(111, 130, 153, 255),
        OutlineThickness = 1f
    };

    private readonly Font? _font;
    private readonly Text? _title;
    private readonly Text? _closeLabel;
    private readonly Text? _rowLabel;
    private readonly Text? _checkLabel;
    private readonly Text? _footer;

    private float _x;
    private float _y = 74f;
    private float _dragOffsetX;
    private float _dragOffsetY;
    private bool _hasPosition;
    private bool _dragging;

    public bool IsOpen { get; private set; }

    public bool Contains(Vector2i position) =>
        IsOpen && _panel.GetGlobalBounds().Contains(position);

    public DebugLayersOverlay()
    {
        _font = LoadFont();
        if (_font == null)
            return;

        _title = new Text(_font, "СЛОИ ОТЛАДКИ  •  V", 13)
        {
            FillColor = new Color(235, 241, 249)
        };
        _closeLabel = new Text(_font, "×", 17)
        {
            FillColor = new Color(225, 233, 242)
        };
        _rowLabel = new Text(_font, "", 11)
        {
            FillColor = new Color(223, 232, 242)
        };
        _checkLabel = new Text(_font, "✓", 13)
        {
            FillColor = new Color(14, 24, 33)
        };
        _footer = new Text(_font, "Галочки управляют слоями независимо", 10)
        {
            FillColor = new Color(145, 160, 178)
        };
    }

    public void Toggle()
    {
        IsOpen = !IsOpen;
        if (!IsOpen)
            _dragging = false;
    }

    public void Close()
    {
        IsOpen = false;
        _dragging = false;
    }

    public bool HandleMousePressed(
        Vector2i position,
        Mouse.Button button,
        uint windowWidth,
        uint windowHeight,
        Action<DebugLayerType> toggleLayer)
    {
        if (!IsOpen)
            return false;

        Layout(windowWidth, windowHeight);
        if (!_panel.GetGlobalBounds().Contains(position))
            return false;

        if (_closeButton.GetGlobalBounds().Contains(position))
        {
            if (button == Mouse.Button.Left)
                Close();
            return true;
        }

        if (button != Mouse.Button.Left)
            return true;

        if (_header.GetGlobalBounds().Contains(position))
        {
            _dragging = true;
            _dragOffsetX = position.X - _panel.Position.X;
            _dragOffsetY = position.Y - _panel.Position.Y;
            return true;
        }

        float firstRowY = _panel.Position.Y + HeaderHeight + 3f;
        for (int i = 0; i < LayerCount; i++)
        {
            float rowY = firstRowY + i * RowHeight;
            FloatRect rowBounds = new(
                new Vector2f(_panel.Position.X + 6f, rowY),
                new Vector2f(Width - 12f, RowHeight - 2f));
            if (!rowBounds.Contains(new Vector2f(position.X, position.Y)))
                continue;

            toggleLayer((DebugLayerType)i);
            return true;
        }

        return true;
    }

    public bool HandleMouseMoved(Vector2i position, uint windowWidth, uint windowHeight)
    {
        if (!_dragging || !IsOpen)
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
        Vector2i mousePosition,
        bool showVision,
        bool showHearing,
        bool showAttackLinks,
        bool showAiState,
        bool showSuppression,
        bool showMemory,
        bool showUtilityAi)
    {
        if (!IsOpen)
            return;

        Layout(windowWidth, windowHeight);
        window.Draw(_panel);
        window.Draw(_header);
        window.Draw(_accent);
        window.Draw(_closeButton);
        if (_title != null)
            window.Draw(_title);
        if (_closeLabel != null)
            window.Draw(_closeLabel);

        bool[] enabled =
        {
            showVision,
            showHearing,
            showAttackLinks,
            showAiState,
            showSuppression,
            showMemory,
            showUtilityAi
        };

        float firstRowY = _panel.Position.Y + HeaderHeight + 3f;
        for (int i = 0; i < LayerCount; i++)
        {
            float rowY = firstRowY + i * RowHeight;
            _rowButton.Position = new Vector2f(_panel.Position.X + 6f, rowY);
            _rowButton.Size = new Vector2f(Width - 12f, RowHeight - 2f);
            bool hovered = _rowButton.GetGlobalBounds().Contains(mousePosition);
            _rowButton.FillColor = hovered
                ? new Color(43, 57, 74, 255)
                : new Color(25, 33, 44, 255);
            _rowButton.OutlineColor = hovered
                ? new Color(103, 151, 193, 230)
                : new Color(64, 81, 101, 180);
            window.Draw(_rowButton);

            _checkbox.Position = new Vector2f(_panel.Position.X + 16f, rowY + 8f);
            _checkbox.FillColor = enabled[i]
                ? new Color(105, 205, 150, 255)
                : new Color(18, 24, 33, 255);
            _checkbox.OutlineColor = enabled[i]
                ? new Color(133, 228, 174, 255)
                : new Color(111, 130, 153, 255);
            window.Draw(_checkbox);

            if (enabled[i] && _checkLabel != null)
            {
                _checkLabel.Position = new Vector2f(_panel.Position.X + 17f, rowY + 4f);
                window.Draw(_checkLabel);
            }

            if (_rowLabel != null)
            {
                _rowLabel.DisplayedString = LayerLabels[i];
                _rowLabel.Position = new Vector2f(_panel.Position.X + 40f, rowY + 8f);
                _rowLabel.FillColor = enabled[i]
                    ? new Color(235, 242, 249, 255)
                    : new Color(147, 159, 174, 255);
                window.Draw(_rowLabel);
            }
        }

        if (_footer != null)
        {
            _footer.Position = new Vector2f(_panel.Position.X + 11f, _panel.Position.Y + Height - 20f);
            window.Draw(_footer);
        }
    }

    private void Layout(uint windowWidth, uint windowHeight)
    {
        if (!_hasPosition)
        {
            _x = Math.Clamp((float)windowWidth - Width - 18f, 8f, Math.Max(8f, windowWidth - Width));
            _y = Math.Clamp(74f, 8f, Math.Max(8f, windowHeight - Height));
            _hasPosition = true;
        }

        _x = Math.Clamp(_x, 8f, Math.Max(8f, windowWidth - Width - 8f));
        _y = Math.Clamp(_y, 8f, Math.Max(8f, windowHeight - Height - 8f));
        float x = _x;
        float y = _y;
        _panel.Position = new Vector2f(x, y);
        _header.Position = new Vector2f(x + 1f, y + 1f);
        _accent.Position = new Vector2f(x + 1f, y + 1f);
        _closeButton.Position = new Vector2f(x + Width - 30f, y + 5f);
        _rowButton.Size = new Vector2f(Width - 12f, RowHeight - 2f);
        if (_title != null)
            _title.Position = new Vector2f(x + 10f, y + 8f);
        if (_closeLabel != null)
            _closeLabel.Position = new Vector2f(x + Width - 25f, y + 5f);
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
        _footer?.Dispose();
        _checkLabel?.Dispose();
        _rowLabel?.Dispose();
        _closeLabel?.Dispose();
        _title?.Dispose();
        _font?.Dispose();
        _checkbox.Dispose();
        _rowButton.Dispose();
        _closeButton.Dispose();
        _accent.Dispose();
        _header.Dispose();
        _panel.Dispose();
    }
}
