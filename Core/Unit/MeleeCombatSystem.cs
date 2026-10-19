using System;
using System.Numerics;
using Core.Combat;
using Core.Items;

namespace Core.Unit;

/// <summary>
/// CE-inspired close-combat loop: weapon reach, skill-based attacks, critical hits,
/// evasive movement, parries and ripostes. Uses the shared armor and anatomy damage path.
/// </summary>
public sealed class MeleeCombatSystem
{
    private static readonly UnitHealthPartId[] HitPartOrder =
    {
        UnitHealthPartId.Torso, UnitHealthPartId.Torso, UnitHealthPartId.Torso,
        UnitHealthPartId.Head, UnitHealthPartId.Neck,
        UnitHealthPartId.LeftArm, UnitHealthPartId.RightArm,
        UnitHealthPartId.LeftLeg, UnitHealthPartId.RightLeg,
        UnitHealthPartId.LeftHand, UnitHealthPartId.RightHand
    };

    private static readonly UnitHealthPartId[] HitPartFallback =
    {
        UnitHealthPartId.Torso, UnitHealthPartId.Head, UnitHealthPartId.Neck,
        UnitHealthPartId.LeftArm, UnitHealthPartId.RightArm,
        UnitHealthPartId.LeftHand, UnitHealthPartId.RightHand,
        UnitHealthPartId.LeftLeg, UnitHealthPartId.RightLeg,
        UnitHealthPartId.WholeBody
    };

    private readonly DamageSystem _damageSystem = new();
    private float[] _attackCooldown = Array.Empty<float>();
    private float[] _riposteCooldown = Array.Empty<float>();
    private uint[] _randomState = Array.Empty<uint>();

    public long TotalSwings { get; private set; }
    public long TotalHits { get; private set; }
    public long TotalMisses { get; private set; }
    public long TotalBlocked { get; private set; }
    public long TotalDodges { get; private set; }
    public long TotalParries { get; private set; }
    public long TotalRipostes { get; private set; }
    public long TotalCriticals { get; private set; }

    public void InitializeUnit(int unitIndex, UnitId id)
    {
        EnsureCapacity(unitIndex + 1);
        _attackCooldown[unitIndex] = 0f;
        _riposteCooldown[unitIndex] = 0f;
        _randomState[unitIndex] = Seed(id);
    }

