using Core.Items;
using Core.Map;
using Core.Structs; // Твой SpatialCoord
using System;
using System.Collections.Generic;

namespace Core.Unit.Systems
{
    public sealed class SquadTacticalAiSystem
    {
        public void Update(UnitStore units, SquadStore squads, FireteamRegistry fireteamRegistry, WorldMap map, EdificeStore edifices)
        {
            if (fireteamRegistry == null || squads == null) return;

            // Перебираем штабные сквады фракций
            for (int s = 0; s < squads.Count; s++)
            {
                var connectedTeamIds = squads.SubFireteamIds[s];
                if (connectedTeamIds == null || connectedTeamIds.Count == 0) continue;

                bool isAnyTeamInCombat = false;

                for (int t = 0; t < connectedTeamIds.Count; t++)
                {
                    int bId = connectedTeamIds[t];

                    // ИСПРАВЛЕНО: Читаем через GetTeamIndex без ломающего out ref
                    int teamIdx = fireteamRegistry.GetTeamIndex(bId);
                    if (teamIdx != -1)
                    {
                        ref Fireteam team = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(fireteamRegistry.ActiveTeams)[teamIdx];

                        // Если БГ попала под раздачу — взводим тревогу всему штабу!
                        if (team.TacticalState == FireteamTacticalState.SuppressedCover)
                        {
                            isAnyTeamInCombat = true;
                        }
                    }
                }

                squads.IsInCombatMode[s] = isAnyTeamInCombat;
            }
        }
    }
}
