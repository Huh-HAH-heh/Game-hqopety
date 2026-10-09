using System;
using SFML.Graphics;

namespace RimClone.Render;

public static class TerrainHeightPalette
{
    public static Color GetSurfaceColor(
        int surfaceLayer,
        int layerCount)
    {
        float height =
            layerCount <= 1
                ? 0f
                : Math.Clamp(
                    surfaceLayer / (float)(layerCount - 1),
                    0f,
                    1f);

        Color low = new Color(35, 115, 235);
        Color cyan = new Color(35, 195, 205);
        Color green = new Color(85, 205, 95);
        Color yellow = new Color(245, 205, 55);
        Color red = new Color(220, 50, 55);

        if (height < 0.25f)
            return Blend(low, cyan, height * 4f);

        if (height < 0.5f)
            return Blend(cyan, green, (height - 0.25f) * 4f);

        if (height < 0.75f)
            return Blend(green, yellow, (height - 0.5f) * 4f);

        return Blend(yellow, red, (height - 0.75f) * 4f);
    }

    public static Color GetBandColor(
        int band,
        int layerCount)
    {
        int surfaceLayer = band switch
        {
            0 => 0,
            1 => Math.Max(0, layerCount / 4 - 1),
            2 => Math.Max(0, layerCount / 2 - 1),
            3 => Math.Max(0, layerCount * 3 / 4 - 1),
            _ => Math.Max(0, layerCount - 1)
        };

        return GetSurfaceColor(surfaceLayer, layerCount);
    }

    public static Color ShadeDepth(
        Color color,
        float depth)
    {
        float factor = MathF.Max(
            0.55f,
            1f / (1f + MathF.Max(0f, depth) * 0.06f));

        return new Color(
            (byte)(color.R * factor),
            (byte)(color.G * factor),
            (byte)(color.B * factor),
            color.A);
    }

    private static Color Blend(
        Color first,
        Color second,
        float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);

        return new Color(
            (byte)(first.R + (second.R - first.R) * amount),
            (byte)(first.G + (second.G - first.G) * amount),
            (byte)(first.B + (second.B - first.B) * amount),
            255);
    }
}
