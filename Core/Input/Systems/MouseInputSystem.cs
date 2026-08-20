using Core.Structs;
using Core.Unit;
using Core.Unit.Components;
using Core.Unit.Components.AiComponents.Core.Unit.Components;
using SFML.Graphics;
using SFML.System;
using SFML.Window;
using System;
using System.Collections.Generic;
using Core.Unit.Systems;
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

        public readonly List<int> SelectedUnitIds = new List<int>(16);

        // СТАРАЯ СИГНАТУРА НА СВОЕМ МЕСТЕ! (VectorRenderer.cs соберется без изменений)
        public void Update(RenderWindow window, UnitStore units, SquadStore squads, View cameraView, float microCellPixelSize)
        {
            Vector2i mousePosWindow = Mouse.GetPosition(window);
            _currentMousePixels = window.MapPixelToCoords(mousePosWindow, cameraView);

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
                    CalculateSelectionBox(units, squads, microCellPixelSize);
                }
            }

            // ========================================================
            // ИСПРАВЛЕНО: БРОНИРОВАННЫЙ БЛОК ПКМ БЕЗ ОШИБОК OUT REF VAR
            // ========================================================
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
                        int firstUnitId = SelectedUnitIds[0];
                        SpatialCoord targetCoord = new SpatialCoord(clickMx, clickMy, units.Positions[firstUnitId].Spatial.Z);

                        HashSet<int> affectedSquads = new HashSet<int>();
                        foreach (int unitId in SelectedUnitIds)
                        {
                            int teamId = units.SquadIds[unitId];
                            if (teamId >= 0)
                            {
                                // Ищем индекс БГ в реестре без использования out ref
                                int teamIdx = squads.Registry.GetTeamIndex(teamId);
                                if (teamIdx != -1)
                                {
                                    // Извлекаем чистую ссылку на структуру из Span памяти
                                    ref Fireteam team = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(squads.Registry.ActiveTeams)[teamIdx];
                                    if (team.ParentSquadId != -1)
                                    {
                                        affectedSquads.Add(team.ParentSquadId);
                                    }
                                }
                            }
                        }

                        // Отдаем приказ через легаси-фасад. Сквад сам веером раскидает его по БГ!
                        foreach (int squadId in affectedSquads)
                        {
                            squads.SetWaypoint(squadId, targetCoord);
                        }

                        // Обслуживание одиночек (у которых нет боевой группы)
                        foreach (int unitId in SelectedUnitIds)
                        {
                            if (units.SquadIds[unitId] == -1)
                            {
                                while (units.AiStackPointers[unitId] >= 0) units.CpuPopCommand(unitId);
                                units.CpuPushCommand(unitId, new AiCommand { OpCode = AiOpCode.MoveToTarget, TargetX = clickMx, TargetY = clickMy });
                                units.Movement[unitId].TargetCell = targetCoord;
                                units.Movement[unitId].State = MovementState.Moving;
                            }
                        }
                    }
                }
            }
            else
            {
                _hasFiredRightClick = false;
            }
        } // Здесь закрывается метод Update


        private void CalculateSelectionBox(UnitStore units, SquadStore squads, float microCellPixelSize)
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
                if (units.HealthMasks[i] == 0 || units.UnitType[i] != UnitType.Human) continue;

                float unitPx = units.Positions[i].RenderX * microCellPixelSize;
                float unitPy = units.Positions[i].RenderY * microCellPixelSize;

                float dist = MathF.Sqrt((_currentMousePixels.X - unitPx) * (_currentMousePixels.X - unitPx) + (_currentMousePixels.Y - unitPy) * (_currentMousePixels.Y - unitPy));

                if (dist <= 14f)
                {
                    // ДАБЛ-КЛИК ВЫДЕЛЯЕТ ВСЁ ТАКТИЧЕСКОЕ ЗВЕНО
                    // Внутри MouseInputSystem.cs в методе CalculateSelectionBox (секция дабл-клика):
                    if (isDoubleClick && squads != null)
                    {
                        int myTeamId = units.SquadIds[i];
                        if (myTeamId != -1)
                        {
                            int teamIdx = squads.Registry.GetTeamIndex(myTeamId);
                            if (teamIdx != -1)
                            {
                                SelectedUnitIds.Clear();
                                // Читаем БГ по прямой ссылке из Span безout ref!
                                ref Fireteam team = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(squads.Registry.ActiveTeams)[teamIdx];

                                foreach (int memberUid in team.MemberUnitIds)
                                {
                                    if (units.HealthMasks[memberUid] > 0) SelectedUnitIds.Add(memberUid);
                                }
                                return;
                            }
                        }
                    }


                    if (isSingleClick) { SelectedUnitIds.Add(i); break; }
                }

                if (!isSingleClick && unitPx >= xMin && unitPx <= xMax && unitPy >= yMin && unitPy <= yMax) SelectedUnitIds.Add(i);
            }
        }

        public void DrawSelectionBox(RenderWindow window)
        {
            if (!_isSelecting) return;
            float xMin = Math.Min(_startDragPixels.X, _currentMousePixels.X);
            float xMax = Math.Max(_startDragPixels.X, _currentMousePixels.X);
            float yMin = Math.Min(_startDragPixels.Y, _currentMousePixels.Y);
            float yMax = Math.Max(_startDragPixels.Y, _currentMousePixels.Y);

            RectangleShape box = new RectangleShape(new Vector2f(xMax - xMin, yMax - yMin));
            box.Position = new Vector2f(xMin, yMin);
            box.FillColor = new Color(0, 150, 255, 35); box.OutlineColor = new Color(0, 200, 255, 220); box.OutlineThickness = 1f;
            window.Draw(box);
        }
    }
}
