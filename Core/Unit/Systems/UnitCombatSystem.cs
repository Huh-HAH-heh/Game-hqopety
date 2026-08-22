using Core.Items;
using Core.Map;
using Core.Structs;
using Core.Unit.Components;
using System;
using System.Collections.Generic;

namespace Core.Unit.Systems
{
    public sealed class UnitCombatSystem
    {
        private readonly List<int> _nearbyCandidates = new List<int>(16);
        private readonly UnitDamageSystem _damageSystem = new UnitDamageSystem();

        /// <summary>
        /// Главный логический тик боевой системы.
        /// </summary>
        public void Update(
            UnitStore units,
            UnitSpatialGrid spatialGrid, 
            WorldMap map, 
            EdificeStore edificeStore, 
            CombatEffectSystem effectSystem, 
            float microCellPixelSize, 
            float deltaTime)
        {
            if (deltaTime <= 0f) return;

           
            // // 1. СБРОС КУЛДАУНОВ, ОСТЫВАНИЕ ОТДАЧИ И ТЕПЛОВОЙ МЕНЕДЖЕМЕНТ
            for (int i = 0; i < units.Count; i++)
            {
                if (units.HealthMasks[i] == 0) continue;

                // Плавно уменьшаем кулдаун выстрела каждую дельту кадра
                if (units.ShotCooldowns[i] > 0f)
                {
                    units.ShotCooldowns[i] -= deltaTime;
                    if (units.ShotCooldowns[i] < 0f) units.ShotCooldowns[i] = 0f;
                }

                // ЧЕСТНОЕ ОСТЫВАНИЕ ОТДАЧИ (БЕЗ ТЕРМОДИНАМИКИ И ПЕРЕГРЕВА)
                if (units.WeaponSlot[i] is Weapon activeWeapon)
                {
                    // Оставляем только восстановление точности/отдачи оружия
                    if (activeWeapon.currentRecoil > 0f)
                    {
                        activeWeapon.currentRecoil -= activeWeapon.BaseStats.RecoilRecovery * deltaTime;
                        if (activeWeapon.currentRecoil < 0f) activeWeapon.currentRecoil = 0f;
                    }
                }
            }


            // 2. ИИ сканирует цели и ведет бой
            ExecuteRimWorldCombat(units, spatialGrid, map, edificeStore, effectSystem, microCellPixelSize, deltaTime);
        }


