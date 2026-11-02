using System;
using System.IO;
using System.Numerics;
using System.Text;
using Core.Unit;
using SFML.Graphics;
using SFML.System;
using SFML.Window;

namespace RimClone.Render;

public sealed class InformationLayersRenderer : IDisposable
{
    private const float HudWidth = 510f;
    private const float HudHeaderHeight = 28f;

    private static readonly int[] DirectionX = { 1, -1, 0, 0, 1, 1, -1, -1 };
    private static readonly int[] DirectionY = { 0, 0, 1, -1, 1, -1, 1, -1 };

    // World-space information: Flow Field, motion, member states and shared reports.
    private readonly VertexArray _flowHeat = new(PrimitiveType.Triangles);
    private readonly VertexArray _flowDirections = new(PrimitiveType.Lines);
    private readonly VertexArray _unitVectors = new(PrimitiveType.Lines);
    private readonly VertexArray _squadGeometry = new(PrimitiveType.Lines);
    private readonly VertexArray _contactGeometry = new(PrimitiveType.Lines);

    private readonly CircleShape _objectiveRing = new(0.45f, 28)
    {
        FillColor = new Color(0, 0, 0, 0),
        OutlineColor = new Color(255, 230, 95, 245),
        OutlineThickness = 2f
    };

    private readonly RectangleShape _flowTooltipPanel = new(new Vector2f(220f, 28f))
    {
        FillColor = new Color(17, 22, 30, 250),
        OutlineColor = new Color(76, 94, 116, 255),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _contactTooltipPanel = new(new Vector2f(232f, 22f))
    {
        FillColor = new Color(17, 22, 30, 250),
        OutlineColor = new Color(76, 94, 116, 255),
        OutlineThickness = 1f
    };

    // Screen-space HUD uses the same card palette as UnitGroupCardOverlay.
    private readonly RectangleShape _hudPanel = new(new Vector2f(HudWidth, 118f))
    {
        FillColor = new Color(17, 22, 30, 250),
        OutlineColor = new Color(76, 94, 116, 255),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _hudHeader = new(new Vector2f(HudWidth - 2f, HudHeaderHeight))
    {
        FillColor = new Color(31, 40, 52, 255)
    };

    private readonly RectangleShape _hudAccent = new(new Vector2f(3f, 116f))
    {
        FillColor = new Color(112, 164, 218, 245)
    };

    private readonly Font? _font;
    private readonly Text? _hudTitle;
    private readonly Text? _hudBody;
    private readonly Text? _flowCostLabel;
    private readonly Text? _contactLabel;

    private string _flowStatus = "Flow Field: отключён";
    private float _hudX = 18f;
    private float _hudY = 72f;
    private float _hudHeight = 118f;
    private float _hudDragOffsetX;
    private float _hudDragOffsetY;
    private bool _hudDragging;
    private bool _hudPositionInitialized;

    public InformationLayersRenderer()
    {
        _font = LoadFont();
        if (_font == null)
            return;

        _hudTitle = new Text(_font, "ИНФО", 12)
        {
            FillColor = new Color(239, 243, 249),
            Style = Text.Styles.Bold
        };
        _hudBody = new Text(_font, string.Empty, 9)
        {
            FillColor = new Color(205, 214, 225)
        };
        _flowCostLabel = new Text(_font, string.Empty, 10)
        {
            FillColor = Color.White
        };
        _contactLabel = new Text(_font, string.Empty, 9)
        {
            FillColor = new Color(255, 225, 165, 245)
        };
    }

    /// <summary>Draw world-space debug geometry while the game camera view is active.</summary>
    public void DrawWorld(
        RenderWindow window,
        View view,
        SimulationFrameSnapshot snapshot,
        float tilePixels,
        float zoomLevel,
        int selectedSquadId,
        bool movementVectors,
        bool movementForces,
        bool flowWeights,
        bool formation,
        bool memberStates,
        bool squadContacts)
    {
        if (flowWeights)
            DrawFlowField(window, view, snapshot, tilePixels, selectedSquadId);
        else
            _flowStatus = "Flow Field: слой выключен";

        if (movementVectors || movementForces)
        {
            DrawUnitVectors(
                window,
                snapshot,
                tilePixels,
                movementVectors,
                movementForces);
        }

        SquadMovementDebugSnapshot? squad =
            FindSquad(snapshot.SquadMovement, selectedSquadId);

        if (squad.HasValue && (formation || memberStates))
        {
            DrawSquad(
                window,
                snapshot,
                squad.Value,
                tilePixels,
                zoomLevel,
                formation,
                memberStates);
        }

        if (squadContacts)
            DrawContacts(window, snapshot, tilePixels, selectedSquadId);
    }

    public bool ContainsHud(Vector2i position) =>
        _hudPanel.GetGlobalBounds().Contains(position);

    public bool HandleHudMousePressed(Vector2i position, uint windowWidth, uint windowHeight)
    {
        if (!ContainsHud(position))
            return false;

        if (_hudHeader.GetGlobalBounds().Contains(position))
        {
            _hudDragging = true;
            _hudDragOffsetX = position.X - _hudPanel.Position.X;
            _hudDragOffsetY = position.Y - _hudPanel.Position.Y;
        }

        return true;
    }

    public bool HandleHudMouseMoved(Vector2i position, uint windowWidth, uint windowHeight)
    {
        if (!_hudDragging)
            return false;

        _hudX = Math.Clamp(position.X - _hudDragOffsetX, 8f,
            Math.Max(8f, windowWidth - HudWidth - 8f));
        _hudY = Math.Clamp(position.Y - _hudDragOffsetY, 8f,
            Math.Max(8f, windowHeight - _hudHeight - 8f));
        return true;
    }

    public bool HandleHudMouseReleased()
    {
        if (!_hudDragging)
            return false;
        _hudDragging = false;
        return true;
    }

    /// <summary>Draw a movable screen-space information panel.</summary>
    public void DrawHud(
        RenderWindow window,
        uint windowWidth,
        uint windowHeight,
        SimulationFrameSnapshot snapshot,
        int selectedSquadId,
        bool movementVectors,
        bool movementForces,
        bool flowWeights,
        bool formation,
        bool memberStates,
        bool squadContacts)
    {
        bool anyLayer = movementVectors || movementForces || flowWeights ||
            formation || memberStates || squadContacts;
        if (!anyLayer || _hudTitle == null || _hudBody == null)
            return;

        SquadMovementDebugSnapshot? selected =
            FindSquad(snapshot.SquadMovement, selectedSquadId);
        StringBuilder body = new(256);
        int rows = 0;

        void AddRow(string value)
        {
            if (rows > 0)
                body.AppendLine();
            body.Append(value);
            rows++;
        }

        if (selected.HasValue)
        {
            SquadMovementInformation info = selected.Value.Information;
            _hudTitle.DisplayedString =
                $"ИНФО  •  ОТРЯД {info.SquadId}  •  {info.Phase}  •  {InfantryFormation.GetName(info.Formation)}";
            AddRow(
                $"Участники {info.MemberCount}  •  Следуют {info.FollowingCount}  •  В бою {info.EngagedCount}  •  Возвращаются {info.RejoiningCount}  •  Отделены {info.SeparatedCount}");
        }
        else
        {
            _hudTitle.DisplayedString = "ИНФО  •  ДИАГНОСТИКА ДВИЖЕНИЯ";
            if (flowWeights || formation || memberStates || squadContacts)
                AddRow("Выбери бойцов одного отряда, чтобы увидеть коллективные данные.");
        }

        if (movementVectors)
            AddRow("Скорость — голубая стрелка: текущая скорость и направление юнита.");
        if (movementForces)
            AddRow("Сила мотора — оранжевая стрелка: тяга, торможение и боковая коррекция.");
        if (flowWeights)
            AddRow(_flowStatus);
        if (formation)
            AddRow("Формация — голубая линия: позиция → мягкое предпочтение; фиолетовый крест — цель.");
        if (memberStates)
            AddRow("Состояния: FOLLOW — зелёный • ENGAGED — красный • REJOIN — жёлтый • SEPARATED — серый.");
        if (squadContacts)
            AddRow("Доклады: жёлтая оценка позиции; круг — радиус ошибки; яркость — доверие.");

        float panelHeight = HudHeaderHeight + 12f + rows * 13f + 8f;
        if (!_hudPositionInitialized)
        {
            _hudX = 18f;
            _hudY = 72f;
            _hudPositionInitialized = true;
        }

        _hudHeight = panelHeight;
        _hudX = Math.Clamp(_hudX, 8f, Math.Max(8f, windowWidth - HudWidth - 8f));
        _hudY = Math.Clamp(_hudY, 8f, Math.Max(8f, windowHeight - panelHeight - 8f));
        float x = _hudX;
        float y = _hudY;

        _hudPanel.Size = new Vector2f(HudWidth, panelHeight);
        _hudPanel.Position = new Vector2f(x, y);
        _hudHeader.Position = new Vector2f(x + 1f, y + 1f);
        _hudAccent.Size = new Vector2f(3f, panelHeight - 2f);
        _hudAccent.Position = new Vector2f(x + 1f, y + 1f);
        _hudTitle.Position = new Vector2f(x + 11f, y + 7f);
        _hudBody.DisplayedString = body.ToString();
        _hudBody.Position = new Vector2f(x + 12f, y + HudHeaderHeight + 7f);

        window.Draw(_hudPanel);
        window.Draw(_hudHeader);
        window.Draw(_hudAccent);
        window.Draw(_hudTitle);
        window.Draw(_hudBody);
    }

    private void DrawFlowField(
        RenderWindow window,
        View view,
        SimulationFrameSnapshot snapshot,
        float tilePixels,
        int selectedSquadId)
    {
        _flowHeat.Clear();
        _flowDirections.Clear();
        _flowStatus = "Flow Field: отряд не выбран";

        SquadMovementDebugSnapshot? selected =
            FindSquad(snapshot.SquadMovement, selectedSquadId);
        if (!selected.HasValue)
            return;

        SquadMovementDebugSnapshot field = selected.Value;
        if (!field.Information.FlowFieldReady)
        {
            _flowStatus = "Flow Field ещё не построен: выдай отряду общий приказ Move.";
            return;
        }

        if (field.Width <= 0 ||
            field.Height <= 0 ||
            field.Costs.Length != field.Width * field.Height ||
            field.Directions.Length != field.Costs.Length)
        {
            _flowStatus = "Flow Field готовится или недоступен для отрисовки.";
            return;
        }

        Vector2f mouseWorld = window.MapPixelToCoords(Mouse.GetPosition(window), view);
        int minX = Math.Clamp(
            (int)MathF.Floor((view.Center.X - view.Size.X * 0.5f) / tilePixels),
            0,
            field.Width - 1);
        int maxX = Math.Clamp(
            (int)MathF.Ceiling((view.Center.X + view.Size.X * 0.5f) / tilePixels),
            0,
            field.Width - 1);
        int minY = Math.Clamp(
            (int)MathF.Floor((view.Center.Y - view.Size.Y * 0.5f) / tilePixels),
            0,
            field.Height - 1);
        int maxY = Math.Clamp(
            (int)MathF.Ceiling((view.Center.Y + view.Size.Y * 0.5f) / tilePixels),
            0,
            field.Height - 1);

        // Normalize the visible part of the field, but report the absolute
        // path-cost values in the HUD and under the mouse pointer.
        float minCost = float.PositiveInfinity;
        float maxCost = 0f;
        for (int y = minY; y <= maxY; y++)
        {
            int row = y * field.Width;
            for (int x = minX; x <= maxX; x++)
            {
                float cost = field.Costs[row + x];
                if (!float.IsFinite(cost))
                    continue;

                minCost = MathF.Min(minCost, cost);
                maxCost = MathF.Max(maxCost, cost);
            }
        }

        if (maxCost <= 0.001f)
        {
            _flowStatus = "Flow Field построен: видимая область почти совпадает с целью.";
            return;
        }

        _flowStatus =
            $"Flow Field: стоимость до цели {minCost:0.0}–{maxCost:0.0}; наведи курсор на клетку для точного значения.";

        for (int y = minY; y <= maxY; y++)
        {
            int row = y * field.Width;
            for (int x = minX; x <= maxX; x++)
            {
                int cell = row + x;
                float cost = field.Costs[cell];
                if (!float.IsFinite(cost))
                    continue;

                float t = Math.Clamp(cost / maxCost, 0f, 1f);
                // High opacity and a saturated cyan-to-orange scale keep costs
                // visible on both pale and dark terrain.
                Color shade = new(
                    (byte)(30f + 225f * t),
                    (byte)(190f - 110f * t),
                    (byte)(245f - 205f * t),
                    220);

                float left = x * tilePixels;
                float top = y * tilePixels;
                float right = left + tilePixels;
                float bottom = top + tilePixels;

                _flowHeat.Append(new Vertex(new Vector2f(left, top), shade));
                _flowHeat.Append(new Vertex(new Vector2f(right, top), shade));
                _flowHeat.Append(new Vertex(new Vector2f(right, bottom), shade));
                _flowHeat.Append(new Vertex(new Vector2f(left, top), shade));
                _flowHeat.Append(new Vertex(new Vector2f(right, bottom), shade));
                _flowHeat.Append(new Vertex(new Vector2f(left, bottom), shade));

                // Sparse direction glyphs preserve heatmap readability.
                if ((x & 1) != 0 || (y & 1) != 0)
                    continue;

                int direction = field.Directions[cell] - 1;
                if ((uint)direction >= (uint)DirectionX.Length)
                    continue;

                Vector2f from = new(
                    left + tilePixels * 0.5f,
                    top + tilePixels * 0.5f);
                Vector2f to = new(
                    from.X + DirectionX[direction] * tilePixels * 0.27f,
                    from.Y + DirectionY[direction] * tilePixels * 0.27f);
                Line(_flowDirections, from, to, new Color(12, 22, 34, 245));
            }
        }

        if (_flowHeat.VertexCount > 0)
            window.Draw(_flowHeat);
        if (_flowDirections.VertexCount > 0)
            window.Draw(_flowDirections);

        Vector3 objective = field.Information.Objective;
        _objectiveRing.Position = new Vector2f(
            objective.X * tilePixels - tilePixels * 0.45f,
            objective.Y * tilePixels - tilePixels * 0.45f);
        _objectiveRing.Scale = new Vector2f(tilePixels, tilePixels);
        window.Draw(_objectiveRing);

        int mouseX = (int)MathF.Floor(mouseWorld.X / tilePixels);
        int mouseY = (int)MathF.Floor(mouseWorld.Y / tilePixels);
        if (_flowCostLabel == null ||
            (uint)mouseX >= (uint)field.Width ||
            (uint)mouseY >= (uint)field.Height)
        {
            return;
        }

        float hoveredCost = field.Costs[mouseX + mouseY * field.Width];
        _flowCostLabel.DisplayedString = float.IsFinite(hoveredCost)
            ? $"Cost ({mouseX},{mouseY}): {hoveredCost:0.00}"
            : $"Cost ({mouseX},{mouseY}): unreachable";

        Vector2f tooltipPosition = new(mouseWorld.X + 12f, mouseWorld.Y + 12f);
        float viewRight = view.Center.X + view.Size.X * 0.5f;
        float viewBottom = view.Center.Y + view.Size.Y * 0.5f;
        if (tooltipPosition.X + 220f > viewRight)
            tooltipPosition.X = mouseWorld.X - 232f;
        if (tooltipPosition.Y + 28f > viewBottom)
            tooltipPosition.Y = mouseWorld.Y - 38f;

        _flowTooltipPanel.Position = tooltipPosition;
        _flowCostLabel.Position = new Vector2f(
            tooltipPosition.X + 8f,
            tooltipPosition.Y + 6f);
        window.Draw(_flowTooltipPanel);
        window.Draw(_flowCostLabel);
    }

    private void DrawUnitVectors(
        RenderWindow window,
        SimulationFrameSnapshot snapshot,
        float tilePixels,
        bool velocityVisible,
        bool forceVisible)
    {
        _unitVectors.Clear();

        foreach (RenderUnitState unit in snapshot.Units)
        {
            if (unit.HitPoints <= 0f)
                continue;

            Vector2f origin = new(
                unit.Position.X * tilePixels,
                unit.Position.Y * tilePixels);

            Vector2 velocity = new(unit.Velocity.X, unit.Velocity.Y);
            float speed = velocity.Length();
            if (velocityVisible && speed > 0.04f)
            {
                float length = Math.Clamp(speed * tilePixels * 0.72f, 4f, 72f);
                Arrow(
                    _unitVectors,
                    origin,
                    new Vector2f(
                        origin.X + velocity.X / speed * length,
                        origin.Y + velocity.Y / speed * length),
                    new Color(80, 220, 255, 235));
            }

            Vector2 force = unit.MotorForce;
            float forceLength = force.Length();
            if (forceVisible && forceLength > 0.5f)
            {
                float length = Math.Clamp(MathF.Sqrt(forceLength) * 0.9f, 4f, 64f);
                Arrow(
                    _unitVectors,
                    origin,
                    new Vector2f(
                        origin.X + force.X / forceLength * length,
                        origin.Y + force.Y / forceLength * length),
                    new Color(255, 150, 55, 245));
            }
        }

        if (_unitVectors.VertexCount > 0)
            window.Draw(_unitVectors);
    }

    private void DrawSquad(
        RenderWindow window,
        SimulationFrameSnapshot snapshot,
        SquadMovementDebugSnapshot squad,
        float tilePixels,
        float zoomLevel,
        bool formation,
        bool states)
    {
        _squadGeometry.Clear();
        int squadId = squad.Information.SquadId;
        int memberCount = 0;

        foreach (RenderUnitState unit in snapshot.Units)
        {
            if (unit.SquadId != squadId || unit.HitPoints <= 0f)
                continue;

            memberCount++;
            if (!states || !unit.HasSquadMovementState)
                continue;

            Color stateColor = unit.SquadMovementState switch
            {
                SquadMemberMovementState.Engaged => new Color(255, 95, 85, 235),
                SquadMemberMovementState.Rejoining => new Color(255, 205, 80, 235),
                SquadMemberMovementState.Separated => new Color(155, 165, 180, 235),
                _ => new Color(85, 220, 145, 235)
            };

            Cross(
                _squadGeometry,
                new Vector2f(
                    unit.Position.X * tilePixels,
                    unit.Position.Y * tilePixels),
                MathF.Max(4f, tilePixels * 0.22f),
                stateColor);
        }

        if (formation && memberCount > 0)
        {
            SquadMovementInformation info = squad.Information;
            Vector2 forward = info.Forward.LengthSquared() > 0.0001f
                ? Vector2.Normalize(info.Forward)
                : Vector2.UnitX;
            Vector2 side = new(-forward.Y, forward.X);

            foreach (RenderUnitState unit in snapshot.Units)
            {
                if (unit.SquadId != squadId ||
                    unit.HitPoints <= 0f ||
                    !unit.HasSquadFormationData)
                {
                    continue;
                }

                Vector2 preferred =
                    info.Center +
                    side * unit.SquadFormationOffset.X -
                    forward * unit.SquadFormationOffset.Y;

                Vector2f actualPixel = new(
                    unit.Position.X * tilePixels,
                    unit.Position.Y * tilePixels);
                Vector2f preferredPixel = new(
                    preferred.X * tilePixels,
                    preferred.Y * tilePixels);
                Vector2f terminalPixel = new(
                    unit.SquadMemberGoal.X * tilePixels,
                    unit.SquadMemberGoal.Y * tilePixels);

                // Keep world-space diagnostics readable at every zoom:
                // distant views omit tiny correction links, while slot markers
                // retain a stable screen size.
                Vector2 correction = preferred - new Vector2(
                    unit.Position.X, unit.Position.Y);
                float minimumLinkLength = 7f * zoomLevel / MathF.Max(0.01f, tilePixels);
                float markerRadius = Math.Clamp(3f * zoomLevel, 0.65f, 7f);

                if (correction.Length() >= minimumLinkLength)
                {
                    byte alpha = (byte)Math.Clamp(
                        95f + correction.Length() * 10f,
                        95f,
                        190f);
                    Line(
                        _squadGeometry,
                        actualPixel,
                        preferredPixel,
                        new Color(100, 190, 255, alpha));
                }

                Cross(
                    _squadGeometry,
                    preferredPixel,
                    markerRadius,
                    new Color(100, 190, 255, 220));
                Cross(
                    _squadGeometry,
                    terminalPixel,
                    markerRadius * 0.82f,
                    new Color(190, 145, 255, 225));
            }
        }

        if (_squadGeometry.VertexCount > 0)
            window.Draw(_squadGeometry);
    }

    private void DrawContacts(
        RenderWindow window,
        SimulationFrameSnapshot snapshot,
        float tilePixels,
        int selectedSquadId)
    {
        _contactGeometry.Clear();

        foreach (RenderSquadContact contact in snapshot.SquadContacts)
        {
            if (selectedSquadId > 0 && contact.SquadId != selectedSquadId)
                continue;

            byte alpha = (byte)(70f + 165f * Math.Clamp(contact.Confidence, 0f, 1f));
            Vector2f observer = new(
                contact.ObserverPosition.X * tilePixels,
                contact.ObserverPosition.Y * tilePixels);
            Vector2f estimate = new(
                contact.EstimatedPosition.X * tilePixels,
                contact.EstimatedPosition.Y * tilePixels);

            Line(_contactGeometry, observer, estimate, new Color(255, 190, 80, alpha));

            float radius = Math.Clamp(contact.ErrorRadius * tilePixels, 3f, 180f);
            Circle(
                _contactGeometry,
                estimate,
                radius,
                new Color(255, 185, 70, alpha),
                24);

            if (_contactLabel != null)
            {
                _contactLabel.DisplayedString =
                    $"Report {contact.Reporter}  Q={contact.Confidence:0.00}  " +
                    $"{contact.AgeSeconds:0.0}s  ±{contact.ErrorRadius:0.0}";
                _contactLabel.Position = new Vector2f(estimate.X + 8f, estimate.Y + 7f);
                _contactTooltipPanel.Position = new Vector2f(estimate.X + 4f, estimate.Y + 4f);
                window.Draw(_contactTooltipPanel);
                window.Draw(_contactLabel);
            }
        }

        if (_contactGeometry.VertexCount > 0)
            window.Draw(_contactGeometry);
    }

    private static SquadMovementDebugSnapshot? FindSquad(
        SquadMovementDebugSnapshot[] snapshots,
        int squadId)
    {
        if (squadId <= 0)
            return null;

        for (int i = 0; i < snapshots.Length; i++)
        {
            if (snapshots[i].Information.SquadId == squadId)
                return snapshots[i];
        }

        return null;
    }

    private static void Arrow(VertexArray lines, Vector2f from, Vector2f to, Color color)
    {
        Line(lines, from, to, color);

        Vector2 delta = new(to.X - from.X, to.Y - from.Y);
        float length = delta.Length();
        if (length < 0.1f)
            return;

        Vector2 direction = delta / length;
        Vector2 side = new(-direction.Y, direction.X);
        Vector2f basePoint = new(
            to.X - direction.X * 5f,
            to.Y - direction.Y * 5f);

        Line(
            lines,
            to,
            new Vector2f(basePoint.X + side.X * 3f, basePoint.Y + side.Y * 3f),
            color);
        Line(
            lines,
            to,
            new Vector2f(basePoint.X - side.X * 3f, basePoint.Y - side.Y * 3f),
            color);
    }

    private static void Cross(VertexArray lines, Vector2f center, float radius, Color color)
    {
        Line(
            lines,
            new Vector2f(center.X - radius, center.Y),
            new Vector2f(center.X + radius, center.Y),
            color);
        Line(
            lines,
            new Vector2f(center.X, center.Y - radius),
            new Vector2f(center.X, center.Y + radius),
            color);
    }

    private static void Circle(
        VertexArray lines,
        Vector2f center,
        float radius,
        Color color,
        int segments)
    {
        float step = MathF.Tau / segments;
        for (int i = 0; i < segments; i++)
        {
            float a = i * step;
            float b = (i + 1) * step;
            Line(
                lines,
                new Vector2f(center.X + MathF.Cos(a) * radius, center.Y + MathF.Sin(a) * radius),
                new Vector2f(center.X + MathF.Cos(b) * radius, center.Y + MathF.Sin(b) * radius),
                color);
        }
    }

    private static void Line(VertexArray lines, Vector2f from, Vector2f to, Color color)
    {
        lines.Append(new Vertex(from, color));
        lines.Append(new Vertex(to, color));
    }

    private static Font? LoadFont()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (string name in new[] { "segoeui.ttf", "arial.ttf", "tahoma.ttf" })
        {
            string path = Path.Combine(folder, name);
            if (!File.Exists(path))
                continue;

            try
            {
                return new Font(path);
            }
            catch
            {
            }
        }

        return null;
    }

    public void Dispose()
    {
        _contactLabel?.Dispose();
        _flowCostLabel?.Dispose();
        _hudBody?.Dispose();
        _hudTitle?.Dispose();
        _font?.Dispose();

        _contactTooltipPanel.Dispose();
        _flowTooltipPanel.Dispose();
        _hudAccent.Dispose();
        _hudHeader.Dispose();
        _hudPanel.Dispose();

        _objectiveRing.Dispose();
        _contactGeometry.Dispose();
        _squadGeometry.Dispose();
        _unitVectors.Dispose();
        _flowDirections.Dispose();
        _flowHeat.Dispose();
    }
}
