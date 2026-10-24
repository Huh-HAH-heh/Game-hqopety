using System.Numerics;
using Core.Unit;

namespace RimClone.Render;

internal static class UnitGameplayZoneRenderGeometry
{
    public static RenderBodyPart CreatePart(in UnitGameplayZoneGeometry zone)
    {
        BodyPartRole role = zone.Zone switch
        {
            UnitGameplayZone.Head => BodyPartRole.Head,
            UnitGameplayZone.Torso => BodyPartRole.Torso,
            UnitGameplayZone.LowerBody => BodyPartRole.Thigh,
            _ => BodyPartRole.Generic
        };

        return new RenderBodyPart(
            zone.Center,
            new Vector3(zone.HalfForward, zone.HalfSide, zone.HalfHeight),
            zone.Rotation,
            BodyPrimitive.Circle,
            role);
    }
}
