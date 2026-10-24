using Core.Items;
using Core.Map;
using Core.AI.Perception;
using System;
using System.Diagnostics;
using System.Numerics;

namespace Core.Unit;

public sealed class UnitSimulation
{
    private readonly UnitMovementSystem _movementSystem;
    private readonly UnitBodySystem _bodySystem;
    private readonly UnitHealthSystem _healthSystem;
    private readonly UnitMetabolismSystem _metabolismSystem;
    private readonly UnitInventorySystem _inventorySystem;
    private readonly VisionSystem _visionSystem;
    private readonly UnitWeaponSystem _weaponSystem;
    private readonly ProjectileSystem _projectileSystem;
    private readonly UnitAiSystem _aiSystem;
    private readonly MeleeCombatSystem _meleeCombatSystem;
    private readonly TacticalHearingSystem _hearingSystem;
    private readonly VisualPerceptionSense _visualPerceptionSense;
    private float _simulationTimeSeconds;
    private bool[] _deathHandled = Array.Empty<bool>();
    private float[] _baseMoveSpeed = Array.Empty<float>();

    public UnitStore Units { get; }
    public UnitSkillStore Skills { get; }
    public UnitNavigationSystem Navigation => _movementSystem.Navigation;
    public UnitBodyStore Bodies { get; }
    public UnitHitRegionStore HitRegions { get; }
    public UnitHealthStore Health { get; }
    public UnitMetabolismStore Metabolism { get; }
    public UnitInventoryStore Inventory { get; }
    public UnitWeaponStore Weapons { get; }
    public ProjectileStore Projectiles { get; }
    public UnitSuppressionStore Suppression { get; }
    public PerceptionMemoryStore PerceptionMemory { get; }
    public PerceptionSystem Perception { get; }

    public bool VisionEnabled
    {
        get => _visualPerceptionSense.Enabled;
        set => _visualPerceptionSense.Enabled = value;
    }

    public double LastSimulationUpdateMilliseconds { get; private set; }
    public double LastPerceptionUpdateMilliseconds { get; private set; }
    public double LastSuppressionUpdateMilliseconds { get; private set; }
    public double LastVisionCallMilliseconds { get; private set; }
    public double LastAIUpdateMilliseconds { get; private set; }
    public double LastMovementUpdateMilliseconds { get; private set; }
    public double LastWeaponUpdateMilliseconds { get; private set; }
    public double LastBodyUpdateMilliseconds { get; private set; }
    public double LastMeleeUpdateMilliseconds { get; private set; }
    public double LastProjectileUpdateMilliseconds { get; private set; }
    public double LastHealthUpdateMilliseconds { get; private set; }
    public long TotalShotsFired => _weaponSystem.TotalShotsFired;
    public long TotalRoundsConsumed => _weaponSystem.TotalRoundsConsumed;
    public long TotalProjectilesSpawned => _weaponSystem.TotalProjectilesSpawned;
    public int ShotsFiredThisFrame => _weaponSystem.ShotsFiredThisFrame;

    public void BeginMetricsFrame()
    {
        _weaponSystem.BeginMetricsFrame();
    }

    /// <summary>Reports an explosion to nearby units when a blast producer supplies the event.</summary>
    public void ReportExplosion(
        Vector3 position,
        ushort sourceFaction = 0,
        float intensity = 1f,
        float audibleRange = 80f) =>
        _hearingSystem.EmitExplosion(position, sourceFaction, intensity, audibleRange);
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
    internal TacticalHearingSystem HearingSystem =>
        _hearingSystem;
    public TacticalSoundDebugEvent[] GetRecentSoundDebugEvents() =>
        _hearingSystem.GetRecentDebugEvents(PerceptionMemory.CurrentTimeSeconds);
    public MeleeCombatSystem MeleeCombat =>
        _meleeCombatSystem;
    public VisionSystem Vision =>
        _visionSystem;

