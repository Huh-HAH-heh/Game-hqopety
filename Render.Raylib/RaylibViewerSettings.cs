using System;
using System.IO;
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

internal enum TerrainColorSlot : byte
{
    Lowlands,
    Highlands,
    CliffFaces,
    CutSurface,
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

    public RgbColorSetting Lowlands { get; set; } = new(7, 8, 10);
    public RgbColorSetting Highlands { get; set; } = new(41, 42, 45);
    public RgbColorSetting CliffFaces { get; set; } = new(22, 23, 26);
    public RgbColorSetting CutSurface { get; set; } = new(14, 15, 18);
    public RgbColorSetting CutHatching { get; set; } = new(125, 132, 142);
    public RgbColorSetting Background { get; set; } = new(0, 0, 0);
    public bool ShowCutHatching { get; set; } = true;

    public Color GetSurfaceColor(float height, ushort materialId)
    {
        float amount = Math.Clamp(height / 50f, 0f, 1f);
        Color color = Lerp(Lowlands.ToRaylibColor(), Highlands.ToRaylibColor(), amount);
        return ApplyMaterialTint(color, materialId);
    }

    public Color GetCliffColor(ushort materialId)
    {
        return ApplyMaterialTint(CliffFaces.ToRaylibColor(), materialId);
    }

    public Color GetCutSurfaceColor(ushort materialId)
    {
        return ApplyMaterialTint(CutSurface.ToRaylibColor(), materialId);
    }

    public Color GetHatchingColor() => CutHatching.ToRaylibColor();

    public void Normalize()
    {
        Lowlands ??= new RgbColorSetting(7, 8, 10);
        Highlands ??= new RgbColorSetting(41, 42, 45);
        CliffFaces ??= new RgbColorSetting(22, 23, 26);
        CutSurface ??= new RgbColorSetting(14, 15, 18);
        CutHatching ??= new RgbColorSetting(125, 132, 142);
        Background ??= new RgbColorSetting(0, 0, 0);

        Lowlands.Normalize();
        Highlands.Normalize();
        CliffFaces.Normalize();
        CutSurface.Normalize();
        CutHatching.Normalize();
        Background.Normalize();
    }

    private static Color ApplyMaterialTint(Color color, ushort materialId)
    {
        float red = 1f;
        float green = 1f;
        float blue = 1f;

        switch (materialId)
        {
            case 2:
                red = 1.08f;
                green = 1.06f;
                blue = 0.96f;
                break;
            case 3:
                red = 0.88f;
                green = 0.91f;
                blue = 1.08f;
                break;
        }

        return new Color(
            (byte)Math.Clamp((int)MathF.Round(color.R * red), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(color.G * green), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(color.B * blue), 0, 255),
            (byte)255);
    }

    private static Color Lerp(Color a, Color b, float amount) => new(
        (byte)Math.Clamp((int)MathF.Round(a.R + (b.R - a.R) * amount), 0, 255),
        (byte)Math.Clamp((int)MathF.Round(a.G + (b.G - a.G) * amount), 0, 255),
        (byte)Math.Clamp((int)MathF.Round(a.B + (b.B - a.B) * amount), 0, 255),
        (byte)255);
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
    public TerrainRenderMode TerrainMode { get; set; } = TerrainRenderMode.Voxel;

    public bool ShowHud { get; set; } = true;
    public bool ShowControls { get; set; } = true;
    public bool ShowPerformanceStats { get; set; } = true;
    public bool ShowCaveCoordinates { get; set; } = true;

    public float RenderDistanceMeters { get; set; } = 8000f;

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

        CameraMoveSpeed = Math.Clamp(CameraMoveSpeed, 0.25f, 4f);
        CameraRotationSpeed = Math.Clamp(CameraRotationSpeed, 0.25f, 4f);
        CameraPanSpeed = Math.Clamp(CameraPanSpeed, 0.25f, 4f);
        CameraZoomSpeed = Math.Clamp(CameraZoomSpeed, 0.25f, 4f);
        CameraVerticalSpeed = Math.Clamp(CameraVerticalSpeed, 0.25f, 4f);
        RenderDistanceMeters = Math.Clamp(RenderDistanceMeters, 500f, 18000f);

        if (!float.IsFinite(CutawayHeightMeters))
            CutawayHeightMeters = 17f;
        CutawayHeightMeters = Math.Clamp(CutawayHeightMeters, 0f, 49.9f);

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
