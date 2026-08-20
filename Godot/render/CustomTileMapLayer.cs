using Godot;

public partial class CustomTileMapLayer : TileMapLayer
{
	public override void _Ready()
	{
		// Очищаем и отключаем встроенную отрисовку тайлов, 
		// так как рисовать будем чистыми примитивами
		Clear(); 
	}
}
