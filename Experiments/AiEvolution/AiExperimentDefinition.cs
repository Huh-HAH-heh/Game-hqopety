using System;
using System.Collections.Generic;

namespace RimClone.Experiments.AiEvolution;

public enum AiExperimentScope
{
    Unit,
    Squad
}

public enum AiExperimentGoal
{
    GeneralCombat,
    RemainUndetected,
    BreakContactAfterDetection,
    SeekCoverUnderFire,
    WeaponEmployment,
    SquadCoordination,
    SquadWeaponEmployment
}

/// <summary>
/// Describes an experiment independently of its evolutionary algorithm or UI.
/// Scenario-set identifiers are resolved by an evaluator; they must not provide
/// privileged world-truth observations to an individual pawn.
/// </summary>
public sealed record AiExperimentDefinition(
    string Id,
    string DisplayName,
    AiExperimentScope Scope,
    AiExperimentGoal Goal,
    string ScenarioSetId,
    int PopulationSize = 32,
    int MatchesPerCandidate = 8,
    int MaxTicksPerMatch = 600,
    int HoldoutMatches = 16)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new ArgumentException("Experiment id is required.", nameof(Id));
        if (string.IsNullOrWhiteSpace(DisplayName))
            throw new ArgumentException("Experiment display name is required.", nameof(DisplayName));
        if (string.IsNullOrWhiteSpace(ScenarioSetId))
            throw new ArgumentException("Scenario set id is required.", nameof(ScenarioSetId));
        if (PopulationSize < 4)
            throw new ArgumentOutOfRangeException(nameof(PopulationSize), "Population must be at least 4.");
        if (MatchesPerCandidate < 2)
            throw new ArgumentOutOfRangeException(nameof(MatchesPerCandidate), "At least two matches are required.");
        if (MaxTicksPerMatch < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxTicksPerMatch));
        if (HoldoutMatches < 1)
            throw new ArgumentOutOfRangeException(nameof(HoldoutMatches));

        if (Scope == AiExperimentScope.Unit &&
            Goal is AiExperimentGoal.SquadCoordination or AiExperimentGoal.SquadWeaponEmployment)
            throw new ArgumentException("Squad goals require a squad-scoped experiment.");
        if (Scope == AiExperimentScope.Squad &&
            Goal is AiExperimentGoal.RemainUndetected or AiExperimentGoal.BreakContactAfterDetection or AiExperimentGoal.SeekCoverUnderFire)
            throw new ArgumentException("This objective is currently defined for individual-unit experiments.");
    }
}

/// <summary>
/// Ready-to-configure experiment types. The runnable evaluator currently runs
/// general combat only; specialized goals are declarations for the upcoming
/// scenario/evaluator stages and must not be reported as trained until implemented.
/// </summary>
public static class AiExperimentPresets
{
    public static IReadOnlyList<AiExperimentDefinition> All { get; } = Array.AsReadOnly(
    [
        new("unit-general-combat", "Individual combat policy", AiExperimentScope.Unit,
            AiExperimentGoal.GeneralCombat, "combat-basic"),
        new("unit-remain-undetected", "Remain undetected", AiExperimentScope.Unit,
            AiExperimentGoal.RemainUndetected, "concealment"),
        new("unit-break-contact", "Break contact after detection", AiExperimentScope.Unit,
            AiExperimentGoal.BreakContactAfterDetection, "break-contact"),
        new("unit-cover-under-fire", "Find cover under fire", AiExperimentScope.Unit,
            AiExperimentGoal.SeekCoverUnderFire, "cover-under-fire"),
        new("unit-weapon-employment", "Weapon-specific employment", AiExperimentScope.Unit,
            AiExperimentGoal.WeaponEmployment, "weapon-employment"),
        new("squad-coordination", "Squad tactical doctrine", AiExperimentScope.Squad,
            AiExperimentGoal.SquadCoordination, "squad-tactics"),
        new("squad-weapon-employment", "Squad weapon doctrine", AiExperimentScope.Squad,
            AiExperimentGoal.SquadWeaponEmployment, "squad-weapon-employment")
    ]);

    public static bool TryGet(string id, out AiExperimentDefinition? definition)
    {
        foreach (AiExperimentDefinition item in All)
        {
            if (!string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
                continue;
            definition = item;
            return true;
        }
        definition = null;
        return false;
    }
}

public sealed record AiBattleJob(int Index, int ScenarioId, int Seed, bool CandidateStartsLeft);
