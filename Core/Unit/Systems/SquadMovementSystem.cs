using Core.Map;
using Core.Unit.Components;
using System;
using System.Collections.Generic;
using Core.Unit.Systems;
using System.Runtime.InteropServices; // КРИТИЧЕСКИЙ СИНТАКСИС ДЛЯ ДЕКОДИРОВАНИЯ ТИПА FIRETEAM!

namespace Core.Unit
{
    public sealed class SquadMovementSystem
    {
        private readonly UnitMovementSystem _unitMovementSystem;

        public SquadMovementSystem(UnitMovementSystem unitMovementSystem)
        {
            _unitMovementSystem = unitMovementSystem;
        }

        public void Update(
            UnitStore units,
            SquadStore squads,
            UnitSpatialGrid spatialGrid,
            WorldMap map,
            float deltaTime)
        {
            if (deltaTime <= 0f || squads == null) return;

            // Извлекаем скрытый внутренний реестр БГ из фасада SquadStore
            var fireteamRegistry = squads.Registry;
            if (fireteamRegistry == null) return;

            // Перебираем все активные дочерние мини-группы в рантайме игры
            for (int i = 0; i < fireteamRegistry.ActiveTeams.Count; i++)
            {
                // ИСПРАВЛЕНО: С типом "T" покончено, C# теперь четко видит структуру Fireteam!
                ref Fireteam team = ref CollectionsMarshal.AsSpan(fireteamRegistry.ActiveTeams)[i];

                if (!team.IsMoving)
                    continue;

                // ========================================================
                // ЖЕСТКИЙ ФИКС КЭША (Анти-Телепорт): Перехватываем событие финиша
                // Читаем напрямую из плоских корневых массивов UnitStore по ID Командира!
                // Покадровый спам и дубликаты исключены аппаратно на уровне ECS!
                // ========================================================
                if (team.LeaderUnitId >= 0 && units.HealthMasks[team.LeaderUnitId] > 0)
                {
                    int leaderId = team.LeaderUnitId;

                    if (units.HasJustFinishedMoveStep[leaderId])
                    {
                        // Пушаем точную ячейку, которую Командир физически оставил позади!
                        team.PushLeaderStep(units.LastVisitedSourceCell[leaderId]);

                        // Гасим флаг события в корневом массиве, закрывая замок от повторного спама!
                        units.HasJustFinishedMoveStep[leaderId] = false;
                    }
                }

                bool allArrived;
                // Запускаем ход группы по чистому, выверенному кэшу истории
                team.UpdateMovement(units, spatialGrid, _unitMovementSystem, map, deltaTime, out allArrived);

                if (allArrived)
                {
                    team.IsMoving = false;

                    // Обнуляем стратегический регистр БГ при финише
                    team.StrategicWaypoint = default;
                    team.TacticalState = FireteamTacticalState.Idle;

                    Console.WriteLine($"🪖 [НАВИГАЦИЯ] {team.Name} (ID: {team.Id}) успешно завершила марш.");

                    // Проверяем финиш родительского штаба
                    int parentId = team.ParentSquadId;
                    if (parentId != -1)
                    {
                        bool anySubMoving = false;
                        var allTeamsSpan = CollectionsMarshal.AsSpan(fireteamRegistry.ActiveTeams);
                        for (int t = 0; t < allTeamsSpan.Length; t++)
                        {
                            if (allTeamsSpan[t].ParentSquadId == parentId && allTeamsSpan[t].IsMoving)
                            {
                                anySubMoving = true;
                                break;
                            }
                        }
                        if (!anySubMoving)
                        {
                            squads.StopSquad(parentId);
                        }
                    }
                }
            } // Конец цикла по БГ
        }
    }
}
