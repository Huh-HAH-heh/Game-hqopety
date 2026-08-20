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
        // Внутри GameCamera.cs добавляем свойство:
        public float ZoomLevel => _zoomLevel;

        // Целевая позиция, к которой камера ПЛАВНО стремится (Interpolation Target)
        private Vector2f _targetPosition;

        public View View => _view;

        public GameCamera(Vector2f startPosition, Vector2f windowSize, float moveSpeed = 400f)
        {
            _view = new View(startPosition, windowSize);
            _targetPosition = startPosition;
            _moveSpeed = moveSpeed;
        }

        /// <summary>
        /// Центрирует камеру прямо на вертикальный проспект города, чтобы он не улетал за край экрана.
        /// </summary>
        public void CenterOnStreet(float microCellPixelSize)
        {
            // Центр нашей дороги находится на 50-й микро-ячейке. 
            // Считаем точную пиксельную координату центра улицы!
            float streetCenterX = 50f * microCellPixelSize;
            float streetCenterY = 60f * microCellPixelSize; // Встаем чуть ниже начала блокпоста

            _targetPosition = new Vector2f(streetCenterX, streetCenterY);
            _view.Center = _targetPosition;
        }

        public void HandleZoom(float delta)
        {
            if (delta > 0)
            {
                // Приближение
                _zoomLevel *= 0.9f;
                _view.Zoom(0.9f);
            }
            else if (delta < 0)
            {
                // Отдаление
                _zoomLevel *= 1.1f;
                _view.Zoom(1.1f);
            }

            // Зажимаем масштаб в разумных пределах, чтобы не сломать рендерер
            _zoomLevel = Math.Clamp(_zoomLevel, 0.2f, 4.0f);
        }

        /// <summary>
        /// Сверхплавное обновление позиции камеры с физической интерполяцией.
        /// </summary>
        public void UpdateInput(float deltaTime)
        {
            if (deltaTime <= 0f) return;

            Vector2f moveDirection = new Vector2f(0f, 0f);

            // Считываем клавиатуру (Поддержка WASD и стрелочек)
            if (Keyboard.IsKeyPressed(Keyboard.Key.W) || Keyboard.IsKeyPressed(Keyboard.Key.Up)) moveDirection.Y -= 1f;
            if (Keyboard.IsKeyPressed(Keyboard.Key.S) || Keyboard.IsKeyPressed(Keyboard.Key.Down)) moveDirection.Y += 1f;
            if (Keyboard.IsKeyPressed(Keyboard.Key.A) || Keyboard.IsKeyPressed(Keyboard.Key.Left)) moveDirection.X -= 1f;
            if (Keyboard.IsKeyPressed(Keyboard.Key.D) || Keyboard.IsKeyPressed(Keyboard.Key.Right)) moveDirection.X += 1f;

            // 1. Нормализуем вектор, чтобы по диагонали камера не летела в 1.4 раза быстрее
            float length = MathF.Sqrt(moveDirection.X * moveDirection.X + moveDirection.Y * moveDirection.Y);
            if (length > 0f)
            {
                moveDirection.X /= length;
                moveDirection.Y /= length;

                // Скорость движения плавно масштабируется под уровень приближения (Зума)
                float speedFactor = _moveSpeed * _zoomLevel;

                // Сдвигаем ЦЕЛЕВУЮ точку, куда камера хочет прилететь
                _targetPosition.X += moveDirection.X * speedFactor * deltaTime;
                _targetPosition.Y += moveDirection.Y * speedFactor * deltaTime;
            }

            // 2. СВЕРХПЛАВНЫЙ ФИЗИЧЕСКИЙ LERP (Сглаживание микро-дерганий кадра)
            // Вместо мгновенного прыжка, камера плавно "плывет" к целевой точке.
            // Коэффициент 15f задает жесткость вязкости камеры (чем выше число, тем быстрее отзывчивость)
            Vector2f currentCenter = _view.Center;

            float lerpFactor = 15f * deltaTime;
            if (lerpFactor > 1f) lerpFactor = 1f; // Защита от сверхбольшого deltaTime

            float newX = currentCenter.X + (_targetPosition.X - currentCenter.X) * lerpFactor;
            float newY = currentCenter.Y + (_targetPosition.Y - currentCenter.Y) * lerpFactor;

            _view.Center = new Vector2f(newX, newY);
        }
    }
}
