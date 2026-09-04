//using Core.Map;
//using Core.Unit.Components;
//using System;
//using System.Collections.Generic;

//namespace Core.Unit.Systems
//{
//    public sealed class CrowdPushSystem
//    {
//        private readonly List<int> _neighbors =
//            new List<int>(16);

//        private const int SearchRadius = 1;

//        private const float UnitRadius = 0.35f;
//        private const float MinDistance = UnitRadius * 2f;

//        private const float PushStrength = 10f;

//        private const float MaxVisualOffset = 0.42f;

//        private const float MovingResistance = 0.35f;
//        private const float IdleResistance = 1f;

//        public void Update(
//            UnitStore units,
//            UnitSpatialGrid spatialGrid,
//            float deltaTime,
//            WorldMap map)
//        {
//            if (deltaTime <= 0f)
//                return;

//            for (int i = 0; i < units.Count; i++)
//            {
//                if (units.HealthMasks[i] == 0)
//                    continue;

//                ref var a =
//                    ref units.Positions[i];

//                _neighbors.Clear();

//                spatialGrid.GetNearby(
//                    a.Spatial,
//                    SearchRadius,
//                    _neighbors);

//                float pushX = 0f;
//                float pushY = 0f;

//                for (int n = 0;
//                     n < _neighbors.Count;
//                     n++)
//                {
//                    int j =
//                        _neighbors[n];

//                    if (j == i ||
//                        j < 0 ||
//                        j >= units.Count)
//                    {
//                        continue;
//                    }

//                    if (units.HealthMasks[j] == 0)
//                        continue;

//                    ref var b =
//                        ref units.Positions[j];

//                    if (a.Spatial.Z !=
//                        b.Spatial.Z)
//                    {
//                        continue;
//                    }

//                    float dx =
//                        a.RenderX -
//                        b.RenderX;

//                    float dy =
//                        a.RenderY -
//                        b.RenderY;

//                    float distanceSqr =
//                        dx * dx +
//                        dy * dy;

//                    if (distanceSqr >=
//                        MinDistance * MinDistance)
//                    {
//                        continue;
//                    }

//                    if (distanceSqr < 0.000001f)
//                    {
//                        GetStableDirection(
//                            i,
//                            j,
//                            out dx,
//                            out dy);

//                        pushX += dx;
//                        pushY += dy;
//                        continue;
//                    }

//                    float distance =
//                        MathF.Sqrt(distanceSqr);

//                    float penetration =
//                        MinDistance -
//                        distance;

//                    float nx =
//                        dx / distance;

//                    float ny =
//                        dy / distance;

//                    float resistance =
//                        units.Movement[j].State ==
//                        MovementState.Moving
//                            ? MovingResistance
//                            : IdleResistance;

//                    float force =
//                        penetration *
//                        PushStrength *
//                        resistance;

//                    pushX +=
//                        nx *
//                        force;

//                    pushY +=
//                        ny *
//                        force;
//                }

//                if (pushX != 0f ||
//                    pushY != 0f)
//                {
//                    a.RenderX +=
//                        pushX *
//                        deltaTime;

//                    a.RenderY +=
//                        pushY *
//                        deltaTime;
//                }

//                ClampVisualOffset(
//                    ref a);

//                KeepVisualPositionInsideWorld(
//                    ref a,
//                    map);
//            }
//        }

//        private void ClampVisualOffset(
//            ref UnitPosition position)
//        {
//            float dx =
//                position.RenderX -
//                position.Spatial.X;

//            float dy =
//                position.RenderY -
//                position.Spatial.Y;

//            float lengthSqr =
//                dx * dx +
//                dy * dy;

//            float max =
//                MaxVisualOffset;

//            if (lengthSqr <=
//                max * max)
//            {
//                return;
//            }

//            float length =
//                MathF.Sqrt(lengthSqr);

//            if (length < 0.0001f)
//                return;

//            float scale =
//                max / length;

//            position.RenderX =
//                position.Spatial.X +
//                dx * scale;

//            position.RenderY =
//                position.Spatial.Y +
//                dy * scale;
//        }

//        private void KeepVisualPositionInsideWorld(
//            ref UnitPosition position,
//            WorldMap map)
//        {
//            MapLayer layer =
//                map.GetLayer(
//                    position.Spatial.Z);

//            if (layer == null)
//            {
//                position.RenderX =
//                    position.Spatial.X;

//                position.RenderY =
//                    position.Spatial.Y;

//                return;
//            }

//            int maxX =
//                MapLayer.WidthInRegions *
//                MapRegion.MicroSize -
//                1;

//            int maxY =
//                MapLayer.HeightInRegions *
//                MapRegion.MicroSize -
//                1;

//            if (position.RenderX < 0f)
//                position.RenderX = 0f;

//            if (position.RenderY < 0f)
//                position.RenderY = 0f;

//            if (position.RenderX > maxX)
//                position.RenderX = maxX;

//            if (position.RenderY > maxY)
//                position.RenderY = maxY;
//        }

//        private void GetStableDirection(
//            int a,
//            int b,
//            out float x,
//            out float y)
//        {
//            uint hash =
//                unchecked(
//                    (uint)(a * 73856093) ^
//                    (uint)(b * 19349663));

//            float angle =
//                (hash % 628u) *
//                0.01f;

//            if (a > b)
//                angle += MathF.PI;

//            x = MathF.Cos(angle);
//            y = MathF.Sin(angle);
//        }
//    }
//}