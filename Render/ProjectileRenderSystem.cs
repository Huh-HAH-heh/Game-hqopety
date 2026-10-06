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

    public void Draw(
        RenderWindow window,
        ProjectileStore projectiles,
        float tilePixelSize)
    {
        _vertices.Clear();

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
    }
}