    public UnitSimulation(
        int unitCapacity = 1024,
        int bodyCapacity = 4096)
    {
        Units =
            new UnitStore(
                unitCapacity);

        HitRegions = new UnitHitRegionStore(unitCapacity);

        _deathHandled = new bool[Units.Capacity];

        Bodies =
            new UnitBodyStore(
                bodyCapacity);

        Health =
            new UnitHealthStore(
                unitCapacity);

        Metabolism =
            new UnitMetabolismStore(
                unitCapacity);

        Inventory =
            new UnitInventoryStore(
                unitCapacity);

        _movementSystem =
            new UnitMovementSystem();

        _bodySystem = new UnitBodySystem();

        _healthSystem =
            new UnitHealthSystem();

        _metabolismSystem =
            new UnitMetabolismSystem();

        _inventorySystem =
            new UnitInventorySystem();

        Weapons =
            new UnitWeaponStore(
                unitCapacity);

        Projectiles =
            new ProjectileStore();

        Skills = new UnitSkillStore(unitCapacity);
        Suppression =
            new UnitSuppressionStore(unitCapacity);
        Suppression.BindSkillStore(Skills);
        PerceptionMemory = new PerceptionMemoryStore(unitCapacity);
        _hearingSystem = new TacticalHearingSystem(unitCapacity, PerceptionMemory);

        _weaponSystem =
            new UnitWeaponSystem(HitRegions, Bodies, Skills, Health);
        _weaponSystem.ShotFired += _hearingSystem.EmitGunshot;

        _projectileSystem =
            new ProjectileSystem();
        _projectileSystem.DangerStimulus += _hearingSystem.EmitDanger;

        _visionSystem =
            new VisionSystem(
                unitCapacity,
                HitRegions);

        _visualPerceptionSense = new VisualPerceptionSense(_visionSystem);
        Perception = new PerceptionSystem(PerceptionMemory);
        Perception.Register(_hearingSystem);
        Perception.Register(_visualPerceptionSense);

        _aiSystem =
            new UnitAiSystem(
                unitCapacity,
                _weaponSystem,
                Skills,
                PerceptionMemory,
                Metabolism);
        _aiSystem.Hearing = _hearingSystem;

        _meleeCombatSystem = new MeleeCombatSystem();
    }

