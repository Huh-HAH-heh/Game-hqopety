using System;
using System.Collections.Generic;
using System.IO;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public enum ContextMenuActionType
{
    None,
    OpenUnitCard,
    OpenUnitAnatomy,
    GiveFood,
    GiveWater,
    OpenSelectionOverview,
    CreateSquad,
    SendSquad,
    AttackTarget
}

public sealed class ContextMenuOverlay : IDisposable
{
    private const float Width = 224f;
    private const float EmptyHeight = 58f;
    private const float HeaderHeight = 26f;
    private const float RowHeight = 30f;

    private readonly RectangleShape _panel = new(new Vector2f(Width, EmptyHeight))
    {
        FillColor = new Color(19, 25, 34, 250),
        OutlineColor = new Color(83, 101, 124, 245),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _header = new(new Vector2f(Width - 2f, HeaderHeight))
    {
        FillColor = new Color(34, 43, 56, 255)
    };

    private readonly RectangleShape _accent = new(new Vector2f(2f, EmptyHeight - 2f))
    {
        FillColor = new Color(125, 170, 220, 220)
    };

    private readonly RectangleShape _actionButton = new(new Vector2f(Width - 12f, RowHeight - 3f))
    {
        FillColor = new Color(28, 36, 47, 255),
        OutlineColor = new Color(75, 98, 125, 200),
        OutlineThickness = 0.5f
    };

    private readonly Font? _font;
    private readonly Text? _title;
    private readonly Text? _emptyLabel;
    private readonly Text? _actionLabel;
    private readonly List<ContextMenuActionType> _actions = new();
    private readonly List<string> _labels = new();

    private float _dragOffsetX;
    private float _dragOffsetY;
    private bool _dragging;

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

        _actionLabel = new Text(_font, "", 11)
        {
            FillColor = new Color(224, 233, 244)
        };
    }

    public void ToggleAt(
        Vector2i position,
        uint windowWidth,
        uint windowHeight,
        bool hasUnit,
        bool hasMultipleUnits,
        bool canCreateSquad,
        bool canSendSquad,
        bool canAttackTarget)
    {
        if (IsOpen && _panel.GetGlobalBounds().Contains(position))
        {
            Close();
            return;
        }

        OpenAt(
            position,
            windowWidth,
            windowHeight,
            hasUnit,
            hasMultipleUnits,
            canCreateSquad,
            canSendSquad,
            canAttackTarget);
    }

    public void OpenAt(
        Vector2i position,
        uint windowWidth,
        uint windowHeight,
        bool hasUnit,
        bool hasMultipleUnits,
        bool canCreateSquad,
        bool canSendSquad,
        bool canAttackTarget)
    {
        _actions.Clear();
        _labels.Clear();
        _dragging = false;

        if (hasUnit)
        {
            AddAction(ContextMenuActionType.OpenUnitCard, "Карточка юнита");
            AddAction(ContextMenuActionType.OpenUnitAnatomy, "Органы / части тела");
            AddAction(ContextMenuActionType.GiveFood, "Рацион (+700 ккал)");
            AddAction(ContextMenuActionType.GiveWater, "Вода (+0,5 л)");
        }

        if (canAttackTarget)
            AddAction(ContextMenuActionType.AttackTarget, "Атаковать цель");

        if (canSendSquad)
            AddAction(ContextMenuActionType.SendSquad, "Отправить отряд сюда");

        if (canCreateSquad)
            AddAction(ContextMenuActionType.CreateSquad, "Создать отряд");

        if (hasMultipleUnits)
            AddAction(ContextMenuActionType.OpenSelectionOverview, "Обзор выделенных");

        float height = GetHeight();
        float x = Math.Clamp(position.X, 0f, Math.Max(0f, windowWidth - Width));
        float y = Math.Clamp(position.Y, 0f, Math.Max(0f, windowHeight - height));

        _panel.Position = new Vector2f(x, y);
        _panel.Size = new Vector2f(Width, height);
        _header.Position = new Vector2f(x + 1f, y + 1f);
        _accent.Position = new Vector2f(x + 1f, y + 1f);
        _accent.Size = new Vector2f(2f, height - 2f);

        if (_title != null)
            _title.Position = new Vector2f(x + 10f, y + 5f);
        if (_emptyLabel != null)
            _emptyLabel.Position = new Vector2f(x + 10f, y + 32f);

        IsOpen = true;
    }

    private void AddAction(ContextMenuActionType action, string label)
    {
        _actions.Add(action);
        _labels.Add(label);
    }

