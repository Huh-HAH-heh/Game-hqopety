using System.Numerics;
using SFML.Window;

namespace RimClone.Render;

public sealed class GameInput
{
    public Vector2 MoveDirection { get; private set; }

    public void Update()
    {
        float x = 0f;
        float y = 0f;

        if (Keyboard.IsKeyPressed(
                Keyboard.Key.A) ||
            Keyboard.IsKeyPressed(
                Keyboard.Key.Left))
        {
            x -= 1f;
        }

        if (Keyboard.IsKeyPressed(
                Keyboard.Key.D) ||
            Keyboard.IsKeyPressed(
                Keyboard.Key.Right))
        {
            x += 1f;
        }

        if (Keyboard.IsKeyPressed(
                Keyboard.Key.W) ||
            Keyboard.IsKeyPressed(
                Keyboard.Key.Up))
        {
            y -= 1f;
        }

        if (Keyboard.IsKeyPressed(
                Keyboard.Key.S) ||
            Keyboard.IsKeyPressed(
                Keyboard.Key.Down))
        {
            y += 1f;
        }

        Vector2 direction =
            new Vector2(
                x,
                y);

        if (direction.LengthSquared() > 1f)
        {
            direction =
                Vector2.Normalize(
                    direction);
        }

        MoveDirection =
            direction;
    }
}
