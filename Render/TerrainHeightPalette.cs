using System;
using SFML.Graphics;

namespace RimClone.Render;

public static class TerrainHeightPalette
{
    // The full 50 m terrain range gets one grayscale cycle.
    public const int ColorCycleLayerCount = 500;

    private const float ShadowBias = 1.8f;

    public static Color GetTerrainColor(
        int surfaceLayer,
        float baseGray,
        float heightContrast)
    {
        return GetTerrainColor(
            (float)surfaceLayer,
            baseGray,
            heightContrast);
    }

    public static Color GetTerrainColor(
        float surfaceLayer,
        float baseGray,
        float heightContrast)
    {
        float heightFraction =
            Math.Clamp(surfaceLayer, 0f, ColorCycleLayerCount - 1f) /
            (ColorCycleLayerCount - 1f);

        float tonalFraction =
            MathF.Pow(heightFraction, ShadowBias);

        float heightRange = Math.Clamp(
            baseGray + heightContrast * 1.5f,
            24f,
            72f);

        byte value = (byte)Math.Clamp(
            (int)MathF.Round(tonalFraction * heightRange),
            0,
            72);

        return new Color(value, value, value, 255);
    }

    public static Color GetHeightPreviewColor(
        int band,
        float baseGray,
        float heightContrast)
    {
        float height = Math.Clamp(band / 4f, 0f, 1f);
        float tonalFraction = MathF.Pow(height, ShadowBias);

        float gray =
            tonalFraction * Math.Clamp(
                baseGray + heightContrast * 1.5f,
                24f,
                72f);

        byte value = (byte)Math.Clamp(
            (int)MathF.Round(gray),
            0,
            72);

        return new Color(value, value, value, 255);
    }
}
