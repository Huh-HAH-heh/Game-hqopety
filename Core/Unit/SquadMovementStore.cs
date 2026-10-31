using System;
using System.Collections.Generic;
using System.Numerics;
using Core.AI;
using Core.Map;

namespace Core.Unit;

public enum SquadMovementPhase : byte
{
    Idle = 0,
    Advancing = 1,
    Contact = 2,
    Regrouping = 3,
    Arrived = 4
}

public enum SquadMemberMovementState : byte
{
    Following = 0,
    Engaged = 1,
    Rejoining = 2,
    Separated = 3
}

/// <summary>
/// Rigidity is an extension point. Free is the active default; Organized may
/// increase cohesion slightly. Strict is reserved and is not enforced yet.
/// </summary>
public enum SquadFormationRigidity : byte
{
    Free = 0,
    Organized = 1,
    Strict = 2
}

/// <summary>
/// Read-only movement/information-layer snapshot for HUD, debug overlays and AI inspection.
/// Shared enemy observations remain in SquadTacticalCoordinator.
/// </summary>
public readonly record struct SquadMovementInformation(
    int SquadId,
    Vector3 Objective,
    SquadMovementPhase Phase,
    InfantryFormationType Formation,
    SquadFormationRigidity Rigidity,
    int MemberCount,
    int FollowingCount,
    int EngagedCount,
    int RejoiningCount,
    int SeparatedCount,
    bool FlowFieldReady,
    long TerrainVersion);

/// <summary>
/// Owns collective movement intent while every pawn remains an autonomous physical unit.
/// The flow field describes the common route; formation offsets are weak spatial preferences.
/// </summary>
public sealed class SquadMovementStore
{
    private const float RejoinDistance = 16f;
    private const float RejoinBlendSeconds = 1.8f;
    private const float MaxCohesionCorrection = 5f;

    private Dictionary<int, SquadState> _squads = new();
    private Dictionary<UnitId, MemberLink> _membership = new();
    private readonly SquadFlowFieldWorkspace _flowWorkspace = new();
    private SquadMemberMovementState[] _memberStates = Array.Empty<SquadMemberMovementState>();
    private UnitId[] _memberOwners = Array.Empty<UnitId>();
    private float[] _rejoinElapsed = Array.Empty<float>();

    public int SquadCount => _squads.Count;

    public void SynchronizeMemberships(IReadOnlyList<SquadMembershipSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        Dictionary<int, SquadState> nextSquads = new();
        Dictionary<UnitId, MemberLink> nextMembership = new();

        for (int i = 0; i < snapshots.Count; i++)
        {
            SquadMembershipSnapshot snapshot = snapshots[i];
            if (snapshot.SquadId <= 0 || nextSquads.ContainsKey(snapshot.SquadId))
                continue;

            List<UnitId> members = new(snapshot.Members.Count);
            HashSet<UnitId> seen = new();
            for (int j = 0; j < snapshot.Members.Count; j++)
            {
                UnitId member = snapshot.Members[j];
                if (member.IsValid && seen.Add(member) && !nextMembership.ContainsKey(member))
                    members.Add(member);
            }

            if (members.Count == 0)
                continue;

            SquadState state;
            if (_squads.TryGetValue(snapshot.SquadId, out SquadState? existing) &&
                SameMembers(existing.Members, members))
            {
                state = existing;
            }
            else
            {
                state = new SquadState(snapshot.SquadId, members);
            }

            nextSquads.Add(snapshot.SquadId, state);
            for (int slot = 0; slot < members.Count; slot++)
                nextMembership[members[slot]] = new MemberLink(snapshot.SquadId, slot);
        }

        foreach (KeyValuePair<UnitId, MemberLink> pair in nextMembership)
        {
            bool membershipChanged =
                !_membership.TryGetValue(pair.Key, out MemberLink previous) ||
                previous.SquadId != pair.Value.SquadId;

            if (membershipChanged &&
                (uint)pair.Key.Index < (uint)_memberOwners.Length)
            {
                _memberOwners[pair.Key.Index] = default;
                _memberStates[pair.Key.Index] = SquadMemberMovementState.Following;
                _rejoinElapsed[pair.Key.Index] = 0f;
            }
        }

        _squads = nextSquads;
        _membership = nextMembership;
    }

