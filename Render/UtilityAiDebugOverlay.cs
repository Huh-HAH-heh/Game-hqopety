using System;
using System.IO;
using Core.AI.Behaviac;
using Core.Unit;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

/// <summary>
/// Optional selected-pawn Utility AI inspector. It consumes the exact immutable
/// context and score breakdown produced by the simulation; it never recomputes AI.
/// </summary>
public sealed class UtilityAiDebugOverlay : IDisposable
{
    private const float Width = 412f;
    private const float Height = 454f;
    private const float HeaderHeight = 28f;
    private const float RowHeight = 21f;
    private const float BarWidth = 112f;

    private static readonly string[] CandidateLabels =
    {
        "Бой по цели",
        "Удержать позицию + огонь",
        "Укрытие",
        "Отход",
        "Приказ: движение",
        "Приказ: удержание",
        "Приказ: атака",
        "Обход цели",
        "Лечь и стрелять",
        "Подавляющий огонь",
        "Последний контакт",
        "Звуковой контакт",
        "Сообщение союзника",
        "Патруль"
    };

    private readonly Font? _font;
    private readonly Text? _title;
    private readonly Text? _decisionLine;
    private readonly Text? _bodyLine;
    private readonly Text? _vitalsLine;
    private readonly Text? _traitsLine;
    private readonly Text? _contactLine;
    private readonly Text? _waitingLine;
    private readonly Text[] _rowLabels = new Text[CandidateLabels.Length];
    private readonly Text[] _rowScores = new Text[CandidateLabels.Length];
    private readonly RectangleShape[] _barBackgrounds = new RectangleShape[CandidateLabels.Length];
    private readonly RectangleShape[] _barFills = new RectangleShape[CandidateLabels.Length];

    private readonly RectangleShape _panel = new(new Vector2f(Width, Height))
    {
        FillColor = new Color(9, 13, 19, 238),
        OutlineColor = new Color(90, 114, 139, 245),
        OutlineThickness = 1f
    };

    private readonly RectangleShape _header = new(new Vector2f(Width - 2f, HeaderHeight))
    {
        FillColor = new Color(28, 41, 55, 255)
    };

    private float _x = 12f;
    private float _y;
    private float _dragOffsetX;
    private float _dragOffsetY;
    private bool _hasPosition;
    private bool _dragging;

    public bool Contains(Vector2i position) =>
        _panel.GetGlobalBounds().Contains(position);

    public bool HandleMousePressed(Vector2i position, uint windowWidth, uint windowHeight)
    {
        Layout(windowWidth, windowHeight);
        if (!_panel.GetGlobalBounds().Contains(position))
            return false;

        if (_header.GetGlobalBounds().Contains(position))
        {
            _dragging = true;
            _dragOffsetX = position.X - _panel.Position.X;
            _dragOffsetY = position.Y - _panel.Position.Y;
        }

        return true;
    }

