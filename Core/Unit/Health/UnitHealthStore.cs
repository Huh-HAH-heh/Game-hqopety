using System;

namespace Core.Unit;

public sealed class UnitHealthStore
{
    private const int DefaultUnitCapacity = 1024;

    // Flat per-unit blocks keep combat health data contiguous and allocation-free.
    // The slot count covers large custom anatomies with many segments and organs.
    private const int MaxPartsPerUnit = 256;

    private UnitHealthPartId[] _part;
    private UnitHealthPartKind[] _kind;
    private short[] _parent;
    private short[] _regionIndex;
    private short[] _bindingIndex;
    private float[] _maxHitPoints;
    private float[] _hitPoints;
    private float[] _bleedRate;
    private bool[] _present;

    private short[] _partCount;

    private float[] _overallMaxHitPoints;
    private float[] _overallHitPoints;

    public int Capacity =>
        _partCount.Length;

    public int MaxParts =>
        MaxPartsPerUnit;

    public UnitHealthPartId[] Part =>
        _part;

    public UnitHealthPartKind[] Kind =>
        _kind;

    public short[] Parent =>
        _parent;

    public float[] MaxHitPoints =>
        _maxHitPoints;

    public float[] HitPoints =>
        _hitPoints;

    public float[] BleedRate =>
        _bleedRate;

    public bool[] Present =>
        _present;

    public short[] PartCount =>
        _partCount;

    public float[] OverallMaxHitPoints =>
        _overallMaxHitPoints;

    public float[] OverallHitPoints =>
        _overallHitPoints;

