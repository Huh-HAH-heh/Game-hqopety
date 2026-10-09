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

        int cyclePosition =
            Math.Clamp(surfaceLayer, 0, int.MaxValue) % 100;

        float heightFraction =
            cyclePosition / 99f;

        int cutSurface = Math.Min(surfaceLayer, visibleMaxLayer);

        float depthFraction = Math.Clamp(
            Math.Max(0, surfaceLayer - voxelLayer) / maxLayer,
            0f,
            1f);

        float heightRange = Math.Clamp(
            baseGray + heightContrast * 1.5f,
            24f,
            72f);

        float gray =
            heightFraction * heightRange -
            depthFraction * (depthShade * 0.7f);

        byte value = (byte)Math.Clamp(
            (int)MathF.Round(gray),
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
        float gray =
            height * Math.Clamp(
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
