using Core.Items;
using Core.Map;
using System.Diagnostics;
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
    private readonly UnitAiSystem _aiSystem;
    private bool[] _deathHandled = Array.Empty<bool>();

    public UnitStore Units { get; }
    public UnitNavigationSystem Navigation => _movementSystem.Navigation;
    public UnitBodyStore Bodies { get; }
    public UnitHealthStore Health { get; }
    public UnitInventoryStore Inventory { get; }
    public UnitWeaponStore Weapons { get; }
    public ProjectileStore Projectiles { get; }
    public UnitSuppressionStore Suppression { get; }

    public bool VisionEnabled { get; set; } = true;

    public double LastSimulationUpdateMilliseconds { get; private set; }
    public double LastSuppressionUpdateMilliseconds { get; private set; }
    public double LastVisionCallMilliseconds { get; private set; }
    public double LastAIUpdateMilliseconds { get; private set; }
    public double LastMovementUpdateMilliseconds { get; private set; }
    public double LastWeaponUpdateMilliseconds { get; private set; }
    public double LastBodyUpdateMilliseconds { get; private set; }
    public double LastProjectileUpdateMilliseconds { get; private set; }
    public double LastHealthUpdateMilliseconds { get; private set; }
    public double LastProjectileGridBuildMilliseconds =>
        _projectileSystem.LastGridBuildMilliseconds;
    public int LastProjectilesVisited =>
        _projectileSystem.LastProjectilesVisited;
    public int LastProjectileTerrainCellsTraced =>
        _projectileSystem.LastTerrainCellsTraced;
    public int LastProjectileUnitCandidates =>
        _projectileSystem.LastUnitCandidates;

    public UnitAiSystem AI =>
        _aiSystem;
    public VisionSystem Vision =>
        _visionSystem;

    public UnitSimulation(
        int unitCapacity = 1024,
        int bodyCapacity = 4096)
    {
        Units =
            new UnitStore(
                unitCapacity);

        _deathHandled = new bool[Units.Capacity];

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

        Suppression =
            new UnitSuppressionStore(unitCapacity);

        _weaponSystem =
            new UnitWeaponSystem();

        _projectileSystem =
            new ProjectileSystem();

        _visionSystem =
            new VisionSystem(
                unitCapacity);

        _aiSystem =
            new UnitAiSystem(
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

        EnsureDeathCapacity();
        _deathHandled[id.Index] = false;

        Health.InitializeUnit(
            id.Index,
            type);

        Inventory.InitializeUnit(
            id.Index);

        Weapons.InitializeUnit(
            id.Index);

        Suppression.InitializeUnit(
            id.Index);

        _aiSystem.Store.InitializeUnit(
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

        _movementSystem.Navigation.ClearRoute(index);
        _visionSystem.ClearUnit(index);
        _deathHandled[index] = false;

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

        Suppression.ClearUnit(
            index);

        _aiSystem.Store.ClearUnit(
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
        if (!Units.TryGetIndex(id, out int unitIndex) ||
            Health.OverallHitPoints[unitIndex] <= 0f)
        {
            return false;
        }

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
        if (!Units.TryGetIndex(id, out int shooterIndex) ||
            Health.OverallHitPoints[shooterIndex] <= 0f ||
            !Units.TryGetIndex(target, out int targetIndex) ||
            Health.OverallHitPoints[targetIndex] <= 0f)
        {
            return false;
        }

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
        if (!Units.TryGetIndex(id, out int unitIndex) ||
            Health.OverallHitPoints[unitIndex] <= 0f)
        {
            return default;
        }

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

        if (Health.OverallHitPoints[unitIndex] <= 0f)
            MarkUnitDead(unitIndex);

        return result;
    }

    public void SetTarget(
        UnitId id,
        Vector3 target)
    {
        if (!Units.TryGetIndex(id, out int unitIndex) ||
            Health.OverallHitPoints[unitIndex] <= 0f)
        {
            return;
        }

        Units.SetTarget(id, target);
    }

    public void SetFactionTag(
        UnitId id,
        ushort factionTag)
    {
        Units.SetFactionTag(
            id,
            factionTag);
    }

    public bool SelectAmmunition(
        UnitId id,
        UnitWeaponSlot slot,
        int ammoType)
    {
        if (!Units.TryGetIndex(
                id,
                out int unitIndex))
        {
            return false;
        }

        return Weapons.SelectAmmunition(
            unitIndex,
            slot,
            ammoType);
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
        long totalStarted = Stopwatch.GetTimestamp();
        EnsureDeathCapacity();

        long phaseStarted = Stopwatch.GetTimestamp();
        Suppression.Update(Units, deltaTime);
        LastSuppressionUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        if (VisionEnabled)
        {
            phaseStarted = Stopwatch.GetTimestamp();
            _visionSystem.Update(Units, worldMap, deltaTime, Health);
            LastVisionCallMilliseconds =
                Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;
        }
        else
        {
            LastVisionCallMilliseconds = 0d;
        }

        phaseStarted = Stopwatch.GetTimestamp();
        _aiSystem.Update(
            Units,
            Health,
            Inventory,
            Weapons,
            Projectiles,
            Suppression,
            _visionSystem,
            worldMap,
            deltaTime);
        LastAIUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        _movementSystem.Update(Units, worldMap, deltaTime, Health);
        LastMovementUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        _weaponSystem.Update(Units, Inventory, Weapons, Projectiles, deltaTime, Health);
        LastWeaponUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        _bodySystem.Update(Units, Bodies, deltaTime);
        LastBodyUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        _projectileSystem.Update(
            Units,
            Inventory,
            Health,
            _healthSystem,
            Projectiles,
            Suppression,
            worldMap,
            deltaTime);
        LastProjectileUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        _healthSystem.Update(Units, Health, deltaTime);
        LastHealthUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        ProcessDeadUnits();
        LastSimulationUpdateMilliseconds =
            Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
    }

    private void EnsureDeathCapacity()
    {
        if (_deathHandled.Length >= Units.Capacity)
            return;

        Array.Resize(ref _deathHandled, Units.Capacity);
    }

    private void ProcessDeadUnits()
    {
        ReadOnlySpan<int> active = Units.ActiveIndices;

        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];

            if (Health.OverallHitPoints[unit] > 0f)
            {
                _deathHandled[unit] = false;
                continue;
            }

            MarkUnitDead(unit);
        }
    }

    private void MarkUnitDead(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)_deathHandled.Length ||
            _deathHandled[unitIndex])
        {
            return;
        }

        _deathHandled[unitIndex] = true;
        Units.HasTarget[unitIndex] = false;
        Units.Velocity[unitIndex] = Vector3.Zero;
        _movementSystem.Navigation.ClearRoute(unitIndex);
        _visionSystem.ClearUnit(unitIndex);
        Weapons.ClearUnit(unitIndex);
        _aiSystem.Store.ClearUnit(unitIndex);
        _aiSystem.Store.State[unitIndex] = UnitAiState.Dead;
    }
}
