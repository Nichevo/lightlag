using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The control-share bar on the HUD: your share from the left, the rival's (as far as you can tell) from
/// the right, the unsure middle grey, and a mark at the tip share (80 %). Hover for the four parts.
/// </summary>
public partial class ControlBar : Control
{
    private double _mine, _theirs, _plus, _tip = 0.8;
    private bool _tipped;

    public ControlBar()
    {
        CustomMinimumSize = new Vector2(240, 32);
        MouseFilter = MouseFilterEnum.Stop;
    }

    public void Set(World w)
    {
        var c = Planets.Control(w, Side.Player);
        var (theirs, plus) = Intel.EstimatedControl(w, Side.Player);
        _mine = c.Share;
        _theirs = theirs;
        _plus = plus;
        _tip = Planets.D.TipShare;
        _tipped = Planets.Tipped(w, Side.Player);
        TooltipText = $"Control: you {c.Share:P0}, the rival about {theirs:P0} ± {plus * 100:0} (your intel level sets how sure)\n"
                      + $"People {c.People:P0} · Industry {c.Industry:P0} · bodies {c.Bodies:P0} · fleets {c.Fleets:P0} (your share of each)\n"
                      + $"At {_tip:P0} a side tips the game: blockaded planets give up sooner; hold it {Planets.D.WinAfterDays:0} days or reach {Planets.D.WinShare:P0} to win"
                      + (_tipped ? "\nYOU HAVE TIPPED THE GAME" : "");
        QueueRedraw();
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        var w = Size.X;
        var y = 16f;
        var h = 10f;
        DrawRect(new Rect2(0, y, w, h), new Color(1, 1, 1, 0.08f));
        var mine = (float)Math.Clamp(_mine, 0, 1) * w;
        var theirs = (float)Math.Clamp(_theirs - _plus, 0, 1) * w;
        DrawRect(new Rect2(0, y, mine, h), Ui.Player);
        DrawRect(new Rect2(w - theirs, y, theirs, h), new Color(Ui.Rival, 0.85f));
        var tipX = (float)_tip * w;
        DrawLine(new Vector2(tipX, y - 3), new Vector2(tipX, y + h + 3), Ui.Text, 1.5f);
        DrawLine(new Vector2(w - tipX, y - 3), new Vector2(w - tipX, y + h + 3), new Color(Ui.Rival, 0.8f), 1);
        DrawString(font, new Vector2(0, 12), $"Control {_mine:P0}{(_tipped ? " TIPPED" : "")}", HorizontalAlignment.Left, -1, 12, Ui.Player);
        DrawString(font, new Vector2(0, 12), $"rival ~{_theirs:P0}", HorizontalAlignment.Right, w, 12, Ui.Rival);
    }
}

/// <summary>One ship: its five bars, crew, fit (with a link to the design) and history. Opened from ⓘ on a ship row.</summary>
public partial class ShipCard : PanelContainer
{
    private readonly GameScreen _screen;
    private Label _title = null!, _text = null!;
    private WoundBars _bars = null!;
    private Button _design = null!;
    private HBoxContainer _renameRow = null!;
    private LineEdit _rename = null!;
    private Label _renameProblem = null!;
    private int? _fleetId, _shipId;
    private Simulation? _sim;

