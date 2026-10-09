using System;
using SFML.Graphics;

namespace RimClone.Render;

public static class TerrainHeightPalette
{
    public static Color GetTerrainColor(
        int surfaceLayer,
        int voxelLayer,
        int visibleMaxLayer,
        int layerCount,
        float baseGray,
        float heightContrast,
        float depthShade)
    {
        float maxLayer = Math.Max(1, layerCount - 1);

        float heightFraction = Math.Clamp(
            surfaceLayer / maxLayer,
            0f,
            1f);

        int cutSurface = Math.Min(surfaceLayer, visibleMaxLayer);

        float depthFraction = Math.Clamp(
            Math.Max(0, cutSurface - voxelLayer) / maxLayer,
            0f,
            1f);

        float baseAdjustment =
            (baseGray - 48f) * 0.35f;

        float heightRange =
            54f + heightContrast * 1.5f;

        float gray =
            3f +
            heightFraction * heightRange +
            baseAdjustment -
            depthFraction * (depthShade * 0.7f);

        byte value = (byte)Math.Clamp(
            (int)MathF.Round(gray),
            0,
            135);

        return new Color(value, value, value, 255);
    }

    public static Color GetHeightPreviewColor(
        int band,
        float baseGray,
        float heightContrast)
    {
        float height = Math.Clamp(band / 4f, 0f, 1f);
        float gray =
            3f +
            height * (54f + heightContrast * 1.5f) +
            (baseGray - 48f) * 0.35f;

        byte value = (byte)Math.Clamp(
            (int)MathF.Round(gray),
            0,
            135);

        return new Color(value, value, value, 255);
    }
}
