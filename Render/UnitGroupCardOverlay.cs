using System;
using System.Collections.Generic;
using System.IO;
using Core.Unit;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public sealed class UnitGroupCardOverlay : IDisposable
{
    private const float Width = 520f;
    private const float MaximumHeight = 520f;
    private const float HeaderHeight = 38f;
    private const float RowHeight = 24f;
    private const int MaxRows = 24;

    private readonly RectangleShape _panel = new(new Vector2f(Width, MaximumHeight))
    {
        FillColor = new Color(17, 22, 30, 250),
        OutlineColor = new Color(76, 94, 116, 255),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _header = new(new Vector2f(Width - 2f, HeaderHeight))
    {
        FillColor = new Color(31, 40, 52, 255)
    };

    private readonly RectangleShape _accent = new(new Vector2f(3f, MaximumHeight - 2f))
    {
        FillColor = new Color(112, 164, 218, 245)
    };

    private readonly RectangleShape _closeButton = new(new Vector2f(26f, 26f))
    {
        FillColor = new Color(48, 58, 73, 255),
        OutlineColor = new Color(89, 105, 126, 220),
        OutlineThickness = 1f
    };

    private readonly Font? _font;
    private readonly Text? _title;
    private readonly Text? _closeLabel;
    private readonly Text? _footer;
    private readonly Text?[] _rows = new Text?[MaxRows];

    private float _x;
    private float _y;
    private float _dragOffsetX;
    private float _dragOffsetY;
    private bool _hasPosition;
    private bool _dragging;
    private int _scrollOffset;
    private int _visibleRows;

    public bool IsOpen { get; private set; }

    public UnitGroupCardOverlay()
    {
        _font = LoadFont();
        if (_font == null)
            return;

        _title = new Text(_font, "ОБЗОР ВЫДЕЛЕННЫХ", 14)
        {
            FillColor = new Color(239, 243, 249),
            Style = Text.Styles.Bold
        };
        _closeLabel = new Text(_font, "×", 18)
        {
            FillColor = new Color(225, 232, 241)
        };
        _footer = new Text(_font, "Колесо мыши — прокрутка списка   •   Esc — закрыть", 10)
        {
            FillColor = new Color(143, 155, 171)
        };

        for (int i = 0; i < _rows.Length; i++)
        {
            _rows[i] = new Text(_font, "", 11)
            {
                FillColor = new Color(205, 214, 225)
            };
        }
    }

    public void Open(float x, float y)
    {
        IsOpen = true;
        _x = x;
        _y = y;
        _hasPosition = true;
        _scrollOffset = 0;
        _dragging = false;
    }

    public void Close()
    {
        IsOpen = false;
        _dragging = false;
    }

    public bool Contains(Vector2i position) =>
        IsOpen && _panel.GetGlobalBounds().Contains(position);

    public bool HandleMousePressed(Vector2i position, uint windowWidth, uint windowHeight)
    {
        if (!Contains(position))
            return false;

        if (_closeButton.GetGlobalBounds().Contains(position))
        {
            Close();
            return true;
        }

        if (_header.GetGlobalBounds().Contains(position))
        {
            _dragging = true;
            _dragOffsetX = position.X - _panel.Position.X;
            _dragOffsetY = position.Y - _panel.Position.Y;
        }

        return true;
    }

    public bool HandleMouseMoved(Vector2i position, uint windowWidth, uint windowHeight)
    {
        if (!_dragging)
            return false;

        float height = Math.Clamp(windowHeight - 24f, 220f, MaximumHeight);
        _x = Math.Clamp(position.X - _dragOffsetX, 8f, Math.Max(8f, windowWidth - Width - 8f));
        _y = Math.Clamp(position.Y - _dragOffsetY, 8f, Math.Max(8f, windowHeight - height - 8f));
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

    public bool HandleWheel(Vector2i position, float delta)
    {
        if (!Contains(position))
            return false;

        _scrollOffset = Math.Max(0, _scrollOffset + (delta > 0f ? -3 : 3));
        return true;
    }

    public void Draw(
        RenderWindow window,
        SimulationFrameSnapshot snapshot,
        HashSet<UnitId> selectedUnits,
        uint windowWidth,
        uint windowHeight)
    {
        if (!IsOpen)
            return;

        Layout(windowWidth, windowHeight);

        RenderUnitState[] units = snapshot.Units;
        int selectedCount = 0;
        for (int i = 0; i < units.Length; i++)
        {
            RenderUnitState unit = units[i];
            if (selectedUnits.Contains(new UnitId(unit.Index, unit.Generation)))
                selectedCount++;
        }

        _visibleRows = Math.Clamp((int)((_panel.Size.Y - 86f) / RowHeight), 1, MaxRows);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, selectedCount - _visibleRows));

        window.Draw(_panel);
        window.Draw(_header);
        window.Draw(_accent);
        window.Draw(_closeButton);

        if (_title != null)
        {
            _title.DisplayedString = $"ОБЪЕКТЫ ВЫДЕЛЕНИЯ ({selectedCount})";
            _title.Position = new Vector2f(_panel.Position.X + 12f, _panel.Position.Y + 10f);
            window.Draw(_title);
        }

        if (_closeLabel != null)
        {
            _closeLabel.Position = new Vector2f(_panel.Position.X + Width - 27f, _panel.Position.Y + 4f);
            window.Draw(_closeLabel);
        }

        int selectedIndex = 0;
        int displayedIndex = 0;
        for (int i = 0; i < units.Length; i++)
        {
            RenderUnitState unit = units[i];
            if (!selectedUnits.Contains(new UnitId(unit.Index, unit.Generation)))
                continue;

            if (selectedIndex >= _scrollOffset &&
                displayedIndex < _visibleRows &&
                displayedIndex < _rows.Length)
            {
                Text? row = _rows[displayedIndex];
                if (row != null)
                {
                    row.DisplayedString = FormatUnit(unit);
                    row.Position = new Vector2f(
                        _panel.Position.X + 13f,
                        _panel.Position.Y + 48f + displayedIndex * RowHeight);
                    row.FillColor = unit.HitPoints <= 0f
                        ? new Color(145, 151, 160)
                        : unit.FactionTag == 1
                            ? new Color(160, 198, 237)
                            : unit.FactionTag == 2
                                ? new Color(237, 174, 158)
                                : new Color(205, 214, 225);
                    window.Draw(row);
                }
                displayedIndex++;
            }

            selectedIndex++;
        }

        for (int i = displayedIndex; i < _rows.Length; i++)
        {
            if (_rows[i] != null)
                _rows[i]!.DisplayedString = "";
        }

        if (_footer != null)
        {
            _footer.Position = new Vector2f(
                _panel.Position.X + 12f,
                _panel.Position.Y + _panel.Size.Y - 22f);
            _footer.DisplayedString = selectedCount > _visibleRows
                ? $"Строки {_scrollOffset + 1}–{Math.Min(_scrollOffset + _visibleRows, selectedCount)} из {selectedCount}   •   Колесо — прокрутка"
                : "Заголовок — перемещение   •   Колесо — прокрутка";
            window.Draw(_footer);
        }
    }

    private static string FormatUnit(RenderUnitState unit)
    {
        string faction = unit.FactionTag switch
        {
            1 => "BLUE",
            2 => "RED",
            _ => $"F{unit.FactionTag}"
        };
        System.Numerics.Vector3 p = unit.Position;
        string line =
            $"{unit.Index}:{unit.Generation} {unit.Type} {faction} " +
            $"HP {unit.HitPoints:0}/{unit.MaxHitPoints:0} " +
            $"SUP {unit.Suppression:0.00} {unit.AiState} " +
            $"@({p.X:0.0},{p.Y:0.0},{p.Z:0.0})";
        return line.Length > 78 ? line.Substring(0, 77) + "…" : line;
    }

    private void Layout(uint windowWidth, uint windowHeight)
    {
        float height = Math.Clamp(windowHeight - 24f, 220f, MaximumHeight);
        if (!_hasPosition)
        {
            _x = 18f;
            _y = 72f;
            _hasPosition = true;
        }

        float x = Math.Clamp(_x, 8f, Math.Max(8f, windowWidth - Width - 8f));
        float y = Math.Clamp(_y, 8f, Math.Max(8f, windowHeight - height - 8f));
        _x = x;
        _y = y;

        _panel.Position = new Vector2f(x, y);
        _panel.Size = new Vector2f(Width, height);
        _header.Position = new Vector2f(x + 1f, y + 1f);
        _accent.Position = new Vector2f(x + 1f, y + 1f);
        _accent.Size = new Vector2f(3f, height - 2f);
        _closeButton.Position = new Vector2f(x + Width - 34f, y + 7f);
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
        for (int i = 0; i < _rows.Length; i++)
            _rows[i]?.Dispose();

        _title?.Dispose();
        _closeLabel?.Dispose();
        _footer?.Dispose();
        _font?.Dispose();
        _panel.Dispose();
        _header.Dispose();
        _accent.Dispose();
        _closeButton.Dispose();
    }
}
