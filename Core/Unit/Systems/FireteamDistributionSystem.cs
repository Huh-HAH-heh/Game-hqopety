using Core.Unit.Squad;
using Core.Unit.Systems;
using System;
using System.Collections.Generic;

namespace Core.Unit.Systems
{
    public sealed class FireteamDistributionSystem
    {
        private static int _globalTeamIdCounter = 0;

        /// <summary>
        /// Принудительно распределяет ЛС из глобального сквада по мелким тактическим огневым группам.
        /// </summary>
        /// <param name="units">ECS-хранилище юнитов</param>
        /// <param name="squads">Глобальный SquadStore</param>
        /// <param name="registry">Наш новый Диспетчер Огневых Групп (FireteamRegistry)</param>
        /// <param name="rootSquadId">ID родительского сквада, который нужно расселить</param>
        /// <param name="unitsPerGroup">По сколько человек нарезать группы (N)</param>
        public static void DistributePersonnel(
            UnitStore units,
            SquadStore squads,
            FireteamRegistry registry,
            int rootSquadId,
            int unitsPerGroup = 5)
        {
            var rootMembers = squads.Members[rootSquadId];
            if (rootMembers == null || rootMembers.Count == 0) return;

            Console.WriteLine($"🪖 [ИЕРАРХИЯ] Распределяем ЛС сквада {rootSquadId} ({rootMembers.Count} тел) по группам по {unitsPerGroup} человек...");

            // Создаем временный список живых бойцов, чтобы не мусорить в памяти
            List<int> aliveUnits = new List<int>(rootMembers.Count);
            for (int i = 0; i < rootMembers.Count; i++)
            {
                int uid = rootMembers[i];
                if (units.HealthMasks[uid] > 0) aliveUnits.Add(uid);
            }

            if (aliveUnits.Count == 0) return;

            // Нарезаем ЛС на автономные Fireteam
            int currentUnitIndex = 0;
            int groupCounter = 0;

            while (currentUnitIndex < aliveUnits.Count)
            {
                // Вычисляем, сколько человек пойдет в эту конкретную группу (не более N)
                int currentGroupSize = Math.Min(unitsPerGroup, aliveUnits.Count - currentUnitIndex);

                int newTeamId = _globalTeamIdCounter++;
                string teamName = $"Звено {(char)('А' + groupCounter)} ({rootSquadId}-го отряда)";

                // Создаем структуру Fireteam со строго изолированным кэшем шагов N + 10 ячеек!
                Fireteam newTeam = new Fireteam(newTeamId, rootSquadId, teamName, maxCapacity: unitsPerGroup);

                // Назначаем командира огневого звена (первый боец в группе)
                newTeam.LeaderUnitId = aliveUnits[currentUnitIndex];

                // Раздаем тактические строи по умолчанию в зависимости от ID группы (для рантайм-разнообразия)
                newTeam.Formation = (SquadFormation)((newTeamId % 3) + 1); // Чередуем Клин, Алмаз, Линию

                // Набиваем личный состав в Fireteam
                for (int g = 0; g < currentGroupSize; g++)
                {
                    int unitId = aliveUnits[currentUnitIndex + g];
                    newTeam.MemberUnitIds.Add(unitId);

                    // Перелинковываем ECS-компонент памяти: теперь SquadId внутри UnitStore 
                    // хранит не глобальный сквад, а персональный ID его новой Fireteam!
                    units.SquadIds[unitId] = newTeam.Id;
                }

                // Официально прописываем огневую группу в реестр навигации
                registry.ActiveTeams.Add(newTeam);

                Console.WriteLine($"  -> Успешно создано {newTeam.Name} [ID: {newTeam.Id}]. Состав: {currentGroupSize} бойцов. Строй: {newTeam.Formation}");

                currentUnitIndex += currentGroupSize;
                groupCounter++;
            }

            // Очищаем список членов глобального сквада, так как теперь их тела полностью 
            // инкапсулированы внутри независимых структур Fireteam!
            rootMembers.Clear();
        }
    }
}
