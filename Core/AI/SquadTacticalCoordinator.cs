using System;
using System.Collections.Generic;
using System.Numerics;
using Core.Unit;

namespace Core.AI;

public sealed class SquadMembershipSnapshot
{
    public int SquadId { get; }
    public IReadOnlyList<UnitId> Members { get; }
    public SquadMembershipSnapshot(int squadId, IReadOnlyList<UnitId> members)
    { SquadId = squadId; Members = members ?? throw new ArgumentNullException(nameof(members)); }
}

public readonly record struct SquadTacticalContact(
    long Sequence, Vector3 EstimatedPosition, float PositionErrorRadius, float Confidence, UnitId Reporter);

public sealed class SquadTacticalCoordinator
{
    public const float ReportDelaySeconds = 0.55f;
    public const float ContactLifetimeSeconds = 4f;
    public const float ContactConfidenceHalfLifeSeconds = 2.2f;
    public const float MinimumContactConfidence = 0.12f;
    private const float RefreshSeconds = 0.90f;
    private const float FlankClaimLifetimeSeconds = 1.0f;
    private const float SignificantMovement = 3f;
    private const float InitialConfidence = 0.68f;
    private const float ErrorRadius = 6f;

    private readonly object _gate = new();
    private readonly Dictionary<int, SquadState> _squads = new();
    private readonly Dictionary<UnitId, int> _membership = new();
    private readonly Dictionary<UnitId, ReceivedContact> _received = new();
    private readonly Dictionary<long, InvestigationClaim> _claims = new();
    private readonly Dictionary<(int SquadId, UnitId Target), FlankClaim> _flankClaims = new();
    private readonly List<PendingReport> _pending = new();
    private long _nextSequence = 1;

    public void SynchronizeMemberships(IReadOnlyList<SquadMembershipSnapshot> snapshots)
    {
        if (snapshots == null) throw new ArgumentNullException(nameof(snapshots));
        lock (_gate)
        {
            var nextMembership = new Dictionary<UnitId, int>();
            var nextMembers = new Dictionary<int, List<UnitId>>();
            foreach (SquadMembershipSnapshot item in snapshots)
            {
                if (item.SquadId <= 0) continue;
                if (!nextMembers.TryGetValue(item.SquadId, out List<UnitId>? members))
                    nextMembers[item.SquadId] = members = new List<UnitId>();
                for (int i = 0; i < item.Members.Count; i++)
                {
                    UnitId member = item.Members[i];
                    if (member.IsValid && !nextMembership.ContainsKey(member))
                    { nextMembership.Add(member, item.SquadId); members.Add(member); }
                }
            }
            foreach (KeyValuePair<UnitId, int> previous in _membership)
                if (!nextMembership.TryGetValue(previous.Key, out int newSquad) || newSquad != previous.Value)
                    _received.Remove(previous.Key);
            List<int> removed = new();
            foreach (int squadId in _squads.Keys)
                if (!nextMembers.ContainsKey(squadId)) removed.Add(squadId);
            for (int i = 0; i < removed.Count; i++) _squads.Remove(removed[i]);
            foreach (KeyValuePair<int, List<UnitId>> pair in nextMembers)
            {
                if (!_squads.TryGetValue(pair.Key, out SquadState? state))
                    _squads[pair.Key] = state = new SquadState();
                state.Members.Clear();
                state.Members.AddRange(pair.Value);
                HashSet<UnitId> members = new(state.Members);
                List<UnitId> stale = new();
                foreach (UnitId observer in state.Observations.Keys)
                    if (!members.Contains(observer)) stale.Add(observer);
                for (int i = 0; i < stale.Count; i++) state.Observations.Remove(stale[i]);
            }
            _membership.Clear();
            foreach (KeyValuePair<UnitId, int> pair in nextMembership) _membership.Add(pair.Key, pair.Value);

            // A contact is valid only while its reporter and receiver still share a squad.
            List<UnitId> staleReceived = new();
            foreach (KeyValuePair<UnitId, ReceivedContact> pair in _received)
            {
                if (!_membership.TryGetValue(pair.Key, out int receiverSquad) ||
                    !_membership.TryGetValue(pair.Value.Reporter, out int reporterSquad) ||
                    receiverSquad != pair.Value.SquadId ||
                    reporterSquad != pair.Value.SquadId)
                    staleReceived.Add(pair.Key);
            }
            for (int i = 0; i < staleReceived.Count; i++) _received.Remove(staleReceived[i]);

            // Remove a reservation only when its owner or owned report is stale;
            // one receiver leaving must not free the same report for another member.
            List<long> staleClaims = new();
            foreach (KeyValuePair<long, InvestigationClaim> pair in _claims)
                if (!_membership.TryGetValue(pair.Value.Owner, out int squadId) ||
                    squadId != pair.Value.SquadId ||
                    !_received.TryGetValue(pair.Value.Owner, out ReceivedContact? ownerContact) ||
                    ownerContact.Sequence != pair.Key || ownerContact.SquadId != pair.Value.SquadId)
                    staleClaims.Add(pair.Key);
            for (int i = 0; i < staleClaims.Count; i++) _claims.Remove(staleClaims[i]);

            List<(int SquadId, UnitId Target)> staleFlankClaims = new();
            foreach (KeyValuePair<(int SquadId, UnitId Target), FlankClaim> pair in _flankClaims)
                if (!_membership.TryGetValue(pair.Value.Owner, out int ownerSquad) ||
                    ownerSquad != pair.Key.SquadId)
                    staleFlankClaims.Add(pair.Key);
            for (int i = 0; i < staleFlankClaims.Count; i++) _flankClaims.Remove(staleFlankClaims[i]);

            _pending.RemoveAll(report => !_squads.ContainsKey(report.SquadId) ||
                !_membership.TryGetValue(report.Reporter, out int squadId) || squadId != report.SquadId);
        }
    }

