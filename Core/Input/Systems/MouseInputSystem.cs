using Core.Structs;
using Core.Unit;
using Core.Unit.Components;
using Core.Unit.Components.AiComponents.Core.Unit.Components;
using Core.Unit.Systems;
using SFML.Graphics;
using SFML.System;
using SFML.Window;
using System;
using System.Collections.Generic;

namespace Core.Input.Systems
{
    public sealed class MouseInputSystem
    {
        private bool _isSelecting = false;
        private Vector2f _startDragPixels;
        private Vector2f _currentMousePixels;
        private bool _hasFiredRightClick = false;

        private float _lastClickTime = 0f;
        private readonly Clock _clickClock = new Clock();

        // Список ID выделенных юнитов
        public readonly List<int> SelectedUnitIds = new List<int>(16);

        /// <summary>
        /// ОБНОВЛЕННАЯ СИГНАТУРА: Полностью совпадает с вызовом из VectorRenderer.cs
        /// </summary>
        public void Update(RenderWindow window, UnitStore units, int currentViewZ, float microCellPixelSize, float deltaTime)
        {
            Vector2i mousePosWindow = Mouse.GetPosition(window);
            _currentMousePixels = window.MapPixelToCoords(mousePosWindow, window.GetView());

            // ============================================================
            // ОБРАБОТКА ЛЕВОЙ КНОПКИ МЫШИ (ВЫДЕЛЕНИЕ РАМКОЙ)
            // ============================================================
            if (Mouse.IsButtonPressed(Mouse.Button.Left))
            {
                if (!_isSelecting)
                {
                    _isSelecting = true;
                    _startDragPixels = _currentMousePixels;
                    SelectedUnitIds.Clear();
                }
            }
            else
            {
                if (_isSelecting)
                {
                    _isSelecting = false;
                    CalculateSelectionBox(units, microCellPixelSize);
                }
            }

            // ============================================================
            // ОБРАБОТКА ПРАВОЙ КНОПКИ МЫШИ (ПРИКАЗ НА МАРШ ОДИНАЧКАМ)
            // ============================================================
            if (Mouse.IsButtonPressed(Mouse.Button.Right))
            {
                if (!_hasFiredRightClick && SelectedUnitIds.Count > 0)
                {
                    _hasFiredRightClick = true;

                    int clickMx = (int)(_currentMousePixels.X / microCellPixelSize);
                    int clickMy = (int)(_currentMousePixels.Y / microCellPixelSize);
                    int maxCoord = (16 * 48) - 1;

                    if (clickMx >= 0 && clickMx <= maxCoord && clickMy >= 0 && clickMy <= maxCoord)
                    {
                        // Обслуживание одиночек: отдаем приказ напрямую в ИИ-стек каждой выделенной пешки
                        foreach (int unitId in SelectedUnitIds)
                        {
                            // Сбрасываем старые команды, если они застряли
                            while (units.AiStackPointers[unitId] >= 0)
                            {
                                units.CpuPopCommand(unitId);
                            }

                            // Пушим чистую команду MoveToTarget без сквадовых надстроек
                            units.CpuPushCommand(unitId, new AiCommand
                            {
                                OpCode = AiOpCode.MoveToTarget,
                                TargetX = clickMx,
                                TargetY = clickMy
                            });

                            // Взводим параметры для системы перемещения
                            units.Movement[unitId].TargetCell = new SpatialCoord(clickMx, clickMy, currentViewZ);
                            units.Movement[unitId].SourceCell = units.Positions[unitId].Spatial;
                            units.Movement[unitId].Progress = 0f;
                            units.Movement[unitId].State = MovementState.Moving;
                        }
                    }
                }
            }
            else
            {
                _hasFiredRightClick = false;
            }
        }

        /// <summary>
        /// МАТЕМАТИЧЕСКИЙ СЧЕТ РАМКИ ВЫДЕЛЕНИЯ ОДИНАЧЕК (Снесли сквад-буферы)
        /// </summary>
        private void CalculateSelectionBox(UnitStore units, float microCellPixelSize)
        {
            float xMin = Math.Min(_startDragPixels.X, _currentMousePixels.X);
            float xMax = Math.Max(_startDragPixels.X, _currentMousePixels.X);
            float yMin = Math.Min(_startDragPixels.Y, _currentMousePixels.Y);
            float yMax = Math.Max(_startDragPixels.Y, _currentMousePixels.Y);

            bool isSingleClick = (xMax - xMin < 5f && yMax - yMin < 5f);

            float currentTime = _clickClock.ElapsedTime.AsSeconds();
            bool isDoubleClick = isSingleClick && (currentTime - _lastClickTime < 0.25f);
            _lastClickTime = currentTime;

            for (int i = 0; i < units.Count; i++)
            {
                if (units.HealthMasks[i] == 0) continue; // Мертвых не выделяем

                // Рассчитываем пиксельные координаты центра пешки на экране
                float unitPx = units.Positions[i].RenderX * microCellPixelSize;
                float unitPy = units.Positions[i].RenderY * microCellPixelSize;

                if (isSingleClick)
                {
                    // Обычный одиночный клик — проверяем дистанцию до пешки
                    float dx = _currentMousePixels.X - unitPx;
                    float dy = _currentMousePixels.Y - unitPy;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);

                    if (dist <= 14f)
                    {
                        SelectedUnitIds.Add(i);
                        break; // Выделяем только одного при одиночном клике
                    }
                }
                else
                {
                    // Проверяем попадание пешки внутрь прямоугольника зажима рамки
                    if (unitPx >= xMin && unitPx <= xMax && unitPy >= yMin && unitPy <= yMax)
                    {
                        SelectedUnitIds.Add(i);
                    }
                }
            }
        }

        /// <summary>
        /// ОТРИСОВКА СИНЕЙ СЕЛЕКТ-РАМКИ ЗАЖИМА (Оригинальный метод без изменений)
        /// </summary>
        public void DrawSelectionBox(RenderWindow window)
        {
            if (!_isSelecting) return;

            float xMin = Math.Min(_startDragPixels.X, _currentMousePixels.X);
            float xMax = Math.Max(_startDragPixels.X, _currentMousePixels.X);
            float yMin = Math.Min(_startDragPixels.Y, _currentMousePixels.Y);
            float yMax = Math.Max(_startDragPixels.Y, _currentMousePixels.Y);

            RectangleShape box = new RectangleShape(new Vector2f(xMax - xMin, yMax - yMin));
            box.Position = new Vector2f(xMin, yMin);
            box.FillColor = new Color(0, 150, 255, 35);
            box.OutlineColor = new Color(0, 200, 255, 220);
            box.OutlineThickness = 1f;
            window.Draw(box);
        }
    }
}
