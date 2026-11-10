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
    private Button _route = null!;
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
        _drift = Ui.SmallButton("Drift", () => _screen.OrderHold(HoldStyle.Drift), "Engines cold: stay put (G)");
        _route = Ui.SmallButton("Change route", () => _screen.EditRoute(), "Open this fleet's route in the planner to change the target, speed or waypoints (C). You can also drag a waypoint or the end of the route on the map");
        _stop = Ui.SmallButton("Stop", () => _screen.OrderStop(), "Cancel the flight and brake to a stop (X)");
        _split = Ui.SmallButton("Split", () => _screen.OrderSplit(Checked()), "Make the ticked ships a new fleet");
        _merge = Ui.SmallButton("Merge", () => _screen.OrderMerge(), "Join the nearest of your fleets into this one (M). To merge fleets further apart, drag a box round them and Gather");
        foreach (var b in new[] { _orbit, _drift, _route, _stop, _split, _merge }) _buttons.AddChild(b);
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

    private int? _tickOnly;

    /// <summary>Tick just this ship (picked from the fleet list), ready to split off.</summary>
    public void TickOnly(int shipId)
    {
        _tickOnly = shipId;
        ApplyTick();
    }

    private void ApplyTick()
    {
        if (_tickOnly is not int id || !_rows.ContainsKey(id)) return;
        foreach (var (shipId, row) in _rows) row.Box.ButtonPressed = shipId == id;
        _tickOnly = null;
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
        var seen = mine || _screen.RevealAll || Sensors.Sees(w, Side.Player, f);
        var lines = new List<string>();
        if (mine)
        {
            lines.Add(Doing(w, f));
            lines.Add(Motion(w, f));
            if (f.JoinFleetId is int join && w.FindFleet(join) is Fleet into) lines.Add($"Joins {into.Name} when both are stopped together");
            lines.Add($"Top speed {Format.Speed(perf.MaxSpeed)} (slowest ship) · fuel for {Format.Speed(perf.DeltaVLeft)} of speed change");
            lines.Add(SignatureLine(w, f));
        }
        else if (Sensors.Find(w, Side.Player, f.Id) is Sighting sighting || _screen.RevealAll)
        {
            sighting = Sensors.Find(w, Side.Player, f.Id);
            if (seen) lines.Add($"In sight: {StateText(Sensors.StateOf(f))} · {Motion(w, f)}");
            else if (sighting != null)
            {
                lines.Add($"Out of sight. Last seen {Format.Hours(w.Hour - sighting.Hour)} ago, {StateText(sighting.State)}, at {Format.Speed(sighting.Velocity.Length)}");
                lines.Add("The dashed line is a guess at where it went if it kept going");
            }
            if (_screen.RevealAll) lines.Add($"(debug) {Doing(w, f)}");
        }
        _status.Text = string.Join("\n", lines);
        _shipList.Visible = seen;

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
        ApplyTick();
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
        _route.Disabled = !_screen.CanEditRoute;
        _split.Disabled = picked == 0 || picked == ships.Count;
        var candidate = _screen.MergeCandidate();
        _merge.Disabled = candidate == null;
        _merge.TooltipText = candidate != null ? $"Join {candidate.Name} into this fleet" : "No other fleet of yours is close by and stopped";
    }

    public static string StateText(SignatureState state) => state switch
    {
        SignatureState.Burn => "engines burning",
        SignatureState.Orbit => "holding orbit",
        _ => "running cold",
    };

    /// <summary>How visible the fleet is right now: what it is doing, and from how far enemy ships could see it.</summary>
    public static string SignatureLine(World w, Fleet f)
    {
        var sig = Sensors.Signature(w, f);
        var frigate = Sensors.Range(GameData.ShipClass("gun-frigate").SensorMm, sig);
        var destroyer = Sensors.Range(GameData.ShipClass("missile-destroyer").SensorMm, sig);
        string Km(double mm) => mm >= Physics.MmPerAu * 0.05 ? Format.Distance(mm) : $"{mm * 1000:#,0} km";
        return $"Signature: {StateText(Sensors.StateOf(f))}. Enemy frigates see it from {Km(frigate)}, destroyers from {Km(destroyer)}";
    }

    /// <summary>Speed, and for a fleet near an enemy how fast the gap is closing.</summary>
    public static string Motion(World w, Fleet f)
    {
        var text = f.Velocity.Length > 1e-6 ? $"Speed {Format.Speed(f.Velocity.Length)}" : "Stopped";
        var enemy = Combat.NearestEnemyFleet(w, f, GameData.Combat.ContactRangeMm * 1.5);
        if (enemy == null) return text;
        var gap = enemy.Position - f.Position;
        var d = gap.Length;
        if (d < 1e-9) return text;
        var closing = -Vec2.Dot(gap, enemy.Velocity - f.Velocity) / d;
        var how = closing > 1e-6 ? $"closing at {Format.Speed(closing)}" : closing < -1e-6 ? $"pulling apart at {Format.Speed(-closing)}" : "holding distance";
        return $"{text} · {enemy.Name} {d * 1000:#,0} km away, {how}";
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

/// <summary>Several of your fleets picked at once: what they are, and one button to gather them into one.</summary>
public partial class GroupPanel : PanelContainer
{
    private readonly GameScreen _screen;
    private Label _title = null!;
    private Label _list = null!;
    private Button _gather = null!;

    public GroupPanel() : this(null!) { }
    public GroupPanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 14);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(330, 0) };
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        _title = Ui.Label("", 17, Ui.Player);
        box.AddChild(_title);
        _list = Ui.Label("", 14, Ui.Dim);
        _list.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _list.CustomMinimumSize = new Vector2(330, 0);
        box.AddChild(_list);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        _gather = Ui.SmallButton("Gather", () => _screen.OrderGather(), "Merge them all into the largest; fleets further off fly there first (M)");
        _gather.CustomMinimumSize = new Vector2(110, 32);
        row.AddChild(_gather);
        var clear = Ui.SmallButton("Clear", () => _screen.Select(null), "Unpick them");
        clear.CustomMinimumSize = new Vector2(80, 32);
        row.AddChild(clear);
        box.AddChild(row);
    }

    public void Refresh(Simulation sim, IReadOnlyList<Fleet> group)
    {
        var w = sim.World;
        var into = group[0];
        _title.Text = $"{group.Count} fleets picked";
        _gather.Text = $"Gather into {into.Name}";
        _list.Text = string.Join("\n", group.Select(f =>
        {
            var where = f == into ? "the others join it" : Fleets.CanJoin(f, into) ? "close by: merges at once"
                : $"{Format.Distance(Vec2.Distance(f.Position, into.Position))} away: flies there, then merges";
            return $"{f.Name} ({f.ShipIds.Count} ships): {where}";
        }));
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
    private Button _together = null!, _ahead = null!;
    private Button _addEnd = null!, _addMid = null!;
    private HBoxContainer _shipsRow = null!;
    private VBoxContainer _waypointBox = null!;
    private readonly List<(Button Through, Button Stop, Label Speed, Label Wait, HBoxContainer WaitRow)> _waypointRows = new();
    private PlannerState? _rowsFor;
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

        _brake = Choice("Stop", "Brake to a stop at the target (then orbit or drift)", () => s.Arrival = ArrivalStyle.Brake);
        _pass = Choice("Fly by", "Fly past the target at speed without stopping or going into orbit: quicker and half the fuel", () => s.Arrival = ArrivalStyle.FastPass);
        box.AddChild(Row("Arrive", _brake, _pass));
        _orbit = Choice("Orbit", "Circle the planet or moon on arrival", () => s.Hold = HoldStyle.Orbit);
        _drift = Choice("Drift", "Sit with engines cold on arrival", () => s.Hold = HoldStyle.Drift);
        box.AddChild(Row("Then", _orbit, _drift));
        _together = Choice("Together", "The whole fleet flies at the pace of its slowest ship", () => s.FastAhead = false);
        _ahead = Choice("Fast ahead", "Faster ships fly at their own pace, wait at the target and rejoin when the slow ones arrive", () => s.FastAhead = true);
        _shipsRow = Row("Ships", _together, _ahead);
        box.AddChild(_shipsRow);
        _addEnd = Choice("New end", "Ctrl+right-click sets a new destination; the old one becomes a waypoint on the way", () => s.AddAtEnd = true);
        _addMid = Choice("Mid point", "Ctrl+right-click adds a waypoint on the way, keeping the destination", () => s.AddAtEnd = false);
        box.AddChild(Row("Ctrl+R-click", _addEnd, _addMid));
        var hint = Ui.Label("Drag a waypoint or the end of the route to move it", 13, Ui.Dim);
        box.AddChild(hint);
        var key = new HBoxContainer();
        key.AddThemeConstantOverride("separation", 10);
        key.AddChild(Ui.Label("Line:", 13, Ui.Dim));
        key.AddChild(Ui.Label("speeding up", 13, MapView.SpeedUp));
        key.AddChild(Ui.Label("braking", 13, MapView.Braking));
        key.AddChild(Ui.Label("coasting (thin)", 13, Ui.Player));
        box.AddChild(key);
        _waypointBox = new VBoxContainer();
        _waypointBox.AddThemeConstantOverride("separation", 2);
        box.AddChild(_waypointBox);

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

    /// <summary>One row per waypoint: fly through or stop (and wait), the speed of the stretch to it, and remove.</summary>
    private void RefreshWaypoints(PlannerState p)
    {
        if (_rowsFor != p || _waypointRows.Count != p.Waypoints.Count)
        {
            _rowsFor = p;
            foreach (var c in _waypointBox.GetChildren()) c.QueueFree();
            _waypointRows.Clear();
            for (var i = 0; i < p.Waypoints.Count; i++)
            {
                var index = i;
                WaypointDraft Wp() => p.Waypoints[index];
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 3);
                var n = Ui.Label($"{i + 1}", 14, Ui.Player);
                n.CustomMinimumSize = new Vector2(16, 0);
                row.AddChild(n);
                Button Small(string text, string tip, Action act)
                {
                    var b = Ui.SmallButton(text, () => { act(); _screen.Replan(); }, tip);
                    b.CustomMinimumSize = new Vector2(28, 28);
                    return b;
                }
                var through = Small("Through", "Fly through this point and turn towards the next", () => Wp().Stop = false);
                var stop = Small("Stop", "Brake to a stop here (and wait, if you like)", () => Wp().Stop = true);
                row.AddChild(through);
                row.AddChild(stop);
                var speed = Ui.Label("", 13, Ui.Dim);
                speed.CustomMinimumSize = new Vector2(64, 0);
                speed.HorizontalAlignment = HorizontalAlignment.Center;
                double Pace() => Wp().Speed ?? _screen.Settings.Speed;
                row.AddChild(Small("−", "Slower to this point (a slow stretch is a long cold coast)", () => Wp().Speed = Math.Max(FlightPlanner.MinSpeedFraction, Math.Round(Pace() * 10 - 1) / 10)));
                row.AddChild(speed);
                row.AddChild(Small("+", "Faster to this point", () => Wp().Speed = Math.Min(1, Math.Round(Pace() * 10 + 1) / 10)));
                var waitRow = new HBoxContainer();
                var wait = Ui.Label("", 13, Ui.Dim);
                wait.CustomMinimumSize = new Vector2(60, 0);
                wait.HorizontalAlignment = HorizontalAlignment.Center;
                waitRow.AddChild(Small("−", "Wait less here", () => Wp().WaitHours = Math.Max(0, Wp().WaitHours - 6)));
                waitRow.AddChild(wait);
                waitRow.AddChild(Small("+", "Wait longer here", () => Wp().WaitHours += 6));
                row.AddChild(waitRow);
                row.AddChild(Small("✕", "Remove this waypoint", () => _screen.RemoveWaypoint(index)));
                _waypointBox.AddChild(row);
                _waypointRows.Add((through, stop, speed, wait, waitRow));
            }
        }
        for (var i = 0; i < _waypointRows.Count; i++)
        {
            var wp = p.Waypoints[i];
            var row = _waypointRows[i];
            Mark(row.Through, !wp.Stop);
            Mark(row.Stop, wp.Stop);
            row.Speed.Text = wp.Speed is double s ? $"{s:P0}" : "route";
            row.WaitRow.Visible = wp.Stop;
            row.Wait.Text = $"wait {wp.WaitHours:0} h";
        }
    }

    public void Refresh(Simulation sim, PlannerState p)
    {
        RefreshWaypoints(p);
        var s = _screen.Settings;
        var w = sim.World;
        var fleet = w.FindFleet(p.FleetId);
        _title.Text = $"{(p.Editing ? "Change route" : "Route")}: {fleet?.Name} to {p.TargetName}";
        _title.AddThemeColorOverride("font_color", p.TargetFleetId != null ? Ui.Danger : Ui.Player);
        Mark(_direct, s.Route == RouteKind.Direct);
        Mark(_addEnd, s.AddAtEnd);
        Mark(_addMid, !s.AddAtEnd);
        Mark(_wait, s.Route == RouteKind.Wait);
        Mark(_brake, s.Arrival == ArrivalStyle.Brake);
        Mark(_pass, s.Arrival == ArrivalStyle.FastPass);
        var canOrbit = s.Arrival == ArrivalStyle.Brake && p.TargetBodyId != null;
        if (p.TargetFleetId != null) _title.Text = $"Attack: {fleet?.Name} on {p.TargetName}";
        Mark(_orbit, canOrbit && s.Hold == HoldStyle.Orbit, canOrbit);
        Mark(_drift, !canOrbit || s.Hold == HoldStyle.Drift, canOrbit);
        var canAhead = fleet != null && _screen.HasFastShips(fleet) && s.Arrival == ArrivalStyle.Brake;
        _shipsRow.Visible = fleet != null && _screen.HasFastShips(fleet);
        Mark(_together, !canAhead || !s.FastAhead, canAhead);
        Mark(_ahead, canAhead && s.FastAhead, canAhead);
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
        if (canAhead && s.FastAhead) lines.Add("Fast ships go ahead, wait there and rejoin; times above are for the slowest ship");
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
