using System;
using Core.AI.Behaviac;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

    private readonly RectangleShape _copyButton = new(new Vector2f(34f, 20f))
    {
        FillColor = new Color(38, 47, 59, 220),
        OutlineColor = new Color(89, 105, 126, 120),
        OutlineThickness = 1f
    };

    private readonly Font? _font;
    private readonly Text? _title;
    private readonly Text? _closeLabel;
    private readonly Text? _copyLabel;

    private RenderUnitState _lastUnit;
    private UnitCardDetailsSnapshot? _lastDetails;
    private bool _hasLastUnit;
    private readonly RectangleShape _bodyTab = new(new Vector2f(88f, 28f))
    {
        FillColor = new Color(39, 52, 67, 255),
        OutlineColor = new Color(80, 103, 129, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _detailsTab = new(new Vector2f(140f, 28f))
    {
        FillColor = new Color(39, 52, 67, 255),
        OutlineColor = new Color(80, 103, 129, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _organToggle = new(new Vector2f(112f, 28f))
    {
        FillColor = new Color(39, 52, 67, 255),
        OutlineColor = new Color(80, 103, 129, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _healthBarBack = new(new Vector2f(170f, 8f))
    {
        FillColor = new Color(44, 54, 68, 255),
        OutlineColor = new Color(75, 89, 107, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _healthBarFill = new(new Vector2f(170f, 8f))
    {
        FillColor = new Color(73, 157, 125, 255)
    };

    private readonly Text? _bodyTabLabel;
    private readonly Text? _detailsTabLabel;
    private readonly Text? _organToggleLabel;
    private readonly Text? _overallHealthText;
    private readonly Text? _footer;
    private readonly Text?[] _lineTexts = new Text?[MaxVisibleLines];
    private readonly UnitAnatomyCardView _anatomyView = new();

    private CardView _view = CardView.Anatomy;
    private bool _showOrgans = true;

    private int _scrollOffset;
    private int _visibleLines;
    private int _lineCount;
    private float _positionX;
    private float _positionY;
    private float _dragOffsetX;
    private float _dragOffsetY;
    private bool _hasPosition;
    private bool _dragging;
    private int _selectedAnatomyEntry = -1;
    private AnatomyCardEntry[] _latestAnatomyEntries = Array.Empty<AnatomyCardEntry>();

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
        _copyLabel = new Text(_font, "коп.", 9)
        {
            FillColor = new Color(164, 177, 194)
        };
        _bodyTabLabel = new Text(_font, "ТЕЛО", 11)
        {
            FillColor = new Color(239, 243, 249),
            Style = Text.Styles.Bold
        };
        _detailsTabLabel = new Text(_font, "ХАРАКТЕРИСТИКИ", 10)
        {
            FillColor = new Color(181, 197, 215),
            Style = Text.Styles.Bold
        };
        _organToggleLabel = new Text(_font, "ОРГАНЫ: ВКЛ", 10)
        {
            FillColor = new Color(181, 197, 215),
            Style = Text.Styles.Bold
        };
        _overallHealthText = new Text(_font, "", 11)
        {
            FillColor = new Color(210, 221, 232)
        };

        _footer = new Text(_font, "Цвет показывает состояние тканей и органов", 10)
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
        _view = CardView.Anatomy;
        _showOrgans = true;
    }

    public void ShowAnatomyList()
    {
        _view = CardView.AnatomyList;
        _selectedAnatomyEntry = -1;
        _scrollOffset = 0;
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

        if (_bodyTab.GetGlobalBounds().Contains(position))
        {
            _view = _view == CardView.EntryDetails
                ? CardView.AnatomyList
                : CardView.Anatomy;
            _scrollOffset = 0;
            return true;
        }

        if (_detailsTab.GetGlobalBounds().Contains(position))
        {
            _view = CardView.Details;
            _scrollOffset = 0;
            return true;
        }

        if (_view == CardView.Anatomy &&
            _organToggle.GetGlobalBounds().Contains(position))
        {
            _showOrgans = !_showOrgans;
            return true;
        }

        if (_view == CardView.AnatomyList && _latestAnatomyEntries.Length > 0)
        {
            float listTop = _panel.Position.Y + 82f;
            int row = (int)((position.Y - listTop) / RowHeight);
            int entryIndex = _scrollOffset + row;
            if (position.Y >= listTop &&
                entryIndex >= _scrollOffset &&
                entryIndex < Math.Min(_latestAnatomyEntries.Length, _scrollOffset + _visibleLines))
            {
                _selectedAnatomyEntry = entryIndex;
                _view = CardView.EntryDetails;
                _scrollOffset = 0;
                return true;
            }
        }

        if (_copyButton.GetGlobalBounds().Contains(position))
        {
            if (_hasLastUnit)
            {
                List<CardLine> lines = BuildLines(_lastUnit, _lastDetails);
                StringBuilder state = new(4096);
                for (int i = 0; i < lines.Count; i++)
                    state.AppendLine(lines[i].Text);

                Clipboard.Contents = state.ToString();
                if (_copyLabel != null)
                    _copyLabel.DisplayedString = "OK";
            }

            return true;
        }

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

        if (_view is CardView.Details or CardView.EntryDetails or CardView.AnatomyList)
            _scrollOffset = Math.Max(0, _scrollOffset + (delta > 0f ? -3 : 3));
        return true;
    }

    public void Draw(
        RenderWindow window,
        RenderUnitState unit,
        RenderBodyPart[] bodyParts,
        UnitCardDetailsSnapshot? details,
        uint windowWidth,
        uint windowHeight)
    {
        if (!IsOpen)
            return;

        Layout(windowWidth, windowHeight);
        _lastUnit = unit;
        _lastDetails = details;
        _hasLastUnit = true;
        _latestAnatomyEntries = details?.AnatomyEntries ?? Array.Empty<AnatomyCardEntry>();
        AnatomyCardEntry? selectedEntry =
            (uint)_selectedAnatomyEntry < (uint)_latestAnatomyEntries.Length
                ? _latestAnatomyEntries[_selectedAnatomyEntry]
                : null;
        List<CardLine>? lines = _view switch
        {
            CardView.Details => BuildLines(unit, details),
            CardView.EntryDetails when selectedEntry != null => BuildEntryLines(selectedEntry),
            _ => null
        };
        _lineCount = _view == CardView.AnatomyList
            ? _latestAnatomyEntries.Length
            : lines?.Count ?? 0;
        _visibleLines = _view is CardView.Details or CardView.EntryDetails or CardView.AnatomyList
            ? Math.Min(MaxVisibleLines, Math.Max(1, (int)((_panel.Size.Y - 112f) / RowHeight)))
            : 0;
        _scrollOffset = Math.Clamp(
            _scrollOffset,
            0,
            Math.Max(0, _lineCount - _visibleLines));

        window.Draw(_panel);
        window.Draw(_header);
        window.Draw(_accent);
        window.Draw(_copyButton);
        window.Draw(_closeButton);
        if (_copyLabel != null)
            window.Draw(_copyLabel);

        _bodyTab.FillColor = _view == CardView.Anatomy
            ? new Color(67, 91, 115, 255)
            : new Color(30, 39, 51, 255);
        _detailsTab.FillColor = _view == CardView.Details
            ? new Color(67, 91, 115, 255)
            : new Color(30, 39, 51, 255);
        window.Draw(_bodyTab);
        window.Draw(_detailsTab);

        if (_bodyTabLabel != null)
        {
            _bodyTabLabel.DisplayedString = _view == CardView.EntryDetails ? "← К СПИСКУ" : "ТЕЛО";
            window.Draw(_bodyTabLabel);
        }
        if (_detailsTabLabel != null)
            window.Draw(_detailsTabLabel);

        if (_view == CardView.Anatomy)
        {
            _organToggle.FillColor = _showOrgans
                ? new Color(48, 72, 66, 255)
                : new Color(30, 39, 51, 255);
            window.Draw(_organToggle);
            if (_organToggleLabel != null)
            {
                _organToggleLabel.DisplayedString = _showOrgans ? "ОРГАНЫ: ВКЛ" : "ОРГАНЫ: ВЫКЛ";
                window.Draw(_organToggleLabel);
            }
        }

        if (_title != null)
        {
            _title.DisplayedString = _view switch
            {
                CardView.AnatomyList => $"АНАТОМИЯ ЮНИТА {unit.Index}:{unit.Generation}",
                CardView.EntryDetails when selectedEntry != null => selectedEntry.Name.ToUpperInvariant(),
                CardView.Details => $"ХАРАКТЕРИСТИКИ {unit.Index}:{unit.Generation}",
                _ => $"ЮНИТ {unit.Index}:{unit.Generation}  •  {unit.Type}"
            };
            window.Draw(_title);
        }

        if (_closeLabel != null)
            window.Draw(_closeLabel);

        if (_view == CardView.Anatomy)
        {
            if (_overallHealthText != null)
            {
                _overallHealthText.DisplayedString =
                    $"ЗДОРОВЬЕ  {unit.HitPoints:0.0}/{unit.MaxHitPoints:0.0} HP ({unit.HealthRatio:P0})";
                _overallHealthText.FillColor = HealthStatusColor(unit.HealthRatio);
                window.Draw(_overallHealthText);
            }

            _healthBarFill.Size = new Vector2f(170f * Math.Clamp(unit.HealthRatio, 0f, 1f), 8f);
            _healthBarFill.FillColor = HealthStatusColor(unit.HealthRatio);
            window.Draw(_healthBarBack);
            window.Draw(_healthBarFill);

            _anatomyView.Draw(
                window,
                unit,
                bodyParts,
                details,
                _panel.Position.X + 5f,
                _panel.Position.Y + 101f,
                _panel.Size.X - 10f,
                _panel.Size.Y - 145f,
                _showOrgans);
        }
        else if (_view == CardView.AnatomyList)
        {
            for (int i = 0; i < _lineTexts.Length; i++)
            {
                Text? text = _lineTexts[i];
                if (text == null)
                    continue;

                int entryIndex = _scrollOffset + i;
                if (i >= _visibleLines || entryIndex >= _latestAnatomyEntries.Length)
                {
                    text.DisplayedString = "";
                    continue;
                }

                AnatomyCardEntry entry = _latestAnatomyEntries[entryIndex];
                string label = $"{entry.Category}: {entry.Name}  {entry.HitPoints:0.0}/{entry.MaxHitPoints:0.0} HP";
                if (label.Length > 64)
                    label = label.Substring(0, 63) + "…";
                text.DisplayedString = label;
                text.FillColor = entry.Present
                    ? HealthStatusColor(entry.HealthRatio)
                    : new Color(139, 148, 159);
                text.Position = new Vector2f(
                    _panel.Position.X + 14f,
                    _panel.Position.Y + 82f + i * RowHeight);
                window.Draw(text);
            }
        }
        else if (lines != null)
        {
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
                    _panel.Position.Y + 82f + i * RowHeight);
                window.Draw(text);
            }
        }

        if (_footer != null)
        {
            _footer.Position = new Vector2f(
                _panel.Position.X + 12f,
                _panel.Position.Y + _panel.Size.Y - 22f);
            _footer.DisplayedString = (_view is CardView.Details or CardView.EntryDetails or CardView.AnatomyList)
                && _lineCount > _visibleLines
                ? $"Строки {_scrollOffset + 1}–{Math.Min(_scrollOffset + _visibleLines, _lineCount)} из {_lineCount}   •   Колесо — прокрутка"
                : _view == CardView.Anatomy
                    ? "Зелёный — норма   Жёлтый — урон   Оранжевый — тяжёлый   Красный — критично"
                    : _view == CardView.AnatomyList
                        ? "Нажми на орган или часть тела, чтобы открыть карточку"
                        : _view == CardView.EntryDetails
                            ? "← К СПИСКУ — выбрать другую анатомическую сущность"
                            : "Колесо — прокрутка подробных характеристик";
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
        _bodyTab.Position = new Vector2f(x + 12f, y + 43f);
        _detailsTab.Position = new Vector2f(x + 106f, y + 43f);
        _organToggle.Position = new Vector2f(x + Width - 124f, y + 43f);
        _healthBarBack.Position = new Vector2f(x + Width - 190f, y + 83f);
        _healthBarFill.Position = new Vector2f(x + Width - 190f, y + 83f);

        if (_bodyTabLabel != null)
            _bodyTabLabel.Position = new Vector2f(x + 32f, y + 50f);
        if (_detailsTabLabel != null)
            _detailsTabLabel.Position = new Vector2f(x + 116f, y + 50f);
        if (_organToggleLabel != null)
            _organToggleLabel.Position = new Vector2f(x + Width - 117f, y + 50f);
        if (_overallHealthText != null)
            _overallHealthText.Position = new Vector2f(x + 12f, y + 79f);

        if (_title != null)
            _title.Position = new Vector2f(x + 12f, y + 10f);

        _copyButton.Position = new Vector2f(x + Width - 78f, y + 9f);
        if (_copyLabel != null)
            _copyLabel.Position = new Vector2f(x + Width - 73f, y + 12f);

        if (_closeLabel != null)
            _closeLabel.Position = new Vector2f(x + Width - 27f, y + 4f);
    }

    private static List<CardLine> BuildEntryLines(AnatomyCardEntry entry)
    {
        List<CardLine> lines = new(12);
        void Heading(string value) => lines.Add(new CardLine(value, true));
        void Value(string label, string value) => lines.Add(new CardLine($"  {label}: {value}", false));

        Heading(entry.Name);
        Value("Тип", entry.Category);
        Value("ID ткани", entry.Id);
        Value("Область тела", entry.RegionId);
        Value("Родитель", string.IsNullOrWhiteSpace(entry.ParentId) ? "Нет" : entry.ParentId);
        Value("Состояние", entry.Present ? "Присутствует" : "Отсутствует / разрушена");
        Value("Здоровье", $"{entry.HitPoints:0.0} / {entry.MaxHitPoints:0.0} HP ({entry.HealthRatio:P0})");
        Value("Кровотечение", $"{entry.BleedRate:0.000}");
        Value("Класс", entry.FunctionDescription);
        Value("Геометрия", entry.GeometryDescription);

        if (entry is OrganCardEntry organ)
        {
            ReadOnlySpan<Core.Unit.UnitAnatomyVolume> volumes = organ.Volumes;
            for (int i = 0; i < volumes.Length; i++)
            {
                Core.Unit.UnitAnatomyVolume volume = volumes[i];
                Value($"Объём {i + 1}", $"{volume.Id}; родитель {volume.ParentRegionId}");
                Value("  Входная ткань", volume.EntryBindingId);
                Value("  Центр / полуразмер", $"{volume.LocalCenter} / {volume.HalfExtents}");
                Value("  Потери энергии / пробития", $"{volume.EnergyLoss:0.0} / {volume.PenetrationLoss:0.0}");
            }
        }

        return lines;
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
        Value("Состояние ИИ", FormatAiState(unit.AiState));
        if (details is not null &&
            details.Unit.Index == unit.Index &&
            details.Unit.Generation == unit.Generation)
        {
            Value("Последнее решение Behaviac", FormatAiIntent(details.DecisionIntent));
        }
        Value("Поза", unit.Posture.ToString());
        Value("Общее здоровье", $"{unit.HitPoints:0.0} / {unit.MaxHitPoints:0.0} HP ({unit.HealthRatio * 100f:0.0}%)");
        if (details is not null &&
            details.Unit.Index == unit.Index &&
            details.Unit.Generation == unit.Generation)
        {
            Value("Боль", FormatPain(details.Pain));
        }
        Value("Учёт здоровья", "Общий HP — запас жизни; HP анатомии — состояние отдельных тканей.");
        if (unit.HitPoints <= 0f)
            Value("Причина смерти", "Общий запас HP исчерпан; локальные HP тканей сохраняются отдельно.");
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
            Heading("НАВЫКИ");
            for (int i = 0; i < details.PawnSkills.Length; i++)
            {
                RenderPawnSkill skill = details.PawnSkills[i];
                string name = skill.Skill switch
                {
                    UnitSkill.Shooting => "Стрельба",
                    UnitSkill.Melee => "Ближний бой",
                    UnitSkill.Construction => "Строительство",
                    UnitSkill.Mining => "Горное дело",
                    UnitSkill.Cooking => "Кулинария",
                    UnitSkill.Plants => "Растения",
                    UnitSkill.Animals => "Животные",
                    UnitSkill.Crafting => "Ремесло",
                    UnitSkill.Artistic => "Искусство",
                    UnitSkill.Medical => "Медицина",
                    UnitSkill.Social => "Социальное",
                    UnitSkill.Intellectual => "Интеллект",
                    _ => skill.Skill.ToString()
                };
                string passion = skill.Passion switch
                {
                    UnitSkillPassion.Minor => " ★",
                    UnitSkillPassion.Major => " ★★",
                    _ => string.Empty
                };
                Value(name, $"{skill.Level}/20{passion} · {skill.Experience:0}/{skill.ExperienceToNextLevel:0} XP");
            }
            Value("Стойкость к подавлению", $"{details.SuppressionResistance:P0}");

            int anatomyCount = details.AnatomyEntries.Length > 0
                ? details.AnatomyEntries.Length
                : details.HealthParts.Length;
            Heading($"АНАТОМИЯ И ЗДОРОВЬЕ ({anatomyCount})");

            if (details.AnatomyEntries.Length > 0)
            {
                // Show stable region IDs beside friendly names. Multiple torso
                // segments and authored rig nodes can share the legacy Part enum;
                // omitting their IDs made all of them look like duplicate parts.
                if (details.HealthParts.Length != details.AnatomyEntries.Length)
                {
                    Value("Сверка здоровья",
                        $"Слотов HP: {details.HealthParts.Length}; подписанных тканей: {details.AnatomyEntries.Length}.");
                }

                for (int i = 0; i < details.AnatomyEntries.Length; i++)
                {
                    AnatomyCardEntry tissue = details.AnatomyEntries[i];
                    string label =
                        $"{tissue.Name} [{tissue.Part}/{tissue.Category}] · {tissue.RegionId}";
                    string status = tissue.Present ? "" : "; отсутствует/разрушена";
                    Value(label,
                        $"{tissue.HitPoints:0.0}/{tissue.MaxHitPoints:0.0} HP; кровотечение {tissue.BleedRate:0.000}{status}");
                }
            }
            else if (details.HealthParts.Length == 0)
            {
                Value("Части тела", "Нет данных");
            }
            else
            {
                // Compatibility display for units without an authored anatomy profile.
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
                        float ballisticRating = armor.SharpRating > 0f
                            ? armor.SharpRating
                            : MathF.Max(armor.PenetrationResistance, armor.ArmorRating);
                        Value("  Покрытие / слой",
                            $"{armor.Coverage} / {armor.Layer} ({(armor.SoftArmor ? "мягкая" : "жёсткая")} броня)");
                        Value("  Защита от пуль/острого",
                            $"рейтинг {ballisticRating:0.0}; сопротивление пробитию {armor.PenetrationResistance:0.0}");
                        Value("  Защита от тупого / тепла",
                            $"{armor.BluntRating:0.0} / {armor.HeatRating:0.0}");
                        Value("  Потери энергии снаряда", $"{armor.EnergyLoss:0.0}");
                        Value("  Процентное поглощение обычного урона",
                            $"{armor.DamageAbsorption:P0} (не формула баллистики)");
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

    private static string FormatPain(float value)
    {
        float pain = Math.Clamp(value, 0f, 100f);
        string severity = pain switch
        {
            < 5f => "нет",
            < 20f => "слабая",
            < 45f => "умеренная",
            < 70f => "сильная",
            _ => "невыносимая"
        };

        return $"{pain:0}/100 — {severity}";
    }

    private static string FormatAiIntent(BehaviacUnitIntent intent) => intent switch
    {
        BehaviacUnitIntent.None => "Нет активного решения",
        BehaviacUnitIntent.Dead => "Юнит мёртв",
        BehaviacUnitIntent.SeekCover => "Укрыться от подавления",
        BehaviacUnitIntent.RetreatToCover => "Отойти в укрытие из-за ранений",
        BehaviacUnitIntent.FlankTarget => "Обойти видимую цель",
        BehaviacUnitIntent.EngageTarget => "Атаковать видимую цель",
        BehaviacUnitIntent.SuppressiveFire => "Подавлять по последней позиции",
        BehaviacUnitIntent.InvestigateLastKnownTarget => "Проверить последнюю позицию цели",
        BehaviacUnitIntent.Patrol => "Патрулировать",
        BehaviacUnitIntent.ProneEngage => "Лечь и атаковать",
        _ => intent.ToString()
    };

    private static string FormatAiState(UnitAiState state) =>
        state switch
        {
            UnitAiState.Idle => "Ожидание",
            UnitAiState.Attack => "Бой",
            UnitAiState.SeekCover => "Укрытие",
            UnitAiState.Search => "Поиск",
            UnitAiState.Dead => "Мёртв",
            UnitAiState.Injured => "Ранен",
            UnitAiState.Flank => "Фланг",
            _ => state.ToString()
        };

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
        _copyLabel?.Dispose();
        _copyButton.Dispose();
        _bodyTabLabel?.Dispose();
        _detailsTabLabel?.Dispose();
        _organToggleLabel?.Dispose();
        _overallHealthText?.Dispose();
        _footer?.Dispose();
        _font?.Dispose();
        _panel.Dispose();
        _header.Dispose();
        _accent.Dispose();
        _closeButton.Dispose();
        _bodyTab.Dispose();
        _detailsTab.Dispose();
        _organToggle.Dispose();
        _healthBarBack.Dispose();
        _healthBarFill.Dispose();
        _anatomyView.Dispose();
    }

    private static Color HealthStatusColor(float ratio) => ratio switch
    {
        <= 0.15f => new Color(200, 62, 67, 255),
        <= 0.40f => new Color(215, 109, 58, 255),
        <= 0.70f => new Color(211, 177, 64, 255),
        _ => new Color(73, 157, 125, 255)
    };

    private enum CardView : byte
    {
        Anatomy,
        AnatomyList,
        EntryDetails,
        Details
    }

    private readonly record struct CardLine(string Text, bool IsHeader);
}
