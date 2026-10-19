using System;

namespace Core.Unit;

/// <summary>
/// Derives weapon handling from current limb health. The calculation is allocation-free
/// and is cheap enough to use when firing, reloading, or resolving a melee strike.
/// </summary>
public static class UnitCombatConditionSystem
{
    public static UnitCombatCondition Calculate(UnitHealthStore? health, int unitIndex)
    {
        if (health == null ||
            (uint)unitIndex >= (uint)health.Capacity ||
            health.PartCount[unitIndex] <= 0)
        {
            return UnitCombatCondition.Healthy;
        }

        float leftArmHp = 0f, leftArmMax = 0f;
        float rightArmHp = 0f, rightArmMax = 0f;
        float leftHandHp = 0f, leftHandMax = 0f;
        float rightHandHp = 0f, rightHandMax = 0f;

        int start = unitIndex * health.MaxParts;
        int count = health.PartCount[unitIndex];

        for (int i = 0; i < count; i++)
        {
            int index = start + i;
            float maxHp = health.MaxHitPoints[index];
            if (maxHp <= 0f)
                continue;

            float hp = health.Present[index]
                ? Math.Clamp(health.HitPoints[index], 0f, maxHp)
                : 0f;

            switch (health.Part[index])
            {
                case UnitHealthPartId.LeftArm:
                    leftArmHp += hp;
                    leftArmMax += maxHp;
                    break;
                case UnitHealthPartId.RightArm:
                    rightArmHp += hp;
                    rightArmMax += maxHp;
                    break;
                case UnitHealthPartId.LeftHand:
                    leftHandHp += hp;
                    leftHandMax += maxHp;
                    break;
                case UnitHealthPartId.RightHand:
                    rightHandHp += hp;
                    rightHandMax += maxHp;
                    break;
            }
        }

        bool hasArms = leftArmMax + rightArmMax > 0f;
        bool hasHands = leftHandMax + rightHandMax > 0f;

        float leftArm = Ratio(leftArmHp, leftArmMax);
        float rightArm = Ratio(rightArmHp, rightArmMax);
        float leftHand = Ratio(leftHandHp, leftHandMax);
        float rightHand = Ratio(rightHandHp, rightHandMax);

        float armIntegrity = AverageDefined(
            leftArm, leftArmMax, rightArm, rightArmMax);
        float handIntegrity = AverageDefined(
            leftHand, leftHandMax, rightHand, rightHandMax);

        bool armsFunctional =
            (!hasArms ||
             (SideFunctional(leftArm, leftArmMax) &&
              SideFunctional(rightArm, rightArmMax)));
        bool handsFunctional =
            (!hasHands ||
             (SideFunctional(leftHand, leftHandMax) &&
              SideFunctional(rightHand, rightHandMax)));

        bool twoHandedWeaponUsable =
            (!hasArms && !hasHands) ||
            (armsFunctional && handsFunctional);

        float aimSpreadMultiplier = Math.Clamp(
            1f + (1f - armIntegrity) * 1.25f +
            (1f - handIntegrity) * 1.6f,
            1f,
            4f);

        float reloadTimeMultiplier = Math.Clamp(
            1f + (1f - armIntegrity) * 0.75f +
            (1f - handIntegrity) * 1.75f,
            1f,
            4f);

        float meleeDamageMultiplier = Math.Clamp(
            (0.4f + 0.6f * armIntegrity) *
            (0.65f + 0.35f * handIntegrity),
            0.2f,
            1f);

        return new UnitCombatCondition(
            aimSpreadMultiplier,
            reloadTimeMultiplier,
            meleeDamageMultiplier,
            armIntegrity,
            handIntegrity,
            twoHandedWeaponUsable);
    }

    private static float Ratio(float hp, float maxHp) =>
        maxHp > 0f ? Math.Clamp(hp / maxHp, 0f, 1f) : 1f;

    private static float AverageDefined(
        float leftValue,
        float leftMax,
        float rightValue,
        float rightMax)
    {
        float sum = 0f;
        int count = 0;
        if (leftMax > 0f)
        {
            sum += leftValue;
            count++;
        }

        if (rightMax > 0f)
        {
            sum += rightValue;
            count++;
        }

        return count > 0 ? sum / count : 1f;
    }

    private static bool SideFunctional(float integrity, float maxHp) =>
        maxHp <= 0f || integrity > 0.25f;
}

public readonly struct UnitCombatCondition
{
    public static UnitCombatCondition Healthy { get; } =
        new(1f, 1f, 1f, 1f, 1f, true);

    public float AimSpreadMultiplier { get; }
    public float ReloadTimeMultiplier { get; }
    public float MeleeDamageMultiplier { get; }
    public float ArmIntegrity { get; }
    public float HandIntegrity { get; }
    public bool CanUseTwoHandedWeapon { get; }

    public UnitCombatCondition(
        float aimSpreadMultiplier,
        float reloadTimeMultiplier,
        float meleeDamageMultiplier,
        float armIntegrity,
        float handIntegrity,
        bool canUseTwoHandedWeapon)
    {
        AimSpreadMultiplier = aimSpreadMultiplier;
        ReloadTimeMultiplier = reloadTimeMultiplier;
        MeleeDamageMultiplier = meleeDamageMultiplier;
        ArmIntegrity = armIntegrity;
        HandIntegrity = handIntegrity;
        CanUseTwoHandedWeapon = canUseTwoHandedWeapon;
    }
}
