using Core.Map;
using Core.Structs;
using System;

namespace Core.Unit
{
    public static class MovementRules
    {
        public const byte MaxHeightStep = 3;

        private const float DiagonalDistanceFactor =
            0.70710678f;

        public static bool CanStep(
            WorldMap map,
            int srcX,
            int srcY,
            int dstX,
            int dstY,
            int z)
        {
            if (map == null)
                return false;

            MapLayer layer =
                map.GetLayer(z);

            if (layer == null)
                return false;

            return CanStep(
                layer,
                srcX,
                srcY,
                dstX,
                dstY,
                z);
        }

        public static bool CanStep(
            MapLayer layer,
            int srcX,
            int srcY,
            int dstX,
            int dstY,
            int z)
        {
            if (layer == null)
                return false;

            int dx =
                Math.Abs(dstX - srcX);

            int dy =
                Math.Abs(dstY - srcY);

            if (dx > 1 ||
                dy > 1 ||
                (dx == 0 && dy == 0))
            {
                return false;
            }

            if (!Inside(
                layer,
                srcX,
                srcY) ||
                !Inside(
                    layer,
                    dstX,
                    dstY))
            {
                return false;
            }

            ref MicroCell src =
                ref layer.GetMicroCell(
                    srcX,
                    srcY);

            ref MicroCell dst =
                ref layer.GetMicroCell(
                    dstX,
                    dstY);

            if (dst.EdificeId > 0)
                return false;

            int heightDifference =
                Math.Abs(
                    dst.Height -
                    src.Height);

            if (heightDifference >
                MaxHeightStep)
            {
                return false;
            }

            if (dx == 1 &&
                dy == 1)
            {
                if (!CanUseSideCell(
                    layer,
                    srcX,
                    srcY,
                    dstX,
                    srcY))
                {
                    return false;
                }

                if (!CanUseSideCell(
                    layer,
                    srcX,
                    srcY,
                    srcX,
                    dstY))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool CanUseSideCell(
            MapLayer layer,
            int srcX,
            int srcY,
            int cellX,
            int cellY)
        {
            if (!Inside(
                layer,
                cellX,
                cellY))
            {
                return false;
            }

            ref MicroCell src =
                ref layer.GetMicroCell(
                    srcX,
                    srcY);

            ref MicroCell cell =
                ref layer.GetMicroCell(
                    cellX,
                    cellY);

            if (cell.EdificeId > 0)
                return false;

            return Math.Abs(
                cell.Height -
                src.Height) <=
                MaxHeightStep;
        }

        public static float GetHeightSpeedMultiplier(
            WorldMap map,
            int srcX,
            int srcY,
            int dstX,
            int dstY,
            int z)
        {
            if (map == null)
                return 0f;

            int dx =
                Math.Abs(dstX - srcX);

            int dy =
                Math.Abs(dstY - srcY);

            if (dx > 1 ||
                dy > 1 ||
                (dx == 0 && dy == 0))
            {
                return 0f;
            }

            if (!CanStep(
                map,
                srcX,
                srcY,
                dstX,
                dstY,
                z))
            {
                return 0f;
            }

            MapLayer layer =
                map.GetLayer(z);

            if (layer == null)
                return 0f;

            ref MicroCell src =
                ref layer.GetMicroCell(
                    srcX,
                    srcY);

            ref MicroCell dst =
                ref layer.GetMicroCell(
                    dstX,
                    dstY);

            int difference =
                Math.Abs(
                    dst.Height -
                    src.Height);

            float multiplier =
                difference switch
                {
                    0 => 1.00f,
                    1 => 0.85f,
                    2 => 0.65f,
                    3 => 0.40f,
                    _ => 0.00f
                };

            if (dx == 1 &&
                dy == 1)
            {
                multiplier *=
                    DiagonalDistanceFactor;
            }

            return multiplier;
        }

        private static bool Inside(
            MapLayer layer,
            int x,
            int y)
        {
            int width =
                MapLayer.WidthInRegions *
                MapRegion.MicroSize;

            int height =
                MapLayer.HeightInRegions *
                MapRegion.MicroSize;

            return x >= 0 &&
                   y >= 0 &&
                   x < width &&
                   y < height;
        }
    }
}