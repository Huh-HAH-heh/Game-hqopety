using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// Smooth RTS-style orbit camera.
/// Right mouse rotates, middle mouse pans, WASD moves, the wheel zooms.
/// Camera state is damped with a time-based critically damped spring.
/// </summary>
internal sealed class OrbitCameraController
{
    private const float RotationSensitivity = 0.0020f;
    private const float RotationSmoothTime = 0.065f;
    private const float MovementSmoothTime = 0.12f;
    private const float ZoomSmoothTime = 0.15f;
    private const float MinimumPitch = 0.22f;
    private const float MaximumPitch = 1.36f;

    private readonly float _minimumDistance;
    private readonly float _maximumDistance;

    private Vector3 _target;
    private Vector3 _desiredTarget;
    private Vector3 _targetVelocity;

    private float _yawRadians;
    private float _desiredYawRadians;
    private float _yawVelocity;

    private float _pitchRadians;
    private float _desiredPitchRadians;
    private float _pitchVelocity;

    private float _distance;
    private float _desiredDistance;
    private float _distanceVelocity;

    private MouseMode _mouseMode;
    private bool _ignoreFirstMouseDelta;

    private enum MouseMode : byte
    {
        None,
        Orbit,
        Pan
    }

    public Camera3D Camera { get; private set; }

    public OrbitCameraController(TerrainRenderSnapshot terrain)
    {
        float largestWorldAxis = MathF.Max(
            terrain.WorldWidth,
            terrain.WorldDepth);

        _target = new Vector3(
            terrain.WorldWidth * 0.5f,
            MathF.Max(1f, terrain.MaximumHeightMeters * 0.18f),
            terrain.WorldDepth * 0.5f);

        _desiredTarget = _target;

        _yawRadians = 0f;
        _desiredYawRadians = _yawRadians;

        _pitchRadians = 0.72f;
        _desiredPitchRadians = _pitchRadians;

        _distance = largestWorldAxis * 0.76f;
        _desiredDistance = _distance;

        _minimumDistance = 18f;
        _maximumDistance = largestWorldAxis * 3.5f;

        UpdateCamera();
    }

    public void Update(float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        // Prevent a debugger pause or window drag from producing a huge camera step.
        deltaTime = Math.Clamp(deltaTime, 0f, 0.05f);

        UpdateMouseMode();
        UpdateMouseInput();
        UpdateZoomInput();
        UpdateKeyboardInput(deltaTime);

        _target = SmoothDamp(
            _target,
            _desiredTarget,
            ref _targetVelocity,
            MovementSmoothTime,
            deltaTime);

        _yawRadians = SmoothDamp(
            _yawRadians,
            _desiredYawRadians,
            ref _yawVelocity,
            RotationSmoothTime,
            deltaTime);

        _pitchRadians = SmoothDamp(
            _pitchRadians,
            _desiredPitchRadians,
            ref _pitchVelocity,
            RotationSmoothTime,
            deltaTime);

        _distance = SmoothDamp(
            _distance,
            _desiredDistance,
            ref _distanceVelocity,
            ZoomSmoothTime,
            deltaTime);

        UpdateCamera();
    }

    private void UpdateMouseMode()
    {
        if (_mouseMode == MouseMode.None)
        {
            if (IsMouseButtonPressed(MouseButton.Right))
            {
                _mouseMode = MouseMode.Orbit;
                _ignoreFirstMouseDelta = true;
                DisableCursor();
            }
            else if (IsMouseButtonPressed(MouseButton.Middle))
            {
                _mouseMode = MouseMode.Pan;
                _ignoreFirstMouseDelta = true;
                DisableCursor();
            }

            return;
        }

        bool released =
            (_mouseMode == MouseMode.Orbit &&
             IsMouseButtonReleased(MouseButton.Right)) ||
            (_mouseMode == MouseMode.Pan &&
             IsMouseButtonReleased(MouseButton.Middle));

        if (released)
        {
            _mouseMode = MouseMode.None;
            EnableCursor();
        }
    }

