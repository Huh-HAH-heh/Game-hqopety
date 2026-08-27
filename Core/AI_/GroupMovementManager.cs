using Core.Items;
using Core.Map;
using Core.Structs;
using Core.Unit.Components;
using Core.Unit.Systems.CombatPath;
using System;

namespace Core.AI
{
    public struct GroupMacroPath
    {
        public int GroupId;
        public int TotalSteps;
        public SpatialCoord[] GlobalSteps;
    }

    public class GroupMovementManager
    {
        public const int MaxWaypointsPointsPerPath = 512;
        public const int MaxSubwaypointsToUnit = 8;

        private GroupMacroPath[] _groupPaths;

        public int[] _unitGlobalPathIndices;
        public int[] _unitSubwaypointsCount;
        public SpatialCoord[,] _unitSubwaypointsBuffer;

        public GroupMovementManager(int initialMaxGroups, int initialMaxUnits)
        {
            if (initialMaxGroups < 1)
                initialMaxGroups = 1;

            if (initialMaxUnits < 1)
                initialMaxUnits = 1;

            _groupPaths = new GroupMacroPath[initialMaxGroups];

            for (int i = 0; i < initialMaxGroups; i++)
            {
                _groupPaths[i].GlobalSteps =
                    new SpatialCoord[MaxWaypointsPointsPerPath];
            }

            _unitGlobalPathIndices =
                new int[initialMaxUnits];

            _unitSubwaypointsCount =
                new int[initialMaxUnits];

            _unitSubwaypointsBuffer =
                new SpatialCoord[
                    initialMaxUnits,
                    MaxSubwaypointsToUnit
                ];
        }

        public bool RequestAndStoreGroupRoute(
            WorldMap map,
            EdificeStore edifices,
            MapLayer layer,
            int groupId,
            SpatialCoord start,
            SpatialCoord target,
            int[] unitIds,
            int unitCount)
        {
            if (layer == null)
                return false;

            if (groupId < 0)
                return false;

            if (unitIds == null || unitCount <= 0)
                return false;

            if (unitCount > unitIds.Length)
                unitCount = unitIds.Length;

            EnsureGroupsCapacity(groupId);

            SpatialCoord[] rawPath =
                new SpatialCoord[MaxWaypointsPointsPerPath];

            int rawLength;

            bool pathFound =
                PureAStarPathfinder.FindRoute(
                    layer,
                    start,
                    target,
                    rawPath,
                    MaxWaypointsPointsPerPath,
                    out rawLength
                );

            if (!pathFound || rawLength <= 0)
                return false;

            ref GroupMacroPath pathStorage =
                ref _groupPaths[groupId];

            pathStorage.GroupId = groupId;
            pathStorage.TotalSteps = 0;

            int optimizedStepsCount = 0;
            int currentIdx = 0;

            pathStorage.GlobalSteps[
                optimizedStepsCount++
            ] = rawPath[0];

            while (
                currentIdx < rawLength - 1 &&
                optimizedStepsCount <
                MaxWaypointsPointsPerPath
            )
            {
                int low = currentIdx + 1;
                int high = rawLength - 1;

                int bestVisibleIdx =
                    currentIdx + 1;

                SpatialCoord startWaypoint =
                    rawPath[currentIdx];

                while (low <= high)
                {
                    int mid =
                        (low + high) / 2;

                    SpatialCoord candidate =
                        rawPath[mid];

                    if (VisibilityChecker.HasLineOfSight(
                        map,
                        edifices,
                        startWaypoint.X,
                        startWaypoint.Y,
                        candidate.X,
                        candidate.Y,
                        startWaypoint.Z))
                    {
                        bestVisibleIdx = mid;
                        low = mid + 1;
                    }
                    else
                    {
                        high = mid - 1;
                    }
                }

                pathStorage.GlobalSteps[
                    optimizedStepsCount++
                ] = rawPath[bestVisibleIdx];

                currentIdx = bestVisibleIdx;
            }

            pathStorage.TotalSteps =
                optimizedStepsCount;

            AssignPathToUnits(
                pathStorage,
                unitIds,
                unitCount
            );

            return true;
        }

