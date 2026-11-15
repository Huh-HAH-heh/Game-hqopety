using SFML.Graphics;
using SFML.System;
using System;
using System.Numerics;

namespace RimClone.Render;

public sealed class GameCamera : IDisposable
{
    private const float InverseSqrtTwo = 0.70710678118f;

    private static readonly string[] OrbitSectorLabels =
    {
        "СЗ", "С", "СВ", "В", "ЮВ", "Ю", "ЮЗ", "З"
    };

    private readonly View _view;
    private readonly float _moveSpeed;

    private float _zoomLevel = 1.0f;
    private Vector2f _targetPosition;
    private Vector2f _viewportSize;
    private bool _isometricProjectionEnabled = true;
    private int _rotationStep;
    private float _yawDegrees;
    private float _pitchDegrees = 45f;

    public float ZoomLevel => _zoomLevel;
    public View View => _view;
    public bool IsometricProjectionEnabled => _isometricProjectionEnabled;
    public int RotationStep => _rotationStep;
    public float PitchDegrees => _pitchDegrees;
    public float YawDegrees => _yawDegrees;

    // Q/E and RMB drag track the camera's orbital sector around the current world pivot.
    public string OrbitSectorLabel => OrbitSectorLabels[_rotationStep];

    public GameCamera(
        Vector2f startPosition,
        Vector2f windowSize,
        float moveSpeed = 400f)
    {
        _view = new View(startPosition, windowSize);
        _targetPosition = startPosition;
        _viewportSize = windowSize;
        _moveSpeed = moveSpeed;
        ApplyProjection();
    }

    public void CenterOnPosition(Vector2f position)
    {
        _targetPosition = position;
        _view.Center = position;
    }

    public void CenterOnWorld(
        float tilePixelSize,
        Core.Map.WorldMap worldMap)
    {
        float worldWidth = worldMap.TileWidth * tilePixelSize;
        float worldHeight = worldMap.TileHeight * tilePixelSize;

        _targetPosition = new Vector2f(
            worldWidth * 0.5f,
            worldHeight * 0.5f);

        _view.Center = _targetPosition;
    }

    public void Resize(Vector2f windowSize)
    {
        if (windowSize.X <= 0f || windowSize.Y <= 0f)
            return;

        _viewportSize = windowSize;
        ApplyProjection();
    }

    public void SetIsometricProjection(bool enabled)
    {
        if (_isometricProjectionEnabled == enabled)
            return;

        _isometricProjectionEnabled = enabled;
        ApplyProjection();
    }

    public void RotateClockwise(Vector2f orbitCenter) =>
        RotateYaw(45f, orbitCenter);

    public void RotateCounterClockwise(Vector2f orbitCenter) =>
        RotateYaw(-45f, orbitCenter);

    public void RotateByMouse(Vector2f pixelDelta, Vector2f orbitCenter)
    {
        if (!_isometricProjectionEnabled)
            return;

        const float yawSensitivity = 0.32f;
        const float pitchSensitivity = 0.22f;

        if (pixelDelta.X != 0f)
            RotateYaw(pixelDelta.X * yawSensitivity, orbitCenter);

        if (pixelDelta.Y != 0f)
        {
            _pitchDegrees = Math.Clamp(
                _pitchDegrees - pixelDelta.Y * pitchSensitivity,
                20f,
                80f);
            ApplyProjection();
        }
    }

    private void RotateYaw(float deltaDegrees, Vector2f orbitCenter)
    {
        if (!_isometricProjectionEnabled || deltaDegrees == 0f)
            return;

        float radians = deltaDegrees * MathF.PI / 180f;
        float cosine = MathF.Cos(radians);
        float sine = MathF.Sin(radians);

        // Orbit the camera centre around the captured world-space pivot.
        float offsetX = _targetPosition.X - orbitCenter.X;
        float offsetY = _targetPosition.Y - orbitCenter.Y;
        _targetPosition = new Vector2f(
            orbitCenter.X + cosine * offsetX - sine * offsetY,
            orbitCenter.Y + sine * offsetX + cosine * offsetY);

        _view.Center = _targetPosition;

        _yawDegrees = (_yawDegrees + deltaDegrees) % 360f;
        if (_yawDegrees < 0f)
            _yawDegrees += 360f;

        _rotationStep =
            ((int)MathF.Round(_yawDegrees / 45f) % 8 + 8) % 8;
        ApplyRotation();
    }

