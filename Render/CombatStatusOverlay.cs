using System;
using System.IO;
using Core.Unit;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public sealed class CombatStatusOverlay : IDisposable
{
    private readonly Font? _font;
    private readonly Text? _title;
    private readonly Text? _blue;
    private readonly Text? _red;
    private readonly Text? _details;
    private readonly RectangleShape _panel =
        new RectangleShape(new Vector2f(430f, 126f))
        {
            FillColor = new Color(5, 8, 13, 220),
            OutlineColor = new Color(72, 88, 105, 210),
            OutlineThickness = 1f
        };
    private readonly RectangleShape _blueAccent =
        new RectangleShape(new Vector2f(5f, 28f))
        {
            FillColor = new Color(70, 178, 255, 245)
        };
    private readonly RectangleShape _redAccent =
        new RectangleShape(new Vector2f(5f, 28f))
        {
            FillColor = new Color(255, 112, 70, 245)
        };

    public bool Visible { get; set; } = true;

    public CombatStatusOverlay()
    {
        _font = LoadFont();
        if (_font == null)
            return;

        _title = new Text(_font, "RIMCLONE  /  FIREFIGHT", 13)
        {
            Position = new Vector2f(24f, 20f),
            FillColor = new Color(245, 248, 252),
            Style = Text.Styles.Bold
        };

        _blue = new Text(_font, "", 12)
        {
            Position = new Vector2f(26f, 42f),
            FillColor = new Color(105, 190, 255),
            Style = Text.Styles.Bold
        };

        _red = new Text(_font, "", 12)
        {
            Position = new Vector2f(220f, 42f),
            FillColor = new Color(255, 144, 103),
            Style = Text.Styles.Bold
        };

        _details = new Text(_font, "", 11)
        {
            Position = new Vector2f(24f, 64f),
            FillColor = new Color(215, 222, 231)
        };
    }

    public void Draw(
        RenderWindow window,
        UnitSimulation simulation,
        double shotsPerSecond,
        double hitEventsPerSecond,
        double hitEventsPerRound,
        bool showTracers)
    {
        if (!Visible || _font == null ||
            _title == null || _blue == null ||
            _red == null || _details == null)
        {
            return;
        }

        int blueAlive = CountAlive(simulation, 1);
        int redAlive = CountAlive(simulation, 2);
        int totalAlive = blueAlive + redAlive;

        _blue.DisplayedString = $"BLUE  {blueAlive}";
        _red.DisplayedString = $"RED  {redAlive}";

        string mode = simulation.AI.Enabled
            ? "MODE: AI ACTIVE"
            : "MODE: SCRIPTED VOLLEYS  |  [A] ENABLE AI";

        _details.DisplayedString =
            $"ALIVE {totalAlive}  |  FIRE {shotsPerSecond:0} rounds/s  |  HIT EVENTS {hitEventsPerSecond:0}/s\n" +
            $"EVENTS/ROUND {hitEventsPerRound:0.00}  |  TOTAL HIT EVENTS {simulation.Projectiles.TotalHits:N0}\n" +
            $"{mode}\n" +
            $"[T] TRACERS {(showTracers ? "ON" : "OFF")}  |  [F9] HIDE PANEL";

        _panel.Position = new Vector2f(12f, 12f);
        _blueAccent.Position = new Vector2f(12f, 40f);
        _redAccent.Position = new Vector2f(206f, 40f);
        _title.Position = new Vector2f(24f, 20f);
        _blue.Position = new Vector2f(26f, 42f);
        _red.Position = new Vector2f(220f, 42f);
        _details.Position = new Vector2f(24f, 64f);

        window.Draw(_panel);
        window.Draw(_blueAccent);
        window.Draw(_redAccent);
        window.Draw(_title);
        window.Draw(_blue);
        window.Draw(_red);
        window.Draw(_details);
    }

    private static int CountAlive(
        UnitSimulation simulation,
        ushort faction)
    {
        int alive = 0;
        ReadOnlySpan<int> active = simulation.Units.ActiveIndices;

        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            if (simulation.Units.FactionTag[unit] == faction &&
                simulation.Health.OverallHitPoints[unit] > 0f)
            {
                alive++;
            }
        }

        return alive;
    }

    private static Font? LoadFont()
    {
        string folder = Environment.GetFolderPath(
            Environment.SpecialFolder.Fonts);
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
        _details?.Dispose();
        _red?.Dispose();
        _blue?.Dispose();
        _title?.Dispose();
        _font?.Dispose();
        _panel.Dispose();
        _blueAccent.Dispose();
        _redAccent.Dispose();
    }
}
