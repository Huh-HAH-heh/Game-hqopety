using Core.Structs;
using Render.RenderNeed;
using SFML.Graphics;
using SFML.System;
using System.Collections.Generic;

namespace Core.Unit.Systems
{
    public sealed class CombatEffectSystem
    {
        // Пул активных трассеров
        private readonly List<CombatTracer> _tracers = new List<CombatTracer>(32);

        /// <summary>
        /// Добавить новый трассер в мир. Вызывается из UnitCombatSystem / UnitDamageSystem в момент удара.
        /// </summary>
        public void AddTracer(Vector2f startPixels, Vector2f endPixels, int zLevel, bool showCross, float duration = 0.4f)
        {
            _tracers.Add(new CombatTracer
            {
                StartPixels = startPixels,
                EndPixels = endPixels,
                ZLevel = zLevel,
                TimeLeft = duration,
                MaxTime = duration,
                ShowCross = showCross
            });
        }

        /// <summary>
        /// Обновляет время жизни эффектов. Удаляет старые.
        /// </summary>
        public void Update(float deltaTime)
        {
            for (int i = _tracers.Count - 1; i >= 0; i--)
            {
                var tracer = _tracers[i];
                tracer.TimeLeft -= deltaTime;

                if (tracer.TimeLeft <= 0f)
                {
                    _tracers.RemoveAt(i); // Быстро удаляем из пула
                }
                else
                {
                    _tracers[i] = tracer; // Перезаписываем обновленную структуру
                }
            }
        }

        /// <summary>
        /// Отрисовка трассеров и крестиков в окно SFML.
        /// </summary>
        public void Draw(RenderWindow window, int currentZ)
        {
            foreach (var tracer in _tracers)
            {
                // Рисуем эффекты только на том этаже, где сейчас смотрит камера игрока
                if (tracer.ZLevel != currentZ) continue;

                // Вычисляем процент увядания линии для плавного исчезновения (Alpha от 255 до 0)
                float alphaPercent = tracer.TimeLeft / tracer.MaxTime;
                byte alpha = (byte)(180 * alphaPercent); // 180 — начальная полупрозрачность белого

                Color effectColor = new Color(255, 255, 255, alpha);

                // 1. РИСУЕМ ПОЛУПРОЗРАЧНУЮ ЛИНЮ (через массив вершин SFML)
                Vertex[] line = new Vertex[]
                {
                    new Vertex(tracer.StartPixels, effectColor),
                    new Vertex(tracer.EndPixels, effectColor)
                };
                window.Draw(line, PrimitiveType.Lines);

                // 2. РИСУЕМ КРЕСТИК В КОНЦЕ ЛИНИИ (Если был нанесен урон)
                if (tracer.ShowCross)
                {
                    const float size = 3f; // Размер усиков крестика в пикселях
                    Vector2f end = tracer.EndPixels;

                    // Первая палочка крестика 
                    Vertex[] cross1 = new Vertex[]
                    {
                        new Vertex(new Vector2f(end.X - size, end.Y - size), effectColor),
                        new Vertex(new Vector2f(end.X + size, end.Y + size), effectColor)
                    };
                    // Вторая палочка крестика
                    Vertex[] cross2 = new Vertex[]
                    {
                        new Vertex(new Vector2f(end.X - size, end.Y + size), effectColor),
                        new Vertex(new Vector2f(end.X + size, end.Y - size), effectColor)
                    };

                    window.Draw(cross1, PrimitiveType.Lines);
                    window.Draw(cross2, PrimitiveType.Lines);
                }
            }
        }
    }
}
