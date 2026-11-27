using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Core.Map;

public sealed partial class VoxelVolumeStore
{
    /// <summary>Saves the base volume and edit bricks as a compressed binary stream.</summary>
    public void Save(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("Destination stream is not writable.", nameof(destination));

        using GZipStream gzip = new(destination, CompressionLevel.Fastest, leaveOpen: true);
        using BinaryWriter writer = new(gzip, Encoding.UTF8, leaveOpen: true);

        writer.Write(0x31535656); // VVS1
        writer.Write(3);
        writer.Write(Width);
        writer.Write(Height);
        writer.Write(Depth);
        writer.Write(VoxelSizeMeters);
        writer.Write(_baseSealed);
        writer.Write(Revision);
        writer.Write(_baseSource != null);
        if (_baseSource != null)
        {
            writer.Write(_baseSource.TypeId);
            writer.Write(_baseSource.DataVersion);
            _baseSource.WriteState(writer);
        }

        writer.Write(_geometry.Count);
        foreach (GeometryOperation operation in _geometry)
        {
            WriteBounds(writer, operation.Bounds);
            writer.Write((byte)operation.Shape);
            writer.Write((byte)operation.Operation);
            WriteVector(writer, operation.A);
            WriteVector(writer, operation.B);
            writer.Write(operation.Radius);
            writer.Write(operation.MaterialId);
        }

        writer.Write(_materials.Count);
        foreach (MaterialOperation operation in _materials)
        {
            WriteBounds(writer, operation.Bounds);
            writer.Write((byte)operation.Shape);
            WriteVector(writer, operation.A);
            WriteVector(writer, operation.B);
            writer.Write(operation.Radius);
            writer.Write(operation.MaterialId);
        }

        writer.Write(_baseBricks.Count);
        foreach (KeyValuePair<BrickKey, BaseBrick> pair in _baseBricks)
        {
            writer.Write(pair.Key.X);
            writer.Write(pair.Key.Y);
            writer.Write(pair.Key.Z);
            writer.Write(pair.Value.IsUniform);
            if (pair.Value.IsUniform)
            {
                writer.Write(pair.Value.UniformSample.Density);
                writer.Write(pair.Value.UniformSample.MaterialId);
            }
            else
            {
                WriteDensityArray(writer, pair.Value.Density!);
                WriteMaterialArray(writer, pair.Value.Material!);
            }
        }

        writer.Write(_editBricks.Count);
        foreach (KeyValuePair<BrickKey, EditBrick> pair in _editBricks)
        {
            writer.Write(pair.Key.X);
            writer.Write(pair.Key.Y);
            writer.Write(pair.Key.Z);

            writer.Write(pair.Value.Density != null);
            if (pair.Value.Density != null)
                WriteDensityArray(writer, pair.Value.Density);

            writer.Write(pair.Value.Material != null);
            if (pair.Value.Material != null)
                WriteMaterialArray(writer, pair.Value.Material);
        }
    }

