//using Core.Unit.Components;
//using System;
//using System.Collections.Generic;

//namespace Core.Unit.Systems
//{
//    public sealed class UnitSeparationSystem
//    {
//        private readonly List<int> _neighborBuffer =
//            new List<int>(16);

//        public void Update(
//            UnitStore units,
//            UnitSpatialGrid spatialGrid,
//            float deltaTime)
//        {
//            if (deltaTime <= 0f)
//                return;

//            const int SearchRadius = 1;
//            const float DesiredDistance = 0.55f;
//            const float SeparationStrength = 3.2f;
//            const float MaxOffset = 0.36f;
//            const float CenteringStrength = 1.15f;

//            for (int i = 0; i < units.Count; i++)
//            {
//                if (units.HealthMasks[i] == 0)
//                    continue;

//                ref var position =
//                    ref units.Positions[i];

//                _neighborBuffer.Clear();

//                spatialGrid.GetNearby(
//                    position.Spatial,
//                    SearchRadius,
//                    _neighborBuffer
//                );

//                float separationX = 0f;
//                float separationY = 0f;

//                int neighbors = 0;

//                for (int n = 0; n < _neighborBuffer.Count; n++)
//                {
//                    int otherId =
//                        _neighborBuffer[n];

//                    if (otherId == i ||
//                        units.HealthMasks[otherId] == 0)
//                    {
//                        continue;
//                    }

//                    ref var other =
//                        ref units.Positions[otherId];

//                    if (other.Spatial.Z != position.Spatial.Z)
//                        continue;

//                    float dx =
//                        position.RenderX -
//                        other.RenderX;

//                    float dy =
//                        position.RenderY -
//                        other.RenderY;

//                    float distanceSqr =
//                        dx * dx +
//                        dy * dy;

//                    if (distanceSqr <= 0.0001f)
//                    {
//                        uint seed =
//                            (uint)(
//                                i * 73856093 ^
//                                otherId * 19349663
//                            );

//                        float angle =
//                            (seed % 628u) *
//                            0.01f;

//                        separationX += MathF.Cos(angle);
//                        separationY += MathF.Sin(angle);
//                        neighbors++;
//                        continue;
//                    }

//                    float distance =
//                        MathF.Sqrt(distanceSqr);

//                    if (distance >= DesiredDistance)
//                        continue;

//                    float strength =
//                        (DesiredDistance - distance) /
//                        DesiredDistance;

//                    separationX +=
//                        dx / distance *
//                        strength;

//                    separationY +=
//                        dy / distance *
//                        strength;

//                    neighbors++;
//                }

//                // Лёгкое возвращение к центру клетки не даёт накопить постоянный
//                // боковой сдвиг после выхода из толпы.
//                float centerX =
//                    position.Spatial.X - position.RenderX;

//                float centerY =
//                    position.Spatial.Y - position.RenderY;

//                position.RenderX +=
//                    separationX *
//                    SeparationStrength *
//                    deltaTime;

//                position.RenderY +=
//                    separationY *
//                    SeparationStrength *
//                    deltaTime;

//                position.RenderX +=
//                    centerX *
//                    CenteringStrength *
//                    deltaTime;

//                position.RenderY +=
//                    centerY *
//                    CenteringStrength *
//                    deltaTime;

//                if (neighbors == 0 &&
//                    MathF.Abs(centerX) < 0.005f &&
//                    MathF.Abs(centerY) < 0.005f)
//                {
//                    continue;
//                }

//                float offsetX =
//                    position.RenderX - position.Spatial.X;

//                float offsetY =
//                    position.RenderY - position.Spatial.Y;

//                if (offsetX > MaxOffset)
//                    position.RenderX =
//                        position.Spatial.X + MaxOffset;

//                if (offsetX < -MaxOffset)
//                    position.RenderX =
//                        position.Spatial.X - MaxOffset;

//                if (offsetY > MaxOffset)
//                    position.RenderY =
//                        position.Spatial.Y + MaxOffset;

//                if (offsetY < -MaxOffset)
//                    position.RenderY =
//                        position.Spatial.Y - MaxOffset;
//            }
//        }
//    }
//}