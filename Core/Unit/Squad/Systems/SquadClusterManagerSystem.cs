using Core.Structs;
using Core.Unit.Components;
using Core.Unit.Squad;
using System;
using System.Collections.Generic;

namespace Core.Unit.Systems
{
    public sealed class SquadClusterManagerSystem
    {
        private float _reclusterTimer = 4.0f;
        private readonly List<int> _tempUnitBuffer = new List<int>(64);

        // ИСПРАВЛЕНО: Теперь менеджер принимает FireteamRegistry вместо взлома внутренностей сквада!
        public void Update(UnitStore units, SquadStore squads, FireteamRegistry fireteamRegistry, float deltaTime)
        {
            _reclusterTimer -= deltaTime;
            if (_reclusterTimer > 0f) return;

            // Задаем интервальный шаг 4 секунды (Анти-Оверклок процессора)
            _reclusterTimer = 4.0f + Random.Shared.NextSingle() * 0.5f;

            // Перебираем глобальные базовые сквады фракций
            for (int s = squads.Count - 1; s >= 0; s--)
            {
                var members = squads.Members[s];
                if (members == null || members.Count <= 5) continue;

                // КРИТИЧЕСКОЕ ПРАВИЛО: Если глобальный отряд воюет — не срываем людей переформированием!
                if (squads.IsInCombatMode[s] || squads.IsMoving[s]) continue;

                // Фильтруем и собираем только живых бойцов в буфер
                _tempUnitBuffer.Clear();
                for (int i = 0; i < members.Count; i++)
                {
                    int uid = members[i];
                    if (units.HealthMasks[uid] > 0) _tempUnitBuffer.Add(uid);
                }

                if (_tempUnitBuffer.Count <= 5) continue;

                Console.WriteLine($"⚙️ [ИЕРАРХИЯ] Корневой Сквад ID: {s} дробится на автономные Fireteams по 5 человек...");

                // Оставляем в главном управлении сквада только штабную ячейку (первые 4 человека)
                members.Clear();
                squads.LeaderUnitIds[s] = _tempUnitBuffer[0];

                for (int i = 0; i < 4; i++)
                {
                    int uid = _tempUnitBuffer[i];
                    members.Add(uid);
                    // Перелинковка: штабные остаются привязаны к глобальному скваду s
                    units.SquadIds[uid] = s;
                }

                // ========================================================
                // ИСПРАВЛЕНО: НАРЕЗАЕМ ЛС НА АВТОНОМНЫЕ СТРУКТУРЫ FIRETEAM!
                // Никаких SubSquadIds и Formations внутри стораджа!
                // Каждый мини-отряд получает свой изолированный кэш N + 10!
                // ========================================================
                int groupCounter = 0;
                int currentUnitIndex = 4;

                while (currentUnitIndex < _tempUnitBuffer.Count)
                {
                    // Нарезаем строго по 5 человек в группу
                    int currentGroupSize = Math.Min(5, _tempUnitBuffer.Count - currentUnitIndex);

                    int newTeamId = fireteamRegistry.ActiveTeams.Count;
                    string teamName = $"Звено {(char)('А' + groupCounter)} ({s}-го отряда)";

                    // Создаем чистую структуру Fireteam с емкостью 5 человек
                    Fireteam newTeam = new Fireteam(newTeamId, s, teamName, maxCapacity: 5);
                    newTeam.LeaderUnitId = _tempUnitBuffer[currentUnitIndex];

                    // Автоматически чередуем строи по умолчанию (Клин, Алмаз, Линия) на основе ID
                    newTeam.Formation = (SquadFormation)((newTeamId % 3) + 1);

                    // Заселяем личный состав
                    for (int g = 0; g < currentGroupSize; g++)
                    {
                        int unitId = _tempUnitBuffer[currentUnitIndex + g];
                        newTeam.MemberUnitIds.Add(unitId);

                        // Записываем ID новой группы прямо в ECS-компонент юнита!
                        units.SquadIds[unitId] = newTeam.Id;
                    }

                    // Регистрируем мини-группу в глобальном навигационном реестре
                    fireteamRegistry.ActiveTeams.Add(newTeam);

                    Console.WriteLine($"  -> Успешно создано {newTeam.Name} [ID: {newTeam.Id}]. Состав: {currentGroupSize} бойцов.");

                    currentUnitIndex += currentGroupSize;
                    groupCounter++;
                }
            }
        }
    }
}
