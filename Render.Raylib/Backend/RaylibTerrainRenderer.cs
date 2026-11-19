using System.Diagnostics;
using System.Numerics;
using Raylib_cs;
using RimClone.Render.RaylibBackend.Geometry;

namespace RimClone.Render.RaylibBackend.Backend;

internal sealed class TerrainGpuChunk : IDisposable
{
    private Mesh _mesh;
    private bool _uploaded;

    public BoundingBoxData Bounds { get; }
    public int TriangleCount { get; }

    public TerrainGpuChunk(TerrainChunkMeshData data)
    {
        Bounds = data.Bounds;
        TriangleCount = data.TriangleCount;

        if (data.Positions.Length == 0)
            return;

        Mesh mesh = new Mesh(
            data.Positions.Length,
            data.TriangleCount);

        try
        {
            mesh.AllocVertices();
            mesh.AllocTexCoords();
            mesh.AllocNormals();
            mesh.AllocColors();

            data.Positions.AsSpan().CopyTo(
                mesh.VerticesAs<Vector3>());

            data.Normals.AsSpan().CopyTo(
                mesh.NormalsAs<Vector3>());

            Span<Vector2> texCoords = mesh.TexCoordsAs<Vector2>();
            texCoords.Clear();

            Span<Color> vertexColors = mesh.ColorsAs<Color>();
            for (int i = 0; i < vertexColors.Length; i++)
            {
                Color32 color = data.Colors[i];
                vertexColors[i] = new Color(
                    color.R,
                    color.G,
                    color.B,
                    color.A);
            }

            // All geometry is uploaded once. The normal frame loop never allocates
            // or transfers terrain vertex data.
            Raylib.UploadMesh(ref mesh, false);
            _mesh = mesh;
            _uploaded = true;
        }
        catch
        {
            Raylib.UnloadMesh(mesh);
            throw;
        }
    }

    public void Draw(Material material)
    {
        if (_uploaded)
            Raylib.DrawMesh(_mesh, material, Matrix4x4.Identity);
    }

    public void Dispose()
    {
        if (!_uploaded)
            return;

        Raylib.UnloadMesh(_mesh);
        _mesh = default;
        _uploaded = false;
    }
}

public sealed class RaylibTerrainRenderer : IDisposable
{
    private const float CameraFarPlane = 1400f;

    private readonly TerrainGpuChunk?[] _chunks;
    private readonly Material _material;
    private bool _disposed;

    public int TotalChunks => _chunks.Length;
    public int TotalTriangles { get; }
    public int VisibleChunks { get; private set; }
    public int VisibleTriangles { get; private set; }
    public double UploadMilliseconds { get; }

    public RaylibTerrainRenderer(TerrainChunkMeshData[] meshData)
    {
        ArgumentNullException.ThrowIfNull(meshData);

        _chunks = new TerrainGpuChunk?[meshData.Length];
        TotalTriangles = meshData.Sum(chunk => chunk.TriangleCount);

        Stopwatch timer = Stopwatch.StartNew();
        _material = Raylib.LoadMaterialDefault();

        try
        {
            for (int i = 0; i < meshData.Length; i++)
            {
                if (meshData[i].Positions.Length == 0)
                    continue;

                _chunks[i] = new TerrainGpuChunk(meshData[i]);
            }
        }
        catch
        {
            foreach (TerrainGpuChunk? chunk in _chunks)
                chunk?.Dispose();

            Raylib.UnloadMaterial(_material);
            throw;
        }

        timer.Stop();
        UploadMilliseconds = timer.Elapsed.TotalMilliseconds;
    }

    public void Draw(Camera3D camera, int viewportWidth, int viewportHeight)
    {
        VisibleChunks = 0;
        VisibleTriangles = 0;

        float aspect = viewportHeight <= 0
            ? 1f
            : viewportWidth / (float)viewportHeight;

        for (int i = 0; i < _chunks.Length; i++)
        {
            TerrainGpuChunk? chunk = _chunks[i];
            if (chunk == null ||
                !IsVisible(chunk.Bounds, camera, aspect))
            {
                continue;
            }

            chunk.Draw(_material);
            VisibleChunks++;
            VisibleTriangles += chunk.TriangleCount;
        }
    }

    private static bool IsVisible(
        BoundingBoxData bounds,
        Camera3D camera,
        float aspect)
    {
        Vector3 forward = camera.Target - camera.Position;
        if (forward.LengthSquared() < 0.0001f)
            return true;

        forward = Vector3.Normalize(forward);
        Vector3 right = Vector3.Normalize(
            Vector3.Cross(forward, camera.Up));
        Vector3 up = Vector3.Normalize(
            Vector3.Cross(right, forward));

        Vector3 center = (bounds.Min + bounds.Max) * 0.5f;
        Vector3 extents = (bounds.Max - bounds.Min) * 0.5f;
        float radius = extents.Length();
        Vector3 relative = center - camera.Position;

        float depth = Vector3.Dot(relative, forward);
        if (depth + radius < 0.1f ||
            depth - radius > CameraFarPlane)
        {
            return false;
        }

        float safeDepth = Math.Max(depth, 0f);
        float verticalHalfExtent =
            MathF.Tan(camera.FovY * MathF.PI / 360f) * safeDepth;
        float horizontalHalfExtent = verticalHalfExtent * Math.Max(aspect, 0.1f);

        if (MathF.Abs(Vector3.Dot(relative, right)) >
            horizontalHalfExtent + radius)
        {
            return false;
        }

        return MathF.Abs(Vector3.Dot(relative, up)) <=
            verticalHalfExtent + radius;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (TerrainGpuChunk? chunk in _chunks)
            chunk?.Dispose();

        Raylib.UnloadMaterial(_material);
        _disposed = true;
    }
}
