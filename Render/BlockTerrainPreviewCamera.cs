using System;
using Core.Map;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

/// <summary>
/// Owns the projected view used by the discrete block-terrain inspector.
/// Its azimuth and elevation are independent from the gameplay camera.
/// </summary>
public sealed class BlockTerrainPreviewCamera : IDisposable
{
    private const float MinimumElevationDegrees = 15f;
    private const float MaximumElevationDegrees = 90f;
    private const float ElevationSmoothing = 7.5f;
    private const float ZoomFactor = 1.1f;

    private readonly View _view;
    private readonly float _moveSpeed;
    private Vector2f _targetPosition;
    private Vector2f _viewportSize;
    private float _zoomLevel = 1f;
    private float _panSensitivity = 1f;
    private float _moveSensitivity = 1f;
    private float _azimuthDegrees;
    private float _renderAzimuthDegrees;
    private float _interactiveRotationDegrees;
    private bool _isInteractiveRotation;
    private float _elevationDegrees = 30f;
    private float _targetElevationDegrees = 30f;
    private long _cachedTerrainVersion = long.MinValue;
    private float _cachedHighestHeightMeters;

    public View View => _view;
    public float ZoomLevel => _zoomLevel;
    public float AzimuthDegrees => _renderAzimuthDegrees;
    public float TargetAzimuthDegrees => _azimuthDegrees;
    public bool IsInteractiveRotation => _isInteractiveRotation;
    public float ElevationDegrees => _elevationDegrees;
    public float TargetElevationDegrees => _targetElevationDegrees;

    public BlockTerrainPreviewCamera(Vector2f startPosition, Vector2f viewportSize, float moveSpeed = 450f)
    {
        _view = new View(startPosition, viewportSize);
        _targetPosition = startPosition;
        _viewportSize = viewportSize;
        _moveSpeed = moveSpeed;
    }

    public void Resize(Vector2f viewportSize)
    {
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f)
            return;