    public bool HandleMousePressed(Vector2i position, uint windowWidth, uint windowHeight)
    {
        if (!IsOpen)
            return false;

        Layout(windowWidth, windowHeight);
        if (!_header.GetGlobalBounds().Contains(position))
            return false;

        _dragging = true;
        _dragOffsetX = position.X - _panel.Position.X;
        _dragOffsetY = position.Y - _panel.Position.Y;
        return true;
    }

    public bool HandleMouseMoved(Vector2i position, uint windowWidth, uint windowHeight)
    {
        if (!_dragging || !IsOpen)
            return false;

        _panel.Position = new Vector2f(
            position.X - _dragOffsetX,
            position.Y - _dragOffsetY);
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

    public bool HandleClick(
        Vector2i position,
        out ContextMenuActionType action)
    {
        action = ContextMenuActionType.None;
        if (!IsOpen)
            return false;

        if (_panel.GetGlobalBounds().Contains(position))
        {
            float firstRowY = _panel.Position.Y + HeaderHeight + 4f;
            int row = (int)((position.Y - firstRowY) / RowHeight);
            if (row >= 0 && row < _actions.Count)
                action = _actions[row];

            Close();
            return true;
        }

        Close();
        return false;
    }

    public bool Contains(Vector2i position) =>
        IsOpen && _panel.GetGlobalBounds().Contains(position);

    public void Close()
    {
        IsOpen = false;
        _dragging = false;
        _actions.Clear();
        _labels.Clear();
    }

    public void Draw(
        RenderWindow window,
        uint windowWidth,
        uint windowHeight,
        Vector2i mousePosition)
    {
        if (!IsOpen)
            return;

        Layout(windowWidth, windowHeight);
        window.Draw(_panel);
        window.Draw(_header);
        window.Draw(_accent);

        if (_title != null)
            window.Draw(_title);

        if (_actions.Count == 0)
        {
            if (_emptyLabel != null)
                window.Draw(_emptyLabel);
            return;
        }

        for (int i = 0; i < _actions.Count; i++)
        {
            float x = _panel.Position.X + 6f;
            float y = _panel.Position.Y + HeaderHeight + 4f + i * RowHeight;
            _actionButton.Position = new Vector2f(x, y);
            bool hovered = _actionButton.GetGlobalBounds().Contains(mousePosition);
            bool command = _actions[i] is ContextMenuActionType.CreateSquad
                or ContextMenuActionType.SendSquad
                or ContextMenuActionType.AttackTarget;
            _actionButton.FillColor = hovered
                ? new Color(61, 88, 118, 255)
                : command
                    ? new Color(35, 50, 67, 255)
                    : new Color(28, 36, 47, 255);
            _actionButton.OutlineColor = command || hovered
                ? new Color(112, 164, 218, 230)
                : new Color(75, 98, 125, 150);
            _actionButton.OutlineThickness = hovered ? 1f : command ? 0.8f : 0.4f;
            window.Draw(_actionButton);

            if (_actionLabel != null)
            {
                _actionLabel.DisplayedString = _labels[i];
                _actionLabel.Position = new Vector2f(x + 7f, y + 6f);
                _actionLabel.FillColor = hovered
                    ? new Color(255, 255, 255, 255)
                    : new Color(224, 233, 244, 255);
                window.Draw(_actionLabel);
            }
        }
    }

    private float GetHeight() =>
        _actions.Count == 0
            ? EmptyHeight
            : HeaderHeight + 8f + _actions.Count * RowHeight + 8f;

    private void Layout(uint windowWidth, uint windowHeight)
    {
        float height = GetHeight();
        float x = Math.Clamp(_panel.Position.X, 0f, Math.Max(0f, windowWidth - Width));
        float y = Math.Clamp(_panel.Position.Y, 0f, Math.Max(0f, windowHeight - height));
        _panel.Position = new Vector2f(x, y);
        _panel.Size = new Vector2f(Width, height);
        _header.Position = new Vector2f(x + 1f, y + 1f);
        _accent.Position = new Vector2f(x + 1f, y + 1f);
        _accent.Size = new Vector2f(2f, height - 2f);

        if (_title != null)
            _title.Position = new Vector2f(x + 10f, y + 5f);
        if (_emptyLabel != null)
            _emptyLabel.Position = new Vector2f(x + 10f, y + 32f);
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
        _actionLabel?.Dispose();
        _font?.Dispose();
        _panel.Dispose();
        _header.Dispose();
        _accent.Dispose();
        _actionButton.Dispose();
    }
}