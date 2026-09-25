using Core.Unit;
using System.Numerics;

namespace Units;

public static class TestUnitSpawner
{
    private const int UnitsPerType = 400;
    private const int SpawnWidth = 20;
    private const float SpawnSpacing = 1.5f;

    public static void Spawn(
        UnitSimulation simulation)
    {
        SpawnType(
            simulation,
            UnitType.Colonist,
            100f,
            120f);

        SpawnType(
            simulation,
            UnitType.Greenbob,
            300f,
            330f);

        SpawnType(
            simulation,
            UnitType.SegmentedMonster,
            220f,
            220f,
            count: 20);
    }

    private static void SpawnType(
        UnitSimulation simulation,
        UnitType type,
        float startX,
        float startY,
        int count = UnitsPerType)
    {
        for (int i = 0; i < count; i++)
        {
            int column = i % SpawnWidth;
            int row = i / SpawnWidth;

            Vector3 position = new Vector3(
                startX + column * SpawnSpacing,
                startY + row * SpawnSpacing,
                0f);

            simulation.Spawn(
                type,
                position);
        }
    }
}
