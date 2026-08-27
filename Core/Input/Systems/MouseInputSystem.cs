using Core.AI;
using Core.Items;
using Core.Map;
using Core.Structs;
using Core.Unit;
using Core.Unit.Components;
using SFML.Graphics;
using SFML.System;
using SFML.Window;
using System;
using System.Collections.Generic;

namespace Core.Input.Systems
{
    public sealed class MouseInputSystem
    {
        private bool _isSelecting;
        private Vector2f _startDragPixels;
        private Vector2f _currentMousePixels;
        private bool _hasFiredRightClick;
        private float _lastClickTime;
        private readonly Clock _clickClock = new Clock();

        public readonly List<int> SelectedUnitIds = new List<int>(16);

        public void Update(
            RenderWindow window,
            UnitStore units,
            WorldMap map,
            EdificeStore edifices,
            int currentViewZ,
            float microCellPixelSize,
            float deltaTime,
            GroupMovementManager groupMovementManager)
        {
            Vector2i mousePosWindow =
                Mouse.GetPosition(window);

            _currentMousePixels =
                window.MapPixelToCoords(
                    mousePosWindow,
                    window.GetView()
                );

            if (Mouse.IsButtonPressed(Mouse.Button.Left))
            {
                if (!_isSelecting)
                {
                    _isSelecting = true;
                    _startDragPixels = _currentMousePixels;
                    SelectedUnitIds.Clear();
                }
            }
            else if (_isSelecting)
            {
                _isSelecting = false;

                CalculateSelectionBox(
                    units,
                    microCellPixelSize,
                    currentViewZ
                );
            }

            if (Mouse.IsButtonPressed(Mouse.Button.Right))
            {
                if (!_hasFiredRightClick &&
                    SelectedUnitIds.Count > 0)
                {
                    _hasFiredRightClick = true;

                    int clickMx =
                        (int)(_currentMousePixels.X /
                        microCellPixelSize);

                    int clickMy =
                        (int)(_currentMousePixels.Y /
                        microCellPixelSize);

                    SpatialCoord target =
                        new SpatialCoord(
                            clickMx,
                            clickMy,
                            currentViewZ
                        );

                    Console.WriteLine(
                        $"[MOUSE] RIGHT CLICK target=({target.X},{target.Y},{target.Z}) selected={SelectedUnitIds.Count}"
                    );

                    SendGroupMoveCommand(
                        units,
                        map,
                        edifices,
                        target,
                        groupMovementManager
                    );
                }
            }
            else
            {
                _hasFiredRightClick = false;
            }
        }

