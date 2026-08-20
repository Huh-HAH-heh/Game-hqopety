using Core.Items;
using System;

namespace Core.Unit.Systems
{
    public sealed class UnitInventorySystem
    {
        /// <summary>
        /// ШАГ 14: Полностью пересчитывает массу юнита на основе надетого снаряжения.
        /// Исключает баги накопления веса, собирая сумму заново.
        /// </summary>
        public void RecalculateUnitMass(UnitStore units, int unitId)
        {
            if (unitId < 0 || unitId >= units.Count) return;

            // Стартуем с базового веса голого тела муравья/жука
            float totalMass = units.BaseBodyMass[unitId];

            // Прибавляем вес оружия, если слот не пуст
            if (units.WeaponSlot[unitId] != null)
            {
                // Извлекаем массу автомата/ножа из BaseStats вашего нового класса Weapon
                totalMass += units.WeaponSlot[unitId].BaseStats.Weight;
            }


            // Прибавляем вес брони из каждого анатомического слота экипировки
            if (units.HeadArmorSlot[unitId] != null) totalMass += units.HeadArmorSlot[unitId].Weight;
            if (units.TorsoArmorSlot[unitId] != null) totalMass += units.TorsoArmorSlot[unitId].Weight;
            if (units.ArmsArmorSlot[unitId] != null) totalMass += units.ArmsArmorSlot[unitId].Weight;
            if (units.LegsArmorSlot[unitId] != null) totalMass += units.LegsArmorSlot[unitId].Weight;

            // Записываем финальный честный вес в динамический массив для физики движения
            units.DynamicMass[unitId] = totalMass;
        }

        /// <summary>
        /// Дать муравью оружие (ближнее или дальнее).
        /// </summary>
        /// <summary>
        /// Выдает муравью/человеку новое оружие на основе чертежа WeaponConfig, 
        /// автоматически создавая динамический экземпляр класса Weapon.
        /// </summary>
        /// <summary>
        /// Выдает муравью/человеку новое оружие на основе чертежа WeaponConfig, 
        /// правильно распределяя параметры для дальнего и ближнего боя.
        /// </summary>
        /// <summary>
        /// Выдает муравью/человеку новое оружие на основе чертежа WeaponConfig, 
        /// правильно распределяя параметры для дальнего и ближнего боя, включая тепловой баланс.
        /// </summary>
        public void EquipWeapon(UnitStore units, int unitId, WeaponConfig weaponConfig)
        {
            if (unitId < 0 || unitId >= units.Count) return;

            if (weaponConfig == null)
            {
                units.WeaponSlot[unitId] = null;
            }
            else
            {
                // По умолчанию параметры ближнего боя (нож / укус)
                float finalRange = 1.5f;
                float finalMaxRange = 2.0f;
                float finalAccuracy = 0.05f;
                float recoilPerShot = 0.0f;
                float recoilRecovery = 1.0f;

                // Параметры тепла по умолчанию (для холодного оружия перегрев равен 0, оно не греется)
                float heatPerShot = 0f;
                float heatCoolingRate = 100f;
                float maxHeatThreshold = 9999f;

                // Если это огнестрел (АК-47 / СВД) — извлекаем баллистику и настраиваем термодинамику ствола
                if (weaponConfig is RangedWeaponConfig ranged)
                {
                    finalRange = ranged.BaseEffectiveRange;
                    finalMaxRange = ranged.BaseEffectiveRange * 1.5f;
                    finalAccuracy = ranged.BaseAccuracy;
                    recoilPerShot = 0.015f;     // Шаг отдачи при зажиме очереди
                    recoilRecovery = 0.25f;     // Скорость возврата прицела в идеал

                    // НАСТРОЙКА ТЕРМОДИНАМИКИ СТВОЛА (RimWorld-стиль)
                    if (ranged.Name.Contains("АК") || ranged.Name.Contains("Автомат"))
                    {
                        heatPerShot = 12f;         // Автомат греется на 12 единиц за пулю
                        heatCoolingRate = 30f;     // Остывает на 30 единиц в секунду в паузах
                        maxHeatThreshold = 80f;    // На 7-й пуле непрерывного Full-Auto зажима словит Клин!
                    }
                    else if (ranged.Name.Contains("СВД") || ranged.Name.Contains("Винтовка"))
                    {
                        heatPerShot = 25f;         // Мощный патрон СВД выделяет много тепла (25)
                        heatCoolingRate = 40f;     // Но за счет длинного ствола остывает быстрее (40)
                        maxHeatThreshold = 60f;    // Если стрелять без остановки, заклинит на 3-й пуле
                    }
                }

                // Собираем и кладем готовую пушку с честным паспортом тепла в ECS-слот пешки
                units.WeaponSlot[unitId] = new Weapon
                {
                    BaseStats = new BaseWeaponStats
                    {
                        Name = weaponConfig.Name,
                        BaseRange = finalRange,
                        MaxRange = finalMaxRange,
                        BaseAccuracy = finalAccuracy,
                        RecoilPerShot = recoilPerShot,
                        RecoilRecovery = recoilRecovery,
                        BaseDamage = weaponConfig.BaseDamage,
                        FireRate = weaponConfig.FireRate,
                        BleedChance = weaponConfig.BleedChance,
                        Weight = weaponConfig.Weight,

                        // ИСПРАВЛЕНО: Теперь пушка официально получает свои лимиты тепла!
                        HeatPerShot = heatPerShot,
                        HeatCoolingRate = heatCoolingRate,
                        MaxHeatThreshold = maxHeatThreshold
                    }
                };
            }

            RecalculateUnitMass(units, unitId);
        }




        /// <summary>
        /// Надеть на муравья элемент брони в правильный анатомический слот.
        /// </summary>
        public void EquipArmor(UnitStore units, int unitId, ArmorConfig armor)
        {
            if (unitId < 0 || unitId >= units.Count || armor == null) return;

            // Распределяем по слотам на основе зоны защиты (0 = Голова, 1 = Торс, 2 = Руки, 3 = Ноги)
            switch (armor.ProtectedZone)
            {
                case 0: units.HeadArmorSlot[unitId] = armor; break;
                case 1: units.TorsoArmorSlot[unitId] = armor; break;
                case 2: units.ArmsArmorSlot[unitId] = armor; break;
                case 3: units.LegsArmorSlot[unitId] = armor; break;
            }

            RecalculateUnitMass(units, unitId);
        }

        /// <summary>
        /// Полностью снять броню с определенной анатомической зоны (0-3).
        /// </summary>
        public void UnequipArmorZone(UnitStore units, int unitId, int zone)
        {
            if (unitId < 0 || unitId >= units.Count) return;

            switch (zone)
            {
                case 0: units.HeadArmorSlot[unitId] = null; break;
                case 1: units.TorsoArmorSlot[unitId] = null; break;
                case 2: units.ArmsArmorSlot[unitId] = null; break;
                case 3: units.LegsArmorSlot[unitId] = null; break;
            }

            RecalculateUnitMass(units, unitId);
        }
    }
}