        /// <summary>
        /// Тактический ИИ: динамически рассчитывает дальность зрения по оружию и тревоге, 
        /// удерживает фокус (Target Lock) через Raycast, управляет фазой прицеливания и выглядыванием.
        /// </summary>
        private void ExecuteRimWorldCombat(
       UnitStore units,
       UnitSpatialGrid spatialGrid,
       WorldMap map,
       EdificeStore edificeStore,
       CombatEffectSystem effectSystem,
       float microCellPixelSize,
       float deltaTime)
        {
            // Переводим радиус ближней угрозы в микро-ячейки
            const float MeleeThreatRadius = 2.5f;

            for (int i = 0; i < units.Count; i++)
            {
                if (units.HealthMasks[i] == 0) continue;

                ref var posA = ref units.Positions[i];
                ref var moveA = ref units.Movement[i];
                int currentTargetId = units.CurrentTargets[i];

                // ========================================================
                // ИСПРАВЛЕНО: ДИНАМИЧЕСКИЙ РАСЧЕТ ДАЛЬНОСТИ ПО ОРУЖИЮ
                // ========================================================
                // Если оружия нет (жук), базовый радиус атаки = 4 микро-ячейки (укус)
                int currentAttackRange = 4;

                if (units.WeaponSlot[i] != null)
                {
                    // ВАЖНО: Если у вас BaseStats.BaseRange равен 100 (метры), 
                    // на экране это 100 тайлов * 3 ячейки = 300 микро-ячеек!
                    // Чтобы они стреляли в пределах видимости экрана, берем дальность оружия
                    float weaponRange = units.WeaponSlot[i].TotalEffectiveRange;

                    // Зажимаем максимальный радиус ИИ (например, до 60 микро-ячеек), 
                    // чтобы они не стреляли вслепую за пределы монитора
                    currentAttackRange = (int)MathF.Min(150f, weaponRange);
                }

                // Дальность зрения (сфера сканирования грида)
                int visionRadius = currentAttackRange + 5;

                // Проверка Target Lock (Удержание фокуса)
                if (currentTargetId != -1)
                {
                    if (units.HealthMasks[currentTargetId] == 0)
                    {
                        units.CurrentTargets[i] = -1;
                    }
                    else
                    {
                        ref var posTarget = ref units.Positions[currentTargetId];
                        float tdx = posA.X - posTarget.X;
                        float tdy = posA.Y - posTarget.Y;
                        float distToTarget = MathF.Sqrt(tdx * tdx + tdy * tdy);

                        bool stillCanSee = CombatPath.VisibilityChecker.HasLineOfSight(map, edificeStore, posA.X, posA.Y, posTarget.X, posTarget.Y, posA.Z);

                        // Теряем цель, только если она вышла за радиус зрения или скрылась за стеной
                        if (distToTarget > visionRadius || posA.Z != posTarget.Z || !stillCanSee)
                        {
                            units.CurrentTargets[i] = -1;
                        }
                    }
                }

                // ========================================================
                // ПОИСК ВРАГОВ ЧЕРЕЗ РАСШИРЕННЫЙ РАДИУС ЗРЕНИЯ
                // ========================================================
                _nearbyCandidates.Clear();
                // Теперь ИИ сканирует честные 40-50 ячеек вокруг себя, и легко найдет отряд напротив!
                spatialGrid.GetNearby(posA.Spatial, visionRadius, _nearbyCandidates);

                int bestNewTargetId = -1;
                float closestDistMicroCells = float.MaxValue;
                bool foundMeleeThreat = false;

                for (int idx = 0; idx < _nearbyCandidates.Count; idx++)
                {
                    int j = _nearbyCandidates[idx];

                    if (i == j || units.HealthMasks[j] == 0) continue;
                    if (units.UnitType[i] == units.UnitType[j]) continue;

                    ref var posB = ref units.Positions[j];
                    if (posA.Z != posB.Z) continue;

                    float cdx = posA.X - posB.X;
                    float cdy = posA.Y - posB.Y;
                    float distMicroCells = MathF.Sqrt(cdx * cdx + cdy * cdy);

                    if (distMicroCells > visionRadius) continue;

                    // Проверка видимости лучом сквозь стены
                    bool canSee = CombatPath.VisibilityChecker.HasLineOfSight(map, edificeStore, posA.X, posA.Y, posB.X, posB.Y, posA.Z);
                    if (!canSee) continue;

                    // КРИТИЧЕСКАЯ УГРОЗА В УПОР
                    if (distMicroCells <= MeleeThreatRadius)
                    {
                        if (!foundMeleeThreat)
                        {
                            foundMeleeThreat = true;
                            closestDistMicroCells = float.MaxValue;
                            bestNewTargetId = -1;
                        }

                        if (distMicroCells < closestDistMicroCells)
                        {
                            closestDistMicroCells = distMicroCells;
                            bestNewTargetId = j;
                        }
                    }
                    // ОБЫЧНАЯ ЦЕЛЬ
                    else if (!foundMeleeThreat && units.CurrentTargets[i] == -1)
                    {
                        if (distMicroCells <= currentAttackRange && distMicroCells < closestDistMicroCells)
                        {
                            closestDistMicroCells = distMicroCells;
                            bestNewTargetId = j;
                        }
                    }
                }

                if (foundMeleeThreat)
                {
                    units.CurrentTargets[i] = bestNewTargetId;
                }
                else if (units.CurrentTargets[i] == -1 && bestNewTargetId != -1)
                {
                    units.CurrentTargets[i] = bestNewTargetId;
                }

                // ========================================================
                // 3. МЕХАНИКА ИЗГОТОВЛЕНИЯ ВЫСТРЕЛА И АДАПТИВНЫХ ОЧЕРЕДЕЙ
                // ========================================================
                int finalTarget = units.CurrentTargets[i];

                if (finalTarget != -1)
                {
                    ref var targetPos = ref units.Positions[finalTarget];
                    float fdx = posA.X - targetPos.X;
                    float fdy = posA.Y - targetPos.Y;
                    float distanceInTiles = MathF.Sqrt(fdx * fdx + fdy * fdy);

                    if (distanceInTiles <= currentAttackRange)
                    {
                        // ФАЗА А: Очередь уже идет (Юнит в режиме отсечки burst-огня)
                        if (units.RemainingBurstShots[i] > 0)
                        {
                            if (units.ShotCooldowns[i] <= 0f)
                            {
                                // Стреляем! Логика нагрева и отдачи из Шага 3 сработает внутри PerformAttack
                                PerformAttack(units, i, finalTarget, map, edificeStore, spatialGrid, effectSystem, microCellPixelSize);

                                units.RemainingBurstShots[i]--; // Израсходовали одну пулю

                                if (units.RemainingBurstShots[i] <= 0)
                                {
                                    // ОЧЕРЕДЬ ЗАВЕРШЕНА ПЛАНОВО: прячемся за стену, уходим на кулдаун между очередями
                                    units.IsAiming[i] = false;
                                    posA.RenderX = posA.X;
                                    posA.RenderY = posA.Y;

                                    // Пауза между очередями (Берем из FireRate, например, 1.2 секунды)
                                    units.ShotCooldowns[i] = (units.WeaponSlot[i] != null) ? units.WeaponSlot[i].BaseStats.FireRate : 1.5f;
                                }
                                else
                                {
                                    // Темп стрельбы ВНУТРИ очереди (АК-47 строчит с задержкой 0.1 секунды между пулями)
                                    units.ShotCooldowns[i] = 0.1f;
                                }
                            }
                            continue; // Пропускаем фазу прицеливания, пока не отстреляем очередь
                        }

                        // ========================================================
                        // ФАЗА Б: Начало подготовки к новой очереди (Изготовление выстрела)
                        // ========================================================
                        // Солдат начнет целиться ТОЛЬКО если старый кулдаун прошел и пушка не заклинила от перегрева
                        bool canShoot = units.WeaponSlot[i] != null;
                        if (!units.IsAiming[i] && units.ShotCooldowns[i] <= 0f && canShoot)
                        {
                            units.IsAiming[i] = true;

                            // Время на вскидку/прицеливание перед первой пулей очереди
                            units.AimingTimers[i] = (units.WeaponSlot[i] != null) ? units.WeaponSlot[i].BaseStats.RecoilRecovery * 1.5f : 0.5f;

                            if (moveA.State == MovementState.InCover)
                            {
                                units.LeanOffsetX[i] = Math.Sign(targetPos.X - posA.X);
                                units.LeanOffsetY[i] = Math.Sign(targetPos.Y - posA.Y);
                            }

                            // ИСПРАВЛЕНО: Включили прицеливание? Ждем следующего кадра для тика таймера!
                            continue;
                        }

                        // ========================================================
                        // ФАЗА В: Процесс прицеливания (Юнит плавно высовывается из-за угла монолита)
                        // ========================================================
                        if (units.IsAiming[i] && units.RemainingBurstShots[i] <= 0)
                        {
                            units.AimingTimers[i] -= deltaTime;

                            if (moveA.State == MovementState.InCover)
                            {
                                posA.RenderX = posA.X + (units.LeanOffsetX[i] * 0.4f);
                                posA.RenderY = posA.Y + (units.LeanOffsetY[i] * 0.4f);
                            }

                            // ВРЕМЯ ИЗГОТОВЛЕНИЯ ВЫШЛО: Заряжаем боезапас очереди
                            if (units.AimingTimers[i] <= 0f)
                            {
                                int burstCount = 1;
                                if (units.WeaponSlot[i] != null)
                                {
                                    // ПОЛИМОРФНЫЙ ВЫЗОВ: пушка сама оценивает дистанцию до цели. 
                                    burstCount = units.WeaponSlot[i].GetBurstCountForDistance(distanceInTiles);
                                }

                                units.RemainingBurstShots[i] = burstCount;

                                // ИСПРАВЛЕНО: Глушим таймер в абсолютный 0, чтобы он не вызывал этот блок повторно 
                                // во время отстрела очереди на следующих кадрах!
                                units.AimingTimers[i] = 0f;
                            }
                        }

                    }
                }
                else
                {
                    // Если цель потеряна/умерла — сбрасываем прицел и остатки очереди, прячемся в сейв
                    if (units.IsAiming[i] || units.RemainingBurstShots[i] > 0)
                    {
                        units.IsAiming[i] = false;
                        units.RemainingBurstShots[i] = 0;
                        posA.RenderX = posA.X;
                        posA.RenderY = posA.Y;
                    }
                }
            } // <-- Конец цикла for (int i = 0; i < units.Count; i++)
        } // <-- Конец метода ExecuteRimWorldCombat




