using System;

namespace Core.Unit.Systems
{
    public static class HealthSystem
    {
        // Битовые сдвиги для зон (ваша схема)
        private const int HeadShift = 24;
        private const int TorsoShift = 16;
        private const int ArmsShift = 8;
        private const int LegsShift = 0;

        /// <summary>
        /// ШАГ 5 и 11: Нанесение урона конкретной части тела.
        /// </summary>
        public static void TakeDamage(ref uint mask, ref float bleed, int zone, byte damage, float bleedChance)
        {
            int shift = zone switch
            {
                0 => HeadShift,   // Голова
                1 => TorsoShift,  // Торс
                2 => ArmsShift,   // Руки
                _ => LegsShift    // Ноги
            };

            // Извлекаем текущее ХП зоны (0-255)
            uint currentZoneHp = (mask >> shift) & 0xFF;

            // Вычитаем урон с защитой от ухода в минус
            currentZoneHp = currentZoneHp > damage ? currentZoneHp - damage : 0;

            // Очищаем старое значение зоны в маске и записываем новое
            mask = (mask & ~(0xFFU << shift)) | (currentZoneHp << shift);

            // Механика RimWorld: Кровотечение зависит от тяжести урона
            if (currentZoneHp < 255 && bleedChance > 0f)
            {
                // Каждая рана увеличивает скорость кровотечения
                bleed += (damage * 0.005f);
            }
        }

        /// <summary>
        /// Ежесекундное обновление здоровья: симулирует кровопотерю и заживление.
        /// Вызывается внутри цикла UnitStore для каждого живого муравья.
        /// </summary>
        public static void UpdateTick(ref uint mask, ref float bleed, ref float bloodLoss, float deltaTime)
        {
            // Если муравей уже мертв — ничего не делаем
            if (mask == 0) return;

            // 1. СИМУЛЯЦИЯ КРОВОТЕЧЕНИЯ
            if (bleed > 0f)
            {
                // Накапливаем общую потерю крови (bloodLoss идет от 0.0f до 1.0f)
                bloodLoss += bleed * deltaTime;

                // Естественное свертывание крови (в RimWorld раны со временем затягиваются сами, если они не критические)
                bleed -= 0.02f * deltaTime;
                if (bleed < 0f) bleed = 0f;
            }
            else
            {
                // Если кровотечения нет, организм муравья медленно восстанавливает кровь
                if (bloodLoss > 0f)
                {
                    bloodLoss -= 0.01f * deltaTime;
                    if (bloodLoss < 0f) bloodLoss = 0f;
                }
            }

            // 2. ПРОВЕРКА КРИТИЧЕСКИХ ФАКТОРОВ СМЕРТИ (Смерть от кровопотери)
            if (bloodLoss >= 1.0f || !IsAlive(mask))
            {
                // Полностью зануляем маску здоровья. В нашей ECS-архитектуре mask == 0 означает труп.
                mask = 0;
                bleed = 0f;
                bloodLoss = 0f;
                return;
            }

            // 3. ЕСТЕСТВЕННАЯ РЕГЕНЕРАЦИЯ (Заживление тканей)
            // Если муравей не истекает кровью, его раны медленно затягиваются
            if (bleed == 0f && bloodLoss < 0.2f)
            {
                // Раз в несколько тиков восстанавливаем по 1 ХП поврежденным зонам
                // Чтобы не усложнять, можно делать легкий побитовый инкремент для неполных зон
                mask = RegenerateZone(mask, LegsShift);
                mask = RegenerateZone(mask, ArmsShift);
                mask = RegenerateZone(mask, TorsoShift);
                mask = RegenerateZone(mask, HeadShift);
            }
        }

        private static uint RegenerateZone(uint mask, int shift)
        {
            uint hp = (mask >> shift) & 0xFF;
            // Если зона повреждена, но не уничтожена в ноль (в RimWorld уничтоженные части тела не регенерируют сами)
            if (hp > 0 && hp < 255)
            {
                // С шансом или фиксированно добавляем ХП (например, очень медленно)
                if (Random.Shared.Next(0, 1000) == 0) hp++;
                mask = (mask & ~(0xFFU << shift)) | (hp << shift);
            }
            return mask;
        }

        /// <summary>
        /// Проверка: живы ли жизненно важные органы?
        /// </summary>
        public static bool IsAlive(uint mask)
        {
            if (((mask >> HeadShift) & 0xFF) == 0) return false;  // Смерть от уничтожения мозга
            if (((mask >> TorsoShift) & 0xFF) == 0) return false; // Смерть от уничтожения сердца/легких
            return true;
        }

        /// <summary>
        /// Влияние здоровья на скорость (RimWorld-механика Consciousness / Подвижность).
        /// Скорость падает как от травм ног, так и от общей слабости из-за потери крови!
        /// </summary>
        public static float GetSpeedModifier(uint mask, float bloodLoss)
        {
            if (mask == 0) return 0f;

            // 1. Влияние физического состояния ног
            uint legsHp = mask & 0xFF;
            float legsFactor = (legsHp == 0) ? 0.15f : (0.4f + ((legsHp / 255f) * 0.6f));

            // 2. Влияние потери крови (Слабость / Сознание)
            // В RimWorld при сильной потере крови сознание падает, и пешка падает в шок (Downed)
            float consciousnessFactor = 1.0f - bloodLoss;
            if (consciousnessFactor < 0.1f) consciousnessFactor = 0.1f; // Полный паралич

            // Итоговый модификатор скорости — произведение физической силы ног и общего тонуса
            return legsFactor * consciousnessFactor;
        }
    }
}
