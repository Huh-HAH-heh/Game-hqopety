using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Raylib_cs;

namespace RimClone.Render.Raylib;

internal enum RaylibCameraMode : byte
{
    Rts,
    Orbit,
    TopDown
}

internal enum TerrainRenderMode : byte
{
    Voxel,
    HeightmapFallback
}

internal enum RenderLogMode : byte
{
    ErrorsOnly,
    FrameSummary,
    Detailed,
    Trace
}

internal enum TerrainColorSlot : byte
{
    BaseRock,
    CutHatching,
    Background
}

internal sealed class RgbColorSetting
{
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }

    public RgbColorSetting()
        : this(0, 0, 0)
    {
    }

    public RgbColorSetting(int r, int g, int b)
    {
        R = r;
        G = g;
        B = b;
    }

    public Color ToRaylibColor() => new(
        (byte)Math.Clamp(R, 0, 255),
        (byte)Math.Clamp(G, 0, 255),
        (byte)Math.Clamp(B, 0, 255),
        (byte)255);

    public void Normalize()
    {
        R = Math.Clamp(R, 0, 255);
        G = Math.Clamp(G, 0, 255);
        B = Math.Clamp(B, 0, 255);
    }
}

internal sealed class TerrainPalette
{
    public TerrainPalette()
    {
    }

    // One neutral rock albedo is shared by every terrain face. Depth comes
    // from the directional light, not from height- or material-based colors.
    public RgbColorSetting BaseRock { get; set; } = new(92, 96, 103);
    public RgbColorSetting CutHatching { get; set; } = new(137, 143, 152);
    public RgbColorSetting Background { get; set; } = new(31, 35, 43);
    public bool ShowCutHatching { get; set; } = true;

    // Cheap global lighting stub: one directional sun and an ambient floor.
    // These values can later be driven by the world's time-of-day/light system.
    public float SunDirectionX { get; set; } = -0.45f;
    public float SunDirectionY { get; set; } = 0.82f;
    public float SunDirectionZ { get; set; } = 0.35f;
    public float AmbientLight { get; set; } = 0.70f;
    public float SunIntensity { get; set; } = 0.58f;

    public Color GetSurfaceColor(float height, ushort materialId) =>
        BaseRock.ToRaylibColor();

    public Color GetCliffColor(ushort materialId) =>
        BaseRock.ToRaylibColor();

    public Color GetCutSurfaceColor(ushort materialId) =>
        BaseRock.ToRaylibColor();

    public Color GetHatchingColor() => CutHatching.ToRaylibColor();

    public Vector3 GetSunDirection()
    {
        Vector3 direction = new(SunDirectionX, SunDirectionY, SunDirectionZ);
        if (direction.LengthSquared() < 0.0001f)
            direction = new Vector3(-0.45f, 0.82f, 0.35f);
        return Vector3.Normalize(direction);
    }

    public Color ApplyStaticLighting(Color color, Vector3 normal)
    {
        float diffuse = MathF.Max(0f,
            Vector3.Dot(Vector3.Normalize(normal), GetSunDirection()));
        float illumination = Math.Clamp(
            AmbientLight + diffuse * SunIntensity, 0.05f, 1.5f);
        return new Color(
            (byte)Math.Clamp((int)MathF.Round(color.R * illumination), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(color.G * illumination), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(color.B * illumination), 0, 255),
            color.A);
    }

    public void Normalize()
    {
        BaseRock ??= new RgbColorSetting(92, 96, 103);
        CutHatching ??= new RgbColorSetting(137, 143, 152);
        Background ??= new RgbColorSetting(31, 35, 43);
        BaseRock.Normalize();
        CutHatching.Normalize();
        Background.Normalize();

        if (!float.IsFinite(SunDirectionX)) SunDirectionX = -0.45f;
        if (!float.IsFinite(SunDirectionY)) SunDirectionY = 0.82f;
        if (!float.IsFinite(SunDirectionZ)) SunDirectionZ = 0.35f;
        if (!float.IsFinite(AmbientLight)) AmbientLight = 0.70f;
        if (!float.IsFinite(SunIntensity)) SunIntensity = 0.58f;
        AmbientLight = Math.Clamp(AmbientLight, 0.05f, 1f);
        SunIntensity = Math.Clamp(SunIntensity, 0f, 1.5f);

        if (SunDirectionX * SunDirectionX +
            SunDirectionY * SunDirectionY +
            SunDirectionZ * SunDirectionZ < 0.0001f)
        {
            SunDirectionX = -0.45f;
            SunDirectionY = 0.82f;
            SunDirectionZ = 0.35f;
        }
    }
}

