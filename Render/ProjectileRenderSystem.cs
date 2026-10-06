using System;
using System.Numerics;
using Core.Unit;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public sealed class ProjectileRenderSystem
{
    private readonly VertexArray _vertices =
        new VertexArray(
            PrimitiveType.Lines);

    private readonly VertexArray _hitMarker =
        new VertexArray(
            PrimitiveType.Lines);

    public void Draw(
        RenderWindow window,
        ProjectileStore projectiles,
        float tilePixelSize)
    {
        _vertices.Clear();
        _hitMarker.Clear();

        ReadOnlySpan<int> active =
            projectiles.ActiveIndices;

        for (int i = 0;
             i < active.Length;
             i++)
        {
            int projectile =
                active[i];

            Vector3 position =
                projectiles.Position[
                    projectile];

            Vector3 velocity =
                projectiles.Velocity[
                    projectile];

            float speed =
                velocity.Length();

            if (speed < 0.001f)
                continue;

            Vector3 direction =
                velocity /
                speed;

            float trail =
                MathF.Min(
                    0.45f,
                    speed * 0.0015f);

            Vector3 start =
                position -
                direction *
                trail;

            _vertices.Append(
                new Vertex(
                    new Vector2f(
                        start.X *
                        tilePixelSize,
                        start.Y *
                        tilePixelSize),
                    new Color(
                        255,
                        235,
                        130,
                        220)));

            _vertices.Append(
                new Vertex(
                    new Vector2f(
                        position.X *
                        tilePixelSize,
                        position.Y *
                        tilePixelSize),
                    new Color(
                        255,
                        255,
                        220,
                        255)));
        }

        if (_vertices.VertexCount > 0)
            window.Draw(_vertices);

        if (projectiles.TotalHits > 0)
        {
            AppendHitMarker(
                _hitMarker,
                projectiles.LastHitPosition,
                tilePixelSize);

            window.Draw(_hitMarker);
        }
    }

    private static void AppendHitMarker(
        VertexArray vertices,
        Vector3 position,
        float tilePixelSize)
    {
        const int segments = 12;

        float radius =
            0.35f * tilePixelSize;

        for (int i = 0; i < segments; i++)
        {
            float a0 =
                i * MathF.Tau / segments;

            float a1 =
                (i + 1) * MathF.Tau / segments;

            vertices.Append(
                new Vertex(
                    new Vector2f(
                        position.X * tilePixelSize +
                        MathF.Cos(a0) * radius,
                        position.Y * tilePixelSize +
                        MathF.Sin(a0) * radius),
                    new Color(255, 80, 80, 230)));

            vertices.Append(
                new Vertex(
                    new Vector2f(
                        position.X * tilePixelSize +
                        MathF.Cos(a1) * radius,
                        position.Y * tilePixelSize +
                        MathF.Sin(a1) * radius),
                    new Color(255, 190, 80, 230)));
        }
    }
}
