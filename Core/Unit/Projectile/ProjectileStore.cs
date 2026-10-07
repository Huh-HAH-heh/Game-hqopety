using System;
using System.Numerics;
using Core.Combat;

namespace Core.Unit;

public sealed class ProjectileStore
{
    private const int DefaultCapacity = 8192;

    private Vector3[] _position;
    private Vector3[] _velocity;
    private UnitId[] _owner;
    private ushort[] _factionTag;
    private float[] _energy;
    private float[] _initialEnergy;
    private float[] _penetration;
    private float[] _bluntPenetration;
    private float[] _baseDamage;
    private float[] _massKg;
    private float[] _diameterM;
    private float[] _dragCoefficient;
    private float[] _suppressionFactor;
    private DamageType[] _damageType;
    private float[] _bleedChance;
    private float[] _lifetime;
    private uint[] _generation;

    private int[] _activeIndices;
    private int[] _activeSlots;
    private int[] _freeIndices;

    private int _activeCount;
    private int _count;
    private int _freeCount;

    public long TotalHits { get; private set; }
    public Vector3 LastHitPosition { get; private set; }
    public UnitId LastHitTarget { get; private set; }
    public UnitHealthPartId LastHitPart { get; private set; }

    public int ActiveCount =>
        _activeCount;

    public int Capacity =>
        _position.Length;

    public Vector3[] Position => _position;
    public Vector3[] Velocity => _velocity;
    public UnitId[] Owner => _owner;
    public ushort[] FactionTag => _factionTag;
    public float[] Energy => _energy;
    public float[] InitialEnergy => _initialEnergy;
    public float[] Penetration => _penetration;
    public float[] BluntPenetration => _bluntPenetration;
    public float[] BaseDamage => _baseDamage;
    public float[] MassKg => _massKg;
    public float[] DiameterM => _diameterM;
    public float[] DragCoefficient => _dragCoefficient;
    public float[] SuppressionFactor => _suppressionFactor;
    public DamageType[] DamageType => _damageType;
    public float[] BleedChance => _bleedChance;
    public float[] Lifetime => _lifetime;

    public ReadOnlySpan<int> ActiveIndices =>
        _activeIndices.AsSpan(
            0,
            _activeCount);