    public void InvalidateUnit(UnitId unit)
    {
        if (!unit.IsValid || !_membership.Remove(unit, out MemberLink link) ||
            !_squads.TryGetValue(link.SquadId, out SquadState? squad))
        {
            return;
        }

        squad.Members.RemoveAt(link.Slot);
        squad.MemberOffsets = RemoveAt(squad.MemberOffsets, link.Slot);
        squad.MemberGoals = RemoveAt(squad.MemberGoals, link.Slot);

        if (squad.Members.Count == 0)
        {
            _squads.Remove(link.SquadId);
            return;
        }

        for (int i = link.Slot; i < squad.Members.Count; i++)
            _membership[squad.Members[i]] = new MemberLink(link.SquadId, i);
    }

    public bool IssueMoveOrder(
        UnitStore units,
        UnitHealthStore health,
        UnitAiStore ai,
        int squadId,
        IReadOnlyList<UnitId> requestedMembers,
        Vector3 destination,
        InfantryFormationType formation,
        WorldMap worldMap)
    {
        if (squadId <= 0 ||
            requestedMembers == null ||
            requestedMembers.Count == 0 ||
            !_squads.TryGetValue(squadId, out SquadState? squad))
        {
            return false;
        }

        HashSet<UnitId> selected = new(requestedMembers.Count);
        for (int i = 0; i < requestedMembers.Count; i++)
            selected.Add(requestedMembers[i]);

        List<(UnitId Id, int Index, int Slot)> activeMembers = new(squad.Members.Count);
        Vector2 center = Vector2.Zero;
        float spacing = 1.15f;

        for (int slot = 0; slot < squad.Members.Count; slot++)
        {
            UnitId id = squad.Members[slot];
            if (!units.TryGetIndex(id, out int index) || health.OverallHitPoints[index] <= 0f)
                continue;

            // Group movement is issued only when the complete living squad is selected.
            if (!selected.Contains(id))
                return false;

            activeMembers.Add((id, index, slot));
            Vector3 position = units.Position[index];
            center += new Vector2(position.X, position.Y);
            spacing = MathF.Max(spacing, units.Radius[index] * 2.25f);
        }

        if (activeMembers.Count == 0)
            return false;

        center /= activeMembers.Count;

        Vector2 goal = new(
            Math.Clamp(destination.X, 0.5f, worldMap.MaxTileX - 0.5f),
            Math.Clamp(destination.Y, 0.5f, worldMap.MaxTileY - 0.5f));
        Vector2 forward = goal - center;
        if (forward.LengthSquared() < 0.0001f)
            forward = new Vector2(1f, 0f);
        else
            forward = Vector2.Normalize(forward);

        Vector2 side = new(-forward.Y, forward.X);
        Vector2 offsetSum = Vector2.Zero;
        Vector2[] rawOffsets = new Vector2[activeMembers.Count];

        for (int i = 0; i < activeMembers.Count; i++)
        {
            rawOffsets[i] = InfantryFormation.GetOffset(
                i, activeMembers.Count, formation, spacing);
            offsetSum += rawOffsets[i];
        }

        Vector2 meanOffset = offsetSum / activeMembers.Count;
        squad.MemberOffsets = new Vector2[squad.Members.Count];
        squad.MemberGoals = new Vector2[squad.Members.Count];

        for (int i = 0; i < activeMembers.Count; i++)
        {
            (UnitId id, int index, int slot) = activeMembers[i];
            Vector2 localOffset = rawOffsets[i] - meanOffset;
            Vector2 worldOffset = side * localOffset.X - forward * localOffset.Y;
            Vector2 terminal = ResolveNavigablePoint(
                worldMap,
                goal + worldOffset);

            squad.MemberOffsets[slot] = localOffset;
            squad.MemberGoals[slot] = terminal;

            Vector3 memberTarget = new(
                terminal.X,
                terminal.Y,
                worldMap.GetSurfaceHeight(
                    Math.Clamp((int)MathF.Floor(terminal.X), 0, worldMap.MaxTileX),
                    Math.Clamp((int)MathF.Floor(terminal.Y), 0, worldMap.MaxTileY)));

            ai.SetPlayerOrder(index, UnitAiOrderType.Move, memberTarget);
            units.SetTarget(id, memberTarget);
            SetMemberState(units, index, SquadMemberMovementState.Following);
        }

        squad.Objective = new Vector3(goal.X, goal.Y, destination.Z);
        squad.Center = center;
        squad.Forward = forward;
        squad.Spacing = spacing;
        squad.Formation = formation;
        squad.Rigidity = SquadFormationRigidity.Free;
        squad.HasMoveOrder = true;
        squad.Phase = SquadMovementPhase.Advancing;
        squad.FlowField.Invalidate();
        return true;
    }

