using System;
using System.Collections.Generic;
using Core.Map;
using Core.Unit;

namespace Core.World;

/// <summary>
/// Owns the independent simulation contexts that advance from one application
/// update loop. Each world keeps its own terrain and unit state.
/// </summary>
public sealed class WorldSimulationGroup
{
    private readonly List<WorldSimulationInstance> _worlds = new();

    public IReadOnlyList<WorldSimulationInstance> Worlds => _worlds;
    public int Count => _worlds.Count;

    public void Add(WorldSimulationInstance world)
    {
        ArgumentNullException.ThrowIfNull(world);

        for (int i = 0; i < _worlds.Count; i++)
        {
            if (string.Equals(_worlds[i].Id, world.Id, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"A simulation world with ID '{world.Id}' is already registered.");
        }

        _worlds.Add(world);
    }

    public bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        for (int i = 0; i < _worlds.Count; i++)
        {
            if (!string.Equals(_worlds[i].Id, id, StringComparison.Ordinal))
                continue;

            _worlds.RemoveAt(i);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Advances every active world in one application simulation tick.
    /// A paused world is skipped; a different time scale does not affect peers.
    /// </summary>
    public void Update(float deltaTime)
    {
        if (!float.IsFinite(deltaTime) || deltaTime <= 0f)
            return;

        for (int i = 0; i < _worlds.Count; i++)
        {
            WorldSimulationInstance world = _worlds[i];
            if (world.IsPaused || world.TimeScale <= 0f)
                continue;

            world.Simulation.Update(
                world.Terrain,
                deltaTime * world.TimeScale);
        }
    }
}

/// <summary>
/// Runtime state belonging to one independent landscape/world.
/// </summary>
public sealed class WorldSimulationInstance
{
    public string Id { get; }
    public WorldMap Terrain { get; }
    public UnitSimulation Simulation { get; }
    public bool IsPaused { get; set; }
    public float TimeScale { get; set; } = 1f;

    public WorldSimulationInstance(
        string id,
        WorldMap terrain,
        UnitSimulation simulation)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("World ID cannot be empty.", nameof(id));

        Id = id;
        Terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        Simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
    }
}
