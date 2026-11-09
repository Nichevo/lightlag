using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The selected fleet: what it is doing, its supplies, its ships (tick some to split them off) and
/// buttons for orbit, drift, stop, split and merge. Rival fleets show the same facts without buttons.
/// </summary>
public partial class FleetPanel : PanelContainer
{
    private readonly GameScreen _screen;
    private Label _title = null!;
    private Label _status = null!;
    private VBoxContainer _shipList = null!;
    private HBoxContainer _buttons = null!;
    private Button _split = null!;
    private Button _merge = null!;
    private Button _orbit = null!;
    private Button _drift = null!;
    private Button _stop = null!;
    private Button _engage = null!;
    private Button _hold = null!;
    private CheckBox _retreat = null!;
    private HBoxContainer _orders = null!;
    private readonly Dictionary<int, (CheckBox Box, Label Supplies)> _rows = new();
    private string _shipKey = "";

    public FleetPanel() : this(null!) { }
    public FleetPanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 14);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(330, 0) };
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        _title = Ui.Label("", 17);
        box.AddChild(_title);
        _status = Ui.Label("", 14, Ui.Dim);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.CustomMinimumSize = new Vector2(330, 0);
        box.AddChild(_status);
        _shipList = new VBoxContainer();
        box.AddChild(_shipList);
        _buttons = new HBoxContainer();
        _buttons.AddThemeConstantOverride("separation", 4);
        _orbit = Ui.SmallButton("Orbit", () => _screen.OrderHold(HoldStyle.Orbit), "Orbit the nearest planet or moon (O)");
        _drift = Ui.SmallButton("Drift", () => _screen.OrderHold(HoldStyle.Drift), "Engines cold: stay put (D)");
        _stop = Ui.SmallButton("Stop", () => _screen.OrderStop(), "Cancel the flight and brake to a stop (S)");
        _split = Ui.SmallButton("Split", () => _screen.OrderSplit(Checked()), "Make the ticked ships a new fleet");
        _merge = Ui.SmallButton("Merge", () => _screen.OrderMerge(), "Join the nearest of your fleets into this one");
        foreach (var b in new[] { _orbit, _drift, _stop, _split, _merge }) _buttons.AddChild(b);
        box.AddChild(_buttons);

        // Standing orders: what the fleet does when enemies come into contact.
        _orders = new HBoxContainer();
        _orders.AddThemeConstantOverride("separation", 4);
        var label = Ui.Label("In contact", 13, Ui.Dim);
        label.CustomMinimumSize = new Vector2(70, 0);
        _orders.AddChild(label);
        _engage = Ui.SmallButton("Engage", () => SetStance(Stance.Engage, null), "Close to railgun range and fire");
        _hold = Ui.SmallButton("Hold", () => SetStance(Stance.Hold, null), "Keep course; fire at whatever comes in range");
        _retreat = new CheckBox { Text = "Retreat when damaged", FocusMode = FocusModeEnum.None, TooltipText = "Fly home when the fleet's armour falls below half" };
        _retreat.AddThemeFontSizeOverride("font_size", 13);
        _retreat.Pressed += () => SetStance(null, _retreat.ButtonPressed);
        _orders.AddChild(_engage);
        _orders.AddChild(_hold);
        _orders.AddChild(_retreat);
        box.AddChild(_orders);
    }

    private Fleet? _fleet;

    private void SetStance(Stance? stance, bool? retreat)
    {
        if (_fleet == null) return;
        _screen.OrderStance(stance ?? _fleet.Stance, retreat ?? _fleet.RetreatWhenDamaged);
    }

    private List<int> Checked() => _rows.Where(r => r.Value.Box.ButtonPressed).Select(r => r.Key).ToList();

    public void Refresh(Simulation sim, Fleet f)
    {
        var w = sim.World;
        _fleet = f;
        var mine = f.Side == Side.Player;
        var ships = w.ShipsOf(f).ToList();
        var perf = FleetPerformance.Of(w, f);
        _title.Text = f.Name + (mine ? "" : "  (rival)");
        _title.AddThemeColorOverride("font_color", mine ? Ui.Player : Ui.Rival);
        _status.Text = string.Join("\n", new[]
        {
            Doing(w, f),
            $"Top speed {Format.Speed(perf.MaxSpeed)} (slowest ship) · fuel for {Format.Speed(perf.DeltaVLeft)} of speed change",
        });

        // Rebuild the ship rows only when the fleet's ships change.
        var key = string.Join(",", f.ShipIds) + (mine ? "m" : "r");
        if (key != _shipKey)
        {
            _shipKey = key;
            foreach (var c in _shipList.GetChildren()) c.QueueFree();
            _rows.Clear();
            foreach (var s in ships)
            {
                var row = new HBoxContainer();
                var check = new CheckBox { Text = $"{s.Name}  {GameData.ShipClass(s.ClassId).Name}", FocusMode = FocusModeEnum.None, Disabled = !mine };
                check.AddThemeFontSizeOverride("font_size", 14);
                check.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(check);
                var supplies = Ui.Label("", 13, Ui.Dim);
                row.AddChild(supplies);
                _shipList.AddChild(row);
                _rows[s.Id] = (check, supplies);
            }
        }
        foreach (var s in ships)
        {
            if (!_rows.TryGetValue(s.Id, out var row)) continue;
            var cls = GameData.ShipClass(s.ClassId);
            var share = s.Supplies / cls.Supplies;
            var armour = s.Armour / cls.Armour;
            var bits = new List<string> { $"armour {armour:P0}", $"supplies {share:P0}" };
            if (cls.Missiles > 0) bits.Add($"{s.Missiles} missiles");
            if (s.Disabled is Module m) bits.Add($"{Combat.ModuleName(m)} out");
            row.Supplies.Text = string.Join(" · ", bits);
            row.Supplies.AddThemeColorOverride("font_color", share < 0.2 || armour < 0.5 || s.Disabled != null ? Ui.Danger : Ui.Dim);
        }

        _buttons.Visible = mine;
        _orders.Visible = mine;
        _engage.Modulate = f.Stance == Stance.Engage ? Ui.Player : new Color(1, 1, 1, 0.7f);
        _hold.Modulate = f.Stance == Stance.Hold ? Ui.Player : new Color(1, 1, 1, 0.7f);
        _retreat.SetPressedNoSignal(f.RetreatWhenDamaged);
        var flying = f.Flight is { Started: true } || f.Velocity.Length > 1e-9;
        var picked = Checked().Count;
        _orbit.Disabled = flying || (f.Hold == HoldStyle.Orbit && f.Flight == null);
        _drift.Disabled = flying || (f.Hold == HoldStyle.Drift && f.Flight == null);
        _stop.Disabled = f.Flight == null && f.Velocity.Length < 1e-9;
        _split.Disabled = picked == 0 || picked == ships.Count;
        var candidate = _screen.MergeCandidate();
        _merge.Disabled = candidate == null;
        _merge.TooltipText = candidate != null ? $"Join {candidate.Name} into this fleet" : "No other fleet of yours is close by and stopped";
    }

    public static string Doing(World w, Fleet f)
    {
        string? Name(int? id) => id is int b ? w.FindBody(b)?.Name : null;
        if (f.Retreating && f.Flight != null) return $"Retreating home, arrives {GameTime.Format(f.Flight.ArriveHour)}";
        if (f.Maneuvering)
            return Combat.NearestEnemyFleet(w, f, GameData.Combat.ContactRangeMm) is Fleet e
                ? $"Closing on {e.Name}, {Vec2.Distance(e.Position, f.Position) * 1000:#,0} km away"
                : $"Braking after battle at {Format.Speed(f.Velocity.Length)}";
        if (f.Flight is Flight fl)
        {
            var to = Name(fl.TargetBodyId) ?? (fl.TargetFleetId is int tf ? w.FindFleet(tf)?.Name : null) ?? "a point in space";
            var verb = fl.Arrival == ArrivalStyle.FastPass ? "passes" : "reaches";
            var arrive = GameTime.Format(fl.ArriveHour);
            if (!fl.Started)
                return $"Leaves for {to} on {GameTime.Format(fl.DepartHour)}, {verb} it {arrive}";
            if (fl.TargetBodyId == null && fl.Legs.Count == 1) return $"Braking to a stop at {Format.Speed(f.Velocity.Length)}";
            return $"Flying to {to} at {Format.Speed(f.Velocity.Length)}, {verb} it {arrive}";
        }
        if (f.Hold == HoldStyle.Orbit && Name(f.OrbitBodyId) is string body)
            return $"In orbit round {body}" + (w.FindBody(f.OrbitBodyId!.Value)?.Owner == f.Side ? " (home: refilling)" : "");
        return f.Velocity.Length > 1e-9 ? $"Drifting on at {Format.Speed(f.Velocity.Length)}" : "Drifting, engines cold";
    }
}