    public bool IsMemberOfSquad(UnitId member, int squadId)
    {
        if (!member.IsValid || squadId <= 0) return false;
        lock (_gate)
            return _membership.TryGetValue(member, out int currentSquad) && currentSquad == squadId;
    }

    public bool HasExpiredObservation(int squadId, UnitId target, float now)
    {
        if (squadId <= 0 || !target.IsValid || !float.IsFinite(now)) return true;
        lock (_gate)
        {
            if (!_squads.TryGetValue(squadId, out SquadState? squad)) return true;
            // Keep per-target history separate from each observer's current target;
            // otherwise seeing a different enemy would erase the stale-contact timer.
            // Explicit objectives that have never been observed remain valid.
            return squad.TargetLastSeen.TryGetValue(target, out float lastSeen) &&
                now - lastSeen >= ContactLifetimeSeconds;
        }
    }

    /// <summary>Remove a unit from squad coordination and invalidate reports or claims tied to it.</summary>
    public void InvalidateUnit(UnitId unit)
    {
        if (!unit.IsValid) return;
        lock (_gate)
        {
            if (_membership.Remove(unit, out int squadId) &&
                _squads.TryGetValue(squadId, out SquadState? memberSquad))
            {
                memberSquad.Members.Remove(unit);
                memberSquad.Observations.Remove(unit);
            }

            foreach (SquadState squad in _squads.Values)
            {
                List<UnitId> staleObservers = new();
                foreach (KeyValuePair<UnitId, Observation> pair in squad.Observations)
                    if (pair.Key.Equals(unit) || pair.Value.Target.Equals(unit))
                        staleObservers.Add(pair.Key);
                for (int i = 0; i < staleObservers.Count; i++) squad.Observations.Remove(staleObservers[i]);
                squad.TargetLastSeen.Remove(unit);
            }

            HashSet<long> invalidSequences = new();
            List<UnitId> staleReceivers = new();
            foreach (KeyValuePair<UnitId, ReceivedContact> pair in _received)
            {
                if (pair.Key.Equals(unit) || pair.Value.Reporter.Equals(unit) || pair.Value.Target.Equals(unit))
                    staleReceivers.Add(pair.Key);

                // A receiver dying invalidates its copy, not the other recipients'
                // reservations. Reporter/target loss invalidates the shared report.
                if (pair.Value.Reporter.Equals(unit) || pair.Value.Target.Equals(unit))
                    invalidSequences.Add(pair.Value.Sequence);
            }
            for (int i = 0; i < staleReceivers.Count; i++) _received.Remove(staleReceivers[i]);

            List<long> staleInvestigationClaims = new();
            foreach (KeyValuePair<long, InvestigationClaim> pair in _claims)
                if (pair.Value.Owner.Equals(unit) || invalidSequences.Contains(pair.Key))
                    staleInvestigationClaims.Add(pair.Key);
            for (int i = 0; i < staleInvestigationClaims.Count; i++) _claims.Remove(staleInvestigationClaims[i]);

            List<(int SquadId, UnitId Target)> staleFlankClaims = new();
            foreach (KeyValuePair<(int SquadId, UnitId Target), FlankClaim> pair in _flankClaims)
                if (pair.Value.Owner.Equals(unit) || pair.Key.Target.Equals(unit))
                    staleFlankClaims.Add(pair.Key);
            for (int i = 0; i < staleFlankClaims.Count; i++) _flankClaims.Remove(staleFlankClaims[i]);

            _pending.RemoveAll(report => report.Reporter.Equals(unit) || report.Target.Equals(unit));
        }
    }

