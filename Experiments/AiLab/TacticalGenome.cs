using Core.Unit;

namespace RimClone.AiLab;

/// <summary>
/// Small, purpose-specific genome for the shared tactical preferences already consumed
/// by Utility AI. Each experiment mutates only the gene indices it declares.
/// </summary>
public sealed class TacticalGenome
{
    public const int GeneCount = 7;

    private static readonly float[] Min = [.65f, .55f, .65f, .70f, .35f, .50f, .50f];
    private static readonly float[] Max = [1.35f, 1.45f, 1.45f, 1.40f, 1.25f, 1.50f, 1.50f];

    public float[] Values { get; }

    public TacticalGenome(float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != GeneCount)
            throw new ArgumentException($"Expected {GeneCount} genes.", nameof(values));

        Values = (float[])values.Clone();
        for (int i = 0; i < Values.Length; i++)
        {
            if (!float.IsFinite(Values[i]))
                throw new ArgumentException($"Gene {i} is not finite.", nameof(values));
            Values[i] = Math.Clamp(Values[i], Min[i], Max[i]);
        }
    }

    public static TacticalGenome FromProfile(AiBalanceProfile profile) =>
        new([
            profile.EngageScale,
            profile.FlankScale,
            profile.CoverScale,
            profile.RetreatScale,
            profile.ProneScale,
            profile.SuppressiveFireScale,
            profile.SquadContactScale
        ]);

    public AiBalanceProfile ApplyTo(AiBalanceProfile baseline) => baseline with
    {
        EngageScale = Values[0],
        FlankScale = Values[1],
        CoverScale = Values[2],
        RetreatScale = Values[3],
        ProneScale = Values[4],
        SuppressiveFireScale = Values[5],
        SquadContactScale = Values[6]
    };

    public TacticalGenome Copy() => new(Values);

    public TacticalGenome Crossover(TacticalGenome other, LabExperimentDefinition definition, Random random)
    {
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(random);

        float[] child = (float[])Values.Clone();
        foreach (int gene in definition.ActiveGenes)
            child[gene] = random.Next(2) == 0 ? Values[gene] : other.Values[gene];
        return new TacticalGenome(child);
    }

    public TacticalGenome Mutate(
        LabExperimentDefinition definition,
        Random random,
        double perGeneChance,
        float strength)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(random);
        if (!double.IsFinite(perGeneChance) || perGeneChance < 0d || perGeneChance > 1d)
            throw new ArgumentOutOfRangeException(nameof(perGeneChance));
        if (!float.IsFinite(strength) || strength < 0f || strength > 1f)
            throw new ArgumentOutOfRangeException(nameof(strength));

        float[] child = (float[])Values.Clone();
        foreach (int gene in definition.ActiveGenes)
        {
            if (random.NextDouble() >= perGeneChance)
                continue;

            float delta = ((float)random.NextDouble() * 2f - 1f) * (Max[gene] - Min[gene]) * strength;
            child[gene] = Math.Clamp(child[gene] + delta, Min[gene], Max[gene]);
        }
        return new TacticalGenome(child);
    }

    public string ToCompactString() =>
        $"engage={Values[0]:0.00}; flank={Values[1]:0.00}; cover={Values[2]:0.00}; " +
        $"retreat={Values[3]:0.00}; prone={Values[4]:0.00}; suppress={Values[5]:0.00}; squad-contact={Values[6]:0.00}";
}
