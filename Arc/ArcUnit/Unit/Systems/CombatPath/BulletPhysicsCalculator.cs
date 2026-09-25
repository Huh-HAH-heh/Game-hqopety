using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Unit.Systems.CombatPath
{
    public class BulletPhysicsCalculator
    {
        // Расчет финального разброса на любой дистанции (в тайлах/метрах)
        public static float CalculateSpreadAtDistance(float distance, float effectiveRange, float baseAccuracy)
        {
            // В пределах эффективной дальности (до 100м у АК) точность идеальна
            if (distance <= effectiveRange)
            {
                return baseAccuracy;
            }

            // За пределами эффективной дальности разброс растет экспоненциально
            float overDistance = distance - effectiveRange;

            // Каждые 10 метров сверх нормы увеличивают разброс в геометрической прогрессии
            float penaltyMultiplier = 1.0f + MathF.Pow(overDistance * 0.15f, 2);

            return baseAccuracy * penaltyMultiplier;
        }

        // Расчет падения урона на излёте снаряда
        public static float CalculateDamageAtDistance(float distance, float effectiveRange, float baseDamage)
        {
            if (distance <= effectiveRange) return baseDamage;

            float overDistance = distance - effectiveRange;

            // Урон плавно падает. Например, на каждые 10 метров излёта теряется 3% урона
            float damageDrop = overDistance * 0.03f;
            float finalDamage = baseDamage - damageDrop;

            // Пуля не может наносить отрицательный урон, минимум 5% от базового (синяк/царапина)
            return MathF.Max(finalDamage, baseDamage * 0.05f);
        }
    }

}
