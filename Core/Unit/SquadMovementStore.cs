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
    long TerrainVersion,
    Vector2 Center,
    Vector2 Forward,
    float Spacing,
    Vector2 Velocity,
    float EnvelopeWidth,
    float EnvelopeDepth,
    int CoreMemberCount,
    bool HasMoveOrder);

/// <summary>Immutable render-thread view of a squad's cached Flow Field.</summary>
public readonly record struct SquadMovementDebugSnapshot(
    SquadMovementInformation Information,
    int Width,
    int Height,
    float[] Costs,
    byte[] Directions);

/// <summary>
/// Owns collective spatial intent while every pawn remains an autonomous physical unit.
/// Individual A* stays authoritative; the envelope supplies only soft steering constraints.
/// </summary>
public sealed class SquadMovementStore
{
    private const float RejoinBlendSeconds = 1.8f;
    private const float MaxCohesionCorrection = 0.62f;
    private const float AlignmentWeight = 0.12f;
    private const float ColumnGapScale = 1.35f;
    private const float FormationCorrectionLimit = 1.25f;
    private const float FormationAnchorWeight = 0.42f;
    private const int MaxFormationNeighborCandidates = 8;
    private const float MinimumFormationForwardGap = 0.35f;

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

    public void ClearMovementGroups(IReadOnlyList<UnitId> members)
    {
        if (members == null || members.Count == 0)
            return;

        HashSet<int> replacedSquads = new();
        for (int i = 0; i < members.Count; i++)
        {
            if (_membership.TryGetValue(members[i], out MemberLink link))
                replacedSquads.Add(link.SquadId);
        }

        foreach (int squadId in replacedSquads)
        {
            if (!_squads.Remove(squadId, out SquadState? squad))
                continue;

            for (int i = 0; i < squad.Members.Count; i++)
            {
                UnitId member = squad.Members[i];
                if (_membership.TryGetValue(member, out MemberLink link) &&
                    link.SquadId == squadId)
                {
                    _membership.Remove(member);
                }
            }
        }
    }

