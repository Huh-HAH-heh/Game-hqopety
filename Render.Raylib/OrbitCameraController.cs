using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// RTS-style terrain camera:
/// RMB orbits, MMB pans, wheel zooms toward the cursor, WASD moves.
/// </summary>
internal sealed class OrbitCameraController
{
    private const float FieldOfViewDegrees = 55f;
    private const float RotationSensitivity = 0.0018f;
    private const float RotationSmoothTime = 0.055f;
    private const float MovementSmoothTime = 0.075f;
    private const float ZoomSmoothTime = 0.095f;
    private const float MinimumPitch = 0.53f;
    private const float TopDownPitch = 1.54f;
    private const float MaximumPitch = TopDownPitch;
    private const float FitMargin = 1.08f;

    private readonly float _minimumDistance;
    private readonly float _maximumDistance;
    private readonly Vector3 _topDownTarget;

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

        _topDownTarget = new Vector3(
            terrain.WorldWidth * 0.5f,
            MathF.Max(1f, terrain.MaximumHeightMeters * 0.12f),
            terrain.WorldDepth * 0.5f);

        _target = _topDownTarget;
        _desiredTarget = _target;

        _yawRadians = 0f;
        _desiredYawRadians = _yawRadians;

        _pitchRadians = TopDownPitch;
        _desiredPitchRadians = _pitchRadians;

        _minimumDistance = 18f;
        _maximumDistance = largestWorldAxis * 3.5f;

        _distance = CalculateFitDistance(
            terrain.WorldWidth,
            terrain.WorldDepth,
            GetScreenWidth(),
            GetScreenHeight());
        _desiredDistance = _distance;

