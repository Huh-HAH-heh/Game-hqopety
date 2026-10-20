using System;
using System.Collections.Generic;
using Core.Unit;

namespace Core.AI;

public sealed class SquadMembershipSnapshot
{
    public int SquadId { get; }
    public IReadOnlyList<UnitId> Members { get; }

    public SquadMembershipSnapshot(int squadId, IReadOnlyList<UnitId> members)
    {
        SquadId = squadId;
        Members = members ?? throw new ArgumentNullException(nameof(members));
    }
}

/// <summary>
/// Small coordination layer for squad membership and one-at-a-time flank ownership.
/// Individual perception, target selection and movement decisions remain in UnitAiSystem.
/// </summary>
public sealed class SquadTacticalCoordinator
{
    private const float FlankClaimLifetimeSeconds = 1.0f;

    private readonly object _gate = new();
    private readonly Dictionary<UnitId, int> _membership = new();
    private readonly Dictionary<(int SquadId, UnitId Target), FlankClaim> _flankClaims = new();

    public void SynchronizeMemberships(IReadOnlyList<SquadMembershipSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        lock (_gate)
        {
            var nextMembership = new Dictionary<UnitId, int>();
            foreach (SquadMembershipSnapshot snapshot in snapshots)
            {
                if (snapshot.SquadId <= 0)
                    continue;

                for (int i = 0; i < snapshot.Members.Count; i++)
                {
                    UnitId member = snapshot.Members[i];
                    if (member.IsValid && !nextMembership.ContainsKey(member))
                        nextMembership.Add(member, snapshot.SquadId);
                }
            }

            _membership.Clear();
            foreach (KeyValuePair<UnitId, int> pair in nextMembership)
                _membership.Add(pair.Key, pair.Value);

            RemoveStaleFlankClaims();
        }
    }

    public bool IsMemberOfSquad(UnitId member, int squadId)
    {
        if (!member.IsValid || squadId <= 0)
            return false;

        lock (_gate)
            return _membership.TryGetValue(member, out int currentSquad) &&
                   currentSquad == squadId;
    }

    public bool IsMemberOfAnySquad(UnitId member)
    {
        if (!member.IsValid)
            return false;

        lock (_gate)
            return _membership.ContainsKey(member);
    }

    public void InvalidateUnit(UnitId unit)
    {
        if (!unit.IsValid)
            return;

        lock (_gate)
        {
            _membership.Remove(unit);

            List<(int SquadId, UnitId Target)> stale = new();
            foreach (KeyValuePair<(int SquadId, UnitId Target), FlankClaim> pair in _flankClaims)
            {
                if (pair.Value.Owner.Equals(unit) || pair.Key.Target.Equals(unit))
                    stale.Add(pair.Key);
            }

            for (int i = 0; i < stale.Count; i++)
                _flankClaims.Remove(stale[i]);

            RemoveStaleFlankClaims();
        }
    }

    public void Advance(float now)
    {
        if (!float.IsFinite(now))
            return;

        lock (_gate)
        {
            List<(int SquadId, UnitId Target)> expired = new();
            foreach (KeyValuePair<(int SquadId, UnitId Target), FlankClaim> pair in _flankClaims)
            {
                if (pair.Value.ExpiresAt <= now ||
                    !_membership.TryGetValue(pair.Value.Owner, out int ownerSquad) ||
                    ownerSquad != pair.Key.SquadId)
                {
                    expired.Add(pair.Key);
                }
            }

            for (int i = 0; i < expired.Count; i++)
                _flankClaims.Remove(expired[i]);
        }
    }

    public bool TryClaimFlank(UnitId member, UnitId target, float now)
    {
        if (!member.IsValid || !target.IsValid || !float.IsFinite(now))
            return false;

        lock (_gate)
        {
            // Lone units keep their normal individual flank behavior.
            if (!_membership.TryGetValue(member, out int squadId))
                return true;

            var key = (SquadId: squadId, Target: target);
            if (_flankClaims.TryGetValue(key, out FlankClaim claim))
            {
                bool ownerStillValid =
                    claim.ExpiresAt > now &&
                    _membership.TryGetValue(claim.Owner, out int ownerSquad) &&
                    ownerSquad == squadId;

                if (ownerStillValid)
                {
                    if (!claim.Owner.Equals(member))
                        return false;

                    _flankClaims[key] = new FlankClaim(member, now + FlankClaimLifetimeSeconds);
                    return true;
                }

                _flankClaims.Remove(key);
            }

            _flankClaims[key] = new FlankClaim(member, now + FlankClaimLifetimeSeconds);
            return true;
        }
    }

    private void RemoveStaleFlankClaims()
    {
        List<(int SquadId, UnitId Target)> stale = new();
        foreach (KeyValuePair<(int SquadId, UnitId Target), FlankClaim> pair in _flankClaims)
        {
            if (!_membership.TryGetValue(pair.Value.Owner, out int ownerSquad) ||
                ownerSquad != pair.Key.SquadId)
            {
                stale.Add(pair.Key);
            }
        }

        for (int i = 0; i < stale.Count; i++)
            _flankClaims.Remove(stale[i]);
    }

    private readonly record struct FlankClaim(UnitId Owner, float ExpiresAt);
}
