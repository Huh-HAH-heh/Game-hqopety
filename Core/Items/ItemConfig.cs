using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Items
{
    public abstract class ItemConfig
    {
        public string Name;
        public float Weight; // Масса предмета (пойдет в DynamicMass)
    }

    public sealed class ArmorConfig : ItemConfig
    {
        public int ProtectedZone;
        public float DamageAbsorption;
        public float BleedProtectionChance;
    }

    public enum WeaponType { Melee, Ranged }

    public abstract class WeaponConfig : ItemConfig
    {
        public WeaponType Type;
        public byte BaseDamage;
        public float BleedChance;
        public float FireRate; // Задержка между атаками в секундах
    }

    public sealed class MeleeWeaponConfig : WeaponConfig
    {
        public MeleeWeaponConfig() => Type = WeaponType.Melee;
    }

    // ========================================================
    // ОБНОВЛЕННЫЙ КЛАСС ДЛЯ ДАЛЬНОБОЙНОГО ОРУЖИЯ
    // ========================================================
    public sealed class RangedWeaponConfig : WeaponConfig
    {
        public RangedWeaponConfig() => Type = WeaponType.Ranged;

        // Идеальная дистанция одиночными (АК = 100, СВД = 300)
        // Мы переименовали MaxRange в BaseEffectiveRange, чтобы уйти от логики "исчезновения пули"
        public float BaseEffectiveRange;

        // Базовый врожденный разброс оружия (чем меньше, тем точнее, например, АК = 0.02f)
        public float BaseAccuracy;

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