        _viewportSize = viewportSize;
        _view.Size = new Vector2f(
            _viewportSize.X * _zoomLevel,
            _viewportSize.Y * _zoomLevel);
    }

    public void CenterOnWorld(WorldMap worldMap, float tilePixelSize) =>
        CenterOnPosition(GetProjectionFocus(worldMap, tilePixelSize, _elevationDegrees));

    public void CenterOnPosition(Vector2f position)
    {
        _targetPosition = position;
        _view.Center = position;
    }

    public void SetPanSensitivity(float sensitivity) =>
        _panSensitivity = ClampSensitivity(sensitivity);

    public void SetMoveSensitivity(float sensitivity) =>
        _moveSensitivity = ClampSensitivity(sensitivity);

    public void HandleZoom(float delta)
    {
        if (!float.IsFinite(delta) || MathF.Abs(delta) < 0.0001f)
            return;

        float newZoom = delta > 0f
            ? _zoomLevel / ZoomFactor
            : _zoomLevel * ZoomFactor;
        newZoom = Math.Max(float.Epsilon, newZoom);
        _view.Zoom(newZoom / _zoomLevel);
        _zoomLevel = newZoom;
    }

    public void PanByPixels(Vector2f pixelDelta, float viewportWidth, float viewportHeight)
    {
        if (viewportWidth <= 0f || viewportHeight <= 0f)
            return;

        float localX = pixelDelta.X * _view.Size.X / viewportWidth * _panSensitivity;
        float localY = pixelDelta.Y * _view.Size.Y / viewportHeight * _panSensitivity;
        _targetPosition = new Vector2f(
            _view.Center.X - localX,
            _view.Center.Y - localY);
        _view.Center = _targetPosition;
    }

    public void Update(
        System.Numerics.Vector2 moveDirection,
        float deltaTime,
        WorldMap worldMap,
        float tilePixelSize)
    {
        if (!float.IsFinite(deltaTime) || deltaTime <= 0f)
            return;

        Vector2f oldFocus = GetProjectionFocus(worldMap, tilePixelSize, _elevationDegrees);
        float elevationDelta = _targetElevationDegrees - _elevationDegrees;
        if (MathF.Abs(elevationDelta) > 0.001f)
        {
            float elevationLerp = 1f - MathF.Exp(-ElevationSmoothing * deltaTime);
            _elevationDegrees += elevationDelta * elevationLerp;
            if (MathF.Abs(_targetElevationDegrees - _elevationDegrees) < 0.02f)
                _elevationDegrees = _targetElevationDegrees;
        }

        Vector2f newFocus = GetProjectionFocus(worldMap, tilePixelSize, _elevationDegrees);
        Vector2f focusDelta = new(newFocus.X - oldFocus.X, newFocus.Y - oldFocus.Y);
        _targetPosition = new Vector2f(
            _targetPosition.X + focusDelta.X,
            _targetPosition.Y + focusDelta.Y);
        Vector2f center = new(
            _view.Center.X + focusDelta.X,
            _view.Center.Y + focusDelta.Y);

        if (moveDirection.LengthSquared() > 0f)
        {
            float speed = _moveSpeed * _moveSensitivity *
                Math.Clamp(_zoomLevel, 0.05f, 24f);
            _targetPosition = new Vector2f(
                _targetPosition.X + moveDirection.X * speed * deltaTime,
                _targetPosition.Y + moveDirection.Y * speed * deltaTime);
        }

        float centerLerp = Math.Min(15f * deltaTime, 1f);
        center.X += (_targetPosition.X - center.X) * centerLerp;
        center.Y += (_targetPosition.Y - center.Y) * centerLerp;
        _view.Center = center;
    }

    public void BeginInteractiveRotation()
    {
        if (_isInteractiveRotation)
            return;

        _isInteractiveRotation = true;
        _interactiveRotationDegrees = 0f;
        _view.Rotation = Angle.FromDegrees(0f);
    }

    public void RotateAzimuth(float deltaDegrees, WorldMap worldMap, float tilePixelSize)
    {
        if (!float.IsFinite(deltaDegrees) || MathF.Abs(deltaDegrees) < 0.0001f)
            return;

        _azimuthDegrees = WrapDegrees(_azimuthDegrees + deltaDegrees);
        if (_isInteractiveRotation)
        {
            _interactiveRotationDegrees += deltaDegrees;
            _view.Rotation = Angle.FromDegrees(
                GetInteractiveScreenRotation(_interactiveRotationDegrees));
            return;
        }

        _view.Center = RotateProjectedPosition(_view.Center, deltaDegrees, worldMap, tilePixelSize);
        _targetPosition = RotateProjectedPosition(_targetPosition, deltaDegrees, worldMap, tilePixelSize);
        _renderAzimuthDegrees = _azimuthDegrees;
        _view.Rotation = Angle.FromDegrees(0f);
    }

    public void EndInteractiveRotation(WorldMap worldMap, float tilePixelSize)
    {
        if (!_isInteractiveRotation)
            return;

        float totalDelta = _interactiveRotationDegrees;
        _isInteractiveRotation = false;
        if (MathF.Abs(totalDelta) > 0.0001f)
        {
            _view.Center = RotateProjectedPosition(_view.Center, totalDelta, worldMap, tilePixelSize);
            _targetPosition = RotateProjectedPosition(_targetPosition, totalDelta, worldMap, tilePixelSize);
        }

        _renderAzimuthDegrees = _azimuthDegrees;
        _interactiveRotationDegrees = 0f;
        _view.Rotation = Angle.FromDegrees(0f);
    }

    private float GetInteractiveScreenRotation(float deltaDegrees)
    {
        float elevationRadians = Math.Clamp(_elevationDegrees, 15f, 90f) *
            (MathF.PI / 180f);
        float verticalToHorizontal = MathF.Sin(elevationRadians);
        float radians = (deltaDegrees % 360f) * (MathF.PI / 180f);
        float cosine = MathF.Cos(radians);
        float sine = MathF.Sin(radians);

        float oldXAxis = MathF.Atan2(verticalToHorizontal, 1f);
        float newXAxis = MathF.Atan2(
            verticalToHorizontal * (cosine + sine), cosine - sine);
        float oldYAxis = MathF.Atan2(verticalToHorizontal, -1f);
        float newYAxis = MathF.Atan2(
            verticalToHorizontal * (cosine - sine), -sine - cosine);

        float xDelta = NormalizeRadians(newXAxis - oldXAxis);
        float yDelta = NormalizeRadians(newYAxis - oldYAxis);
        return (xDelta + yDelta) * 0.5f * (180f / MathF.PI);
    }

    private static float NormalizeRadians(float radians)
    {
        while (radians > MathF.PI)
            radians -= MathF.PI * 2f;
        while (radians < -MathF.PI)
            radians += MathF.PI * 2f;
        return radians;
    }

    private static float WrapDegrees(float degrees)
    {
        degrees %= 360f;
        return degrees < 0f ? degrees + 360f : degrees;
    }

    public bool AdjustElevation(float deltaDegrees)
    {
        if (!float.IsFinite(deltaDegrees) || MathF.Abs(deltaDegrees) < 0.0001f)
            return false;

        float next = Math.Clamp(
            _targetElevationDegrees + deltaDegrees,
            MinimumElevationDegrees,
            MaximumElevationDegrees);
        if (MathF.Abs(next - _targetElevationDegrees) < 0.0001f)
            return false;

        _targetElevationDegrees = next;
        return true;
    }

    private Vector2f RotateProjectedPosition(
        Vector2f position,
        float deltaAzimuthDegrees,
        WorldMap worldMap,
        float tilePixelSize)
    {
        float tileSize = MathF.Max(1f, tilePixelSize * 2f);
        float halfTileSize = tileSize * 0.5f;
        float elevationRadians = Math.Clamp(_elevationDegrees, 15f, 90f) *
            (MathF.PI / 180f);
        float groundDepthScale = halfTileSize * MathF.Sin(elevationRadians);
        if (groundDepthScale <= 0.001f)
            return position;

        Vector2f focus = GetProjectionFocus(worldMap, tilePixelSize, _elevationDegrees);
        float projectedX = (position.X - focus.X) / halfTileSize;
        float projectedY = (position.Y - focus.Y) / groundDepthScale;
        float mapX = (projectedX + projectedY) * 0.5f;
        float mapY = (projectedY - projectedX) * 0.5f;

        float radians = (deltaAzimuthDegrees % 360f) * (MathF.PI / 180f);
        float cosine = MathF.Cos(radians);
        float sine = MathF.Sin(radians);
        float rotatedX = cosine * mapX - sine * mapY;
        float rotatedY = sine * mapX + cosine * mapY;

        return new Vector2f(
            focus.X + (rotatedX - rotatedY) * halfTileSize,
            focus.Y + (rotatedX + rotatedY) * groundDepthScale);
    }

    private Vector2f GetProjectionFocus(WorldMap worldMap, float tilePixelSize, float elevationDegrees)
    {
        float tileSize = MathF.Max(1f, tilePixelSize * 2f);
        float elevationRadians = Math.Clamp(elevationDegrees, 15f, 90f) *
            (MathF.PI / 180f);
        float groundDepthScale = tileSize * 0.5f * MathF.Sin(elevationRadians);
        float heightScale = tileSize * 0.5f * MathF.Cos(elevationRadians) /
            MathF.Cos(MathF.PI / 6f);
        float highestHeightMeters = GetHighestHeightMeters(worldMap);
        float centerX = worldMap.TileWidth * 0.5f;
        float centerY = worldMap.TileHeight * 0.5f;

        return new Vector2f(
            (centerX - centerY) * tileSize * 0.5f,
            (centerX + centerY) * groundDepthScale -
                highestHeightMeters * heightScale * 0.42f);
    }

    private float GetHighestHeightMeters(WorldMap worldMap)
    {
        if (_cachedTerrainVersion != worldMap.TerrainVersion)
        {
            int highestLayer = worldMap.GetHighestOccupiedLayer();
            _cachedHighestHeightMeters = highestLayer < 0
                ? 0f
                : (highestLayer + 1f) * 0.1f;
            _cachedTerrainVersion = worldMap.TerrainVersion;
        }

        return _cachedHighestHeightMeters;
    }

    private static float ClampSensitivity(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0.1f, 4f) : 1f;

    public void Dispose() => _view.Dispose();
}
