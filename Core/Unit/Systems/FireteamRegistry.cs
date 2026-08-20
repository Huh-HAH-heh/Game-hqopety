using Core.Map;
using Core.Structs; // Твой SpatialCoord
using Core.Unit.Components;
using Core.Unit.Squad;
using System;
using System.Collections.Generic;

namespace Core.Unit.Systems
{
    public sealed class FireteamRegistry
    {
        // Глобальный список всех активных огневых групп в игре
        public readonly List<Fireteam> ActiveTeams = new List<Fireteam>(32);

        /// <summary>
        /// МЕТОД PUSH: Добавляет готовую огневую группу в реестр
        /// </summary>
        public void PushTeam(Fireteam team)
        {
            ActiveTeams.Add(team);
            Console.WriteLine($"⚙️ [РЕЕСТР] Группа '{team.Name}' (ID: {team.Id}) успешно зарегистрирована.");
        }

        /// <summary>
        /// МЕТОД GET: Ищет индекс группы в списке по её ID. Возвращает -1, если не найдена.
        /// </summary>
        public int GetTeamIndex(int teamId)
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ActiveTeams);
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i].Id == teamId) return i;
            }
            return -1;
        }

        /// <summary>
        /// МЕТОД ПРИКАЗА: Направляет группу в точку клика
        /// </summary>
        public void OrderTeamTo(int teamId, SpatialCoord destCell, SquadFormation formation)
        {
            int index = GetTeamIndex(teamId);
            if (index != -1)
            {
                // Извлекаем прямую ref-ссылку на структуру внутри списка по индексу
                ref Fireteam team = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ActiveTeams)[index];

                team.Formation = formation;
                team.StrategicWaypoint = destCell;
                team.TacticalState = FireteamTacticalState.Marching; // Переключаем БГ в режим марша!

                Array.Clear(team.StepTrails, 0, team.StepTrails.Length);
                Console.WriteLine($"📣 [РЕЕСТР API] {team.Name} отправлена в ({destCell.X}, {destCell.Y}) строем {formation}.");
            }
        }

        public void UpdateMovement(UnitStore units, UnitSpatialGrid spatialGrid, UnitMovementSystem movementMotor, WorldMap map, float deltaTime)
        {
            if (deltaTime <= 0f) return;

            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ActiveTeams);
            for (int i = 0; i < span.Length; i++)
            {
                ref Fireteam team = ref span[i];
                if (!team.IsMoving) continue;

                bool allArrived;
                team.UpdateMovement(units, spatialGrid, movementMotor, map, deltaTime, out allArrived);
                if (allArrived) team.IsMoving = false;
            }
        }
    }
}