    /// <summary>Restores a complete storage snapshot written by <see cref="Save"/>.</summary>
    public static VoxelVolumeStore Load(
        Stream source,
        VoxelBaseSourceResolver? baseSourceResolver = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("Source stream is not readable.", nameof(source));

        using GZipStream gzip = new(source, CompressionMode.Decompress, leaveOpen: true);
        using BinaryReader reader = new(gzip, Encoding.UTF8, leaveOpen: true);

        if (reader.ReadInt32() != 0x31535656)
            throw new InvalidDataException("Invalid voxel volume signature.");
        if (reader.ReadInt32() != 3)
            throw new InvalidDataException("Unsupported voxel volume version.");

        int width = reader.ReadInt32();
        int height = reader.ReadInt32();
        int depth = reader.ReadInt32();
        float voxelSize = reader.ReadSingle();
        bool sealedBase = reader.ReadBoolean();
        long revision = reader.ReadInt64();
        if (revision < 0)
            throw new InvalidDataException("Invalid voxel volume revision.");

        VoxelVolumeStore store = new(width, height, depth, voxelSize);
        if (reader.ReadBoolean())
        {
            string typeId = reader.ReadString();
            ushort dataVersion = reader.ReadUInt16();
            if (baseSourceResolver == null)
                throw new InvalidDataException(
                    $"Voxel base source '{typeId}' requires a resolver.");

            IVoxelBaseSource baseSource = baseSourceResolver(
                typeId, dataVersion,
                width, height, depth, voxelSize,
                reader);

            if (baseSource == null ||
                !string.Equals(baseSource.TypeId, typeId, StringComparison.Ordinal) ||
                baseSource.DataVersion != dataVersion)
                throw new InvalidDataException("Voxel base source resolver returned a mismatched source.");

            store.SetBaseSource(baseSource);
        }

        long maxBrickCount =
            ((long)width + BrickSize - 1) / BrickSize *
            (((long)height + BrickSize - 1) / BrickSize) *
            (((long)depth + BrickSize - 1) / BrickSize);

        int geometryCount = ReadCount(reader, 10_000_000, "geometry operation");
        for (int i = 0; i < geometryCount; i++)
        {
            VoxelBounds bounds = ReadBounds(reader);
            store.ValidateBounds(bounds);

            ShapeKind shape = ReadShape(reader);
            GeometryOperationKind operation = (GeometryOperationKind)reader.ReadByte();
            if (operation is not GeometryOperationKind.Union and not GeometryOperationKind.Difference)
                throw new InvalidDataException("Invalid geometry operation.");

            Vector3 a = ReadVector(reader);
            Vector3 b = ReadVector(reader);
            float radius = reader.ReadSingle();
            ushort materialId = reader.ReadUInt16();
            ValidateShape(shape, a, b, radius);
            if (operation == GeometryOperationKind.Union)
                ValidateMaterial(materialId);

            int id = store._geometry.Count;
            store._geometry.Add(new GeometryOperation(
                bounds, shape, operation, a, b, radius, materialId));
            store.AddToBuckets(store._geometryBuckets, bounds, id);
        }

        int materialCount = ReadCount(reader, 10_000_000, "material operation");
        for (int i = 0; i < materialCount; i++)
        {
            VoxelBounds bounds = ReadBounds(reader);
            store.ValidateBounds(bounds);

            ShapeKind shape = ReadShape(reader);
            Vector3 a = ReadVector(reader);
            Vector3 b = ReadVector(reader);
            float radius = reader.ReadSingle();
            ushort materialId = reader.ReadUInt16();
            ValidateShape(shape, a, b, radius);
            ValidateMaterial(materialId);

            int id = store._materials.Count;
            store._materials.Add(new MaterialOperation(
                bounds, shape, a, b, radius, materialId));
            store.AddToBuckets(store._materialBuckets, bounds, id);
        }

        int baseBrickCount = ReadCount(reader, maxBrickCount, "base brick");
        for (int i = 0; i < baseBrickCount; i++)
        {
            BrickKey key = ReadBrickKey(reader);
            store.ValidateBrickKey(key);
            bool uniform = reader.ReadBoolean();
            BaseBrick brick;

            if (uniform)
            {
                short density = store.Quantize(Dequantize(reader.ReadInt16()));
                ushort material = reader.ReadUInt16();
                if (density <= 0)
                    material = 0;

                brick = new BaseBrick
                {
                    Density = null,
                    Material = null,
                    IsUniform = true,
                    UniformSample = new VoxelSample(density, material)
                };
            }
            else
            {
                short[] density = ReadDensityArray(reader);
                ushort[] material = ReadMaterialArray(reader);
                for (int j = 0; j < BrickCellCount; j++)
                {
                    density[j] = store.Quantize(Dequantize(density[j]));
                    if (density[j] <= 0)
                        material[j] = 0;
                }

                bool actuallyUniform = AreUniform(
                    density, material, out VoxelSample uniformSample);
                brick = new BaseBrick
                {
                    Density = actuallyUniform ? null : density,
                    Material = actuallyUniform ? null : material,
                    IsUniform = actuallyUniform,
                    UniformSample = uniformSample
                };
            }

            if (!store._baseBricks.TryAdd(key, brick))
                throw new InvalidDataException("Duplicate base brick key.");
        }

        int editBrickCount = ReadCount(reader, maxBrickCount, "edit brick");
        for (int i = 0; i < editBrickCount; i++)
        {
            BrickKey key = ReadBrickKey(reader);
            store.ValidateBrickKey(key);
            EditBrick brick = new();

            if (reader.ReadBoolean())
                brick.Density = ReadDensityArray(reader);
            if (reader.ReadBoolean())
                brick.Material = ReadMaterialArray(reader);

            if (brick.Density == null && brick.Material == null)
                throw new InvalidDataException("An edit brick must contain at least one data channel.");

            if (brick.Density != null)
            {
                for (int j = 0; j < BrickCellCount; j++)
                {
                    brick.Density[j] = store.Quantize(Dequantize(brick.Density[j]));
                    if (brick.Density[j] <= 0 && brick.Material != null)
                        brick.Material[j] = 0;
                }
            }

            if (!store._editBricks.TryAdd(key, brick))
                throw new InvalidDataException("Duplicate edit brick key.");
        }

        store._baseSealed = sealedBase;
        store.Revision = revision;
        return store;
    }

