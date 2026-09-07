using System.Collections.Generic;
using System.Text;
using Core.Items;
using Core.Structs;

namespace Core.Map;

public static class TileMetadataSystem
{
    private static readonly Dictionary<ushort, string> _floorDescriptions =
        new Dictionary<ushort, string>();

    private static readonly Dictionary<ushort, string> _flagDescriptions =
        new Dictionary<ushort, string>();

    static TileMetadataSystem()
    {
        _floorDescriptions[0] =
            "Дикая пустошь (Оливковый грунт). Проходимость: 100%. Скорость движения: Нормальная.";

        _floorDescriptions[1] =
            "Высокотехнологичный настил базы (Фиолетовая плитка). Проходимость: 100%. Скорость движения: Высокая.";

        _floorDescriptions[2] =
            "Асфальт проспекта (Городское покрытие). Проходимость: 100%. Скорость движения: Максимальная.";

        _flagDescriptions[0x0001] =
            "Биологическая угроза: Активные следы ДНК";

        _flagDescriptions[0x0002] =
            "Запекшаяся кровь существа (Требуется влажная уборка территории)";

        _flagDescriptions[0x0004] =
            "Сетка проходимости: Доступно для шага юнитов";

        _flagDescriptions[0x0008] =
            "Электронный шлюз (Дверной проем / Активная зона двери)";
    }

    public static string InspectMicroCell(
        ref MicroCell cell,
        int mx,
        int my,
        EdificeStore edificeStore)
    {
        StringBuilder sb =
            new StringBuilder();

        sb.AppendLine(
            "=== ИНСПЕКТОР ТАКТИЧЕСКОГО ТАЙЛА ===");

        sb.AppendLine(
            $"Координаты ячейки: ({mx}, {my})");

        sb.AppendLine(
            $"Высота рельефа: {cell.Height}");

        if (_floorDescriptions.TryGetValue(
                cell.FloorId,
                out string floorText))
        {
            sb.AppendLine(
                $"Покрытие: {floorText}");
        }
        else
        {
            sb.AppendLine(
                $"Покрытие: Неизвестный тип пола (ID: {cell.FloorId})");
        }

        if (cell.EdificeId > 0 &&
            edificeStore != null)
        {
            ushort id = cell.EdificeId;

            if (id < edificeStore.Instances.Length)
            {
                var instance =
                    edificeStore.Instances[id];

                if (instance.ConfigId <
                    edificeStore.Configs.Length)
                {
                    var config =
                        edificeStore.Configs[
                            instance.ConfigId];

                    if (config != null)
                    {
                        sb.AppendLine(
                            $"Объект: {config.Name} (ID Экземпляра: {id})");

                        sb.AppendLine(
                            $"Прочность конструкции: {instance.HitPoints} / {config.MaxHitPoints} HP");

                        sb.AppendLine(
                            $"Эффективность укрытия: {config.CoverEffectiveness * 100f}% (Снижение шанса попадания)");

                        sb.AppendLine(
                            $"Тип архитектуры: {config.Type}");
                    }
                    else
                    {
                        sb.AppendLine(
                            $"Объект: Сломанный чертеж здания (ConfigId: {instance.ConfigId})");
                    }
                }
                else
                {
                    sb.AppendLine(
                        $"Объект: Некорректный ConfigId ({instance.ConfigId})");
                }
            }
            else
            {
                sb.AppendLine(
                    $"Объект: Некорректный EdificeId ({id})");
            }
        }
        else
        {
            sb.AppendLine(
                "Объект: Пустое пространство (Чистый горизонт)");
        }

        if (cell.RoofType == 1)
        {
            sb.AppendLine(
                "Крыша: Тонкий промышленный настил");
        }
        else if (cell.RoofType == 2)
        {
            sb.AppendLine(
                "Крыша: Толстая монолитная скала");
        }
        else
        {
            sb.AppendLine(
                "Крыша: Открытое небо");
        }

        sb.AppendLine(
            "Активные тактические маркеры:");

        bool hasFlags = false;

        foreach (var kvp in _flagDescriptions)
        {
            if ((cell.Flags & kvp.Key) == 0)
                continue;

            sb.AppendLine(
                $"  [+] {kvp.Value}");

            hasFlags = true;
        }

        if (!hasFlags)
        {
            sb.AppendLine(
                "  [-] Нет активных флагов окружения");
        }

        return sb.ToString();
    }

    public static string InspectWeapon(
        Weapon weapon)
    {
        if (weapon == null)
        {
            return
                "Оружие: Безоружный (Атака кулаками / Ближний укус)";
        }

        StringBuilder sb =
            new StringBuilder();

        var stats = weapon.BaseStats;

        sb.AppendLine(
            $"=== ОРУЖЕЙНЫЙ ПАСПОРТ: {stats.Name} ===");

        sb.AppendLine(
            $"Базовый урон пули: {stats.BaseDamage} HP | Масса ствола: {stats.Weight} кг");

        sb.AppendLine(
            $"Шанс вызвать кровотечение: {stats.BleedChance * 100f}%");

        sb.AppendLine(
            $"Идеальная дальность: {weapon.TotalEffectiveRange:F1} тайлов (Макс: {stats.MaxRange:F1})");

        sb.AppendLine(
            $"Текущая точность: {(1f - weapon.GetCurrentSpread()) * 100f:F1}% (Падение от зажима: {weapon.currentRecoil:F3})");

        sb.AppendLine(
            "Термодинамика:");

        sb.AppendLine(
            $"  Текущий нагрев: {weapon.CurrentHeat:F1} / {stats.MaxHeatThreshold:F1}°C");

        sb.AppendLine(
            $"  Состояние: {(weapon.IsOverheated ? "⚠️ КЛИН! (Раскалено докрасна)" : "♻️ Стабильное (Готов к зажиму)")}");

        if (weapon.InstalledAttachments.Count > 0)
        {
            sb.AppendLine(
                "Установленные модули (Обвесы):");

            foreach (var attachment in weapon.InstalledAttachments)
            {
                sb.AppendLine(
                    $"  - {attachment.Name}");
            }
        }

        return sb.ToString();
    }
}