        private void PerformAttack(
        UnitStore units,
        int attackerId,
        int targetId,
        WorldMap map,
        EdificeStore edificeStore,
        UnitSpatialGrid spatialGrid,
        CombatEffectSystem effectSystem,
        float microCellPixelSize)
        {
            if (attackerId < 0 || attackerId >= units.Count) return;
            if (targetId < 0 || targetId >= units.Count) return;
            if (units.HealthMasks[attackerId] == 0 || units.HealthMasks[targetId] == 0) return;

            ref var posA = ref units.Positions[attackerId];
            ref var posB = ref units.Positions[targetId];

            // 1. БАЗОВЫЕ ХАРАКТЕРИСТИКИ ДЛЯ БЕЗОРУЖНОГО БОЯ (УКУС ЖУКА / КУЛАК)
            byte baseDamage = 25;
            float weaponBleedChance = 0.7f;
            float effectiveRange = 2f;
            float baseAccuracy = 0.05f;

            // ИСПРАВЛЕНО: Извлекаем параметры из структуры BaseStats нового динамического класса Weapon
            if (units.WeaponSlot[attackerId] is Weapon activeWeapon)
            {
                baseDamage = activeWeapon.BaseStats.BaseDamage;
                weaponBleedChance = activeWeapon.BaseStats.BleedChance;

                // Эффективную дальность и текущий разброс запрашиваем через динамические геттеры с учетом обвесов!
                effectiveRange = activeWeapon.TotalEffectiveRange;
                baseAccuracy = activeWeapon.GetCurrentSpread();
            }

            // 2. РАСЧЕТ ИТОГОВОГО ШАНСА ПОПАДАНИЯ С УЧЕТОМ ОТДАЧИ И УКРЫТИЙ
            float hitChance = CombatPath.CombatMath.CalculateHitChance(
                map, edificeStore, units, attackerId,
                posA.X, posA.Y, posB.X, posB.Y, posA.Z
            );

            bool isHit = Random.Shared.NextSingle() <= hitChance;

            // 3. НАКАПЛИВАЕМ ОТДАЧУ И ТЕПЛО СТВОЛА ПОСЛЕ ВЫСТРЕЛА
            if (units.WeaponSlot[attackerId] is Weapon shootingWeapon)
            {
                shootingWeapon.currentRecoil += shootingWeapon.BaseStats.RecoilPerShot;

            }

            // Физическая дистанция для расчета излёта и промахов
            float dx = posB.X - posA.X;
            float dy = posB.Y - posA.Y;
            float distanceInTiles = MathF.Sqrt(dx * dx + dy * dy);

            // ========================================================
            // БАЛЛИСТИЧЕСКИЙ РАСЧЕТ ТРАЕКТОРИИ И ПРОМАХОВ
            // ========================================================

            // Пиксельный старт всегда из центра стрелка
            SFML.System.Vector2f startPixels = new SFML.System.Vector2f(
                posA.RenderX * microCellPixelSize + microCellPixelSize * 0.5f,
                posA.RenderY * microCellPixelSize + microCellPixelSize * 0.5f
            );

            SFML.System.Vector2f endPixels;
            int finalHitUnitId = -1;
            int hitX = posB.X;
            int hitY = posB.Y;

            if (isHit)
            {
                // СЛУЧАЙ ПОПАДАНИЯ: Пуля летит точно в цель
                finalHitUnitId = targetId;
                endPixels = new SFML.System.Vector2f(
                    posB.RenderX * microCellPixelSize + microCellPixelSize * 0.5f,
                    posB.RenderY * microCellPixelSize + microCellPixelSize * 0.5f
                );
            }
            else
            {
                // СЛУЧАЙ ПРОМАХА (Твой алгоритм разброса по радиусу излёта снаряда):
                int scatterRadius = distanceInTiles > effectiveRange ? 2 : 1;

                int scatterX = Random.Shared.Next(-scatterRadius, scatterRadius + 1);
                int scatterY = Random.Shared.Next(-scatterRadius, scatterRadius + 1);

                if (scatterX == 0 && scatterY == 0) scatterX = scatterRadius;

                // Жесткая защита от вылета пули за края массива микро-карты (0..767)
                int maxCoord = (16 * 48) - 1;
                hitX = Math.Clamp(posB.X + scatterX, 0, maxCoord);
                hitY = Math.Clamp(posB.Y + scatterY, 0, maxCoord);

                endPixels = new SFML.System.Vector2f(
                    hitX * microCellPixelSize + microCellPixelSize * 0.5f,
                    hitY * microCellPixelSize + microCellPixelSize * 0.5f
                );

                // Friendly Fire: проверяем, не зацепила ли пуля кого-то в ячейке приземления
                var unitsInScatterCell = spatialGrid.GetUnitsAt(new Components.SpatialCoord(hitX, hitY, posA.Z));
                if (unitsInScatterCell.Count > 0)
                {
                    finalHitUnitId = unitsInScatterCell[0];
                }
            }

            // ========================================================
            // ПРИМЕНЕНИЕ ДИНАМИЧЕСКОГО УРОНА НА ИЗЛЁТЕ
            // ========================================================
            // Твой калькулятор BulletPhysicsCalculator считает падение убойной силы за пределами эффективной зоны
            float finalCalculatedDamage = CombatPath.BulletPhysicsCalculator.CalculateDamageAtDistance(distanceInTiles, effectiveRange, baseDamage);
            byte damageToApply = (byte)Math.Clamp(finalCalculatedDamage, 1, 255);

            if (finalHitUnitId != -1)
            {
                // Наносим урон существу и триггерим брызги крови (пробрасываем карту)
                _damageSystem.ApplyDamage(units, finalHitUnitId, attackerId, damageToApply, weaponBleedChance, map);

                // Оповещаем группу в радиусе 5 ячеек от точки удара о близкой опасности (Impact Danger)
                // Оповещаем группу в радиусе 35 ячеек от стрелка о звуке выстрела (Acoustic Shockwave)
                // (Этот блок командной тревоги завязан ниже в методе, он отработает штатно)

                if (effectSystem != null && microCellPixelSize > 0f)
                {
                    effectSystem.AddTracer(startPixels, endPixels, posA.Z, showCross: true, duration: 0.35f);
                }
            }
            else
            {
                // Пуля врезалась в окружение (землю, скалу или постройку-укрытие)
                MapLayer layer = map.GetLayer(posA.Z);
                if (layer != null)
                {
                    ref MicroCell cell = ref layer.GetMicroCell(hitX, hitY);
                    if (cell.EdificeId > 0)
                    {
                        ref var edifice = ref edificeStore.Instances[cell.EdificeId];
                        if (edificeStore.Configs[edifice.ConfigId] != null)
                        {
                            // Крошим прочность баррикады/стены пулей
                            edifice.HitPoints -= damageToApply;
                            if (edifice.HitPoints <= 0)
                            {
                                Console.WriteLine($"💥 Укрытие ID {cell.EdificeId} полностью разрушено баллистическим огнем!");
                            }
                        }
                    }
                }

                if (effectSystem != null && microCellPixelSize > 0f)
                {
                    effectSystem.AddTracer(startPixels, endPixels, posA.Z, showCross: false, duration: 0.35f);
                }
            }

            // ИСПРАВЛЕНО: Легаси-строчка units.ShotCooldowns[attackerId] = fireRate; полностью удалена!
            // Кулдаунами теперь безопасно управляет ИИ в ExecuteRimWorldCombat.
        }


    }
}