        private void SendGroupMoveCommand(
            UnitStore units,
            WorldMap map,
            EdificeStore edifices,
            SpatialCoord target,
            GroupMovementManager groupMovementManager)
        {
            if (SelectedUnitIds.Count == 0)
            {
                Console.WriteLine("[MOUSE] COMMAND CANCELLED: no selected units");
                return;
            }

            MapLayer layer =
                map.GetLayer(target.Z);

            if (layer == null)
            {
                Console.WriteLine(
                    $"[MOUSE] COMMAND CANCELLED: layer Z={target.Z} is null"
                );

                return;
            }

            List<int> processedGroups =
                new List<int>(8);

            for (int i = 0; i < SelectedUnitIds.Count; i++)
            {
                int unitId =
                    SelectedUnitIds[i];

                if (unitId < 0 ||
                    unitId >= units.Count)
                {
                    Console.WriteLine(
                        $"[MOUSE] SKIP unit={unitId}: invalid id"
                    );

                    continue;
                }

                if (units.HealthMasks[unitId] == 0)
                {
                    Console.WriteLine(
                        $"[MOUSE] SKIP unit={unitId}: dead"
                    );

                    continue;
                }

                SpatialCoord unitPosition =
                    units.Positions[unitId].Spatial;

                if (unitPosition.Z != target.Z)
                {
                    Console.WriteLine(
                        $"[MOUSE] SKIP unit={unitId}: different Z unitZ={unitPosition.Z} targetZ={target.Z}"
                    );

                    continue;
                }

                int groupId =
                    units.CurrentGroupId[unitId];

                if (groupId < 0)
                {
                    Console.WriteLine(
                        $"[MOUSE] SKIP unit={unitId}: no group"
                    );

                    continue;
                }

                if (processedGroups.Contains(groupId))
                    continue;

                processedGroups.Add(groupId);

                int groupUnitCount = 0;

                for (int s = 0; s < SelectedUnitIds.Count; s++)
                {
                    int selectedId =
                        SelectedUnitIds[s];

                    if (selectedId < 0 ||
                        selectedId >= units.Count)
                    {
                        continue;
                    }

                    if (units.HealthMasks[selectedId] == 0)
                        continue;

                    if (units.CurrentGroupId[selectedId] != groupId)
                        continue;

                    if (units.Positions[selectedId].Spatial.Z != target.Z)
                        continue;

                    groupUnitCount++;
                }

                if (groupUnitCount == 0)
                {
                    Console.WriteLine(
                        $"[MOUSE] GROUP {groupId}: no valid selected members"
                    );

                    continue;
                }

                int[] groupUnitIds =
                    new int[groupUnitCount];

                int actualCount = 0;
                int firstUnitId = -1;

                for (int s = 0; s < SelectedUnitIds.Count; s++)
                {
                    int selectedId =
                        SelectedUnitIds[s];

                    if (selectedId < 0 ||
                        selectedId >= units.Count)
                    {
                        continue;
                    }

                    if (units.HealthMasks[selectedId] == 0)
                        continue;

                    if (units.CurrentGroupId[selectedId] != groupId)
                        continue;

                    if (units.Positions[selectedId].Spatial.Z != target.Z)
                        continue;

                    groupUnitIds[actualCount++] =
                        selectedId;

                    if (firstUnitId == -1 ||
                        selectedId < firstUnitId)
                    {
                        firstUnitId = selectedId;
                    }
                }

                if (actualCount == 0 ||
                    firstUnitId < 0)
                {
                    Console.WriteLine(
                        $"[MOUSE] GROUP {groupId}: failed to build member list"
                    );

                    continue;
                }

                SpatialCoord groupStart =
                    units.Positions[firstUnitId].Spatial;

                Console.WriteLine(
                    $"[MOUSE] GROUP {groupId}: start=({groupStart.X},{groupStart.Y},{groupStart.Z}) target=({target.X},{target.Y},{target.Z}) units={actualCount}"
                );

                for (int u = 0; u < actualCount; u++)
                {
                    int memberId =
                        groupUnitIds[u];

                    SpatialCoord memberPosition =
                        units.Positions[memberId].Spatial;

                    Console.WriteLine(
                        $"[MOUSE] GROUP {groupId}: member[{u}] unit={memberId} pos=({memberPosition.X},{memberPosition.Y},{memberPosition.Z})"
                    );
                }

                bool routeCreated =
                    groupMovementManager.RequestAndStoreGroupRoute(
                        map,
                        edifices,
                        layer,
                        groupId,
                        groupStart,
                        target,
                        groupUnitIds,
                        actualCount
                    );

                Console.WriteLine(
                    $"[MOUSE] GROUP {groupId}: routeCreated={routeCreated}"
                );

                if (!routeCreated)
                    continue;

                for (int s = 0; s < actualCount; s++)
                {
                    int selectedUnitId =
                        groupUnitIds[s];

                    if (selectedUnitId < 0 ||
                        selectedUnitId >= units.Count)
                    {
                        continue;
                    }

                    units.Movement[selectedUnitId].State =
                        MovementState.Idle;

                    units.Movement[selectedUnitId].Progress =
                        0f;

                    units.MovementCooldowns[selectedUnitId] =
                        0f;

                    bool hasRoute =
                        groupMovementManager.HasRouteForUnit(
                            selectedUnitId
                        );

                    SpatialCoord firstWaypoint =
                        groupMovementManager.GetCurrentWaypoint(
                            selectedUnitId
                        );

                    Console.WriteLine(
                        $"[MOUSE] GROUP {groupId}: unit={selectedUnitId} routeAssigned={hasRoute} waypoint=({firstWaypoint.X},{firstWaypoint.Y},{firstWaypoint.Z})"
                    );
                }
            }
        }