    private void ValidateBrickKey(BrickKey key)
    {
        if (key.X < 0 || key.Y < 0 || key.Z < 0 ||
            (long)key.X * BrickSize >= Width ||
            (long)key.Y * BrickSize >= Height ||
            (long)key.Z * BrickSize >= Depth)
            throw new InvalidDataException("Voxel brick key is outside the world.");
    }

    private static int ReadCount(BinaryReader reader, long maximum, string label)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
            throw new InvalidDataException($"Invalid {label} count.");
        return count;
    }

    private static ShapeKind ReadShape(BinaryReader reader)
    {
        ShapeKind shape = (ShapeKind)reader.ReadByte();
        if (shape is not ShapeKind.Box and not ShapeKind.Sphere and not ShapeKind.Capsule)
            throw new InvalidDataException("Invalid voxel shape.");
        return shape;
    }

    private static BrickKey ReadBrickKey(BinaryReader reader) =>
        new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

    private static void WriteBounds(BinaryWriter writer, VoxelBounds bounds)
    {
        writer.Write(bounds.MinX);
        writer.Write(bounds.MinY);
        writer.Write(bounds.MinZ);
        writer.Write(bounds.MaxX);
        writer.Write(bounds.MaxY);
        writer.Write(bounds.MaxZ);
    }

    private static VoxelBounds ReadBounds(BinaryReader reader) =>
        new(
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32());

    private static void WriteVector(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteDensityArray(BinaryWriter writer, short[] values)
    {
        for (int i = 0; i < values.Length; i++)
            writer.Write(values[i]);
    }

    private static short[] ReadDensityArray(BinaryReader reader)
    {
        short[] values = new short[BrickCellCount];
        for (int i = 0; i < values.Length; i++)
            values[i] = reader.ReadInt16();
        return values;
    }

    private static void WriteMaterialArray(BinaryWriter writer, ushort[] values)
    {
        for (int i = 0; i < values.Length; i++)
            writer.Write(values[i]);
    }

    private static ushort[] ReadMaterialArray(BinaryReader reader)
    {
        ushort[] values = new ushort[BrickCellCount];
        for (int i = 0; i < values.Length; i++)
            values[i] = reader.ReadUInt16();
        return values;
    }

    private static bool AreUniform(
        short[] density,
        ushort[] material,
        out VoxelSample sample)
    {
        short firstDensity = density[0];
        ushort firstMaterial = material[0];
        for (int i = 1; i < BrickCellCount; i++)
        {
            if (density[i] != firstDensity || material[i] != firstMaterial)
            {
                sample = default;
                return false;
            }
        }

        sample = new VoxelSample(firstDensity, firstMaterial);
        return true;
    }

}