    public void BeginUpdate(
        UnitStore units,
        UnitHealthStore? health,
        UnitAiStore? ai,
        WorldMap worldMap,
        float deltaTime)
    {
        EnsureMemberCapacity(units.Capacity);
        _flowWorkspace.EnsureMap(worldMap);

        foreach (SquadState squad in _squads.Values)
        {
            squad.CenterAccumulator = Vector2.Zero;
            squad.CenterMemberCount = 0;
            squad.FollowingCount = 0;
            squad.EngagedCount = 0;
            squad.RejoiningCount = 0;
            squad.SeparatedCount = 0;
            squad.MovingCount = 0;
        }

        ReadOnlySpan<int> active = units.ActiveIndices;
        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            UnitId id = units.GetId(unit);
            if (!_membership.TryGetValue(id, out MemberLink link) ||
                !_squads.TryGetValue(link.SquadId, out SquadState? squad) ||
                !squad.HasMoveOrder ||
                (health != null && health.OverallHitPoints[unit] <= 0f))
            {
                continue;
            }

            EnsureMemberIdentity(units, unit);
            if (units.HasTarget[unit])
                squad.MovingCount++;

            if (IsDetachedFromCollective(units, ai, unit))
            {
                _memberStates[unit] = SquadMemberMovementState.Engaged;
                _rejoinElapsed[unit] = 0f;
                squad.EngagedCount++;
                continue;
            }

            if (_memberStates[unit] == SquadMemberMovementState.Engaged)
            {
                _memberStates[unit] = SquadMemberMovementState.Rejoining;
                _rejoinElapsed[unit] = 0f;
            }

            Vector2 position = new(units.Position[unit].X, units.Position[unit].Y);
            float distanceFromGroup = Vector2.Distance(position, squad.Center);
            if (distanceFromGroup > RejoinDistance)
                _memberStates[unit] = SquadMemberMovementState.Separated;
            else if (_memberStates[unit] == SquadMemberMovementState.Separated)
            {
                _memberStates[unit] = SquadMemberMovementState.Rejoining;
                _rejoinElapsed[unit] = 0f;
            }

            if (_memberStates[unit] == SquadMemberMovementState.Rejoining)
            {
                _rejoinElapsed[unit] += Math.Clamp(deltaTime, 0f, 0.25f);
                squad.RejoiningCount++;
            }
            else if (_memberStates[unit] == SquadMemberMovementState.Separated)
            {
                squad.SeparatedCount++;
                continue;
            }
            else
            {
                _memberStates[unit] = SquadMemberMovementState.Following;
                squad.FollowingCount++;
            }

            squad.CenterAccumulator += position;
            squad.CenterMemberCount++;
        }

