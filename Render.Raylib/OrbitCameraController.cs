using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// Perspective orbit camera with RTS-style free movement.
/// Right mouse button rotates; WASD pans relative to camera yaw; wheel zooms.
/// </summary>
internal sealed class OrbitCameraController
{
    private readonly float _minimumDistance;
    private readonly float _maximumDistance;

    private Vector3 _target;
    private float _yawRadians;
    private float _pitchRadians;
    private float _distance;
    private bool _rotating;

    public Camera3D Camera { get; private set; }

    public OrbitCameraController(TerrainRenderSnapshot terrain)
    {
        float largestWorldAxis = MathF.Max(terrain.WorldWidth, terrain.WorldDepth);

        _target = new Vector3(
            terrain.WorldWidth * 0.5f,
            MathF.Max(1f, terrain.MaximumHeightMeters * 0.18f),
            terrain.WorldDepth * 0.5f);

        _yawRadians = 0f;
        _pitchRadians = 0.72f;
        _distance = largestWorldAxis * 0.76f;
        _minimumDistance = 18f;
        _maximumDistance = largestWorldAxis * 3.5f;

        UpdateCamera();
    }

    public void Update(float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        if (IsMouseButtonPressed(MouseButton.Right))
        {
            _rotating = true;
            HideCursor();
        }

        if (IsMouseButtonReleased(MouseButton.Right))
        {
            _rotating = false;
            ShowCursor();
        }

        if (_rotating)
        {
            Vector2 mouseDelta = GetMouseDelta();
            const float sensitivity = 0.0035f;

            _yawRadians -= mouseDelta.X * sensitivity;
            _pitchRadians = Math.Clamp(
                _pitchRadians - mouseDelta.Y * sensitivity,
                0.10f,
                1.43f);
        }

        float wheel = GetMouseWheelMove();
        if (wheel != 0f)
        {
            _distance *= MathF.Pow(0.82f, wheel);
            _distance = Math.Clamp(
                _distance,
                _minimumDistance,
                _maximumDistance);
        }

        float moveX = 0f;
        float moveZ = 0f;

        if (IsKeyDown(KeyboardKey.D)) moveX += 1f;
        if (IsKeyDown(KeyboardKey.A)) moveX -= 1f;
        if (IsKeyDown(KeyboardKey.W)) moveZ += 1f;
        if (IsKeyDown(KeyboardKey.S)) moveZ -= 1f;

        if (moveX != 0f || moveZ != 0f)
        {
            Vector3 forward = new(
                -MathF.Sin(_yawRadians),
                0f,
                -MathF.Cos(_yawRadians));

            Vector3 right = new(
                MathF.Cos(_yawRadians),
                0f,
                -MathF.Sin(_yawRadians));

            Vector3 direction = forward * moveZ + right * moveX;
            if (direction.LengthSquared() > 1f)
                direction = Vector3.Normalize(direction);

            float speed = MathF.Max(35f, _distance * 0.72f);
            if (IsKeyDown(KeyboardKey.LeftShift) ||
                IsKeyDown(KeyboardKey.RightShift))
            {
                speed *= 3f;
            }

            _target += direction * (speed * deltaTime);
        }

        float verticalInput = 0f;
        if (IsKeyDown(KeyboardKey.E)) verticalInput += 1f;
        if (IsKeyDown(KeyboardKey.Q)) verticalInput -= 1f;

        if (verticalInput != 0f)
        {
            float speed = MathF.Max(20f, _distance * 0.4f);
            _target.Y = Math.Max(
                0f,
                _target.Y + verticalInput * speed * deltaTime);
        }

        UpdateCamera();
    }

    private void UpdateCamera()
    {
        float horizontalDistance = _distance * MathF.Cos(_pitchRadians);

        Vector3 offset = new(
            MathF.Sin(_yawRadians) * horizontalDistance,
            MathF.Sin(_pitchRadians) * _distance,
            MathF.Cos(_yawRadians) * horizontalDistance);

        Camera = new Camera3D(
            _target + offset,
            _target,
            Vector3.UnitY,
            55f,
            CameraProjection.Perspective);
    }

    public void ReleaseMouse()
    {
        if (!_rotating)
            return;

        _rotating = false;
        ShowCursor();
    }
}
