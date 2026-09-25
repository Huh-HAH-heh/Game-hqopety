using Core.Map;
using System.Numerics;

namespace Core.Unit;

public sealed class UnitSimulation
{
    private readonly UnitMovementSystem _movementSystem;
    private readonly UnitBodySystem _bodySystem;

    public UnitStore Units { get; }
    public UnitBodyStore Bodies { get; }

    public UnitSimulation(
        int unitCapacity = 1024,
        int bodyCapacity = 4096)
    {
        Units =
            new UnitStore(
                unitCapacity);

        Bodies =
            new UnitBodyStore(
                bodyCapacity);

        _movementSystem =
            new UnitMovementSystem();

        _bodySystem =
            new UnitBodySystem();
    }

    public UnitId Spawn(
        UnitType type,
        Vector3 position,
        float rotation = 0f)
    {
        UnitDefinition definition =
            UnitCatalog.Get(type);

        BodyHandle body =
            Bodies.CreateBody(
                definition.BodyType);

        UnitId id =
            Units.Create(
                definition,
                position,
                rotation,
                body);

        Bodies.SetInitialWorldPosition(
            body,
            position,
            rotation);

        return id;
    }

    public bool Destroy(
        UnitId id)
    {
        if (!Units.TryGetIndex(
                id,
                out int index))
        {
            return false;
        }

        BodyHandle body =
            new BodyHandle(
                Units.BodyStart[index],
                Units.BodyCount[index]);

        Bodies.FreeBody(
            body);

        return Units.Destroy(id);
    }

    public void SetTarget(
        UnitId id,
        Vector3 target)
    {
        Units.SetTarget(
            id,
            target);
    }

    public void Update(
        WorldMap worldMap,
        float deltaTime)
    {
        _movementSystem.Update(
            Units,
            worldMap,
            deltaTime);

        _bodySystem.Update(
            Units,
            Bodies,
            deltaTime);
    }
}
