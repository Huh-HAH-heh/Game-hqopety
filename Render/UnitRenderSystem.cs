using Core.Unit;
using Core.Unit.Components;
using SFML.Graphics; // Для RenderWindow, CircleShape, Color
using SFML.System;   // Для Vector2f
using System;

namespace RimClone.Render
{
    public sealed class UnitRenderSystem
    {
        // Размер макро-тайла (16) и деление на 3 суб-ячейки
        private const int TileSize = 16;
        private const float MicroCellPixelSize = 16f / 3f; // 5.33333f пикселей

        /// <summary>
        /// Отрисовывает всех живых муравьев и существ по новым сглаженным микро-координатам.
        /// </summary>
        /// <summary>
        /// Отрисовывает всех живых существ и возвращает их общее количество для заголовка окна.
        /// </summary>
        public int Draw(RenderWindow window, UnitStore units, CircleShape antShape, int currentZ)
        {
            int aliveCount = 0; // Счетчик живых существ в мире

            for (int i = 0; i < units.Count; i++)
            {
                // Счетчик считает абсолютно всех живых муравьев в массиве UnitStore
                if (units.HealthMasks[i] == 0)
                    continue;

                aliveCount++;

                // А рисуем только тех, кто на текущем этаже камеры (Z)
                if (units.Positions[i].Z != currentZ)
                    continue;

                ref var pos = ref units.Positions[i];
                float bloodLoss = units.BloodLossLevels[i];

                float antScreenX = pos.RenderX * MicroCellPixelSize + (MicroCellPixelSize * 0.5f);
                float antScreenY = pos.RenderY * MicroCellPixelSize + (MicroCellPixelSize * 0.5f);

                Color unitColor = units.UnitType[i] switch
                {
                    UnitType.Human => new Color(100, 180, 240, 255),
                    UnitType.Animal => new Color(140, 220, 100, 255),
                    UnitType.Drone => new Color(240, 200, 80, 255),
                    UnitType.Vehicle => new Color(220, 130, 60, 255),
                    UnitType.Insect => new Color(230, 80, 80, 255),
                    _ => new Color(200, 200, 200, 255)
                };

                if (bloodLoss > 0.1f)
                {
                    float healthPercent = Math.Clamp(1.0f - bloodLoss, 0.1f, 1.0f);
                    unitColor.R = (byte)(unitColor.R * healthPercent);
                    unitColor.G = (byte)(unitColor.G * healthPercent);
                    unitColor.B = (byte)(unitColor.B * healthPercent);
                }

                antShape.FillColor = unitColor;
                antShape.Position = new Vector2f(antScreenX, antScreenY);
                window.Draw(antShape);
            }

            return aliveCount; // Возвращаем честную цифру
        }

    }
}
