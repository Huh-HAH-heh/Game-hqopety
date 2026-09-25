using Core.Structs;
using Core.Unit.Components;
using System;
using System.Collections.Generic;

namespace Core.Unit.Systems
{
    public sealed class UnitSeparationSystem
    {
        private readonly List<int> _neighborBuffer =
            new List<int>(16);

        private readonly List<int> _sameTileBuffer =
            new List<int>(4);

        private float[] _pushX = Array.Empty<float>();
        private float[] _pushY = Array.Empty<float>();

        public void Update(
            UnitStore units,
            UnitSpatialGrid spatialGrid,
            float deltaTime)
        {
            if (deltaTime <= 0f ||
                units.Count == 0)
            {
                return;
            }

            EnsureStorage(units.Count);

            Array.Clear(_pushX, 0, units.Count);
            Array.Clear(_pushY, 0, units.Count);

            const int SearchRadius = 2;

            const float DesiredDistance = 0.82f;
            const float PushStrength = 8f;

            const float MaxOffset = 0.55f;

            const float SlotFollowSpeed = 10f;
            const float PushFollowSpeed = 14f;

            /*
             * =========================================================
             * 1. Сначала считаем динамические столкновения.
             *
             * ВАЖНО:
             * юниты в одной логической клетке здесь НЕ толкаем.
             *
             * Они будут разложены слотами ниже.
             * =========================================================
             */

            for (int i = 0; i < units.Count; i++)
            {
                if (units.HealthMasks[i] == 0)
                    continue;

                ref var position =
                    ref units.Positions[i];

                float currentX =
                    position.RenderX +
                    units.SeparationOffsetX[i];

                float currentY =
                    position.RenderY +
                    units.SeparationOffsetY[i];

                int queryX =
                    (int)MathF.Floor(currentX);

                int queryY =
                    (int)MathF.Floor(currentY);

                SpatialCoord queryPosition =
                    new SpatialCoord(
                        queryX,
                        queryY,
                        position.Spatial.Z);

                _neighborBuffer.Clear();

                spatialGrid.GetNearby(
                    queryPosition,
                    SearchRadius,
                    _neighborBuffer);

                for (int n = 0; n < _neighborBuffer.Count; n++)
                {
                    int otherId =
                        _neighborBuffer[n];

                    if (otherId <= i)
                        continue;

                    if (units.HealthMasks[otherId] == 0)
                        continue;

                    ref var other =
                        ref units.Positions[otherId];

                    if (other.Spatial.Z != position.Spatial.Z)
                        continue;

                    /*
                     * Одна логическая клетка:
                     *
                     * НЕ сталкиваем.
                     *
                     * Они будут разложены по слотам.
                     */

                    if (other.Spatial.X == position.Spatial.X &&
                        other.Spatial.Y == position.Spatial.Y)
                    {
                        continue;
                    }

                    float otherX =
                        other.RenderX +
                        units.SeparationOffsetX[otherId];

                    float otherY =
                        other.RenderY +
                        units.SeparationOffsetY[otherId];

                    float dx =
                        currentX - otherX;

                    float dy =
                        currentY - otherY;

                    float distanceSqr =
                        dx * dx +
                        dy * dy;

                    if (distanceSqr >=
                        DesiredDistance * DesiredDistance)
                    {
                        continue;
                    }

                    float distance;

                    if (distanceSqr < 0.0001f)
                    {
                        uint seed =
                            (uint)(
                                i * 73856093 ^
                                otherId * 19349663);

                        float angle =
                            (seed % 628u) * 0.01f;

                        dx = MathF.Cos(angle);
                        dy = MathF.Sin(angle);

                        distance = 0f;
                    }
                    else
                    {
                        distance =
                            MathF.Sqrt(distanceSqr);
                    }

                    float strength =
                        (DesiredDistance - distance) /
                        DesiredDistance;

                    float pushX =
                        dx /
                        MathF.Max(distance, 0.0001f) *
                        strength;

                    float pushY =
                        dy /
                        MathF.Max(distance, 0.0001f) *
                        strength;

                    _pushX[i] += pushX;
                    _pushY[i] += pushY;

                    _pushX[otherId] -= pushX;
                    _pushY[otherId] -= pushY;
                }
            }

            /*
             * =========================================================
             * 2. Теперь формируем конечный target offset.
             *
             * Для одной клетки:
             *
             *   1 юнит -> 0,0
             *   2       -> -0.30 / +0.30
             *   3       -> треугольник
             *
             * При этом НИКАКОГО push между ними нет.
             * =========================================================
             */

            for (int i = 0; i < units.Count; i++)
            {
                if (units.HealthMasks[i] == 0)
                    continue;

                ref var position =
                    ref units.Positions[i];

                _sameTileBuffer.Clear();

                IReadOnlyList<int> sameTile =
                    spatialGrid.GetUnitsAt(position.Spatial);

                for (int n = 0; n < sameTile.Count; n++)
                {
                    int otherId =
                        sameTile[n];

                    if (otherId == i)
                        continue;

                    if (units.HealthMasks[otherId] == 0)
                        continue;

                    _sameTileBuffer.Add(otherId);
                }

                int count =
                    _sameTileBuffer.Count + 1;

                float targetX = 0f;
                float targetY = 0f;

                if (count == 2)
                {
                    int slot = 0;

                    if (_sameTileBuffer.Count > 0 &&
                        _sameTileBuffer[0] < i)
                    {
                        slot = 1;
                    }

                    if (slot == 0)
                        targetX = -0.30f;
                    else
                        targetX = 0.30f;
                }
                else if (count >= 3)
                {
                    int slot = 0;

                    for (int n = 0; n < _sameTileBuffer.Count; n++)
                    {
                        if (_sameTileBuffer[n] < i)
                            slot++;
                    }

                    if (slot == 0)
                    {
                        targetX = 0f;
                        targetY = -0.34f;
                    }
                    else if (slot == 1)
                    {
                        targetX = -0.30f;
                        targetY = 0.22f;
                    }
                    else
                    {
                        targetX = 0.30f;
                        targetY = 0.22f;
                    }
                }

                /*
                 * -----------------------------------------------------
                 * Если в этой клетке несколько юнитов:
                 *
                 *     slot target
                 *
                 * Если один:
                 *
                 *     target = 0
                 *
                 * Dynamic push добавляем только если
                 * логически рядом НЕ находятся в одной клетке.
                 * -----------------------------------------------------
                 */

                if (count == 1)
                {
                    targetX =
                        _pushX[i] *
                        PushStrength *
                        deltaTime;

                    targetY =
                        _pushY[i] *
                        PushStrength *
                        deltaTime;
                }

                /*
                 * Если несколько юнитов в одной клетке,
                 * push НЕ участвует вообще.
                 */

                float currentX =
                    units.SeparationOffsetX[i];

                float currentY =
                    units.SeparationOffsetY[i];

                float followSpeed =
                    count > 1
                        ? SlotFollowSpeed
                        : PushFollowSpeed;

                float t =
                    MathF.Min(
                        1f,
                        followSpeed * deltaTime);

                currentX +=
                    (targetX - currentX) * t;

                currentY +=
                    (targetY - currentY) * t;

                /*
                 * Ограничение только визуального offset.
                 */

                float lengthSqr =
                    currentX * currentX +
                    currentY * currentY;

                if (lengthSqr >
                    MaxOffset * MaxOffset)
                {
                    float length =
                        MathF.Sqrt(lengthSqr);

                    float scale =
                        MaxOffset / length;

                    currentX *= scale;
                    currentY *= scale;
                }

                units.SeparationOffsetX[i] =
                    currentX;

                units.SeparationOffsetY[i] =
                    currentY;
            }
        }

        private void EnsureStorage(int size)
        {
            if (_pushX.Length >= size)
                return;

            int newSize =
                Math.Max(
                    size,
                    Math.Max(
                        16,
                        _pushX.Length * 2));

            Array.Resize(
                ref _pushX,
                newSize);

            Array.Resize(
                ref _pushY,
                newSize);
        }
    }
}