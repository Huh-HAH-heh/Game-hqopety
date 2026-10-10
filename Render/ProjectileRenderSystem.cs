using System;
using System.Numerics;
using Core.Unit;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public sealed class ProjectileRenderSystem : IDisposable
{
    private readonly VertexArray _tracers =
        new VertexArray(PrimitiveType.Lines);

    private readonly VertexArray _impacts =
        new VertexArray(PrimitiveType.Lines);

    public void Draw(
        RenderWindow window,
        ProjectileStore projectiles,
        float tilePixelSize)
    {
        _tracers.Clear();
        _impacts.Clear();

        Vector3[] starts = projectiles.TraceStarts;
        Vector3[] ends = projectiles.TraceEnds;
        ushort[] factions = projectiles.TraceFactions;
        float[] traceLifetimes = projectiles.TraceLifetimes;

        for (int i = 0; i < projectiles.TraceCapacityCount; i++)
        {
            if (traceLifetimes[i] <= 0f)
                continue;

            Vector3 start = starts[i];
            Vector3 end = ends[i];
            Vector2 delta = new Vector2(end.X - start.X, end.Y - start.Y);
            if (delta.LengthSquared() < 0.025f)
                continue;

            GetTeamColors(factions[i], out Color trail, out Color head);

            // Fade old segments instead of losing a fast bullet the same frame it hits.
            byte fade = (byte)(255f * Math.Clamp(
                traceLifetimes[i] / 0.14f, 0f, 1f));
            trail = new Color(trail.R, trail.G, trail.B,
                (byte)(trail.A * fade / 255));
            head = new Color(head.R, head.G, head.B,
                (byte)(head.A * fade / 255));

            Vector2 screenStart = new Vector2(
                start.X * tilePixelSize,
                start.Y * tilePixelSize);
            Vector2 screenEnd = new Vector2(
                end.X * tilePixelSize,
                end.Y * tilePixelSize);

            _tracers.Append(new Vertex(
                new Vector2f(screenStart.X, screenStart.Y),
                trail));
            _tracers.Append(new Vertex(
                new Vector2f(screenEnd.X, screenEnd.Y),
                head));

            Vector2 coreStart = screenEnd - delta * tilePixelSize * 0.22f;
            _tracers.Append(new Vertex(
                new Vector2f(coreStart.X, coreStart.Y),
                new Color(255, 246, 210, fade)));
            _tracers.Append(new Vertex(
                new Vector2f(screenEnd.X, screenEnd.Y),
                new Color(255, 255, 245, fade)));
        }

        if (_tracers.VertexCount > 0)
            window.Draw(_tracers);

        AppendImpacts(projectiles, tilePixelSize);
        if (_impacts.VertexCount > 0)
            window.Draw(_impacts);
    }

    private void AppendImpacts(
        ProjectileStore projectiles,
        float tilePixelSize)
    {
        Vector3[] positions = projectiles.ImpactPositions;
        float[] lifetimes = projectiles.ImpactLifetimes;
        ProjectileImpactKind[] kinds = projectiles.ImpactKinds;

        for (int i = 0; i < projectiles.ImpactCapacityCount; i++)
        {
            float remaining = lifetimes[i];
            if (remaining <= 0f)
                continue;

            ProjectileImpactKind kind = kinds[i];
            float duration = kind switch
            {
                ProjectileImpactKind.MuzzleFlash => 0.10f,
                ProjectileImpactKind.UnitHit => 0.30f,
                _ => 0.24f
            };

            float age = 1f - Math.Clamp(remaining / duration, 0f, 1f);
            Vector3 position = positions[i];
            float x = position.X * tilePixelSize;
            float y = position.Y * tilePixelSize;

            if (kind == ProjectileImpactKind.MuzzleFlash)
            {
                AppendFlash(x, y, tilePixelSize, age);
            }
            else if (kind == ProjectileImpactKind.UnitHit)
            {
                AppendRing(x, y, tilePixelSize, age,
                    new Color(255, 72, 52, (byte)(255f * (1f - age))));
            }
            else
            {
                AppendRing(x, y, tilePixelSize, age,
                    new Color(255, 190, 90, (byte)(230f * (1f - age))));
            }
        }
    }

    private void AppendFlash(
        float x,
        float y,
        float tilePixelSize,
        float age)
    {
        float radius = tilePixelSize * (0.18f + age * 0.42f);
        byte alpha = (byte)(255f * (1f - age));

        for (int ray = 0; ray < 8; ray++)
        {
            float angle = ray * MathF.Tau / 8f;
            float inner = radius * 0.18f;
            float outer = radius * (0.75f + ((ray & 1) == 0 ? 0.7f : 0.25f));

            AppendLine(
                x + MathF.Cos(angle) * inner,
                y + MathF.Sin(angle) * inner,
                x + MathF.Cos(angle) * outer,
                y + MathF.Sin(angle) * outer,
                new Color(255, 245, 190, alpha));
        }
    }

    private void AppendRing(
        float x,
        float y,
        float tilePixelSize,
        float age,
        Color color)
    {
        const int segments = 10;
        float radius = tilePixelSize * (0.22f + age * 0.72f);
        float innerRadius = radius * 0.45f;

        for (int segment = 0; segment < segments; segment++)
        {
            float a0 = segment * MathF.Tau / segments;
            float a1 = (segment + 1) * MathF.Tau / segments;

            AppendLine(
                x + MathF.Cos(a0) * radius,
                y + MathF.Sin(a0) * radius,
                x + MathF.Cos(a1) * radius,
                y + MathF.Sin(a1) * radius,
                color);
        }

        // Four short radial sparks make impacts legible when the ring is tiny.
        for (int ray = 0; ray < 4; ray++)
        {
            float angle = ray * MathF.PI / 2f + MathF.PI / 4f;
            AppendLine(
                x + MathF.Cos(angle) * innerRadius,
                y + MathF.Sin(angle) * innerRadius,
                x + MathF.Cos(angle) * (radius * 1.25f),
                y + MathF.Sin(angle) * (radius * 1.25f),
                color);
        }
    }

    private void AppendLine(
        float x0,
        float y0,
        float x1,
        float y1,
        Color color)
    {
        _impacts.Append(new Vertex(new Vector2f(x0, y0), color));
        _impacts.Append(new Vertex(new Vector2f(x1, y1), color));
    }

    private static void GetTeamColors(
        ushort faction,
        out Color trail,
        out Color head)
    {
        if (faction == 1)
        {
            trail = new Color(45, 140, 255, 50);
            head = new Color(80, 200, 255, 245);
            return;
        }

        if (faction == 2)
        {
            trail = new Color(255, 72, 42, 50);
            head = new Color(255, 128, 68, 245);
            return;
        }

        trail = new Color(255, 220, 115, 45);
        head = new Color(255, 255, 215, 240);
    }

    public void Dispose()
    {
        _tracers.Dispose();
        _impacts.Dispose();
    }
}
