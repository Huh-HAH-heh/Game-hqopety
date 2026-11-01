using System;
using System.IO;
using System.Numerics;
using Core.Unit;
using SFML.Graphics;
using SFML.System;
using SFML.Window;

namespace RimClone.Render;

public sealed class InformationLayersRenderer : IDisposable
{
    private static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
    private static readonly int[] Dy = { 0, 0, 1, -1, 1, -1, 1, -1 };
    private readonly VertexArray _heat = new(PrimitiveType.Triangles);
    private readonly VertexArray _fieldDirections = new(PrimitiveType.Lines);
    private readonly VertexArray _unitVectors = new(PrimitiveType.Lines);
    private readonly VertexArray _squadLines = new(PrimitiveType.Lines);
    private readonly VertexArray _contactLines = new(PrimitiveType.Lines);
    private readonly CircleShape _objectiveRing = new(0.45f, 28)
    {
        FillColor = new Color(0, 0, 0, 0),
        OutlineColor = new Color(255, 230, 95, 240),
        OutlineThickness = 2f
    };
    private readonly Font? _font;
    private readonly Text? _label;
    private readonly Text? _legend;
    private readonly Text? _flowCostLabel;
    private readonly Text? _contactLabel;
    private readonly Text? _stateLegend;

    public InformationLayersRenderer()
    {
        _font = LoadFont();
        if (_font == null)
            return;

        _label = new Text(_font, string.Empty, 12)
        {
            FillColor = new Color(245, 247, 250)
        };
        _legend = new Text(
            _font,
            "Скорость — голубой  |  Сила мотора — оранжевый  |  Стоимость пути — холодный → тёплый",
            10)
        {
            FillColor = new Color(238, 242, 247, 235)
        };
        _flowCostLabel = new Text(_font, string.Empty, 11)
        {
            FillColor = Color.White,
            OutlineColor = new Color(10, 15, 22, 230),
            OutlineThickness = 1f
        };
        _contactLabel = new Text(_font, string.Empty, 10)
        {
            FillColor = new Color(255, 225, 165, 245),
            OutlineColor = new Color(10, 15, 22, 225),
            OutlineThickness = 1f
        };
        _stateLegend = new Text(
            _font,
            "FOLLOW — зелёный  |  ENGAGED — красный  |  REJOIN — жёлтый  |  SEPARATED — серый",
            10)
        {
            FillColor = new Color(238, 242, 247, 235)
        };
    }

    public void Draw(
        RenderWindow window,
        View view,
        SimulationFrameSnapshot snapshot,
        float tilePixels,
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

        if (movementVectors || movementForces)
            DrawUnitVectors(window, snapshot, tilePixels, movementVectors, movementForces);

        SquadMovementDebugSnapshot? squad = FindSquad(snapshot.SquadMovement, selectedSquadId);
        if (squad.HasValue && (formation || memberStates))
            DrawSquad(window, snapshot, squad.Value, tilePixels, formation, memberStates);

        if (squadContacts)
            DrawContacts(window, snapshot, tilePixels, selectedSquadId);

        if (squad.HasValue && _label != null && (formation || memberStates || flowWeights))
        {
            SquadMovementInformation info = squad.Value.Information;
            _label.DisplayedString =
                $"SQUAD {info.SquadId}  {info.Phase}  {InfantryFormation.GetName(info.Formation)}\n" +
                $"Members {info.MemberCount}  Follow {info.FollowingCount}  Engage {info.EngagedCount}  " +
                $"Rejoin {info.RejoiningCount}  Separate {info.SeparatedCount}";
            _label.Position = new Vector2f(
                info.Objective.X * tilePixels + 12f,
                info.Objective.Y * tilePixels + 12f);
            window.Draw(_label);
        }

        float legendY = view.Center.Y - view.Size.Y * 0.5f + 12f;
        if (_legend != null && (movementVectors || movementForces || flowWeights))
        {
            _legend.Position = new Vector2f(
                view.Center.X - view.Size.X * 0.5f + 12f,
                legendY);
            window.Draw(_legend);
            legendY += 18f;
        }

        if (_stateLegend != null && memberStates)
        {
            _stateLegend.Position = new Vector2f(
                view.Center.X - view.Size.X * 0.5f + 12f,
                legendY);
            window.Draw(_stateLegend);
        }
    }

