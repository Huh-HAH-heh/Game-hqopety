using Core.Items;
using Core.Map;
using Core.Structs;
using System;

namespace Core.Unit.Systems
{
    public sealed class UnitDamageSystem
    {
        /// <summary>
        /// ШАГ 15: Наносит урон конкретному муравью с распределением по зонам тела и учетом брони.
        /// Генерирует брызги крови на манер RimWorld, если рана вызвала кровотечение.
        /// </summary>
        /// <param name="units">Хранилище данных всех юнитов</param>
        /// <param name="targetId">ID муравья-цели</param>
        /// <param name="rawDamage">Базовый урон оружия</param>
        /// <param name="bleedChance">Шанс вызвать кровотечение (от 0.0f до 1.0f)</param>
        /// <param name="map">Глобальная карта мира для разметки брызг крови</param>
               // ИСПРАВЛЕНО: В сигнатуру метода добавлен обязательный параметр int attackerId!
        public void ApplyDamage(UnitStore units, int targetId, int attackerId, byte rawDamage, float bleedChance, WorldMap map)
        {
            // Базовая валидация: проверяем границы массива и жив ли муравей
            if (targetId < 0 || targetId >= units.Count || units.HealthMasks[targetId] == 0)
                return;

            // 1. Выбираем случайную зону попадания (RimWorld-пропорции: голова — редко, торс — часто)
            int roll = Random.Shared.Next(100);
            int targetZone = roll switch
            {
                < 10 => 0, // 10% шанс попасть в Голову
                < 60 => 1, // 50% шанс попасть в Торс
                < 80 => 2, // 20% шанс попасть в Руки/Передние лапки
                _ => 3  // 20% шанс попасть в Ноги/Задние лапки
            };

            // 2. Ищем, надета ли броня на эту зону тела у цели через массивы UnitStore
            ArmorConfig equippedArmor = targetZone switch
            {
                0 => units.HeadArmorSlot[targetId],
                1 => units.TorsoArmorSlot[targetId],
                2 => units.ArmsArmorSlot[targetId],
                _ => units.LegsArmorSlot[targetId]
            };

            float finalDamage = rawDamage;
            float finalBleedChance = bleedChance;

            // 3. ТАНКОВАНИЕ БРОНЕЙ: Если слот не пуст, снижаем урон и защищаем от ран
            if (equippedArmor != null)
            {
                // Уменьшаем урон на процент поглощения брони (например, хитиновый панцирь гасит 40% урона)
                finalDamage *= (1f - equippedArmor.DamageAbsorption);

                // Проверяем, смогла ли броня удержать рану и предотвратить кровотечение
                if (Random.Shared.NextSingle() <= equippedArmor.BleedProtectionChance)
                {
                    finalBleedChance = 0f;
                }
            }

            // Безопасное округление и каст дробного float-урона в byte без вылета за границы 0..255
            byte damageToApply = (byte)Math.Clamp(finalDamage, 0, 255);

            // Фиксируем старую скорость кровотечения муравья перед ударом
            float oldBleedRate = units.BleedRates[targetId];

            // 4. Применяем чистый остаток урона напрямую в биты маски здоровья
            HealthSystem.TakeDamage(
                ref units.HealthMasks[targetId],
                ref units.BleedRates[targetId],
                targetZone,
                damageToApply,
                finalBleedChance
            );

            // ========================================================
            // ИСПРАВЛЕНО (Такт 4): ТРИГГЕР БОЛИ И ПОИСК АГРЕССОРА В ЦП
            // ========================================================
            // Если атаковал реальный враг, и раненый юнит выжил после удара брони,
            // записываем ID обидчика в регистр боли. ЦП-мозг перехватит его на следующем кадре!
            if (attackerId >= 0 && attackerId < units.Count && attackerId != targetId && units.HealthMasks[targetId] > 0)
            {
                units.LastAttackerIds[targetId] = attackerId;
            }

            // 5. РАЗМЕТКА БРЫЗГ КРОВИ (RimWorld-стиль)
            // Если скорость кровотечения выросла — рана открылась, пускаем кровь на карту!
            if (units.BleedRates[targetId] > oldBleedRate && map != null)
            {
                ref var pos = ref units.Positions[targetId];
                MapLayer layer = map.GetLayer(pos.Z);

                if (layer != null)
                {
                    // Пачкаем кровью ту микро-ячейку, в которой прямо сейчас стоит раненый муравей
                    ref MicroCell currentCell = ref layer.GetMicroCell(pos.X, pos.Y);

                    // Взводим Бит 1 (0x0002) — "Грязь/Кровь" в поле Flags
                    currentCell.Flags |= 0x0002;

                    // Если удар был по-настоящему тяжелым (урон больше 30), кровь брызгает во все стороны
                    if (damageToApply > 30)
                    {
                        // Случайное смещение брызг в соседнюю ячейку (-1, 0, 1) с защитой от вылета за края (0..767)
                        int maxCoord = (16 * 48) - 1;
                        int bloodX = Math.Clamp(pos.X + Random.Shared.Next(-1, 2), 0, maxCoord);
                        int bloodY = Math.Clamp(pos.Y + Random.Shared.Next(-1, 2), 0, maxCoord);

                        ref MicroCell neighborCell = ref layer.GetMicroCell(bloodX, bloodY);
                        neighborCell.Flags |= 0x0002; // Пачкаем соседнюю клетку асфальта
                    }
                }
            }
        } // Конец метода ApplyDamage

    }
}
