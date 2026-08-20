using System;
using Core.Map;
using Core.Structs;
using Core.Items;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render
{
    public sealed class MapRenderSystem
    {
        private readonly RectangleShape _tileShape;

        public MapRenderSystem(float microCellPixelSize)
        {
            // Один универсальный кубик для всех типов тайлов
            _tileShape = new RectangleShape();
        }

        public void Draw(RenderWindow window, WorldMap worldMap, EdificeStore edificeStore, View cameraView, float regionPixelSize, float microCellPixelSize, float zoomLevel, bool showGrid)
        {
            int currentZ = worldMap.CurrentViewZ;
            MapLayer currentLayer = worldMap.GetLayer(currentZ);
            if (currentLayer == null) return;

            Vector2f center = cameraView.Center;
            Vector2f size = cameraView.Size;

            float screenMinX = center.X - (size.X * 0.5f);
            float screenMaxX = center.X + (size.X * 0.5f);
            float screenMinY = center.Y - (size.Y * 0.5f);
            float screenMaxY = center.Y + (size.Y * 0.5f);

            // ========================================================
            // ИСПРАВЛЕНО: КАСКАДЫ ДЛЯ СВЕРХБЫСТРОГО FLAT-РЕНДЕРА
            // ========================================================
            int lodStep = 1;
            bool drawDetails = true;

            // Упрощение включается строго на огромном расстоянии
            if (zoomLevel >= 4.5f) { lodStep = 3; drawDetails = false; }
            else if (zoomLevel >= 2.5f) { lodStep = 2; drawDetails = false; }

            // Настраиваем размер сплошной плитки. Небольшой нахлёст +0.35f 
            // нужен только для того, чтобы залить микроскопические щели сглаживания окна SFML.
            float currentTilePixelSize = microCellPixelSize * lodStep;
            float safeSize = MathF.Ceiling(currentTilePixelSize) + 0.35f;
            _tileShape.Size = new Vector2f(safeSize, safeSize);

            // Отладочная сетка по кнопке G (Очень тусклая, чисто для тестов ячеек)
            if (showGrid)
            {
                _tileShape.OutlineColor = new Color(50, 55, 70, 90);
                _tileShape.OutlineThickness = -0.5f;
            }
            else
            {
                _tileShape.OutlineThickness = 0f; // В обычном режиме обводка СТРОГО равна нулю везде!
            }

            int maxCoord = (16 * 48) - 1;
            int minCellX = Math.Max(0, (int)(screenMinX / microCellPixelSize) - lodStep);
            int maxCellX = Math.Min(maxCoord, (int)(screenMaxX / microCellPixelSize) + lodStep);
            int minCellY = Math.Max(0, (int)(screenMinY / microCellPixelSize) - lodStep);
            int maxCellY = Math.Min(maxCoord, (int)(screenMaxY / microCellPixelSize) + lodStep);

            minCellX = (minCellX / lodStep) * lodStep;
            minCellY = (minCellY / lodStep) * lodStep;

            // СВЕРХБЫСТРЫЙ ОДНОПРОХОДНЫЙ ЦИКЛ ПО МАТРИЦЕ КАРТЫ
            for (int y = minCellY; y <= maxCellY; y += lodStep)
            {
                for (int x = minCellX; x <= maxCellX; x += lodStep)
                {
                    ref MicroCell cell = ref currentLayer.GetMicroCell(x, y);

                    float px = MathF.Floor(x * microCellPixelSize);
                    float py = MathF.Floor(y * microCellPixelSize);

                    // ========================================================
                    // А. МОНОЛИТНАЯ FLAT-ОТРИСОВКА ПОСТРОЕК (СТЕНЫ И МЕШКИ)
                    // ========================================================
                    if (cell.EdificeId > 0 && edificeStore != null)
                    {
                        ushort id = cell.EdificeId;
                        if (id < edificeStore.Instances.Length)
                        {
                            var instance = edificeStore.Instances[id];
                            var config = edificeStore.Configs[instance.ConfigId];

                            if (config != null)
                            {
                                Color blockColor = new Color(85, 85, 90); // Дефолтный серый

                                if (config.Type == EdificeType.Wall)
                                {
                                    // Бетонные стены кибиток: Чистый матово-белый flat-монолит!
                                    // Мешки с песком (баррикады): Однотонный темно-серый цвет заграждений
                                    blockColor = config.CoverEffectiveness >= 1.0f
                                        ? new Color(225, 225, 230)
                                        : new Color(120, 125, 130);
                                }
                                else if (config.Type == EdificeType.Generator)
                                {
                                    blockColor = new Color(65, 65, 70); // Индустриальный генератор
                                }

                                // Принудительно выключаем рамки, заливаем чистым цветом
                                _tileShape.FillColor = blockColor;
                                _tileShape.Position = new Vector2f(px, py);
                                window.Draw(_tileShape);

                                continue; // Стену залили, пол под ней не нужен
                            }
                        }
                    }

                    // ========================================================
                    // Б. БЕСШОВНАЯ FLAT-ОТРИСОВКА ПОЛОВ
                    // ========================================================
                    if (cell.FloorId == 2)
                    {
                        _tileShape.FillColor = new Color(42, 44, 46); // Графитовый асфальт проспекта
                    }
                    else if (cell.FloorId == 1)
                    {
                        // Пол базы: глубокий темный сине-фиолетовый настил
                        _tileShape.FillColor = new Color(38, 44, 65);
                    }
                    else
                    {
                        _tileShape.FillColor = new Color(20, 21, 24); // Глубокий фоновый угольный монолит пустоты
                    }

                    _tileShape.Position = new Vector2f(px, py);
                    window.Draw(_tileShape);

                    // ========================================================
                    // В. ТАКТИЧЕСКАЯ КРОВЬ (БИТ 1)
                    // ========================================================
                    if (drawDetails && (cell.Flags & 0x0002) != 0)
                    {
                        RectangleShape bloodSplatter = new RectangleShape(new Vector2f(microCellPixelSize + 0.1f, microCellPixelSize + 0.1f));
                        // Сочные контрастные flat-пятна крови поверх темного монолита пола
                        bloodSplatter.FillColor = new Color(185, 15, 25, 220);
                        bloodSplatter.Position = new Vector2f(px, py);
                        window.Draw(bloodSplatter);
                    }
                }
            }
        }
    }
}
