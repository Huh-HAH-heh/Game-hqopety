using Core.Items;
using Core.Map;
using Core.Structs;
using Core.Unit.Components;
using Core.Unit.Components.AiComponents;
using Core.Unit.Systems.CombatPath;
using System;

namespace Core.Unit.Systems;

public sealed class UnitCpuBrainSystem
{
    private readonly IAiTask[] _tasks;

    public UnitCpuBrainSystem(params IAiTask[] tasks)
    {
        _tasks = new IAiTask[Enum.GetValues<AiOpCode>().Length];

        for (int i = 0; i < tasks.Length; i++)
            _tasks[(int)tasks[i].OpCode] = tasks[i];
    }

    public void IssueCommand(UnitStore units, int unitId, AiCommand command, AiCommandInsertMode mode)
    {
        //Console.WriteLine(
        //$"[CPU] ISSUE unit={unitId} op={command.OpCode} target=({command.TargetX},{command.TargetY},{command.TargetZ})");


        if (mode == AiCommandInsertMode.ReplaceAll)
        {
            if (units.AiStackPointers[unitId] >= 0)
            {
                ref AiCommand current = ref units.CpuPeekCommand(unitId);
                IAiTask? task = _tasks[(int)current.OpCode];
                task?.Cancel(unitId);
            }

            units.CpuClearCommands(unitId);
        }

        if (mode == AiCommandInsertMode.Append)
            units.CpuAppendCommand(unitId, command);
        else
            units.CpuPushCommand(unitId, command);
    //    Console.WriteLine(
    //$"[CPU] STACK unit={unitId} count={units.AiStackPointers[unitId] + 1}");

    }

    public void Update(UnitStore units, UnitSpatialGrid spatialGrid, WorldMap map, EdificeStore edifices, CombatEffectSystem effects, float pixelSize, float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        for (int unitId = 0; unitId < units.Count; unitId++)
        {
            if (units.HealthMasks[unitId] == 0)
                continue;

            if (units.AiStackPointers[unitId] < 0)
                units.CpuPushCommand(unitId, new AiCommand { OpCode = AiOpCode.Idle });

            if (units.ShotCooldowns[unitId] > 0f)
            {
                units.ShotCooldowns[unitId] -= deltaTime;

                if (units.ShotCooldowns[unitId] < 0f)
                    units.ShotCooldowns[unitId] = 0f;
            }

            ProcessInterrupts(units, unitId, map, edifices, deltaTime);

            ref AiCommand command = ref units.CpuPeekCommand(unitId);
            IAiTask task = _tasks[(int)command.OpCode];

            if (task != null)
                task.Update(units, spatialGrid, map, edifices, unitId, ref command, deltaTime);
        }
    }

    private void ProcessInterrupts(UnitStore units, int unitId, WorldMap map, EdificeStore edifices, float deltaTime)
    {
        ref AiCommand command = ref units.CpuPeekCommand(unitId);
        ref UnitPosition position = ref units.Positions[unitId];

        if (units.LastImpactSourceX[unitId] != 0)
        {
            if (command.OpCode != AiOpCode.TakeCover)
            {
                SpatialCoord cover = FindDirectedCover(units, unitId, units.LastImpactSourceX[unitId], units.LastImpactSourceY[unitId], map, edifices);

                units.CpuPushCommand(unitId, new AiCommand
                {
                    OpCode = AiOpCode.TakeCover,
                    TargetX = cover.X,
                    TargetY = cover.Y,
                    TargetZ = cover.Z,
                    Timer = 3f
                });
            }

            units.LastImpactSourceX[unitId] = 0;
        }

        units.VisionTickTimers[unitId] += deltaTime;

        if (units.VisionTickTimers[unitId] < 0.16f)
            return;

        units.VisionTickTimers[unitId] = 0.10f + (float)Random.Shared.NextDouble() * 0.04f;

        int attackerId = units.LastAttackerIds[unitId];

        if (attackerId < 0 || units.HealthMasks[attackerId] == 0)
            return;

        ref UnitPosition attacker = ref units.Positions[attackerId];

        if (attacker.Spatial.Z != position.Spatial.Z)
            return;

        if (!VisibilityChecker.HasLineOfSight(map, edifices, position.Spatial.X, position.Spatial.Y, attacker.Spatial.X, attacker.Spatial.Y, position.Spatial.Z))
            return;

        command = ref units.CpuPeekCommand(unitId);

        if (command.OpCode != AiOpCode.CombatEngage)
        {
            units.CpuPushCommand(unitId, new AiCommand
            {
                OpCode = AiOpCode.CombatEngage,
                TargetX = attackerId
            });
        }

        units.LastAttackerIds[unitId] = -1;
    }

    private SpatialCoord FindDirectedCover(UnitStore units, int unitId, int hazardX, int hazardY, WorldMap map, EdificeStore edifices)
    {
        ref UnitPosition pos = ref units.Positions[unitId];
        SpatialCoord current = pos.Spatial;
        MapLayer layer = map.GetLayer(pos.Spatial.Z);

        if (layer == null)
            return current;

        const int maxCoord = (16 * 48) - 1;

        int vecX = Math.Sign(pos.Spatial.X - hazardX);
        int vecY = Math.Sign(pos.Spatial.Y - hazardY);
        int bestX = -1;
        int bestY = -1;
        float bestDist = float.MaxValue;

        for (int oy = -5; oy <= 5; oy++)
        {
            for (int ox = -5; ox <= 5; ox++)
            {
                int x = pos.Spatial.X + ox;
                int y = pos.Spatial.Y + oy;

                if (x < 0 || x > maxCoord || y < 0 || y > maxCoord)
                    continue;

                ref MicroCell cell = ref layer.GetMicroCell(x, y);

                if (cell.EdificeId == 0 || edifices == null)
                    continue;

                var instance = edifices.Instances[cell.EdificeId];
                var config = edifices.Configs[instance.ConfigId];

                if (config == null || config.Type != EdificeType.Wall || config.CoverEffectiveness >= 1f)
                    continue;

                int safeX = x + vecX;
                int safeY = y + vecY;

                if (safeX < 0 || safeX > maxCoord || safeY < 0 || safeY > maxCoord)
                    continue;

                ref MicroCell hide = ref layer.GetMicroCell(safeX, safeY);

                if (hide.EdificeId != 0 || (hide.Flags & 0x01) != 0)
                    continue;

                float dist = MathF.Sqrt(ox * ox + oy * oy);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestX = safeX;
                    bestY = safeY;
                }
            }
        }

        return bestX < 0 ? current : new SpatialCoord(bestX, bestY, pos.Spatial.Z);
    }
}