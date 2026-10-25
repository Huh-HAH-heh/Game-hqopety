using System;
using System.Numerics;
using Core.AI;
using Core.AI.Perception;
using Core.Map;

namespace Core.Unit;

public readonly record struct AiStealthRisk(float Exposure, float Proximity)
{
    public float Combined => Math.Clamp(Exposure * 0.72f + Proximity * 0.58f, 0f, 1f);
}

public readonly record struct AiStealthWaypointPlan(
    Vector3 Destination,
    UnitPosture RecommendedPosture,
    AiStealthRisk Risk);

/// <summary>
/// Safe-waypoint planner for an explicit StealthMove order. It can use only the mover's
/// remembered/reported contacts, never live enemy positions. Contact uncertainty widens
/// the risk region; unknown-facing contacts are treated conservatively.
/// </summary>
public static class AiStealthMovementPlanner
{
    private static readonly float[] HeadingOffsetsDegrees =
        { -150f, -120f, -90f, -60f, -35f, 0f, 35f, 60f, 90f, 120f, 150f, 180f };

    private static readonly float[] LookAheadFractions = { 0.60f, 1f, 1.45f };

    public static AiStealthWaypointPlan SelectNextWaypoint(
        UnitStore units,
        WorldMap worldMap,
        VisionSystem vision,
        PerceptionMemoryStore? perception,
        int unitIndex,
        Vector3 destination,
        AiStealthProfile? configuredProfile)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(worldMap);
        ArgumentNullException.ThrowIfNull(vision);
        if ((uint)unitIndex >= (uint)units.Capacity)
            throw new ArgumentOutOfRangeException(nameof(unitIndex));

        AiStealthProfile profile = (configuredProfile ?? AiStealthProfile.Default).Sanitized();
        Vector3 current = units.Position[unitIndex];
        AiStealthRisk currentRisk = MeasureRisk(
            units, worldMap, vision, perception, unitIndex, current, profile);

        float remainingDistance = Distance2D(current, destination);
        float bestScore = float.NegativeInfinity;
        Vector3 bestDestination = current;
        AiStealthRisk bestRisk = currentRisk;

        // Do not select the final destination unconditionally: if it is exposed, a
        // safer intermediate waypoint should win. Consider the goal only when it is
        // within the current local planning horizon.
        if (remainingDistance <= profile.LookAheadDistance * 1.45f)
        {
            ConsiderCandidate(
                units, worldMap, vision, perception, unitIndex, current, destination,
                destination, profile, remainingDistance, ref bestScore,
                ref bestDestination, ref bestRisk);
        }

        float heading = MathF.Atan2(destination.Y - current.Y, destination.X - current.X);
        for (int distanceIndex = 0; distanceIndex < LookAheadFractions.Length; distanceIndex++)
        {
            float lookAhead = profile.LookAheadDistance * LookAheadFractions[distanceIndex];
            for (int directionIndex = 0; directionIndex < HeadingOffsetsDegrees.Length; directionIndex++)
            {
                float angle = heading + HeadingOffsetsDegrees[directionIndex] * MathF.PI / 180f;
                float x = current.X + MathF.Cos(angle) * lookAhead;
                float y = current.Y + MathF.Sin(angle) * lookAhead;
                int tileX = (int)MathF.Floor(x);
                int tileY = (int)MathF.Floor(y);
                if ((uint)tileX >= (uint)worldMap.TileWidth ||
                    (uint)tileY >= (uint)worldMap.TileHeight ||
                    worldMap.IsNavigationBlocked(tileX, tileY) ||
                    worldMap.GetSurfaceLayer(tileX, tileY) < 0)
                    continue;

                Vector3 candidate = new(
                    tileX + 0.5f,
                    tileY + 0.5f,
                    worldMap.GetSurfaceHeight(tileX, tileY));

                if (Distance2D(current, candidate) < 1.5f)
                    continue;

                ConsiderCandidate(
                    units, worldMap, vision, perception, unitIndex, current, destination,
                    candidate, profile, remainingDistance, ref bestScore,
                    ref bestDestination, ref bestRisk);
            }
        }

        // If all sampled destinations were blocked, preserve the requested route as
        // a fallback; the shared navigation system still validates/pathfinds it.
        if (!float.IsFinite(bestScore))
        {
            bestDestination = destination;
            bestRisk = MeasureRisk(
                units, worldMap, vision, perception, unitIndex, destination, profile);
        }

