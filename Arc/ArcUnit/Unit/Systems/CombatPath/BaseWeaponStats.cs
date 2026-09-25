public struct BaseWeaponStats
{
    public string Name;
    public float BaseRange;
    public float MaxRange;
    public float BaseAccuracy;
    public float RecoilPerShot;
    public float RecoilRecovery;
    public byte BaseDamage;
    public float FireRate;
    public float BleedChance;
    public float Weight;
    public float HeatPerShot;     // Сколько тепла выделяет один выстрел
    public float HeatCoolingRate; // Скорость остывания ствола в секунду
    public float MaxHeatThreshold;// Лимит тепла, после которого оружие перегревается
}

public class Weapon
{
    public BaseWeaponStats BaseStats;
    public List<Attachment> InstalledAttachments = new List<Attachment>();

    public float currentRecoil = 0f;

    // ДИНАМИЧЕСКИЕ ПАРАМЕТРЫ СОСТОЯНИЯ ПУШКИ
    public float CurrentHeat = 0f;       // Текущий уровень перегрева ствола
    public bool IsOverheated = false;    // Флаг блокировки: true = ствол раскален, стрельба запрещена!

    public float TotalEffectiveRange
    {
        get
        {
            float range = BaseStats.BaseRange;
            foreach (var mod in InstalledAttachments) range = mod.ApplyRangeModifier(range);
            return range;
        }
    }

    public float GetCurrentSpread()
    {
        float accuracy = BaseStats.BaseAccuracy;
        foreach (var mod in InstalledAttachments) accuracy = mod.ApplyAccuracyModifier(accuracy);
        return accuracy + currentRecoil;
    }

    // ========================================================
    // ПОЛИМОРФНЫЙ РАСЧЕТ РЕЖИМА ОЧЕРЕДИ (Burst / Full Auto)
    // ========================================================
    /// <summary>
    /// Оружие само оценивает дистанцию до врага и решает, сколько пуль выпустить в очереди.
    /// </summary>
    public int GetBurstCountForDistance(float distanceInCells)
    {
        if (BaseStats.Name.Contains("СВД") || BaseStats.Name.Contains("Винтовка"))
        {
            return 1; // Снайперские винтовки всегда стреляют одиночными
        }

        if (BaseStats.Name.Contains("АК") || BaseStats.Name.Contains("Автомат"))
        {
            // В упор и на ближней дистанции (до 12 ячеек) вжимаем гашетку на максимум!
            if (distanceInCells <= 12f)
            {
                return 8; // Длинный Full-Auto зажим в упор!
            }

            // На средней дистанции (до 25 ячеек) — контролируемая тактическая отсечка
            if (distanceInCells <= 25f)
            {
                return 3; // Очередь по 3 патрона
            }

            // На предельной дистанции — аккуратные одиночные / двойные выстрелы
            return 1;
        }

        return 1; // Дефолт для укуса жука / пистолета
    }
}