    public ShipCard() : this(null!) { }
    public ShipCard(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 14);
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(340, 0) };
        v.AddThemeConstantOverride("separation", 6);
        var head = new HBoxContainer();
        _title = Ui.Label("", 17, Ui.Player);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(_title);
        head.AddChild(Ui.SmallButton("✕", () => _screen.ShipCardId = null, "Close (Esc)"));
        v.AddChild(head);
        _bars = new WoundBars();
        v.AddChild(_bars);
        _text = Ui.Label("", 13, Ui.Text);
        _text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _text.CustomMinimumSize = new Vector2(340, 0);
        v.AddChild(_text);
        _design = Ui.SmallButton("Fit and design ▸", () => { if (_fleetId != null) _screen.FittingFleetId = _fleetId; _screen.ShipCardId = null; },
            "Open the fleet's ships and fittings: swap modules at your shipyard, or save the fit as the class design");
        v.AddChild(_design);
        _renameRow = new HBoxContainer();
        _rename = new LineEdit { PlaceholderText = "New name (Enter)", MaxLength = ShipNames.MaxLength, CustomMinimumSize = new Vector2(200, 28) };
        _rename.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _rename.TextSubmitted += _ => Rename();
        _renameRow.AddChild(_rename);
        _renameRow.AddChild(Ui.SmallButton("Rename", Rename, "Give the ship a new name (works while paused)"));
        v.AddChild(_renameRow);
        _renameProblem = Ui.Label("", 12, Ui.Rival);
        v.AddChild(_renameProblem);
        AddChild(v);
    }

    private void Rename()
    {
        if (_sim == null || _shipId is not int id || _sim.World.FindShip(id) is not Ship s) return;
        var name = _rename.Text.Trim();
        if (ShipNames.Problem(_sim.World, s, name) is string problem) { _renameProblem.Text = problem; return; }
        _screen.IssueNow(new RenameShipCommand(Side.Player, id, name), $"Rename {s.Name} to {name}");
        _rename.Text = "";
        _renameProblem.Text = "";
        _rename.ReleaseFocus();
    }

    public void Refresh(Simulation sim, Ship s)
    {
        var w = sim.World;
        var mine = s.Side == Side.Player;
        var cls = Fitting.Spec(s);
        var fleet = w.FleetOf(s);
        _fleetId = fleet?.Id;
        if (_shipId != s.Id) { _rename.Text = ""; _renameProblem.Text = ""; }
        _shipId = s.Id;
        _sim = sim;
        _title.Text = $"{s.Name} · {s.Code} · {cls.Name}" + (mine ? "" : "  (rival)");
        _title.AddThemeColorOverride("font_color", mine ? Ui.Player : Ui.Rival);
        _bars.Set(w, new[] { s });
        var bars = WoundBars.Of(w, s);
        var lines = new List<string>();
        if (s.Crippled) lines.Add("CRIPPLED: limping home, it can't fight until repaired");
        lines.Add($"Hull {bars[0]:P0} · weapons {bars[1]:P0} · defence {bars[3]:P0} · crew {bars[4]:P0}");
        lines.Add($"Engines {bars[2]:P0}" + (bars[2] < 0.05 ? ": out, a quarter speed at best" : bars[2] < 0.5 ? ": slowed" : ""));
        if (s.Knocked.Count > 0) lines.Add("Knocked out: " + string.Join(", ", s.Knocked.Select(Combat.ModuleName)));
        lines.Add("Crew: " + (mine || _screen.RevealAll ? ShipsScreen.CrewText(s) + (Crews.WornOut(s) ? " (worn out: worse aim, slower repairs and dodges; rest in port)" : "") : "unknown"));
        if (mine && Designer.Find(w, s.Side, s.DesignId) is Design d) lines.Add($"Variant: {d.Name}");
        if (mine || _screen.RevealAll)
        {
            var fit = Fitting.FitOf(s).Select(id => GameData.Module(id)?.Name).Where(n => n != null).GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
            lines.Add("Fit: " + string.Join(", ", fit) + (mine && Refits.Outdated(w, s) ? " (behind the current design)" : ""));
            lines.Add($"Fuel {s.Supplies / Math.Max(1e-9, Fitting.MaxSupplies(w, s)):P0}"
                      + (SupplyLines.MaxAmmo(s) > 0 ? $" · railgun ammo {s.Ammo / SupplyLines.MaxAmmo(s):P0}" : "")
                      + (SupplyLines.MaxFood(s) > 0 ? $" · food {s.Food:0} d" : "")
                      + (cls.Missiles > 0 ? (s.Missiles <= 0 ? " · OUT OF MISSILES" : $" · missiles {s.Missiles}/{Tech.Racks(w, s)}") : "")
                      + (Heat.Capacity(s) > 0 ? $" · heat {s.Heat / Heat.Capacity(s):P0}" : ""));
            var built = s.BuiltHour is double h
                ? $"Built {GameTime.Format(h)}" + (s.BuiltAtBodyId is int b && w.FindBody(b) is Body yard ? $" at {yard.Name}" : "")
                : "With the fleet since the start";
            lines.Add($"{built} · {s.Battles} battle{(s.Battles == 1 ? "" : "s")}" + (s.Battles > 0 ? $", {s.EnemyLostInBattles} enemy ships lost in them" : ""));
        }
        if (fleet != null) lines.Add($"In {fleet.Name}" + (Light.Flagship(w, s.Side)?.Id == s.Id ? " · FLAGSHIP" : ""));
        _text.Text = string.Join("\n", lines);
        _design.Visible = mine && fleet != null;
        _renameRow.Visible = mine;
        _renameProblem.Visible = mine && _renameProblem.Text.Length > 0;
    }
}

