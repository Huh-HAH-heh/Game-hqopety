using System.Numerics;
using Raylib_cs;
using RimClone.Render.RaylibBackend.Data;
using RimClone.Render.RaylibBackend.Geometry;

namespace RimClone.Render.RaylibBackend.Camera;

public sealed class OrbitCameraController
{
    private const float MinimumDistance = 8f;
    private const float MaximumDistance = 1400f;
    private const float MinimumPitch = 8f * MathF.PI / 180f;
    private const float MaximumPitch = 82f * MathF.PI / 180f;
    private const float MouseOrbitSensitivity = 0.004f;

    private readonly Vector3 _initialTarget;
    private readonly float _initialDistance;

    private Vector3 _target;
    private float _yaw = 45f * MathF.PI / 180f;
    private float _pitch = 38f * MathF.PI / 180f;
    private float _distance;

    public Vector3 Target => _target;
    public float Distance => _distance;

    public OrbitCameraController(WorldTerrainSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _initialTarget = new Vector3(
            snapshot.TileWidth * TerrainChunkMeshBuilder.HorizontalUnitsPerTile * 0.5f,
            snapshot.AverageHeightMeters * TerrainChunkMeshBuilder.VerticalUnitsPerMeter,
            snapshot.TileHeight * TerrainChunkMeshBuilder.HorizontalUnitsPerTile * 0.5f);

        _target = _initialTarget;
        _initialDistance = Math.Clamp(
            Math.Max(snapshot.TileWidth, snapshot.TileHeight) *
            TerrainChunkMeshBuilder.HorizontalUnitsPerTile * 0.86f,
            100f,
            1000f);
        _distance = _initialDistance;
    }

    public Camera3D GetCamera()
    {
        float horizontalDistance = MathF.Cos(_pitch) * _distance;

        Vector3 position = new(
            _target.X + MathF.Sin(_yaw) * horizontalDistance,
            _target.Y + MathF.Sin(_pitch) * _distance,
            _target.Z + MathF.Cos(_yaw) * horizontalDistance);

        return new Camera3D(
            position,
            _target,
            Vector3.UnitY,
            48f,
            CameraProjection.Perspective);
    }

    public void Update(float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        UpdateOrbit();
        UpdatePan();
        UpdateZoom();
        UpdateMovement(deltaTime);

        if (Raylib.IsKeyPressed(KeyboardKey.Home))
            Reset();
    }

    private void UpdateOrbit()
    {
        if (!Raylib.IsMouseButtonDown(MouseButton.Right))
            return;

        Vector2 delta = Raylib.GetMouseDelta();
        _yaw -= delta.X * MouseOrbitSensitivity;
        _pitch = Math.Clamp(
            _pitch + delta.Y * MouseOrbitSensitivity,
            MinimumPitch,
            MaximumPitch);
    }

    private void UpdatePan()
    {
        if (!Raylib.IsMouseButtonDown(MouseButton.Middle))
            return;

        Vector2 delta = Raylib.GetMouseDelta();
        Camera3D camera = GetCamera();
        Vector3 forward = camera.Target - camera.Position;
        forward.Y = 0f;

        if (forward.LengthSquared() < 0.0001f)
            return;

        forward = Vector3.Normalize(forward);
        Vector3 right = Vector3.Normalize(
            Vector3.Cross(forward, Vector3.UnitY));

        float scale = _distance * 0.0015f;
        _target -= right * delta.X * scale;
        _target += forward * delta.Y * scale;
    }

    private void UpdateZoom()
    {
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel == 0f)
            return;

        _distance = Math.Clamp(
            _distance * MathF.Exp(-wheel * 0.14f),
            MinimumDistance,
            MaximumDistance);
    }

    private void UpdateMovement(float deltaTime)
    {
        Camera3D camera = GetCamera();
        Vector3 forward = camera.Target - camera.Position;
        forward.Y = 0f;

        if (forward.LengthSquared() < 0.0001f)
            return;

        forward = Vector3.Normalize(forward);
        Vector3 right = Vector3.Normalize(
            Vector3.Cross(forward, Vector3.UnitY));

        float forwardInput =
            (Raylib.IsKeyDown(KeyboardKey.W) || Raylib.IsKeyDown(KeyboardKey.Up) ? 1f : 0f) -
            (Raylib.IsKeyDown(KeyboardKey.S) || Raylib.IsKeyDown(KeyboardKey.Down) ? 1f : 0f);

        float rightInput =
            (Raylib.IsKeyDown(KeyboardKey.D) || Raylib.IsKeyDown(KeyboardKey.Right) ? 1f : 0f) -
            (Raylib.IsKeyDown(KeyboardKey.A) || Raylib.IsKeyDown(KeyboardKey.Left) ? 1f : 0f);

        Vector3 movement = forward * forwardInput + right * rightInput;
        if (movement.LengthSquared() > 1f)
            movement = Vector3.Normalize(movement);

        float speed = Math.Clamp(_distance * 0.42f, 28f, 260f);
        if (Raylib.IsKeyDown(KeyboardKey.LeftShift) ||
            Raylib.IsKeyDown(KeyboardKey.RightShift))
        {
            speed *= 2.5f;
        }
        else if (Raylib.IsKeyDown(KeyboardKey.LeftControl) ||
                 Raylib.IsKeyDown(KeyboardKey.RightControl))
        {
            speed *= 0.3f;
        }

        _target += movement * speed * deltaTime;

        float verticalInput =
            (Raylib.IsKeyDown(KeyboardKey.E) ? 1f : 0f) -
            (Raylib.IsKeyDown(KeyboardKey.Q) ? 1f : 0f);

        _target.Y += verticalInput * speed * 0.65f * deltaTime;
    }

    private void Reset()
    {
        _target = _initialTarget;
        _yaw = 45f * MathF.PI / 180f;
        _pitch = 38f * MathF.PI / 180f;
        _distance = _initialDistance;
    }
}
