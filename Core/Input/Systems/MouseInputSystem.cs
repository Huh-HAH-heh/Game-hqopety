using Core.AI;
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
        /// Главный цикл обновления ввода мыши.
        /// Фиксирует выделение рамкой (ЛКМ) и отдает приказы перемещения (ПКМ).
        /// </summary>
        public void Update(
            RenderWindow window,
            UnitStore units,
            int currentViewZ,
            float microCellPixelSize,
            float deltaTime,
            GroupMovementManager groupMovementManager) // Ссылка на оригинальный менеджер симуляции
        {
            Vector2i mousePosWindow = Mouse.GetPosition(window);
            _currentMousePixels = window.MapPixelToCoords(mousePosWindow, window.GetView());

            // =========================================================================
            // // ОБРАБОТКА ЛЕВОЙ КНОПКИ МЫШИ (ВЫДЕЛЕНИЕ РАМКОЙ)
            // =========================================================================
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

            // =========================================================================
            // // ОБРАБОТКА ПРАВОЙ КНОПКИ МЫШИ (ПРИКАЗ НА ПЕРЕМЕЩЕНИЕ)
            // =========================================================================
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
                        foreach (int unitId in SelectedUnitIds)
                        {
                            // Сбрасываем старые ИИ команды из стека юнита
                            while (units.AiStackPointers[unitId] >= 0)
                            {
                                units.CpuPopCommand(unitId);
                            }

                            // Пушим базовую команду движения в стек ИИ
                            units.CpuPushCommand(unitId, new AiCommand
                            {
                                OpCode = AiOpCode.MoveToTarget,
                                TargetX = clickMx,
                                TargetY = clickMy
                            });

                            // ВАЖНО ДЛЯ ТЕСТА: Пишем координаты клика в оригинальный менеджер групп!
                            // Буква 'W' используется большая, как объявлено в вашем менеджере.
                            groupMovementManager._unitSubWaypointsBuffer[unitId, 0] = new PacketWaypoint
                            {
                                X = (short)clickMx,
                                Y = (short)clickMy
                            };
                            groupMovementManager._unitSubWaypointsCount[unitId] = 1;

                            // Переводим стейт перемещения в Idle и обнуляем прогресс,
                            // чтобы UnitMovementSystem на следующем кадре сама рассчитала первый шаг.
                            units.Movement[unitId].State = MovementState.Idle;
                            units.Movement[unitId].Progress = 0f;
                            units.MovementCooldowns[unitId] = 0f;
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
        /// Расчет попадания юнитов в рамку выделения мыши.
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

                // Переводим render-координаты юнита на сетке в экранные пиксели
                float unitPx = units.Positions[i].RenderX * microCellPixelSize;
                float unitPy = units.Positions[i].RenderY * microCellPixelSize;

                if (isSingleClick)
                {
                    // Клик в точку — проверяем радиус вокруг пешки
                    float dx = _currentMousePixels.X - unitPx;
                    float dy = _currentMousePixels.Y - unitPy;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);

                    if (dist <= 14f)
                    {
                        SelectedUnitIds.Add(i);
                        break; // Выделяем только одного при одиночном клике
                    }
                }
                else
                {
                    // Зажим рамки — проверяем попадание в прямоугольник
                    if (unitPx >= xMin && unitPx <= xMax && unitPy >= yMin && unitPy <= yMax)
                    {
                        SelectedUnitIds.Add(i);
                    }
                }
            }
        }

        /// <summary>
        /// Отрисовка полупрозрачной синей рамки выделения на экране.
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
