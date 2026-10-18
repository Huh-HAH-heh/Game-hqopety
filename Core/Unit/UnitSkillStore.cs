using System;

namespace Core.Unit;

public enum UnitSkill : byte
{
    Shooting, Melee, Construction, Mining, Cooking, Plants,
    Animals, Crafting, Artistic, Medical, Social, Intellectual, Count
}

public enum UnitSkillPassion : byte { None, Minor, Major }

public readonly record struct UnitPawnSkill(
    UnitSkill Skill, byte Level, UnitSkillPassion Passion,
    float Experience, float ExperienceToNextLevel);

/// <summary>
/// RimWorld-style pawn skills: twelve independent 0-20 skills, passions and XP.
/// Skills are deterministic per spawned pawn; Shooting affects projectile spread.
/// </summary>
public sealed class UnitSkillStore
{
    public const byte MaxLevel = 20;
    public const int SkillCount = (int)UnitSkill.Count;

    private byte[] _levels;
    private byte[] _passions;
    private float[] _experience;
    private float[] _suppressionResistance;

    public int Capacity => _suppressionResistance.Length;
    public byte[] Levels => _levels;
    public byte[] Passions => _passions;
    public float[] Experience => _experience;
    public float[] SuppressionResistance => _suppressionResistance;

    public UnitSkillStore(int initialCapacity = 1024)
    {
        if (initialCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        _levels = new byte[initialCapacity * SkillCount];
        _passions = new byte[initialCapacity * SkillCount];
        _experience = new float[initialCapacity * SkillCount];
        _suppressionResistance = new float[initialCapacity];
    }

    public void InitializeUnit(int unitIndex, UnitType type, ushort factionTag)
    {
        EnsureCapacity(unitIndex + 1);
        uint state = unchecked(0x9E3779B9u ^
            (uint)(unitIndex + 1) * 2654435761u ^
            (uint)type * 2246822519u ^
            (uint)factionTag * 3266489917u);
        if (state == 0u) state = 0xA341316Cu;

        int start = unitIndex * SkillCount;
        for (int i = 0; i < SkillCount; i++)
        {
            UnitSkill skill = (UnitSkill)i;
            uint roll = Next(ref state);
            byte level = skill switch
            {
                UnitSkill.Shooting => (byte)(1 + roll % 14u),
                UnitSkill.Melee => (byte)(1 + roll % 13u),
                _ => (byte)(roll % 11u)
            };
            uint passionRoll = Next(ref state) % 12u;
            UnitSkillPassion passion = passionRoll == 0u
                ? UnitSkillPassion.Major
                : passionRoll <= 3u ? UnitSkillPassion.Minor : UnitSkillPassion.None;
            _levels[start + i] = level;
            _passions[start + i] = (byte)passion;
            _experience[start + i] = 0f;
        }

        float combatSkill = _levels[start + (int)UnitSkill.Shooting] +
            _levels[start + (int)UnitSkill.Melee];
        _suppressionResistance[unitIndex] = Math.Clamp(
            0.18f + ToUnitFloat(Next(ref state)) * 0.46f + combatSkill * 0.008f,
            0.15f, 0.90f);
    }

    public byte GetLevel(int unitIndex, UnitSkill skill) => _levels[GetIndex(unitIndex, skill)];
    public UnitSkillPassion GetPassion(int unitIndex, UnitSkill skill) =>
        (UnitSkillPassion)_passions[GetIndex(unitIndex, skill)];
    public float GetExperience(int unitIndex, UnitSkill skill) => _experience[GetIndex(unitIndex, skill)];
    public float GetExperienceToNextLevel(int unitIndex, UnitSkill skill) =>
        ExperienceRequired(GetLevel(unitIndex, skill));
    public float GetSuppressionResistance(int unitIndex) =>
        (uint)unitIndex < (uint)Capacity ? _suppressionResistance[unitIndex] : 0f;

    public void SetSkillLevel(int unitIndex, UnitSkill skill, int level)
    {
        int index = GetIndex(unitIndex, skill);
        _levels[index] = (byte)Math.Clamp(level, 0, MaxLevel);
        _experience[index] = 0f;
    }

    public void SetPassion(int unitIndex, UnitSkill skill, UnitSkillPassion passion)
    {
        int index = GetIndex(unitIndex, skill);
        _passions[index] = (byte)(Enum.IsDefined(passion) ? passion : UnitSkillPassion.None);
    }

    public void SetSuppressionResistance(int unitIndex, float resistance)
    {
        EnsureCapacity(unitIndex + 1);
        _suppressionResistance[unitIndex] =
            Math.Clamp(float.IsFinite(resistance) ? resistance : 0f, 0f, 1f);
    }

    public void GainExperience(int unitIndex, UnitSkill skill, float amount)
    {
        if (!float.IsFinite(amount) || amount <= 0f) return;
        int index = GetIndex(unitIndex, skill);
        float multiplier = (UnitSkillPassion)_passions[index] switch
        {
            UnitSkillPassion.Minor => 1.5f,
            UnitSkillPassion.Major => 2f,
            _ => 1f
        };
        _experience[index] += amount * multiplier;
        while (_levels[index] < MaxLevel)
        {
            float required = ExperienceRequired(_levels[index]);
            if (_experience[index] < required) break;
            _experience[index] -= required;
            _levels[index]++;
        }
        if (_levels[index] >= MaxLevel) _experience[index] = 0f;
    }

    /// <summary>Lower is better: experienced shooters produce less random spread.</summary>
    public float GetShootingSpreadMultiplier(int unitIndex) =>
        Math.Clamp(1.35f - GetLevel(unitIndex, UnitSkill.Shooting) * 0.035f, 0.65f, 1.35f);

    public void EnsureCapacity(int required)
    {
        if (required <= Capacity) return;
        int newCapacity = Math.Max(required, Capacity * 2);
        Array.Resize(ref _levels, newCapacity * SkillCount);
        Array.Resize(ref _passions, newCapacity * SkillCount);
        Array.Resize(ref _experience, newCapacity * SkillCount);
        Array.Resize(ref _suppressionResistance, newCapacity);
    }

    private static int GetIndex(int unitIndex, UnitSkill skill)
    {
        if (unitIndex < 0 || (uint)skill >= (uint)UnitSkill.Count)
            throw new ArgumentOutOfRangeException(nameof(unitIndex));
        return unitIndex * SkillCount + (int)skill;
    }

    private static float ExperienceRequired(byte level) => 1000f + level * 250f;
    private static uint Next(ref uint state)
    {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        return state;
    }
    private static float ToUnitFloat(uint value) => (value & 0x00FFFFFFu) / 16777216f;
}
