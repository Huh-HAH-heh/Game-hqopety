using System;
using Core.Combat;

namespace Core.Unit;

public static class ShotAccuracy
{
    public static float CalculateSpread(
        float baseSpread,
        float sightEfficiency,
        float aimingAccuracy,
        float movementSpeed,
        float movementSpread,
        float recoil,
        float suppressionMultiplier,
        float range,
        float effectiveRange,
        float aimProgress,
        AimMode aimMode,
        float circularError)
    {
        float sight = MathF.Max(0.10f, sightEfficiency);
        float skill = MathF.Max(0.10f, aimingAccuracy);

        float movement =
            1f +
            MathF.Min(
                3f,
                movementSpeed *
                MathF.Max(0f, movementSpread));

        float rangeFactor =
            effectiveRange <= 0.001f
                ? 0f
                : Math.Clamp(
                    range / effectiveRange,
                    0f,
                    2f);

        float aimFactor =
            1f -
            Math.Clamp(
                aimProgress,
                0f,
                1f);

        float modeFactor =
            aimMode switch
            {
                AimMode.Snapshot => 1.75f,
                AimMode.SuppressFire => 2.25f,
                _ => 1f + aimFactor * 1.35f
            };

        float rangeError =
            baseSpread *
            rangeFactor *
            0.50f;

        float swayError =
            baseSpread *
            aimFactor *
            0.70f;

        float recoilError =
            MathF.Max(0f, recoil) *
            0.015f;

        float spread =
            baseSpread /
            sight /
            skill;

        spread *= movement;
        spread *= MathF.Max(
            1f,
            suppressionMultiplier);
        spread *= modeFactor;

        return
            spread +
            rangeError +
            swayError +
            recoilError +
            MathF.Max(
                0f,
                circularError);
    }
}