/// <summary>
/// The route planner: pick Direct or Wait, how fast to fly (faster burns more fuel), and how to
/// arrive, and see when the fleet leaves and arrives and what it costs before sending it.
/// </summary>
public partial class PlannerPanel : PanelContainer
{
    private readonly GameScreen _screen;
    private Label _title = null!;
    private Button _direct = null!, _wait = null!, _brake = null!, _pass = null!, _orbit = null!, _drift = null!, _go = null!;
    private HSlider _speed = null!;
    private Label _speedLabel = null!;
    private Label _result = null!;
    private bool _updating;

    public PlannerPanel() : this(null!) { }
    public PlannerPanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 14);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0) };
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        _title = Ui.Label("", 17, Ui.Player);
        box.AddChild(_title);

        var s = _screen.Settings;
        _direct = Choice("Direct", "Leave now", () => s.Route = RouteKind.Direct);
        _wait = Choice("Wait", "Leave when the trip is shortest (within 120 days)", () => s.Route = RouteKind.Wait);
        box.AddChild(Row("Route", _direct, _wait));

        _speed = new HSlider
        {
            MinValue = FlightPlanner.MinSpeedFraction * 100, MaxValue = 100, Step = 5,
            CustomMinimumSize = new Vector2(200, 24), FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Faster arrives sooner but burns more fuel",
        };
        _speed.ValueChanged += v =>
        {
            if (_updating) return;
            s.Speed = v / 100;
            _screen.Replan();
        };
        _speedLabel = Ui.Label("", 13, Ui.Dim);
        _speedLabel.CustomMinimumSize = new Vector2(110, 0);
        var speedRow = new HBoxContainer();
        speedRow.AddChild(RowLabel("Speed"));
        speedRow.AddChild(_speed);
        speedRow.AddChild(_speedLabel);
        box.AddChild(speedRow);

        _brake = Choice("Brake", "Stop at the target", () => s.Arrival = ArrivalStyle.Brake);
        _pass = Choice("Fast pass", "Fly through at speed: quicker and half the fuel, but no stop", () => s.Arrival = ArrivalStyle.FastPass);
        box.AddChild(Row("Arrive", _brake, _pass));
        _orbit = Choice("Orbit", "Circle the planet or moon on arrival", () => s.Hold = HoldStyle.Orbit);
        _drift = Choice("Drift", "Sit with engines cold on arrival", () => s.Hold = HoldStyle.Drift);
        box.AddChild(Row("Then", _orbit, _drift));

        _result = Ui.Label("", 14);
        _result.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _result.CustomMinimumSize = new Vector2(360, 0);
        box.AddChild(_result);

        _go = Ui.SmallButton("Go", () => _screen.ConfirmPlan(), "Fly this route (Enter)");
        _go.CustomMinimumSize = new Vector2(90, 32);
        var cancel = Ui.SmallButton("Cancel", () => _screen.CancelPlanning(), "Close the planner (Esc)");
        cancel.CustomMinimumSize = new Vector2(90, 32);
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 6);
        actions.AddChild(_go);
        actions.AddChild(cancel);
        box.AddChild(actions);
    }

    private Button Choice(string text, string tip, Action set)
    {
        var b = Ui.SmallButton(text, () => { set(); _screen.Replan(); }, tip);
        b.CustomMinimumSize = new Vector2(96, 30);
        return b;
    }

    private static Label RowLabel(string text)
    {
        var l = Ui.Label(text, 14, Ui.Dim);
        l.CustomMinimumSize = new Vector2(60, 0);
        return l;
    }

    private static HBoxContainer Row(string label, params Button[] buttons)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        row.AddChild(RowLabel(label));
        foreach (var b in buttons) row.AddChild(b);
        return row;
    }

    private static void Mark(Button b, bool on, bool enabled = true)
    {
        b.Disabled = !enabled;
        b.Modulate = on ? Ui.Player : new Color(1, 1, 1, 0.7f);
    }

    public void Refresh(Simulation sim, PlannerState p)
    {
        var s = _screen.Settings;
        var w = sim.World;
        var fleet = w.FindFleet(p.FleetId);
        _title.Text = $"Route: {fleet?.Name} to {p.TargetName}";
        _title.AddThemeColorOverride("font_color", p.TargetFleetId != null ? Ui.Danger : Ui.Player);
        Mark(_direct, s.Route == RouteKind.Direct);
        Mark(_wait, s.Route == RouteKind.Wait);
        Mark(_brake, s.Arrival == ArrivalStyle.Brake);
        Mark(_pass, s.Arrival == ArrivalStyle.FastPass);
        var canOrbit = s.Arrival == ArrivalStyle.Brake && p.TargetBodyId != null;
        if (p.TargetFleetId != null) _title.Text = $"Attack: {fleet?.Name} on {p.TargetName}";
        Mark(_orbit, canOrbit && s.Hold == HoldStyle.Orbit, canOrbit);
        Mark(_drift, !canOrbit || s.Hold == HoldStyle.Drift, canOrbit);
        _updating = true;
        _speed.Value = s.Speed * 100;
        _updating = false;
        var perf = fleet != null ? FleetPerformance.Of(w, fleet) : default;
        _speedLabel.Text = $"{s.Speed:P0} · {Format.Speed(perf.MaxSpeed * s.Speed)}";

        var plan = p.Plan;
        _go.Disabled = plan is not { Ok: true };
        if (plan?.Flight is not Flight f)
        {
            _result.Text = plan?.Problem ?? "Working it out...";
            _result.AddThemeColorOverride("font_color", Ui.Danger);
            return;
        }
        var now = w.Hour;
        var waitDays = (f.DepartHour - now) / 24;
        var lines = new List<string>
        {
            waitDays < 0.05 ? "Leave: now" : $"Leave: {GameTime.Format(f.DepartHour)} (in {Format.Hours(f.DepartHour - now)})",
            $"Trip: {Format.Hours(f.ArriveHour - f.DepartHour)} · arrive {GameTime.Format(f.ArriveHour)}",
            $"Fuel: {Format.Speed(plan.DeltaV)} of speed change, {plan.FuelShare:P0} of what the fleet has",
        };
        if (!plan.Ok) lines.Add(plan.Problem ?? "Cannot fly this");
        _result.Text = string.Join("\n", lines);
        _result.AddThemeColorOverride("font_color", plan.Ok ? Ui.Text : Ui.Danger);
    }
}

