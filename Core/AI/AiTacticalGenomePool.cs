using System;
using System.Collections.Generic;
using System.Globalization;

namespace Core.Unit;

public sealed record AiTacticalGenome
{
    public string Name { get; init; } = "genome";
    public bool IsBaseline { get; init; }
    public double TrainingFitness { get; init; }
    public double ValidationFitness { get; init; }
    public AiBalanceProfile? Profile { get; init; } = AiBalanceProfile.Default;
}

/// <summary>
/// A small, validated population of personal combat tactics. A pawn keeps its assigned
/// profile for its lifetime; its identity makes assignment stable instead of rerolling
/// every decision or frame.
/// </summary>
public sealed record AiTacticalGenomePool
{
    public int Version { get; init; } = 1;
    public string CreatedUtc { get; init; } = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    public int ValidationSeed { get; init; }
    public double BaselineFitness { get; init; }
    public List<AiTacticalGenome> Genomes { get; init; } = new();

    public AiTacticalGenomePool Sanitized()
    {
        var valid = new List<AiTacticalGenome>(Genomes?.Count ?? 0);
        if (Genomes != null)
        {
            foreach (AiTacticalGenome? genome in Genomes)
            {
                if (genome == null || genome.Profile == null ||
                    !double.IsFinite(genome.TrainingFitness) ||
                    !double.IsFinite(genome.ValidationFitness))
                    continue;
                valid.Add(genome);
            }
        }

        return this with { Genomes = valid };
    }

    public AiBalanceProfile? SelectProfile(int unitIndex, uint unitGeneration, ushort factionTag)
    {
        if (Genomes == null || Genomes.Count == 0)
            return null;

        uint hash = unchecked(
            (uint)(unitIndex + 1) * 2654435761u ^
            unitGeneration * 2246822519u ^
            (uint)factionTag * 3266489917u);
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        hash *= 0x846CA68Bu;
        hash ^= hash >> 16;

        int index = (int)(hash % (uint)Genomes.Count);
        return Genomes[index].Profile;
    }
}
