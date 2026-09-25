using Core.Map;
using SFML.System;

namespace RimClone.Render;

public static class WorldRenderMath
{
    public static float TileToPixelX(
        float tileX,
        float tilePixelSize)
    {
        return
            tileX * tilePixelSize +
            tilePixelSize * 0.5f;
    }

    public static float TileToPixelY(
        float tileY,
        float tilePixelSize)
    {
        return
            tileY * tilePixelSize +
            tilePixelSize * 0.5f;
    }

    public static Vector2f TileToPixel(
        float tileX,
        float tileY,
        float tilePixelSize)
    {
        return new Vector2f(
            TileToPixelX(
                tileX,
                tilePixelSize),

            TileToPixelY(
                tileY,
                tilePixelSize));
    }
}