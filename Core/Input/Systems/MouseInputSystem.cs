using SFML.Graphics;
using SFML.System;
using SFML.Window;
using System.Numerics;

namespace RimClone.Render;

public sealed class GameInput
{
    public Vector2 MoveDirection { get; private set; }

    /// <summary>
    /// Смещение мыши в пикселях за текущий кадр.
    /// Используется для перетаскивания камеры.
    /// </summary>
    public Vector2 CameraPanDelta { get; private set; }

    /// <summary>
    /// ПКМ + Shift.
    /// Отдельный режим камеры, чтобы ПКМ можно было
    /// позднее использовать для других действий.
    /// </summary>
    public bool IsCameraPanMode =>
        Keyboard.IsKeyPressed(
            Keyboard.Key.LShift) ||
        Keyboard.IsKeyPressed(
            Keyboard.Key.RShift);

    private bool _wasPanning;
    private Vector2i _lastMousePosition;

    public void Update(
        RenderWindow window)
    {
        UpdateKeyboard();
        UpdateMouse(window);
    }

    private void UpdateKeyboard()
    {
        float x = 0f;
        float y = 0f;

        if (Keyboard.IsKeyPressed(Keyboard.Key.W) ||
            Keyboard.IsKeyPressed(Keyboard.Key.Up))
        {
            y -= 1f;
        }

        if (Keyboard.IsKeyPressed(Keyboard.Key.S) ||
            Keyboard.IsKeyPressed(Keyboard.Key.Down))
        {
            y += 1f;
        }

        if (Keyboard.IsKeyPressed(Keyboard.Key.A) ||
            Keyboard.IsKeyPressed(Keyboard.Key.Left))
        {
            x -= 1f;
        }

        if (Keyboard.IsKeyPressed(Keyboard.Key.D) ||
            Keyboard.IsKeyPressed(Keyboard.Key.Right))
        {
            x += 1f;
        }

        Vector2 direction =
            new Vector2(x, y);

        if (direction.LengthSquared() > 1f)
            direction =
                Vector2.Normalize(direction);

        MoveDirection =
            direction;
    }

    private void UpdateMouse(
        RenderWindow window)
    {
        Vector2i mousePosition =
            Mouse.GetPosition(window);

        CameraPanDelta =
            Vector2.Zero;

        bool panning =
            Mouse.IsButtonPressed(
                Mouse.Button.Right) &&
            IsCameraPanMode;

        if (!panning)
        {
            _wasPanning = false;
            _lastMousePosition = mousePosition;
            return;
        }

        if (!_wasPanning)
        {
            _wasPanning = true;
            _lastMousePosition = mousePosition;
            return;
        }

        CameraPanDelta =
            new Vector2(
                mousePosition.X -
                _lastMousePosition.X,

                mousePosition.Y -
                _lastMousePosition.Y);

        _lastMousePosition =
            mousePosition;
    }
}