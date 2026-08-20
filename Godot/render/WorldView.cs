using Core; // Наш проект Core с логикой симуляции
using Godot;
using System;
using Core.Map;

public partial class WorldView : Node2D
{
	private MapLayer _logicLayer;
	
	// Физический размер одного векторного квадрата в пикселях на экране
	private const int TileSize = 16; 

	public override void _Ready()
	{
		// 1. Инициализируем карту 250х250 из проекта Core
		_logicLayer = new MapLayer(0);

		// 2. Генерируем тестовый шахматный узор в логике памяти
		for (int i = 0; i < _logicLayer.Tiles.Length; i++)
		{
			var (x, y) = _logicLayer.GetCoords(i);

			// Если сумма координат четная — тип пола 1 (зеленый), если нечетная — тип 2 (темно-зеленый)
			if ((x + y) % 2 == 0)
			{
				_logicLayer.Tiles[i].SurfaceType = 1;
			}
			else
			{
				_logicLayer.Tiles[i].SurfaceType = 2;
			}
		}

		// 3. Отправляем команду движку: "Перерисуй экран и вызови метод _Draw()"
		QueueRedraw();

		// 4. Настраиваем камеру, чтобы она смотрела точно на нашу векторную карту
		var camera = GetNode<Camera2D>("Camera2D");
		if (camera != null)
		{
			// Отвязываем камеру от локального нуля и жестко ставим в центр сетки
			camera.TopLevel = true;
			camera.GlobalPosition = new Vector2(2000, 2000); // 250 клеток * 16 пикселей = 4000. Центр на 2000.
			camera.MakeCurrent();
			
			// Немного отдаляем камеру (0.5), чтобы влез больший кусок карты
			camera.Zoom = new Vector2(0.5f, 0.5f); 
		}
	}

	// Встроенный системный метод Godot для отрисовки 2D-примитивов (вызывается автоматически после QueueRedraw)
	public override void _Draw()
	{
		// Защита от вылета: если логика карты еще не создана, ничего не рисуем
		if (_logicLayer == null || _logicLayer.Tiles == null) return;

		// Бежим по нашему одномерному массиву из Core
		for (int i = 0; i < _logicLayer.Tiles.Length; i++)
		{
			var (x, y) = _logicLayer.GetCoords(i);
			byte tileType = _logicLayer.Tiles[i].SurfaceType;

			// Выбираем цвет в зависимости от типа тайла
			Color tileColor = tileType == 1
				? new Color(0.2f, 0.6f, 0.2f)    // Ярко-зеленая трава
				: new Color(0.15f, 0.5f, 0.15f); // Темно-зеленая трава

			// Строим векторный прямоугольник (X-позиция, Y-позиция, Ширина, Высота)
			Rect2 rect = new Rect2(x * TileSize, y * TileSize, TileSize, TileSize);

			// Рисуем заполненный цветной квадрат прямо на экране
			DrawRect(rect, tileColor);
		}

		GD.Print("Чистый векторный рендер карты 250х250 завершен успешно!");
	}
}
