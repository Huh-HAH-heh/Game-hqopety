using Core.Map;
using Core.Structs;
using Core.Unit;
using Core.Unit.Components;
using SFML.Graphics;
using SFML.System;
using SFML.Window;
using System;
using System.Collections.Generic;

namespace Core.Input.Systems;

public sealed class MouseInputSystem
{
    private bool _isSelecting;
    private bool _isCommanding;

    private Vector2f _startDragPixels;
    private Vector2f _currentMousePixels;
    private Vector2f _commandStartWorld;

    private float _lastClickTime;

    private readonly Clock _clickClock = new();

    public readonly List<int> SelectedUnitIds = new(16);
    public readonly List<SpatialCoord> CommandPoints = new(64);

    public bool IsCommanding => _isCommanding;

    public event Action<IReadOnlyList<int>, IReadOnlyList<SpatialCoord>> MoveCommandRequested;

    public void Update(RenderWindow window, UnitStore units, int currentViewZ, float microCellPixelSize, Vector2f mouseWorld)
    {
        _currentMousePixels = mouseWorld;

        UpdateSelection(units, currentViewZ, microCellPixelSize);
        UpdateCommand(units, currentViewZ, microCellPixelSize);
    }

    private void UpdateSelection(UnitStore units, int currentViewZ, float cellSize)
    {
        if (Mouse.IsButtonPressed(Mouse.Button.Left))
        {
            if (!_isSelecting)
            {
                _isSelecting = true;
                _startDragPixels = _currentMousePixels;
                SelectedUnitIds.Clear();
            }

            return;
        }

        if (!_isSelecting)
            return;

        _isSelecting = false;
        SelectUnits(units, currentViewZ, cellSize);
    }

    private void SelectUnits(UnitStore units, int currentViewZ, float cellSize)
    {
        float minX = Math.Min(_startDragPixels.X, _currentMousePixels.X);
        float maxX = Math.Max(_startDragPixels.X, _currentMousePixels.X);
        float minY = Math.Min(_startDragPixels.Y, _currentMousePixels.Y);
        float maxY = Math.Max(_startDragPixels.Y, _currentMousePixels.Y);

        bool singleClick = maxX - minX < 5f && maxY - minY < 5f;
        float time = _clickClock.ElapsedTime.AsSeconds();
        bool doubleClick = singleClick && time - _lastClickTime < 0.25f;

        _lastClickTime = time;

        if (doubleClick)
            SelectedUnitIds.Clear();

        SpatialCoord mouseCoord = ToCoord(_currentMousePixels, currentViewZ, cellSize);
        SpatialCoord minCoord = ToCoord(new Vector2f(minX, minY), currentViewZ, cellSize);
        SpatialCoord maxCoord = ToCoord(new Vector2f(maxX, maxY), currentViewZ, cellSize);

        for (int i = 0; i < units.Count; i++)
        {
            if (units.HealthMasks[i] == 0)
                continue;

            SpatialCoord unitCoord = units.Positions[i].Spatial;

            if (unitCoord.Z != currentViewZ)
                continue;

            if (singleClick)
            {
                if (unitCoord.X != mouseCoord.X || unitCoord.Y != mouseCoord.Y)
                    continue;

                SelectedUnitIds.Add(i);
                break;
            }

            if (unitCoord.X < minCoord.X || unitCoord.X > maxCoord.X ||
                unitCoord.Y < minCoord.Y || unitCoord.Y > maxCoord.Y)
                continue;

            SelectedUnitIds.Add(i);
        }

        //Console.WriteLine($"[MOUSE] SELECTION count={SelectedUnitIds.Count}");
    }

