using System;
using System.Numerics;
using Core.Items;

namespace Core.Unit;

public sealed class UnitWeaponSystem
{
    public void Update(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        float deltaTime)
    {
        weapons.UpdateTimers(deltaTime);

        ReadOnlySpan<int> active =
            units.ActiveIndices;

        for (int i = 0;
             i < active.Length;
             i++)
        {
            int unit =
                active[i];

            for (int slot = 0;
                 slot < UnitInventoryStore.WeaponSlotCount;
                 slot++)
            {
                UnitWeaponSlot weaponSlot =
                    (UnitWeaponSlot)slot;

                short inventorySlot =
                    inventory.GetWeaponEquipment(
                        unit,
                        weaponSlot);

                int stateIndex =
                    UnitWeaponStore.GetIndex(
                        unit,
                        weaponSlot);

                if (inventorySlot < 0)
                {
                    weapons.ClearSlot(
                        unit,
                        weaponSlot);
                    continue;
                }

                RangedWeaponConfig? ranged =
                    inventory.GetItem(
                        unit,
                        inventorySlot) as RangedWeaponConfig;

                if (ranged == null)
                    continue;

                if (weapons.Reloading[stateIndex] &&
                    weapons.ReloadTimer[stateIndex] <= 0f)
                {
                    weapons.FinishReload(
                        unit,
                        weaponSlot,
                        ranged.MagazineSize);
                }

                if (!weapons.Reloading[stateIndex] &&
                    weapons.Ammo[stateIndex] <= 0 &&
                    weapons.ReserveAmmo[stateIndex] > 0 &&
                    ranged.MagazineSize > 0)
                {
                    weapons.StartReload(
                        unit,
                        weaponSlot,
                        ranged.ReloadTime);
                }
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
        Vector3 direction)
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
                inventorySlot) as RangedWeaponConfig;

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

        Vector3 shotDirection =
            ApplySpread(
                direction,
                weapon.GetCurrentSpread(),
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

        float lifetime =
            MathF.Max(
                0.5f,
                weapon.BaseEffectiveRange /
                velocity *
                2f);

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
            lifetime);

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
        UnitId target)
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

        short inventorySlot =
            inventory.GetWeaponEquipment(
                shooterIndex,
                slot);

        if (inventorySlot < 0)
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
                units.Height[targetIndex] *
                0.55f);

        RangedWeaponConfig? weapon =
            inventory.GetItem(
                shooterIndex,
                inventorySlot) as RangedWeaponConfig;

        if (weapon == null ||
            weapon.DefaultAmmunition == null)
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
                out Vector3 ballisticDirection))
        {
            ballisticDirection =
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
            ballisticDirection);
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
            MathF.Min(
                width,
                length) *
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
        direction =
            Normalize(
                direction);

        if (spread <= 0f)
            return direction;

        Vector3 right =
            new Vector3(
                -direction.Y,
                direction.X,
                0f);

        if (right.LengthSquared() <
            0.000001f)
        {
            right =
                new Vector3(
                    1f,
                    0f,
                    0f);
        }
        else
        {
            right =
                Normalize(right);
        }

        Vector3 up =
            Normalize(
                Vector3.Cross(
                    right,
                    direction));

        float u =
            ToUnitFloat(
                randomA);

        float v =
            ToUnitFloat(
                randomB);

        float radius =
            MathF.Sqrt(u) *
            spread;

        float angle =
            v *
            MathF.Tau;

        Vector3 offset =
            right *
            (MathF.Cos(angle) *
             radius) +
            up *
            (MathF.Sin(angle) *
             radius);

        return Normalize(
            direction +
            offset);
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