    public void HandleZoom(float delta)
    {
        if (delta == 0f)
            return;

        const float zoomFactor = 1.1f;

        float newZoom = delta > 0f
            ? _zoomLevel / zoomFactor
            : _zoomLevel * zoomFactor;

        newZoom = Math.Max(float.Epsilon, newZoom);
        _view.Zoom(newZoom / _zoomLevel);
        _zoomLevel = newZoom;
    }

    public void PanByPixels(
        Vector2f pixelDelta,
        float viewportWidth,
        float viewportHeight)
    {
        if (viewportWidth <= 0f || viewportHeight <= 0f)
            return;

        float localX = pixelDelta.X * _view.Size.X / viewportWidth;
        float localY = pixelDelta.Y * _view.Size.Y / viewportHeight;
        Vector2f worldDelta = ScreenUnitsToWorld(localX, localY);

        _targetPosition.X -= worldDelta.X;
        _targetPosition.Y -= worldDelta.Y;
        _view.Center = _targetPosition;
    }

    public void Update(
        Vector2 moveDirection,
        float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        if (moveDirection.LengthSquared() > 0f)
        {
            float zoomSpeed = _moveSpeed * Math.Clamp(_zoomLevel, 0.05f, 24f);
            float localScaleX = _isometricProjectionEnabled ? InverseSqrtTwo : 1f;
            float localScaleY = _isometricProjectionEnabled
                ? 1f / MathF.Sin(_pitchDegrees * MathF.PI / 180f)
                : 1f;

            float localX = moveDirection.X * zoomSpeed * localScaleX * deltaTime;
            float localY = moveDirection.Y * zoomSpeed * localScaleY * deltaTime;
            Vector2f worldDelta = ScreenUnitsToWorld(localX, localY);

            _targetPosition.X += worldDelta.X;
            _targetPosition.Y += worldDelta.Y;
        }

        float lerp = Math.Min(15f * deltaTime, 1f);
        Vector2f center = _view.Center;
        center.X += (_targetPosition.X - center.X) * lerp;
        center.Y += (_targetPosition.Y - center.Y) * lerp;
        _view.Center = center;
    }

    public static void GetVisibleWorldHalfExtents(
        View view,
        out float halfWidth,
        out float halfHeight)
    {
        float halfViewWidth = MathF.Abs(view.Size.X) * 0.5f;
        float halfViewHeight = MathF.Abs(view.Size.Y) * 0.5f;
        float radians = view.Rotation.Radians;
        float cosine = MathF.Abs(MathF.Cos(radians));
        float sine = MathF.Abs(MathF.Sin(radians));

        halfWidth = cosine * halfViewWidth + sine * halfViewHeight;
        halfHeight = sine * halfViewWidth + cosine * halfViewHeight;
    }

    private Vector2f ScreenUnitsToWorld(float localX, float localY)
    {
        float radians = _view.Rotation.Radians;
        float cosine = MathF.Cos(radians);
        float sine = MathF.Sin(radians);

        return new Vector2f(
            cosine * localX - sine * localY,
            sine * localX + cosine * localY);
    }

    private void ApplyProjection()
    {
        if (_isometricProjectionEnabled)
        {
            float pitchRadians = _pitchDegrees * MathF.PI / 180f;
            float pitchScale = 1f / MathF.Sin(pitchRadians);
            _view.Size = new Vector2f(
                _viewportSize.X * _zoomLevel * InverseSqrtTwo,
                _viewportSize.Y * _zoomLevel * pitchScale);
            ApplyRotation();
            return;
        }

        _view.Size = new Vector2f(
            _viewportSize.X * _zoomLevel,
            _viewportSize.Y * _zoomLevel);
        _view.Rotation = Angle.Zero;
    }

    private void ApplyRotation()
    {
        if (_isometricProjectionEnabled)
            _view.Rotation = Angle.FromDegrees(-45f + _yawDegrees);
    }

    public void Dispose()
    {
        _view.Dispose();
    }
}
