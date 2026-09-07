using Core.Input.Systems;
using Core.Items;
using Core.Map;
using Core.Structs;
using Core.Unit;
using Core.Unit.Components;
using SFML.Graphics;
using SFML.System;
using SFML.Window;
using System;
using System.Net.NetworkInformation;
using static System.Collections.Specialized.BitVector32;

namespace RimClone.Render;

public static class WorldRenderMath
{
    public const float HeightPixelsPerLevel = 1.5f;

    public const int MaxVisualHeight = 32;

    public static float GetHeightOffset(
        byte height)
    {
        int h =
            Math.Min(
                (int)height,
                MaxVisualHeight);

        return
            h * HeightPixelsPerLevel;
    }

    public static float MicroToPixelX(
        float microX,
        float microCellPixelSize)
    {
        return
            microX * microCellPixelSize +
            microCellPixelSize * 0.5f;
    }

    public static float MicroToPixelY(
        float microY,
        byte height,
        float microCellPixelSize)
    {
        return
            microY * microCellPixelSize +
            microCellPixelSize * 0.5f -
            GetHeightOffset(height);
    }

    public static Vector2fData GetUnitPixelPosition(
        UnitPosition position,
        MapLayer layer,
        float microCellPixelSize)
    {
        int x =
            position.Spatial.X;

        int y =
            position.Spatial.Y;

        byte height = 0;

        if (layer != null &&
            x >= 0 &&
            y >= 0 &&
            x < 16 * 48 &&
            y < 16 * 48)
        {
            height =
                layer.GetMicroCell(
                    x,
                    y).Height;
        }

        float pixelX =
            MicroToPixelX(
                position.RenderX,
                microCellPixelSize);

        float pixelY =
            MicroToPixelY(
                position.RenderY,
                height,
                microCellPixelSize);

        return new Vector2fData(
            pixelX,
            pixelY);
    }

    public static int GetMaxCoord()
    {
        return
            16 * 48 - 1;
    }
}

public readonly struct Vector2fData
{
    public readonly float X;
    public readonly float Y;

    public Vector2fData(
        float x,
        float y)
    {
        X = x;
        Y = y;
    }
}