    public UnitId Spawn(
        UnitType type,
        Vector3 position,
        float bodyAngle = 0f,
        int locationId = 0,
        ushort factionTag = 0,
        UnitHitRegionProfile? hitProfile = null)
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
            factionTag,
            hitProfile);
    }

    public UnitId Spawn(
        UnitType type,
        Vector3 position,
        Vector3 bodyNormal,
        Vector3 headNormal,
        int locationId = 0,
        ushort factionTag = 0,
        UnitHitRegionProfile? hitProfile = null)
    {
        UnitArchetypeProfile archetype = UnitAnatomyCatalog.Get(type);
        UnitDefinition definition = archetype.Definition;
        BodyHandle body = Bodies.CreateBody(archetype.BodyParts);

        UnitId id =
            Units.Create(
                definition,
                position,
                bodyNormal,
                headNormal,
                body,
                locationId,
                factionTag);

        UnitHitRegionProfile profile = hitProfile ?? archetype.Anatomy;

        return FinishSpawn(
            id,
            definition,
            position,
            bodyNormal,
            headNormal,
            body,
            profile,
            archetype.StartingEquipment);
    }

    public UnitId SpawnCustom(
        UnitType type,
        Vector3 position,
        ReadOnlySpan<BodyPartDefinition> bodyParts,
        float bodyAngle = 0f,
        int locationId = 0,
        ushort factionTag = 0,
        UnitHitRegionProfile? hitProfile = null)
    {
        UnitArchetypeProfile archetype = UnitAnatomyCatalog.Get(type);
        UnitDefinition definition = archetype.Definition;
        BodyHandle body = Bodies.CreateBody(bodyParts);

        Vector3 bodyNormal = new Vector3(
            MathF.Cos(bodyAngle),
            MathF.Sin(bodyAngle),
            0f);

        UnitId id = Units.Create(
            definition,
            position,
            bodyNormal,
            bodyNormal,
            body,
            locationId,
            factionTag);

        UnitHitRegionProfile profile =
            hitProfile ?? UnitAnatomyProfile.FromBodyParts(bodyParts);

        return FinishSpawn(
            id,
            definition,
            position,
            bodyNormal,
            bodyNormal,
            body,
            profile,
            archetype.StartingEquipment);
    }

    public UnitId SpawnCustom(
        in UnitDefinition definition,
        Vector3 position,
        ReadOnlySpan<BodyPartDefinition> bodyParts,
        UnitHitRegionProfile hitProfile,
        float bodyAngle = 0f,
        int locationId = 0,
        ushort factionTag = 0)
    {
        if (hitProfile == null)
            throw new ArgumentNullException(nameof(hitProfile));

        BodyHandle body = Bodies.CreateBody(bodyParts);

        Vector3 bodyNormal = new Vector3(
            MathF.Cos(bodyAngle),
            MathF.Sin(bodyAngle),
            0f);

        UnitId id = Units.Create(
            definition,
            position,
            bodyNormal,
            bodyNormal,
            body,
            locationId,
            factionTag);

        return FinishSpawn(
            id,
            definition,
            position,
            bodyNormal,
            bodyNormal,
            body,
            hitProfile,
            ReadOnlySpan<UnitStartingEquipment>.Empty);
    }

    private UnitId FinishSpawn(
        UnitId id,
        in UnitDefinition definition,
        Vector3 position,
        Vector3 bodyNormal,
        Vector3 headNormal,
        BodyHandle body,
        UnitHitRegionProfile hitProfile,
        ReadOnlySpan<UnitStartingEquipment> startingEquipment)
    {
        Bodies.SetInitialWorldPosition(
            body,
            position,
            MathF.Atan2(bodyNormal.Y, bodyNormal.X),
            MathF.Atan2(headNormal.Y, headNormal.X));

        EnsureDeathCapacity();
        _deathHandled[id.Index] = false;
        _baseMoveSpeed[id.Index] = Units.MoveSpeed[id.Index];

        bool initializeLegacyHealth =
            hitProfile is not UnitAnatomyProfile anatomy ||
            !anatomy.HasSharedShapes ||
            body.Count != anatomy.NodeCount;
        Health.InitializeUnit(id.Index, definition.Type, initializeLegacyHealth);
        Metabolism.InitializeUnit(id.Index, definition);
        HitRegions.SetProfile(id.Index, hitProfile);
        Health.RegisterHitRegionParts(id.Index, hitProfile);
        Inventory.InitializeUnit(id.Index);
        Weapons.InitializeUnit(id.Index);
        Perception.InitializeUnit(id.Index);
        Skills.InitializeUnit(id.Index, definition.Type, Units.FactionTag[id.Index]);
        Suppression.InitializeUnit(id.Index);
        _aiSystem.Store.InitializeUnit(id.Index);
        _aiSystem.InitializeUnit(id.Index, id.Generation, Units.FactionTag[id.Index]);
        _meleeCombatSystem.InitializeUnit(id.Index, id);

        for (int i = 0; i < startingEquipment.Length; i++)
        {
            UnitStartingEquipment item = startingEquipment[i];
            EquipStartingItem(id, item.Item, item.Durability);
        }

        if (startingEquipment.Length > 0 && Units.TryGetIndex(id, out int unitIndex))
            RefreshMovementSpeed(unitIndex);

        return id;
    }

    private void EquipStartingItem(
        UnitId id,
        ArmorConfig armor,
        float durability)
    {
        int slot = Inventory.AddItem(id.Index, armor, durability);
        if (slot >= 0)
            _inventorySystem.EquipArmor(Units, Inventory, id, slot);
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

        _aiSystem.InvalidateSquadUnit(id);
        _movementSystem.Navigation.ClearRoute(index);
        Perception.ClearUnit(index);
        _deathHandled[index] = false;

        BodyHandle body =
            new BodyHandle(
                Units.BodyStart[index],
                Units.BodyCount[index]);

        Bodies.FreeBody(
            body);

        HitRegions.ClearUnit(index);

        Health.ClearUnit(
            index);

        Metabolism.ClearUnit(index);

        Inventory.ClearUnit(
            index);

        Weapons.ClearUnit(
            index);

        Suppression.ClearUnit(
            index);

        _aiSystem.Store.ClearUnit(
            index);
        _aiSystem.ClearUnit(index);
        _meleeCombatSystem.ClearUnit(index);

        return Units.Destroy(id);
    }

    public float ConsumeFood(
        UnitId id,
        float caloriesKcal,
        float proteinGrams,
        float carbohydrateGrams,
        float fatGrams,
        float micronutrientUnits = 12f)
    {
        if (!Units.TryGetIndex(id, out int unitIndex) ||
            Health.OverallHitPoints[unitIndex] <= 0f)
        {
            return 0f;
        }

        return Metabolism.AddFood(
            unitIndex, caloriesKcal, proteinGrams,
            carbohydrateGrams, fatGrams, micronutrientUnits);
    }

    public float DrinkWater(UnitId id, float liters)
    {
        if (!Units.TryGetIndex(id, out int unitIndex) ||
            Health.OverallHitPoints[unitIndex] <= 0f)
        {
            return 0f;
        }

        return Metabolism.AddWater(unitIndex, liters);
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
        bool removed = _inventorySystem.RemoveItem(
            Units,
            Inventory,
            id,
            inventorySlot);

        if (removed && Units.TryGetIndex(id, out int unitIndex))
            RefreshMovementSpeed(unitIndex);

        return removed;
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

        if (Units.TryGetIndex(id, out int unitIndex))
            RefreshMovementSpeed(unitIndex);
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
        bool equipped = _inventorySystem.EquipArmor(
            Units,
            Inventory,
            id,
            inventorySlot);

        if (equipped && Units.TryGetIndex(id, out int unitIndex))
            RefreshMovementSpeed(unitIndex);

        return equipped;
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
        UnitId target,
        Vector3? aimPoint = null)
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
            target,
            1f,
            aimPoint);
    }

    public UnitDamageResult ApplyDamage(
        UnitId id,
        UnitHealthPartId part,
        float rawDamage)
    {
        if (!Units.TryGetIndex(id, out int unitIndex) ||
            Health.OverallHitPoints[unitIndex] <= 0f ||
            !UnitHitSystem.TryFindBindingForPart(
                Units, unitIndex, part,
                out int regionIndex, out int bindingIndex, HitRegions))
        {
            return default;
        }

        return ApplyAnatomyDamage(id, regionIndex, bindingIndex, rawDamage);
    }

    public UnitDamageResult ApplyAnatomyDamage(
        UnitId id,
        string tissueId,
        float rawDamage)
    {
        if (string.IsNullOrWhiteSpace(tissueId) ||
            !Units.TryGetIndex(id, out int unitIndex) ||
            Health.OverallHitPoints[unitIndex] <= 0f ||
            HitRegions.GetProfile(unitIndex) is not UnitHitRegionProfile profile ||
            !profile.TryFindPart(tissueId, out int regionIndex, out int bindingIndex))
        {
            return default;
        }

        return ApplyAnatomyDamage(id, regionIndex, bindingIndex, rawDamage);
    }

    public UnitDamageResult ApplyAnatomyDamage(
        UnitId id,
        int regionIndex,
        int bindingIndex,
        float rawDamage)
    {
        if (!Units.TryGetIndex(id, out int unitIndex) ||
            Health.OverallHitPoints[unitIndex] <= 0f ||
            !UnitHitSystem.TryGetBinding(
                Units, unitIndex, regionIndex, bindingIndex,
                out HitRegionPartBinding binding, HitRegions) ||
            !Health.TryFindRegionPart(unitIndex, regionIndex, bindingIndex, out _))
        {
            return default;
        }

        UnitDamageResult result;
        if (binding.ArmorZone >= 0)
        {
            result = _inventorySystem.ResolveIncomingDamage(
                binding.ArmorZone, rawDamage, id, Inventory);
        }
        else if (binding.Part != UnitHealthPartId.None)
        {
            result = _inventorySystem.ResolveIncomingDamage(
                binding.Part, rawDamage, id, Inventory);
        }
        else
        {
            result = new UnitDamageResult(rawDamage, MathF.Max(0f, rawDamage), 0f, 0f);
        }

        _healthSystem.ApplyRegionDamage(
            Units, Health, id, regionIndex, bindingIndex, result.FinalDamage);

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

    private static bool IsAppendOnlyAnatomy(
        UnitHitRegionProfile? previous,
        UnitHitRegionProfile next)
    {
        if (previous == null || previous.RegionCount > next.RegionCount)
            return false;

        ReadOnlySpan<UnitCircleHitRegion> oldNodes = previous.Regions;
        ReadOnlySpan<UnitCircleHitRegion> newNodes = next.Regions;

        for (int i = 0; i < oldNodes.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(oldNodes[i].Id) ||
                !string.Equals(oldNodes[i].Id, newNodes[i].Id, StringComparison.Ordinal))
                return false;

            ReadOnlySpan<HitRegionPartBinding> oldTissues = oldNodes[i].Parts;
            ReadOnlySpan<HitRegionPartBinding> newTissues = newNodes[i].Parts;
            if (oldTissues.Length != newTissues.Length)
                return false;

            for (int j = 0; j < oldTissues.Length; j++)
            {
                if (oldTissues[j].Part != newTissues[j].Part ||
                    oldTissues[j].Kind != newTissues[j].Kind ||
                    !string.Equals(oldTissues[j].Id, newTissues[j].Id, StringComparison.Ordinal) ||
                    !string.Equals(oldTissues[j].ParentId, newTissues[j].ParentId, StringComparison.Ordinal) ||
                    oldTissues[j].MaxHitPoints != newTissues[j].MaxHitPoints ||
                    oldTissues[j].ArmorZone != newTissues[j].ArmorZone)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Append a named anatomy circle at runtime without rebuilding the caller's
    /// profile manually. Existing named tissue health is preserved.
    /// </summary>
    public bool AddAnatomyNode(UnitId id, UnitAnatomyNode node)
    {
        if (!Units.TryGetIndex(id, out int index) ||
            HitRegions.GetProfile(index) is not UnitHitRegionProfile current)
        {
            return false;
        }

        var nextNodes = new UnitCircleHitRegion[current.RegionCount + 1];
        current.Regions.CopyTo(nextNodes);
        nextNodes[^1] = node;
        return SetHitRegionProfile(id, new UnitHitRegionProfile(nextNodes));
    }

    /// <summary>
    /// Replaces an edited anatomy profile while preserving health for all unchanged
    /// region/tissue IDs. Appended editable regions receive fresh health slots.
    /// </summary>
    /// <summary>Applies edited movement, size and vision settings to an existing instance.</summary>
    public bool ApplyEditedUnitDefinition(UnitId id, in UnitDefinition definition) =>
        ApplyEditedUnitDefinition(id, definition, definition);

    public bool ApplyEditedUnitDefinition(
        UnitId id,
        in UnitDefinition previousDefinition,
        in UnitDefinition definition)
    {
        if (!Units.TryGetIndex(id, out int index) || Units.Type[index] != definition.Type)
            return false;

        Metabolism.ApplyEditedDefinition(index, previousDefinition, definition);
        _baseMoveSpeed[index] = definition.MoveSpeed;
        Units.MoveSpeed[index] = definition.MoveSpeed;
        Units.Width[index] = definition.Width;
        Units.Length[index] = definition.Length;
        Units.Height[index] = definition.Height;
        Units.Radius[index] = MathF.Max(definition.Width, definition.Length) * 0.5f;
        Units.ViewRange[index] = definition.ViewRange;
        Units.FieldOfView[index] = definition.FieldOfView;
        RefreshMovementSpeed(index);
        return true;
    }

    public bool ApplyEditedAnatomyProfile(UnitId id, UnitAnatomyProfile profile)
    {
        if (profile == null || !Units.TryGetIndex(id, out int index))
            return false;

        UnitHitRegionProfile? previous = HitRegions.GetProfile(index);
        bool stablePrefix = previous is UnitAnatomyProfile old && profile.RegionCount >= old.RegionCount;
        if (stablePrefix && previous is UnitAnatomyProfile oldProfile)
        {
            ReadOnlySpan<UnitCircleHitRegion> oldRegions = oldProfile.Regions;
            ReadOnlySpan<UnitCircleHitRegion> newRegions = profile.Regions;
            for (int i = 0; i < oldRegions.Length && stablePrefix; i++)
            {
                if (!string.Equals(oldRegions[i].Id, newRegions[i].Id, StringComparison.Ordinal))
                {
                    stablePrefix = false;
                    break;
                }

                ReadOnlySpan<HitRegionPartBinding> oldParts = oldRegions[i].Parts;
                ReadOnlySpan<HitRegionPartBinding> newParts = newRegions[i].Parts;
                if (oldParts.Length != newParts.Length)
                {
                    stablePrefix = false;
                    break;
                }

                for (int j = 0; j < oldParts.Length; j++)
                {
                    if (oldParts[j].Id != newParts[j].Id ||
                        oldParts[j].Part != newParts[j].Part ||
                        oldParts[j].Kind != newParts[j].Kind ||
                        oldParts[j].MaxHitPoints != newParts[j].MaxHitPoints)
                    {
                        stablePrefix = false;
                        break;
                    }
                }
            }
        }
        else
        {
            stablePrefix = false;
        }

        if (!stablePrefix)
            Health.ClearHitRegionParts(index);

        HitRegions.SetProfile(index, profile);
        Health.RegisterHitRegionParts(index, profile);
        return true;
    }

    public bool SetHitRegionProfile(
        UnitId id,
        UnitHitRegionProfile profile)
    {
        if (profile == null ||
            !Units.TryGetIndex(id, out int index))
        {
            return false;
        }

        UnitHitRegionProfile? previous = HitRegions.GetProfile(index);
        bool appendOnly = IsAppendOnlyAnatomy(previous, profile);

        if (!appendOnly)
            Health.ClearHitRegionParts(index);

        HitRegions.SetProfile(index, profile);
        Health.RegisterHitRegionParts(index, profile);
        return true;
    }

    public void Update(
        WorldMap worldMap,
        float deltaTime)
    {
        long totalStarted = Stopwatch.GetTimestamp();
        EnsureDeathCapacity();

        if (float.IsFinite(deltaTime) && deltaTime > 0f)
            _simulationTimeSeconds += deltaTime;

        long phaseStarted = Stopwatch.GetTimestamp();
        var perceptionContext = new PerceptionSenseContext(
            Units, Health, Inventory, Weapons, Suppression, Metabolism, Skills,
            Bodies, HitRegions, worldMap, _visionSystem, PerceptionMemory,
            _simulationTimeSeconds, deltaTime);
        Perception.Update(in perceptionContext);
        LastPerceptionUpdateMilliseconds = Perception.LastUpdateMilliseconds;
        LastVisionCallMilliseconds = _visualPerceptionSense.LastUpdateMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        Suppression.Update(Units, deltaTime);
        LastSuppressionUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

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
        double aiUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;
        // The profiler samples once per second while the AI think cadence is 150 ms.
        // Keep the last full think-pass duration so it isn't replaced by an idle-frame zero.
        if (!_aiSystem.Enabled || Units.ActiveCount == 0)
            LastAIUpdateMilliseconds = 0d;
        else if (_aiSystem.LastUpdateRanThinkPass)
            LastAIUpdateMilliseconds = aiUpdateMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        _movementSystem.Update(
            Units, worldMap, deltaTime, Health, _meleeCombatSystem, _aiSystem.Store, Inventory);
        _meleeCombatSystem.UpdateMovementLocks(Units, deltaTime);
        LastMovementUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        _weaponSystem.Update(
            Units,
            Inventory,
            Weapons,
            Projectiles,
            deltaTime,
            Health,
            _visionSystem,
            worldMap);
        LastWeaponUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        // Apply the selected posture, saved pose transforms and articulated motion
        // before projectile tracing so render and hit geometry use the same rig pose.
        phaseStarted = Stopwatch.GetTimestamp();
        _bodySystem.Update(Units, Bodies, deltaTime);
        LastBodyUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        _meleeCombatSystem.Update(
            Units,
            _aiSystem.Store,
            Inventory,
            Health,
            _healthSystem,
            Skills,
            Suppression,
            HitRegions,
            Bodies,
            deltaTime);
        LastMeleeUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        _projectileSystem.Update(
            Units,
            Bodies,
            HitRegions,
            Inventory,
            Health,
            _healthSystem,
            Projectiles,
            Suppression,
            worldMap,
            deltaTime,
            _visionSystem);
        LastProjectileUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        RefreshMovementSpeeds();

        _healthSystem.Update(Units, Health, deltaTime);
        _metabolismSystem.Update(Units, Health, Suppression, Metabolism, deltaTime);
        LastHealthUpdateMilliseconds =
            Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        ProcessDeadUnits();
        LastSimulationUpdateMilliseconds =
            Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
    }

    private void EnsureDeathCapacity()
    {
        if (_deathHandled.Length < Units.Capacity)
            Array.Resize(ref _deathHandled, Units.Capacity);

        if (_baseMoveSpeed.Length < Units.Capacity)
            Array.Resize(ref _baseMoveSpeed, Units.Capacity);
    }

    private void RefreshMovementSpeeds()
    {
        ReadOnlySpan<int> active = Units.ActiveIndices;
        for (int i = 0; i < active.Length; i++)
            RefreshMovementSpeed(active[i]);
    }

    private void RefreshMovementSpeed(int unitIndex)
    {
        float multiplier = 1f;

        for (int zone = 0; zone < UnitInventoryStore.BodyZoneCount; zone++)
        {
            for (int layer = 0; layer < UnitInventoryStore.ArmorLayerCount; layer++)
            {
                short slot = Inventory.GetBodyEquipment(unitIndex, zone, (ArmorLayer)layer);
                if (slot < 0)
                    continue;

                if (Inventory.GetItem(unitIndex, slot) is not ArmorConfig armor ||
                    armor.MovementSpeedMultiplier <= multiplier)
                    continue;

                int durabilityIndex =
                    unitIndex * UnitInventoryStore.MaxInventorySlots + slot;
                if (Inventory.Durability[durabilityIndex] > 0f)
                    multiplier = armor.MovementSpeedMultiplier;
            }
        }

        // Damaged/missing legs and feet slow the unit, but never change its gameplay collider.
        int healthStart = unitIndex * Health.MaxParts;
        int partCount = Health.PartCount[unitIndex];
        float locomotionHp = 0f;
        float locomotionMaxHp = 0f;
        for (int partIndex = 0; partIndex < partCount; partIndex++)
        {
            int healthIndex = healthStart + partIndex;
            UnitHealthPartId part = Health.Part[healthIndex];
            bool locomotionPart = part is
                UnitHealthPartId.LeftLeg or UnitHealthPartId.RightLeg or
                UnitHealthPartId.LeftFoot or UnitHealthPartId.RightFoot;
            if (!locomotionPart)
                continue;

            float maxHp = Health.MaxHitPoints[healthIndex];
            if (maxHp <= 0f)
                continue;

            locomotionMaxHp += maxHp;
            if (Health.Present[healthIndex])
                locomotionHp += Math.Clamp(Health.HitPoints[healthIndex], 0f, maxHp);
        }

        if (locomotionMaxHp > 0f)
        {
            float locomotionRatio = Math.Clamp(locomotionHp / locomotionMaxHp, 0f, 1f);
            multiplier *= 0.25f + 0.75f * locomotionRatio;
        }

        Units.MoveSpeed[unitIndex] =
            _baseMoveSpeed[unitIndex] * multiplier * Metabolism.MovementMultiplier[unitIndex];
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
        UnitId deadId = Units.GetId(unitIndex);
        _aiSystem.InvalidateSquadUnit(deadId);

        // Do not leave live units' AI or weapon state pointing at the corpse.
        ReadOnlySpan<int> active = Units.ActiveIndices;
        for (int i = 0; i < active.Length; i++)
        {
            int other = active[i];
            if (other == unitIndex)
                continue;

            if (_aiSystem.Store.HasTarget[other] &&
                _aiSystem.Store.Target[other] == deadId)
            {
                _aiSystem.Store.ClearTarget(other);
                _aiSystem.Store.ClearGoal(other);
                _aiSystem.Store.State[other] = UnitAiState.Idle;
                Units.HasTarget[other] = false;
                Units.Target[other] = Units.Position[other];
                Units.Velocity[other] = Vector3.Zero;
                _movementSystem.Navigation.ClearRoute(other);
            }

            Weapons.ClearReferencesToTarget(other, deadId);
        }

        Units.HasTarget[unitIndex] = false;
        Units.Velocity[unitIndex] = Vector3.Zero;
        Units.Posture[unitIndex] = UnitPosture.Lying;
        _movementSystem.Navigation.ClearRoute(unitIndex);
        Perception.ClearUnit(unitIndex);
        Weapons.CancelActions(unitIndex);
        _aiSystem.Store.ClearUnit(unitIndex);
        _aiSystem.Store.State[unitIndex] = UnitAiState.Dead;
    }
}