/// <summary>
/// The combat report: for each side, shots and hits, missiles fired and what became of them (hit, shot
/// down by point defence, dodged, ran dry), damage done, parts knocked out and ships lost.
/// </summary>
public partial class ReportPanel : PanelContainer
{
    private readonly GameScreen _screen;
    private Label _title = null!;
    private Label _body = null!;

    public ReportPanel() : this(null!) { }
    public ReportPanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 18);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
        box.AddThemeConstantOverride("separation", 8);
        AddChild(box);
        _title = Ui.Label("", 20, Ui.Player);
        box.AddChild(_title);
        _body = Ui.Label("", 14);
        _body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _body.CustomMinimumSize = new Vector2(560, 0);
        box.AddChild(_body);
        box.AddChild(Ui.SmallButton("Close (Esc)", () => _screen.CloseReport(), "Close the report; R opens the last one again"));
    }

    public void Refresh(Battle b)
    {
        var p = b.For(Side.Player);
        var r = b.For(Side.Rival);
        var end = b.EndHour is double e ? GameTime.Format(e) : "still going";
        var length = (b.EndHour ?? Main.Instance.Sim!.World.Hour) - b.StartHour;
        _title.Text = $"Combat report: {GameTime.Format(b.StartHour)} to {end} ({Format.Hours(length)})";
        _body.Text = SideText("You", p) + "\n\n" + SideText("Rival", r);
    }

    private static string SideText(string who, BattleSide s)
    {
        var lines = new List<string> { $"{who}: {(s.Fleets.Count > 0 ? string.Join(", ", s.Fleets) : "no fleets")}" };
        if (s.RailShots > 0) lines.Add($"  Railguns: {s.RailHits} hits from {s.RailShots} shots");
        if (s.MissilesFired > 0)
            lines.Add($"  Missiles: {s.MissilesFired} fired, {s.MissileHits} hit, {s.ShotDown} shot down, {s.Dodged} dodged, {s.RanDry} ran dry or lost their target");
        lines.Add($"  Damage done: {s.DamageDealt:0}");
        if (s.Knocked.Count > 0) lines.Add($"  Knocked out: {string.Join(", ", s.Knocked)}");
        lines.Add(s.ShipsLost.Count > 0 ? $"  Ships lost: {string.Join(", ", s.ShipsLost)}" : "  Ships lost: none");
        return string.Join("\n", lines);
    }
}