        foreach (SquadState squad in _squads.Values)
        {
            if (squad.CenterMemberCount > 0)
                squad.Center = squad.CenterAccumulator / squad.CenterMemberCount;

            Vector2 toObjective = new(
                squad.Objective.X - squad.Center.X,
                squad.Objective.Y - squad.Center.Y);
            if (toObjective.LengthSquared() > 0.0001f)
                squad.Forward = Vector2.Normalize(toObjective);

            if (!squad.HasMoveOrder)
                squad.Phase = SquadMovementPhase.Idle;
            else if (squad.EngagedCount > 0)
                squad.Phase = SquadMovementPhase.Contact;
            else if (squad.RejoiningCount > 0 || squad.SeparatedCount > 0)
                squad.Phase = SquadMovementPhase.Regrouping;
            else if (squad.MovingCount == 0)
                squad.Phase = SquadMovementPhase.Arrived;
            else
                squad.Phase = SquadMovementPhase.Advancing;
        }
    }

    public bool TryGetSteering(
        UnitStore units,
        int unitIndex,
        WorldMap worldMap,
        Vector3 position,
        float currentSpeed,
        UnitAiStore? ai,
        out Vector2 steeringPoint,
        out float distanceToGoal)
    {
        steeringPoint = default;
        distanceToGoal = float.PositiveInfinity;

        if ((uint)unitIndex >= (uint)units.Capacity)
            return false;

        UnitId id = units.GetId(unitIndex);
        if (!_membership.TryGetValue(id, out MemberLink link) ||
            !_squads.TryGetValue(link.SquadId, out SquadState? squad) ||
            !squad.HasMoveOrder ||
            link.Slot >= squad.MemberGoals.Length ||
            !units.HasTarget[unitIndex] ||
            IsDetachedFromCollective(units, ai, unitIndex))
        {
            return false;
        }

        if (!squad.FlowField.EnsureBuilt(worldMap, squad.Objective, _flowWorkspace) ||
            !squad.FlowField.TrySample(position.X, position.Y,
                out Vector2 flowDirection, out float routeDistance))
        {
            return false;
        }

        Vector2 terminal = squad.MemberGoals[link.Slot];
        Vector2 terminalDelta = terminal - new Vector2(position.X, position.Y);
        float terminalDistance = terminalDelta.Length();
        float terminalApproachRadius = Math.Clamp(
            squad.Spacing * 3f,
            3f,
            8f);
        float terminalBlend = Math.Clamp(
            (terminalApproachRadius - terminalDistance) / terminalApproachRadius,
            0f,
            1f);

        Vector2 direction = flowDirection;
        if (terminalBlend > 0.001f && terminalDistance > 0.001f)
        {
            Vector2 terminalDirection = terminalDelta / terminalDistance;
            direction = Vector2.Lerp(flowDirection, terminalDirection, terminalBlend);
        }

        Vector2 localOffset = squad.MemberOffsets[link.Slot];
        Vector2 side = new(-squad.Forward.Y, squad.Forward.X);
        Vector2 preferredPosition = squad.Center +
            side * localOffset.X - squad.Forward * localOffset.Y;
        Vector2 cohesion = preferredPosition - new Vector2(position.X, position.Y);
        float cohesionLength = cohesion.Length();
        if (cohesionLength > MaxCohesionCorrection)
            cohesion *= MaxCohesionCorrection / cohesionLength;

        float cohesionStrength = squad.Rigidity == SquadFormationRigidity.Organized
            ? 0.25f
            : 0.18f;

        if (_memberStates[unitIndex] == SquadMemberMovementState.Rejoining)
        {
            float rejoinProgress = Math.Clamp(
                _rejoinElapsed[unitIndex] / RejoinBlendSeconds,
                0.08f,
                1f);
            cohesionStrength *= rejoinProgress;
        }

        float finalDistance = routeDistance * (1f - terminalBlend) +
            terminalDistance * terminalBlend;
        if (cohesionLength > 0.001f && finalDistance > 0.5f)
            direction += cohesion * (cohesionStrength / MathF.Max(1f, cohesionLength));

        float directionLength = direction.Length();
        if (directionLength < 0.001f)
        {
            if (terminalDistance < 0.35f)
                direction = Vector2.Zero;
            else if (terminalDistance > 0.001f)
                direction = terminalDelta / terminalDistance;
            else
                return false;
        }
        else
        {
            direction /= directionLength;
        }

        float lookAhead = Math.Clamp(1.25f + currentSpeed * 0.72f, 1.4f, 5f);
        steeringPoint = new Vector2(position.X, position.Y) + direction * lookAhead;
        distanceToGoal = finalDistance;
        return true;
    }

    public bool TryGetMemberState(
        UnitId member,
        out SquadMemberMovementState state)
    {
        state = default;
        if (!member.IsValid ||
            !_membership.TryGetValue(member, out MemberLink link) ||
            !_squads.TryGetValue(link.SquadId, out SquadState? squad) ||
            !squad.HasMoveOrder ||
            (uint)member.Index >= (uint)_memberStates.Length ||
            _memberOwners[member.Index] != member)
        {
            return false;
        }

        state = _memberStates[member.Index];
        return true;
    }

    public bool TryGetInformation(int squadId, out SquadMovementInformation information)
    {
        if (!_squads.TryGetValue(squadId, out SquadState? squad))
        {
            information = default;
            return false;
        }

        information = new SquadMovementInformation(
            squad.Id,
            squad.Objective,
            squad.Phase,
            squad.Formation,
            squad.Rigidity,
            squad.Members.Count,
            squad.FollowingCount,
            squad.EngagedCount,
            squad.RejoiningCount,
            squad.SeparatedCount,
            squad.FlowField.IsReady,
            squad.FlowField.TerrainVersion);
        return true;
    }

    public void SetFormationRigidity(int squadId, SquadFormationRigidity rigidity)
    {
        if (_squads.TryGetValue(squadId, out SquadState? squad))
        {
            // Strict remains a future hook; until implemented it behaves as Organized.
            squad.Rigidity = rigidity == SquadFormationRigidity.Strict
                ? SquadFormationRigidity.Organized
                : rigidity;
        }
    }

    private bool IsDetachedFromCollective(UnitStore units, UnitAiStore? ai, int unit)
    {
        if (ai == null)
            return false;

        if (ai.PlayerOrderType[unit] == UnitAiOrderType.Attack ||
            ai.State[unit] is UnitAiState.Attack or UnitAiState.SeekCover or
                UnitAiState.Flank or UnitAiState.Injured)
        {
            return true;
        }

        return ai.HasTarget[unit] &&
            ai.Target[unit].IsValid &&
            units.IsAlive(ai.Target[unit]);
    }

    private void EnsureMemberIdentity(UnitStore units, int unit)
    {
        UnitId id = units.GetId(unit);
        if (_memberOwners[unit] == id)
            return;

        _memberOwners[unit] = id;
        _memberStates[unit] = SquadMemberMovementState.Following;
        _rejoinElapsed[unit] = 0f;
    }

    private void SetMemberState(
        UnitStore units,
        int unit,
        SquadMemberMovementState state)
    {
        EnsureUnitCapacity(units.Capacity);
        EnsureMemberIdentity(units, unit);
        _memberStates[unit] = state;
        _rejoinElapsed[unit] = 0f;
    }

    private void EnsureMemberCapacity(int required)
    {
        if (_memberStates.Length >= required)
            return;

        int capacity = Math.Max(required, Math.Max(16, _memberStates.Length * 2));
        Array.Resize(ref _memberStates, capacity);
        Array.Resize(ref _memberOwners, capacity);
        Array.Resize(ref _rejoinElapsed, capacity);
    }

    private static Vector2 ResolveNavigablePoint(WorldMap worldMap, Vector2 requested)
    {
        int centerX = Math.Clamp((int)MathF.Floor(requested.X), 0, worldMap.MaxTileX);
        int centerY = Math.Clamp((int)MathF.Floor(requested.Y), 0, worldMap.MaxTileY);

        for (int radius = 0; radius <= 6; radius++)
        {
            float bestDistance = float.PositiveInfinity;
            Vector2 best = default;
            for (int y = Math.Max(0, centerY - radius);
                 y <= Math.Min(worldMap.MaxTileY, centerY + radius); y++)
            {
                for (int x = Math.Max(0, centerX - radius);
                     x <= Math.Min(worldMap.MaxTileX, centerX + radius); x++)
                {
                    if (Math.Max(Math.Abs(x - centerX), Math.Abs(y - centerY)) != radius ||
                        worldMap.IsNavigationBlocked(x, y) ||
                        worldMap.GetSurfaceLayer(x, y) < 0)
                    {
                        continue;
                    }

                    float dx = x + 0.5f - requested.X;
                    float dy = y + 0.5f - requested.Y;
                    float distance = dx * dx + dy * dy;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = new Vector2(x + 0.5f, y + 0.5f);
                    }
                }
            }

            if (float.IsFinite(bestDistance))
                return best;
        }

        return new Vector2(centerX + 0.5f, centerY + 0.5f);
    }

    private static bool SameMembers(List<UnitId> left, List<UnitId> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
                return false;
        }

        return true;
    }

    private static Vector2[] RemoveAt(Vector2[] source, int index)
    {
        if ((uint)index >= (uint)source.Length || source.Length <= 1)
            return Array.Empty<Vector2>();

        Vector2[] result = new Vector2[source.Length - 1];
        if (index > 0)
            Array.Copy(source, 0, result, 0, index);
        if (index < source.Length - 1)
            Array.Copy(source, index + 1, result, index, source.Length - index - 1);
        return result;
    }

    private readonly record struct MemberLink(int SquadId, int Slot);

    private sealed class SquadState
    {
        public int Id { get; }
        public List<UnitId> Members { get; }
        public SquadFlowField FlowField { get; } = new();
        public Vector3 Objective;
        public Vector2 Center;
        public Vector2 CenterAccumulator;
        public Vector2 Forward = Vector2.UnitX;
        public float Spacing = 1.15f;
        public InfantryFormationType Formation = InfantryFormationType.Free;
        public SquadFormationRigidity Rigidity = SquadFormationRigidity.Free;
        public SquadMovementPhase Phase = SquadMovementPhase.Idle;
        public Vector2[] MemberOffsets = Array.Empty<Vector2>();
        public Vector2[] MemberGoals = Array.Empty<Vector2>();
        public bool HasMoveOrder;
        public int CenterMemberCount;
        public int FollowingCount;
        public int EngagedCount;
        public int RejoiningCount;
        public int SeparatedCount;
        public int MovingCount;

        public SquadState(int id, List<UnitId> members)
        {
            Id = id;
            Members = members;
        }
    }
}

