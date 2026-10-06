using Core.Items;
using Core.Map;
using System.Numerics;

namespace Core.Unit;

public sealed class UnitSimulation
{
    private readonly UnitMovementSystem _movementSystem;
    private readonly UnitBodySystem _bodySystem;
    private readonly UnitHealthSystem _healthSystem;
    private readonly UnitInventorySystem _inventorySystem;
    private readonly VisionSystem _visionSystem;
    private readonly UnitWeaponSystem _weaponSystem;
    private readonly ProjectileSystem _projectileSystem;

    public UnitStore Units { get; }
    public UnitBodyStore Bodies { get; }
    public UnitHealthStore Health { get; }
    public UnitInventoryStore Inventory { get; }
    public UnitWeaponStore Weapons { get; }
    public ProjectileStore Projectiles { get; }
    public VisionSystem Vision =>
        _visionSystem;

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

        Health =
            new UnitHealthStore(
                unitCapacity);

        Inventory =
            new UnitInventoryStore(
                unitCapacity);

        _movementSystem =
            new UnitMovementSystem();

        _bodySystem =
            new UnitBodySystem();

        _healthSystem =
            new UnitHealthSystem();

        _inventorySystem =
            new UnitInventorySystem();

        Weapons =
            new UnitWeaponStore(
                unitCapacity);

        Projectiles =
            new ProjectileStore();

        _weaponSystem =
            new UnitWeaponSystem();

        _projectileSystem =
            new ProjectileSystem();

        _visionSystem =
            new VisionSystem(
                unitCapacity);
    }

    public UnitId Spawn(
        UnitType type,
        Vector3 position,
        float bodyAngle = 0f,
        int locationId = 0,
        ushort factionTag = 0)
    {
        Vector3 bodyNormal =
            new Vector3(
                MathF.Cos(bodyAngle),
                MathF.Sin(bodyAngle),
                0f);

        return Spawn(
            type,
            position,
            bodyNormal,
            bodyNormal,
            locationId,
            factionTag);
    }

    public UnitId Spawn(
        UnitType type,
        Vector3 position,
        Vector3 bodyNormal,
        Vector3 headNormal,
        int locationId = 0,
        ushort factionTag = 0)
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
                bodyNormal,
                headNormal,
                body,
                locationId,
                factionTag);

        Bodies.SetInitialWorldPosition(
            body,
            position,
            MathF.Atan2(
                bodyNormal.Y,
                bodyNormal.X));

        Health.InitializeUnit(
            id.Index,
            type);

        Inventory.InitializeUnit(
            id.Index);

        Weapons.InitializeUnit(
            id.Index);

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

        Health.ClearUnit(
            index);

        Inventory.ClearUnit(
            index);

        Weapons.ClearUnit(
            index);

        return Units.Destroy(id);
    }

    public int AddInventoryItem(
        UnitId id,
        ItemConfig item,
        float maxDurability = 0f)
    {
        return _inventorySystem.AddItem(
            Inventory,
            id,
            item,
            maxDurability);
    }

    public bool RemoveInventoryItem(
        UnitId id,
        int inventorySlot)
    {
        return _inventorySystem.RemoveItem(
            Units,
            Inventory,
            id,
            inventorySlot);
    }

    public void UnequipArmor(
        UnitId id,
        int zone,
        ArmorLayer layer)
    {
        _inventorySystem.UnequipArmor(
            Units,
            Inventory,
            id,
            zone,
            layer);
    }

    public void UnequipWeapon(
        UnitId id,
        UnitWeaponSlot slot)
    {
        _inventorySystem.UnequipWeapon(
            Units,
            Inventory,
            id,
            slot);
    }

    public bool EquipArmor(
        UnitId id,
        int inventorySlot)
    {
        return _inventorySystem.EquipArmor(
            Units,
            Inventory,
            id,
            inventorySlot);
    }

    public bool EquipWeapon(
        UnitId id,
        int inventorySlot,
        UnitWeaponSlot slot)
    {
        bool equipped =
            _inventorySystem.EquipWeapon(
                Units,
                Inventory,
                id,
                inventorySlot,
                slot);

        if (!equipped ||
            !Units.TryGetIndex(
                id,
                out int unitIndex))
        {
            return equipped;
        }

        WeaponConfig? weapon =
            Inventory.GetItem(
                unitIndex,
                inventorySlot) as WeaponConfig;

        if (weapon != null)
        {
            Weapons.ConfigureSlot(
                unitIndex,
                slot,
                weapon);
        }

        return true;
    }

    public bool FireWeapon(
        UnitId id,
        UnitWeaponSlot slot,
        Vector3 direction)
    {
        return _weaponSystem.TryFire(
            Units,
            Inventory,
            Weapons,
            Projectiles,
            id,
            slot,
            direction);
    }

    public bool FireWeaponAt(
        UnitId id,
        UnitWeaponSlot slot,
        UnitId target)
    {
        return _weaponSystem.TryFireAt(
            Units,
            Inventory,
            Weapons,
            Projectiles,
            id,
            slot,
            target);
    }

    public UnitDamageResult ApplyDamage(
        UnitId id,
        UnitHealthPartId part,
        float rawDamage)
    {
        UnitDamageResult result =
            _inventorySystem.ResolveIncomingDamage(
                part,
                rawDamage,
                id,
                Inventory);

        _healthSystem.ApplyDamage(
            Units,
            Health,
            id,
            part,
            result.FinalDamage);

        return result;
    }

    public void SetTarget(
        UnitId id,
        Vector3 target)
    {
        Units.SetTarget(
            id,
            target);
    }

    public void SetFactionTag(
        UnitId id,
        ushort factionTag)
    {
        Units.SetFactionTag(
            id,
            factionTag);
    }

    public void AddReserveAmmo(
        UnitId id,
        UnitWeaponSlot slot,
        int amount)
    {
        if (!Units.TryGetIndex(
                id,
                out int unitIndex))
        {
            return;
        }

        Weapons.AddReserveAmmo(
            unitIndex,
            slot,
            amount);
    }

    public void SetPosture(
        UnitId id,
        UnitPosture posture)
    {
        Units.SetPosture(
            id,
            posture);
    }

    public void SetBodyNormal(
        UnitId id,
        Vector3 normal)
    {
        Units.SetBodyNormal(
            id,
            normal);
    }

    public void SetHeadNormal(
        UnitId id,
        Vector3 normal)
    {
        Units.SetHeadNormal(
            id,
            normal);
    }

    public void Update(
        WorldMap worldMap,
        float deltaTime)
    {
        _movementSystem.Update(
            Units,
            worldMap,
            deltaTime);

        _weaponSystem.Update(
            Units,
            Inventory,
            Weapons,
            deltaTime);

        _bodySystem.Update(
            Units,
            Bodies,
            deltaTime);

        _projectileSystem.Update(
            Units,
            Inventory,
            Health,
            _healthSystem,
            Projectiles,
            worldMap,
            deltaTime);

        _healthSystem.Update(
            Units,
            Health);

        _visionSystem.Update(
            Units,
            worldMap,
            deltaTime);
    }
}
