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
    private const int MapWidth =
        16 * MapRegion.MicroSize;

    private const int MaxCoord =
        MapWidth - 1;

    private bool _isSelecting;
    private bool _isCommanding;

    private Vector2f _startDragWorld;
    private Vector2f _currentMouseWorld;
    private Vector2f _commandStartWorld;

    private float _lastClickTime;

    private readonly Clock _clickClock =
        new();

    public readonly List<int> SelectedUnitIds =
        new(16);

    public readonly List<SpatialCoord> CommandPoints =
        new(64);

    public bool IsCommanding =>
        _isCommanding;

    public event Action<
        IReadOnlyList<int>,
        IReadOnlyList<SpatialCoord>> MoveCommandRequested;

    public void Update(
        RenderWindow window,
        UnitStore units,
        WorldMap map,
        int currentViewZ,
        float microCellPixelSize,
        Vector2f mouseWorld)
    {
        if (units == null ||
            microCellPixelSize <= 0f)
        {
            return;
        }

        _currentMouseWorld =
            mouseWorld;

        UpdateSelection(
            units,
            currentViewZ,
            microCellPixelSize);

        UpdateCommand(
            units,
            currentViewZ,
            microCellPixelSize);
    }

    // ============================================================
    // SELECTION
    // ============================================================

    private void UpdateSelection(
        UnitStore units,
        int currentViewZ,
        float cellSize)
    {
        if (Mouse.IsButtonPressed(
                Mouse.Button.Left))
        {
            if (!_isSelecting)
            {
                _isSelecting = true;

                _startDragWorld =
                    _currentMouseWorld;

                SelectedUnitIds.Clear();
            }

            return;
        }

        if (!_isSelecting)
            return;

        _isSelecting = false;

        SelectUnits(
            units,
            currentViewZ,
            cellSize);
    }

    private void SelectUnits(
        UnitStore units,
        int currentViewZ,
        float cellSize)
    {
        float minX =
            Math.Min(
                _startDragWorld.X,
                _currentMouseWorld.X);

        float maxX =
            Math.Max(
                _startDragWorld.X,
                _currentMouseWorld.X);

        float minY =
            Math.Min(
                _startDragWorld.Y,
                _currentMouseWorld.Y);

        float maxY =
            Math.Max(
                _startDragWorld.Y,
                _currentMouseWorld.Y);

        bool singleClick =
            maxX - minX < 5f &&
            maxY - minY < 5f;

        float time =
            _clickClock.ElapsedTime
                .AsSeconds();

        bool doubleClick =
            singleClick &&
            time - _lastClickTime < 0.25f;

        _lastClickTime =
            time;

        if (doubleClick)
            SelectedUnitIds.Clear();

        SpatialCoord mouseCoord =
            ToCoord(
                _currentMouseWorld,
                currentViewZ,
                cellSize);

        SpatialCoord minCoord =
            ToCoord(
                new Vector2f(
                    minX,
                    minY),
                currentViewZ,
                cellSize);

        SpatialCoord maxCoord =
            ToCoord(
                new Vector2f(
                    maxX,
                    maxY),
                currentViewZ,
                cellSize);

        for (int i = 0;
             i < units.Count;
             i++)
        {
            if (units.HealthMasks[i] == 0)
                continue;

            ref UnitPosition unitPosition =
                ref units.Positions[i];

            SpatialCoord unitCoord =
                unitPosition.Spatial;

            if (unitCoord.Z != currentViewZ)
                continue;

            if (singleClick)
            {
                if (unitCoord.X != mouseCoord.X ||
                    unitCoord.Y != mouseCoord.Y)
                {
                    continue;
                }

                SelectedUnitIds.Add(i);
                break;
            }

            if (unitCoord.X < minCoord.X ||
                unitCoord.X > maxCoord.X ||
                unitCoord.Y < minCoord.Y ||
                unitCoord.Y > maxCoord.Y)
            {
                continue;
            }

            SelectedUnitIds.Add(i);
        }
    }

    // ============================================================
    // COMMAND
    // ============================================================

    private void UpdateCommand(
        UnitStore units,
        int currentViewZ,
        float cellSize)
    {
        if (Mouse.IsButtonPressed(
                Mouse.Button.Right))
        {
            if (!_isCommanding &&
                SelectedUnitIds.Count > 0)
            {
                _isCommanding = true;

                _commandStartWorld =
                    _currentMouseWorld;

                CommandPoints.Clear();
            }

            if (_isCommanding)
            {
                BuildCommandPoints(
                    currentViewZ,
                    cellSize);
            }

            return;
        }

        if (!_isCommanding)
            return;

        _isCommanding = false;

        if (CommandPoints.Count == 0)
            return;

        AssignCommandPoints(
            units);

        MoveCommandRequested?.Invoke(
            new List<int>(
                SelectedUnitIds),

            new List<SpatialCoord>(
                CommandPoints));
    }

    // ============================================================
    // ASSIGNMENT
    // ============================================================

    private void AssignCommandPoints(
        UnitStore units)
    {
        if (SelectedUnitIds.Count == 0 ||
            CommandPoints.Count == 0)
        {
            return;
        }

        List<SpatialCoord> available =
            new(CommandPoints);

        CommandPoints.Clear();

        int count =
            Math.Min(
                SelectedUnitIds.Count,
                available.Count);

        for (int i = 0;
             i < count;
             i++)
        {
            int unitId =
                SelectedUnitIds[i];

            SpatialCoord unitPos =
                units.Positions[
                    unitId].Spatial;

            int bestIndex = 0;

            int bestDistance =
                Distance(
                    unitPos,
                    available[0]);

            for (int j = 1;
                 j < available.Count;
                 j++)
            {
                int distance =
                    Distance(
                        unitPos,
                        available[j]);

                if (distance < bestDistance)
                {
                    bestDistance =
                        distance;

                    bestIndex =
                        j;
                }
            }

            CommandPoints.Add(
                available[bestIndex]);

            available.RemoveAt(
                bestIndex);
        }
    }

    private static int Distance(
        SpatialCoord a,
        SpatialCoord b)
    {
        return
            Math.Abs(
                a.X - b.X) +
            Math.Abs(
                a.Y - b.Y);
    }

    // ============================================================
    // COMMAND POINTS
    // ============================================================

    private void BuildCommandPoints(
        int z,
        float cellSize)
    {
        SpatialCoord start =
            ToCoord(
                _commandStartWorld,
                z,
                cellSize);

        SpatialCoord end =
            ToCoord(
                _currentMouseWorld,
                z,
                cellSize);

        int count =
            SelectedUnitIds.Count;

        CommandPoints.Clear();

        if (count == 0)
            return;

        int dx =
            end.X - start.X;

        int dy =
            end.Y - start.Y;

        float length =
            MathF.Sqrt(
                dx * dx +
                dy * dy);

        if (length <= 0f)
        {
            BuildDenseArea(
                start,
                z,
                count);

            return;
        }

        if (count == 1)
        {
            CommandPoints.Add(
                end);

            return;
        }

        int columns =
            Math.Max(
                1,
                (int)MathF.Floor(
                    length) + 1);

        // --------------------------------------------------------
        // LINE
        // --------------------------------------------------------

        if (count <= columns)
        {
            HashSet<SpatialCoord> used =
                new();

            for (int i = 0;
                 i < count;
                 i++)
            {
                float t =
                    (float)i /
                    (count - 1);

                int x =
                    (int)MathF.Round(
                        start.X +
                        dx * t);

                int y =
                    (int)MathF.Round(
                        start.Y +
                        dy * t);

                x =
                    Math.Clamp(
                        x,
                        0,
                        MaxCoord);

                y =
                    Math.Clamp(
                        y,
                        0,
                        MaxCoord);

                SpatialCoord point =
                    new(
                        x,
                        y,
                        z);

                if (used.Add(point))
                    CommandPoints.Add(
                        point);
            }

            if (CommandPoints.Count < count)
            {
                BuildDenseArea(
                    end,
                    z,
                    count,
                    used);
            }

            return;
        }

        // --------------------------------------------------------
        // AREA / FORMATION
        // --------------------------------------------------------

        int rows =
            (int)MathF.Ceiling(
                (float)count /
                columns);

        float invLength =
            1f /
            length;

        float nx =
            -dy *
            invLength;

        float ny =
            dx *
            invLength;

        float centerX =
            (start.X +
             end.X) *
            0.5f;

        float centerY =
            (start.Y +
             end.Y) *
            0.5f;

        HashSet<SpatialCoord> usedPoints =
            new();

        for (int row = 0;
             row < rows;
             row++)
        {
            float rowOffset =
                row -
                (rows - 1) *
                0.5f;

            for (int col = 0;
                 col < columns;
                 col++)
            {
                if (CommandPoints.Count >= count)
                    break;

                float t =
                    columns == 1
                        ? 0.5f
                        : (float)col /
                          (columns - 1);

                float along =
                    (t - 0.5f) *
                    length;

                int x =
                    (int)MathF.Round(
                        centerX +
                        dx *
                        invLength *
                        along +
                        nx *
                        rowOffset);

                int y =
                    (int)MathF.Round(
                        centerY +
                        dy *
                        invLength *
                        along +
                        ny *
                        rowOffset);

                if (x < 0 ||
                    y < 0 ||
                    x > MaxCoord ||
                    y > MaxCoord)
                {
                    continue;
                }

                SpatialCoord point =
                    new(
                        x,
                        y,
                        z);

                if (usedPoints.Add(
                        point))
                {
                    CommandPoints.Add(
                        point);
                }
            }
        }

        if (CommandPoints.Count < count)
        {
            BuildDenseArea(
                new SpatialCoord(
                    (int)MathF.Round(
                        centerX),
                    (int)MathF.Round(
                        centerY),
                    z),
                z,
                count,
                usedPoints);
        }
    }

    private void BuildDenseArea(
        SpatialCoord center,
        int z,
        int count)
    {
        BuildDenseArea(
            center,
            z,
            count,
            new HashSet<SpatialCoord>());
    }

    private void BuildDenseArea(
        SpatialCoord center,
        int z,
        int count,
        HashSet<SpatialCoord> used)
    {
        for (int radius = 0;
             CommandPoints.Count < count;
             radius++)
        {
            int minX =
                Math.Max(
                    0,
                    center.X - radius);

            int maxX =
                Math.Min(
                    MaxCoord,
                    center.X + radius);

            int minY =
                Math.Max(
                    0,
                    center.Y - radius);

            int maxY =
                Math.Min(
                    MaxCoord,
                    center.Y + radius);

            for (int y = minY;
                 y <= maxY;
                 y++)
            {
                for (int x = minX;
                     x <= maxX;
                     x++)
                {
                    if (CommandPoints.Count >= count)
                        return;

                    if (Math.Abs(
                            x - center.X) != radius &&
                        Math.Abs(
                            y - center.Y) != radius)
                    {
                        continue;
                    }

                    SpatialCoord point =
                        new(
                            x,
                            y,
                            z);

                    if (used.Add(point))
                        CommandPoints.Add(
                            point);
                }
            }
        }
    }

    // ============================================================
    // DRAW
    // ============================================================

    public void DrawSelectionBox(
        RenderWindow window)
    {
        if (_isSelecting)
        {
            DrawSelection(
                window);

            return;
        }

        if (_isCommanding)
            DrawCommand(
                window);
    }

    private void DrawSelection(
        RenderWindow window)
    {
        float minX =
            Math.Min(
                _startDragWorld.X,
                _currentMouseWorld.X);

        float minY =
            Math.Min(
                _startDragWorld.Y,
                _currentMouseWorld.Y);

        float maxX =
            Math.Max(
                _startDragWorld.X,
                _currentMouseWorld.X);

        float maxY =
            Math.Max(
                _startDragWorld.Y,
                _currentMouseWorld.Y);

        RectangleShape box =
            new RectangleShape(
                new Vector2f(
                    maxX - minX,
                    maxY - minY))
            {
                Position =
                    new Vector2f(
                        minX,
                        minY),

                FillColor =
                    new Color(
                        0,
                        100,
                        255,
                        35),

                OutlineColor =
                    new Color(
                        0,
                        150,
                        255,
                        220),

                OutlineThickness =
                    1f
            };

        window.Draw(
            box);
    }

    private void DrawCommand(
        RenderWindow window)
    {
        if (CommandPoints.Count == 0)
            return;

        float cellSize =
            16f /
            MapRegion.SubDivision;

        float radius =
            cellSize *
            0.25f;

        for (int i = 0;
             i < CommandPoints.Count;
             i++)
        {
            SpatialCoord point =
                CommandPoints[i];

            float x =
                point.X *
                cellSize +
                cellSize * 0.5f;

            float y =
                point.Y *
                cellSize +
                cellSize * 0.5f;

            CircleShape circle =
                new CircleShape(
                    radius)
                {
                    Origin =
                        new Vector2f(
                            radius,
                            radius),

                    Position =
                        new Vector2f(
                            x,
                            y),

                    FillColor =
                        new Color(
                            255,
                            255,
                            255,
                            220)
                };

            window.Draw(
                circle);
        }
    }

    // ============================================================
    // GLOBAL MICRO COORDINATE <-> WORLD PIXEL
    // ============================================================

    private static SpatialCoord ToCoord(
        Vector2f position,
        int z,
        float cellSize)
    {
        int x =
            (int)MathF.Floor(
                position.X /
                cellSize);

        int y =
            (int)MathF.Floor(
                position.Y /
                cellSize);

        x =
            Math.Clamp(
                x,
                0,
                MaxCoord);

        y =
            Math.Clamp(
                y,
                0,
                MaxCoord);

        return new SpatialCoord(
            x,
            y,
            z);
    }
}