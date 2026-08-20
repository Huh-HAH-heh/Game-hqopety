using System;
using Core.Map;
using Core.Structs;

namespace Core.Items
{
    public sealed class EdificeStore
    {
        public int Count = 1;
        public EdificeInstance[] Instances;
        public EdificeConfig[] Configs;

        public EdificeStore(int maxEdifices, int maxConfigs)
        {
            Instances = new EdificeInstance[maxEdifices];
            Configs = new EdificeConfig[maxConfigs];
        }

        public ushort Build(WorldMap map, ushort configId, int startMx, int startY, int z)
        {
            var config = Configs[configId];
            ushort instanceId = (ushort)Count++;

            Instances[instanceId] = new EdificeInstance
            {
                ConfigId = configId,
                HitPoints = config.MaxHitPoints,
                OriginX = startMx,
                OriginY = startY,
                ZLevel = z,
                InternalTimer = 0f,
                IsDoorOpened = false
            };

            MapLayer layer = map.GetLayer(z);
            if (layer == null) return 0;

            for (int dy = 0; dy < config.HeightCells; dy++)
            {
                for (int dx = 0; dx < config.WidthCells; dx++)
                {
                    ref MicroCell cell = ref layer.GetMicroCell(startMx + dx, startY + dy);
                    cell.EdificeId = instanceId;

                    // --- ТАКТИЧЕСКОЕ УПРАВЛЕНИЕ ПРОХОДИМОСТЬЮ (RimWorld-стиль) ---
                    // Только глухая бетонная/каменная стена здания или дверь полностью перекрывают путь!
                    // Если это мешки с песком (у них CoverEffectiveness равен 0.65f, а не 1.0f), 
                    // мы НЕ затираем проходимость, чтобы солдаты могли физически залезть НА мешки и укрыться!
                    if ((config.Type == EdificeType.Wall && config.CoverEffectiveness >= 1.0f) || config.Type == EdificeType.Door)
                    {
                        // Сбрасываем Бит 2 (0x0004) — ячейка становится абсолютно твердой препятствием
                        cell.Flags = (ushort)(cell.Flags & ~0x0004);

                        if (config.Type == EdificeType.Door)
                        {
                            // Взводим Бит 3 (0x0008) — пометка "Это шлюз/дверь" для ИИ
                            cell.Flags |= 0x0008;
                        }
                    }
                    else
                    {
                        // Мешки с песком, баррикады, верстаки и генераторы проходимы (взводим Бит 2 в 1)
                        cell.Flags |= 0x0004;
                    }
                }
            }

            if (config.Type != EdificeType.Turret)
            {
                BuildRoof(map, startMx, startY, config.WidthCells, config.HeightCells, z, 1);
            }

            return instanceId;
        }


        public void SetDoorState(WorldMap map, ushort instanceId, bool open)
        {
            ref var instance = ref Instances[instanceId];
            var config = Configs[instance.ConfigId];

            if (config.Type != EdificeType.Door || instance.IsDoorOpened == open) return;

            instance.IsDoorOpened = open;
            MapLayer layer = map.GetLayer(instance.ZLevel);
            if (layer == null) return;

            for (int dy = 0; dy < config.HeightCells; dy++)
            {
                for (int dx = 0; dx < config.WidthCells; dx++)
                {
                    ref MicroCell cell = ref layer.GetMicroCell(instance.OriginX + dx, instance.OriginY + dy);

                    // --- ИСПРАВЛЕНИЕ ОШИБКИ USHORT (-2) ---
                    if (open)
                    {
                        cell.Flags |= 0x0004; // Открылась: проходимо
                    }
                    else
                    {
                        // Закрылась: сбрасываем Бит 2 через безопасное приведение типов
                        cell.Flags = (ushort)(cell.Flags & ~0x0004);
                    }
                }
            }
        }


        public void BuildRoof(WorldMap map, int sx, int sy, int w, int h, int z, byte roofType)
        {
            MapLayer layer = map.GetLayer(z);
            if (layer == null) return;

            for (int dy = 0; dy < h; dy++)
            {
                for (int dx = 0; dx < w; dx++)
                {
                    layer.GetMicroCell(sx + dx, sy + dy).RoofType = roofType;
                }
            }
        }
    }
}