internal sealed class SquadFlowFieldWorkspace
{
    private bool[] _passable = Array.Empty<bool>();
    private float[] _height = Array.Empty<float>();
    private int[] _heap = Array.Empty<int>();
    private int[] _heapPosition = Array.Empty<int>();
    private float[]? _keys;
    private int _heapCount;
    private int _width;
    private int _heightInCells;
    private long _terrainVersion = long.MinValue;

    public bool[] Passable => _passable;
    public float[] Heights => _height;
    public bool IsEmpty => _heapCount == 0;

    public void EnsureMap(WorldMap map)
    {
        int width = map.TileWidth;
        int height = map.TileHeight;
        int count = checked(width * height);
        if (_terrainVersion == map.TerrainVersion &&
            _width == width &&
            _heightInCells == height)
        {
            return;
        }

        _width = width;
        _heightInCells = height;
        if (_passable.Length != count)
        {
            _passable = new bool[count];
            _height = new float[count];
            _heap = new int[count];
            _heapPosition = new int[count];
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int cell = x + y * width;
                _passable[cell] = !map.IsNavigationBlocked(x, y) &&
                    map.GetSurfaceLayer(x, y) >= 0;
                _height[cell] = map.GetSurfaceHeight(x, y);
            }
        }

        _terrainVersion = map.TerrainVersion;
    }

    public void Begin(float[] keys, int cellCount)
    {
        _keys = keys;
        _heapCount = 0;
        Array.Fill(_heapPosition, -1, 0, cellCount);
    }

    public void PushOrDecrease(int cell)
    {
        int position = _heapPosition[cell];
        if (position < 0)
        {
            position = _heapCount++;
            _heap[position] = cell;
            _heapPosition[cell] = position;
        }

        SiftUp(position);
    }

    public int PopMin()
    {
        int result = _heap[0];
        _heapPosition[result] = -1;
        _heapCount--;
        if (_heapCount > 0)
        {
            int last = _heap[_heapCount];
            _heap[0] = last;
            _heapPosition[last] = 0;
            SiftDown(0);
        }

        return result;
    }

    private void SiftUp(int position)
    {
        int cell = _heap[position];
        while (position > 0)
        {
            int parent = (position - 1) >> 1;
            int parentCell = _heap[parent];
            if (_keys![parentCell] <= _keys[cell])
                break;

            _heap[position] = parentCell;
            _heapPosition[parentCell] = position;
            position = parent;
        }

        _heap[position] = cell;
        _heapPosition[cell] = position;
    }

    private void SiftDown(int position)
    {
        int cell = _heap[position];
        while (true)
        {
            int left = position * 2 + 1;
            if (left >= _heapCount)
                break;

            int right = left + 1;
            int child = right < _heapCount &&
                _keys![_heap[right]] < _keys[_heap[left]]
                    ? right
                    : left;
            int childCell = _heap[child];
            if (_keys![cell] <= _keys[childCell])
                break;

            _heap[position] = childCell;
            _heapPosition[childCell] = position;
            position = child;
        }

        _heap[position] = cell;
        _heapPosition[cell] = position;
    }
}