    public UnitHealthStore(
        int initialUnitCapacity = DefaultUnitCapacity)
    {
        if (initialUnitCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialUnitCapacity));
        }

        _partCount =
            new short[initialUnitCapacity];

        _overallMaxHitPoints =
            new float[initialUnitCapacity];

        _overallHitPoints =
            new float[initialUnitCapacity];

        int nodeCapacity =
            initialUnitCapacity *
            MaxPartsPerUnit;

        _part =
            new UnitHealthPartId[nodeCapacity];

        _kind =
            new UnitHealthPartKind[nodeCapacity];

        _parent =
            new short[nodeCapacity];

        _regionIndex = new short[nodeCapacity];
        _bindingIndex = new short[nodeCapacity];

        _maxHitPoints =
            new float[nodeCapacity];

        _hitPoints =
            new float[nodeCapacity];

        _bleedRate =
            new float[nodeCapacity];

        _present =
            new bool[nodeCapacity];

        Array.Fill(_parent, (short)-1);
        Array.Fill(_regionIndex, (short)-1);
        Array.Fill(_bindingIndex, (short)-1);
    }

    public void InitializeUnit(
        int unitIndex,
        UnitType type)
    {
        EnsureUnitCapacity(
            unitIndex + 1);

        int start =
            GetStart(
                unitIndex);

        Array.Clear(
            _part,
            start,
            MaxPartsPerUnit);

        Array.Clear(
            _kind,
            start,
            MaxPartsPerUnit);

        Array.Clear(
            _maxHitPoints,
            start,
            MaxPartsPerUnit);

        Array.Clear(
            _hitPoints,
            start,
            MaxPartsPerUnit);

        Array.Clear(
            _bleedRate,
            start,
            MaxPartsPerUnit);

        Array.Clear(
            _present,
            start,
            MaxPartsPerUnit);

        for (int i = 0; i < MaxPartsPerUnit; i++)
        {
            _parent[start + i] = -1;
            _regionIndex[start + i] = -1;
            _bindingIndex[start + i] = -1;
        }

        _partCount[unitIndex] = 0;

        _overallMaxHitPoints[unitIndex] =
            type == UnitType.Colonist
                ? 100f
                : 100f;

        _overallHitPoints[unitIndex] =
            _overallMaxHitPoints[unitIndex];

        if (type ==
            UnitType.Colonist)
        {
            BuildColonist(
                unitIndex);

            return;
        }

        AddPart(
            unitIndex,
            UnitHealthPartId.WholeBody,
            UnitHealthPartKind.BodyPart,
            parent: -1,
            maxHitPoints: 100f);
    }

    public void RegisterHitRegionParts(
        int unitIndex,
        UnitHitRegionProfile profile)
    {
        EnsureUnitCapacity(unitIndex + 1);

        ReadOnlySpan<UnitCircleHitRegion> regions = profile.Regions;

        // Every node/binding owns a separate health pool, even when several
        // segments share legacy Part=WholeBody or another compatibility enum.
        for (int regionIndex = 0; regionIndex < regions.Length; regionIndex++)
        {
            ReadOnlySpan<HitRegionPartBinding> bindings = regions[regionIndex].Parts;

            for (int bindingIndex = 0; bindingIndex < bindings.Length; bindingIndex++)
            {
                HitRegionPartBinding binding = bindings[bindingIndex];
                AddPart(
                    unitIndex,
                    binding.Part,
                    binding.Kind,
                    parent: -1,
                    maxHitPoints: binding.MaxHitPoints,
                    regionIndex: regionIndex,
                    bindingIndex: bindingIndex);
            }
        }

        // Parent links use stable string IDs first. Legacy enum parent links
        // remain supported for existing human profiles.
        for (int regionIndex = 0; regionIndex < regions.Length; regionIndex++)
        {
            ReadOnlySpan<HitRegionPartBinding> bindings = regions[regionIndex].Parts;

            for (int bindingIndex = 0; bindingIndex < bindings.Length; bindingIndex++)
            {
                HitRegionPartBinding binding = bindings[bindingIndex];
                int childIndex = FindRegionPartIndex(unitIndex, regionIndex, bindingIndex);
                int parentIndex = -1;

                if (!string.IsNullOrWhiteSpace(binding.ParentId) &&
                    profile.TryFindPart(binding.ParentId, out int parentRegion, out int parentBinding))
                {
                    parentIndex = FindRegionPartIndex(unitIndex, parentRegion, parentBinding);
                }
                else if (binding.ParentPart != UnitHealthPartId.None)
                {
                    for (int candidate = 0; candidate < bindings.Length; candidate++)
                    {
                        if (bindings[candidate].Part == binding.ParentPart)
                        {
                            parentIndex = FindRegionPartIndex(unitIndex, regionIndex, candidate);
                            break;
                        }
                    }

                    if (parentIndex < 0 &&
                        TryFindPart(unitIndex, binding.ParentPart, out int legacyParent))
                    {
                        parentIndex = legacyParent;
                    }
                }

                if (childIndex >= 0 && parentIndex >= 0)
                    _parent[childIndex] = checked((short)(parentIndex % MaxPartsPerUnit));
            }
        }
    }

    public void ClearHitRegionParts(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)Capacity)
            return;

        int start = GetStart(unitIndex);
        int legacyCount = 0;
        while (legacyCount < _partCount[unitIndex] &&
               _regionIndex[start + legacyCount] < 0)
        {
            legacyCount++;
        }

        int clearStart = start + legacyCount;
        int clearCount = MaxPartsPerUnit - legacyCount;
        Array.Clear(_part, clearStart, clearCount);
        Array.Clear(_kind, clearStart, clearCount);
        Array.Clear(_maxHitPoints, clearStart, clearCount);
        Array.Clear(_hitPoints, clearStart, clearCount);
        Array.Clear(_bleedRate, clearStart, clearCount);
        Array.Clear(_present, clearStart, clearCount);

        for (int i = legacyCount; i < MaxPartsPerUnit; i++)
        {
            _parent[start + i] = -1;
            _regionIndex[start + i] = -1;
            _bindingIndex[start + i] = -1;
        }

        _partCount[unitIndex] = checked((short)legacyCount);
    }

    public bool TryFindRegionPart(
        int unitIndex,
        int regionIndex,
        int bindingIndex,
        out int partIndex)
    {
        partIndex = FindRegionPartIndex(unitIndex, regionIndex, bindingIndex);
        return partIndex >= 0 && _present[partIndex];
    }

    private int FindRegionPartIndex(int unitIndex, int regionIndex, int bindingIndex)
    {
        if ((uint)unitIndex >= (uint)Capacity)
            return -1;

        int start = GetStart(unitIndex);
        int count = _partCount[unitIndex];

        for (int i = 0; i < count; i++)
        {
            int index = start + i;
            if (_regionIndex[index] == regionIndex &&
                _bindingIndex[index] == bindingIndex)
                return index;
        }

        return -1;
    }

    public void ClearUnit(
        int unitIndex)
    {
        if (unitIndex < 0 ||
            unitIndex >= Capacity)
        {
            return;
        }

        int start =
            GetStart(
                unitIndex);

        Array.Clear(
            _part,
            start,
            MaxPartsPerUnit);

        Array.Clear(
            _kind,
            start,
            MaxPartsPerUnit);

        Array.Clear(
            _maxHitPoints,
            start,
            MaxPartsPerUnit);

        Array.Clear(
            _hitPoints,
            start,
            MaxPartsPerUnit);

        Array.Clear(
            _bleedRate,
            start,
            MaxPartsPerUnit);

        Array.Clear(
            _present,
            start,
            MaxPartsPerUnit);

        for (int i = 0;
             i < MaxPartsPerUnit;
             i++)
        {
            _parent[start + i] = -1;
        }

        _partCount[unitIndex] = 0;
        _overallMaxHitPoints[unitIndex] = 0f;
        _overallHitPoints[unitIndex] = 0f;
    }

    public bool TryFindPart(
        int unitIndex,
        UnitHealthPartId partId,
        out int partIndex)
    {
        partIndex = -1;

        if (partId == UnitHealthPartId.None ||
            unitIndex < 0 ||
            unitIndex >= Capacity)
        {
            return false;
        }

        int start =
            GetStart(
                unitIndex);

        int count =
            _partCount[unitIndex];

        for (int i = 0;
             i < count;
             i++)
        {
            int index =
                start + i;

            if (_part[index] != partId)
                continue;

            if (!_present[index])
                return false;

            partIndex = index;
            return true;
        }

        return false;
    }

    public void EnsureUnitCapacity(
        int required)
    {
        if (required <= Capacity)
            return;

        int oldCapacity =
            Capacity;

        int newCapacity =
            oldCapacity * 2;

        while (newCapacity < required)
        {
            newCapacity *= 2;
        }

        Array.Resize(
            ref _partCount,
            newCapacity);

        Array.Resize(
            ref _overallMaxHitPoints,
            newCapacity);

        Array.Resize(
            ref _overallHitPoints,
            newCapacity);

        int oldNodeCapacity =
            oldCapacity *
            MaxPartsPerUnit;

        int newNodeCapacity =
            newCapacity *
            MaxPartsPerUnit;

        Array.Resize(
            ref _part,
            newNodeCapacity);

        Array.Resize(
            ref _kind,
            newNodeCapacity);

        Array.Resize(
            ref _parent,
            newNodeCapacity);

        Array.Resize(ref _regionIndex, newNodeCapacity);
        Array.Resize(ref _bindingIndex, newNodeCapacity);

        Array.Resize(
            ref _maxHitPoints,
            newNodeCapacity);

        Array.Resize(
            ref _hitPoints,
            newNodeCapacity);

        Array.Resize(
            ref _bleedRate,
            newNodeCapacity);

        Array.Resize(
            ref _present,
            newNodeCapacity);

        Array.Fill(
            _parent,
            (short)-1,
            oldNodeCapacity,
            newNodeCapacity - oldNodeCapacity);
        Array.Fill(_regionIndex, (short)-1, oldNodeCapacity, newNodeCapacity - oldNodeCapacity);
        Array.Fill(_bindingIndex, (short)-1, oldNodeCapacity, newNodeCapacity - oldNodeCapacity);
    }

    private void BuildColonist(
        int unitIndex)
    {
        int head =
            AddPart(
                unitIndex,
                UnitHealthPartId.Head,
                UnitHealthPartKind.BodyPart,
                parent: -1,
                maxHitPoints: 40f);

        AddPart(
            unitIndex,
            UnitHealthPartId.Brain,
            UnitHealthPartKind.Organ,
            head,
            maxHitPoints: 10f);

        int torso =
            AddPart(
                unitIndex,
                UnitHealthPartId.Torso,
                UnitHealthPartKind.BodyPart,
                parent: -1,
                maxHitPoints: 60f);

        AddPart(
            unitIndex,
            UnitHealthPartId.Heart,
            UnitHealthPartKind.Organ,
            torso,
            maxHitPoints: 15f);

        AddPart(
            unitIndex,
            UnitHealthPartId.LeftLung,
            UnitHealthPartKind.Organ,
            torso,
            maxHitPoints: 12f);

        AddPart(
            unitIndex,
            UnitHealthPartId.RightLung,
            UnitHealthPartKind.Organ,
            torso,
            maxHitPoints: 12f);

        AddPart(
            unitIndex,
            UnitHealthPartId.Stomach,
            UnitHealthPartKind.Organ,
            torso,
            maxHitPoints: 12f);

        AddPart(
            unitIndex,
            UnitHealthPartId.Liver,
            UnitHealthPartKind.Organ,
            torso,
            maxHitPoints: 15f);

        AddPart(
            unitIndex,
            UnitHealthPartId.LeftKidney,
            UnitHealthPartKind.Organ,
            torso,
            maxHitPoints: 10f);

        AddPart(
            unitIndex,
            UnitHealthPartId.RightKidney,
            UnitHealthPartKind.Organ,
            torso,
            maxHitPoints: 10f);

        int leftArm =
            AddPart(
                unitIndex,
                UnitHealthPartId.LeftArm,
                UnitHealthPartKind.BodyPart,
                parent: -1,
                maxHitPoints: 30f);

        AddPart(
            unitIndex,
            UnitHealthPartId.LeftHand,
            UnitHealthPartKind.BodyPart,
            leftArm,
            maxHitPoints: 12f);

        int rightArm =
            AddPart(
                unitIndex,
                UnitHealthPartId.RightArm,
                UnitHealthPartKind.BodyPart,
                parent: -1,
                maxHitPoints: 30f);

        AddPart(
            unitIndex,
            UnitHealthPartId.RightHand,
            UnitHealthPartKind.BodyPart,
            rightArm,
            maxHitPoints: 12f);

        int leftLeg =
            AddPart(
                unitIndex,
                UnitHealthPartId.LeftLeg,
                UnitHealthPartKind.BodyPart,
                parent: -1,
                maxHitPoints: 35f);

        AddPart(
            unitIndex,
            UnitHealthPartId.LeftFoot,
            UnitHealthPartKind.BodyPart,
            leftLeg,
            maxHitPoints: 14f);

        int rightLeg =
            AddPart(
                unitIndex,
                UnitHealthPartId.RightLeg,
                UnitHealthPartKind.BodyPart,
                parent: -1,
                maxHitPoints: 35f);

        AddPart(
            unitIndex,
            UnitHealthPartId.RightFoot,
            UnitHealthPartKind.BodyPart,
            rightLeg,
            maxHitPoints: 14f);
    }

    private int AddPart(
        int unitIndex,
        UnitHealthPartId part,
        UnitHealthPartKind kind,
        int parent,
        float maxHitPoints,
        int regionIndex = -1,
        int bindingIndex = -1)
    {
        int localIndex =
            _partCount[unitIndex];

        if (localIndex >= MaxPartsPerUnit)
        {
            throw new InvalidOperationException(
                "Превышено число health parts для Unit.");
        }

        int index =
            GetStart(unitIndex) +
            localIndex;

        _partCount[unitIndex] =
            checked((short)(localIndex + 1));

        _part[index] = part;
        _kind[index] = kind;
        _parent[index] = parent < 0 ? (short)-1 : checked((short)(parent % MaxPartsPerUnit));
        _regionIndex[index] = checked((short)regionIndex);
        _bindingIndex[index] = checked((short)bindingIndex);

        _maxHitPoints[index] =
            maxHitPoints;

        _hitPoints[index] =
            maxHitPoints;

        _present[index] = true;

        return index;
    }

    private int GetStart(
        int unitIndex)
    {
        return unitIndex *
            MaxPartsPerUnit;
    }
}