    private void UpdateCommand(UnitStore units, int currentViewZ, float cellSize)
    {
        if (Mouse.IsButtonPressed(Mouse.Button.Right))
        {
            if (!_isCommanding && SelectedUnitIds.Count > 0)
            {
                _isCommanding = true;
                _commandStartWorld = _currentMousePixels;
                CommandPoints.Clear();

                //Console.WriteLine($"[COMMAND] START units={SelectedUnitIds.Count} pos={_currentMousePixels}");
            }

            if (_isCommanding)
                BuildCommandPoints(currentViewZ, cellSize);

            return;
        }

        if (!_isCommanding)
            return;

        _isCommanding = false;

        //Console.WriteLine($"[COMMAND] RELEASE points={CommandPoints.Count}");

        if (CommandPoints.Count == 0)
        {
            //Console.WriteLine("[COMMAND] CANCEL: no points");
            return;
        }

        AssignCommandPoints(units);

        //Console.WriteLine($"[COMMAND] ASSIGNED units={SelectedUnitIds.Count} points={CommandPoints.Count}");

        for (int i = 0; i < SelectedUnitIds.Count && i < CommandPoints.Count; i++)
        {
            int unitId = SelectedUnitIds[i];
            SpatialCoord unitPos = units.Positions[unitId].Spatial;
            SpatialCoord point = CommandPoints[i];

            //Console.WriteLine(
            //    $"[COMMAND] UNIT {unitId}: " +
            //    $"({unitPos.X},{unitPos.Y},{unitPos.Z}) -> " +
            //    $"({point.X},{point.Y},{point.Z})");
        }

        MoveCommandRequested?.Invoke(
            new List<int>(SelectedUnitIds),
            new List<SpatialCoord>(CommandPoints));
    }
    private void AssignCommandPoints(UnitStore units)
    {
        if (SelectedUnitIds.Count == 0 || CommandPoints.Count == 0)
            return;

        List<SpatialCoord> available = new(CommandPoints);

        CommandPoints.Clear();

        for (int i = 0; i < SelectedUnitIds.Count; i++)
        {
            if (available.Count == 0)
                break;

            int unitId = SelectedUnitIds[i];
            SpatialCoord unitPos = units.Positions[unitId].Spatial;

            int bestIndex = 0;
            int bestDistance = Distance(unitPos, available[0]);

            for (int j = 1; j < available.Count; j++)
            {
                int distance = Distance(unitPos, available[j]);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = j;
                }
            }

            CommandPoints.Add(available[bestIndex]);
            available.RemoveAt(bestIndex);
        }
    }

    private static int Distance(SpatialCoord a, SpatialCoord b)
    {
        return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    }
    private int _lastLoggedPointCount = -1;
    private void LogCommandPoints(string mode)
    {
        if (_lastLoggedPointCount == CommandPoints.Count)
            return;

        _lastLoggedPointCount = CommandPoints.Count;

        //Console.WriteLine($"[WAYPOINTS] mode={mode} count={CommandPoints.Count}");

        for (int i = 0; i < CommandPoints.Count; i++)
        {
            SpatialCoord p = CommandPoints[i];
            //Console.WriteLine($"[WAYPOINTS] {i}: ({p.X},{p.Y},{p.Z})");
        }
    }
    private void BuildCommandPoints(int z, float cellSize)
    {
        SpatialCoord start = ToCoord(_commandStartWorld, z, cellSize);
        SpatialCoord end = ToCoord(_currentMousePixels, z, cellSize);

        int count = SelectedUnitIds.Count;

        CommandPoints.Clear();

        if (count == 0)
            return;

        int dx = end.X - start.X;
        int dy = end.Y - start.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);

        if (length <= 0f)
        {
            BuildDenseArea(start, z, count);
            LogCommandPoints("DENSE");
            return;
        }

        if (count == 1)
        {
            CommandPoints.Add(end);
            LogCommandPoints("SINGLE");
            return;
        }

        int columns = Math.Max(1, (int)MathF.Floor(length) + 1);

        if (count <= columns)
        {
            for (int i = 0; i < count; i++)
            {
                int x = start.X + dx * i / (count - 1);
                int y = start.Y + dy * i / (count - 1);

                CommandPoints.Add(new SpatialCoord(x, y, z));
            }

            LogCommandPoints("LINE");
            return;
        }

        int rows = (int)MathF.Ceiling((float)count / columns);

        float invLength = 1f / length;
        float nx = -dy * invLength;
        float ny = dx * invLength;

        float centerX = (start.X + end.X) * 0.5f;
        float centerY = (start.Y + end.Y) * 0.5f;

        HashSet<SpatialCoord> used = new();

        for (int row = 0; row < rows; row++)
        {
            float rowOffset = row - (rows - 1) * 0.5f;

            for (int col = 0; col < columns; col++)
            {
                if (CommandPoints.Count >= count)
                    break;

                float t = columns == 1 ? 0.5f : (float)col / (columns - 1);
                float along = (t - 0.5f) * length;

                int x = (int)MathF.Round(centerX + dx * invLength * along + nx * rowOffset);
                int y = (int)MathF.Round(centerY + dy * invLength * along + ny * rowOffset);

                SpatialCoord point = new(x, y, z);

                if (used.Add(point))
                    CommandPoints.Add(point);
            }
        }

        if (CommandPoints.Count < count)
        {
            BuildDenseArea(
                new SpatialCoord(
                    (int)MathF.Round(centerX),
                    (int)MathF.Round(centerY),
                    z),
                z,
                count,
                used);
        }

        LogCommandPoints("AREA");
    }
    private void BuildDenseArea(SpatialCoord center, int z, int count)
    {
        BuildDenseArea(center, z, count, new HashSet<SpatialCoord>());
    }

    private void BuildDenseArea(SpatialCoord center, int z, int count, HashSet<SpatialCoord> used)
    {
        for (int radius = 0; CommandPoints.Count < count; radius++)
        {
            int minX = center.X - radius;
            int maxX = center.X + radius;
            int minY = center.Y - radius;
            int maxY = center.Y + radius;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (CommandPoints.Count >= count)
                        return;

                    if (Math.Abs(x - center.X) != radius &&
                        Math.Abs(y - center.Y) != radius)
                        continue;

                    SpatialCoord point = new(x, y, z);

                    if (used.Add(point))
                        CommandPoints.Add(point);
                }
            }
        }
    }

    public void DrawSelectionBox(RenderWindow window)
    {
        if (_isSelecting)
        {
            DrawSelection(window);
            return;
        }

        if (_isCommanding)
            DrawCommand(window);
    }

    private void DrawSelection(RenderWindow window)
    {
        float minX = Math.Min(_startDragPixels.X, _currentMousePixels.X);
        float minY = Math.Min(_startDragPixels.Y, _currentMousePixels.Y);
        float maxX = Math.Max(_startDragPixels.X, _currentMousePixels.X);
        float maxY = Math.Max(_startDragPixels.Y, _currentMousePixels.Y);

        RectangleShape box = new(
            new Vector2f(maxX - minX, maxY - minY))
        {
            Position = new Vector2f(minX, minY),
            FillColor = new Color(0, 100, 255, 35),
            OutlineColor = new Color(0, 150, 255, 220),
            OutlineThickness = 1f
        };

        window.Draw(box);
    }

    private void DrawCommand(RenderWindow window)
    {
        if (CommandPoints.Count == 0)
            return;

        float cellSize = GetCellSize();

        for (int i = 0; i < CommandPoints.Count; i++)
        {
            SpatialCoord point = CommandPoints[i];
            float radius = cellSize * 0.25f;

            CircleShape circle = new(radius)
            {
                Origin = new Vector2f(radius, radius),
                Position = new Vector2f(
                    point.X * cellSize + cellSize * 0.5f,
                    point.Y * cellSize + cellSize * 0.5f),
                FillColor = new Color(255, 255, 255, 220)
            };

            window.Draw(circle);
        }
    }

    private static SpatialCoord ToCoord(Vector2f position, int z, float cellSize)
    {
        return new SpatialCoord(
            (int)MathF.Floor(position.X / cellSize),
            (int)MathF.Floor(position.Y / cellSize),
            z);
    }

    private static float GetCellSize()
    {
        return 16f / MapRegion.SubDivision;
    }
}