    public void ObserveVisibleContact(UnitId observer, UnitId target, Vector3 position, float now)
    {
        if (!observer.IsValid || !target.IsValid || !float.IsFinite(now) ||
            !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) return;
        lock (_gate)
        {
            if (!_membership.TryGetValue(observer, out int squadId) ||
                !_squads.TryGetValue(squadId, out SquadState? squad)) return;
            bool publish = true;
            if (squad.Observations.TryGetValue(observer, out Observation prior))
            {
                float dx = position.X - prior.Position.X, dy = position.Y - prior.Position.Y;
                publish = !prior.Target.Equals(target) ||
                    dx * dx + dy * dy >= SignificantMovement * SignificantMovement ||
                    now - prior.PublishedAt >= RefreshSeconds;
            }
            if (!publish) return;
            squad.Observations[observer] = new Observation(target, position, now);
            squad.TargetLastSeen[target] = now;
            _pending.Add(new PendingReport(squadId, observer, target, _nextSequence++, position,
                now + ReportDelaySeconds, ErrorRadius, InitialConfidence));
        }
    }

    public void Advance(float now)
    {
        if (!float.IsFinite(now)) return;
        lock (_gate)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingReport report = _pending[i];
                if (report.DeliverAt > now) continue;
                _pending.RemoveAt(i);
                if (!_squads.TryGetValue(report.SquadId, out SquadState? squad) ||
                    !_membership.TryGetValue(report.Reporter, out int reporterSquad) ||
                    reporterSquad != report.SquadId) continue;
                foreach (UnitId receiver in squad.Members)
                {
                    if (receiver.Equals(report.Reporter)) continue;
                    if (_received.TryGetValue(receiver, out ReceivedContact? old) && old.Sequence >= report.Sequence) continue;
                    float angle = (receiver.Index * 2.39996323f + report.Sequence * 0.75487767f) % MathF.Tau;
                    float offset = MathF.Min(report.ErrorRadius * 0.35f, 2.5f);
                    Vector3 estimate = report.Position + new Vector3(MathF.Cos(angle) * offset, MathF.Sin(angle) * offset, 0f);
                    _received[receiver] = new ReceivedContact(report.Sequence, estimate, report.ErrorRadius,
                        report.Confidence, now, report.Reporter, report.Target, report.SquadId);
                }
            }
            List<UnitId> expired = new();
            foreach (KeyValuePair<UnitId, ReceivedContact> pair in _received)
                if (now - pair.Value.ReceivedAt >= ContactLifetimeSeconds || !_membership.ContainsKey(pair.Key))
                    expired.Add(pair.Key);
            for (int i = 0; i < expired.Count; i++) _received.Remove(expired[i]);

            List<long> expiredClaims = new();
            foreach (KeyValuePair<long, InvestigationClaim> pair in _claims)
                if (pair.Value.ExpiresAt <= now ||
                    !_membership.TryGetValue(pair.Value.Owner, out int squadId) ||
                    squadId != pair.Value.SquadId)
                    expiredClaims.Add(pair.Key);
            for (int i = 0; i < expiredClaims.Count; i++) _claims.Remove(expiredClaims[i]);

