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
    private const float FieldOfViewDegrees = 45f;
    private const float BaseRotationSensitivity = 0.0018f;
    private const float RotationSmoothTime = 0.055f;
    private const float MovementSmoothTime = 0.075f;
    private const float ZoomSmoothTime = 0.095f;
    private const float MinimumPitch = 0.08f;
    private const float RtsMinimumPitch = 0.42f;
    private const float RtsMaximumPitch = 1.48f;
    private const float TopDownPitch = 1.565f;
    private const float MaximumPitch = TopDownPitch;
    private const float VerticalTargetLimitMeters = 500f;
    private const float FitMargin = 1.08f;

    private readonly float _minimumDistance;
    private readonly float _maximumDistance;
    private readonly Vector3 _topDownTarget;
    private int _viewportWidth;
    private int _viewportHeight;
    private bool _fitViewActive = true;

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
    private RaylibCameraMode _mode = RaylibCameraMode.Rts;
    private float _interactiveSpeedMultiplier = 1f;

    private enum MouseMode : byte
    {
        None,
        Orbit,
        Pan,
        SpeedAdjust
    }

    public Camera3D Camera { get; private set; }
    public float TargetHeightMeters => _target.Y;
    public float RotationSensitivityMultiplier { get; set; } = 1f;
    public float MovementSpeedMultiplier { get; set; } = 1f;
    public float PanSpeedMultiplier { get; set; } = 1f;
    public float ZoomSpeedMultiplier { get; set; } = 1f;
    public float VerticalSpeedMultiplier { get; set; } = 1f;
    public float CurrentMovementSpeedMetersPerSecond =>
        Math.Clamp(_desiredDistance * 0.14f, 1.5f, 240f) *
        MovementSpeedMultiplier *
        _interactiveSpeedMultiplier;
    public RaylibCameraMode Mode => _mode;

    public OrbitCameraController(TerrainRenderSnapshot terrain)
    {
        float largestWorldAxis = MathF.Max(
            terrain.WorldWidth,
            terrain.WorldDepth);

        _topDownTarget = new Vector3(
            terrain.WorldWidth * 0.5f,
            MathF.Min(
                terrain.VerticalWorldExtentMeters,
                MathF.Max(1f, terrain.MaximumHeightMeters * 0.12f)),
            terrain.WorldDepth * 0.5f);

        _target = _topDownTarget;
        _desiredTarget = _target;

        _yawRadians = 0f;
        _desiredYawRadians = _yawRadians;

        _pitchRadians = TopDownPitch;
        _desiredPitchRadians = _pitchRadians;

        _minimumDistance = 2f;
        _maximumDistance = largestWorldAxis * 3.5f;
        _viewportWidth = Math.Max(1, GetScreenWidth());
        _viewportHeight = Math.Max(1, GetScreenHeight());

        _distance = CalculateFitDistance(
            terrain.WorldWidth,
            terrain.WorldDepth,
            GetScreenWidth(),
            GetScreenHeight());
        _desiredDistance = _distance;

        UpdateCamera();
    }

    public void Update(
        float deltaTime,
        float mouseWheel,
        bool mouseInputEnabled = true)
    {
        UpdateViewportCalibration();
        if (deltaTime <= 0f)
            return;

        // Avoid a large camera jump after a debugger pause or window drag.
        deltaTime = Math.Clamp(deltaTime, 0f, 0.05f);

        if (mouseInputEnabled)
        {
            UpdateMouseMode();
            UpdateMouseInput();
            UpdateZoomInput(mouseWheel);
        }
        else
        {
            ReleaseMouse();
        }

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

    private void UpdateViewportCalibration()
    {
        int width = Math.Max(1, GetScreenWidth());
        int height = Math.Max(1, GetScreenHeight());
        if (width == _viewportWidth && height == _viewportHeight)
            return;

        _viewportWidth = width;
        _viewportHeight = height;

        if (_fitViewActive)
        {
            _desiredDistance = CalculateFitDistance(
                _topDownTarget.X * 2f,
                _topDownTarget.Z * 2f,
                width,
                height);
            _distance = _desiredDistance;
            _distanceVelocity = 0f;
        }

        UpdateCamera();
    }

    private void UpdateMouseMode()
    {
        if (_mouseMode == MouseMode.None)
        {
            if (_mode != RaylibCameraMode.TopDown &&
                IsMouseButtonPressed(MouseButton.Right))
            {
                _mouseMode = MouseMode.Orbit;
                _ignoreFirstMouseDelta = true;
                DisableCursor();
            }
            else if (IsMouseButtonPressed(MouseButton.Middle))
            {
                _mouseMode = IsShiftDown()
                    ? MouseMode.SpeedAdjust
                    : MouseMode.Pan;
                _ignoreFirstMouseDelta = true;
                DisableCursor();
            }

            return;
        }

        bool released =
            (_mouseMode == MouseMode.Orbit &&
             IsMouseButtonReleased(MouseButton.Right)) ||
            ((_mouseMode == MouseMode.Pan ||
              _mouseMode == MouseMode.SpeedAdjust) &&
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

        if (_mouseMode == MouseMode.SpeedAdjust)
        {
            // Vertical drag changes camera speed exponentially for fine control
            // at low values and a wide useful range at high values.
            _interactiveSpeedMultiplier = Math.Clamp(
                _interactiveSpeedMultiplier * MathF.Exp(-mouseDelta.Y * 0.006f),
                0.1f,
                10f);
            return;
        }

        if (_mouseMode == MouseMode.Orbit)
        {
            _fitViewActive = false;
            // Dragging right moves the camera around the focus to the right.
            _desiredYawRadians += mouseDelta.X * BaseRotationSensitivity * RotationSensitivityMultiplier;
            _desiredPitchRadians = Math.Clamp(
                _desiredPitchRadians - mouseDelta.Y * BaseRotationSensitivity * RotationSensitivityMultiplier,
                _mode == RaylibCameraMode.Rts ? RtsMinimumPitch : MinimumPitch,
                _mode == RaylibCameraMode.Rts ? RtsMaximumPitch : MaximumPitch);
            NormalizeYawRange();

            return;
        }

        if (_mouseMode == MouseMode.Pan)
            PanByMouseDelta(mouseDelta);
    }

    private void PanByMouseDelta(Vector2 mouseDelta)
    {
        mouseDelta *= PanSpeedMultiplier * _interactiveSpeedMultiplier;
        Vector2 center = new(
            GetScreenWidth() * 0.5f,
            GetScreenHeight() * 0.5f);

        // Treat MMB as grabbing the ground under the cursor. Intersect two
        // screen rays with the focus plane instead of estimating movement in
        // world units; this remains stable at every supported camera pitch.
        if (!TryGetTargetPlanePoint(
                center - mouseDelta,
                _desiredTarget,
                _desiredYawRadians,
                _desiredPitchRadians,
                _desiredDistance,
                out Vector3 pointBefore) ||
            !TryGetTargetPlanePoint(
                center,
                _desiredTarget,
                _desiredYawRadians,
                _desiredPitchRadians,
                _desiredDistance,
                out Vector3 pointAfter))
        {
            return;
        }

        _fitViewActive = false;
        _desiredTarget.X += pointBefore.X - pointAfter.X;
        _desiredTarget.Z += pointBefore.Z - pointAfter.Z;
    }

    private void UpdateZoomInput(float wheel)
    {
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

        _fitViewActive = false;
        _desiredDistance *= MathF.Pow(0.88f, wheel * ZoomSpeedMultiplier);
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
        Vector3 right = new(
            MathF.Cos(yaw),
            0f,
            -MathF.Sin(yaw));
        Vector3 up = CalculateCameraUp(yaw, pitch);

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

        // The far upper edge of a shallow perspective view can intersect the
        // focus plane far beyond the visible scene. Refuse that anchor rather
        // than causing a sudden multi-hundred-metre pan during zoom.
        if (distanceToPlane <= 0f ||
            distanceToPlane > distance * 8f)
        {
            return false;
        }

        point = cameraPosition + rayDirection * distanceToPlane;
        return true;
    }

    private static bool IsShiftDown() =>
        IsKeyDown(KeyboardKey.LeftShift) ||
        IsKeyDown(KeyboardKey.RightShift);

    private void UpdateKeyboardInput(float deltaTime)
    {
        if (IsKeyPressed(KeyboardKey.T))
            ResetTopDownView();

        float moveX = 0f;
        float moveZ = 0f;

        if (IsKeyDown(KeyboardKey.D)) moveX += 1f;
        if (IsKeyDown(KeyboardKey.A)) moveX -= 1f;
        if (IsKeyDown(KeyboardKey.W)) moveZ += 1f;
        if (IsKeyDown(KeyboardKey.S)) moveZ -= 1f;

        if (moveX != 0f || moveZ != 0f)
        {
            _fitViewActive = false;
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
            // Movement follows the current view scale: close inspection is
            // deliberately slow, while overview travel covers large distances.
            float speed = CurrentMovementSpeedMetersPerSecond;
            if (IsKeyDown(KeyboardKey.LeftShift) ||
                IsKeyDown(KeyboardKey.RightShift))
            {
                speed *= 2.2f;
            }

            _desiredTarget += direction * (speed * deltaTime);
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
            CalculateCameraUp(_yawRadians, _pitchRadians),
            FieldOfViewDegrees,
            CameraProjection.Perspective);
    }

    private static Vector3 CalculateCameraUp(float yaw, float pitch)
    {
        return Vector3.Normalize(new Vector3(
            -MathF.Sin(yaw) * MathF.Sin(pitch),
            MathF.Cos(pitch),
            -MathF.Cos(yaw) * MathF.Sin(pitch)));
    }

    public void ApplySettings(RaylibViewerSettings settings)
    {
        RotationSensitivityMultiplier = settings.CameraRotationSpeed;
        MovementSpeedMultiplier = settings.CameraMoveSpeed;
        PanSpeedMultiplier = settings.CameraPanSpeed;
        ZoomSpeedMultiplier = settings.CameraZoomSpeed;
        VerticalSpeedMultiplier = settings.CameraVerticalSpeed;

        if (_mode != settings.CameraMode)
            SetMode(settings.CameraMode);
    }

    public void SetMode(RaylibCameraMode mode)
    {
        _mode = mode;

        if (_mode == RaylibCameraMode.TopDown)
        {
            _fitViewActive = true;
            _desiredYawRadians = 0f;
            _desiredPitchRadians = TopDownPitch;
            _yawRadians = 0f;
            _pitchRadians = TopDownPitch;
        }
        else
        {
            _fitViewActive = false;
            float minimumPitch = _mode == RaylibCameraMode.Rts
                ? RtsMinimumPitch
                : MinimumPitch;
            float maximumPitch = _mode == RaylibCameraMode.Rts
                ? RtsMaximumPitch
                : MaximumPitch;

            _desiredPitchRadians = Math.Clamp(
                _desiredPitchRadians,
                minimumPitch,
                maximumPitch);
            _pitchRadians = _desiredPitchRadians;
        }

        ResetSmoothing();
        UpdateCamera();
    }

    public void FocusOn(Vector3 target, float distance, float pitchRadians)
    {
        _fitViewActive = false;
        target.Y = Math.Clamp(
            target.Y,
            -VerticalTargetLimitMeters,
            VerticalTargetLimitMeters);
        _target = target;
        _desiredTarget = target;

        _yawRadians = 0f;
        _desiredYawRadians = 0f;

        float minimumPitch = _mode == RaylibCameraMode.Rts
            ? RtsMinimumPitch
            : MinimumPitch;
        float maximumPitch = _mode == RaylibCameraMode.Rts
            ? RtsMaximumPitch
            : MaximumPitch;
        _pitchRadians = _mode == RaylibCameraMode.TopDown
            ? TopDownPitch
            : Math.Clamp(pitchRadians, minimumPitch, maximumPitch);
        _desiredPitchRadians = _pitchRadians;

        _distance = Math.Clamp(
            distance,
            _minimumDistance,
            _maximumDistance);
        _desiredDistance = _distance;

        ResetSmoothing();
        UpdateCamera();
    }

    public void ResetTopDownView()
    {
        _fitViewActive = true;
        _target = _topDownTarget;
        _desiredTarget = _topDownTarget;

        _yawRadians = 0f;
        _desiredYawRadians = 0f;
        _pitchRadians = TopDownPitch;
        _desiredPitchRadians = TopDownPitch;

        _distance = CalculateFitDistance(
            GetScreenWidth(),
            GetScreenHeight());
        _desiredDistance = _distance;

        ResetSmoothing();
        UpdateCamera();
    }

    private void NormalizeYawRange()
    {
        const float fullTurn = MathF.Tau;

        if (MathF.Abs(_desiredYawRadians) < fullTurn * 8f)
            return;

        float offset = MathF.Round(_desiredYawRadians / fullTurn) * fullTurn;
        _desiredYawRadians -= offset;
        _yawRadians -= offset;
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
