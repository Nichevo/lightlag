using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;
using SimEngagement = Lightlag.Sim.Core.Engagement;

namespace Lightlag.Game;

/// <summary>
/// When things arrive: one row per fleet of yours in the fight, each with when your guns and missiles can reach, when
/// theirs can, the closest pass and where missiles land. Click a mark to pause the game there (click it again to cancel).
/// </summary>
public partial class BattleTimeline : Control
{
    /// <summary>One row per fleet, in tab order.</summary>
    public List<EngagementInfo> Rows = new();

    /// <summary>The fleet on the chosen tab (its row is lit; null: all fleets).</summary>
    public int? Lit;

    /// <summary>The game hour the game will pause at (null: none).</summary>
    public double? PauseAt;

    /// <summary>Clicked a mark: the game hour to pause at, or null to cancel.</summary>
    public Action<double?, string>? Picked;

    private Font _font = null!;
    private readonly List<(Vector2 At, double Hour, string Label)> _marks = new();

    public const float LabelW = 200, Top = 2, RowH = 22, Foot = 14;

    /// <summary>What the marks mean (the "?" beside the panel's title line).</summary>
    public const string Legend = "When things arrive, if nobody changes course. Each fleet's row, top lane to bottom: your guns, your missiles, their guns, their missiles. "
        + "Gold dashes: closest pass · dots: missiles land · green: you spot them. Click a mark to pause the game there (click it again to cancel).";

