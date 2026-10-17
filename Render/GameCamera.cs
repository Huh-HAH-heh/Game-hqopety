using SFML.Graphics;
using SFML.System;
using System;
using System.Numerics;

namespace RimClone.Render;

public sealed class GameCamera : IDisposable
{
    private readonly View _view;
    private readonly float _moveSpeed;

    private float _zoomLevel = 1.0f;

    private Vector2f _targetPosition;

    public float ZoomLevel =>
        _zoomLevel;

    public View View =>
        _view;

    public GameCamera(
        Vector2f startPosition,
        Vector2f windowSize,
        float moveSpeed = 400f)
    {
        _view =
            new View(
                startPosition,
                windowSize);

        _targetPosition =
            startPosition;

        _moveSpeed =
            moveSpeed;
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
        float worldWidth =
            worldMap.TileWidth *
            tilePixelSize;

        float worldHeight =
            worldMap.TileHeight *
            tilePixelSize;

        _targetPosition =
            new Vector2f(
                worldWidth * 0.5f,
                worldHeight * 0.5f);

        _view.Center =
            _targetPosition;
    }

    public void Resize(Vector2f windowSize)
    {
        if (windowSize.X <= 0f || windowSize.Y <= 0f)
            return;

        _view.Size = new Vector2f(
            windowSize.X * _zoomLevel,
            windowSize.Y * _zoomLevel);
    }

    public void HandleZoom(
        float delta)
    {
        if (delta == 0f)
            return;

        const float zoomFactor = 1.1f;

        float newZoom =
            delta > 0f
                ? _zoomLevel / zoomFactor
                : _zoomLevel * zoomFactor;

        // No gameplay zoom-in limit. Keep only the smallest positive float
        // to prevent the view size from reaching zero after extreme wheel input.
        newZoom =
            Math.Max(
                float.Epsilon,
                newZoom);

        float viewFactor =
            newZoom /
            _zoomLevel;

        _view.Zoom(
            viewFactor);

        _zoomLevel =
            newZoom;
    }

    public void PanByPixels(
        Vector2f pixelDelta,
        float viewportWidth,
        float viewportHeight)
    {
        if (viewportWidth <= 0f || viewportHeight <= 0f)
            return;

        _targetPosition.X -= pixelDelta.X * _view.Size.X / viewportWidth;
        _targetPosition.Y -= pixelDelta.Y * _view.Size.Y / viewportHeight;
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
            // ZoomLevel is the world-space distance represented by one screen-space unit.
            // Scale panning speed with it so the camera covers comparable screen distance
            // per second both close to the map and when zoomed far out.
            float zoomSpeedMultiplier = Math.Clamp(_zoomLevel, 0.05f, 24f);
            float zoomAdjustedSpeed = _moveSpeed * zoomSpeedMultiplier;

            _targetPosition.X +=
                moveDirection.X *
                zoomAdjustedSpeed *
                deltaTime;

            _targetPosition.Y +=
                moveDirection.Y *
                zoomAdjustedSpeed *
                deltaTime;
        }

        float lerp =
            Math.Min(
                15f * deltaTime,
                1f);

        Vector2f center =
            _view.Center;

        center.X +=
            (_targetPosition.X -
             center.X) *
            lerp;

        center.Y +=
            (_targetPosition.Y -
             center.Y) *
            lerp;

        _view.Center =
            center;
    }

    public void Dispose()
    {
        _view.Dispose();
    }
}
