// Path: Core/Items/EdificeStore.cs
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

        /// <summary>
        /// Внутренний метод для динамического увеличения емкости массива инстансов построек
        /// </summary>
        private void EnsureInstancesCapacity(int requiredId)
        {
            if (requiredId >= Instances.Length)
            {
                int newCapacity = Math.Max(Instances.Length * 2, requiredId + 4);
                EdificeInstance[] newArray = new EdificeInstance[newCapacity];
                Array.Copy(Instances, 0, newArray, 0, Instances.Length);
                Instances = newArray;
            }
        }

        /// <summary>
        /// Внутренний метод для динамического увеличения емкости массива конфигураций
        /// </summary>
        private void EnsureConfigsCapacity(int requiredConfigId)
        {
            if (requiredConfigId >= Configs.Length)
            {
                int newCapacity = Math.Max(Configs.Length * 2, requiredConfigId + 4);
                EdificeConfig[] newArray = new EdificeConfig[newCapacity];
                Array.Copy(Configs, 0, newArray, 0, Configs.Length);
                Configs = newArray;
            }
        }

        public ushort Build(WorldMap map, ushort configId, int startX, int startY, int z)
        {
            // Динамически расширяем массив конфигураций, если переданный ID выходит за текущие границы
            EnsureConfigsCapacity(configId);

            var config = Configs[configId];
            ushort instanceId = (ushort)Count++;

            // Динамически расширяем массив инстансов перед записью нового элемента
            EnsureInstancesCapacity(instanceId);

            Instances[instanceId] = new EdificeInstance
            {
                ConfigId = configId,
                HitPoints = config.MaxHitPoints,
                OriginX = startX,
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
                    ref MicroCell cell = ref layer.GetMicroCell(startX + dx, startY + dy);
                    cell.EdificeId = instanceId;

                    // ТАКТИЧЕСКОЕ УПРАВЛЕНИЕ ПРОХОДИМОСТЬЮ (RimWorld-стиль)
                    if (config.Type == EdificeType.Wall && config.CoverEffectiveness >= 1.0f || config.Type == EdificeType.Door)
                    {
                        // Сбрасываем Бит 2 (0x0004) - ячейка становится абсолютно твердой преградой
                        cell.Flags = (ushort)(cell.Flags & ~0x0004);

                        if (config.Type == EdificeType.Door)
                        {
                            // Вводим Бит 3 (0x0008) - пометка "Это шлюз/дверь" для ИИ
                            cell.Flags |= 0x0008;
                        }
                    }
                    else
                    {
                        // Мешки с песком, баррикады, верстаки и генераторы проходимы (вводим Бит 2 в 1)
                        cell.Flags |= 0x0004;
                    }
                }
            }

            if (config.Type != EdificeType.Turret)
            {
                BuildRoof(map, startX, startY, config.WidthCells, config.HeightCells, z, 1);
            }

            return instanceId;
        }

        public void SetDoorState(WorldMap map, ushort instanceId, bool open)
        {
            // Проверка на выход за границы динамического массива во избежание IndexOutOfRangeException
            if (instanceId >= Instances.Length) return;

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

                    // ИСПРАВЛЕНИЕ ОШИБКИ USHORT (~2)
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
