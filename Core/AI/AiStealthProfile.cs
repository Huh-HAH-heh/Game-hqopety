namespace Core.Unit;

/// <summary>
/// Tunable policy for the explicit StealthMove task. The vision and movement rules
/// remain simulation rules; these fields only express the pawn's trade-off between
/// progress and avoiding remembered/reported threats.
/// </summary>
public sealed record AiStealthProfile
{
    public static AiStealthProfile Default { get; } = new();

    public float ProgressWeight { get; init; } = 2.2f;
    public float ExposureWeight { get; init; } = 5.0f;
    public float ProximityWeight { get; init; } = 2.0f;
    public float TravelCostWeight { get; init; } = 0.18f;
    public float EstimatedThreatRange { get; init; } = 30f;
    public float LookAheadDistance { get; init; } = 7f;
    public float CrawlRiskThreshold { get; init; } = 0.42f;

    public AiStealthProfile Sanitized() => this with
    {
        ProgressWeight = Clamp(ProgressWeight, 0.25f, 6f, Default.ProgressWeight),
        ExposureWeight = Clamp(ExposureWeight, 0.25f, 12f, Default.ExposureWeight),
        ProximityWeight = Clamp(ProximityWeight, 0f, 8f, Default.ProximityWeight),
        TravelCostWeight = Clamp(TravelCostWeight, 0f, 1.5f, Default.TravelCostWeight),
        EstimatedThreatRange = Clamp(EstimatedThreatRange, 6f, 80f, Default.EstimatedThreatRange),
        LookAheadDistance = Clamp(LookAheadDistance, 2f, 18f, Default.LookAheadDistance),
        CrawlRiskThreshold = Clamp(CrawlRiskThreshold, 0.08f, 0.95f, Default.CrawlRiskThreshold)
    };

    private static float Clamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