        private void AssignPathToUnits(
            GroupMacroPath path,
            int[] unitIds,
            int unitCount)
        {
            for (int i = 0; i < unitCount; i++)
            {
                int unitId = unitIds[i];

                if (unitId < 0 ||
                    unitId >= _unitGlobalPathIndices.Length)
                {
                    continue;
                }

                _unitGlobalPathIndices[unitId] = 0;

                _unitSubwaypointsCount[unitId] = 0;

                int count =
                    Math.Min(
                        path.TotalSteps,
                        MaxSubwaypointsToUnit
                    );

                for (int w = 0; w < count; w++)
                {
                    _unitSubwaypointsBuffer[
                        unitId,
                        w
                    ] = path.GlobalSteps[w];
                }

                _unitSubwaypointsCount[unitId] =
                    count;
            }
        }

        public bool TryRefillUnitSubwaypoints(
            int unitId)
        {
            if (unitId < 0 ||
                unitId >= _unitGlobalPathIndices.Length)
            {
                return false;
            }

            return true;
        }

        private void EnsureGroupsCapacity(
            int requiredGroupId)
        {
            if (requiredGroupId < _groupPaths.Length)
                return;

            int newCapacity =
                Math.Max(
                    _groupPaths.Length * 2,
                    requiredGroupId + 1
                );

            GroupMacroPath[] newArray =
                new GroupMacroPath[newCapacity];

            Array.Copy(
                _groupPaths,
                newArray,
                _groupPaths.Length
            );

            for (
                int i = _groupPaths.Length;
                i < newCapacity;
                i++)
            {
                newArray[i].GlobalSteps =
                    new SpatialCoord[
                        MaxWaypointsPointsPerPath
                    ];
            }

            _groupPaths = newArray;
        }

        public void ResizeStorage(
            int newSize)
        {
            int oldSize =
                _unitGlobalPathIndices.Length;

            if (newSize <= oldSize)
                return;

            Array.Resize(
                ref _unitGlobalPathIndices,
                newSize
            );

            Array.Resize(
                ref _unitSubwaypointsCount,
                newSize
            );

            SpatialCoord[,] newBuffer =
                new SpatialCoord[
                    newSize,
                    MaxSubwaypointsToUnit
                ];

            for (int u = 0; u < oldSize; u++)
            {
                for (
                    int w = 0;
                    w < MaxSubwaypointsToUnit;
                    w++
                )
                {
                    newBuffer[u, w] =
                        _unitSubwaypointsBuffer[u, w];
                }
            }

            _unitSubwaypointsBuffer =
                newBuffer;
        }

        public bool HasRouteForUnit(
            int unitId)
        {
            if (unitId < 0 ||
                unitId >= _unitGlobalPathIndices.Length)
            {
                return false;
            }

            return _unitSubwaypointsCount[unitId] > 0;
        }

        public SpatialCoord GetCurrentWaypoint(
            int unitId)
        {
            if (unitId < 0 ||
                unitId >= _unitGlobalPathIndices.Length)
            {
                return default;
            }

            if (_unitSubwaypointsCount[unitId] <= 0)
                return default;

            return _unitSubwaypointsBuffer[
                unitId,
                0
            ];
        }

        public void AdvanceWaypoint(
            int unitId)
        {
            if (unitId < 0 ||
                unitId >= _unitGlobalPathIndices.Length)
            {
                return;
            }

            int count =
                _unitSubwaypointsCount[unitId];

            if (count <= 0)
                return;

            for (int i = 0; i < count - 1; i++)
            {
                _unitSubwaypointsBuffer[
                    unitId,
                    i
                ] =
                _unitSubwaypointsBuffer[
                    unitId,
                    i + 1
                ];
            }

            _unitSubwaypointsCount[unitId]--;

            _unitGlobalPathIndices[unitId]++;
        }
    }
}