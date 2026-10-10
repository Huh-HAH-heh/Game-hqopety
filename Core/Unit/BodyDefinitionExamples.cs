using System;
using System.Numerics;

namespace Core.Unit;

/// <summary>
/// Small stress example: three torsos, four heads, five arms and ten legs.
/// Build once and pass the returned array to UnitSimulation.SpawnCustom.
/// </summary>
public static class BodyDefinitionExamples
{
    public static BodyPartDefinition[] CreateMultiLimbCreature()
    {
        var parts = new BodyPartDefinition[22];

        // Main torso.
        parts[0] = new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.25f, 0.40f, 0.30f),
            BodyPrimitive.Box);

        // Two additional torso segments.
        parts[1] = new BodyPartDefinition(
            new Vector3(0f, -0.52f, 0f),
            new Vector3(0.20f, 0.30f, 0.27f),
            BodyPrimitive.Box,
            parentIndex: 0);

        parts[2] = new BodyPartDefinition(
            new Vector3(0f, 0.52f, 0f),
            new Vector3(0.20f, 0.30f, 0.27f),
            BodyPrimitive.Box,
            parentIndex: 0);

        // Four heads.
        parts[3] = Head(new Vector3(-0.22f, -0.66f, 0f));
        parts[4] = Head(new Vector3(0.22f, -0.66f, 0f));
        parts[5] = Head(new Vector3(-0.22f, 0.66f, 0f));
        parts[6] = Head(new Vector3(0.22f, 0.66f, 0f));

        // Five arms.
        parts[7] = Limb(new Vector3(-0.34f, -0.32f, 0f), MathF.PI / 2f, 0.22f);
        parts[8] = Limb(new Vector3(0.34f, -0.32f, 0f), -MathF.PI / 2f, 0.22f);
        parts[9] = Limb(new Vector3(-0.38f, 0f, 0f), MathF.PI / 2f, 0.24f);
        parts[10] = Limb(new Vector3(0.38f, 0f, 0f), -MathF.PI / 2f, 0.24f);
        parts[11] = Limb(new Vector3(0f, 0.36f, 0f), 0f, 0.22f);

        // Ten legs, distributed on both sides of the lower body.
        parts[12] = Limb(new Vector3(-0.15f, 0.38f, 0f), -0.25f, 0.23f);
        parts[13] = Limb(new Vector3(0.15f, 0.38f, 0f), 0.25f, 0.23f);
        parts[14] = Limb(new Vector3(-0.32f, 0.52f, 0f), -0.35f, 0.24f);
        parts[15] = Limb(new Vector3(0.32f, 0.52f, 0f), 0.35f, 0.24f);
        parts[16] = Limb(new Vector3(-0.42f, 0.67f, 0f), -0.45f, 0.22f);
        parts[17] = Limb(new Vector3(0.42f, 0.67f, 0f), 0.45f, 0.22f);
        parts[18] = Limb(new Vector3(-0.18f, 0.78f, 0f), -0.15f, 0.20f);
        parts[19] = Limb(new Vector3(0.18f, 0.78f, 0f), 0.15f, 0.20f);
        parts[20] = Limb(new Vector3(-0.30f, 0.88f, 0f), -0.25f, 0.18f);
        parts[21] = Limb(new Vector3(0.30f, 0.88f, 0f), 0.25f, 0.18f);

        return parts;
    }

    private static BodyPartDefinition Head(Vector3 position) =>
        new BodyPartDefinition(
            position,
            new Vector3(0.12f, 0.12f, 0.12f),
            BodyPrimitive.Circle,
            parentIndex: 0);

    private static BodyPartDefinition Limb(
        Vector3 position,
        float rotation,
        float halfLength) =>
        new BodyPartDefinition(
            position,
            new Vector3(0.055f, halfLength, 0.07f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: rotation);
}
