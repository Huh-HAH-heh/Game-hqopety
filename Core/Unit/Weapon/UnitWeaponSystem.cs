using System;
using System.Numerics;
using Core.Combat;
using Core.Items;

namespace Core.Unit;

public sealed class UnitWeaponSystem
{
    public void Update(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        ProjectileStore projectiles,
        float deltaTime)
    {
        weapons.UpdateTimers(deltaTime);
        weapons.AdvanceBurstTimer(deltaTime);

        ReadOnlySpan<int> active =
            units.ActiveIndices;

        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];

            for (int slot = 0;
                 slot < UnitInventoryStore.WeaponSlotCount;
                 slot++)
            {
                UnitWeaponSlot weaponSlot =
                    (UnitWeaponSlot)slot;

                int stateIndex =
                    UnitWeaponStore.GetIndex(
                        unit,
                        weaponSlot);

                short inventorySlot =
                    inventory.GetWeaponEquipment(
                        unit,
                        weaponSlot);

                if (inventorySlot < 0)
                {
                    weapons.ClearSlot(
                        unit,
                        weaponSlot);
                    continue;
                }

                RangedWeaponConfig? weapon =
                    inventory.GetItem(
                        unit,
                        inventorySlot) as RangedWeaponConfig;

                if (weapon == null)
                    continue;

                if (weapons.Reloading[stateIndex] &&
                    weapons.ReloadTimer[stateIndex] <= 0f)
                {
                    weapons.FinishReload(
                        unit,
                        weaponSlot,
                        weapon.MagazineSize);
                }

                if (!weapons.Reloading[stateIndex] &&
                    weapons.Ammo[stateIndex] <= 0 &&
                    weapons.ReserveAmmo[stateIndex] > 0 &&
                    weapon.MagazineSize > 0)
                {
                    weapons.StartReload(
                        unit,
                        weaponSlot,
                        weapon.ReloadTime);
                }

                if (weapon.DefaultFireMode != FireMode.Burst ||
                    weapons.BurstRemaining[stateIndex] <= 0 ||
                    weapons.BurstTimer[stateIndex] > 0f)
                {
                    continue;
                }

                UnitId target =
                    weapons.BurstTarget[stateIndex];

                if (!units.IsAlive(target))
                {
                    weapons.StartBurst(
                        unit,
                        weaponSlot,
                        default,
                        0,
                        0f);
                    continue;
                }

                TryFireAt(
                    units,
                    inventory,
                    weapons,
                    projectiles,
                    units.GetId(unit),
                    weaponSlot,
                    target);
            }
        }
    }

    public bool TryFire(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        ProjectileStore projectiles,
        UnitId shooter,
        UnitWeaponSlot slot,
        Vector3 direction,
        float accuracyMultiplier = 1f)
    {
        if (!units.TryGetIndex(
                shooter,
                out int unitIndex))
        {
            return false;
        }

        short inventorySlot =
            inventory.GetWeaponEquipment(
                unitIndex,
                slot);

        if (inventorySlot < 0)
            return false;

        RangedWeaponConfig? weapon =
            inventory.GetItem(
                unitIndex,
                inventorySlot)
            as RangedWeaponConfig;

        if (weapon == null ||
            weapon.DefaultAmmunition == null ||
            weapon.MagazineSize <= 0)
        {
            return false;
        }

        int stateIndex =
            UnitWeaponStore.GetIndex(
                unitIndex,
                slot);

        if (weapons.Cooldown[stateIndex] > 0f ||
            weapons.Reloading[stateIndex] ||
            weapons.Ammo[stateIndex] <= 0)
        {
            return false;
        }

        AmmunitionConfig ammo =
            weapon.DefaultAmmunition;

        float spread =
            weapon.GetCurrentSpread() *
            MathF.Max(
                1f,
                accuracyMultiplier);

        Vector3 shotDirection =
            ApplySpread(
                direction,
                spread,
                weapons.NextRandom(
                    unitIndex,
                    slot),
                weapons.NextRandom(
                    unitIndex,
                    slot));

        float velocity =
            MathF.Max(
                0f,
                ammo.MuzzleVelocity);

        if (velocity <= 0f)
            return false;

        Vector3 muzzle =
            GetMuzzlePosition(
                units,
                unitIndex);

        float range =
            MathF.Max(
                1f,
                weapon.TotalEffectiveRange);

        float lifetime =
            MathF.Max(
                0.5f,
                range / velocity * 2f);

        projectiles.Create(
            shooter,
            units.FactionTag[unitIndex],
            muzzle,
            shotDirection,
            ammo.ProjectileMassKg,
            ammo.ProjectileDiameterM,
            ammo.MuzzleVelocity,
            ammo.DragCoefficient,
            ammo.Penetration,
            weapon.BaseDamage *
            ammo.DamageMultiplier,
            lifetime,
            ammo.DamageType,
            ammo.SuppressionFactor *
            weapon.SuppressionFactor,
            weapon.BleedChance);

        weapons.Ammo[stateIndex]--;
        weapons.Cooldown[stateIndex] =
            MathF.Max(
                0f,
                weapon.FireRate);

        return true;
    }

    public bool TryFireAt(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        ProjectileStore projectiles,
        UnitId shooter,
        UnitWeaponSlot slot,
        UnitId target,
        float accuracyMultiplier = 1f)
    {
        if (!units.TryGetIndex(
                shooter,
                out int shooterIndex) ||
            !units.TryGetIndex(
                target,
                out int targetIndex))
        {
            return false;
        }

        int stateIndex =
            UnitWeaponStore.GetIndex(
                shooterIndex,
                slot);

        RangedWeaponConfig? weapon =
            GetRangedWeapon(
                inventory,
                shooterIndex,
                slot);

        if (weapon == null)
            return false;

        bool continuingBurst =
            weapon.DefaultFireMode == FireMode.Burst &&
            weapons.BurstRemaining[stateIndex] > 0;

        if (continuingBurst)
        {
            UnitId burstTarget =
                weapons.BurstTarget[stateIndex];

            if (!units.IsAlive(burstTarget))
                return false;

            target =
                burstTarget;

            targetIndex =
                target.Index;
        }

        int previousBurstRemaining =
            weapons.BurstRemaining[stateIndex];

        bool fired =
            TryFireAtInternal(
                units,
                inventory,
                weapons,
                projectiles,
                shooter,
                slot,
                target,
                accuracyMultiplier);

        if (!fired)
        {
            if (continuingBurst)
            {
                weapons.StartBurst(
                    shooterIndex,
                    slot,
                    default,
                    0,
                    0f);
            }

            return false;
        }

        if (weapon.DefaultFireMode == FireMode.Burst)
        {
            int remaining =
                continuingBurst
                    ? previousBurstRemaining - 1
                    : Math.Max(
                        0,
                        weapon.BurstCount - 1);

            weapons.StartBurst(
                shooterIndex,
                slot,
                remaining > 0
                    ? target
                    : default,
                remaining,
                remaining > 0
                    ? MathF.Max(
                        weapon.BurstInterval,
                        weapon.FireRate)
                    : 0f);
        }

        Console.WriteLine(
            $"[SHOT] {shooter} faction={units.FactionTag[shooterIndex]} " +
            $"target={target} mode={weapon.DefaultFireMode} " +
            $"targetPos={units.Position[targetIndex]} " +
            $"ammo={weapon.DefaultAmmunition!.Name}");

        return true;
    }

    private bool TryFireAtInternal(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        ProjectileStore projectiles,
        UnitId shooter,
        UnitWeaponSlot slot,
        UnitId target,
        float accuracyMultiplier)
    {
        if (!units.TryGetIndex(
                shooter,
                out int shooterIndex) ||
            !units.TryGetIndex(
                target,
                out int targetIndex))
        {
            return false;
        }

        RangedWeaponConfig? weapon =
            GetRangedWeapon(
                inventory,
                shooterIndex,
                slot);

        if (weapon?.DefaultAmmunition == null)
            return false;

        Vector3 muzzle =
            GetMuzzlePosition(
                units,
                shooterIndex);

        Vector3 targetPoint =
            units.Position[targetIndex] +
            new Vector3(
                0f,
                0f,
                MathF.Max(
                    0.05f,
                    units.Height[targetIndex] *
                    0.55f));

        Vector3 horizontalTargetDelta =
            targetPoint - muzzle;

        float targetDistance =
            MathF.Sqrt(
                horizontalTargetDelta.X *
                horizontalTargetDelta.X +
                horizontalTargetDelta.Y *
                horizontalTargetDelta.Y);

        if (targetDistance >
            weapon.TotalEffectiveRange)
        {
            return false;
        }

        AmmunitionConfig ammo =
            weapon.DefaultAmmunition;

        if (!ProjectileBallistics.TrySolveDirection(
                muzzle,
                targetPoint,
                ammo.ProjectileMassKg,
                ammo.ProjectileDiameterM,
                ammo.MuzzleVelocity,
                ammo.DragCoefficient,
                out Vector3 direction))
        {
            direction =
                targetPoint -
                muzzle;
        }

        return TryFire(
            units,
            inventory,
            weapons,
            projectiles,
            shooter,
            slot,
            direction,
            accuracyMultiplier);
    }

    private static RangedWeaponConfig? GetRangedWeapon(
        UnitInventoryStore inventory,
        int unitIndex,
        UnitWeaponSlot slot)
    {
        short inventorySlot =
            inventory.GetWeaponEquipment(
                unitIndex,
                slot);

        if (inventorySlot < 0)
            return null;

        return inventory.GetItem(
                unitIndex,
                inventorySlot)
            as RangedWeaponConfig;
    }

    private static Vector3 GetMuzzlePosition(
        UnitStore units,
        int unitIndex)
    {
        Vector3 head =
            NormalizeHorizontal(
                units.HeadNormal[unitIndex]);

        float width =
            MathF.Max(
                0.05f,
                units.Width[unitIndex]);

        float length =
            MathF.Max(
                0.05f,
                units.Length[unitIndex]);

        float height =
            MathF.Max(
                0.05f,
                units.Height[unitIndex]);

        float bodyLength =
            units.Posture[unitIndex] ==
            UnitPosture.Lying
                ? length
                : length * 0.60f;

        float headRadius =
            MathF.Min(width, length) *
            0.32f;

        float headDistance =
            bodyLength * 0.5f +
            headRadius * 1.15f;

        return
            units.Position[unitIndex] +
            head *
            (headDistance + 0.15f) +
            new Vector3(
                0f,
                0f,
                units.Posture[unitIndex] ==
                UnitPosture.Lying
                    ? height * 0.55f
                    : height);
    }

    private static Vector3 ApplySpread(
        Vector3 direction,
        float spread,
        uint randomA,
        uint randomB)
    {
        direction = Normalize(direction);

        if (spread <= 0f)
            return direction;

        Vector3 right =
            new Vector3(
                -direction.Y,
                direction.X,
                0f);

        right =
            right.LengthSquared() <
            0.000001f
                ? new Vector3(1f, 0f, 0f)
                : Normalize(right);

        Vector3 up =
            Normalize(
                Vector3.Cross(
                    right,
                    direction));

        float radius =
            MathF.Sqrt(
                ToUnitFloat(randomA)) *
            spread;

        float angle =
            ToUnitFloat(randomB) *
            MathF.Tau;

        Vector3 offset =
            right *
            (MathF.Cos(angle) * radius) +
            up *
            (MathF.Sin(angle) * radius);

        return Normalize(
            direction + offset);
    }

    private static float ToUnitFloat(
        uint value)
    {
        return
            (value & 0x00FFFFFFu) /
            16777215f;
    }

    private static Vector3 NormalizeHorizontal(
        Vector3 value)
    {
        value.Z = 0f;
        return Normalize(value);
    }

    private static Vector3 Normalize(
        Vector3 value)
    {
        float lengthSquared =
            value.LengthSquared();

        if (lengthSquared < 0.000001f)
        {
            return new Vector3(
                1f,
                0f,
                0f);
        }

        return value /
            MathF.Sqrt(
                lengthSquared);
    }
}
