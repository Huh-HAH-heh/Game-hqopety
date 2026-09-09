using System;
using Core.Items;
using Core.Map;
using Core.Structs;

namespace Core.CombatPath;

public static class VisibilityChecker
{
    private const int MaxCoord =
        MapLayer.WidthInRegions * MapRegion.MicroSize - 1;

    public static bool HasLineOfSight(
        MapLayer layer,
        EdificeStore edificeStore,
        int x0,
        int y0,
        HeightRange sourceHeight,
        int x1,
        int y1,
        HeightRange targetHeight)
    {
        if (layer == null)
            return false;

        if (!InBounds(x0, y0) ||
            !InBounds(x1, y1))
        {
            return false;
        }

        if (x0 == x1 &&
            y0 == y1)
        {
            return true;
        }

        int dx = x1 - x0;
        int dy = y1 - y0;

        int stepX =
            dx < 0
                ? -1
                : 1;

        int stepY =
            dy < 0
                ? -1
                : 1;

        float deltaX =
            dx == 0
                ? float.PositiveInfinity
                : 1f / MathF.Abs(dx);

        float deltaY =
            dy == 0
                ? float.PositiveInfinity
                : 1f / MathF.Abs(dy);

        float tMaxX =
            dx == 0
                ? float.PositiveInfinity
                : 0.5f * deltaX;

        float tMaxY =
            dy == 0
                ? float.PositiveInfinity
                : 0.5f * deltaY;

        int x = x0;
        int y = y0;

        while (true)
        {
            if (tMaxX < tMaxY)
            {
                x += stepX;

                float tEnter = tMaxX;

                tMaxX += deltaX;

                if (x == x1 &&
                    y == y1)
                {
                    return true;
                }

                float tExit =
                    MathF.Min(
                        tMaxX,
                        tMaxY);

                if (!CheckCell(
                        layer,
                        edificeStore,
                        x,
                        y,
                        sourceHeight,
                        targetHeight,
                        tEnter,
                        tExit))
                {
                    return false;
                }
            }
            else if (tMaxY < tMaxX)
            {
                y += stepY;

                float tEnter = tMaxY;

                tMaxY += deltaY;

                if (x == x1 &&
                    y == y1)
                {
                    return true;
                }

                float tExit =
                    MathF.Min(
                        tMaxX,
                        tMaxY);

                if (!CheckCell(
                        layer,
                        edificeStore,
                        x,
                        y,
                        sourceHeight,
                        targetHeight,
                        tEnter,
                        tExit))
                {
                    return false;
                }
            }
            else
            {
                /*
                 * Луч проходит точно через угол.
                 *
                 * Проверяем обе соседние клетки,
                 * чтобы нельзя было смотреть
                 * через угол двух препятствий.
                 */

                float t = tMaxX;

                int sideX = x + stepX;
                int sideY = y;

                if (InBounds(sideX, sideY) &&
                    !(sideX == x1 &&
                      sideY == y1))
                {
                    if (!CheckCell(
                            layer,
                            edificeStore,
                            sideX,
                            sideY,
                            sourceHeight,
                            targetHeight,
                            t,
                            t))
                    {
                        return false;
                    }
                }

                int sideX2 = x;
                int sideY2 = y + stepY;

                if (InBounds(sideX2, sideY2) &&
                    !(sideX2 == x1 &&
                      sideY2 == y1))
                {
                    if (!CheckCell(
                            layer,
                            edificeStore,
                            sideX2,
                            sideY2,
                            sourceHeight,
                            targetHeight,
                            t,
                            t))
                    {
                        return false;
                    }
                }

                x += stepX;
                y += stepY;

                tMaxX += deltaX;
                tMaxY += deltaY;

                if (x == x1 &&
                    y == y1)
                {
                    return true;
                }

                float tExit =
                    MathF.Min(
                        tMaxX,
                        tMaxY);

                if (!CheckCell(
                        layer,
                        edificeStore,
                        x,
                        y,
                        sourceHeight,
                        targetHeight,
                        t,
                        tExit))
                {
                    return false;
                }
            }
        }
    }

    private static bool CheckCell(
        MapLayer layer,
        EdificeStore edificeStore,
        int x,
        int y,
        HeightRange sourceHeight,
        HeightRange targetHeight,
        float tEnter,
        float tExit)
    {
        if (!InBounds(x, y))
            return false;

        ref MicroCell cell =
            ref layer.GetMicroCell(x, y);

        if (BlocksVisibility(
                ref cell,
                edificeStore))
        {
            return false;
        }

        float rayEnter =
            GetRayHeight(
                sourceHeight,
                targetHeight,
                tEnter);

        float rayExit =
            GetRayHeight(
                sourceHeight,
                targetHeight,
                tExit);

        float rayMin =
            MathF.Min(
                rayEnter,
                rayExit);

        /*
         * Касание поверхности считается
         * пересечением рельефа.
         */

        if (rayMin <= cell.Height)
            return false;

        return true;
    }

    private static float GetRayHeight(
        HeightRange sourceHeight,
        HeightRange targetHeight,
        float t)
    {
        /*
         * Используем верхнюю границу диапазона.
         *
         * Для HeightRange.Point()
         * это обычный точечный луч.
         */

        return
            sourceHeight.Max +
            (targetHeight.Max - sourceHeight.Max) *
            t;
    }

    private static bool BlocksVisibility(
        ref MicroCell cell,
        EdificeStore edificeStore)
    {
        if (cell.EdificeId == 0)
            return false;

        if (edificeStore == null)
            return false;

        if (cell.EdificeId >=
            edificeStore.Instances.Length)
        {
            return false;
        }

        ref var edifice =
            ref edificeStore.Instances[
                cell.EdificeId];

        if (edifice.ConfigId >=
            edificeStore.Configs.Length)
        {
            return false;
        }

        var config =
            edificeStore.Configs[
                edifice.ConfigId];

        if (config == null)
            return false;

        return
            config.Type == EdificeType.Wall &&
            config.CoverEffectiveness >= 1f;
    }

    private static bool InBounds(
        int x,
        int y)
    {
        return
            x >= 0 &&
            y >= 0 &&
            x <= MaxCoord &&
            y <= MaxCoord;
    }
}