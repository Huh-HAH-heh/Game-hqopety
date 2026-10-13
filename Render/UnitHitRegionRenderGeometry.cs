using System;
using System.Numerics;
using Core.Unit;

namespace RimClone.Render;

internal static class UnitHitRegionRenderGeometry
{
    // This renderer is top-down, so preserve the region's real XY coordinates.
    // Height remains in Z for simulation, hit testing, and visibility.
    public static RenderBodyPart CreatePart(
        UnitStore units,
        int unitIndex,
        in UnitCircleHitRegion region,
        in UnitHitRegionGeometry geometry)
    {
        return new RenderBodyPart(
            geometry.Center,
            new Vector3(geometry.Radius, geometry.Radius, geometry.HalfHeight),
            0f,
            BodyPrimitive.Circle,
            GetRole(region));
    }

    private static BodyPartRole GetRole(in UnitCircleHitRegion region)
    {
        ReadOnlySpan<HitRegionPartBinding> parts = region.Parts;
        bool torso = false;
        bool legs = false;

        for (int i = 0; i < parts.Length; i++)
        {
            switch (parts[i].Part)
            {
                case UnitHealthPartId.Head:
                    return BodyPartRole.Head;
                case UnitHealthPartId.LowerBody:
                    legs = true;
                    break;
                case UnitHealthPartId.Torso:
                    torso = true;
                    break;
                case UnitHealthPartId.LeftLeg:
                case UnitHealthPartId.RightLeg:
                case UnitHealthPartId.LeftFoot:
                case UnitHealthPartId.RightFoot:
                    legs = true;
                    break;
            }
        }

        if (torso)
            return BodyPartRole.Torso;

        return legs ? BodyPartRole.Thigh : BodyPartRole.Generic;
    }
}
