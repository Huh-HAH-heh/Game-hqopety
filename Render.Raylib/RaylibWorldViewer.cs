using System.Diagnostics;
using Raylib_cs;
using Core.Map;
using RimClone.Render.RaylibBackend.Backend;
using RimClone.Render.RaylibBackend.Camera;
using RimClone.Render.RaylibBackend.Data;
using RimClone.Render.RaylibBackend.Geometry;

namespace RimClone.Render.RaylibBackend;

/// <summary>
/// First standalone presentation path: it reads the generated world, builds
/// region meshes once, uploads them once, then renders the cached GPU resources.
/// </summary>
public static class RaylibWorldViewer
{
    private const string ControlsText =
        "RMB orbit  |  MMB pan  |  Wheel zoom  |  WASD move  |  Q/E altitude  |  Shift fast  |  Home reset";

    public static void Run(WorldMap worldMap)
    {
        ArgumentNullException.ThrowIfNull(worldMap);

        Stopwatch captureTimer = Stopwatch.StartNew();
        WorldTerrainSnapshot snapshot =
            WorldTerrainSnapshot.Capture(worldMap);
        captureTimer.Stop();

        Stopwatch meshTimer = Stopwatch.StartNew();
        TerrainChunkMeshData[] meshData =
            TerrainChunkMeshBuilder.BuildAll(snapshot);
        meshTimer.Stop();

        int totalVertices = 0;
        int totalTriangles = 0;
        foreach (TerrainChunkMeshData chunk in meshData)
        {
            totalVertices += chunk.Positions.Length;
            totalTriangles += chunk.TriangleCount;
        }

        Console.WriteLine(
            $"[RAYLIB] Snapshot: {captureTimer.Elapsed.TotalMilliseconds:0.0} ms; " +
            $"regions: {snapshot.RegionsX}x{snapshot.RegionsY}; " +
            $"tile grid: {snapshot.TileWidth}x{snapshot.TileHeight}.");

        Console.WriteLine(
            $"[RAYLIB] CPU mesh build: {meshTimer.Elapsed.TotalMilliseconds:0.0} ms; " +
            $"chunks: {meshData.Length}; vertices: {totalVertices:N0}; " +
            $"triangles: {totalTriangles:N0}.");

        Raylib.SetConfigFlags(
            ConfigFlags.ResizableWindow |
            ConfigFlags.VSyncHint);

        Raylib.InitWindow(
            1600,
            900,
            "RimClone | Raylib terrain renderer");

        Raylib.SetWindowMinSize(1024, 640);
        Raylib.SetTargetFPS(144);

        try
        {
            using RaylibTerrainRenderer renderer =
                new RaylibTerrainRenderer(meshData);

            OrbitCameraController camera =
                new OrbitCameraController(snapshot);

            string timingText =
                $"Snapshot {captureTimer.Elapsed.TotalMilliseconds:0.0} ms | " +
                $"CPU mesh {meshTimer.Elapsed.TotalMilliseconds:0.0} ms | " +
                $"GPU upload {renderer.UploadMilliseconds:0.0} ms";

            string fpsText = "FPS --";
            string chunkText = string.Empty;
            string triangleText = string.Empty;
            float overlayRefreshTimer = 0f;

            Console.WriteLine(
                $"[RAYLIB] GPU upload: {renderer.UploadMilliseconds:0.0} ms; " +
                $"draw-ready chunks: {renderer.TotalChunks}.");

            while (!Raylib.WindowShouldClose())
            {
                float deltaTime = Math.Clamp(
                    Raylib.GetFrameTime(),
                    0f,
                    0.05f);

                camera.Update(deltaTime);
                Camera3D cameraState = camera.GetCamera();

                Raylib.BeginDrawing();
                Raylib.ClearBackground(new Color(145, 165, 180));

                Raylib.BeginMode3D(cameraState);
                renderer.Draw(
                    cameraState,
                    Raylib.GetScreenWidth(),
                    Raylib.GetScreenHeight());
                Raylib.EndMode3D();

                // UI text is formatted four times per second, not on every frame.
                overlayRefreshTimer += deltaTime;
                if (overlayRefreshTimer >= 0.25f)
                {
                    overlayRefreshTimer = 0f;
                    fpsText =
                        $"FPS {Raylib.GetFPS()} | Frame {Raylib.GetFrameTime() * 1000f:0.0} ms";
                    chunkText =
                        $"Chunks {renderer.VisibleChunks}/{renderer.TotalChunks} | " +
                        $"Draw calls {renderer.VisibleChunks}";
                    triangleText =
                        $"Triangles {renderer.VisibleTriangles:N0}/{renderer.TotalTriangles:N0}";
                }

                DrawOverlay(
                    fpsText,
                    chunkText,
                    triangleText,
                    timingText);

                Raylib.EndDrawing();
            }
        }
        finally
        {
            Raylib.CloseWindow();
        }
    }

    private static void DrawOverlay(
        string fpsText,
        string chunkText,
        string triangleText,
        string timingText)
    {
        Raylib.DrawRectangle(
            10,
            10,
            570,
            126,
            new Color(18, 24, 28, 218));

        Raylib.DrawText(
            fpsText,
            22,
            18,
            22,
            Color.White);

        Raylib.DrawText(
            chunkText,
            22,
            48,
            16,
            new Color(216, 224, 226));

        Raylib.DrawText(
            triangleText,
            22,
            72,
            16,
            new Color(216, 224, 226));

        Raylib.DrawText(
            timingText,
            22,
            98,
            14,
            new Color(190, 202, 207));

        Raylib.DrawText(
            ControlsText,
            18,
            Raylib.GetScreenHeight() - 28,
            15,
            Color.White);
    }
}