    public void ClearUnit(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)_attackCooldown.Length)
            return;

        _attackCooldown[unitIndex] = 0f;
        _riposteCooldown[unitIndex] = 0f;
        _randomState[unitIndex] = 0u;
    }

    public void Update(
        UnitStore units,
        UnitAiStore ai,
        UnitInventoryStore inventory,
        UnitHealthStore health,
        UnitHealthSystem healthSystem,
        UnitSkillStore skills,
        UnitSuppressionStore suppression,
        UnitHitRegionStore hitRegions,
        UnitBodyStore bodies,
        float deltaTime)
    {
        if (deltaTime <= 0f || units.ActiveCount == 0)
            return;

        EnsureCapacity(units.Capacity);
        ReadOnlySpan<int> active = units.ActiveIndices;

        for (int i = 0; i < active.Length; i++)
        {
            int attacker = active[i];
            _attackCooldown[attacker] = MathF.Max(0f, _attackCooldown[attacker] - deltaTime);
            _riposteCooldown[attacker] = MathF.Max(0f, _riposteCooldown[attacker] - deltaTime);

            if (_randomState[attacker] == 0u)
                _randomState[attacker] = Seed(units.GetId(attacker));

            if (health.OverallHitPoints[attacker] <= 0f ||
                !ai.HasTarget[attacker] ||
                !units.TryGetIndex(ai.Target[attacker], out int target) ||
                target == attacker ||
                health.OverallHitPoints[target] <= 0f ||
                !FactionRules.ShouldAttack(units.FactionTag[attacker], units.FactionTag[target]))
            {
                continue;
            }

            MeleeProfile profile = GetProfile(attacker, units, inventory);
            Vector3 delta = units.Position[target] - units.Position[attacker];
            float reach = profile.Reach +
                MathF.Max(0f, units.Radius[attacker]) +
                MathF.Max(0f, units.Radius[target]);

            if (delta.LengthSquared() > reach * reach || _attackCooldown[attacker] > 0f)
                continue;

            _attackCooldown[attacker] = profile.AttackInterval;
            TotalSwings++;

            ResolveStrike(
                attacker, target, profile, units, inventory, health,
                healthSystem, skills, suppression, hitRegions, bodies,
                allowDefense: true);
        }
    }

    private bool ResolveStrike(
        int attacker,
        int target,
        MeleeProfile profile,
        UnitStore units,
        UnitInventoryStore inventory,
        UnitHealthStore health,
        UnitHealthSystem healthSystem,
        UnitSkillStore skills,
        UnitSuppressionStore suppression,
        UnitHitRegionStore hitRegions,
        UnitBodyStore bodies,
        bool allowDefense)
    {
        if (health.OverallHitPoints[attacker] <= 0f ||
            health.OverallHitPoints[target] <= 0f)
            return false;

        MeleeProfile defenderProfile = GetProfile(target, units, inventory);
        int attackerSkill = skills.GetLevel(attacker, UnitSkill.Melee);
        int defenderSkill = skills.GetLevel(target, UnitSkill.Melee);

        if (allowDefense)
        {
            float postureFactor = units.Posture[target] switch
            {
                UnitPosture.Lying => 0.25f,
                UnitPosture.Crouching => 0.78f,
                UnitPosture.Sitting => 0.65f,
                _ => 1f
            };
            float movementBonus = MathF.Min(0.08f, units.Velocity[target].Length() * 0.02f);
            float suppressionPenalty = suppression.Value[target] * 0.012f;
            float dodgeChance = Math.Clamp(
                (0.035f + defenderSkill * 0.008f + movementBonus - suppressionPenalty) *
                postureFactor,
                0.01f,
                0.32f);

            if (NextUnit(target) < dodgeChance)
            {
                TotalDodges++;
                return false;
            }

            float parryPassion = GetPassionBonus(skills, target, UnitSkill.Melee);
            float parryChance = Math.Clamp(
                defenderProfile.ParryChance + defenderSkill * 0.005f +
                parryPassion - suppressionPenalty,
                0f,
                0.48f);

            if (NextUnit(target) < parryChance)
            {
                TotalParries++;
                if (_riposteCooldown[target] <= 0f)
                {
                    _riposteCooldown[target] = MathF.Max(0.35f, defenderProfile.AttackInterval * 1.2f);
                    TotalRipostes++;
                    ResolveStrike(
                        target, attacker, defenderProfile, units, inventory, health,
                        healthSystem, skills, suppression, hitRegions, bodies,
                        allowDefense: false);
                }

                return false;
            }

            float missChance = Math.Clamp(
                0.22f - attackerSkill * 0.012f + defenderSkill * 0.003f,
                0.04f,
                0.24f);
            if (NextUnit(attacker) < missChance)
            {
                TotalMisses++;
                return false;
            }
        }

        if (!TryChooseHitBinding(
                attacker, target, units, hitRegions,
                out UnitHealthPartId part, out int regionIndex, out int bindingIndex))
        {
            TotalMisses++;
            return false;
        }

        Vector3 impact = UnitHitSystem.GetRegionPoint(
            units, target, regionIndex, units.Position[attacker],
            hitRegions, UnitAnatomyMarkerRole.Aim, bodies);

        if (!UnitHitSystem.IsPointInsideRegion(
                units, target, regionIndex, part, impact, 0.06f, hitRegions, bodies))
        {
            impact = UnitHitSystem.GetRegionGeometry(
                units, target, regionIndex, hitRegions, bodies).Center;

            if (!UnitHitSystem.IsPointInsideRegion(
                    units, target, regionIndex, part, impact, 0.06f, hitRegions, bodies))
            {
                TotalMisses++;
                return false;
            }
        }

        float passionBonus = GetPassionBonus(skills, attacker, UnitSkill.Melee);
        float criticalChance = Math.Clamp(
            profile.CriticalChance + attackerSkill * 0.0035f + passionBonus * 0.5f,
            0.01f,
            0.40f);
        bool critical = NextUnit(attacker) < criticalChance;

        if (critical)
            TotalCriticals++;

        float damage = profile.Damage * (1f + attackerSkill * 0.015f);
        if (critical)
            damage *= 1.75f;

        Vector3 direction = units.Position[target] - units.Position[attacker];
        float directionLength = direction.Length();
        direction = directionLength > 0.0001f ? direction / directionLength : Vector3.UnitX;

        float energy = MathF.Max(50f, damage * 30f);
        float penetration = profile.DamageType == DamageType.Blunt
            ? profile.BluntPenetration
            : profile.ArmorPenetration;

        var damageEvent = new DamageEvent(
            units.GetId(attacker),
            units.GetId(target),
            part,
            impact,
            direction,
            damage,
            energy,
            energy,
            penetration,
            profile.BluntPenetration,
            profile.DamageType,
            profile.BleedChance,
            regionIndex,
            0.06f,
            bindingIndex);

        ProjectileDamageResult result = _damageSystem.ApplyProjectile(
            units, inventory, health, healthSystem, damageEvent, hitRegions, bodies);

        TotalHits++;
        if (result.DamageApplied <= 0.001f)
            TotalBlocked++;

        return true;
    }

    private bool TryChooseHitBinding(
        int attacker,
        int target,
        UnitStore units,
        UnitHitRegionStore hitRegions,
        out UnitHealthPartId part,
        out int regionIndex,
        out int bindingIndex)
    {
        for (int i = 0; i < 8; i++)
        {
            UnitHealthPartId candidate = HitPartOrder[
                Math.Clamp((int)(NextUnit(attacker) * HitPartOrder.Length), 0, HitPartOrder.Length - 1)];

            if (UnitHitSystem.TryFindBindingForPart(
                    units, target, candidate, out regionIndex, out bindingIndex, hitRegions))
            {
                part = candidate;
                return true;
            }
        }

        for (int i = 0; i < HitPartFallback.Length; i++)
        {
            UnitHealthPartId candidate = HitPartFallback[i];
            if (UnitHitSystem.TryFindBindingForPart(
                    units, target, candidate, out regionIndex, out bindingIndex, hitRegions))
            {
                part = candidate;
                return true;
            }
        }

        part = UnitHealthPartId.None;
        regionIndex = -1;
        bindingIndex = -1;
        return false;
    }

    private static MeleeProfile GetProfile(
        int unitIndex,
        UnitStore units,
        UnitInventoryStore inventory)
    {
        short slot = inventory.GetWeaponEquipment(unitIndex, UnitWeaponSlot.Melee);
        MeleeWeaponConfig? weapon = slot >= 0
            ? inventory.GetItem(unitIndex, slot) as MeleeWeaponConfig
            : null;

        return weapon == null
            ? MeleeProfile.Unarmed
            : new MeleeProfile(weapon);
    }

    private float NextUnit(int unitIndex)
    {
        uint state = _randomState[unitIndex];
        if (state == 0u)
            state = 0xA341316Cu;

        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        _randomState[unitIndex] = state;

        return (state & 0x00FFFFFFu) / 16777216f;
    }

    private static uint Seed(UnitId id)
    {
        uint seed = unchecked(0x9E3779B9u ^
            (uint)(id.Index + 1) * 2654435761u ^
            id.Generation * 2246822519u);
        return seed == 0u ? 0xA341316Cu : seed;
    }

    private static float GetPassionBonus(
        UnitSkillStore skills,
        int unitIndex,
        UnitSkill skill) =>
        skills.GetPassion(unitIndex, skill) switch
        {
            UnitSkillPassion.Major => 0.12f,
            UnitSkillPassion.Minor => 0.06f,
            _ => 0f
        };

    private void EnsureCapacity(int required)
    {
        if (required <= _attackCooldown.Length)
            return;

        int capacity = Math.Max(required, Math.Max(16, _attackCooldown.Length * 2));
        Array.Resize(ref _attackCooldown, capacity);
        Array.Resize(ref _riposteCooldown, capacity);
        Array.Resize(ref _randomState, capacity);
    }

    private readonly struct MeleeProfile
    {
        public static MeleeProfile Unarmed { get; } =
            new(8f, 0.95f, 0.80f, DamageType.Blunt, 1f, 8f, 0.025f, 0.04f);

        public float Damage { get; }
        public float Reach { get; }
        public float AttackInterval { get; }
        public DamageType DamageType { get; }
        public float ArmorPenetration { get; }
        public float BluntPenetration { get; }
        public float CriticalChance { get; }
        public float ParryChance { get; }
        public float BleedChance { get; }

        public MeleeProfile(MeleeWeaponConfig weapon)
            : this(
                weapon.BaseDamage,
                weapon.Reach,
                weapon.FireRate,
                weapon.MeleeDamageType,
                weapon.ArmorPenetration,
                weapon.BluntPenetration,
                weapon.CriticalChance,
                weapon.ParryChance,
                weapon.BleedChance)
        {
        }

        private MeleeProfile(
            float damage,
            float reach,
            float attackInterval,
            DamageType damageType,
            float armorPenetration,
            float bluntPenetration,
            float criticalChance,
            float parryChance,
            float bleedChance = 0f)
        {
            Damage = MathF.Max(0f, damage);
            Reach = Math.Clamp(reach, 0.2f, 3f);
            AttackInterval = Math.Clamp(attackInterval, 0.15f, 5f);
            DamageType = damageType;
            ArmorPenetration = MathF.Max(0f, armorPenetration);
            BluntPenetration = MathF.Max(0f, bluntPenetration);
            CriticalChance = Math.Clamp(criticalChance, 0f, 0.8f);
            ParryChance = Math.Clamp(parryChance, 0f, 0.5f);
            BleedChance = Math.Clamp(bleedChance, 0f, 1f);
        }
    }
}
