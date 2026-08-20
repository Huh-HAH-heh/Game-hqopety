using Core.Items;
using Core.Map;
using Core.Structs;
using System;

namespace Core.Unit.Systems.CombatPath;

public static class CombatMath
{
    /// <summary>
    /// Вычисляет итоговый шанс попадания с учетом дальности, обвесов оружия, зажима (отдачи) и укрытий.
    /// </summary>
    // ПРАВКА СИГНАТУРЫ: добавили units и attackerId, чтобы знать, из чего именно стреляют
    public static float CalculateHitChance(
        WorldMap map,
        EdificeStore edifices,
        UnitStore units,
        int attackerId,
        int srcX, int srcY,
        int dstX, int dstY,
        int z)
    {
        // 1. БАЗОВАЯ ТОЧНОСТЬ СТРЕЛКА (например, 95% базовый навык контроля)
        float shooterSkill = 0.95f;

        // Считаем расстояние между стрелком и целью в микро-ячейках
        float dx = dstX - srcX;
        float dy = dstY - srcY;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        if (distance <= 0f) distance = 0.1f;

        // Дефолтные параметры для безбилетников (укус/кулак)
        float effectiveRange = 2f;
        float baseAccuracy = 0.05f;

        // 2. ПРАВКА ЛОГИКИ: Извлекаем параметры оружия, обвесов и отдачи из ECS-хранилища
        // Проверяем, экипирован ли ваш новый динамический класс Weapon
        if (units.WeaponSlot[attackerId] is Weapon activeWeapon)
        {
            // Берем дальность, которую обвесы (Scope/Silencer) уже модифицировали динамически
            effectiveRange = activeWeapon.TotalEffectiveRange;

            // Запрашиваем текущий разброс оружия С УЧЕТОМ обвесов И накопленной отдачи (currentRecoil) от очереди!
            baseAccuracy = activeWeapon.GetCurrentSpread();
        }

        // Вычисляем финальный угловой разброс по формуле экспоненциального штрафа из вашего BulletPhysicsCalculator
        float finalSpreadAngle = BulletPhysicsCalculator.CalculateSpreadAtDistance(distance, effectiveRange, baseAccuracy);

        // Переводим угловой разброс в линейный радиус промаха пули у силуэта цели
        float spreadRadiusCells = distance * finalSpreadAngle;

        // Шанс попасть обратно пропорционален радиусу разброса пули у цели (Размер цели / (Размер цели + Разброс))
        float currentChance = (1.0f / (1.0f + spreadRadiusCells)) * shooterSkill;

        // ========================================================
        // 3. УЧЕТ УКРЫТИЯ (Ваш защищенный код без изменений)
        // ========================================================
        MapLayer layer = map.GetLayer(z);
        if (layer != null)
        {
            float bestCoverProtection = 0f;
            int maxCoord = (16 * 48) - 1;

            for (int dyOffset = -1; dyOffset <= 1; dyOffset++)
            {
                for (int dxOffset = -1; dxOffset <= 1; dxOffset++)
                {
                    if (dxOffset == 0 && dyOffset == 0) continue;

                    int checkX = dstX + dxOffset;
                    int checkY = dstY + dyOffset;

                    if (checkX < 0 || checkX > maxCoord || checkY < 0 || checkY > maxCoord)
                        continue;

                    float distToShooterFromCover = GetDistance(srcX, srcY, checkX, checkY);
                    if (distToShooterFromCover < distance)
                    {
                        ref MicroCell cell = ref layer.GetMicroCell(checkX, checkY);

                        if (cell.EdificeId > 0)
                        {
                            var instance = edifices.Instances[cell.EdificeId];

                            if (edifices.Configs[instance.ConfigId] != null)
                            {
                                var config = edifices.Configs[instance.ConfigId];

                                if (config.CoverEffectiveness > bestCoverProtection)
                                {
                                    bestCoverProtection = config.CoverEffectiveness;
                                }
                            }
                        }
                    }
                }
            }

            currentChance *= (1.0f - bestCoverProtection);
        }

        // Ваш скорректированный лимит: гарантируем шанс от 1% до 95%
        return Math.Clamp(currentChance, 0.01f, 0.95f);
    }

    private static float GetDistance(int x1, int y1, int x2, int y2)
    {
        float dx = x2 - x1;
        float dy = y2 - y1;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
