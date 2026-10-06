using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Items
{
    
[Flags]
public enum ArmorCoverage : ushort
{
    None = 0,
    Head = 1 << 0,
    Torso = 1 << 1,
    LeftArm = 1 << 2,
    RightArm = 1 << 3,
    LeftHand = 1 << 4,
    RightHand = 1 << 5,
    LeftLeg = 1 << 6,
    RightLeg = 1 << 7,
    LeftFoot = 1 << 8,
    RightFoot = 1 << 9
}

public enum ArmorLayer : byte
{
    Base = 0,
    Clothing = 1,
    Armor = 2,
    Outer = 3
}

[Flags]
public enum WeaponCapabilities : byte
{
    None = 0,
    Ranged = 1 << 0,
    Melee = 1 << 1
}

public enum UnitWeaponSlot : byte
{
    Primary = 0,
    Secondary = 1,
    Melee = 2,
    Utility = 3
}

    public abstract class ItemConfig
    {
        public string Name;
        public float Weight; // Масса предмета (пойдет в DynamicMass)
    }

    public sealed class ArmorConfig : ItemConfig
    {
        // Legacy zone is kept for compatibility with the archived system.
        public int ProtectedZone;

        // Fraction of incoming damage removed by this item.
        public float DamageAbsorption;

        // Fraction of the absorbed damage that damages the armor itself.
        public float ArmorDamageCoefficient = 0.25f;

        public float BleedProtectionChance;

        // Energy loss when a projectile crosses this armor layer.
        public float EnergyLoss;

        // Projectile penetration budget consumed by this layer.
        public float PenetrationResistance;

        public ArmorCoverage Coverage;

        public ArmorLayer Layer;
    }

    public enum WeaponType { Melee, Ranged }

    public abstract class WeaponConfig : ItemConfig
    {
        public WeaponType Type;

        // Ranged + Melee supports hybrid weapons.
        public WeaponCapabilities Capabilities;

        public byte BaseDamage;
        public float BleedChance;
        public float FireRate; // Задержка между атаками в секундах
        public byte MagazineSize;
        public float ReloadTime;
    }

    public sealed class MeleeWeaponConfig : WeaponConfig
    {
        public MeleeWeaponConfig()
        {
            Type = WeaponType.Melee;
            Capabilities = WeaponCapabilities.Melee;
        }
    }

    // ========================================================
    // ОБНОВЛЕННЫЙ КЛАСС ДЛЯ ДАЛЬНОБОЙНОГО ОРУЖИЯ
    // ========================================================
    public sealed class RangedWeaponConfig : WeaponConfig
    {
        public RangedWeaponConfig()
        {
            Type = WeaponType.Ranged;
            Capabilities = WeaponCapabilities.Ranged;
        }

        // Идеальная дистанция одиночными (АК = 100, СВД = 300)
        // Мы переименовали MaxRange в BaseEffectiveRange, чтобы уйти от логики "исчезновения пули"
        public float BaseEffectiveRange;

        // Базовый врожденный разброс оружия (чем меньше, тем точнее, например, АК = 0.02f)
        public float BaseAccuracy;

        // Default cartridge used when this weapon fires.
        public AmmunitionConfig? DefaultAmmunition;

        // Список установленных на пушку модификаций (прицелы, глушители)
        // Чтобы код не падал, сразу инициализируем пустой список
        public List<Attachment> InstalledAttachments = new List<Attachment>();

        /// <summary>
        /// Динамический расчет эффективной дальности с учетом модификаций
        /// </summary>
        public float TotalEffectiveRange
        {
            get
            {
                float range = BaseEffectiveRange;
                if (InstalledAttachments != null)
                {
                    foreach (var mod in InstalledAttachments)
                    {
                        range = mod.ApplyRangeModifier(range);
                    }
                }
                return range;
            }
        }

        /// <summary>
        /// Динамический расчет базового разброса с учетом модификаций
        /// </summary>
        public float GetCurrentSpread()
        {
            float accuracy = BaseAccuracy;
            if (InstalledAttachments != null)
            {
                foreach (var mod in InstalledAttachments)
                {
                    accuracy = mod.ApplyAccuracyModifier(accuracy);
                }
            }
            return accuracy;
        }
    }
}