        UpdateCamera();
    }

    public void Update(float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        // Avoid a large camera jump after a debugger pause or window drag.
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
            // Dragging right moves the camera around the focus to the right.
            _desiredYawRadians += mouseDelta.X * RotationSensitivity;
            _desiredPitchRadians = Math.Clamp(
                _desiredPitchRadians - mouseDelta.Y * RotationSensitivity,
                MinimumPitch,
                MaximumPitch);

            return;
        }

        if (_mouseMode == MouseMode.Pan)
            PanByMouseDelta(mouseDelta);
    }

    private void PanByMouseDelta(Vector2 mouseDelta)
    {
        float viewportHeight = Math.Max(1, GetScreenHeight());
        float halfFovTangent =
            MathF.Tan(FieldOfViewDegrees * MathF.PI / 360f);
        float sinPitch = MathF.Max(0.15f, MathF.Sin(_desiredPitchRadians));

        // Horizontal and vertical screen directions project differently onto
        // the ground when the camera is tilted.
        float horizontalUnitsPerPixel =
            2f * _desiredDistance * sinPitch *
            halfFovTangent / viewportHeight;
        float verticalUnitsPerPixel =
            2f * _desiredDistance * halfFovTangent /
            (viewportHeight * sinPitch);

        Vector3 right = new(
            MathF.Cos(_desiredYawRadians),
            0f,
            -MathF.Sin(_desiredYawRadians));

        Vector3 groundForward = new(
            -MathF.Sin(_desiredYawRadians),
            0f,
            -MathF.Cos(_desiredYawRadians));

        _desiredTarget -= right *
            (mouseDelta.X * horizontalUnitsPerPixel);
        _desiredTarget += groundForward *
            (mouseDelta.Y * verticalUnitsPerPixel);
    }

    private void UpdateZoomInput()
    {
        float wheel = GetMouseWheelMove();
        if (wheel == 0f)
            return;

        Vector2 cursor = GetMousePosition();
        bool hasAnchor = TryGetTargetPlanePoint(
            cursor,
            _desiredTarget,
            _desiredYawRadians,
            _desiredPitchRadians,
            _desiredDistance,
            out Vector3 pointBeforeZoom);

        _desiredDistance *= MathF.Pow(0.88f, wheel);
        _desiredDistance = Math.Clamp(
            _desiredDistance,
            _minimumDistance,
            _maximumDistance);

        if (!hasAnchor ||
            !TryGetTargetPlanePoint(
                cursor,
                _desiredTarget,
                _desiredYawRadians,
                _desiredPitchRadians,
                _desiredDistance,
                out Vector3 pointAfterZoom))
        {
            return;
        }

        // Keep the point under the cursor stationary while zooming.
        _desiredTarget.X += pointBeforeZoom.X - pointAfterZoom.X;
        _desiredTarget.Z += pointBeforeZoom.Z - pointAfterZoom.Z;
    }

    private bool TryGetTargetPlanePoint(
        Vector2 screenPosition,
        Vector3 target,
        float yaw,
        float pitch,
        float distance,
        out Vector3 point)
    {
        point = default;

        int viewportWidth = Math.Max(1, GetScreenWidth());
        int viewportHeight = Math.Max(1, GetScreenHeight());

        Vector3 cameraPosition = target + CalculateCameraOffset(
            yaw,
            pitch,
            distance);

        Vector3 forward = Vector3.Normalize(target - cameraPosition);
        Vector3 right = Vector3.Normalize(
            Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Normalize(
            Vector3.Cross(right, forward));

        float aspect = viewportWidth / (float)viewportHeight;
        float halfFovTangent =
            MathF.Tan(FieldOfViewDegrees * MathF.PI / 360f);

        float normalizedX =
            (screenPosition.X / viewportWidth * 2f - 1f) *
            aspect * halfFovTangent;
        float normalizedY =
            (1f - screenPosition.Y / viewportHeight * 2f) *
            halfFovTangent;

        Vector3 rayDirection = Vector3.Normalize(
            forward +
            right * normalizedX +
            up * normalizedY);

        if (MathF.Abs(rayDirection.Y) < 0.0001f)
            return false;

        float distanceToPlane =
            (target.Y - cameraPosition.Y) / rayDirection.Y;

        if (distanceToPlane <= 0f)
            return false;

        point = cameraPosition + rayDirection * distanceToPlane;
        return true;
    }

    private void UpdateKeyboardInput(float deltaTime)
    {
        if (IsKeyPressed(KeyboardKey.T))
        {
            _desiredTarget = _topDownTarget;
            _desiredYawRadians = 0f;
            _desiredPitchRadians = TopDownPitch;
            _desiredDistance = CalculateFitDistance(
                GetScreenWidth(),
                GetScreenHeight());

            ResetSmoothing();
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

            // Move by a fraction of the visible map per second, not over half
            // of the world in one second at the initial zoom level.
            float speed = MathF.Max(12f, _desiredDistance * 0.20f);
            if (IsKeyDown(KeyboardKey.LeftShift) ||
                IsKeyDown(KeyboardKey.RightShift))
            {
                speed *= 2.2f;
            }

            _desiredTarget += direction * (speed * deltaTime);
        }

        float verticalInput = 0f;
        if (IsKeyDown(KeyboardKey.E)) verticalInput += 1f;
        if (IsKeyDown(KeyboardKey.Q)) verticalInput -= 1f;

        if (verticalInput != 0f)
        {
            float speed = MathF.Max(20f, _desiredDistance * 0.24f);
            _desiredTarget.Y = Math.Max(
                0f,
                _desiredTarget.Y + verticalInput * speed * deltaTime);
        }
    }

    private float CalculateFitDistance(int viewportWidth, int viewportHeight)
    {
        return CalculateFitDistance(
            _topDownTarget.X * 2f,
            _topDownTarget.Z * 2f,
            viewportWidth,
            viewportHeight);
    }

    private float CalculateFitDistance(
        float worldWidth,
        float worldDepth,
        int viewportWidth,
        int viewportHeight)
    {
        float aspect = Math.Max(1, viewportWidth) /
            (float)Math.Max(1, viewportHeight);
        float requiredVerticalSpan = MathF.Max(
            worldDepth,
            worldWidth / aspect);

        float halfFovTangent =
            MathF.Tan(FieldOfViewDegrees * MathF.PI / 360f);

        return Math.Clamp(
            requiredVerticalSpan * 0.5f / halfFovTangent * FitMargin,
            _minimumDistance,
            _maximumDistance);
    }

    private static Vector3 CalculateCameraOffset(
        float yaw,
        float pitch,
        float distance)
    {
        float horizontalDistance = distance * MathF.Cos(pitch);

        return new Vector3(
            MathF.Sin(yaw) * horizontalDistance,
            MathF.Sin(pitch) * distance,
            MathF.Cos(yaw) * horizontalDistance);
    }

    private void UpdateCamera()
    {
        Camera = new Camera3D(
            _target + CalculateCameraOffset(
                _yawRadians,
                _pitchRadians,
                _distance),
            _target,
            Vector3.UnitY,
            FieldOfViewDegrees,
            CameraProjection.Perspective);
    }

    private void ResetSmoothing()
    {
        _targetVelocity = Vector3.Zero;
        _yawVelocity = 0f;
        _pitchVelocity = 0f;
        _distanceVelocity = 0f;
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
