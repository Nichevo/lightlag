using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The zone part of the fleet panel, for a fleet holding a zone: a Drift / Station-keep switch with three
/// tiles (fuel a day, how long it can hold, catch chance), then whether it preys on passing ships, how
/// hidden it is, when the next ship is due, what it has caught and how long the lane stays good. Under it,
/// the known lanes: the rival's runs you have spotted with how sure you are and the catch chance, and your
/// own runs with their risk. All numbers come from the simulation; changes go through commands.
/// </summary>
public partial class ZonePanel : VBoxContainer
{
    private readonly GameScreen _screen;
    private Fleet? _fleet;
    private Zone? _zone;
    private Button _drift = null!, _keep = null!, _prey = null!;
    private Label _fuel = null!, _days = null!, _catch = null!, _note = null!;
    private Label _hidden = null!, _due = null!, _caught = null!, _window = null!;
    private VBoxContainer _held = null!;
    private VBoxContainer _lanes = null!;
    private double _windowHour = -1;
    private string _windowText = "";

    public ZonePanel() : this(null!) { }
    public ZonePanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 4);
        _held = new VBoxContainer();
        _held.AddThemeConstantOverride("separation", 4);
        AddChild(_held);

        var toggle = new HBoxContainer();
        toggle.AddThemeConstantOverride("separation", 2);
        _drift = Ui.SmallButton("Drift", () => SetHold(ZoneHold.Drift), "Drift in the zone with small nudges: nearly no fuel, but the fleet wanders across it");
        _keep = Ui.SmallButton("Station-keep", () => SetHold(ZoneHold.StationKeep), "Hold still in the zone for a little fuel a day");
        foreach (var b in new[] { _drift, _keep })
        {
            b.ToggleMode = true;
            b.CustomMinimumSize = new Vector2(0, 26);
            b.AddThemeFontSizeOverride("font_size", 12);
            toggle.AddChild(b);
        }
        _held.AddChild(toggle);

        var tiles = new HBoxContainer();
        tiles.AddThemeConstantOverride("separation", 6);
        _fuel = Tile(tiles, "Fuel a day");
        _days = Tile(tiles, "Can hold");
        _catch = Tile(tiles, "Catch chance");
        _held.AddChild(tiles);
        _note = Line(_held);

        var preyRow = new HBoxContainer();
        preyRow.AddThemeConstantOverride("separation", 6);
        preyRow.AddChild(Caption("Prey on passing ships"));
        _prey = Ui.SmallButton("On", TogglePrey, "Go after enemy cargo and supply ships that come within reach");
        _prey.CustomMinimumSize = new Vector2(0, 24);
        _prey.AddThemeFontSizeOverride("font_size", 12);
        preyRow.AddChild(_prey);
        _held.AddChild(preyRow);
        _hidden = Fact(_held, "Hidden");
        _due = Fact(_held, "Next ship due");
        _caught = Fact(_held, "Caught so far");
        _window = Fact(_held, "Lane window");

        AddChild(Ui.Label("Known lanes", 13, Ui.Text));
        _lanes = new VBoxContainer();
        _lanes.AddThemeConstantOverride("separation", 2);
        AddChild(_lanes);
    }

    private static Label Tile(HBoxContainer row, string caption)
    {
        var box = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.04f), BorderColor = new Color(Ui.Player, 0.18f) };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(4);
        style.SetContentMarginAll(6);
        box.AddThemeStyleboxOverride("panel", style);
        box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 0);
        col.AddChild(Ui.Label(caption, 11, Ui.Dim));
        var value = Ui.Label("", 15, Ui.Text);
        col.AddChild(value);
        box.AddChild(col);
        row.AddChild(box);
        return value;
    }

    private static Label Caption(string text)
    {
        var l = Ui.Label(text, 12, Ui.Dim);
        l.CustomMinimumSize = new Vector2(130, 0);
        return l;
    }

    private static Label Fact(Container parent, string caption)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(Caption(caption));
        var value = Ui.Label("", 12, Ui.Text);
        value.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        value.CustomMinimumSize = new Vector2(200, 0);
        value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(value);
        parent.AddChild(row);
        return value;
    }

    private static Label Line(Container parent)
    {
        var l = Ui.Label("", 11, Ui.Dim);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(330, 0);
        parent.AddChild(l);
        return l;
    }

    private void SetHold(ZoneHold hold)
    {
        if (_zone is not Zone z) return;
        _screen.IssueNow(new EditZoneCommand(Side.Player, z.Id, z.Center, z.RadiusMm, z.FromBodyId, z.ToBodyId, z.T, hold, z.Prey), "zone hold");
    }

    private void TogglePrey()
    {
        if (_zone is not Zone z) return;
        _screen.IssueNow(new EditZoneCommand(Side.Player, z.Id, z.Center, z.RadiusMm, z.FromBodyId, z.ToBodyId, z.T, z.Hold, !z.Prey), "zone prey");
    }

    /// <summary>Show the panel for a fleet (the zone part only while it holds one; known lanes while any are known).</summary>
    public void Refresh(World w, Fleet f, Zone? zone)
    {
        _fleet = f;
        _zone = zone;
        _held.Visible = zone != null;
        if (zone != null) FillZone(w, f, zone);
        FillLanes(w, f);
        Visible = zone != null || _lanes.GetChildCount() > 0;
    }

    private void FillZone(World w, Fleet f, Zone z)
    {
        _drift.SetPressedNoSignal(z.Hold == ZoneHold.Drift);
        _keep.SetPressedNoSignal(z.Hold == ZoneHold.StationKeep);
        var ships = w.ShipsOf(f).ToList();
        var fuel = Lanes.FuelPerDay(w, f, z);
        _fuel.Text = z.Hold == ZoneHold.Drift ? "nudges only" : $"{fuel:0.#}";
        // How long it can stay: station keeping runs the fuel down; either way the food runs out (with supply lines on).
        double? days = null;
        if (z.Hold == ZoneHold.StationKeep && ships.Count > 0)
            days = ships.Min(s => s.Supplies / Math.Max(1e-9, Fitting.MaxSupplies(w, s) * Lanes.KeepStationPerDay));
        if (w.Rules.SupplyLines && ships.Count > 0)
        {
            var food = ships.Min(s => s.Food);
            days = days is double d ? Math.Min(d, food) : food;
        }
        _days.Text = days is double dd ? $"{dd:0} days" : "no limit";
        var (catches, due, chance) = Lanes.Info(w, z);
        _catch.Text = chance > 0 ? $"{chance:P0}" : "no lane";
        _note.Text = z.Hold == ZoneHold.Drift
            ? "Drifting: nearly no fuel, but the fleet wanders across the zone and may be out of place when a ship comes."
            : "Keeping station: holds still where you put it, for a little fuel a day.";
        _prey.Text = z.Prey ? "On · support ships only" : "Off";
        _prey.Modulate = z.Prey ? RaidTint : Colors.White;
        var state = Sensors.StateOf(f);
        var seen = Sensors.Range(Sensors.SensorOf(w, f), Sensors.Signature(w, f));
        _hidden.Text = state switch
        {
            SignatureState.Cold => $"Cold · seen only within {Format.Distance(seen)}",
            SignatureState.Warm => $"Warm · seen within {Format.Distance(seen)}",
            _ => $"Burning · seen from {Format.Distance(seen)}",
        };
        _hidden.AddThemeColorOverride("font_color", state == SignatureState.Cold ? Ui.Text : Ui.Rival);
        var lane = Lanes.LanesThrough(w, z).OrderByDescending(l => Lanes.Confidence(w, l)).ThenBy(l => l.Id).FirstOrDefault();
        _due.Text = due is double h ? $"about {Format.Hours(h)}{(lane != null ? $" ({LaneLooks.Sureness(lane)})" : "")}" : lane == null ? "no lane through the zone" : "not known yet";
        _caught.Text = catches == 0 ? "nothing yet" : $"{catches} ship{(catches == 1 ? "" : "s")}";
        if (z.FromBodyId is int a && z.ToBodyId is int b)
        {
            // Worked out once a game day: it looks two months ahead.
            if (Math.Abs(w.Hour - _windowHour) >= 24)
            {
                _windowHour = w.Hour;
                _windowText = LaneLooks.Window(w, a, b) is var (days2, why) ? $"Good for {days2} days, {why}" : "Good for the next 60 days";
            }
            _window.Text = _windowText;
        }
        else _window.Text = "Not on a lane: it stays where it is";
    }

    private static readonly Color RaidTint = new("ffb347");

    private void FillLanes(World w, Fleet f)
    {
        var rows = new List<(string Name, string Pill, string Right, Color Color, string Tip)>();
        foreach (var l in w.Lanes.Where(l => l.SeenBy == Side.Player).OrderByDescending(l => Lanes.Confidence(w, l)).ThenBy(l => l.Id))
        {
            var name = $"{w.FindBody(l.FromBodyId)?.Name ?? "?"} – {w.FindBody(l.ToBodyId)?.Name ?? "?"}";
            var tip = $"{LaneLooks.Sureness(l)}{LaneLooks.Often(w, l)} · last seen {GameTime.Format((long)l.LastHour)} · spread {Format.Distance(Lanes.WidthMm(w, l))}";
            var chance = Combat.Armed(w, f) ? $"catch ~{LaneLooks.CatchChance(w, f, l):P0}" : "";
            rows.Add((name, LaneLooks.Sureness(l), chance, Lanes.Confidence(w, l) < 0.25 ? Ui.Dim : RaidTint, tip));
        }
        foreach (var run in LaneLooks.OwnRuns(w))
        {
            var name = $"{w.FindBody(run.From)?.Name ?? "?"} – {w.FindBody(run.To)?.Name ?? "?"} (yours)";
            var risk = LaneLooks.Risk(w, run.From, run.To);
            rows.Add((name, $"{run.Ships} freighter{(run.Ships == 1 ? "" : "s")}", $"risk: {risk}", risk == "high" ? Ui.Danger : new Color("4aa8ff"),
                "Your cargo under way on this run. Risk is high after a loss near it lately, medium with armed enemies seen near it"));
        }
        // Rebuild only when the rows change.
        var key = string.Join("|", rows.Select(r => $"{r.Name}{r.Pill}{r.Right}"));
        if (_lanes.HasMeta("key") && (string)_lanes.GetMeta("key") == key) return;
        _lanes.SetMeta("key", key);
        foreach (var c in _lanes.GetChildren()) c.QueueFree();
        foreach (var (name, pill, right, color, tip) in rows)
        {
            var row = new HBoxContainer { TooltipText = tip, MouseFilter = MouseFilterEnum.Pass };
            row.AddThemeConstantOverride("separation", 6);
            var n = Ui.Label(name, 12, Ui.Text);
            n.CustomMinimumSize = new Vector2(150, 0);
            row.AddChild(n);
            row.AddChild(Pill(pill, color));
            var r = Ui.Label(right, 12, color);
            r.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            r.HorizontalAlignment = HorizontalAlignment.Right;
            row.AddChild(r);
            _lanes.AddChild(row);
        }
    }

    private static Control Pill(string text, Color color)
    {
        var p = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = new Color(color, 0.16f), BorderColor = new Color(color, 0.5f) };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(8);
        style.ContentMarginLeft = style.ContentMarginRight = 6;
        style.ContentMarginTop = style.ContentMarginBottom = 0;
        p.AddThemeStyleboxOverride("panel", style);
        p.AddChild(Ui.Label(text, 11, color));
        return p;
    }
}