        return new AiStealthWaypointPlan(
            SurfacePoint(worldMap, bestDestination),
            SelectPosture(currentRisk, profile),
            currentRisk);
    }

    public static AiStealthRisk MeasureRisk(
        UnitStore units,
        WorldMap worldMap,
        VisionSystem vision,
        PerceptionMemoryStore? perception,
        int unitIndex,
        Vector3 candidatePosition,
        AiStealthProfile? configuredProfile)
    {
        AiStealthProfile profile = (configuredProfile ?? AiStealthProfile.Default).Sanitized();
        if (perception == null ||
            !perception.TryGetMemory(unitIndex, out TacticalContactMemory memory) ||
            memory.Contacts.Count == 0)
            return default;

        float exposureSurvival = 1f;
        float proximitySurvival = 1f;
        float now = memory.CurrentTimeSeconds;
        ushort ownFaction = units.FactionTag[unitIndex];
        float eyeOffset = units.Posture[unitIndex] switch
        {
            UnitPosture.Lying => 0.48f,
            UnitPosture.Crouching => 0.92f,
            UnitPosture.Sitting => 1.08f,
            _ => 1.45f
        };
        Vector3 candidateEye = candidatePosition + new Vector3(0f, 0f, eyeOffset);

        for (int i = 0; i < memory.Contacts.Count; i++)
        {
            TacticalContact contact = memory.Contacts[i];
            if (contact.ExpiresAtSeconds <= now || !IsThreatContact(units, ownFaction, contact))
                continue;

            float age = MathF.Max(0f, now - contact.LastReceivedAtSeconds);
            float contactHalfLife = contact.LastStimulusType == TacticalStimulusType.AllyContactReport ? 65f : 18f;
            float freshness = MathF.Exp(-0.69314718f * age / contactHalfLife);
            float confidence = Math.Clamp(contact.Confidence * freshness, 0f, 1f);
            if (confidence < 0.035f)
                continue;

            Vector3 threatPosition = contact.EstimatedPosition;
            float dx = candidatePosition.X - threatPosition.X;
            float dy = candidatePosition.Y - threatPosition.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            float dangerRange = profile.EstimatedThreatRange +
                Math.Clamp(contact.PositionErrorRadius, 0f, 20f);
            float maxRelevantDistance = dangerRange + profile.LookAheadDistance * 1.5f;
            if (distance > maxRelevantDistance)
                continue;

            float proximity = Math.Clamp(
                (dangerRange - distance) / MathF.Max(1f, dangerRange), 0f, 1f);

            // LOS is evaluated only against an uncertain contact estimate. It is not
            // an input to the pawn's state and cannot reveal unobserved live positions.
            Vector3 threatEye = threatPosition + new Vector3(0f, 0f, 1.45f);
            bool lineOfSight = vision.HasLineOfSight(worldMap, threatEye, candidateEye, out _);
            float distanceGate = Math.Clamp(
                (maxRelevantDistance - distance) / MathF.Max(1f, profile.LookAheadDistance * 1.5f), 0f, 1f);
            float exposedChance = lineOfSight ? confidence * distanceGate : 0f;

            exposureSurvival *= 1f - exposedChance;
            proximitySurvival *= 1f - confidence * proximity;
        }

        return new AiStealthRisk(1f - exposureSurvival, 1f - proximitySurvival);
    }

    public static UnitPosture SelectPosture(AiStealthRisk risk, AiStealthProfile? configuredProfile)
    {
        AiStealthProfile profile = (configuredProfile ?? AiStealthProfile.Default).Sanitized();
        float combined = risk.Combined;
        if (combined >= profile.CrawlRiskThreshold)
            return UnitPosture.Lying;
        if (combined >= profile.CrawlRiskThreshold * 0.52f)
            return UnitPosture.Crouching;
        return UnitPosture.Standing;
    }

    private static void ConsiderCandidate(
        UnitStore units,
        WorldMap worldMap,
        VisionSystem vision,
        PerceptionMemoryStore? perception,
        int unitIndex,
        Vector3 current,
        Vector3 destination,
        Vector3 candidate,
        AiStealthProfile profile,
        float remainingDistance,
        ref float bestScore,
        ref Vector3 bestDestination,
        ref AiStealthRisk bestRisk)
    {
        candidate = SurfacePoint(worldMap, candidate);
        if (!IsPassable(worldMap, candidate))
            return;

        float travelDistance = Distance2D(current, candidate);
        if (travelDistance < 1.5f)
            return;

        float distanceAfter = Distance2D(candidate, destination);
        float progress = Math.Clamp(
            remainingDistance - distanceAfter,
            -profile.LookAheadDistance,
            profile.LookAheadDistance * 1.45f);
        AiStealthRisk risk = MeasureRisk(
            units, worldMap, vision, perception, unitIndex, candidate, profile);

        float score =
            progress * profile.ProgressWeight -
            travelDistance * profile.TravelCostWeight -
            risk.Exposure * profile.ExposureWeight * 2.0f -
            risk.Proximity * profile.ProximityWeight;

        // Avoid reversing direction unless the move meaningfully reduces exposure.
        if (progress < -0.5f)
            score -= MathF.Abs(progress) * profile.ProgressWeight * 0.55f;

        if (score <= bestScore)
            return;

        bestScore = score;
        bestDestination = candidate;
        bestRisk = risk;
    }

    private static bool IsThreatContact(UnitStore units, ushort ownFaction, TacticalContact contact)
    {
        if (contact.SubjectUnit is UnitId subject &&
            units.TryGetIndex(subject, out int subjectIndex))
            return FactionRules.ShouldAttack(ownFaction, units.FactionTag[subjectIndex]);

        for (int i = 0; i < contact.Observations.Count; i++)
        {
            PerceptionSignalFlags signals = contact.Observations[i].Signals;
            if ((signals & (PerceptionSignalFlags.PotentialThreat |
                            PerceptionSignalFlags.ImmediateDanger |
                            PerceptionSignalFlags.Avoid)) != 0)
                return true;
        }

        return false;
    }

    private static bool IsPassable(WorldMap worldMap, Vector3 position)
    {
        int x = (int)MathF.Floor(position.X);
        int y = (int)MathF.Floor(position.Y);
        return (uint)x < (uint)worldMap.TileWidth &&
               (uint)y < (uint)worldMap.TileHeight &&
               !worldMap.IsNavigationBlocked(x, y) &&
               worldMap.GetSurfaceLayer(x, y) >= 0;
    }

    private static Vector3 SurfacePoint(WorldMap worldMap, Vector3 position)
    {
        int x = Math.Clamp((int)MathF.Floor(position.X), 0, worldMap.MaxTileX);
        int y = Math.Clamp((int)MathF.Floor(position.Y), 0, worldMap.MaxTileY);
        return new Vector3(position.X, position.Y, worldMap.GetSurfaceHeight(x, y));
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
