using System;
using System.Collections.Generic;
using System.IO;
using Core.Items;
using Core.Unit;
using SFML.Graphics;
using SFML.System;
using SFML.Window;

namespace RimClone.Render;

public sealed class UnitCardOverlay : IDisposable
{
    public const float PanelWidth = 430f;
    private const float Width = PanelWidth;
    private const float MaximumHeight = 760f;
    private const float RowHeight = 17f;
    private const int MaxVisibleLines = 42;

    private readonly RectangleShape _panel = new(new Vector2f(Width, MaximumHeight))
    {
        FillColor = new Color(17, 22, 30, 250),
        OutlineColor = new Color(76, 94, 116, 255),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _header = new(new Vector2f(Width - 2f, 38f))
    {
        FillColor = new Color(31, 40, 52, 255)
    };

    private readonly RectangleShape _accent = new(new Vector2f(3f, MaximumHeight - 2f))
    {
        FillColor = new Color(112, 164, 218, 245)
    };

    private readonly RectangleShape _closeButton = new(new Vector2f(26f, 26f))
    {
        FillColor = new Color(48, 58, 73, 255),
        OutlineColor = new Color(89, 105, 126, 220),
        OutlineThickness = 1f
    };

    private readonly Font? _font;
    private readonly Text? _title;
    private readonly Text? _closeLabel;
    private readonly Text? _footer;
    private readonly Text?[] _lineTexts = new Text?[MaxVisibleLines];

    private int _scrollOffset;
    private int _visibleLines;
    private int _lineCount;
    private float _positionX;
    private float _positionY;
    private float _dragOffsetX;
    private float _dragOffsetY;
    private bool _hasPosition;
    private bool _dragging;

    public bool IsOpen { get; private set; }

    public UnitCardOverlay()
    {
        _font = LoadFont();

        if (_font == null)
            return;

        _title = new Text(_font, "КАРТОЧКА ЮНИТА", 14)
        {
            FillColor = new Color(239, 243, 249),
            Style = Text.Styles.Bold
        };

        _closeLabel = new Text(_font, "×", 18)
        {
            FillColor = new Color(225, 232, 241)
        };

        _footer = new Text(_font, "Заголовок — перемещение   •   Колесо — прокрутка", 10)
        {
            FillColor = new Color(143, 155, 171)
        };

        for (int i = 0; i < _lineTexts.Length; i++)
        {
            _lineTexts[i] = new Text(_font, "", 11)
            {
                FillColor = new Color(205, 214, 225)
            };
        }
    }

    public void Open(float x, float y)
    {
        IsOpen = true;
        _scrollOffset = 0;
        _positionX = x;
        _positionY = y;
        _hasPosition = true;
        _dragging = false;
    }

    public void Close()
    {
        IsOpen = false;
        _dragging = false;
    }

    public bool Contains(Vector2i position) =>
        IsOpen && _panel.GetGlobalBounds().Contains(position);

    public bool HandleMousePressed(
        Vector2i position,
        uint windowWidth,
        uint windowHeight)
    {
        if (!Contains(position))
            return false;

        if (_closeButton.GetGlobalBounds().Contains(position))
        {
            Close();
            return true;
        }

        if (_header.GetGlobalBounds().Contains(position))
        {
            _dragging = true;
            _dragOffsetX = position.X - _panel.Position.X;
            _dragOffsetY = position.Y - _panel.Position.Y;
        }

        return true;
    }

    public bool HandleMouseMoved(
        Vector2i position,
        uint windowWidth,
        uint windowHeight)
    {
        if (!_dragging)
            return false;

        float panelHeight = Math.Clamp(windowHeight - 24f, 300f, MaximumHeight);
        _positionX = Math.Clamp(
            position.X - _dragOffsetX,
            8f,
            Math.Max(8f, windowWidth - Width - 8f));
        _positionY = Math.Clamp(
            position.Y - _dragOffsetY,
            8f,
            Math.Max(8f, windowHeight - panelHeight - 8f));
        Layout(windowWidth, windowHeight);
        return true;
    }

    public bool HandleMouseReleased()
    {
        if (!_dragging)
            return false;

        _dragging = false;
        return true;
    }

    public bool HandleWheel(Vector2i position, float delta)
    {
        if (!IsOpen || !_panel.GetGlobalBounds().Contains(position))
            return false;

        _scrollOffset = Math.Max(0, _scrollOffset + (delta > 0f ? -3 : 3));
        return true;
    }

    public void Draw(
        RenderWindow window,
        RenderUnitState unit,
        UnitCardDetailsSnapshot? details,
        uint windowWidth,
        uint windowHeight)
    {
        if (!IsOpen)
            return;

        Layout(windowWidth, windowHeight);
        List<CardLine> lines = BuildLines(unit, details);
        _lineCount = lines.Count;
        _visibleLines = Math.Min(
            MaxVisibleLines,
            Math.Max(1, (int)((_panel.Size.Y - 79f) / RowHeight)));
        _scrollOffset = Math.Clamp(
            _scrollOffset,
            0,
            Math.Max(0, _lineCount - _visibleLines));

        window.Draw(_panel);
        window.Draw(_header);
        window.Draw(_accent);
        window.Draw(_closeButton);

        if (_title != null)
        {
            _title.DisplayedString = $"ЮНИТ {unit.Index}:{unit.Generation}  •  {unit.Type}";
            window.Draw(_title);
        }

        if (_closeLabel != null)
            window.Draw(_closeLabel);

        for (int i = 0; i < _lineTexts.Length; i++)
        {
            Text? text = _lineTexts[i];
            if (text == null)
                continue;

            int lineIndex = _scrollOffset + i;
            if (i >= _visibleLines || lineIndex >= lines.Count)
            {
                text.DisplayedString = "";
                continue;
            }

            CardLine line = lines[lineIndex];
            string displayed = line.Text.Length > 64
                ? line.Text.Substring(0, 63) + "…"
                : line.Text;

            if (text.DisplayedString != displayed)
                text.DisplayedString = displayed;

            text.FillColor = line.IsHeader
                ? new Color(135, 183, 231)
                : new Color(205, 214, 225);

            text.Position = new Vector2f(
                _panel.Position.X + 14f,
                _panel.Position.Y + 48f + i * RowHeight);
            window.Draw(text);
        }

        if (_footer != null)
        {
            _footer.Position = new Vector2f(
                _panel.Position.X + 12f,
                _panel.Position.Y + _panel.Size.Y - 22f);
            _footer.DisplayedString = _lineCount > _visibleLines
                ? $"Строки {_scrollOffset + 1}–{Math.Min(_scrollOffset + _visibleLines, _lineCount)} из {_lineCount}   •   Колесо — прокрутка"
                : "Заголовок — перемещение   •   Колесо — прокрутка";
            window.Draw(_footer);
        }
    }

    private void Layout(uint windowWidth, uint windowHeight)
    {
        float height = Math.Clamp(windowHeight - 24f, 300f, MaximumHeight);
        if (!_hasPosition)
        {
            _positionX = Math.Max(8f, windowWidth - Width - 14f);
            _positionY = 12f;
            _hasPosition = true;
        }

        float x = Math.Clamp(
            _positionX,
            8f,
            Math.Max(8f, windowWidth - Width - 8f));
        float y = Math.Clamp(
            _positionY,
            8f,
            Math.Max(8f, windowHeight - height - 8f));
        _positionX = x;
        _positionY = y;

        _panel.Position = new Vector2f(x, y);
        _panel.Size = new Vector2f(Width, height);
        _header.Position = new Vector2f(x + 1f, y + 1f);
        _accent.Position = new Vector2f(x + 1f, y + 1f);
        _accent.Size = new Vector2f(3f, height - 2f);
        _closeButton.Position = new Vector2f(x + Width - 34f, y + 7f);

        if (_title != null)
            _title.Position = new Vector2f(x + 12f, y + 10f);

        if (_closeLabel != null)
            _closeLabel.Position = new Vector2f(x + Width - 27f, y + 4f);
    }

    private static List<CardLine> BuildLines(
        RenderUnitState unit,
        UnitCardDetailsSnapshot? details)
    {
        List<CardLine> lines = new(48);

        void Heading(string value) => lines.Add(new CardLine(value, true));
        void Value(string label, string value) => lines.Add(new CardLine($"  {label}: {value}", false));

        Heading("СОСТОЯНИЕ");
        Value("Тип", unit.Type.ToString());
        Value("ID", $"{unit.Index}:{unit.Generation}");
        Value("Фракция", unit.FactionTag switch
        {
            1 => "Синие (1)",
            2 => "Красные (2)",
            _ => unit.FactionTag.ToString()
        });
        Value("Состояние ИИ", unit.AiState.ToString());
        Value("Поза", unit.Posture.ToString());
        Value("Здоровье", $"{unit.HitPoints:0.0} / {unit.MaxHitPoints:0.0} HP ({unit.HealthRatio * 100f:0.0}%)");
        Value("Подавление", $"{unit.Suppression:0.00} — {unit.SuppressionState}");
        Value("Одежда / броня", unit.HasApparel ? (unit.HasPoweredArmor ? "Есть, силовая" : "Есть") : "Нет");

        Heading("ПОЛОЖЕНИЕ И ДВИЖЕНИЕ");
        Value("Координаты", FormatVector(unit.Position));
        Value("Скорость сейчас", FormatVector(unit.Velocity));
        Value("Скорость движения", $"{unit.MoveSpeed:0.00} клеток/с");
        Value("Размер Ш × Д × В", $"{unit.Width:0.00} × {unit.Length:0.00} × {unit.Height:0.00}");
        Value("Радиус", $"{unit.Radius:0.00}");
        Value("Направление тела", $"{Angle(unit.BodyNormal):0.0}°");
        Value("Направление головы", $"{Angle(unit.HeadNormal):0.0}°");
        Value("ID локации", unit.LocationId.ToString());

        Heading("ОБНАРУЖЕНИЕ И ЦЕЛИ");
        Value("Дальность обзора", $"{unit.ViewRange:0.0} клеток");
        Value("Угол обзора", $"{unit.FieldOfView:0.0}°");
        Value("Цель ИИ", unit.HasTarget ? FormatVector(unit.TargetPosition) : "Нет");
        Value("Точка движения", unit.HasGoal ? FormatVector(unit.Goal) : "Нет");

        AddWeapon(lines, "ОСНОВНОЕ ОРУЖИЕ", unit.PrimaryWeapon);
        AddWeapon(lines, "ВТОРОЕ ОРУЖИЕ", unit.SecondaryWeapon);
        AddWeapon(lines, "РУЧНОЕ / БЛИЖНИЙ БОЙ", unit.MeleeWeapon);
        AddWeapon(lines, "СЛОТ СНАРЯЖЕНИЯ", unit.UtilityWeapon);

        if (details != null &&
            details.Unit.Index == unit.Index &&
            details.Unit.Generation == unit.Generation)
        {
            Heading($"АНАТОМИЯ И ЗДОРОВЬЕ ({details.HealthParts.Length})");
            if (details.HealthParts.Length == 0)
            {
                Value("Части тела", "Нет данных");
            }
            else
            {
                for (int i = 0; i < details.HealthParts.Length; i++)
                {
                    RenderHealthPart part = details.HealthParts[i];
                    Value($"{part.Part} [{part.Kind}]",
                        $"{part.HitPoints:0.0}/{part.MaxHitPoints:0.0} HP; кровотечение {part.BleedRate:0.000}");
                }
            }

            RenderMetabolismState metabolism = details.Metabolism;
            Heading("МЕТАБОЛИЗМ / СОСТОЯНИЕ ОРГАНИЗМА");
            Value("Состояние", metabolism.Condition.ToString());
            Value("Энергия", $"{metabolism.EnergyKcal:0}/{metabolism.MaxEnergyKcal:0} ккал " +
                $"({(metabolism.MaxEnergyKcal > 0f ? metabolism.EnergyKcal / metabolism.MaxEnergyKcal : 0f):P0})");
            Value("Вода", $"{metabolism.WaterLiters:0.00}/{metabolism.MaxWaterLiters:0.00} л");
            Value("Резервы Б/У/Ж", $"{metabolism.ProteinReserveGrams:0}/" +
                $"{metabolism.CarbohydrateReserveGrams:0}/{metabolism.FatReserveGrams:0} г");
            Value("Витамины и минералы", $"{metabolism.MicronutrientReserve:0}%");
            Value("Пищеварение, ккал", $"{metabolism.StomachKcal:0} в желудке / {metabolism.GutKcal:0} в кишечнике");
            Value("Вода в пищеварении", $"{metabolism.StomachWaterLiters:0.00} / {metabolism.GutWaterLiters:0.00} л");
            Value("Масса / мышцы / жир", $"{metabolism.BodyMassKg:0.0} / " +
                $"{metabolism.MuscleMassKg:0.0} / {metabolism.BodyFatKg:0.0} кг");
            Value("Кровь", $"{metabolism.BloodLiters:0.00}/{metabolism.MaxBloodLiters:0.00} л");
            Value("Усталость / стресс", $"{metabolism.Fatigue:0}% / {metabolism.Stress:0}%");
            Value("Сила", $"{metabolism.Strength} → {metabolism.EffectiveStrength:0.0}");
            Value("Выносливость", $"{metabolism.Constitution} → {metabolism.EffectiveConstitution:0.0}");
            Value("Ловкость", $"{metabolism.Dexterity} → {metabolism.EffectiveDexterity:0.0}");
            Value("Интеллект", metabolism.Intelligence.ToString());
            Value("Модификатор скорости", $"{metabolism.MovementMultiplier:P0}");

            Heading($"ИНВЕНТАРЬ ({details.Inventory.Length})");
            if (details.Inventory.Length == 0)
            {
                Value("Предметы", "Нет");
            }
            else
            {
                for (int i = 0; i < details.Inventory.Length; i++)
                {
                    RenderInventoryItem item = details.Inventory[i];
                    string suffix = item.Equipped ? " [надето/установлено]" : "";
                    string durability = item.MaxDurability > 0f
                        ? $"; прочность {item.Durability:0.0}/{item.MaxDurability:0.0}"
                        : "";
                    Value(item.Config.Name ?? "Безымянный предмет",
                        $"{item.Config.Weight:0.0} кг{durability}{suffix}");

                    if (item.Config is ArmorConfig armor)
                    {
                        Value("  Защита " + armor.Name,
                            $"поглощение {armor.DamageAbsorption:P0}; рейтинг {armor.ArmorRating:0.0}; " +
                            $"энергия {armor.EnergyLoss:0.0}; пробитие {armor.PenetrationResistance:0.0}");
                    }
                }
            }
        }
        else
        {
            Heading("АНАТОМИЯ И ИНВЕНТАРЬ");
            Value("Подробные данные", "Загружаются…");
        }

        return lines;
    }

    private static void AddWeapon(
        List<CardLine> lines,
        string heading,
        RenderWeaponVisual weapon)
    {
        lines.Add(new CardLine(heading, true));

        WeaponConfig? config = weapon.Config;
        if (config == null)
        {
            lines.Add(new CardLine("  Не установлено", false));
            return;
        }

        void Value(string label, string value) =>
            lines.Add(new CardLine($"  {label}: {value}", false));

        Value("Название", config.Name ?? "Безымянное");
        Value("Тип / технология", $"{config.Type} / {config.Technology}");
        Value("Возможности", config.Capabilities.ToString());
        Value("Масса", $"{config.Weight:0.00} кг");
        Value("Урон", config.BaseDamage.ToString());
        Value("Интервал выстрела", $"{config.FireRate:0.000} с");
        Value("Магазин", $"{weapon.Ammo} / {config.MagazineSize}");
        Value("Запас патронов", weapon.ReserveAmmo.ToString());
        Value("Тип боеприпаса", weapon.AmmoType.ToString());
        Value("Перезарядка", weapon.Reloading
            ? $"идёт ({weapon.ReloadTimer:0.0} с)"
            : "нет");
        Value("Кулдаун / отдача", $"{weapon.Cooldown:0.000} с / {weapon.Recoil:0.000}");
        Value("Режимы", $"{weapon.FireMode} / {weapon.AimMode} / {weapon.TargetMode}");
        Value("Очередь", weapon.BurstRemaining.ToString());
        Value("Прицеливание", weapon.AimIndicatorActive
            ? $"активно, {weapon.AimTimer:0.00} с, точка {FormatVector(weapon.AimPoint)}"
            : "нет");

        if (config is RangedWeaponConfig ranged)
        {
            Value("Эффективная дальность", $"{ranged.TotalEffectiveRange:0.0} клеток");
            Value("Базовая точность", $"{ranged.BaseAccuracy:0.000}");
            Value("Текущий разброс", $"{ranged.GetCurrentSpread():0.000}");
            Value("Время прицеливания", $"{ranged.AimTime:0.00} с");
            Value("Размер очереди", ranged.BurstCount.ToString());
            Value("Боеприпас по умолчанию", ranged.DefaultAmmunition?.Name ?? "Не задан");
            Value("Модификации", ranged.InstalledAttachments?.Count.ToString() ?? "0");
        }

        Value("Перезарядка оружия", $"{config.ReloadTime:0.00} с");
    }

    private static string FormatVector(System.Numerics.Vector3 value) =>
        $"({value.X:0.00}, {value.Y:0.00}, {value.Z:0.00})";

    private static float Angle(System.Numerics.Vector3 direction) =>
        MathF.Atan2(direction.Y, direction.X) * 180f / MathF.PI;

    private static Font? LoadFont()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates = { "segoeui.ttf", "arial.ttf", "tahoma.ttf" };

        for (int i = 0; i < candidates.Length; i++)
        {
            string path = Path.Combine(folder, candidates[i]);
            if (!File.Exists(path))
                continue;

            try
            {
                return new Font(path);
            }
            catch
            {
                // Try the next installed font.
            }
        }

        return null;
    }

    public void Dispose()
    {
        for (int i = 0; i < _lineTexts.Length; i++)
            _lineTexts[i]?.Dispose();

        _title?.Dispose();
        _closeLabel?.Dispose();
        _footer?.Dispose();
        _font?.Dispose();
        _panel.Dispose();
        _header.Dispose();
        _accent.Dispose();
        _closeButton.Dispose();
    }

    private readonly record struct CardLine(string Text, bool IsHeader);
}