/// <summary>Every key on one overlay, grouped (F1).</summary>
public partial class KeyHelp : PanelContainer
{
    private readonly GameScreen _screen;

    public KeyHelp() : this(null!) { }
    public KeyHelp(GameScreen screen) => _screen = screen;

    private static readonly (string Group, string[] Keys)[] Groups =
    {
        ("Time", new[] { "Space  pause", "1-4  speed", "+ / -  faster, slower", "P  pause when enemies are spotted" }),
        ("Map", new[] { "Wheel  zoom", "WASD, arrows or right-drag  pan", "H or Home  whole system", "Z  zoom in on what is selected", "F  follow (Shift+F: without zooming)",
            "Double-click  open your planet, follow anything else", "L  fleets and planets list", "B  sight shadows, coverage, off", "Map layers ▾  what the map shows" }),
        ("Fleets", new[] { "Click  select · drag a box or Shift+click  several", "Tab / Shift+Tab  next fleet", "Right-click  route or attack (Shift: go now)",
            "Ctrl+right-click  add a waypoint", "Enter  go", "C  change route", "O  orbit · G  drift · X  stop", "M  merge or gather", "V  engagement view", "R  last battle report" }),
        ("Screens", new[] { "E  empire (Needs you is a tab)", "T  research", "Y  planets", "K  fleets: roles, ships, supplies, calls for help", "J  ships: designs, crewing, class tree", "I  intelligence", "N  how am I doing", "F1  this list", "Esc  close, or the game menu" }),
        ("Saves and reports", new[] { "F5  quick save", "F9  quick load", "F8  send a report", "F7  debug: show every fleet and the rival's mind" }),
        ("Display", new[] { "F11 or Alt+Enter  full screen on or off", "Esc  game menu: window size and interface size" }),
    };

    public override void _Ready()
    {
        Ui.StylePanel(this, 20);
        SetAnchorsAndOffsetsPreset(LayoutPreset.Center, LayoutPresetMode.KeepSize);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 10);
        var head = new HBoxContainer();
        var title = Ui.Label("Keys", 22, Ui.Player);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(title);
        head.AddChild(Ui.SmallButton("✕", () => _screen.ShowHelp = false, "Close (F1 or Esc)"));
        v.AddChild(head);
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 28);
        grid.AddThemeConstantOverride("v_separation", 14);
        foreach (var (group, keys) in Groups)
        {
            var col = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkBegin };
            col.AddThemeConstantOverride("separation", 3);
            col.AddChild(Ui.Label(group.ToUpperInvariant(), 12, Ui.Dim));
            foreach (var k in keys) col.AddChild(Ui.Label(k, 14, Ui.Text));
            grid.AddChild(col);
        }
        v.AddChild(grid);
        v.AddChild(Ui.Label("Orders work while paused. Hover anything for the numbers and the reasons.", 12, Ui.Dim));
        AddChild(v);
    }
}