    public ProjectileStore(
        int initialCapacity = DefaultCapacity)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(initialCapacity));

        _position = new Vector3[initialCapacity];
        _velocity = new Vector3[initialCapacity];
        _owner = new UnitId[initialCapacity];
        _factionTag = new ushort[initialCapacity];
        _energy = new float[initialCapacity];
        _initialEnergy = new float[initialCapacity];
        _penetration = new float[initialCapacity];
        _bluntPenetration = new float[initialCapacity];
        _baseDamage = new float[initialCapacity];
        _massKg = new float[initialCapacity];
        _diameterM = new float[initialCapacity];
        _dragCoefficient = new float[initialCapacity];
        _suppressionFactor = new float[initialCapacity];
        _damageType = new DamageType[initialCapacity];
        _bleedChance = new float[initialCapacity];
        _lifetime = new float[initialCapacity];
        _generation = new uint[initialCapacity];

        _activeIndices = new int[initialCapacity];
        _activeSlots = new int[initialCapacity];
        _freeIndices = new int[initialCapacity];

        Array.Fill(
            _activeSlots,
            -1);
    }

    public ProjectileId Create(
        UnitId owner,
        ushort factionTag,
        Vector3 position,
        Vector3 direction,
        float massKg,
        float diameterM,
        float muzzleVelocity,
        float dragCoefficient,
        float penetration,
        float bluntPenetration,
        float baseDamage,
        float lifetime,
        DamageType damageType = DamageType.Ballistic,
        float suppressionFactor = 1f,
        float bleedChance = 0f)
    {
        int index;

        if (_freeCount > 0)
        {
            index =
                _freeIndices[
                    --_freeCount];
        }
        else
        {
            if (_count == Capacity)
                Grow();

            index = _count++;

            if (_generation[index] == 0)
                _generation[index] = 1;
        }

        direction =
            Normalize(direction);

        float velocityValue =
            MathF.Max(
                0f,
                muzzleVelocity);

        float mass =
            MathF.Max(
                0.000001f,
                massKg);

        float energy =
            0.5f *
            mass *
            velocityValue *
            velocityValue;

        _position[index] = position;
        _velocity[index] =
            direction *
            velocityValue;
        _owner[index] = owner;
        _factionTag[index] = factionTag;
        _energy[index] = energy;
        _initialEnergy[index] = energy;
        _penetration[index] =
            MathF.Max(
                0f,
                penetration);
        _bluntPenetration[index] =
            MathF.Max(
                0f,
                bluntPenetration);
        _baseDamage[index] =
            MathF.Max(
                0f,
                baseDamage);
        _massKg[index] = mass;
        _diameterM[index] =
            MathF.Max(
                0.0001f,
                diameterM);
        _dragCoefficient[index] =
            MathF.Max(
                0f,
                dragCoefficient);
        _suppressionFactor[index] =
            MathF.Max(
                0f,
                suppressionFactor);
        _damageType[index] = damageType;
        _bleedChance[index] = MathF.Max(0f, bleedChance);
        _lifetime[index] =
            MathF.Max(
                0f,
                lifetime);

        _activeSlots[index] =
            _activeCount;

        _activeIndices[
            _activeCount++] =
            index;

        return new ProjectileId(
            index,
            _generation[index]);
    }

    public void RegisterHit(
        UnitId target,
        UnitHealthPartId part,
        Vector3 position)
    {
        TotalHits++;
        LastHitTarget = target;
        LastHitPart = part;
        LastHitPosition = position;
    }

    public bool Destroy(
        ProjectileId id)
    {
        if (!TryGetIndex(
                id,
                out int index))
        {
            return false;
        }

        return DestroyIndex(index);
    }

    public bool DestroyIndex(
        int index)
    {
        if (index < 0 ||
            index >= _count ||
            _activeSlots[index] < 0)
        {
            return false;
        }

        int slot =
            _activeSlots[index];

        int lastSlot =
            _activeCount - 1;

        int movedIndex =
            _activeIndices[lastSlot];

        _activeIndices[slot] =
            movedIndex;

        _activeSlots[movedIndex] =
            slot;

        _activeCount--;
        _activeSlots[index] = -1;

        _generation[index] =
            NextGeneration(
                _generation[index]);

        _freeIndices[
            _freeCount++] =
            index;

        return true;
    }

    public bool TryGetIndex(
        ProjectileId id,
        out int index)
    {
        index = id.Index;

        if (index < 0 ||
            index >= _count ||
            id.Generation == 0 ||
            _generation[index] != id.Generation ||
            _activeSlots[index] < 0)
        {
            index = -1;
            return false;
        }

        return true;
    }

    private void Grow()
    {
        int oldCapacity =
            Capacity;

        int newCapacity =
            oldCapacity * 2;

        Array.Resize(
            ref _position,
            newCapacity);
        Array.Resize(
            ref _velocity,
            newCapacity);
        Array.Resize(
            ref _owner,
            newCapacity);
        Array.Resize(
            ref _factionTag,
            newCapacity);
        Array.Resize(
            ref _energy,
            newCapacity);
        Array.Resize(
            ref _initialEnergy,
            newCapacity);
        Array.Resize(
            ref _penetration,
            newCapacity);
        Array.Resize(
            ref _bluntPenetration,
            newCapacity);
        Array.Resize(
            ref _baseDamage,
            newCapacity);
        Array.Resize(
            ref _massKg,
            newCapacity);
        Array.Resize(
            ref _diameterM,
            newCapacity);
        Array.Resize(
            ref _dragCoefficient,
            newCapacity);
        Array.Resize(
            ref _suppressionFactor,
            newCapacity);
        Array.Resize(
            ref _damageType,
            newCapacity);
        Array.Resize(
            ref _bleedChance,
            newCapacity);
        Array.Resize(
            ref _lifetime,
            newCapacity);
        Array.Resize(
            ref _generation,
            newCapacity);
        Array.Resize(
            ref _activeIndices,
            newCapacity);
        Array.Resize(
            ref _activeSlots,
            newCapacity);
        Array.Resize(
            ref _freeIndices,
            newCapacity);

        Array.Fill(
            _activeSlots,
            -1,
            oldCapacity,
            newCapacity -
            oldCapacity);
    }

    private static Vector3 Normalize(
        Vector3 value)
    {
        float lengthSquared =
            value.LengthSquared();

        if (lengthSquared <
            0.000001f)
        {
            return new Vector3(
                1f,
                0f,
                0f);
        }

        return value /
            MathF.Sqrt(
                lengthSquared);
    }

    private static uint NextGeneration(
        uint generation)
    {
        if (generation == uint.MaxValue)
            return 1;

        return generation + 1;
    }
}