    private void DrawFlowField(
        RenderWindow window,
        View view,
        SimulationFrameSnapshot snapshot,
        float tilePixels,
        int selectedSquadId)
    {
        _heat.Clear();
        _fieldDirections.Clear();

        SquadMovementDebugSnapshot? selected = FindSquad(snapshot.SquadMovement, selectedSquadId);
        if (!selected.HasValue)
            return;

        SquadMovementDebugSnapshot field = selected.Value;
        Vector2f mouseWorld = window.MapPixelToCoords(Mouse.GetPosition(window), view);
        if (field.Width <= 0 || field.Height <= 0 ||
            field.Costs.Length != field.Width * field.Height ||
            field.Directions.Length != field.Costs.Length)
            return;

        int minX = Math.Clamp((int)MathF.Floor((view.Center.X - view.Size.X * 0.5f) / tilePixels), 0, field.Width - 1);
        int maxX = Math.Clamp((int)MathF.Ceiling((view.Center.X + view.Size.X * 0.5f) / tilePixels), 0, field.Width - 1);
        int minY = Math.Clamp((int)MathF.Floor((view.Center.Y - view.Size.Y * 0.5f) / tilePixels), 0, field.Height - 1);
        int maxY = Math.Clamp((int)MathF.Ceiling((view.Center.Y + view.Size.Y * 0.5f) / tilePixels), 0, field.Height - 1);

        // Normalize over the visible viewport, not the whole map.
        float maxCost = 0f;
        for (int y = minY; y <= maxY; y++)
        {
            int row = y * field.Width;
            for (int x = minX; x <= maxX; x++)
            {
                float cost = field.Costs[row + x];
                if (float.IsFinite(cost))
                    maxCost = MathF.Max(maxCost, cost);
            }
        }
        if (maxCost <= 0.001f)
            return;

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
                Color shade = new(
                    (byte)(65f + 185f * t),
                    (byte)(205f - 115f * t),
                    (byte)(225f - 175f * t),
                    112);

                float left = x * tilePixels, top = y * tilePixels;
                float right = left + tilePixels, bottom = top + tilePixels;
                _heat.Append(new Vertex(new Vector2f(left, top), shade));
                _heat.Append(new Vertex(new Vector2f(right, top), shade));
                _heat.Append(new Vertex(new Vector2f(right, bottom), shade));
                _heat.Append(new Vertex(new Vector2f(left, top), shade));
                _heat.Append(new Vertex(new Vector2f(right, bottom), shade));
                _heat.Append(new Vertex(new Vector2f(left, bottom), shade));

                if ((x & 1) != 0 || (y & 1) != 0)
                    continue;

                int direction = field.Directions[cell] - 1;
                if ((uint)direction >= (uint)Dx.Length)
                    continue;

                Vector2f from = new(left + tilePixels * 0.5f, top + tilePixels * 0.5f);
                Vector2f to = new(
                    from.X + Dx[direction] * tilePixels * 0.27f,
                    from.Y + Dy[direction] * tilePixels * 0.27f);
                Line(_fieldDirections, from, to, new Color(20, 35, 55, 185));
            }
        }

        if (_heat.VertexCount > 0)
            window.Draw(_heat);
        if (_fieldDirections.VertexCount > 0)
            window.Draw(_fieldDirections);

        Vector3 objective = field.Information.Objective;
        _objectiveRing.Position = new Vector2f(
            objective.X * tilePixels - tilePixels * 0.45f,
            objective.Y * tilePixels - tilePixels * 0.45f);
        _objectiveRing.Scale = new Vector2f(tilePixels, tilePixels);
        window.Draw(_objectiveRing);

        int mouseX = (int)MathF.Floor(mouseWorld.X / tilePixels);
        int mouseY = (int)MathF.Floor(mouseWorld.Y / tilePixels);
        if (_flowCostLabel != null &&
            (uint)mouseX < (uint)field.Width &&
            (uint)mouseY < (uint)field.Height)
        {
            float hoveredCost = field.Costs[mouseX + mouseY * field.Width];
            _flowCostLabel.DisplayedString = float.IsFinite(hoveredCost)
                ? $"Flow cost ({mouseX},{mouseY}): {hoveredCost:0.00}"
                : $"Flow cost ({mouseX},{mouseY}): unreachable";
            _flowCostLabel.Position = new Vector2f(mouseWorld.X + 12f, mouseWorld.Y + 12f);
            window.Draw(_flowCostLabel);
        }
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

            Vector2f origin = new(unit.Position.X * tilePixels, unit.Position.Y * tilePixels);
            Vector2 velocity = new(unit.Velocity.X, unit.Velocity.Y);
            float speed = velocity.Length();

            if (velocityVisible && speed > 0.04f)
            {
                float length = Math.Clamp(speed * tilePixels * 0.72f, 4f, 72f);
                Arrow(_unitVectors, origin,
                    new Vector2f(origin.X + velocity.X / speed * length,
                                 origin.Y + velocity.Y / speed * length),
                    new Color(80, 220, 255, 235));
            }

            Vector2 force = unit.MotorForce;
            float forceLength = force.Length();
            if (forceVisible && forceLength > 0.5f)
            {
                float length = Math.Clamp(MathF.Sqrt(forceLength) * 0.9f, 4f, 64f);
                Arrow(_unitVectors, origin,
                    new Vector2f(origin.X + force.X / forceLength * length,
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
        bool formation,
        bool states)
    {
        _squadLines.Clear();
        int squadId = squad.Information.SquadId;
        Vector2 center = Vector2.Zero;
        int count = 0;

        foreach (RenderUnitState unit in snapshot.Units)
        {
            if (unit.SquadId != squadId || unit.HitPoints <= 0f)
                continue;

            center += new Vector2(unit.Position.X, unit.Position.Y);
            count++;

            if (states && unit.HasSquadMovementState)
            {
                Color status = unit.SquadMovementState switch
                {
                    SquadMemberMovementState.Engaged => new Color(255, 95, 85, 235),
                    SquadMemberMovementState.Rejoining => new Color(255, 205, 80, 235),
                    SquadMemberMovementState.Separated => new Color(155, 165, 180, 235),
                    _ => new Color(85, 220, 145, 235)
                };
                Cross(_squadLines,
                    new Vector2f(unit.Position.X * tilePixels, unit.Position.Y * tilePixels),
                    MathF.Max(4f, tilePixels * 0.22f),
                    status);
            }
        }

        if (count > 0)
            center /= count;

        if (formation && count > 0)
        {
            SquadMovementInformation info = squad.Information;
            Vector2 forward = info.Forward.LengthSquared() > 0.0001f
                ? Vector2.Normalize(info.Forward)
                : Vector2.UnitX;
            Vector2 side = new(-forward.Y, forward.X);

            foreach (RenderUnitState unit in snapshot.Units)
            {
                if (unit.SquadId != squadId || unit.HitPoints <= 0f ||
                    !unit.HasSquadFormationData)
                    continue;

                Vector2 preferred = info.Center +
                    side * unit.SquadFormationOffset.X -
                    forward * unit.SquadFormationOffset.Y;
                Vector2f preferredPixel = new(preferred.X * tilePixels, preferred.Y * tilePixels);
                Vector2f actualPixel = new(unit.Position.X * tilePixels, unit.Position.Y * tilePixels);

                // Connect the pawn to the actual soft formation preference. It is
                // deliberately a preference marker, not a locked movement slot.
                Line(_squadLines, actualPixel, preferredPixel, new Color(100, 190, 255, 155));
                Cross(_squadLines, preferredPixel, MathF.Max(3f, tilePixels * 0.15f),
                    new Color(100, 190, 255, 190));

                Vector2f terminalPixel = new(
                    unit.SquadMemberGoal.X * tilePixels,
                    unit.SquadMemberGoal.Y * tilePixels);
                Cross(_squadLines, terminalPixel, MathF.Max(3f, tilePixels * 0.13f),
                    new Color(190, 145, 255, 210));
            }
        }

        if (_squadLines.VertexCount > 0)
            window.Draw(_squadLines);
    }

    private void DrawContacts(
        RenderWindow window,
        SimulationFrameSnapshot snapshot,
        float tilePixels,
        int selectedSquadId)
    {
        _contactLines.Clear();
        foreach (RenderSquadContact contact in snapshot.SquadContacts)
        {
            if (selectedSquadId > 0 && contact.SquadId != selectedSquadId)
                continue;

            byte alpha = (byte)(70f + 165f * Math.Clamp(contact.Confidence, 0f, 1f));
            Vector2f from = new(contact.ObserverPosition.X * tilePixels, contact.ObserverPosition.Y * tilePixels);
            Vector2f to = new(contact.EstimatedPosition.X * tilePixels, contact.EstimatedPosition.Y * tilePixels);
            Line(_contactLines, from, to, new Color(255, 190, 80, alpha));

            float radius = Math.Clamp(contact.ErrorRadius * tilePixels, 3f, 180f);
            Circle(_contactLines, to, radius, new Color(255, 185, 70, alpha), 24);

            if (_contactLabel != null)
            {
                _contactLabel.DisplayedString =
                    $"Report {contact.Reporter}  Q={contact.Confidence:0.00}  " +
                    $"{contact.AgeSeconds:0.0}s  ±{contact.ErrorRadius:0.0}";
                _contactLabel.Position = new Vector2f(to.X + 8f, to.Y + 8f);
                window.Draw(_contactLabel);
            }
        }
        if (_contactLines.VertexCount > 0)
            window.Draw(_contactLines);
    }

    private static SquadMovementDebugSnapshot? FindSquad(SquadMovementDebugSnapshot[] snapshots, int squadId)
    {
        if (squadId <= 0)
            return null;
        for (int i = 0; i < snapshots.Length; i++)
            if (snapshots[i].Information.SquadId == squadId)
                return snapshots[i];
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
        Vector2f basePoint = new(to.X - direction.X * 5f, to.Y - direction.Y * 5f);
        Line(lines, to, new Vector2f(basePoint.X + side.X * 3f, basePoint.Y + side.Y * 3f), color);
        Line(lines, to, new Vector2f(basePoint.X - side.X * 3f, basePoint.Y - side.Y * 3f), color);
    }

    private static void Cross(VertexArray lines, Vector2f center, float radius, Color color)
    {
        Line(lines, new Vector2f(center.X - radius, center.Y),
            new Vector2f(center.X + radius, center.Y), color);
        Line(lines, new Vector2f(center.X, center.Y - radius),
            new Vector2f(center.X, center.Y + radius), color);
    }

    private static void Circle(VertexArray lines, Vector2f center, float radius, Color color, int segments)
    {
        float step = MathF.Tau / segments;
        for (int i = 0; i < segments; i++)
        {
            float a = i * step, b = (i + 1) * step;
            Line(lines,
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
            try { return new Font(path); }
            catch { }
        }
        return null;
    }

    public void Dispose()
    {
        _stateLegend?.Dispose();
        _contactLabel?.Dispose();
        _flowCostLabel?.Dispose();
        _legend?.Dispose();
        _label?.Dispose();
        _font?.Dispose();
        _objectiveRing.Dispose();
        _contactLines.Dispose();
        _squadLines.Dispose();
        _unitVectors.Dispose();
        _fieldDirections.Dispose();
        _heat.Dispose();
    }
}