/// <summary>
/// "How am I doing?": control share over time (with the 80 % line and the battles that moved it), Industry,
/// fleet strength, research and losses, from the daily statistics. The rival's line is dashed: it is your
/// estimate, as good as your intel level.
/// </summary>
public partial class ProgressScreen : PanelContainer
{
    private readonly GameScreen _screen;
    private ProgressChart _chart = null!;
    private Label _note = null!;
    private readonly List<Button> _tabs = new();

    public static readonly string[] Metrics = { "Control", "Industry", "Fleet strength", "Research", "Losses" };

    public ProgressScreen() : this(null!) { }
    public ProgressScreen(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 18);
        SetAnchorsAndOffsetsPreset(LayoutPreset.Center, LayoutPresetMode.KeepSize);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);
        var head = new HBoxContainer();
        var title = Ui.Label("How am I doing?", 22, Ui.Player);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(title);
        head.AddChild(Ui.SmallButton("✕", () => _screen.ShowProgress = false, "Close (N or Esc)"));
        v.AddChild(head);
        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 4);
        for (var i = 0; i < Metrics.Length; i++)
        {
            var index = i;
            var b = Ui.SmallButton(Metrics[i], () => _chart.Metric = index, "");
            _tabs.Add(b);
            tabs.AddChild(b);
        }
        v.AddChild(tabs);
        _chart = new ProgressChart { CustomMinimumSize = new Vector2(760, 340) };
        v.AddChild(_chart);
        _note = Ui.Label("", 12, Ui.Dim);
        _note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _note.CustomMinimumSize = new Vector2(760, 0);
        v.AddChild(_note);
        AddChild(v);
    }

    public void Refresh(Simulation sim)
    {
        _chart.World = sim.World;
        for (var i = 0; i < _tabs.Count; i++) _tabs[i].Modulate = i == _chart.Metric ? Ui.Player : new Color(1, 1, 1, 0.7f);
        _note.Text = _chart.Metric switch
        {
            0 => "Your share of control (solid) against the rival's as you can tell it (dashed), with the 80 % tip line. Ticks along the bottom are battles you fought.",
            1 => "Your Industry a day (solid) against the Industry of the planets the rival is known to hold (dashed estimate).",
            2 => "Your ships (solid) against the rival warships you believe exist (dashed: the middle of your estimate; the band shows its range).",
            3 => "Ideas you have researched. The rival's research shows only as the technology you have seen (Intelligence, I).",
            _ => "Ships you have lost (solid) and rival ships you have destroyed (dashed), over the game.",
        };
        _chart.QueueRedraw();
    }
}

public partial class ProgressChart : Control
{
    public World? World;
    public int Metric;