internal sealed class SquadFlowField
{
    private const float MaxStepHeight = 1.5f;
    private const float HeightCost = 0.15f;

    private static readonly int[] DirectionX = { 1, -1, 0, 0, 1, 1, -1, -1 };
    private static readonly int[] DirectionY = { 0, 0, 1, -1, 1, -1, 1, -1 };

    private float[] _distance = Array.Empty<float>();
    private byte[] _direction = Array.Empty<byte>();
    private int _width;
    private int _height;
    private int _goalCell = -1;
    private long _terrainVersion = long.MinValue;
    private bool _hasBuild;

    public bool IsReady { get; private set; }
    public long TerrainVersion => _terrainVersion;

    public void Invalidate()
    {
        _goalCell = -1;
        _terrainVersion = long.MinValue;
        _hasBuild = false;
        IsReady = false;
    }

    public bool EnsureBuilt(
        WorldMap map,
        Vector3 goal,
        SquadFlowFieldWorkspace workspace)
    {
        workspace.EnsureMap(map);
        int width = map.TileWidth;
        int height = map.TileHeight;
        int count = checked(width * height);
        int goalX = Math.Clamp((int)MathF.Floor(goal.X), 0, width - 1);
        int goalY = Math.Clamp((int)MathF.Floor(goal.Y), 0, height - 1);
        int requestedGoal = goalX + goalY * width;

        if (_hasBuild &&
            _terrainVersion == map.TerrainVersion &&
            _width == width &&
            _height == height)
        {
            return IsReady;
        }

        if (_distance.Length != count)
        {
            _distance = new float[count];
            _direction = new byte[count];
        }

        _width = width;
        _height = height;
        _terrainVersion = map.TerrainVersion;
        _goalCell = FindNearestPassableGoal(requestedGoal, goalX, goalY, workspace);
        IsReady = _goalCell >= 0;
        _hasBuild = true;
        if (!IsReady)
            return false;

        Array.Fill(_distance, float.PositiveInfinity, 0, count);
        Array.Clear(_direction, 0, count);
        workspace.Begin(_distance, count);
        _distance[_goalCell] = 0f;
        workspace.PushOrDecrease(_goalCell);

        while (!workspace.IsEmpty)
        {
            int cell = workspace.PopMin();
            int x = cell % width;
            int y = cell / width;

            for (int direction = 0; direction < 8; direction++)
            {
                int dx = DirectionX[direction];
                int dy = DirectionY[direction];
                int nx = x + dx;
                int ny = y + dy;
                if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                    continue;

                int next = nx + ny * width;
                if (!CanStep(cell, next, dx, dy, width, workspace))
                    continue;

                float stepCost = dx != 0 && dy != 0 ? 1.41421356f : 1f;
                stepCost += MathF.Abs(workspace.Heights[cell] - workspace.Heights[next]) *
                    HeightCost;
                float candidate = _distance[cell] + stepCost;
                if (candidate >= _distance[next] - 0.0001f)
                    continue;

                _distance[next] = candidate;
                _direction[next] = EncodeDirection(-dx, -dy);
                workspace.PushOrDecrease(next);
            }

            if (workspace.IsEmpty)
                break;
        }

        return true;
    }