        private void CalculateSelectionBox(
            UnitStore units,
            float microCellPixelSize,
            int currentViewZ)
        {
            float xMin =
                Math.Min(
                    _startDragPixels.X,
                    _currentMousePixels.X
                );

            float xMax =
                Math.Max(
                    _startDragPixels.X,
                    _currentMousePixels.X
                );

            float yMin =
                Math.Min(
                    _startDragPixels.Y,
                    _currentMousePixels.Y
                );

            float yMax =
                Math.Max(
                    _startDragPixels.Y,
                    _currentMousePixels.Y
                );

            bool isSingleClick =
                xMax - xMin < 5f &&
                yMax - yMin < 5f;

            float currentTime =
                _clickClock.ElapsedTime.AsSeconds();

            bool isDoubleClick =
                isSingleClick &&
                currentTime - _lastClickTime < 0.25f;

            _lastClickTime =
                currentTime;

            if (isDoubleClick)
                SelectedUnitIds.Clear();

            for (int i = 0; i < units.Count; i++)
            {
                if (units.HealthMasks[i] == 0)
                    continue;

                if (units.Positions[i].Spatial.Z != currentViewZ)
                    continue;

                float unitPx =
                    units.Positions[i].RenderX *
                    microCellPixelSize;

                float unitPy =
                    units.Positions[i].RenderY *
                    microCellPixelSize;

                if (isSingleClick)
                {
                    float dx =
                        _currentMousePixels.X -
                        unitPx;

                    float dy =
                        _currentMousePixels.Y -
                        unitPy;

                    float dist =
                        (float)Math.Sqrt(
                            dx * dx +
                            dy * dy
                        );

                    if (dist <= 14f)
                    {
                        SelectedUnitIds.Add(i);

                        Console.WriteLine(
                            $"[MOUSE] SELECT unit={i} group={units.CurrentGroupId[i]} pos=({units.Positions[i].Spatial.X},{units.Positions[i].Spatial.Y},{units.Positions[i].Spatial.Z})"
                        );

                        break;
                    }
                }
                else
                {
                    if (unitPx >= xMin &&
                        unitPx <= xMax &&
                        unitPy >= yMin &&
                        unitPy <= yMax)
                    {
                        SelectedUnitIds.Add(i);

                        Console.WriteLine(
                            $"[MOUSE] SELECT unit={i} group={units.CurrentGroupId[i]} pos=({units.Positions[i].Spatial.X},{units.Positions[i].Spatial.Y},{units.Positions[i].Spatial.Z})"
                        );
                    }
                }
            }

            Console.WriteLine(
                $"[MOUSE] SELECTION COMPLETE count={SelectedUnitIds.Count}"
            );
        }

        public void DrawSelectionBox(
            RenderWindow window)
        {
            if (!_isSelecting)
                return;

            float xMin =
                Math.Min(
                    _startDragPixels.X,
                    _currentMousePixels.X
                );

            float xMax =
                Math.Max(
                    _startDragPixels.X,
                    _currentMousePixels.X
                );

            float yMin =
                Math.Min(
                    _startDragPixels.Y,
                    _currentMousePixels.Y
                );

            float yMax =
                Math.Max(
                    _startDragPixels.Y,
                    _currentMousePixels.Y
                );

            RectangleShape box =
                new RectangleShape(
                    new Vector2f(
                        xMax - xMin,
                        yMax - yMin
                    )
                );

            box.Position =
                new Vector2f(
                    xMin,
                    yMin
                );

            box.FillColor =
                new Color(
                    0,
                    150,
                    255,
                    35
                );

            box.OutlineColor =
                new Color(
                    0,
                    200,
                    255,
                    220
                );

            box.OutlineThickness = 1f;

            window.Draw(box);
        }
    }
}