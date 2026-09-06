using System;
using SFML.Graphics;
using SFML.System;
using SFML.Window;

namespace RimClone.Render
{
    public sealed class GameCamera
    {
        private readonly View _view;
        private readonly float _moveSpeed;

        private float _zoomLevel = 1.0f;

        public float ZoomLevel => _zoomLevel;

        private Vector2f _targetPosition;

        public View View => _view;

        public GameCamera(
            Vector2f startPosition,
            Vector2f windowSize,
            float moveSpeed = 400f)
        {
            _view = new View(
                startPosition,
                windowSize);

            _targetPosition = startPosition;
            _moveSpeed = moveSpeed;
        }

        public void CenterOnStreet(
            float microCellPixelSize)
        {
            float streetCenterX =
                50f * microCellPixelSize;

            float streetCenterY =
                60f * microCellPixelSize;

            _targetPosition =
                new Vector2f(
                    streetCenterX,
                    streetCenterY);

            _view.Center =
                _targetPosition;
        }

        public void HandleZoom(float delta)
        {
            if (delta == 0f)
                return;

            const float zoomFactor = 1.1f;

            float newZoom =
                delta > 0f
                    ? _zoomLevel / zoomFactor
                    : _zoomLevel * zoomFactor;

            // Только защита от экстремальных значений.
            newZoom = Math.Max(0.05f, newZoom);

            float viewFactor =
                newZoom / _zoomLevel;

            _view.Zoom(viewFactor);

            _zoomLevel = newZoom;
        }

        public void UpdateInput(
            float deltaTime)
        {
            if (deltaTime <= 0f)
                return;

            Vector2f moveDirection =
                new Vector2f(0f, 0f);

            if (Keyboard.IsKeyPressed(
                    Keyboard.Key.W) ||
                Keyboard.IsKeyPressed(
                    Keyboard.Key.Up))
            {
                moveDirection.Y -= 1f;
            }

            if (Keyboard.IsKeyPressed(
                    Keyboard.Key.S) ||
                Keyboard.IsKeyPressed(
                    Keyboard.Key.Down))
            {
                moveDirection.Y += 1f;
            }

            if (Keyboard.IsKeyPressed(
                    Keyboard.Key.A) ||
                Keyboard.IsKeyPressed(
                    Keyboard.Key.Left))
            {
                moveDirection.X -= 1f;
            }

            if (Keyboard.IsKeyPressed(
                    Keyboard.Key.D) ||
                Keyboard.IsKeyPressed(
                    Keyboard.Key.Right))
            {
                moveDirection.X += 1f;
            }

            float length =
                MathF.Sqrt(
                    moveDirection.X *
                        moveDirection.X +
                    moveDirection.Y *
                        moveDirection.Y);

            if (length > 0f)
            {
                moveDirection.X /= length;
                moveDirection.Y /= length;

                // Скорость теперь НЕ зависит от zoom.
                // Поэтому при приближении камера не становится
                // физически медленнее.
                float speedFactor =
                    _moveSpeed;

                _targetPosition.X +=
                    moveDirection.X *
                    speedFactor *
                    deltaTime;

                _targetPosition.Y +=
                    moveDirection.Y *
                    speedFactor *
                    deltaTime;
            }

            Vector2f currentCenter =
                _view.Center;

            // Сохраняем плавность камеры.
            float lerpFactor =
                15f * deltaTime;

            if (lerpFactor > 1f)
                lerpFactor = 1f;

            float newX =
                currentCenter.X +
                (_targetPosition.X -
                 currentCenter.X) *
                lerpFactor;

            float newY =
                currentCenter.Y +
                (_targetPosition.Y -
                 currentCenter.Y) *
                lerpFactor;

            _view.Center =
                new Vector2f(
                    newX,
                    newY);
        }
    }
}