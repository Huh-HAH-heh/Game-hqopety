using Core.Items;
using Core.Map;
using Core.Unit;
using RimClone.Render;
using System;
using Units;
using World;

namespace RimClone.App
{
    public static class GameBootstrap
    {
        public static void Run()
        {
            Console.WriteLine("Запуск многоэтажной симуляции RimClone...");

            // 1. Создаём мир (от -3 до 3 этажа)
            WorldMap worldMap = new WorldMap(
                minZ: -3,
                maxZ: 3
            );

            // ИСПРАВЛЕНО: Создаем базу построек до генерации мира, 
            // чтобы передать её в качестве аргумента в WorldGenerator!
            EdificeStore edificeStore = new EdificeStore(maxEdifices: 2000, maxConfigs: 50);

            // 2. Генерируем тестовый мир (городскую улицу-полигон на 1-м этаже)
            // ИСПРАВЛЕНО: Теперь передаем edificeStore вторым параметром!
            WorldGenerator.Generate(worldMap, edificeStore);

            // Используем безопасный конструктор пространственной сетки.
            UnitSpatialGrid spatialGrid = new UnitSpatialGrid(
                maxUnits: 100,
                regionsX: 16,
                regionsY: 16,
                subDivision: 3
            );

            // 3. Создаём юнитов и отряды на 100 существ
            UnitStore unitStore = new UnitStore(maxUnits: 100);
            

            // Наполняем мир: спавним 5 синих людей-стрелков и 3 красных жуков-рейдеров
            TestUnitSpawner.Spawn(
                unitStore,
                
                spatialGrid
            );

            // 4. Запускаем графический движок
            VectorRenderer renderer = new VectorRenderer();

            // Прокидываем всё в рендерер, запускается 60 FPS цикл симуляции
            renderer.InitializeAndRun(
                worldMap,
                unitStore,
             
                spatialGrid,
                edificeStore
            );
        }

    }
}
