using SFML.Graphics;
using SFML.System;
using System;
using System.Numerics;

namespace RimClone.Render;

public sealed class GameCamera
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

        newZoom =
            Math.Max(
                0.05f,
                newZoom);

        float viewFactor =
            newZoom /
            _zoomLevel;

        _view.Zoom(
            viewFactor);

        _zoomLevel =
            newZoom;
    }

    public void Update(
        Vector2 moveDirection,
        float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        if (moveDirection.LengthSquared() > 0f)
        {
            _targetPosition.X +=
                moveDirection.X *
                _moveSpeed *
                deltaTime;

            _targetPosition.Y +=
                moveDirection.Y *
                _moveSpeed *
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
}
