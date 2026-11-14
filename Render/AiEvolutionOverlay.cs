using System;
using System.IO;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public sealed class AiEvolutionOverlay : IDisposable
{
    private const float Width = 560f;
    private const float Height = 260f;

    private readonly Font? _font;
    private readonly RectangleShape _panel = new(new Vector2f(Width, Height))
    {
        FillColor = new Color(5, 9, 15, 238),
        OutlineColor = new Color(91, 118, 145, 245),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _header = new(new Vector2f(Width - 2f, 28f))
    {
        FillColor = new Color(25, 39, 54, 255)
    };
    private readonly RectangleShape _blueTrack = new(new Vector2f(232f, 5f))
    {
        FillColor = new Color(35, 48, 62, 255)
    };
    private readonly RectangleShape _blueFill = new(new Vector2f(0f, 5f))
    {
        FillColor = new Color(75, 174, 245, 255)
    };
    private readonly RectangleShape _redTrack = new(new Vector2f(232f, 5f))
    {
        FillColor = new Color(35, 48, 62, 255)
    };
    private readonly RectangleShape _redFill = new(new Vector2f(0f, 5f))
    {
        FillColor = new Color(240, 106, 90, 255)
    };

    private readonly Text? _title;
    private readonly Text? _generation;
    private readonly Text? _timer;
    private readonly Text? _blue;
    private readonly Text? _red;
    private readonly Text? _fitness;
    private readonly Text? _genes;
    private readonly Text? _lastCandidate;
    private readonly Text? _message;
    private readonly Text? _controls;
    private float _x = 12f;
    private float _y = 218f;
    private float _dragOffsetX;
    private float _dragOffsetY;
    private bool _dragging;
    private bool _hasPosition;

    public bool Contains(Vector2i position) =>
        _panel.GetGlobalBounds().Contains(position);

    public bool HandleMousePressed(Vector2i position, uint windowWidth, uint windowHeight)
    {
        if (!Contains(position))
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

        _x = Math.Clamp(position.X - _dragOffsetX, 8f,
            Math.Max(8f, windowWidth - Width - 8f));
        _y = Math.Clamp(position.Y - _dragOffsetY, 8f,
            Math.Max(8f, windowHeight - Height - 8f));
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

    public AiEvolutionOverlay()
    {
        _font = LoadFont();
        if (_font == null)
            return;

        _title = CreateText("ЭВОЛЮЦИЯ ИИ · LIVE", 12, true);
        _generation = CreateText("", 11, true);
        _timer = CreateText("", 10, false);
        _blue = CreateText("", 10, true);
        _red = CreateText("", 10, true);
        _fitness = CreateText("", 10, true);
        _genes = CreateText("", 9, false);
        _lastCandidate = CreateText("", 9, true);
        _message = CreateText("", 9, false);
        _controls = CreateText("Остановка и применение профиля — меню тестовых сцен", 9, true);
    }

    public void Draw(RenderWindow window, uint windowWidth, uint windowHeight, AiEvolutionStatus status)
    {
        if (_font == null || _title == null || _generation == null ||
            _timer == null || _blue == null || _red == null ||
            _fitness == null || _genes == null || _lastCandidate == null ||
            _message == null || _controls == null)
        {
            return;
        }

        Layout(windowWidth, windowHeight);
        float x = _panel.Position.X;
        float y = _panel.Position.Y;

        _panel.Position = new Vector2f(x, y);
        _header.Position = new Vector2f(x + 1f, y + 1f);
        _title.Position = new Vector2f(x + 12f, y + 7f);
        _title.DisplayedString = status.IsRunning
            ? "ЭВОЛЮЦИОННОЕ ОБУЧЕНИЕ · АКТИВНО"
            : "ЭВОЛЮЦИОННОЕ ОБУЧЕНИЕ · ОСТАНОВЛЕНО";

        _generation.Position = new Vector2f(x + 12f, y + 36f);
        _generation.DisplayedString =
            $"Поколение {status.Generation}  ·  кандидат {status.CandidateIndex}/{status.PopulationSize}  ·  матч {status.RoundIndex}/{status.RoundCount}";

        string candidateSide = status.CandidateFaction == 1 ? "СИНИЕ" : "КРАСНЫЕ";
        _timer.Position = new Vector2f(x + 12f, y + 55f);
        _timer.DisplayedString =
            $"Матч: {status.EpisodeElapsedSeconds:0.0}/{status.EpisodeDurationSeconds:0}s  ·  геном кандидата: {candidateSide}  ·  против базового профиля";

        _blue.Position = new Vector2f(x + 12f, y + 74f);
        _blue.DisplayedString =
            $"Синие {(status.CandidateFaction == 1 ? "· КАНДИДАТ" : "· БАЗА")}: {status.BlueAlive}/{status.BlueTotal}  ·  здоровье {status.BlueHealthRatio:P0}";
        _blue.FillColor = status.CandidateFaction == 1
            ? new Color(112, 198, 255, 255)
            : new Color(185, 198, 210, 255);

        _red.Position = new Vector2f(x + 290f, y + 74f);
        _red.DisplayedString =
            $"Красные {(status.CandidateFaction == 2 ? "· КАНДИДАТ" : "· БАЗА")}: {status.RedAlive}/{status.RedTotal}  ·  здоровье {status.RedHealthRatio:P0}";
        _red.FillColor = status.CandidateFaction == 2
            ? new Color(255, 145, 122, 255)
            : new Color(185, 198, 210, 255);

        _blueTrack.Position = new Vector2f(x + 12f, y + 94f);
        _redTrack.Position = new Vector2f(x + 290f, y + 94f);
        _blueFill.Position = _blueTrack.Position;
        _redFill.Position = _redTrack.Position;
        _blueFill.Size = new Vector2f(232f * Math.Clamp(status.BlueHealthRatio, 0f, 1f), 5f);
        _redFill.Size = new Vector2f(232f * Math.Clamp(status.RedHealthRatio, 0f, 1f), 5f);

        _fitness.Position = new Vector2f(x + 12f, y + 108f);
        string currentFitness = double.IsFinite(status.CurrentRoundFitness)
            ? $"матч fitness {status.CurrentRoundFitness:+0.0;-0.0;0.0}"
            : "матч ещё оценивается";
        _fitness.DisplayedString =
            $"{currentFitness}  ·  среднее кандидата {status.CandidateMeanFitness:+0.0;-0.0;0.0}  ·  лучший {status.BestFitness:+0.0;-0.0;0.0}";
        _fitness.FillColor = status.BestFitness > 0d
            ? new Color(158, 225, 169, 255)
            : new Color(220, 222, 228, 255);

        _genes.Position = new Vector2f(x + 12f, y + 131f);
        _genes.DisplayedString = status.GeneSummary;

        _lastCandidate.Position = new Vector2f(x + 12f, y + 157f);
        _lastCandidate.DisplayedString = double.IsFinite(status.LastCompletedCandidateFitness)
            ? $"Последний завершённый кандидат G{status.LastCompletedGeneration} #{status.LastCompletedCandidateIndex}: " +
              $"fitness {status.LastCompletedCandidateFitness:+0.0;-0.0;0.0}"
            : "Последний завершённый кандидат: ожидание первого результата";
        _lastCandidate.FillColor = double.IsFinite(status.LastCompletedCandidateFitness) &&
            status.LastCompletedCandidateFitness >= status.BestFitness
                ? new Color(158, 225, 169, 255)
                : new Color(191, 208, 224, 255);

        _message.Position = new Vector2f(x + 12f, y + 181f);
        _message.DisplayedString = status.Message;
        _message.FillColor = status.IsRunning
            ? new Color(191, 208, 224, 255)
            : new Color(242, 207, 139, 255);

        _controls.Position = new Vector2f(x + 12f, y + 231f);
        _controls.DisplayedString = status.IsRunning
            ? $"Остановка — меню тестовых сцен  ·  файл: {Path.GetFileName(status.BestProfilePath)}"
            : $"Лучший профиль сохранён отдельно  ·  файл: {Path.GetFileName(status.BestProfilePath)}";

        window.Draw(_panel);
        window.Draw(_header);
        window.Draw(_blueTrack);
        window.Draw(_blueFill);
        window.Draw(_redTrack);
        window.Draw(_redFill);
        window.Draw(_title);
        window.Draw(_generation);
        window.Draw(_timer);
        window.Draw(_blue);
        window.Draw(_red);
        window.Draw(_fitness);
        window.Draw(_genes);
        window.Draw(_lastCandidate);
        window.Draw(_message);
        window.Draw(_controls);
    }

    private void Layout(uint windowWidth, uint windowHeight)
    {
        if (!_hasPosition)
        {
            _x = 12f;
            _y = 218f;
            _hasPosition = true;
        }

        _x = Math.Clamp(_x, 8f, Math.Max(8f, windowWidth - Width - 8f));
        _y = Math.Clamp(_y, 8f, Math.Max(8f, windowHeight - Height - 8f));
        _panel.Position = new Vector2f(_x, _y);
        _header.Position = new Vector2f(_x + 1f, _y + 1f);
    }

    private Text CreateText(string value, uint size, bool bold) =>
        new(_font!, value, size)
        {
            FillColor = new Color(224, 234, 245, 255),
            Style = bold ? Text.Styles.Bold : Text.Styles.Regular
        };

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
                // Try the next installed system font.
            }
        }

        return null;
    }

    public void Dispose()
    {
        _controls?.Dispose();
        _message?.Dispose();
        _lastCandidate?.Dispose();
        _genes?.Dispose();
        _fitness?.Dispose();
        _red?.Dispose();
        _blue?.Dispose();
        _timer?.Dispose();
        _generation?.Dispose();
        _title?.Dispose();
        _font?.Dispose();
        _redFill.Dispose();
        _redTrack.Dispose();
        _blueFill.Dispose();
        _blueTrack.Dispose();
        _header.Dispose();
        _panel.Dispose();
    }
}