    public override void _Draw()
    {
        if (World is not World w) return;
        var font = ThemeDB.FallbackFont;
        var rect = new Rect2(44, 8, Size.X - 56, Size.Y - 36);
        DrawRect(rect, new Color(1, 1, 1, 0.03f));
        var mine = w.History.Where(r => r.Side == Side.Player).ToList();
        if (mine.Count < 2)
        {
            DrawString(font, rect.Position + new Vector2(12, 24), "Not enough days yet: the chart fills in as the game goes on", HorizontalAlignment.Left, -1, 14, Ui.Dim);
            return;
        }
        var est = Intel.Of(w, Side.Player).Days;
        var firstDay = mine[0].Day;
        var lastDay = Math.Max(firstDay + 1, mine[^1].Day);
        List<(int Day, double V)> a, b = new();
        List<(int Day, double Low, double High)> band = new();
        switch (Metric)
        {
            case 0:
                a = mine.Select(r => (r.Day, r.Control)).ToList();
                b = mine.Select(r => (r.Day, 1 - r.Control)).ToList();
                break;
            case 1:
                a = mine.Select(r => (r.Day, r.Income)).ToList();
                b = est.Select(d => (d.Day, d.Industry)).ToList();
                break;
            case 2:
                a = mine.Select(r => (r.Day, (double)r.Ships)).ToList();
                b = est.Select(d => (d.Day, (d.WarshipsLow + d.WarshipsHigh) / 2.0)).ToList();
                band = est.Select(d => (d.Day, (double)d.WarshipsLow, (double)d.WarshipsHigh)).ToList();
                break;
            case 3:
                a = mine.Select(r => (r.Day, (double)r.Researched)).ToList();
                break;
            default:
                a = mine.Select(r => (r.Day, (double)r.ShipsLost)).ToList();
                b = mine.Select(r => (r.Day, (double)r.ShipsKilled)).ToList();
                break;
        }
        var max = Metric == 0 ? 1 : Math.Max(1, a.Concat(b).Select(p => p.V).Concat(band.Select(p => p.High)).DefaultIfEmpty(1).Max() * 1.1);
        Vector2 P(int day, double v) => new(rect.Position.X + (float)(day - firstDay) / (lastDay - firstDay) * rect.Size.X,
            rect.End.Y - (float)(v / max) * rect.Size.Y);

        // Grid and labels.
        for (var i = 0; i <= 4; i++)
        {
            var v = max * i / 4;
            var y = P(firstDay, v).Y;
            DrawLine(new Vector2(rect.Position.X, y), new Vector2(rect.End.X, y), new Color(1, 1, 1, 0.06f), 1);
            DrawString(font, new Vector2(0, y + 4), Metric == 0 ? $"{v:P0}" : $"{v:0.#}", HorizontalAlignment.Left, 40, 11, Ui.Dim);
        }
        DrawString(font, new Vector2(rect.Position.X, rect.End.Y + 18), $"day {firstDay}", HorizontalAlignment.Left, -1, 11, Ui.Dim);
        DrawString(font, new Vector2(rect.End.X - 60, rect.End.Y + 18), $"day {lastDay}", HorizontalAlignment.Left, -1, 11, Ui.Dim);
        if (Metric == 0)
        {
            var tip = P(firstDay, Planets.D.TipShare).Y;
            DrawDashedLine(new Vector2(rect.Position.X, tip), new Vector2(rect.End.X, tip), new Color(Ui.Text, 0.5f), 1, 4);
            DrawString(font, new Vector2(rect.End.X - 70, tip - 4), "80 % tips it", HorizontalAlignment.Left, -1, 11, Ui.Dim);
        }

        // Band of the estimate, then the lines.
        for (var i = 1; i < band.Count; i++)
        {
            var pts = new[] { P(band[i - 1].Day, band[i - 1].Low), P(band[i].Day, band[i].Low), P(band[i].Day, band[i].High), P(band[i - 1].Day, band[i - 1].High) };
            DrawColoredPolygon(pts, new Color(Ui.Rival, 0.08f));
        }
        for (var i = 1; i < a.Count; i++) DrawLine(P(a[i - 1].Day, a[i - 1].V), P(a[i].Day, a[i].V), Ui.Player, 2, true);
        for (var i = 1; i < b.Count; i++) DrawDashedLine(P(b[i - 1].Day, b[i - 1].V), P(b[i].Day, b[i].V), Ui.Rival, 2, 5);

        // The moments that moved it: battles you fought.
        foreach (var battle in w.Battles.Where(x => x.For(Side.Player).ShipsIn > 0 && x.For(Side.Rival).ShipsIn > 0))
        {
            var day = GameTime.DayOf((long)(battle.StartHour / GameTime.HoursPerTick));
            if (day < firstDay || day > lastDay) continue;
            var x = P(day, 0).X;
            var lost = battle.For(Side.Player).ShipsLost.Count > battle.For(Side.Rival).ShipsLost.Count;
            DrawLine(new Vector2(x, rect.End.Y), new Vector2(x, rect.End.Y - 8), lost ? Ui.Danger : new Color("7fd67f"), 2);
        }
    }
}
