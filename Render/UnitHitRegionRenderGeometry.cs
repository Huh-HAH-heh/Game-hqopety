using System.Numerics;
using Core.Unit;

namespace RimClone.Render;

internal static class UnitHitRegionRenderGeometry
{
    // The top-down renderer has no camera pitch. Compress vertical world height
    // into screen Y so stacked Z regions remain individually visible.
    private const float VerticalScreenScale = 0.45f;

    public static RenderBodyPart CreatePart(
        UnitStore units,
        int unitIndex,
        in UnitCircleHitRegion region,
        in UnitHitRegionGeometry geometry)
    {
        Vector3 root = units.Position[unitIndex];
        Vector3 center = geometry.Center;
        center.Y -= (geometry.Center.Z - root.Z) * VerticalScreenScale;

        return new RenderBodyPart(
            center,
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