    public bool TrySample(
        float x,
        float y,
        out Vector2 direction,
        out float distance)
    {
        direction = default;
        distance = float.PositiveInfinity;
        if (!IsReady || _width <= 0 || _height <= 0)
            return false;

        int x0 = Math.Clamp((int)MathF.Floor(x), 0, _width - 1);
        int y0 = Math.Clamp((int)MathF.Floor(y), 0, _height - 1);
        int x1 = Math.Min(x0 + 1, _width - 1);
        int y1 = Math.Min(y0 + 1, _height - 1);
        float tx = Math.Clamp(x - x0, 0f, 1f);
        float ty = Math.Clamp(y - y0, 0f, 1f);

        Vector2 sum = Vector2.Zero;
        AddDirection(x0 + y0 * _width, (1f - tx) * (1f - ty), ref sum);
        AddDirection(x1 + y0 * _width, tx * (1f - ty), ref sum);
        AddDirection(x0 + y1 * _width, (1f - tx) * ty, ref sum);
        AddDirection(x1 + y1 * _width, tx * ty, ref sum);

        int cell = x0 + y0 * _width;
        distance = _distance[cell];
        if (!float.IsFinite(distance))
            return false;

        float lengthSquared = sum.LengthSquared();
        if (lengthSquared < 0.0001f)
        {
            byte code = _direction[cell];
            if (code == 0)
                return false;
            direction = GetDirection(code);
            return true;
        }

        direction = sum / MathF.Sqrt(lengthSquared);
        return true;
    }