    public bool IssueGroupMoveOrder(
        UnitStore units,
        UnitHealthStore health,
        UnitAiStore ai,
        UnitNavigationSystem navigation,
        int squadId,
        IReadOnlyList<UnitId> requestedMembers,
        Vector3 destination,
        InfantryFormationType formation,
        bool keepFormationInCombat,
        WorldMap worldMap,
        ColumnMovementVariant columnVariant = ColumnMovementVariant.HistoricalSlots)
    {
        if (squadId <= 0 || requestedMembers == null || requestedMembers.Count == 0)
            return false;

        List<UnitId> members = new(requestedMembers.Count);
        HashSet<UnitId> unique = new();
        for (int i = 0; i < requestedMembers.Count; i++)
        {
            UnitId id = requestedMembers[i];
            if (id.IsValid &&
                unique.Add(id) &&
                units.TryGetIndex(id, out int index) &&
                health.OverallHitPoints[index] > 0f)
            {
                members.Add(id);
            }
        }

        if (members.Count == 0)
            return false;

        // A new movement group replaces any older temporary cohort that overlaps.
        // Persistent squad UI/membership remains independent of movement orders.
        ClearMovementGroups(members);
        if (_squads.Remove(squadId, out SquadState? previousSquad))
        {
            for (int i = 0; i < previousSquad.Members.Count; i++)
                _membership.Remove(previousSquad.Members[i]);
        }

        SquadState squad = new(squadId, members);
        _squads.Add(squadId, squad);
        for (int i = 0; i < members.Count; i++)
        {
            _membership[members[i]] = new MemberLink(squadId, i);
            EnsureMemberIdentity(units, members[i].Index);
        }

        Vector2 center = Vector2.Zero;
        float spacing = 1.15f;
        float slowestSpeed = float.PositiveInfinity;
        for (int i = 0; i < members.Count; i++)
        {
            UnitId id = members[i];
            int index = id.Index;
            Vector3 position = units.Position[index];
            center += new Vector2(position.X, position.Y);
            spacing = MathF.Max(spacing, MathF.Max(units.Width[index], units.Length[index]) * 1.15f);
            float effectiveSpeed = MathF.Max(0.1f, units.MoveSpeed[index]);
            if (units.Posture[index] == UnitPosture.Lying)
                effectiveSpeed *= 0.45f;
            slowestSpeed = MathF.Min(slowestSpeed, effectiveSpeed);
        }

        center /= members.Count;
        Vector2 goal = new(
            Math.Clamp(destination.X, 0.5f, worldMap.MaxTileX - 0.5f),
            Math.Clamp(destination.Y, 0.5f, worldMap.MaxTileY - 0.5f));
        Vector2 forward = goal - center;
        if (forward.LengthSquared() < 0.0001f)
            forward = Vector2.UnitX;
        else
            forward = Vector2.Normalize(forward);
        // Line, wedge and the historical column test retain individual landing
        // positions. Each pawn gets its own footprint-valid A* goal.
        bool usesIndividualFormationGoals =
            formation is InfantryFormationType.Line or InfantryFormationType.Wedge ||
            (formation == InfantryFormationType.Column &&
             columnVariant == ColumnMovementVariant.HistoricalSlots);
        Vector2 side = new(-forward.Y, forward.X);
        Vector2 meanOffset = Vector2.Zero;
        if (usesIndividualFormationGoals)
        {
            for (int i = 0; i < members.Count; i++)
                meanOffset += InfantryFormation.GetOffset(i, members.Count, formation, spacing);
            meanOffset /= members.Count;
        }

        HashSet<int> reservedGoals = usesIndividualFormationGoals
            ? new HashSet<int>()
            : null!;
        Vector2[] goals = new Vector2[members.Count];
        Vector2[] memberOffsets = new Vector2[members.Count];
        for (int i = 0; i < members.Count; i++)
        {
            UnitId id = members[i];
            Vector2 localOffset = usesIndividualFormationGoals
                ? InfantryFormation.GetOffset(i, members.Count, formation, spacing) - meanOffset
                : Vector2.Zero;
            Vector2 requestedGoal = goal +
                side * localOffset.X -
                forward * localOffset.Y;
            requestedGoal.X = Math.Clamp(
                requestedGoal.X, 0.5f, worldMap.MaxTileX - 0.5f);
            requestedGoal.Y = Math.Clamp(
                requestedGoal.Y, 0.5f, worldMap.MaxTileY - 0.5f);

            if (!navigation.TryResolveNavigableGoal(
                    units,
                    id.Index,
                    worldMap,
                    requestedGoal,
                    usesIndividualFormationGoals ? reservedGoals : null,
                    out Vector2 terminal))
            {
                terminal = ResolveNavigablePoint(
                    worldMap,
                    requestedGoal,
                    usesIndividualFormationGoals ? reservedGoals : null);
            }

            goals[i] = terminal;
            memberOffsets[i] = localOffset;
        }

        squad.MemberOffsets = memberOffsets;
        squad.MemberGoals = new Vector2[members.Count];
        for (int i = 0; i < members.Count; i++)
        {
            UnitId id = members[i];
            Vector2 terminal = goals[i];
            int tileX = Math.Clamp((int)MathF.Floor(terminal.X), 0, worldMap.MaxTileX);
            int tileY = Math.Clamp((int)MathF.Floor(terminal.Y), 0, worldMap.MaxTileY);
            Vector3 memberTarget = new(
                terminal.X,
                terminal.Y,
                worldMap.GetSurfaceHeight(tileX, tileY));

            squad.MemberGoals[i] = terminal;
            ai.SetPlayerOrder(id.Index, UnitAiOrderType.Move, memberTarget);
            ai.ClearGoal(id.Index);
            units.SetTarget(id, memberTarget);
            SetMemberState(units, id.Index, SquadMemberMovementState.Following);
        }

        squad.AnchorTarget = goal;
        squad.ColumnVariant = formation == InfantryFormationType.Column
            ? columnVariant
            : ColumnMovementVariant.HistoricalSlots;
        squad.Objective = new Vector3(goal.X, goal.Y, destination.Z);
        squad.Center = center;
        squad.HasCenter = true;
        squad.Forward = forward;
        squad.Spacing = spacing;
        squad.Formation = formation;
        squad.Rigidity = formation == InfantryFormationType.Free
            ? SquadFormationRigidity.Free
            : SquadFormationRigidity.Organized;
        squad.HasMoveOrder = true;
        squad.IsAStarFormation = true;
        squad.EnforceGroupSpeed =
            formation is not (InfantryFormationType.None or InfantryFormationType.Column);
        squad.KeepFormationInCombat = keepFormationInCombat;
        squad.SlowestMoveSpeed = slowestSpeed;
        squad.Phase = SquadMovementPhase.Advancing;
        squad.FlowField.Invalidate();
        return true;
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

        Vector2 terminal = ResolveNavigablePoint(worldMap, goal);
        squad.MemberOffsets = new Vector2[squad.Members.Count];
        squad.MemberGoals = new Vector2[squad.Members.Count];

        for (int i = 0; i < activeMembers.Count; i++)
        {
            (UnitId id, int index, int slot) = activeMembers[i];
            Vector2 localOffset = Vector2.Zero;

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

        squad.AnchorTarget = goal;
        squad.Objective = new Vector3(goal.X, goal.Y, destination.Z);
        squad.Center = center;
        squad.HasCenter = true;
        squad.Forward = forward;
        squad.Spacing = spacing;
        squad.Formation = formation;
        squad.Rigidity = SquadFormationRigidity.Free;
        squad.HasMoveOrder = true;
        squad.IsAStarFormation = false;
        squad.EnforceGroupSpeed = false;
        squad.KeepFormationInCombat = false;
        squad.SlowestMoveSpeed = float.PositiveInfinity;
        squad.Phase = SquadMovementPhase.Advancing;
        squad.FlowField.Invalidate();
        return true;
    }

    public void BeginUpdate(
        UnitStore units,
        UnitHealthStore? health,
        UnitAiStore? ai,
        WorldMap worldMap,
        float deltaTime,
        UnitNavigationSystem navigation,
        UnitSuppressionStore? suppression)
    {
        EnsureMemberCapacity(units.Capacity);

        foreach (SquadState squad in _squads.Values)
        {
            squad.CenterAccumulator = Vector2.Zero;
            squad.FollowingCount = 0;
            squad.EngagedCount = 0;
            squad.RejoiningCount = 0;
            squad.SeparatedCount = 0;
            squad.MovingCount = 0;
            squad.SlowestMoveSpeed = float.PositiveInfinity;

            Vector2 allCenter = Vector2.Zero;
            Vector2 previousCoreCenter = Vector2.Zero;
            int allMemberCount = 0;
            int previousCoreCount = 0;

            // Use the previous core as the reference, keeping newly detached
            // tactical members from shifting the collective center.
            for (int slot = 0; slot < squad.Members.Count; slot++)
            {
                UnitId id = squad.Members[slot];
                if (!units.TryGetIndex(id, out int unit) ||
                    (health != null && health.OverallHitPoints[unit] <= 0f))
                {
                    continue;
                }

                EnsureMemberIdentity(units, unit);
                Vector3 position = units.Position[unit];
                Vector2 planarPosition = new(position.X, position.Y);
                allCenter += planarPosition;
                allMemberCount++;

                if (_memberStates[unit] == SquadMemberMovementState.Following)
                {
                    previousCoreCenter += planarPosition;
                    previousCoreCount++;
                }

                if (units.HasTarget[unit])
                    squad.MovingCount++;
            }

            Vector2 referenceCenter = previousCoreCount > 0
                ? previousCoreCenter / previousCoreCount
                : squad.HasCenter
                    ? squad.Center
                    : allMemberCount > 0
                        ? allCenter / allMemberCount
                        : squad.Center;

            Vector2 coreVelocity = Vector2.Zero;
            int coreMemberCount = 0;

            // Tactical actions change steering participation, not persistent
            // squad membership or the shared movement objective.
            for (int slot = 0; slot < squad.Members.Count; slot++)
            {
                UnitId id = squad.Members[slot];
                if (!units.TryGetIndex(id, out int unit) ||
                    (health != null && health.OverallHitPoints[unit] <= 0f))
                {
                    continue;
                }

                bool tacticallyDetached =
                    IsCombatEngaged(units, ai, suppression, unit) ||
                    IsDetachedFromCollective(units, ai, unit);

                if (tacticallyDetached)
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

                Vector3 worldPosition = units.Position[unit];
                Vector2 position = new(worldPosition.X, worldPosition.Y);
                float distanceFromGroup = Vector2.Distance(position, referenceCenter);
                float rejoinDistance = MathF.Max(
                    8f,
                    MathF.Max(squad.EnvelopeWidth, squad.EnvelopeDepth) +
                    squad.Spacing * 2.5f);

                if (distanceFromGroup > rejoinDistance)
                {
                    _memberStates[unit] = SquadMemberMovementState.Separated;
                }
                else if (_memberStates[unit] == SquadMemberMovementState.Separated)
                {
                    _memberStates[unit] = SquadMemberMovementState.Rejoining;
                    _rejoinElapsed[unit] = 0f;
                }

                if (_memberStates[unit] == SquadMemberMovementState.Rejoining)
                {
                    bool insideCore = distanceFromGroup <= MathF.Max(
                        squad.Spacing * 2f,
                        MathF.Max(squad.EnvelopeWidth, squad.EnvelopeDepth) * 0.65f);
                    if (!insideCore)
                    {
                        squad.RejoiningCount++;
                        continue;
                    }

                    _rejoinElapsed[unit] += Math.Clamp(deltaTime, 0f, 0.25f);
                    if (_rejoinElapsed[unit] >= RejoinBlendSeconds)
                    {
                        _memberStates[unit] = SquadMemberMovementState.Following;
                        _rejoinElapsed[unit] = 0f;
                        squad.FollowingCount++;
                    }
                    else
                    {
                        squad.RejoiningCount++;
                    }
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
                coreVelocity += new Vector2(
                    units.Velocity[unit].X,
                    units.Velocity[unit].Y);
                coreMemberCount++;

                if (squad.IsAStarFormation)
                {
                    float memberSpeed = MathF.Max(0.1f, units.MoveSpeed[unit]);
                    if (units.Posture[unit] == UnitPosture.Lying)
                        memberSpeed *= 0.45f;
                    squad.SlowestMoveSpeed = MathF.Min(
                        squad.SlowestMoveSpeed, memberSpeed);
                }
            }

            squad.CoreMemberCount = coreMemberCount;
            if (coreMemberCount > 0)
            {
                Vector2 observedCenter = squad.CenterAccumulator / coreMemberCount;
                float coreInfluence = allMemberCount > 0
                    ? Math.Clamp((float)coreMemberCount / allMemberCount, 0.15f, 1f)
                    : 1f;
                float centerBlend = Math.Clamp(
                    deltaTime * 5f * coreInfluence,
                    0f,
                    1f);
                squad.Center = squad.HasCenter
                    ? Vector2.Lerp(squad.Center, observedCenter, centerBlend)
                    : observedCenter;

                Vector2 observedVelocity = coreVelocity / coreMemberCount;
                squad.CenterVelocity = Vector2.Lerp(
                    squad.CenterVelocity,
                    observedVelocity,
                    Math.Clamp(deltaTime * 3f * coreInfluence, 0f, 1f));
                squad.HasCenter = true;
            }
            else
            {
                squad.CenterVelocity = Vector2.Lerp(
                    squad.CenterVelocity,
                    Vector2.Zero,
                    Math.Clamp(deltaTime * 1.2f, 0f, 1f));
                if (!squad.HasCenter && allMemberCount > 0)
                {
                    squad.Center = allCenter / allMemberCount;
                    squad.HasCenter = true;
                }
            }

            Vector2 toObjective = squad.AnchorTarget - squad.Center;
            Vector2 desiredForward = squad.CenterVelocity.LengthSquared() > 0.04f
                ? squad.CenterVelocity
                : squad.HasMoveOrder
                    ? toObjective
                    : Vector2.Zero;

            if (desiredForward.LengthSquared() > 0.0001f)
            {
                desiredForward = Vector2.Normalize(desiredForward);
                Vector2 smoothedForward = Vector2.Lerp(
                    squad.Forward,
                    desiredForward,
                    Math.Clamp(deltaTime * 6f, 0f, 1f));
                squad.Forward = smoothedForward.LengthSquared() > 0.0001f
                    ? Vector2.Normalize(smoothedForward)
                    : desiredForward;
            }

            // A soft spatial envelope replaces continuous pulling toward fixed
            // member slots. It stretches along travel during the march and opens
            // laterally during contact.
            float effectiveCoreCount = Math.Max(1, coreMemberCount);
            float baseRadius = MathF.Max(
                squad.Spacing * 1.75f,
                MathF.Sqrt(effectiveCoreCount) * squad.Spacing * 1.1f);
            if (squad.Rigidity == SquadFormationRigidity.Organized)
                baseRadius *= 0.92f;

            bool inContact = squad.EngagedCount > 0;
            float contactScale = !inContact
                ? 1f
                : squad.KeepFormationInCombat ? 1.35f : 1.75f;

            // The live envelope is the shape the group travels in:
            // Column is narrow and deep, Line is wide and shallow,
            // Free is a loose rounded crowd.
            switch (squad.Formation)
            {
                case InfantryFormationType.Column:
                    squad.EnvelopeWidth = MathF.Max(0.7f, squad.Spacing * 0.75f) *
                        contactScale;
                    squad.EnvelopeDepth = MathF.Max(
                        baseRadius * 1.2f,
                        effectiveCoreCount * squad.Spacing * 0.72f) *
                        (inContact ? 1.12f : 1f);
                    break;

                case InfantryFormationType.Line:
                    squad.EnvelopeWidth = MathF.Max(
                        baseRadius * 1.2f,
                        effectiveCoreCount * squad.Spacing * 0.72f) *
                        contactScale;
                    squad.EnvelopeDepth = MathF.Max(0.65f, squad.Spacing * 0.65f) *
                        (inContact ? 1.25f : 1f);
                    break;

                case InfantryFormationType.Wedge:
                {
                    float halfWidth = baseRadius;
                    float halfDepth = baseRadius;
                    for (int slot = 0; slot < squad.MemberOffsets.Length; slot++)
                    {
                        Vector2 offset = squad.MemberOffsets[slot];
                        halfWidth = MathF.Max(
                            halfWidth, MathF.Abs(offset.X) + squad.Spacing * 0.5f);
                        halfDepth = MathF.Max(
                            halfDepth, MathF.Abs(offset.Y) + squad.Spacing * 0.5f);
                    }

                    // A wedge naturally has a larger diagonal footprint than a
                    // generic crowd. Its wing members must remain part of the core.
                    squad.EnvelopeWidth = halfWidth * contactScale;
                    squad.EnvelopeDepth = halfDepth * contactScale;
                    break;
                }

                case InfantryFormationType.Free:
                    // Free movement is a compact moving crowd, not a dispersed cloud.
                    float crowdRadius = MathF.Max(
                        squad.Spacing * 1.3f,
                        MathF.Sqrt(effectiveCoreCount) * squad.Spacing * 0.55f) *
                        contactScale;
                    squad.EnvelopeWidth = crowdRadius;
                    squad.EnvelopeDepth = crowdRadius;
                    break;

                default:
                    squad.EnvelopeWidth = baseRadius * contactScale;
                    squad.EnvelopeDepth = baseRadius * (inContact ? 1.25f : 1.55f);
                    break;
            }

            Vector2 toAnchor = squad.AnchorTarget - squad.Center;
            bool reachedAnchor =
                toAnchor.LengthSquared() <= 0.65f * 0.65f &&
                squad.CenterVelocity.LengthSquared() <= 0.18f * 0.18f;
            bool waitsForIndividualGoals = squad.IsAStarFormation;
            if (squad.HasMoveOrder &&
                squad.EngagedCount == 0 &&
                squad.RejoiningCount == 0 &&
                squad.SeparatedCount == 0 &&
                (!waitsForIndividualGoals || squad.MovingCount == 0) &&
                reachedAnchor)
            {
                CompleteSquadMove(squad, units, ai, navigation);
            }

            if (squad.EngagedCount > 0)
                squad.Phase = SquadMovementPhase.Contact;
            else if (squad.RejoiningCount > 0 || squad.SeparatedCount > 0)
                squad.Phase = SquadMovementPhase.Regrouping;
            else if (!squad.HasMoveOrder)
                squad.Phase = squad.Phase == SquadMovementPhase.Arrived
                    ? SquadMovementPhase.Arrived
                    : SquadMovementPhase.Idle;
            else if (squad.MovingCount == 0)
                squad.Phase = SquadMovementPhase.Arrived;
            else
                squad.Phase = SquadMovementPhase.Advancing;
        }

        BuildFormationGraph(units, health, ai);
    }

    private void CompleteSquadMove(
        SquadState squad,
        UnitStore units,
        UnitAiStore? ai,
        UnitNavigationSystem navigation)
    {
        for (int slot = 0; slot < squad.Members.Count; slot++)
        {
            UnitId member = squad.Members[slot];
            if (!units.TryGetIndex(member, out int unit))
                continue;

            ai?.SetPlayerOrder(unit, UnitAiOrderType.Stop, units.Position[unit]);
            ai?.ClearGoal(unit);
            units.ClearTarget(member);
            navigation.ClearRoute(unit);
        }

        squad.HasMoveOrder = false;
        squad.IsAStarFormation = false;
        squad.EnforceGroupSpeed = false;
        squad.KeepFormationInCombat = false;
        squad.MovingCount = 0;
        squad.FormationBootstrap = false;
        squad.Phase = SquadMovementPhase.Arrived;
    }

    private void BuildFormationGraph(
        UnitStore units,
        UnitHealthStore? health,
        UnitAiStore? ai)
    {
        foreach (SquadState squad in _squads.Values)
        {
            int memberCount = squad.Members.Count;
            squad.FormationMemberCount = 0;
            squad.EnsureFormationBuffers(memberCount);
            Array.Fill(squad.FormationNeighborBefore, -1, 0, memberCount);
            Array.Fill(squad.FormationNeighborAfter, -1, 0, memberCount);
            Array.Clear(squad.FormationLateralOffsets, 0, memberCount);

            if (!squad.HasMoveOrder ||
                squad.Formation != InfantryFormationType.Line)
            {
                squad.FormationBootstrap = false;
                continue;
            }

            Vector2 side = new(-squad.Forward.Y, squad.Forward.X);
            float minForward = float.PositiveInfinity;
            float maxForward = float.NegativeInfinity;

            // Build one compact live cohort. Tactical detachments retain membership
            // but do not distort the geometry of the moving core.
            for (int slot = 0; slot < memberCount; slot++)
            {
                UnitId id = squad.Members[slot];
                if (!units.TryGetIndex(id, out int unit) ||
                    (health != null && health.OverallHitPoints[unit] <= 0f) ||
                    (uint)slot >= (uint)squad.MemberGoals.Length ||
                    IsDetachedFromCollective(units, ai, unit) ||
                    IsMemberTacticallyEngaged(id) ||
                    (_memberOwners[unit] == id &&
                     _memberStates[unit] == SquadMemberMovementState.Separated))
                {
                    continue;
                }

                Vector3 position = units.Position[unit];
                Vector2 memberPosition = new(position.X, position.Y);
                Vector2 toGoal = squad.MemberGoals[slot] - memberPosition;
                bool reachedSharedTerminal = !units.HasTarget[unit] &&
                    toGoal.LengthSquared() <= 0.85f * 0.85f;
                bool followingGroupOrder = units.HasTarget[unit] &&
                    HasMatchingGroupMoveOrder(ai, unit, squad.MemberGoals[slot]);
                if (!reachedSharedTerminal && !followingGroupOrder)
                    continue;

                Vector2 fromCenter = memberPosition - squad.Center;
                float forward = Vector2.Dot(fromCenter, squad.Forward);
                minForward = MathF.Min(minForward, forward);
                maxForward = MathF.Max(maxForward, forward);
                squad.FormationSortSlots[squad.FormationMemberCount++] = slot;
            }

            int activeCount = squad.FormationMemberCount;
            if (activeCount < 2)
            {
                squad.FormationBootstrap = false;
                continue;
            }

            if (squad.Formation == InfantryFormationType.Column)
            {
                float desiredGap = MathF.Max(1.3f, squad.Spacing * ColumnGapScale);
                float targetDepth = desiredGap * (activeCount - 1);
                float currentDepth = maxForward - minForward;
                float threshold = squad.FormationBootstrap ? 0.68f : 0.28f;
                squad.FormationBootstrap = currentDepth < targetDepth * threshold;
            }
            else
            {
                squad.FormationBootstrap = false;
            }

            // A column is always ordered by actual forward projection. A follower
            // never receives a neighbour that happens to be next in a side-sorted
            // array but is physically behind it. Line ranks remain lateral.
            for (int i = 0; i < activeCount; i++)
            {
                int slot = squad.FormationSortSlots[i];
                if (!units.TryGetIndex(squad.Members[slot], out int unit))
                    continue;

                Vector3 position = units.Position[unit];
                Vector2 fromCenter = new(
                    position.X - squad.Center.X,
                    position.Y - squad.Center.Y);
                float forward = Vector2.Dot(fromCenter, squad.Forward);
                float lateral = Vector2.Dot(fromCenter, side);
                float primary = squad.Formation == InfantryFormationType.Column
                    ? forward
                    : lateral;
                float secondary = squad.Formation == InfantryFormationType.Column
                    ? lateral
                    : forward;

                squad.FormationSortKeys[i] =
                    primary + secondary * 0.0001f + slot * 0.000001f;
            }

            Array.Sort(
                squad.FormationSortKeys,
                squad.FormationSortSlots,
                0,
                activeCount);

            if (squad.Formation == InfantryFormationType.Line)
            {
                float desiredSideGap = MathF.Max(1.3f, squad.Spacing * 1.2f);
                for (int i = 0; i < activeCount; i++)
                {
                    int slot = squad.FormationSortSlots[i];
                    squad.FormationLateralOffsets[slot] =
                        (i - (activeCount - 1) * 0.5f) * desiredSideGap;
                    if (i > 0)
                        squad.FormationNeighborBefore[slot] = squad.FormationSortSlots[i - 1];
                    if (i + 1 < activeCount)
                        squad.FormationNeighborAfter[slot] = squad.FormationSortSlots[i + 1];
                }

                continue;
            }

            // Find the closest actual member ahead, with a bounded forward scan.
            // The scan is linear in the small sorted neighbour window, not all pairs.
            float maximumForwardGap = MathF.Max(4f, squad.Spacing * 4.5f);
            for (int i = 0; i < activeCount; i++)
            {
                int slot = squad.FormationSortSlots[i];
                if (!units.TryGetIndex(squad.Members[slot], out int unit))
                    continue;

                Vector3 current3 = units.Position[unit];
                Vector2 current = new(current3.X, current3.Y);
                float bestDistanceSquared = float.PositiveInfinity;
                int bestAheadSlot = -1;
                int end = Math.Min(activeCount, i + 1 + MaxFormationNeighborCandidates);

                for (int j = i + 1; j < end; j++)
                {
                    int candidateSlot = squad.FormationSortSlots[j];
                    if (!units.TryGetIndex(squad.Members[candidateSlot], out int candidate))
                        continue;

                    Vector3 candidate3 = units.Position[candidate];
                    Vector2 delta = new(candidate3.X - current.X, candidate3.Y - current.Y);
                    float forwardGap = Vector2.Dot(delta, squad.Forward);
                    if (forwardGap < MinimumFormationForwardGap)
                        continue;
                    if (forwardGap > maximumForwardGap)
                        break;

                    float distanceSquared = delta.LengthSquared();
                    if (distanceSquared >= bestDistanceSquared)
                        continue;

                    bestDistanceSquared = distanceSquared;
                    bestAheadSlot = candidateSlot;
                }

                squad.FormationNeighborAfter[slot] = bestAheadSlot;
            }
        }
    }

    public bool TryAdjustAStarFormationWaypoint(
        UnitStore units,
        int unitIndex,
        Vector3 position,
        Vector2 pathWaypoint,
        UnitAiStore? ai,
        out Vector2 adjustedWaypoint)
    {
        adjustedWaypoint = pathWaypoint;
        if ((uint)unitIndex >= (uint)units.Capacity)
            return false;

        UnitId id = units.GetId(unitIndex);
        if (!_membership.TryGetValue(id, out MemberLink link) ||
            !_squads.TryGetValue(link.SquadId, out SquadState? squad) ||
            !squad.HasMoveOrder ||
            squad.Formation is InfantryFormationType.None ||
            !units.HasTarget[unitIndex] ||
            (uint)link.Slot >= (uint)squad.MemberGoals.Length ||
            (uint)link.Slot >= (uint)squad.MemberOffsets.Length ||
            !HasMatchingGroupMoveOrder(ai, unitIndex, squad.MemberGoals[link.Slot]) ||
            IsDetachedFromCollective(units, ai, unitIndex) ||
            IsMemberTacticallyEngaged(id))
        {
            return false;
        }

        Vector2 current = new(position.X, position.Y);
        Vector2 pathDelta = pathWaypoint - current;
        float pathDistance = pathDelta.Length();
        Vector2 routeDirection = pathDistance > 0.05f
            ? pathDelta / pathDistance
            : squad.Forward;
        Vector2 side = new(-squad.Forward.Y, squad.Forward.X);
        Vector2 fromCenter = current - squad.Center;
        float forwardDistance = Vector2.Dot(fromCenter, squad.Forward);
        float lateralDistance = Vector2.Dot(fromCenter, side);

        bool inContact = squad.EngagedCount > 0;
        bool rejoining =
            _memberOwners[unitIndex] == id &&
            _memberStates[unitIndex] is SquadMemberMovementState.Rejoining or
                SquadMemberMovementState.Separated;

        Vector2 shapeForce = Vector2.Zero;
        float shapeWeight;

        if (rejoining)
        {
            // The virtual anchor brings a detached pawn back without changing
            // its identity or assigning it a permanent formation slot.
            Vector2 toAnchor = squad.Center - current;
            if (toAnchor.LengthSquared() <= 0.04f)
                return false;

            shapeForce = Vector2.Normalize(toAnchor);
            shapeWeight = 0.58f;
        }
        else if (squad.Formation == InfantryFormationType.Column)
        {
            // Column movement uses independent destination slots only.
            return false;
        }
        else if (squad.Formation is InfantryFormationType.Line or InfantryFormationType.Wedge)
        {
            // Their A* destinations already encode the requested shape.
            return false;
        }
        else if (squad.Formation == InfantryFormationType.Free)
        {
            // Cohesion uses one predicted virtual anchor. Pawns keep individual
            // A* routes and collision avoidance; this only pulls outer members
            // inward enough to make the moving group read as one crowd.
            float preferredRadius = MathF.Max(
                squad.Spacing * 1.3f,
                MathF.Sqrt(Math.Max(1, squad.CoreMemberCount)) * squad.Spacing * 0.5f);
            Vector2 predictedCenter = squad.Center + squad.CenterVelocity * 0.25f;
            Vector2 toAnchor = predictedCenter - current;
            float anchorDistance = toAnchor.Length();
            float cohesionStart = preferredRadius * 0.72f;
            if (anchorDistance <= cohesionStart || anchorDistance <= 0.05f)
                return false;

            float excessDistance = anchorDistance - cohesionStart;
            shapeForce = toAnchor / anchorDistance;
            shapeWeight = Math.Clamp(
                FormationAnchorWeight + excessDistance * 0.075f,
                FormationAnchorWeight,
                0.62f);
        }
        else
        {
            return false;
        }

        if (shapeForce.LengthSquared() < 0.0025f)
            return false;

        if (squad.CenterVelocity.LengthSquared() > 0.04f)
        {
            Vector2 groupDirection = Vector2.Normalize(squad.CenterVelocity);
            if (Vector2.Dot(groupDirection, routeDirection) > 0.1f)
                shapeForce += groupDirection * AlignmentWeight;
        }

        // Adaptive weights: when A* turns around terrain, let its validated
        // route dominate. Combat also relaxes the body unless the order says
        // explicitly to preserve formation in contact.
        float routeAgreement = Vector2.Dot(routeDirection, squad.Forward);
        float obstacleAdaptation = Math.Clamp(
            (routeAgreement + 0.2f) / 0.9f,
            0.2f,
            1f);
        float contactAdaptation = !inContact
            ? 1f
            : squad.KeepFormationInCombat ? 0.72f : 0.42f;
        shapeWeight *= obstacleAdaptation * contactAdaptation;
        shapeWeight = Math.Clamp(shapeWeight, 0f, 0.82f);

        float shapeLengthSquared = shapeForce.LengthSquared();
        if (shapeLengthSquared < 0.0001f || shapeWeight <= 0.001f)
            return false;

        Vector2 shapeDirection = shapeForce / MathF.Sqrt(shapeLengthSquared);
        Vector2 blendedDirection =
            routeDirection * (1f - shapeWeight) +
            shapeDirection * shapeWeight;
        float blendedLengthSquared = blendedDirection.LengthSquared();
        if (blendedLengthSquared < 0.0001f)
            blendedDirection = routeDirection;
        else
            blendedDirection /= MathF.Sqrt(blendedLengthSquared);

        float lookAhead = pathDistance > 0.05f
            ? Math.Clamp(pathDistance, 0.75f, 3.2f)
            : 1f;
        adjustedWaypoint = current + blendedDirection * lookAhead;
        return true;
    }

    public bool TryGetAStarFormationSpeedLimit(
        UnitStore units,
        UnitId member,
        UnitAiStore? ai,
        out float speedLimit)
    {
        speedLimit = 0f;
        if (!member.IsValid ||
            !_membership.TryGetValue(member, out MemberLink link) ||
            !_squads.TryGetValue(link.SquadId, out SquadState? squad) ||
            !squad.HasMoveOrder ||
            !squad.IsAStarFormation ||
            !squad.EnforceGroupSpeed ||
            squad.Formation == InfantryFormationType.Column ||
            (uint)link.Slot >= (uint)squad.MemberGoals.Length ||
            !HasMatchingGroupMoveOrder(ai, member.Index, squad.MemberGoals[link.Slot]) ||
            IsDetachedFromCollective(units, ai, member.Index) ||
            IsMemberTacticallyEngaged(member) ||
            !float.IsFinite(squad.SlowestMoveSpeed))
        {
            return false;
        }

        speedLimit = squad.SlowestMoveSpeed;
        return speedLimit > 0f;
    }

    public bool TryGetFormationSpeedLimit(
        UnitStore units,
        UnitId member,
        UnitAiStore? ai,
        float currentSpeedLimit,
        out float speedLimit)
    {
        speedLimit = currentSpeedLimit;
        if (!member.IsValid ||
            !units.TryGetIndex(member, out int currentUnit) ||
            !_membership.TryGetValue(member, out MemberLink link) ||
            !_squads.TryGetValue(link.SquadId, out SquadState? squad) ||
            !squad.HasMoveOrder ||
            !squad.IsAStarFormation ||
            squad.Formation == InfantryFormationType.Column ||
            (uint)link.Slot >= (uint)squad.MemberGoals.Length ||
            !HasMatchingGroupMoveOrder(ai, currentUnit, squad.MemberGoals[link.Slot]) ||
            IsDetachedFromCollective(units, ai, currentUnit) ||
            IsMemberTacticallyEngaged(member) ||
            (uint)link.Slot >= (uint)squad.FormationNeighborAfter.Length)
        {
            return false;
        }

        int aheadSlot = squad.FormationNeighborAfter[link.Slot];
        if ((uint)aheadSlot >= (uint)squad.Members.Count ||
            !units.TryGetIndex(squad.Members[aheadSlot], out int ahead))
        {
            return false;
        }

        Vector3 currentPosition = units.Position[currentUnit];
        Vector3 aheadPosition = units.Position[ahead];
        Vector2 gapVector = new(
            aheadPosition.X - currentPosition.X,
            aheadPosition.Y - currentPosition.Y);
        float gap = Vector2.Dot(gapVector, squad.Forward);
        if (gap <= 0.1f)
            return false;

        float desiredGap = MathF.Max(
            squad.Spacing * 1.2f,
            (units.Radius[currentUnit] + units.Radius[ahead]) * ColumnGapScale);
        if (gap >= desiredGap * 1.25f)
            return false;

        Vector3 aheadVelocity3 = units.Velocity[ahead];
        float aheadSpeed = Vector2.Dot(
            new Vector2(aheadVelocity3.X, aheadVelocity3.Y),
            squad.Forward);
        float allowedSpeed = MathF.Max(
            0.15f,
            aheadSpeed + (gap - desiredGap) * 0.7f);
        speedLimit = MathF.Min(currentSpeedLimit, allowedSpeed);
        return speedLimit < currentSpeedLimit;
    }

    public bool IsExecutingCollectiveMove(
        UnitStore units,
        UnitId member,
        UnitAiStore? ai)
    {
        if (!member.IsValid ||
            !units.TryGetIndex(member, out int unit) ||
            !units.HasTarget[unit] ||
            !_membership.TryGetValue(member, out MemberLink link) ||
            !_squads.TryGetValue(link.SquadId, out SquadState? squad) ||
            !squad.HasMoveOrder ||
            !squad.IsAStarFormation ||
            (uint)link.Slot >= (uint)squad.MemberGoals.Length ||
            !HasMatchingGroupMoveOrder(ai, unit, squad.MemberGoals[link.Slot]) ||
            IsDetachedFromCollective(units, ai, unit) ||
            IsMemberTacticallyEngaged(member))
        {
            return false;
        }

        return true;
    }

    public bool HasIndividualFormationGoal(UnitId member)
    {
        return member.IsValid &&
            _membership.TryGetValue(member, out MemberLink link) &&
            _squads.TryGetValue(link.SquadId, out SquadState? squad) &&
            squad.HasMoveOrder &&
            squad.IsAStarFormation;
    }

    public bool TryGetMemberSquadId(UnitId member, out int squadId)
    {
        if (member.IsValid &&
            _membership.TryGetValue(member, out MemberLink link) &&
            _squads.ContainsKey(link.SquadId))
        {
            squadId = link.SquadId;
            return true;
        }

        squadId = 0;
        return false;
    }

    public bool TryGetMemberFormationData(
        UnitId member,
        out Vector2 preferredOffset,
        out Vector2 terminalGoal)
    {
        preferredOffset = Vector2.Zero;
        terminalGoal = Vector2.Zero;

        if (!member.IsValid ||
            !_membership.TryGetValue(member, out MemberLink link) ||
            !_squads.TryGetValue(link.SquadId, out SquadState? squad) ||
            !squad.HasMoveOrder ||
            (uint)link.Slot >= (uint)squad.MemberOffsets.Length ||
            (uint)link.Slot >= (uint)squad.MemberGoals.Length)
        {
            return false;
        }

        preferredOffset = squad.MemberOffsets[link.Slot];
        terminalGoal = squad.MemberGoals[link.Slot];
        return true;
    }

    public SquadMovementDebugSnapshot[] CaptureDebugSnapshots()
    {
        SquadMovementDebugSnapshot[] snapshots =
            new SquadMovementDebugSnapshot[_squads.Count];
        int write = 0;

        foreach (SquadState squad in _squads.Values)
        {
            TryGetInformation(squad.Id, out SquadMovementInformation information);
            if (squad.FlowField.TryGetDebugData(
                    out int width, out int height, out float[] costs, out byte[] directions))
            {
                snapshots[write++] = new SquadMovementDebugSnapshot(
                    information, width, height, costs, directions);
            }
            else
            {
                snapshots[write++] = new SquadMovementDebugSnapshot(
                    information, 0, 0, Array.Empty<float>(), Array.Empty<byte>());
            }
        }

        return snapshots;
    }

    public bool TryGetMemberState(
        UnitId member,
        out SquadMemberMovementState state)
    {
        state = default;
        if (!member.IsValid ||
            !_membership.TryGetValue(member, out MemberLink link) ||
            !_squads.TryGetValue(link.SquadId, out SquadState? squad) ||
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
            squad.FlowField.TerrainVersion,
            squad.Center,
            squad.Forward,
            squad.Spacing,
            squad.CenterVelocity,
            squad.EnvelopeWidth,
            squad.EnvelopeDepth,
            squad.CoreMemberCount,
            squad.HasMoveOrder);
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

    private static bool HasMatchingGroupMoveOrder(
        UnitAiStore? ai,
        int unit,
        Vector2 expectedGoal)
    {
        if (ai == null)
            return true;

        if (ai.PlayerOrderType[unit] != UnitAiOrderType.Move)
            return false;

        Vector3 order = ai.PlayerOrderPosition[unit];
        float dx = order.X - expectedGoal.X;
        float dy = order.Y - expectedGoal.Y;
        return dx * dx + dy * dy <= 0.01f;
    }

    private static bool IsCombatEngaged(
        UnitStore units,
        UnitAiStore? ai,
        UnitSuppressionStore? suppression,
        int unit)
    {
        if (suppression?.GetState(unit) == UnitSuppressionState.Panicked)
            return true;

        return ai != null &&
            (ai.PlayerOrderType[unit] == UnitAiOrderType.Attack ||
             ai.State[unit] is UnitAiState.Attack or UnitAiState.SeekCover or
                 UnitAiState.Flank or UnitAiState.Injured);
    }

    private bool IsMemberTacticallyEngaged(UnitId member)
    {
        return member.IsValid &&
            (uint)member.Index < (uint)_memberOwners.Length &&
            _memberOwners[member.Index] == member &&
            _memberStates[member.Index] == SquadMemberMovementState.Engaged;
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
        if ((uint)unit >= (uint)units.Capacity)
            throw new ArgumentOutOfRangeException(nameof(unit));

        // A* formation orders can initialize member state before the first
        // simulation update, so storage must grow at the point of first use.
        EnsureMemberCapacity(units.Capacity);

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
        EnsureMemberCapacity(units.Capacity);
        EnsureMemberIdentity(units, unit);
        _memberStates[unit] = state;
        _rejoinElapsed[unit] = 0f;
    }

    private void EnsureMemberCapacity(int required)
    {
        if (_memberStates.Length >= required &&
            _memberOwners.Length >= required &&
            _rejoinElapsed.Length >= required)
        {
            return;
        }

        int currentCapacity = Math.Max(
            _memberStates.Length,
            Math.Max(_memberOwners.Length, _rejoinElapsed.Length));
        int capacity = Math.Max(required, Math.Max(16, currentCapacity * 2));
        Array.Resize(ref _memberStates, capacity);
        Array.Resize(ref _memberOwners, capacity);
        Array.Resize(ref _rejoinElapsed, capacity);
    }

    private static Vector2 ResolveNavigablePoint(
        WorldMap worldMap,
        Vector2 requested,
        HashSet<int>? reservedCells = null)
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
                    int cell = x + y * worldMap.TileWidth;
                    if (Math.Max(Math.Abs(x - centerX), Math.Abs(y - centerY)) != radius ||
                        worldMap.IsNavigationBlocked(x, y) ||
                        worldMap.GetSurfaceLayer(x, y) < 0 ||
                        (reservedCells != null && reservedCells.Contains(cell)))
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
            {
                int bestX = (int)MathF.Floor(best.X);
                int bestY = (int)MathF.Floor(best.Y);
                reservedCells?.Add(bestX + bestY * worldMap.TileWidth);
                return best;
            }
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
        public Vector2 CenterVelocity;
        public float EnvelopeWidth;
        public float EnvelopeDepth;
        public int CoreMemberCount;
        public bool HasCenter;
        public Vector2 Forward = Vector2.UnitX;
        public float Spacing = 1.15f;
        public InfantryFormationType Formation = InfantryFormationType.Free;
        public ColumnMovementVariant ColumnVariant = ColumnMovementVariant.HistoricalSlots;
        public SquadFormationRigidity Rigidity = SquadFormationRigidity.Free;
        public SquadMovementPhase Phase = SquadMovementPhase.Idle;
        public Vector2[] MemberOffsets = Array.Empty<Vector2>();
        public Vector2[] MemberGoals = Array.Empty<Vector2>();
        public bool HasMoveOrder;
        public bool IsAStarFormation;
        public bool EnforceGroupSpeed;
        public bool KeepFormationInCombat;
        public float SlowestMoveSpeed = float.PositiveInfinity;
        public int FollowingCount;
        public int EngagedCount;
        public int RejoiningCount;
        public int SeparatedCount;
        public int MovingCount;
        public int FormationMemberCount;
        public bool FormationBootstrap;
        public Vector2 AnchorTarget;
        public float[] FormationSortKeys = Array.Empty<float>();
        public int[] FormationSortSlots = Array.Empty<int>();
        public int[] FormationNeighborBefore = Array.Empty<int>();
        public int[] FormationNeighborAfter = Array.Empty<int>();
        public float[] FormationLateralOffsets = Array.Empty<float>();

        public SquadState(int id, List<UnitId> members)
        {
            Id = id;
            Members = members;
            EnsureFormationBuffers(members.Count);
        }

        public void EnsureFormationBuffers(int memberCount)
        {
            if (FormationSortSlots.Length >= memberCount &&
                FormationLateralOffsets.Length >= memberCount)
            {
                return;
            }

            Array.Resize(ref FormationSortSlots, memberCount);
            Array.Resize(ref FormationSortKeys, memberCount);
            Array.Resize(ref FormationNeighborBefore, memberCount);
            Array.Resize(ref FormationNeighborAfter, memberCount);
            Array.Resize(ref FormationLateralOffsets, memberCount);
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
    private const float HeightCost = 0.35f;

    private static readonly int[] DirectionX = { 1, -1, 0, 0, 1, 1, -1, -1 };
    private static readonly int[] DirectionY = { 0, 0, 1, -1, 1, -1, 1, -1 };

    private float[] _distance = Array.Empty<float>();
    private byte[] _direction = Array.Empty<byte>();
    private int _width;
    private int _height;
    private int _goalCell = -1;
    private int _requestedGoalCell = -1;
    private long _terrainVersion = long.MinValue;
    private bool _hasBuild;

    public bool IsReady { get; private set; }
    public long TerrainVersion => _terrainVersion;

    public void Invalidate()
    {
        _goalCell = -1;
        _requestedGoalCell = -1;
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
            _requestedGoalCell == requestedGoal &&
            _terrainVersion == map.TerrainVersion &&
            _width == width &&
            _height == height)
        {
            return IsReady;
        }

        // Build into fresh buffers; published render snapshots retain the
        // previous field until this entire Dijkstra pass finishes.
        float[] distance = new float[count];
        byte[] directionField = new byte[count];

        _width = width;
        _height = height;
        _requestedGoalCell = requestedGoal;
        _terrainVersion = map.TerrainVersion;
        _goalCell = FindNearestPassableGoal(requestedGoal, goalX, goalY, workspace);
        IsReady = _goalCell >= 0;
        _hasBuild = true;
        if (!IsReady)
            return false;

        Array.Fill(distance, float.PositiveInfinity, 0, count);
        workspace.Begin(distance, count);
        distance[_goalCell] = 0f;
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
                float candidate = distance[cell] + stepCost;
                if (candidate >= distance[next] - 0.0001f)
                    continue;

                distance[next] = candidate;
                directionField[next] = EncodeDirection(-dx, -dy);
                workspace.PushOrDecrease(next);
            }

            if (workspace.IsEmpty)
                break;
        }

        _distance = distance;
        _direction = directionField;
        return true;
    }

    public bool TryGetDebugData(
        out int width,
        out int height,
        out float[] costs,
        out byte[] directions)
    {
        width = _width;
        height = _height;
        costs = _distance;
        directions = _direction;
        return IsReady &&
            width > 0 &&
            height > 0 &&
            costs.Length == width * height &&
            directions.Length == costs.Length;
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

        int cell00 = x0 + y0 * _width;
        int cell10 = x1 + y0 * _width;
        int cell01 = x0 + y1 * _width;
        int cell11 = x1 + y1 * _width;

        float cost00 = _distance[cell00];
        float cost10 = _distance[cell10];
        float cost01 = _distance[cell01];
        float cost11 = _distance[cell11];

        // Interpolate the scalar cost surface, then follow its downhill
        // gradient. This is smoother and more stable than averaging eight
        // discrete direction codes, especially when a route bends around a ridge.
        if (float.IsFinite(cost00) &&
            float.IsFinite(cost10) &&
            float.IsFinite(cost01) &&
            float.IsFinite(cost11))
        {
            float topCost = cost00 + (cost10 - cost00) * tx;
            float bottomCost = cost01 + (cost11 - cost01) * tx;
            distance = topCost + (bottomCost - topCost) * ty;

            float gradientX =
                (cost10 - cost00) * (1f - ty) +
                (cost11 - cost01) * ty;
            float gradientY =
                (cost01 - cost00) * (1f - tx) +
                (cost11 - cost10) * tx;
            Vector2 downhill = new(-gradientX, -gradientY);
            float gradientLengthSquared = downhill.LengthSquared();
            if (gradientLengthSquared > 0.0001f)
            {
                direction = downhill / MathF.Sqrt(gradientLengthSquared);
                return true;
            }

            if (distance <= 0.25f)
                return true;
        }

        // At map edges and beside disconnected terrain the four-cell cost
        // stencil may contain infinity. Fall back to the validated Dijkstra
        // predecessor directions instead of inventing a gradient through it.
        distance = _distance[cell00];
        if (!float.IsFinite(distance))
            return false;

        Vector2 sum = Vector2.Zero;
        AddDirection(cell00, (1f - tx) * (1f - ty), ref sum);
        AddDirection(cell10, tx * (1f - ty), ref sum);
        AddDirection(cell01, (1f - tx) * ty, ref sum);
        AddDirection(cell11, tx * ty, ref sum);

        float lengthSquared = sum.LengthSquared();
        if (lengthSquared > 0.0001f)
        {
            direction = sum / MathF.Sqrt(lengthSquared);
            return true;
        }

        byte code = _direction[cell00];
        if (code == 0)
            return distance <= 0.25f;

        direction = GetDirection(code);
        return true;
    }

    public void TryGetTurnConstraint(
        float x,
        float y,
        Vector2 initialDirection,
        out float distanceToTurn,
        out float turnSeverity)
    {
        distanceToTurn = float.PositiveInfinity;
        turnSeverity = 0f;

        float initialLengthSquared = initialDirection.LengthSquared();
        if (!IsReady || initialLengthSquared < 0.0001f)
            return;

        initialDirection /= MathF.Sqrt(initialLengthSquared);
        Vector2 probeDirection = initialDirection;
        Vector2 probePosition = new(x, y);

        const float sampleStep = 0.8f;
        const int sampleCount = 6;
        const float turnDotThreshold = 0.8660254f; // cos(30 degrees)

        for (int i = 1; i <= sampleCount; i++)
        {
            probePosition += probeDirection * sampleStep;
            if (!TrySample(
                    probePosition.X,
                    probePosition.Y,
                    out Vector2 nextDirection,
                    out _))
            {
                return;
            }

            float alignment = Math.Clamp(
                Vector2.Dot(initialDirection, nextDirection),
                -1f,
                1f);
            if (alignment < turnDotThreshold)
            {
                float angle = MathF.Acos(alignment);
                distanceToTurn = (i - 1) * sampleStep;
                turnSeverity = Math.Clamp(
                    angle / (MathF.PI * 0.5f),
                    0.15f,
                    1f);
                return;
            }

            // Follow the field itself instead of probing a straight ray. This
            // makes gradual bends visible to the controller as well as sharp corners.
            probeDirection = nextDirection;
        }
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