            List<(int SquadId, UnitId Target)> expiredFlankClaims = new();
            foreach (KeyValuePair<(int SquadId, UnitId Target), FlankClaim> pair in _flankClaims)
                if (pair.Value.ExpiresAt <= now ||
                    !_membership.TryGetValue(pair.Value.Owner, out int flankOwnerSquad) ||
                    flankOwnerSquad != pair.Key.SquadId)
                    expiredFlankClaims.Add(pair.Key);
            for (int i = 0; i < expiredFlankClaims.Count; i++) _flankClaims.Remove(expiredFlankClaims[i]);
        }
    }

    public bool TryGetContact(UnitId receiver, float now, out SquadTacticalContact contact)
    {
        contact = default;
        if (!receiver.IsValid || !float.IsFinite(now)) return false;
        lock (_gate)
        {
            if (!_received.TryGetValue(receiver, out ReceivedContact? value) ||
                value.InvestigatedSequence == value.Sequence ||
                !_membership.TryGetValue(receiver, out int receiverSquad) ||
                receiverSquad != value.SquadId ||
                !_membership.TryGetValue(value.Reporter, out int reporterSquad) ||
                reporterSquad != value.SquadId) return false;
            if (_claims.TryGetValue(value.Sequence, out InvestigationClaim claim) &&
                !claim.Owner.Equals(receiver)) return false;
            float age = MathF.Max(0f, now - value.ReceivedAt);
            if (age >= ContactLifetimeSeconds) return false;
            float confidence = value.InitialConfidence * MathF.Pow(0.5f, age / ContactConfidenceHalfLifeSeconds);
            if (confidence < MinimumContactConfidence) return false;
            contact = new SquadTacticalContact(value.Sequence, value.Position, value.ErrorRadius, confidence, value.Reporter);
            return true;
        }
    }

    public bool TryClaimInvestigation(UnitId receiver, long sequence, float now)
    {
        if (!float.IsFinite(now)) return false;
        lock (_gate)
        {
            if (!_received.TryGetValue(receiver, out ReceivedContact? value) ||
                value.Sequence != sequence ||
                value.InvestigatedSequence == sequence ||
                !_membership.TryGetValue(receiver, out int squadId) ||
                value.SquadId != squadId)
                return false;

            if (_claims.TryGetValue(sequence, out InvestigationClaim existing))
                return existing.Owner.Equals(receiver);

            _claims[sequence] = new InvestigationClaim(
                receiver,
                squadId,
                MathF.Min(value.ReceivedAt + ContactLifetimeSeconds, now + ContactLifetimeSeconds));
            return true;
        }
    }

    public bool TryClaimFlank(UnitId member, UnitId target, float now)
    {
        if (!member.IsValid || !target.IsValid || !float.IsFinite(now)) return false;
        lock (_gate)
        {
            // Units outside a squad keep the existing individual flank behavior.
            if (!_membership.TryGetValue(member, out int squadId)) return true;

            var key = (SquadId: squadId, Target: target);
            if (_flankClaims.TryGetValue(key, out FlankClaim claim))
            {
                if (claim.ExpiresAt <= now ||
                    !_membership.TryGetValue(claim.Owner, out int ownerSquad) ||
                    ownerSquad != squadId)
                {
                    _flankClaims.Remove(key);
                }
                else if (claim.Owner.Equals(member))
                {
                    _flankClaims[key] = new FlankClaim(member, now + FlankClaimLifetimeSeconds);
                    return true;
                }
                else
                {
                    return false;
                }
            }

            _flankClaims[key] = new FlankClaim(member, now + FlankClaimLifetimeSeconds);
            return true;
        }
    }

    public bool MarkInvestigated(UnitId receiver, long sequence)
    {
        lock (_gate)
        {
            if (!_received.TryGetValue(receiver, out ReceivedContact? value) || value.Sequence != sequence) return false;
            value.InvestigatedSequence = sequence;
            return true;
        }
    }

    private sealed class SquadState
    {
        public readonly List<UnitId> Members = new();
        public readonly Dictionary<UnitId, Observation> Observations = new();
        public readonly Dictionary<UnitId, float> TargetLastSeen = new();
    }
    private readonly record struct Observation(UnitId Target, Vector3 Position, float PublishedAt);
    private readonly record struct PendingReport(int SquadId, UnitId Reporter, UnitId Target, long Sequence, Vector3 Position,
        float DeliverAt, float ErrorRadius, float Confidence);
    private readonly record struct InvestigationClaim(UnitId Owner, int SquadId, float ExpiresAt);
    private readonly record struct FlankClaim(UnitId Owner, float ExpiresAt);
    private sealed class ReceivedContact
    {
        public long Sequence { get; }
        public Vector3 Position { get; }
        public float ErrorRadius { get; }
        public float InitialConfidence { get; }
        public float ReceivedAt { get; }
        public UnitId Reporter { get; }
        public UnitId Target { get; }
        public int SquadId { get; }
        public long InvestigatedSequence { get; set; }
        public ReceivedContact(long sequence, Vector3 position, float errorRadius, float confidence, float receivedAt, UnitId reporter, UnitId target, int squadId)
        { Sequence = sequence; Position = position; ErrorRadius = errorRadius; InitialConfidence = confidence; ReceivedAt = receivedAt; Reporter = reporter; Target = target; SquadId = squadId; }
    }
}