    public bool HandleMouseMoved(Vector2i position, uint windowWidth, uint windowHeight)
    {
        if (!_dragging)
            return false;

        _x = Math.Clamp(position.X - _dragOffsetX, 8f, Math.Max(8f, windowWidth - Width - 8f));
        _y = Math.Clamp(position.Y - _dragOffsetY, 8f, Math.Max(8f, windowHeight - Height - 8f));
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

    public UtilityAiDebugOverlay()
    {
        _font = LoadFont();
        if (_font == null)
            return;

        _title = CreateText("UTILITY AI  /  ПЕШКА", 12, bold: true);
        _decisionLine = CreateText("", 11, bold: true);
        _bodyLine = CreateText("", 10);
        _vitalsLine = CreateText("", 10);
        _traitsLine = CreateText("", 10);
        _contactLine = CreateText("", 10);
        _waitingLine = CreateText("", 11);

        for (int i = 0; i < CandidateLabels.Length; i++)
        {
            _rowLabels[i] = CreateText(CandidateLabels[i], 10);
            _rowScores[i] = CreateText("0.00", 10);
            _barBackgrounds[i] = new RectangleShape(new Vector2f(BarWidth, 8f))
            {
                FillColor = new Color(30, 40, 52, 255),
                OutlineColor = new Color(67, 82, 99, 180),
                OutlineThickness = 0.5f
            };
            _barFills[i] = new RectangleShape(new Vector2f(0f, 8f))
            {
                FillColor = new Color(84, 157, 218, 255)
            };
        }
    }

    public void Draw(
        RenderWindow window,
        uint windowWidth,
        uint windowHeight,
        bool visible,
        UnitId selectedUnit,
        bool hasDecision,
        UnitIntentUtilityContext context,
        UnitIntentUtilityBreakdown decision)
    {
        if (!visible || _font == null || _title == null ||
            _decisionLine == null || _bodyLine == null || _vitalsLine == null ||
            _traitsLine == null || _contactLine == null || _waitingLine == null)
            return;

        Layout(windowWidth, windowHeight);
        float x = _panel.Position.X;
        float y = _panel.Position.Y;

        window.Draw(_panel);
        window.Draw(_header);

        _title.Position = new Vector2f(x + 10f, y + 8f);
        window.Draw(_title);

        if (!selectedUnit.IsValid || !hasDecision)
        {
            _waitingLine.DisplayedString = selectedUnit.IsValid
                ? $"Пешка {selectedUnit.Index}:{selectedUnit.Generation}: ожидание актуального решения"
                : "Выберите одну пешку для просмотра её оценок.";
            _waitingLine.Position = new Vector2f(x + 12f, y + 45f);
            _waitingLine.FillColor = new Color(188, 201, 216, 255);
            window.Draw(_waitingLine);
            return;
        }

        _decisionLine.DisplayedString =
            $"ID {selectedUnit.Index}:{selectedUnit.Generation}  {IntentLabel(decision.SuggestedIntent)} " +
            $"({decision.SuggestedScore:0.00})  {ReasonLabel(decision.Reason)}";
        _decisionLine.Position = new Vector2f(x + 11f, y + 32f);
        _decisionLine.FillColor = decision.RetainedPreviousIntent
            ? new Color(244, 201, 112, 255)
            : new Color(134, 222, 171, 255);
        window.Draw(_decisionLine);

        _bodyLine.DisplayedString =
            $"HP {Percent(context.HealthRatio)}  Боль {Percent(context.Pain)}  " +
            $"Усталость {Percent(context.Fatigue)}  Стресс {Percent(context.Stress)}";
        _bodyLine.Position = new Vector2f(x + 11f, y + 51f);
        _bodyLine.FillColor = new Color(213, 223, 234, 255);
        window.Draw(_bodyLine);

        _vitalsLine.DisplayedString =
            $"Энерг. {Percent(context.EnergyRatio)} · Вода {Percent(context.HydrationRatio)} · " +
            $"Подавл. {(context.IsSuppressed ? "ДА" : "нет")} · Паника {(context.IsPanicked ? "ДА" : "нет")}";
        _vitalsLine.Position = new Vector2f(x + 11f, y + 68f);
        _vitalsLine.FillColor = new Color(184, 199, 215, 255);
        window.Draw(_vitalsLine);

        _traitsLine.DisplayedString =
            $"Характер: осторожность {Percent(context.Caution)}  агрессия {Percent(context.Aggression)}  " +
            $"дисциплина {Percent(context.Discipline)}";
        _traitsLine.Position = new Vector2f(x + 11f, y + 85f);
        _traitsLine.FillColor = new Color(184, 199, 215, 255);
        window.Draw(_traitsLine);

        _contactLine.DisplayedString =
            $"Контакт: зрение {(context.HasVisibleTarget ? "+" : "−")} · память {(context.HasRecentTarget ? "+" : "−")} · " +
            $"сигнал {(context.HasInvestigableContact ? "+" : "−")} · группа {(context.HasSquadContact ? "+" : "−")} · " +
            $"опасность {(context.IsInImmediateDanger ? "!" : "—")}";
        _contactLine.Position = new Vector2f(x + 11f, y + 102f);
        _contactLine.FillColor = new Color(157, 178, 199, 255);
        window.Draw(_contactLine);

        float rowStartY = y + 126f;
        for (int i = 0; i < CandidateLabels.Length; i++)
        {
            float rowY = rowStartY + i * RowHeight;
            float score = Clamp01(ScoreAt(decision, i));
            _rowLabels[i].Position = new Vector2f(x + 11f, rowY);
            _rowLabels[i].FillColor = new Color(215, 224, 235, 255);
            window.Draw(_rowLabels[i]);

            _rowScores[i].DisplayedString = score.ToString("0.00");
            _rowScores[i].Position = new Vector2f(x + 251f, rowY);
            _rowScores[i].FillColor = new Color(215, 224, 235, 255);
            window.Draw(_rowScores[i]);

            _barBackgrounds[i].Position = new Vector2f(x + 286f, rowY + 5f);
            _barFills[i].Position = new Vector2f(x + 286f, rowY + 5f);
            _barFills[i].Size = new Vector2f(BarWidth * score, 8f);

            BehaviacUnitIntent rowIntent = IntentAt(i);
            _barFills[i].FillColor = rowIntent == decision.SuggestedIntent
                ? new Color(91, 209, 144, 255)
                : score >= decision.SuggestedScore - 0.12f && score > 0f
                    ? new Color(232, 180, 83, 255)
                    : new Color(82, 145, 205, 240);

            window.Draw(_barBackgrounds[i]);
            if (score > 0f)
                window.Draw(_barFills[i]);
        }
    }

    private void Layout(uint windowWidth, uint windowHeight)
    {
        if (!_hasPosition)
        {
            _x = 12f;
            _y = windowHeight - Height - 8f;
            _hasPosition = true;
        }

        _x = Math.Clamp(_x, 8f, Math.Max(8f, windowWidth - Width - 8f));
        _y = Math.Clamp(_y, 8f, Math.Max(8f, windowHeight - Height - 8f));
        _panel.Position = new Vector2f(_x, _y);
        _header.Position = new Vector2f(_x + 1f, _y + 1f);
    }

    private static float ScoreAt(in UnitIntentUtilityBreakdown decision, int index) =>
        index switch
        {
            0 => decision.EngageScore,
            1 => decision.HoldAndEngageScore,
            2 => decision.CoverScore,
            3 => decision.RetreatScore,
            4 => decision.MoveOrderScore,
            5 => decision.HoldOrderScore,
            6 => decision.AttackOrderScore,
            7 => decision.FlankScore,
            8 => decision.ProneScore,
            9 => decision.SuppressiveFireScore,
            10 => decision.LastKnownContactScore,
            11 => decision.InvestigableContactScore,
            12 => decision.SquadContactScore,
            _ => decision.PatrolScore
        };

    private static BehaviacUnitIntent IntentAt(int index) =>
        index switch
        {
            0 => BehaviacUnitIntent.EngageTarget,
            1 => BehaviacUnitIntent.HoldPositionEngage,
            2 => BehaviacUnitIntent.SeekCover,
            3 => BehaviacUnitIntent.RetreatToCover,
            4 => BehaviacUnitIntent.FollowMoveOrder,
            5 => BehaviacUnitIntent.HoldPosition,
            6 => BehaviacUnitIntent.AttackOrder,
            7 => BehaviacUnitIntent.FlankTarget,
            8 => BehaviacUnitIntent.ProneEngage,
            9 => BehaviacUnitIntent.SuppressiveFire,
            10 => BehaviacUnitIntent.InvestigateLastKnownTarget,
            11 => BehaviacUnitIntent.InvestigateContact,
            12 => BehaviacUnitIntent.InvestigateSquadContact,
            _ => BehaviacUnitIntent.Patrol
        };

    private static string IntentLabel(BehaviacUnitIntent intent) =>
        intent switch
        {
            BehaviacUnitIntent.Dead => "МЁРТВ",
            BehaviacUnitIntent.SeekCover => "УКРЫТИЕ",
            BehaviacUnitIntent.RetreatToCover => "ОТХОД",
            BehaviacUnitIntent.FlankTarget => "ОБХОД",
            BehaviacUnitIntent.EngageTarget => "АТАКА",
            BehaviacUnitIntent.ProneEngage => "ЛЁЖА",
            BehaviacUnitIntent.SuppressiveFire => "ПОДАВЛЕНИЕ",
            BehaviacUnitIntent.InvestigateLastKnownTarget => "ПОСЛЕДНИЙ КОНТАКТ",
            BehaviacUnitIntent.InvestigateContact => "ИССЛЕДОВАТЬ КОНТАКТ",
            BehaviacUnitIntent.InvestigateSquadContact => "ОТЧЁТ СОЮЗНИКА",
            BehaviacUnitIntent.FollowMoveOrder => "ПРИКАЗ: ДВИЖЕНИЕ",
            BehaviacUnitIntent.HoldPosition => "ПРИКАЗ: УДЕРЖАНИЕ",
            BehaviacUnitIntent.AttackOrder => "ПРИКАЗ: АТАКА",
            BehaviacUnitIntent.HoldPositionEngage => "ДЕРЖАТЬСЯ И СТРЕЛЯТЬ",
            _ => "ПАТРУЛЬ"
        };

    private static string ReasonLabel(UnitIntentDecisionReason reason) =>
        reason switch
        {
            UnitIntentDecisionReason.InitialSelection => "первый выбор",
            UnitIntentDecisionReason.RetainedPreviousIntent => "удерживает цель",
            UnitIntentDecisionReason.EmergencyOverride => "срочная реакция",
            UnitIntentDecisionReason.PreviousIntentNoLongerValid => "цель недействительна",
            UnitIntentDecisionReason.Dead => "смерть",
            _ => "максимальная оценка"
        };

    private static string Percent(float value) =>
        $"{MathF.Round(Clamp01(value) * 100f):0}%";

    private Text CreateText(string value, uint size, bool bold = false)
    {
        var text = new Text(_font!, value, size)
        {
            FillColor = new Color(215, 224, 235, 255)
        };
        if (bold)
            text.Style = Text.Styles.Bold;
        return text;
    }

    private static float Clamp01(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;

    private static Font? LoadFont()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates = { "segoeui.ttf", "arial.ttf", "tahoma.ttf" };
        for (int i = 0; i < candidates.Length; i++)
        {
            string path = Path.Combine(folder, candidates[i]);
            if (!File.Exists(path))
                continue;
            try { return new Font(path); }
            catch { /* Try the next installed font. */ }
        }
        return null;
    }

    public void Dispose()
    {
        for (int i = 0; i < _rowLabels.Length; i++)
        {
            _rowLabels[i]?.Dispose();
            _rowScores[i]?.Dispose();
            _barBackgrounds[i]?.Dispose();
            _barFills[i]?.Dispose();
        }

        _waitingLine?.Dispose();
        _contactLine?.Dispose();
        _traitsLine?.Dispose();
        _vitalsLine?.Dispose();
        _bodyLine?.Dispose();
        _decisionLine?.Dispose();
        _title?.Dispose();
        _font?.Dispose();
        _header.Dispose();
        _panel.Dispose();
    }
}