    private int FindNearestPassableGoal(
        int requestedGoal,
        int centerX,
        int centerY,
        SquadFlowFieldWorkspace workspace)
    {
        if (workspace.Passable[requestedGoal])
            return requestedGoal;

        for (int radius = 1; radius <= 8; radius++)
        {
            float bestDistance = float.PositiveInfinity;
            int bestCell = -1;
            for (int y = Math.Max(0, centerY - radius);
                 y <= Math.Min(_height - 1, centerY + radius); y++)
            {
                for (int x = Math.Max(0, centerX - radius);
                     x <= Math.Min(_width - 1, centerX + radius); x++)
                {
                    if (Math.Max(Math.Abs(x - centerX), Math.Abs(y - centerY)) != radius)
                        continue;

                    int cell = x + y * _width;
                    if (!workspace.Passable[cell])
                        continue;

                    float dx = x - centerX;
                    float dy = y - centerY;
                    float candidate = dx * dx + dy * dy;
                    if (candidate < bestDistance)
                    {
                        bestDistance = candidate;
                        bestCell = cell;
                    }
                }
            }

            if (bestCell >= 0)
                return bestCell;
        }

        return -1;
    }

    private static bool CanStep(
        int from,
        int to,
        int dx,
        int dy,
        int width,
        SquadFlowFieldWorkspace workspace)
    {
        if (!workspace.Passable[to] ||
            MathF.Abs(workspace.Heights[from] - workspace.Heights[to]) > MaxStepHeight)
        {
            return false;
        }

        if (dx != 0 && dy != 0)
        {
            int horizontal = from + dx;
            int vertical = from + dy * width;
            return workspace.Passable[horizontal] &&
                workspace.Passable[vertical] &&
                MathF.Abs(workspace.Heights[from] - workspace.Heights[horizontal]) <= MaxStepHeight &&
                MathF.Abs(workspace.Heights[from] - workspace.Heights[vertical]) <= MaxStepHeight;
        }

        return true;
    }

    private void AddDirection(int cell, float weight, ref Vector2 sum)
    {
        if (weight <= 0f || !float.IsFinite(_distance[cell]))
            return;

        byte code = _direction[cell];
        if (code != 0)
            sum += GetDirection(code) * weight;
    }

    private static byte EncodeDirection(int x, int y)
    {
        for (int i = 0; i < DirectionX.Length; i++)
        {
            if (DirectionX[i] == x && DirectionY[i] == y)
                return (byte)(i + 1);
        }

        return 0;
    }

    private static Vector2 GetDirection(byte code)
    {
        int index = code - 1;
        return index >= 0 && index < DirectionX.Length
            ? new Vector2(DirectionX[index], DirectionY[index]) *
                (1f / MathF.Sqrt(DirectionX[index] * DirectionX[index] +
                    DirectionY[index] * DirectionY[index]))
            : Vector2.Zero;
    }
}
