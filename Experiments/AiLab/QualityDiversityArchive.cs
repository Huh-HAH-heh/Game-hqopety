namespace RimClone.AiLab;

/// <summary>
/// Compact MAP-Elites-style archive. It retains the best genome in each activity/contact-
/// avoidance cell, so evolution can preserve useful tactical styles instead of only one champion.
/// </summary>
public sealed class QualityDiversityArchive
{
    private const int Bins = 5;
    private readonly Dictionary<int, ArchiveEntry> _cells = new();

    public int Count => _cells.Count;

    public void Add(CandidateEvaluation result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int activity = Bin(result.Activity);
        int contactAvoidance = Bin(1d - result.VisualExposureRatio);
        int key = activity * Bins + contactAvoidance;

        if (_cells.TryGetValue(key, out ArchiveEntry? current) &&
            current.Fitness >= result.Fitness)
        {
            return;
        }

        _cells[key] = new ArchiveEntry(
            result.Generation,
            result.CandidateIndex,
            result.Fitness,
            activity,
            contactAvoidance,
            result.Genome.Copy());
    }

    public ArchiveEntry[] GetEntries() =>
        _cells.Values.OrderByDescending(x => x.Fitness).ToArray();

    public static int Bin(double value) =>
        Math.Clamp((int)(Math.Clamp(value, 0d, 0.999999d) * Bins), 0, Bins - 1);
}

public sealed record ArchiveEntry(
    int Generation,
    int CandidateIndex,
    double Fitness,
    int ActivityBin,
    int ContactAvoidanceBin,
    TacticalGenome Genome);