    private void UpdateMouseInput()
    {
        if (_mouseMode == MouseMode.None)
            return;

        Vector2 mouseDelta = GetMouseDelta();
        if (_ignoreFirstMouseDelta)
        {
            _ignoreFirstMouseDelta = false;
            return;
        }

        if (_mouseMode == MouseMode.Orbit)
        {
            _desiredYawRadians -= mouseDelta.X * RotationSensitivity;
            _desiredPitchRadians = Math.Clamp(
                _desiredPitchRadians - mouseDelta.Y * RotationSensitivity,
                MinimumPitch,
                MaximumPitch);

            return;
        }

        if (_mouseMode == MouseMode.Pan)
        {
            float viewportHeight = Math.Max(1, GetScreenHeight());
            float worldUnitsPerPixel =
                2f * _desiredDistance *
                MathF.Tan(55f * MathF.PI / 360f) /
                viewportHeight;

            Vector3 right = new(
                MathF.Cos(_desiredYawRadians),
                0f,
                -MathF.Sin(_desiredYawRadians));

            Vector3 forward = new(
                -MathF.Sin(_desiredYawRadians),
                0f,
                -MathF.Cos(_desiredYawRadians));

            _desiredTarget -= right * (mouseDelta.X * worldUnitsPerPixel);
            _desiredTarget += forward * (mouseDelta.Y * worldUnitsPerPixel);
        }
    }

    private void UpdateZoomInput()
    {
        float wheel = GetMouseWheelMove();
        if (wheel == 0f)
            return;

        // One wheel notch changes the target distance by about ten percent.
        _desiredDistance *= MathF.Pow(0.90f, wheel);
        _desiredDistance = Math.Clamp(
            _desiredDistance,
            _minimumDistance,
            _maximumDistance);
    }

    private void UpdateKeyboardInput(float deltaTime)
    {
        float moveX = 0f;
        float moveZ = 0f;

        if (IsKeyDown(KeyboardKey.D)) moveX += 1f;
        if (IsKeyDown(KeyboardKey.A)) moveX -= 1f;
        if (IsKeyDown(KeyboardKey.W)) moveZ += 1f;
        if (IsKeyDown(KeyboardKey.S)) moveZ -= 1f;

        if (moveX != 0f || moveZ != 0f)
        {
            Vector3 forward = new(
                -MathF.Sin(_desiredYawRadians),
                0f,
                -MathF.Cos(_desiredYawRadians));

            Vector3 right = new(
                MathF.Cos(_desiredYawRadians),
                0f,
                -MathF.Sin(_desiredYawRadians));

            Vector3 direction = forward * moveZ + right * moveX;
            if (direction.LengthSquared() > 1f)
                direction = Vector3.Normalize(direction);

            float speed = MathF.Max(35f, _desiredDistance * 0.58f);
            if (IsKeyDown(KeyboardKey.LeftShift) ||
                IsKeyDown(KeyboardKey.RightShift))
            {
                speed *= 2.25f;
            }

            _desiredTarget += direction * (speed * deltaTime);
        }

        float verticalInput = 0f;
        if (IsKeyDown(KeyboardKey.E)) verticalInput += 1f;
        if (IsKeyDown(KeyboardKey.Q)) verticalInput -= 1f;

        if (verticalInput != 0f)
        {
            float speed = MathF.Max(20f, _desiredDistance * 0.32f);
            _desiredTarget.Y = Math.Max(
                0f,
                _desiredTarget.Y + verticalInput * speed * deltaTime);
        }
    }

    private void UpdateCamera()
    {
        float horizontalDistance =
            _distance * MathF.Cos(_pitchRadians);

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

    private static Vector3 SmoothDamp(
        Vector3 current,
        Vector3 target,
        ref Vector3 velocity,
        float smoothTime,
        float deltaTime)
    {
        float omega = 2f / MathF.Max(0.0001f, smoothTime);
        float x = omega * deltaTime;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);

        Vector3 change = current - target;
        Vector3 temp = (velocity + omega * change) * deltaTime;

        velocity = (velocity - omega * temp) * exp;
        return target + (change + temp) * exp;
    }

    private static float SmoothDamp(
        float current,
        float target,
        ref float velocity,
        float smoothTime,
        float deltaTime)
    {
        float omega = 2f / MathF.Max(0.0001f, smoothTime);
        float x = omega * deltaTime;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);

        float change = current - target;
        float temp = (velocity + omega * change) * deltaTime;

        velocity = (velocity - omega * temp) * exp;
        return target + (change + temp) * exp;
    }

    public void ReleaseMouse()
    {
        if (_mouseMode == MouseMode.None)
            return;

        _mouseMode = MouseMode.None;
        EnableCursor();
    }
}
