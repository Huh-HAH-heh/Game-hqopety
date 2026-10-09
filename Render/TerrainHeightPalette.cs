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

        float gray =
            baseGray +
            (heightFraction - 0.5f) * heightContrast -
            depthFraction * depthShade;

        if (voxelLayer == visibleMaxLayer)
            gray = Math.Min(202f, gray + 32f);

        byte value = (byte)Math.Clamp(
            (int)MathF.Round(gray),
            0,
            202);

        return new Color(value, value, value, 255);
    }

    public static Color GetHeightPreviewColor(
        int band,
        float baseGray,
        float heightContrast)
    {
        float height = Math.Clamp(band / 4f, 0f, 1f);
        float gray = baseGray + (height - 0.5f) * heightContrast;
        byte value = (byte)Math.Clamp(
            (int)MathF.Round(gray),
            0,
            170);

        return new Color(value, value, value, 255);
    }
}
