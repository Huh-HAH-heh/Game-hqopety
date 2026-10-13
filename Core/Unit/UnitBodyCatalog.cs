using System;

namespace Core.Unit;

/// <summary>Compatibility access to the central anatomy catalog.</summary>
internal static class UnitBodyCatalog
{
    public static ReadOnlySpan<BodyPartDefinition> Get(UnitBodyType bodyType) =>
        UnitAnatomyCatalog.Get(bodyType).BodyParts;
}