internal sealed class RaylibViewerSettings
{
    public RaylibViewerSettings()
    {
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public int WindowWidth { get; set; } = 1600;
    public int WindowHeight { get; set; } = 900;
    public bool Fullscreen { get; set; }
    public int TargetFps { get; set; } = 144;
    public bool VSync { get; set; } = true;
    public bool Msaa4x { get; set; } = true;

    public RaylibCameraMode CameraMode { get; set; } = RaylibCameraMode.Rts;
    public float CameraMoveSpeed { get; set; } = 1f;
    public float CameraRotationSpeed { get; set; } = 1f;
    public float CameraPanSpeed { get; set; } = 1f;
    public float CameraZoomSpeed { get; set; } = 1f;
    public float CameraVerticalSpeed { get; set; } = 1f;

    public bool CutawayEnabled { get; set; }
    public float CutawayHeightMeters { get; set; } = 17f;
    public float CutawayStepMeters { get; set; } = 0.1f;
    public TerrainRenderMode TerrainMode { get; set; } = TerrainRenderMode.Voxel;

    public bool ShowHud { get; set; } = true;
    public bool ShowControls { get; set; } = true;
    public bool ShowPerformanceStats { get; set; } = true;
    public bool ShowCaveCoordinates { get; set; } = true;

    public float RenderDistanceMeters { get; set; } = 8000f;

    public bool RenderLoggingEnabled { get; set; }
    public RenderLogMode LogMode { get; set; } = RenderLogMode.Detailed;

    public TerrainPalette Palette { get; set; } = new();

    public static RaylibViewerSettings Load()
    {
        string path = GetSettingsPath();

        try
        {
            if (!File.Exists(path))
                return new RaylibViewerSettings();

            string json = File.ReadAllText(path);
            RaylibViewerSettings? settings =
                JsonSerializer.Deserialize<RaylibViewerSettings>(json, JsonOptions);

            settings ??= new RaylibViewerSettings();

            settings.Palette ??= new TerrainPalette();

            // Upgrade only the exact shipped defaults. User-customized colors
            // and lighting values are preserved.
            bool hadDarkDefaultRock =
                settings.Palette.BaseRock is { R: 28, G: 30, B: 33 };
            if (hadDarkDefaultRock)
                settings.Palette.BaseRock = new RgbColorSetting(92, 96, 103);

            if (settings.Palette.CutHatching is { R: 76, G: 81, B: 89 })
                settings.Palette.CutHatching = new RgbColorSetting(137, 143, 152);

            if (settings.Palette.Background is { R: 18, G: 22, B: 29 } or { R: 0, G: 0, B: 0 })
                settings.Palette.Background = new RgbColorSetting(31, 35, 43);

            if (hadDarkDefaultRock &&
                MathF.Abs(settings.Palette.AmbientLight - 0.34f) < 0.001f &&
                MathF.Abs(settings.Palette.SunIntensity - 0.82f) < 0.001f)
            {
                settings.Palette.AmbientLight = 0.70f;
                settings.Palette.SunIntensity = 0.58f;
            }

            settings.Normalize();
            return settings;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"[Settings] Could not load viewer settings; defaults will be used: {exception.Message}");
            return new RaylibViewerSettings();
        }
    }

    public void Save()
    {
        try
        {
            Normalize();
            string path = GetSettingsPath();
            string? directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(this, JsonOptions);
            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"[Settings] Could not save viewer settings: {exception.Message}");
        }
    }

    public void Normalize()
    {
        WindowWidth = Math.Clamp(WindowWidth, 960, 7680);
        WindowHeight = Math.Clamp(WindowHeight, 540, 4320);

        int[] supportedFps = { 30, 45, 60, 90, 120, 144, 165, 240, 0 };
        if (Array.IndexOf(supportedFps, TargetFps) < 0)
            TargetFps = 144;

        if (!Enum.IsDefined(CameraMode))
            CameraMode = RaylibCameraMode.Rts;
        if (!Enum.IsDefined(TerrainMode))
            TerrainMode = TerrainRenderMode.Voxel;
        if (!Enum.IsDefined(LogMode))
            LogMode = RenderLogMode.Detailed;

        CameraMoveSpeed = Math.Clamp(CameraMoveSpeed, 0.25f, 4f);
        CameraRotationSpeed = Math.Clamp(CameraRotationSpeed, 0.25f, 4f);
        CameraPanSpeed = Math.Clamp(CameraPanSpeed, 0.25f, 4f);
        CameraZoomSpeed = Math.Clamp(CameraZoomSpeed, 0.25f, 4f);
        CameraVerticalSpeed = Math.Clamp(CameraVerticalSpeed, 0.25f, 4f);
        RenderDistanceMeters = Math.Clamp(RenderDistanceMeters, 500f, 18000f);

        if (!float.IsFinite(CutawayHeightMeters))
            CutawayHeightMeters = 17f;
        CutawayHeightMeters = Math.Clamp(CutawayHeightMeters, 0f, 49.9f);

        CutawayStepMeters = float.IsFinite(CutawayStepMeters) &&
            CutawayStepMeters >= 0.5f
                ? 1f
                : 0.1f;

        Palette ??= new TerrainPalette();
        Palette.Normalize();
    }

    public static string GetSettingsPath()
    {
        string root = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;

        return Path.Combine(root, "RimClone", "raylib-viewer-settings.json");
    }
}
