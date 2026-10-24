using System;
using Core.Map;
using Core.Unit;

namespace Core.AI.Perception;

/// <summary>
/// Shared read context for all sensory modules. Each module owns its detection rules
/// and the uncertainty of observations it publishes.
/// </summary>
public readonly struct PerceptionSenseContext
{
    public UnitStore Units { get; }
    public UnitHealthStore Health { get; }
    public UnitInventoryStore Inventory { get; }
    public UnitWeaponStore Weapons { get; }
    public UnitSuppressionStore Suppression { get; }
    public UnitMetabolismStore Metabolism { get; }
    public UnitSkillStore Skills { get; }
    public UnitBodyStore Bodies { get; }
    public UnitHitRegionStore HitRegions { get; }
    public WorldMap WorldMap { get; }
    public VisionSystem Vision { get; }
    public IPerceptionObservationSink ObservationSink { get; }
    public float SimulationTimeSeconds { get; }
    public float DeltaTime { get; }

    public PerceptionSenseContext(
        UnitStore units, UnitHealthStore health, UnitInventoryStore inventory,
        UnitWeaponStore weapons, UnitSuppressionStore suppression,
        UnitMetabolismStore metabolism, UnitSkillStore skills, UnitBodyStore bodies,
        UnitHitRegionStore hitRegions, WorldMap worldMap, VisionSystem vision,
        IPerceptionObservationSink observationSink, float simulationTimeSeconds,
        float deltaTime)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(weapons);
        ArgumentNullException.ThrowIfNull(suppression);
        ArgumentNullException.ThrowIfNull(metabolism);
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(bodies);
        ArgumentNullException.ThrowIfNull(hitRegions);
        ArgumentNullException.ThrowIfNull(worldMap);
        ArgumentNullException.ThrowIfNull(vision);
        ArgumentNullException.ThrowIfNull(observationSink);
        if (!float.IsFinite(simulationTimeSeconds) || simulationTimeSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(simulationTimeSeconds));

        Units = units;
        Health = health;
        Inventory = inventory;
        Weapons = weapons;
        Suppression = suppression;
        Metabolism = metabolism;
        Skills = skills;
        Bodies = bodies;
        HitRegions = hitRegions;
        WorldMap = worldMap;
        Vision = vision;
        ObservationSink = observationSink;
        SimulationTimeSeconds = simulationTimeSeconds;
        DeltaTime = deltaTime;
    }

    /// <summary>Publishes through the shared contact store and checks channel ownership.</summary>
    public TacticalContact? Publish(
        PerceptionSense channel, int observerIndex, PerceptionObservation observation,
        long? correlatedContactId = null)
    {
        if (!Enum.IsDefined(channel) || channel == PerceptionSense.Unknown)
            throw new ArgumentOutOfRangeException(nameof(channel));
        if (observation.Sense != channel)
            throw new ArgumentException("Observation channel must match the publishing sense.", nameof(observation));
        return ObservationSink.RecordObservation(observerIndex, observation, correlatedContactId);
    }
}
