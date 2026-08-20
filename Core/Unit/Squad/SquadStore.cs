//using Components;
using Core.Structs; // Твой SpatialCoord
using Core.Unit.Components;
using Core.Unit.Squad;
using Core.Unit.Systems;
using System;
using System.Collections.Generic;

namespace Core.Unit
{
    public sealed class SquadStore
    {
        public int Count { get; private set; } = 0;
        private readonly int _maxSquads;

        public readonly List<int>[] SubFireteamIds;
        public readonly int[] LeaderUnitIds;
        public readonly bool[] IsInCombatMode;
        public readonly bool[] IsMoving;
        public readonly SpatialCoord[] CurrentWaypoints;
        public readonly List<int>[] Members;

        private readonly FireteamRegistry _internalRegistry = new FireteamRegistry();
        public FireteamRegistry Registry => _internalRegistry;

        public SquadStore(int maxSquads = 128)
        {
            _maxSquads = maxSquads;
            SubFireteamIds = new List<int>[_maxSquads];
            Members = new List<int>[_maxSquads];
            LeaderUnitIds = new int[_maxSquads];
            IsInCombatMode = new bool[_maxSquads];
            IsMoving = new bool[_maxSquads];
            CurrentWaypoints = new SpatialCoord[_maxSquads];

            for (int i = 0; i < _maxSquads; i++)
            {
                SubFireteamIds[i] = new List<int>(4);
                Members[i] = new List<int>(16);
                LeaderUnitIds[i] = -1;
                IsInCombatMode[i] = false;
                IsMoving[i] = false;
                CurrentWaypoints[i] = default;
            }
        }

        public int CreateSquad()
        {
            if (Count >= _maxSquads) throw new Exception("Достигнут лимит глобальных сквадов!");
            int id = Count++;
            SubFireteamIds[id].Clear();
            Members[id].Clear();
            LeaderUnitIds[id] = -1;
            IsInCombatMode[id] = false;
            IsMoving[id] = false;
            return id;
        }

        public void AddMember(int squadId, int unitId, UnitStore unitStore, UnitSpatialGrid spatialGrid)
        {
            if (squadId < 0 || squadId >= Count) return;

            if (!Members[squadId].Contains(unitId))
            {
                Members[squadId].Add(unitId);
            }

            int targetTeamId = -1;
            var connectedTeams = SubFireteamIds[squadId];

            // ИСПРАВЛЕНО: Безопасный перебор через индексы Registry безout ref!
            for (int i = 0; i < connectedTeams.Count; i++)
            {
                int bId = connectedTeams[i];
                int teamIdx = _internalRegistry.GetTeamIndex(bId);
                if (teamIdx != -1)
                {
                    ref Fireteam existingTeam = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_internalRegistry.ActiveTeams)[teamIdx];
                    if (existingTeam.MemberUnitIds.Count < 5)
                    {
                        targetTeamId = bId;
                        break;
                    }
                }
            }

            if (targetTeamId == -1)
            {
                int newTeamId = _internalRegistry.ActiveTeams.Count;
                string teamName = $"Звено {(char)('А' + connectedTeams.Count)} ({squadId}-го отряда)";

                Fireteam newTeam = new Fireteam(newTeamId, squadId, teamName, maxCapacity: 5);
                newTeam.LeaderUnitId = unitId;

                _internalRegistry.PushTeam(newTeam);
                connectedTeams.Add(newTeamId);
                targetTeamId = newTeamId;

                if (LeaderUnitIds[squadId] == -1) LeaderUnitIds[squadId] = unitId;
            }

            int targetIdx = _internalRegistry.GetTeamIndex(targetTeamId);
            if (targetIdx != -1)
            {
                ref Fireteam targetTeam = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_internalRegistry.ActiveTeams)[targetIdx];
                if (!targetTeam.MemberUnitIds.Contains(unitId))
                {
                    targetTeam.MemberUnitIds.Add(unitId);
                    unitStore.SquadIds[unitId] = targetTeam.Id;
                }
            }
        }

        public void SetWaypoint(int squadId, SpatialCoord target)
        {
            if (squadId < 0 || squadId >= Count) return;

            CurrentWaypoints[squadId] = target;
            IsMoving[squadId] = true;

            var teamIds = SubFireteamIds[squadId];
            for (int i = 0; i < teamIds.Count; i++)
            {
                _internalRegistry.OrderTeamTo(teamIds[i], target, SquadFormation.Square);
            }
        }

        public void StopSquad(int squadId)
        {
            if (squadId >= 0 && squadId < Count) IsMoving[squadId] = false;
        }

        public void RearrangeSquadCells(int squadId, UnitStore unitStore, UnitSpatialGrid spatialGrid) { }
    }
}