    /// <summary>The title line for the panel (drawn there, not on the timeline).</summary>
    public string Title => Rows.Count == 0 ? "When things arrive: no enemy in sight or on record"
        : Rows.All(r => r.InSight) ? "When things arrive, if nobody changes course · click a mark to pause there"
        : "When things arrive: GUESS (enemy out of sight, where it should be if it kept going)";
    public float WantHeight => Top + Math.Max(1, Rows.Count) * RowH + Foot;

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;
        MouseFilter = MouseFilterEnum.Stop;
    }

    private double Span => Rows.Count == 0 ? 1 : Rows.Max(r => r.LookAheadHours);
    private float X(double hours) => LabelW + (float)(Math.Clamp(hours / Span, 0, 1) * (Size.X - LabelW - 8));

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm)
        {
            var near = Nearest(mm.Position);
            TooltipText = near is { } n ? $"{n.Label}: click to pause there" : PauseAt != null ? "Click the pause mark to cancel it" : "";
        }
        if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb || Main.Instance.Sim is not Simulation sim) return;
        if (Nearest(mb.Position) is not { } m) return;
        var hour = sim.World.Hour + m.Hour;
        // Clicking the mark already set cancels it.
        if (PauseAt is double p && Math.Abs(p - hour) < Span / 200) Picked?.Invoke(null, m.Label);
        else Picked?.Invoke(hour, m.Label);
        AcceptEvent();
    }

    private (Vector2 At, double Hour, string Label)? Nearest(Vector2 p)
    {
        (Vector2, double, string)? best = null;
        var bestD = 12f;
        foreach (var m in _marks)
        {
            var d = m.At.DistanceTo(p);
            if (d < bestD) { bestD = d; best = m; }
        }
        return best;
    }

    private readonly System.Diagnostics.Stopwatch _drawClock = new();

    public override void _Draw()
    {
        _drawClock.Restart();
        try { DrawTimeline(); }
        finally { FrameTimes.AddDraw(_drawClock.Elapsed.TotalMilliseconds); }
    }

    private void DrawTimeline()
    {
        _marks.Clear();
        var now = Main.Instance.Sim?.World.Hour ?? 0;
        var span = Span;
        var right = Size.X - 8;
        if (Rows.Count == 0) return;
        var bottom = Top + Rows.Count * RowH;

        // Time ticks across all rows.
        for (var i = 0; i <= 4; i++)
        {
            var x = X(span * i / 4);
            DrawLine(new Vector2(x, Top - 2), new Vector2(x, bottom), new Color(Ui.Dim, 0.25f), 1);
            var text = i == 0 ? "now" : $"+{SimEngagement.Duration(span * i / 4)}";
            var w = _font.GetStringSize(text, HorizontalAlignment.Left, -1, 11).X;
            DrawString(_font, new Vector2(Math.Clamp(x - w / 2, LabelW, right - w), bottom + 13), text, HorizontalAlignment.Left, -1, 11, Ui.Dim);
        }

        for (var r = 0; r < Rows.Count; r++)
        {
            var e = Rows[r];
            var y = Top + r * RowH;
            var lit = Lit == null || Lit == e.Mine.Id;
            if (Lit == e.Mine.Id) DrawRect(new Rect2(0, y, right, RowH - 2), new Color(Ui.Player, 0.07f));
            // One line: the fleet, then the first thing that happens.
            var nameW = Math.Min(_font.GetStringSize(e.Mine.Name, HorizontalAlignment.Left, -1, 12).X, LabelW * 0.6f);
            DrawString(_font, new Vector2(4, y + 15), e.Mine.Name, HorizontalAlignment.Left, LabelW * 0.6f, 12, lit ? Ui.Player : new Color(Ui.Player, 0.6f));
            DrawString(_font, new Vector2(10 + nameW, y + 15), Summary(e), HorizontalAlignment.Left, LabelW - 16 - nameW, 10, Ui.Dim);

            var bar = new Rect2(LabelW, y + 2, right - LabelW, 18);
            DrawRect(bar, new Color(Ui.Dim, e.InSight ? 0.12f : 0.06f));
            if (!e.InSight) DrawRect(bar, new Color(Ui.Warn, 0.5f), false, 1);
            void Lane(TimeWindow? win, int lane, Color color, string what)
            {
                if (win == null) return;
                var x0 = X(win.From);
                var x1 = double.IsPositiveInfinity(win.To) ? right : X(win.To);
                DrawRect(new Rect2(x0, bar.Position.Y + 1 + lane * 4.25f, Math.Max(3, x1 - x0), 4), new Color(color, lane % 2 == 0 ? 0.9f : 0.55f));
                if (win.From > 1e-3) _marks.Add((new Vector2(x0, bar.Position.Y + 3 + lane * 4.25f), win.From, $"{e.Mine.Name}: {what} in {SimEngagement.Duration(win.From)}"));
            }
            if (e.WeHaveRailguns) Lane(e.OurRailguns, 0, Ui.Player, "your guns reach");
            if (e.WeHaveMissiles) Lane(e.OurMissiles, 1, Ui.Player, "your missiles reach");
            Lane(e.TheirRailguns, 2, Ui.Rival, "their guns reach");
            Lane(e.TheirMissiles, 3, Ui.Rival, "their missiles reach");
            var spot = new Color(0.62f, 0.92f, 0.62f);
            if (!e.InSight && e.SpotWindow != null)
            {
                var sx = X(e.SpotWindow.From);
                DrawLine(new Vector2(sx, bar.Position.Y - 2), new Vector2(sx, bar.End.Y + 2), spot, 2);
                _marks.Add((new Vector2(sx, bar.GetCenter().Y), e.SpotWindow.From, $"{e.Mine.Name}: you spot them in {SimEngagement.Duration(e.SpotWindow.From)}"));
            }
            if (e.ClosestInHours > 1e-3 && e.ClosestInHours <= span)
            {
                var cx = X(e.ClosestInHours);
                var gold = new Color(1f, 0.83f, 0.47f);
                DrawDashedLine(new Vector2(cx, bar.Position.Y - 2), new Vector2(cx, bar.End.Y + 2), gold, 1.5f, 3);
                _marks.Add((new Vector2(cx, bar.GetCenter().Y), e.ClosestInHours, $"{e.Mine.Name}: closest pass in {SimEngagement.Duration(e.ClosestInHours)} at {Format.Distance(e.ClosestGap)}"));
            }
            foreach (var sv in e.Salvos)
            {
                var at = new Vector2(X(sv.ImpactHours), bar.GetCenter().Y);
                DrawCircle(at, 4, sv.Ours ? Ui.Player : Ui.Danger);
                _marks.Add((at, sv.ImpactHours, $"{sv.Count} missiles {(sv.Ours ? "of yours land" : "land on you")} in {SimEngagement.Duration(sv.ImpactHours)}"));
            }
        }

        // The pause mark.
        if (PauseAt is double p && p > now)
        {
            var px = X(p - now);
            DrawLine(new Vector2(px, Top - 4), new Vector2(px, bottom + 2), Ui.Warn, 2);
            DrawString(_font, new Vector2(Math.Min(px + 4, right - 90), bottom + 12), "⏸ pauses here", HorizontalAlignment.Left, -1, 11, Ui.Warn);
            _marks.Add((new Vector2(px, Top), p - now, "Pause mark"));
        }
    }

    /// <summary>The row's second line: the first thing that happens.</summary>
    private static string Summary(EngagementInfo e)
    {
        string? Soon(TimeWindow? w) => w == null ? null : w.From < 1e-3 ? "now" : $"in {SimEngagement.Duration(w.From)}";
        if (e.WeHaveRailguns && Soon(e.OurRailguns) is string a) return $"guns {a}";
        if (e.WeHaveMissiles && Soon(e.OurMissiles) is string b) return $"missiles {b}";
        if (Soon(e.TheirRailguns) is string c) return $"their guns {c}";
        if (Soon(e.TheirMissiles) is string d) return $"their missiles {d}";
        return !e.WeHaveRailguns && !e.WeHaveMissiles ? "unarmed" : "nothing in reach";
    }
}
