using System;
using System.Collections.Generic;
using System.Numerics;
using Core.Unit;

namespace Core.AI.Perception;

/// <summary>
/// Converts loud events and locomotion into uncertain per-observer contacts.
/// The observer never receives the emitter ID from an audio-only contact.
/// </summary>
public sealed class TacticalHearingSystem
{
    private const float GunshotRange = 90f;
    private const float GunshotMemorySeconds = 8f;
    private const float FootstepMemorySeconds = 3f;
    private const float ConfidenceHalfLifeSeconds = 2.5f;
    private const float MinimumContactConfidence = 0.12f;
    private readonly List<SoundPulse> _pendingPulses = new();
    private TacticalContactMemory?[] _memories;
    private float[] _footstepCooldown;
    private long _nextPulseId = 1;
    public float CurrentTimeSeconds { get; private set; }

    public TacticalHearingSystem(int initialUnitCapacity = 1024)
    {
        if (initialUnitCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialUnitCapacity));
        _memories = new TacticalContactMemory?[initialUnitCapacity];
        _footstepCooldown = new float[initialUnitCapacity];
    }

    public void EmitGunshot(UnitId shooter, Vector3 position, ushort factionTag) =>
        EmitDanger(shooter, factionTag, TacticalStimulusType.Gunshot, position, 1f, GunshotRange);

    public void EmitDanger(
        UnitId sourceUnit,
        ushort sourceFaction,
        TacticalStimulusType type,
        Vector3 position,
        float intensity,
        float audibleRange)
    {
        if (!IsFinite(position) ||
            !float.IsFinite(intensity) ||
            !float.IsFinite(audibleRange) ||
            intensity <= 0f ||
            audibleRange <= 0f ||
            type is not (TacticalStimulusType.Gunshot or TacticalStimulusType.NearMiss or
                TacticalStimulusType.CoverImpact or TacticalStimulusType.Explosion))
            return;

        _pendingPulses.Add(new SoundPulse(
            _nextPulseId++, sourceUnit, sourceFaction, position, type,
            CurrentTimeSeconds, Math.Clamp(intensity, 0f, 1f), audibleRange));
    }

    /// <summary>
    /// Entry point for future blast producers. The current weapon model does not
    /// yet implement explosive ammunition or radius-based blast damage.
    /// </summary>
    public void EmitExplosion(
        Vector3 position,
        ushort sourceFaction = 0,
        float intensity = 1f,
        float audibleRange = 80f) =>
        EmitDanger(default, sourceFaction, TacticalStimulusType.Explosion,
            position, intensity, audibleRange);

    public void Update(UnitStore units, UnitHealthStore health, float nowSeconds, float deltaTime)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(health);
        if (!float.IsFinite(nowSeconds) || nowSeconds < CurrentTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds));
        EnsureCapacity(units.Capacity);
        CurrentTimeSeconds = nowSeconds;
        for (int i = 0; i < _memories.Length; i++)
            _memories[i]?.AdvanceTime(nowSeconds);
        if (!float.IsFinite(deltaTime) || deltaTime <= 0f)
            return;
        CollectFootsteps(units, health, deltaTime, nowSeconds);
        DeliverPulses(units, health, nowSeconds);
        _pendingPulses.Clear();
    }

    public void InitializeUnit(int unitIndex)
    {
        if (unitIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(unitIndex));
        EnsureCapacity(unitIndex + 1);
        _memories[unitIndex]?.Clear();
        _memories[unitIndex] = null;
        _footstepCooldown[unitIndex] = 0f;
    }

    public void ClearUnit(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)_memories.Length)
            return;
        _memories[unitIndex]?.Clear();
        _memories[unitIndex] = null;
        _footstepCooldown[unitIndex] = 0f;
    }

    public bool HasInvestigableContact(int observerIndex) =>
        TryGetBestInvestigableContact(observerIndex, out _);

    public bool TryGetBestInvestigableContact(int observerIndex, out TacticalContact contact)
    {
        contact = null!;
        if ((uint)observerIndex >= (uint)_memories.Length ||
            _memories[observerIndex] is not TacticalContactMemory memory)
            return false;
        float bestScore = 0f;
        IReadOnlyList<TacticalContact> contacts = memory.Contacts;
        for (int i = 0; i < contacts.Count; i++)
        {
            TacticalContact candidate = contacts[i];
            if (candidate.IsInvestigated ||
                candidate.ExpiresAtSeconds <= CurrentTimeSeconds ||
                candidate.LastStimulusType is not (TacticalStimulusType.Gunshot or TacticalStimulusType.Footstep))
                continue;
            float confidence = candidate.GetEffectiveConfidence(CurrentTimeSeconds, ConfidenceHalfLifeSeconds);
            float minimum = candidate.LastStimulusType == TacticalStimulusType.Footstep ? 0.18f : 0.16f;
            if (confidence < minimum)
                continue;
            float score = confidence +
                (candidate.LastStimulusType == TacticalStimulusType.Gunshot ? 0.12f : 0f);
            if (score <= bestScore)
                continue;
            bestScore = score;
            contact = candidate;
        }
        return bestScore > 0f;
    }

    public bool HasImmediateDangerContact(int observerIndex) =>
        TryGetBestImmediateDangerContact(observerIndex, out _);

    public bool TryGetBestImmediateDangerContact(int observerIndex, out TacticalContact contact)
    {
        contact = null!;
        if ((uint)observerIndex >= (uint)_memories.Length ||
            _memories[observerIndex] is not TacticalContactMemory memory)
            return false;

        float bestScore = 0f;
        IReadOnlyList<TacticalContact> contacts = memory.Contacts;
        for (int i = 0; i < contacts.Count; i++)
        {
            TacticalContact candidate = contacts[i];
            if (candidate.IsInvestigated ||
                candidate.ExpiresAtSeconds <= CurrentTimeSeconds ||
                candidate.LastStimulusType is not (TacticalStimulusType.NearMiss or
                    TacticalStimulusType.CoverImpact or TacticalStimulusType.Explosion))
                continue;

            float confidence = candidate.GetEffectiveConfidence(CurrentTimeSeconds, ConfidenceHalfLifeSeconds);
            if (confidence < 0.14f || confidence <= bestScore)
                continue;
            bestScore = confidence;
            contact = candidate;
        }
        return bestScore > 0f;
    }

    public bool MarkInvestigated(int observerIndex, long contactId)
    {
        if ((uint)observerIndex >= (uint)_memories.Length ||
            _memories[observerIndex] is not TacticalContactMemory memory)
            return false;
        return memory.MarkInvestigated(contactId);
    }

    private void CollectFootsteps(UnitStore units, UnitHealthStore health, float deltaTime, float nowSeconds)
    {
        ReadOnlySpan<int> active = units.ActiveIndices;
        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            _footstepCooldown[unit] = MathF.Max(0f, _footstepCooldown[unit] - deltaTime);
            if (health.OverallHitPoints[unit] <= 0f)
            {
                _footstepCooldown[unit] = 0f;
                continue;
            }
            Vector3 velocity = units.Velocity[unit];
            if (velocity.X * velocity.X + velocity.Y * velocity.Y < 0.36f ||
                _footstepCooldown[unit] > 0f)
                continue;
            UnitPosture posture = units.Posture[unit];
            float intensity, range, interval;
            switch (posture)
            {
                case UnitPosture.Lying: intensity = 0.22f; range = 9f; interval = 0.90f; break;
                case UnitPosture.Crouching: intensity = 0.36f; range = 14f; interval = 0.70f; break;
                case UnitPosture.Sitting: intensity = 0.28f; range = 11f; interval = 0.80f; break;
                default: intensity = 0.55f; range = 20f; interval = 0.50f; break;
            }
            _pendingPulses.Add(new SoundPulse(
                _nextPulseId++, units.GetId(unit), units.FactionTag[unit],
                units.Position[unit], TacticalStimulusType.Footstep, nowSeconds, intensity, range));
            _footstepCooldown[unit] = interval;
        }
    }

    private void DeliverPulses(UnitStore units, UnitHealthStore health, float nowSeconds)
    {
        ReadOnlySpan<int> active = units.ActiveIndices;
        for (int pulseIndex = 0; pulseIndex < _pendingPulses.Count; pulseIndex++)
        {
            SoundPulse pulse = _pendingPulses[pulseIndex];
            float lifetime = pulse.Type switch
            {
                TacticalStimulusType.Footstep => FootstepMemorySeconds,
                TacticalStimulusType.Gunshot => GunshotMemorySeconds,
                TacticalStimulusType.NearMiss => 5f,
                TacticalStimulusType.CoverImpact => 6f,
                TacticalStimulusType.Explosion => 10f,
                _ => 4f
            };
            if (nowSeconds - pulse.OccurredAtSeconds >= lifetime)
                continue;
            for (int i = 0; i < active.Length; i++)
            {
                int observer = active[i];
                if (health.OverallHitPoints[observer] <= 0f ||
                    units.GetId(observer).Equals(pulse.SourceUnit))
                    continue;
                ushort observerFaction = units.FactionTag[observer];
                if (pulse.SourceFaction != 0 && pulse.SourceFaction == observerFaction)
                    continue;
                Vector3 observerPosition = units.Position[observer];
                float dx = pulse.Position.X - observerPosition.X;
                float dy = pulse.Position.Y - observerPosition.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 > pulse.Range * pulse.Range)
                    continue;
                float distance = MathF.Sqrt(d2);
                float attenuation = Math.Clamp(1f - distance / pulse.Range, 0f, 1f);
                float confidence = pulse.Intensity * (0.25f + 0.75f * attenuation);
                if (confidence < MinimumContactConfidence)
                    continue;
                float error = pulse.Type == TacticalStimulusType.Footstep
                    ? 1.5f + distance * 0.20f
                    : 4f + distance * 0.22f;
                Vector3 estimate = EstimatePosition(pulse, observer, error, attenuation);
                TacticalContactMemory memory = _memories[observer] ??= new TacticalContactMemory();
                memory.Record(new TacticalStimulus(
                    pulse.Type, estimate, pulse.OccurredAtSeconds,
                    pulse.Intensity, error, confidence), nowSeconds, lifetime);
            }
        }
    }

    private static Vector3 EstimatePosition(SoundPulse pulse, int observer, float error, float attenuation)
    {
        ulong seed = unchecked(
            (ulong)pulse.Sequence * 6364136223846793005UL +
            (ulong)(observer + 1) * 1442695040888963407UL +
            (ulong)(pulse.SourceUnit.Index + 1) * 22695477UL);
        float angle = (seed % 628319UL) / 100000f;
        float offset = error * (0.15f + (1f - attenuation) * 0.45f);
        return new Vector3(
            pulse.Position.X + MathF.Cos(angle) * offset,
            pulse.Position.Y + MathF.Sin(angle) * offset,
            pulse.Position.Z);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _memories.Length)
            return;
        int capacity = Math.Max(required, _memories.Length * 2);
        Array.Resize(ref _memories, capacity);
        Array.Resize(ref _footstepCooldown, capacity);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct SoundPulse(
        long Sequence, UnitId SourceUnit, ushort SourceFaction, Vector3 Position,
        TacticalStimulusType Type, float OccurredAtSeconds, float Intensity, float Range);
}
