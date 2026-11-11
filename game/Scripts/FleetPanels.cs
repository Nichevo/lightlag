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
    private MenuButton _join = null!;
    private Button _orbit = null!;
    private Button _drift = null!;
    private Button _stop = null!;
    private Button _route = null!;
    private OptionButton _stance = null!;

    /// <summary>The standing orders offered, in the menu's order, with what each does.</summary>
    private static readonly (Stance Stance, string Name, string Tip)[] Stances =
    {
        (Stance.Engage, "Engage", "Close to railgun range and fire"),
        (Stance.EngageIfStronger, "Engage if stronger", "Close in and fight only when this fleet clearly outguns what it meets; otherwise keep course"),
        (Stance.Hold, "Hold course", "Keep course; fire at whatever comes in range"),
        (Stance.HoldPosition, "Hold position", "Stop where it is on contact and fire without chasing"),
        (Stance.Avoid, "Avoid contact", "Fly home as soon as an enemy is seen coming for it"),
        (Stance.ReturnHome, "Return home", "Break off on contact and fly home"),
    };
    private CheckBox _retreat = null!;
    private HBoxContainer _missionRow = null!;
    private HBoxContainer _coverRow = null!;
    private OptionButton _cover = null!;
    private OptionButton _fireAt = null!;
    private readonly Dictionary<int, Button> _protect = new();
    private MenuButton _mission = null!;
    private OptionButton _autonomy = null!;

    /// <summary>The autonomy levels, in the menu's order, with what each does.</summary>
    private static readonly (AutonomyLevel Level, string Name, string Tip)[] Levels =
    {
        (AutonomyLevel.Strict, "Strict", "Only its standing orders: it waits for yours, however long they take"),
        (AutonomyLevel.Sensible, "Sensible", "When cut off, it runs from a stronger enemy if your orders could not reach it in time"),
        (AutonomyLevel.Free, "Free", "When cut off, it runs from danger at once and goes after weaker enemies near it"),
    };

    // Mission menu ids: small numbers for the plain missions; raids and escorts carry the body or fleet id.
    private const int MissionNone = 1, MissionHold = 2, MissionPatrol = 3, MissionScout = 4, RaidBase = 1000, EscortBase = 100000;
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
        _join = new MenuButton { Text = "Join ▾", Flat = false, FocusMode = FocusModeEnum.None,
            TooltipText = "Fly to another of your fleets and merge into it: to where it will stop, or on intercept tracks if it is drifting on" };
        _join.AddThemeFontSizeOverride("font_size", 15);
        _join.AboutToPopup += FillJoinMenu;
        _join.GetPopup().IdPressed += id => Main.Instance.RunGuarded("join order", () => _screen.OrderJoin((int)id));
        _buttons.AddChild(_join);
        box.AddChild(_buttons);

        // Standing orders: what the fleet does when enemies come into contact.
        _orders = new HBoxContainer();
        _orders.AddThemeConstantOverride("separation", 4);
        var label = Ui.Label("In contact", 13, Ui.Dim);
        label.CustomMinimumSize = new Vector2(70, 0);
        _orders.AddChild(label);
        _stance = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "What the fleet does when enemies come into contact" };
        _stance.AddThemeFontSizeOverride("font_size", 13);
        for (var i = 0; i < Stances.Length; i++)
        {
            _stance.AddItem(Stances[i].Name, i);
            _stance.SetItemTooltip(i, Stances[i].Tip);
        }
        _stance.ItemSelected += i => SetStance(Stances[i].Stance, null);
        _retreat = new CheckBox { Text = "Retreat when damaged", FocusMode = FocusModeEnum.None, TooltipText = "Fly home when the fleet's armour falls below half" };
        _retreat.AddThemeFontSizeOverride("font_size", 13);
        _retreat.Pressed += () => SetStance(null, _retreat.ButtonPressed);
        _orders.AddChild(_stance);
        _orders.AddChild(_retreat);
        box.AddChild(_orders);

        // Mission and autonomy: what the fleet does without orders, and how much it decides when cut off.
        _missionRow = new HBoxContainer();
        _missionRow.AddThemeConstantOverride("separation", 4);
        var missionLabel = Ui.Label("Mission", 13, Ui.Dim);
        missionLabel.CustomMinimumSize = new Vector2(70, 0);
        _missionRow.AddChild(missionLabel);
        _mission = new MenuButton { Text = "None ▾", Flat = false, FocusMode = FocusModeEnum.None,
            TooltipText = "A job the fleet carries out by itself: hold or patrol a place, raid an enemy planet, escort a fleet, or scout. A direct move order ends it" };
        _mission.AddThemeFontSizeOverride("font_size", 13);
        _mission.AboutToPopup += FillMissionMenu;
        _mission.GetPopup().IdPressed += id => Main.Instance.RunGuarded("mission order", () => PickMission((int)id));
        _missionRow.AddChild(_mission);
        _missionRow.AddChild(Ui.Label("Autonomy", 13, Ui.Dim));
        _autonomy = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "How much the fleet decides for itself when it is cut off from your flagship" };
        _autonomy.AddThemeFontSizeOverride("font_size", 13);
        for (var i = 0; i < Levels.Length; i++)
        {
            _autonomy.AddItem(Levels[i].Name, i);
            _autonomy.SetItemTooltip(i, Levels[i].Tip);
        }
        _autonomy.ItemSelected += i => { if (_fleet != null) _screen.OrderAutonomy(Levels[i].Level); };
        _missionRow.AddChild(_autonomy);
        box.AddChild(_missionRow);

        // Cover and targeting (light lag): guard the protected ships, and what to shoot at.
        _coverRow = new HBoxContainer();
        _coverRow.AddThemeConstantOverride("separation", 4);
        _coverLabel = Ui.Label("Cover", 13, Ui.Dim);
        _coverLabel.CustomMinimumSize = new Vector2(70, 0);
        _coverRow.AddChild(_coverLabel);
        _cover = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "How the fleet guards its protected ships (⚑ on the ship rows; the flagship by default)" };
        _cover.AddThemeFontSizeOverride("font_size", 13);
        _cover.AddItem("Loose", 0);
        _cover.SetItemTooltip(0, "No special care: everyone fights for themselves");
        _cover.AddItem("Screen", 1);
        _cover.SetItemTooltip(1, "Other ships put themselves in the line of fire: most shots at a protected ship hit one of them instead");
        _cover.AddItem("Close", 2);
        _cover.SetItemTooltip(2, "Tight round the protected ships: even more shots stopped and better point defence for them, but the fleet dodges worse, and the enemy can tell something important is inside");
        _cover.ItemSelected += i => { if (_fleet != null) _screen.OrderCover((CoverStyle)(int)i, _fleet.FireAt); };
        _coverRow.AddChild(_cover);
        _coverRow.AddChild(Ui.Label("Fire at", 13, Ui.Dim));
        _fireAt = new OptionButton { FocusMode = FocusModeEnum.None };
        _fireAt.AddThemeFontSizeOverride("font_size", 13);
        _fireAt.AddItem("Nearest", 0);
        _fireAt.SetItemTooltip(0, "Each ship fires at the nearest enemy");
        _fireAt.AddItem("Flagship", 1);
        _fireAt.SetItemTooltip(1, "Everything goes for the ship you take to be the enemy flagship (a guess until you have seen it close). Ships nobody shoots at aim better, so only do it if it will die");
        _fireAt.AddItem("Largest first", 2);
        _fireAt.SetItemTooltip(2, "The biggest ship in reach first (destroyers before frigates)");
        _fireAt.AddItem("Finish off", 3);
        _fireAt.SetItemTooltip(3, "The ship closest to dying first: fewer enemy guns sooner, but damaged ships may be screened");
        _fireAt.AddItem("Engines", 4);
        _fireAt.SetItemTooltip(4, "Aim for engines: fewer hits, but a hit often knocks the engines out, so the target cannot run or close");
        _fireAt.AddItem("Split fire", 5);
        _fireAt.SetItemTooltip(5, "Each ship takes a different target: damage and the enemy's dodging spread across the fleet");
        _fireAt.ItemSelected += i => { if (_fleet != null) _screen.OrderCover(_fleet.Cover, (FireAt)(int)i); };
        _coverRow.AddChild(_fireAt);
        box.AddChild(_coverRow);

        // Heat (M9): radiators, heat sinks, the cold clock.
        var heatRow = new HBoxContainer();
        heatRow.AddThemeConstantOverride("separation", 4);
        var heatLabel = Ui.Label("Heat", 13, Ui.Dim);
        heatLabel.CustomMinimumSize = new Vector2(70, 0);
        heatRow.AddChild(heatLabel);
        _radiators = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "Where the radiators are" };
        _radiators.AddThemeFontSizeOverride("font_size", 13);
        _radiators.AddItem("Radiators auto", 0);
        _radiators.SetItemTooltip(0, "Out normally, pulled in for battle (radiators are fragile): heat builds while it fights");
        _radiators.AddItem("Radiators out", 1);
        _radiators.SetItemTooltip(1, "Always out: stays cool in battle, but hits do more harm");
        _radiators.AddItem("Run cold", 2);
        _radiators.SetItemTooltip(2, "Always in, to hide: much harder to see, but heat builds until the ships must vent (the cold clock). Burning the engines heats them fast");
        _radiators.ItemSelected += i => { if (_fleet != null) _screen.OrderHeat((RadiatorMode)(int)i, _fleet.AutoSinks); };
        heatRow.AddChild(_radiators);
        heatRow.AddChild(Ui.SmallButton("Eject sinks", () => _screen.EjectSinks(),
            "Every ship throws a heat sink overboard: sheds heat at once, and the hot sink can draw off missiles closing on it. Ships do it on their own when they run hot or missiles close in (Auto sinks)"));
        _autoSinks = new CheckButton { Text = "Auto sinks", FocusMode = FocusModeEnum.None, TooltipText = "Ships throw heat sinks on their own when they run hot or missiles close in" };
        _autoSinks.AddThemeFontSizeOverride("font_size", 12);
        _autoSinks.Toggled += on => { if (_fleet != null) _screen.OrderHeat(_fleet.Radiators, on); };
        heatRow.AddChild(_autoSinks);
        box.AddChild(heatRow);
        _heat = Ui.Label("", 12, Ui.Dim);
        box.AddChild(_heat);

        // Long-range fire and missile timing (M9).
        var fireRow = new HBoxContainer();
        fireRow.AddThemeConstantOverride("separation", 4);
        var fireLabel = Ui.Label("Weapons", 13, Ui.Dim);
        fireLabel.CustomMinimumSize = new Vector2(70, 0);
        fireRow.AddChild(fireLabel);
        _longShots = new CheckButton { Text = "Long-range fire", FocusMode = FocusModeEnum.None,
            TooltipText = $"Railguns also fire beyond their reach, out to {Format.Distance(Heat.D.LongRangeMm)}: they almost never hit and never hit a ship that dodges, but make the enemy dodge (fuel and heat). Good when chased. Costs ammunition" };
        _longShots.AddThemeFontSizeOverride("font_size", 12);
        _longShots.Toggled += on => { if (_fleet != null) _screen.OrderFireControl(on, _fleet.MissileLaunch); };
        fireRow.AddChild(_longShots);
        _launch = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "When destroyers launch: held missiles fly a shorter way and keep fuel to follow a dodge" };
        _launch.AddThemeFontSizeOverride("font_size", 13);
        _launch.AddItem($"Launch far ({Format.Distance(GameData.Combat.Missile.LaunchRangeMm)})", 0);
        _launch.AddItem($"Hold to {Format.Distance(Heat.D.MidLaunchMm)}", 1);
        _launch.AddItem($"Hold to {Format.Distance(Heat.D.CloseLaunchMm)}", 2);
        _launch.ItemSelected += i => { if (_fleet != null) _screen.OrderFireControl(_fleet.LongShots, (MissileLaunch)(int)i); };
        fireRow.AddChild(_launch);
        _drop = new MenuButton { Text = "Drop missiles ▾", Flat = false, FocusMode = FocusModeEnum.None,
            TooltipText = "Each destroyer drops a salvo to drift dark; after the timer the missiles go for the first enemy ship that passes close" };
        _drop.AddThemeFontSizeOverride("font_size", 13);
        foreach (var wake in new[] { "Wake now", "Wake in 1 hour", "Wake in 6 hours", "Wake in a day" })
            _drop.GetPopup().AddItem(wake);
        _drop.GetPopup().IdPressed += id => _screen.DropMissiles(id switch { 0 => 0, 1 => 1, 2 => 6, _ => 24 });
        fireRow.AddChild(_drop);
        box.AddChild(fireRow);
        _fireRow = fireRow;
        _heatRow = heatRow;
        _engagementButton = Ui.SmallButton("Engagement (V)", () => _screen.ToggleEngagement(),
            "Zoom in on this fleet and the enemy it faces: weapon reach, closest pass, missiles and ways to play it");
        box.AddChild(_engagementButton);
        _supplyRun = new MenuButton { Text = "Supply run ▾", Flat = false, FocusMode = FocusModeEnum.None,
            TooltipText = "Auto supply run: this fleet's tankers and freighters fly out to the fleet you pick, top it up while they are together, go home to load when low, and repeat" };
        _supplyRun.AddThemeFontSizeOverride("font_size", 15);
        _supplyRun.AboutToPopup += FillSupplyRunMenu;
        _supplyRun.GetPopup().IdPressed += id => Main.Instance.RunGuarded("supply run", () =>
        {
            if (_screen.SelectedFleet is Fleet sf) _screen.IssueNow(new SetSupplyRunCommand(Side.Player, sf.Id, id < 0 ? null : (int)id), "supply run");
        });
        box.AddChild(_supplyRun);
        box.AddChild(Ui.SmallButton("Ships and fittings", () => _screen.FittingFleetId = _screen.FittingFleetId == null ? _screen.SelectedFleet?.Id : null,
            "Each ship's drawing, slots and modules. Swap modules at your shipyard, or save a design for new ships"));
        _droneButton = Ui.SmallButton("Send orders by drone", () => _screen.SendDrone(),
            $"A planet blocks the signal to this fleet. A messenger drone flies the orders there instead: slower than light, {Drones.D.Cost:0} Industry, and easy to shoot down");
        box.AddChild(_droneButton);
    }

    private Button _engagementButton = null!;
    private MenuButton _supplyRun = null!;

    private void FillSupplyRunMenu()
    {
        var popup = _supplyRun.GetPopup();
        popup.Clear();
        var w = Main.Instance.Sim!.World;
        if (_screen.SelectedFleet is not Fleet f) return;
        if (f.SupplyRunFor != null) popup.AddItem("End the supply run", -1);
        foreach (var other in w.Fleets.Where(o => o.Side == Side.Player && o.Id != f.Id && !w.ShipsOf(o).All(SupplyLines.IsSupplyShip)))
            popup.AddItem($"Keep {other.Name} supplied" + (f.SupplyRunFor == other.Id ? " (now)" : ""), other.Id);
    }
    private Button _droneButton = null!;
    private Label _coverLabel = null!;
    private OptionButton _radiators = null!;
    private CheckButton _autoSinks = null!;
    private Label _heat = null!;
    private CheckButton _longShots = null!;
    private OptionButton _launch = null!;
    private MenuButton _drop = null!;
    private HBoxContainer _fireRow = null!;
    private HBoxContainer _heatRow = null!;

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
        _supplyRun.Visible = mine && ships.Any(SupplyLines.IsSupplyShip);
        if (_supplyRun.Visible)
            _supplyRun.Text = f.SupplyRunFor is int run && w.FindFleet(run) is Fleet t ? $"Supply run: {t.Name} ▾" : "Supply run ▾";
        _title.Text = f.Name + (mine ? "" : "  (rival)");
        _title.AddThemeColorOverride("font_color", mine ? Ui.Player : Ui.Rival);
        var seen = mine || _screen.RevealAll || Sensors.Sees(w, Side.Player, f, seat: true);
        var lines = new List<string>();
        if (mine)
        {
            lines.Add(Doing(w, f));
            if (w.Light.On && Light.DelayHours(w, Side.Player, f.Position) is var lag and > 0)
                lines.Add($"{Format.Lag(lag)} of light from the flagship each way. News is gathered once a game hour, so what you see can be up to an hour older");
            lines.Add(Motion(w, f));
            if (f.JoinFleetId is int join && w.FindFleet(join) is Fleet into) lines.Add($"Joins {into.Name} when both are stopped together");
            lines.Add($"Top speed {Format.Speed(perf.MaxSpeed)} (slowest ship) · fuel for {Format.Speed(perf.DeltaVLeft)} of speed change"
                      + (f.ReserveDeltaV > 0 && !f.Resupplying ? $" (keeps {Format.Speed(f.ReserveDeltaV * Supply.Margin)} to get back to a planet you hold)" : ""));
            if (f.Resupplying)
                lines.Add(f.Flight?.TargetBodyId is int to && w.FindBody(to) is Body refill
                    ? $"LOW ON SUPPLIES: coasting to {refill.Name} to refill; it won't fight or dodge on the way"
                    : "OUT OF SUPPLIES: drifting, it can't reach a planet you hold");
            if (f.LeftOrbitBodyId is int left && f.Hold != HoldStyle.Orbit && w.FindBody(left) is Body leftBody)
                lines.Add($"LEFT ORBIT at {leftBody.Name} to fight: claiming and bombarding there stop until it orbits again (O)");
            if (f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int orbitId && w.FindBody(orbitId) is Body orbiting && Economy.BombardStatus(w, orbiting, Side.Player) is string attack)
                lines.Add($"{orbiting.Name}: {attack}");
            lines.Add(SignatureLine(w, f));
        }
        else if (Sensors.Find(w, Side.Player, f.Id, seat: true) is Sighting sighting || _screen.RevealAll)
        {
            sighting = Sensors.Find(w, Side.Player, f.Id, seat: true);
            if (seen) lines.Add($"In sight: {StateText(Sensors.StateOf(f))} · {Motion(w, f)}");
            else if (sighting != null)
            {
                lines.Add($"Out of sight. Last seen {Format.Hours(w.Hour - sighting.Hour)} ago, {StateText(sighting.State)}, at {Format.Speed(sighting.Velocity.Length)}");
                lines.Add("The dashed line is a guess at where it went if it kept going");
            }
            if (sighting is { Flag: not FlagKnown.Unknown } || sighting is { FlagScore: > 0.05 })
                lines.Add(sighting!.Flag switch
                {
                    FlagKnown.Confirmed => "FLAGSHIP: confirmed by a close look",
                    FlagKnown.Cleared => "Not the flagship (seen close)",
                    FlagKnown.Suspected => $"Flagship suspected ({sighting.FlagScore:P0}): signal traffic or guarding behaviour",
                    _ => $"Flagship? {sighting.FlagScore:P0} so far",
                });
            if (_screen.RevealAll)
            {
                lines.Add($"(debug) {Doing(w, f)}");
                if (Mind.Of(w, f.Side)?.JobOf(f.Id) is FleetJob job) lines.Add($"(debug) Rival job: {job.Kind}: {job.Why}");
            }
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
            _protect.Clear();
            foreach (var s in ships)
            {
                var row = new HBoxContainer();
                if (mine && w.Light.On)
                {
                    var shipId = s.Id;
                    var protect = new Button { Text = "⚑", ToggleMode = true, FocusMode = FocusModeEnum.None, Flat = true,
                        TooltipText = "Protected: fleets in Screen or Close cover guard it, and point defence puts it first. Marking another ship makes a decoy" };
                    protect.Toggled += on => _screen.OrderProtect(shipId, on);
                    row.AddChild(protect);
                    _protect[s.Id] = protect;
                }
                var check = new CheckBox { Text = $"{s.Name}  {Fitting.Spec(s).Name}", FocusMode = FocusModeEnum.None, Disabled = !mine };
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
            var cls = Fitting.Spec(s);
            var share = s.Supplies / Math.Max(1e-9, Fitting.MaxSupplies(w, s));
            var armour = s.Armour / Tech.MaxArmour(w, s);
            var bits = new List<string> { $"armour {armour:P0}", $"fuel {share:P0}" };
            if (mine && SupplyLines.MaxAmmo(s) > 0) bits.Add($"ammo {s.Ammo / SupplyLines.MaxAmmo(s):P0}");
            if (mine && SupplyLines.MaxFood(s) > 0) bits.Add(SupplyLines.Hungry(s) ? "HUNGRY" : $"food {s.Food:0} d");
            if (mine && SupplyLines.IsSupplyShip(s)) bits.Add($"cargo {SupplyLines.CargoShare(s):P0}");
            if (cls.Missiles > 0) bits.Add($"{s.Missiles} missiles");
            var heat = Heat.Capacity(s) > 0 ? s.Heat / Heat.Capacity(s) : 0;
            if (mine) bits.Add(s.Overheated ? "OVERHEATED" : $"heat {heat:P0}");
            if (mine && Heat.MaxShield(w, s) > 0) bits.Add($"shield {s.Shield / Heat.MaxShield(w, s):P0}");
            if (s.Disabled is Module m) bits.Add($"{Combat.ModuleName(m)} out");
            if (Light.Flagship(w, s.Side)?.Id == s.Id) bits.Insert(0, "FLAGSHIP");
            row.Supplies.Text = string.Join(" · ", bits);
            if (_protect.TryGetValue(s.Id, out var pb))
            {
                var on = Flagships.IsProtected(w, s);
                pb.SetPressedNoSignal(on);
                pb.Modulate = on ? Ui.Player : new Color(1, 1, 1, 0.35f);
            }
            row.Supplies.AddThemeColorOverride("font_color", share < 0.2 || armour < 0.5 || s.Disabled != null || s.Overheated || SupplyLines.Hungry(s) ? Ui.Danger
                : mine && heat >= 0.75 ? Ui.Rival : Ui.Dim);
        }

        _buttons.Visible = mine;
        _orders.Visible = mine;
        _engagementButton.Visible = mine && Lightlag.Sim.Core.Engagement.EnemyFor(sim.World, f) != null;
        var at = Array.FindIndex(Stances, x => x.Stance == f.Stance);
        if (at >= 0 && _stance.Selected != at) _stance.Select(at);
        _retreat.SetPressedNoSignal(f.RetreatWhenDamaged);
        _missionRow.Visible = mine;
        _coverRow.Visible = mine;
        _coverLabel.Visible = _cover.Visible = w.Light.On;
        _fireAt.SetItemDisabled(1, !w.Light.On);
        _heatRow.Visible = _fireRow.Visible = _heat.Visible = mine;
        if (_radiators.Selected != (int)f.Radiators) _radiators.Select((int)f.Radiators);
        _autoSinks.SetPressedNoSignal(f.AutoSinks);
        _longShots.SetPressedNoSignal(f.LongShots);
        if (_launch.Selected != (int)f.MissileLaunch) _launch.Select((int)f.MissileLaunch);
        _drop.Disabled = !ships.Any(x => x.Missiles > 0 && x.Disabled != Module.Missiles);
        _heat.Text = HeatText(w, f, ships);
        _heat.AddThemeColorOverride("font_color", ships.Any(x => x.Overheated) ? Ui.Danger : Ui.Dim);
        _droneButton.Visible = mine && w.Light.On && Drones.Pending(w, Side.Player, f.Id).Count > 0 && !Light.SignalClear(w, Side.Player, f.Position);
        if (_cover.Selected != (int)f.Cover) _cover.Select((int)f.Cover);
        if (_fireAt.Selected != (int)f.FireAt) _fireAt.Select((int)f.FireAt);
        _mission.Text = MissionText(w, f) + " ▾";
        var level = Array.FindIndex(Levels, x => x.Level == f.Autonomy);
        if (level >= 0 && _autonomy.Selected != level) _autonomy.Select(level);
        var flying = f.Flight is { Started: true } || f.Velocity.Length > 1e-9;
        var picked = Checked().Count;
        _orbit.Disabled = flying || (f.Hold == HoldStyle.Orbit && f.Flight == null);
        _drift.Disabled = flying || (f.Hold == HoldStyle.Drift && f.Flight == null);
        _stop.Disabled = f.Flight == null && f.Velocity.Length < 1e-9;
        _route.Disabled = !_screen.CanEditRoute;
        _split.Disabled = picked == 0 || picked == ships.Count;
        _join.Disabled = !mine || !sim.World.Fleets.Any(o => o.Side == f.Side && o.Id != f.Id);
        var candidate = _screen.MergeCandidate();
        _merge.Disabled = candidate == null;
        _merge.TooltipText = candidate != null ? $"Join {candidate.Name} into this fleet" : "No other fleet of yours is close by and stopped";
    }

    /// <summary>One line on the fleet's heat: how hot, sinks and shields left, overheated ships, the cold clock.</summary>
    private static string HeatText(World w, Fleet f, List<Ship> ships)
    {
        var parts = new List<string> { $"Heat {Heat.Share(w, f):P0}" };
        var hot = ships.Where(s => s.Overheated).Select(s => s.Name).ToList();
        if (hot.Count > 0) parts.Add($"OVERHEATED: {string.Join(", ", hot)}");
        parts.Add($"{ships.Sum(s => s.HeatSinks)} sinks");
        var max = ships.Sum(s => Heat.MaxShield(w, s));
        if (max > 0) parts.Add($"shields {ships.Sum(s => s.Shield) / max:P0}");
        if (Heat.ColdClockHours(w, f) is double clock) parts.Add($"cold for {Format.Hours(clock)} more");
        if (w.Dropped.Count(d => d.Side == f.Side) is var dropped and > 0) parts.Add($"{dropped} dropped missiles waiting");
        return string.Join(" · ", parts);
    }

    /// <summary>The body a hold or patrol is about: where the fleet is, or where it is going, else the nearest planet.</summary>
    private static Body? PlaceOf(World w, Fleet f)
    {
        if (f.Flight?.TargetBodyId is int to && w.FindBody(to) is Body target) return target;
        if (f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int at && w.FindBody(at) is Body b) return b;
        return w.Bodies.Where(b => b.Kind is BodyKind.Planet or BodyKind.Moon).OrderBy(b => Vec2.Distance(b.Position, f.Position)).FirstOrDefault();
    }

    private void FillMissionMenu()
    {
        var popup = _mission.GetPopup();
        popup.Clear();
        var sim = Main.Instance.Sim;
        if (sim == null || _screen.SelectedFleet is not Fleet f) return;
        var w = sim.World;
        if (f.Mission != Mission.None) popup.AddItem("End the mission", MissionNone);
        if (PlaceOf(w, f) is Body place)
        {
            popup.AddItem($"Hold {place.Name}", MissionHold);
            var planet = place.Kind == BodyKind.Moon && place.ParentId is int p && w.FindBody(p) is Body parent ? parent : place;
            var moons = w.Bodies.Count(m => m.ParentId == planet.Id && m.Kind == BodyKind.Moon);
            popup.AddItem(moons > 0 ? $"Patrol {planet.Name} and its moons" : $"Patrol {planet.Name}", MissionPatrol);
        }
        popup.AddItem("Scout the planets (avoids contact)", MissionScout);
        foreach (var b in w.Bodies.Where(b => b.Owner is Side o && o != f.Side).OrderBy(b => Vec2.Distance(b.Position, f.Position)))
            popup.AddItem($"Raid {b.Name}", RaidBase + b.Id);
        foreach (var o in w.Fleets.Where(o => o.Side == f.Side && o.Id != f.Id).OrderBy(o => Vec2.Distance(o.Position, f.Position)))
            popup.AddItem($"Escort {o.Name} ({o.ShipIds.Count} ships)", EscortBase + o.Id);
    }

    private void PickMission(int id)
    {
        var w = Main.Instance.Sim?.World;
        if (w == null || _fleet == null) return;
        var place = PlaceOf(w, _fleet);
        switch (id)
        {
            case MissionNone: _screen.OrderMission(Mission.None, null, null); break;
            case MissionHold: _screen.OrderMission(Mission.Hold, place?.Id, null); break;
            case MissionPatrol: _screen.OrderMission(Mission.Patrol, place?.Id, null); break;
            case MissionScout: _screen.OrderMission(Mission.Scout, null, null); break;
            case >= EscortBase: _screen.OrderMission(Mission.Escort, null, id - EscortBase); break;
            case >= RaidBase: _screen.OrderMission(Mission.Raid, id - RaidBase, null); break;
        }
    }

    /// <summary>The mission in a few words, e.g. "Patrol Tamber" or "Escort Fleet 1".</summary>
    public static string MissionText(World w, Fleet f) => f.Mission switch
    {
        Mission.None => "None",
        Mission.Escort => $"Escort {w.FindFleet(f.MissionFleetId ?? -1)?.Name ?? "?"}",
        Mission.Scout => "Scout",
        _ => $"{f.Mission} {w.FindBody(f.MissionBodyId ?? -1)?.Name ?? "?"}",
    };

    private void FillJoinMenu()
    {
        var popup = _join.GetPopup();
        popup.Clear();
        var sim = Main.Instance.Sim;
        if (sim == null || _screen.SelectedFleet is not Fleet f) return;
        foreach (var o in sim.World.Fleets.Where(o => o.Side == f.Side && o.Id != f.Id).OrderBy(o => Vec2.Distance(o.Position, f.Position)))
        {
            var moving = o.Velocity.Length > 1e-9 ? $", moving at {Format.Speed(o.Velocity.Length)}" : "";
            popup.AddItem($"{o.Name} ({o.ShipIds.Count} ships), {Format.Distance(Vec2.Distance(o.Position, f.Position))} away{moving}", o.Id);
        }
    }

    public static string StateText(SignatureState state) => state switch
    {
        SignatureState.Burn => "engines burning",
        SignatureState.Orbit => "holding orbit",
        SignatureState.Warm => "coasting, engines still warm",
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
        if (f.Chase is ChaseOrder c)
        {
            var who = Sensors.Find(w, f.Side, c.TargetFleetId, seat: true)?.Name ?? w.FindFleet(c.TargetFleetId)?.Name ?? "the enemy";
            var how = FlightPlanner.ChaseName(c.Mode);
            var text = f.Flight is Flight cf
                ? $"Chasing {who} ({how}), there {GameTime.Format(cf.ArriveHour)}"
                : $"Keeping pace with {who} ({how})";
            return c.Problem != null ? $"{text}. Can't keep up: {c.Problem}" : text;
        }
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
    /// <summary>How far the planned flight goes, along its curves.</summary>
    private static double PathLength(Flight f)
    {
        double sum = 0;
        foreach (var leg in f.Legs)
        {
            var last = leg.PositionAt(leg.Start);
            for (var i = 1; i <= 32; i++)
            {
                var p = leg.PositionAt(leg.Start + (leg.End - leg.Start) * i / 32);
                sum += Vec2.Distance(p, last);
                last = p;
            }
        }
        return sum;
    }

    private readonly GameScreen _screen;
    private Label _title = null!;
    private Button _direct = null!, _wait = null!, _sling = null!, _brake = null!, _pass = null!, _orbit = null!, _drift = null!, _go = null!;
    private Button _together = null!, _ahead = null!;
    private Button _addEnd = null!, _addMid = null!;
    private HBoxContainer _shipsRow = null!, _routeRow = null!, _arriveRow = null!, _thenRow = null!, _ctrlRow = null!, _chaseRow = null!, _keyRow = null!;
    private OptionButton _chase = null!;
    private Label _chaseInfo = null!, _hint = null!;
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
        _sling = Choice("Slingshot", "Swing past a big planet on the way when that beats the direct route (no waypoints)", () => s.Route = RouteKind.Slingshot);
        _routeRow = Row("Route", _direct, _wait, _sling);
        box.AddChild(_routeRow);

        _chase = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(250, 30) };
        foreach (var mode in GameScreen.ChaseModes) _chase.AddItem(ChaseLabel(mode), (int)mode);
        _chase.ItemSelected += i =>
        {
            if (_updating) return;
            s.Chase = (ChaseMode)_chase.GetItemId((int)i);
            _screen.Replan();
        };
        _chaseRow = new HBoxContainer();
        _chaseRow.AddThemeConstantOverride("separation", 4);
        _chaseRow.AddChild(RowLabel("Chase"));
        _chaseRow.AddChild(_chase);
        box.AddChild(_chaseRow);
        _chaseInfo = Ui.Label("", 13, Ui.Dim);
        _chaseInfo.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _chaseInfo.CustomMinimumSize = new Vector2(360, 0);
        box.AddChild(_chaseInfo);

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
        _arriveRow = Row("Arrive", _brake, _pass);
        box.AddChild(_arriveRow);
        _orbit = Choice("Orbit", "Circle the planet or moon on arrival", () => s.Hold = HoldStyle.Orbit);
        _drift = Choice("Drift", "Sit with engines cold on arrival", () => s.Hold = HoldStyle.Drift);
        _thenRow = Row("Then", _orbit, _drift);
        box.AddChild(_thenRow);
        _together = Choice("Together", "The whole fleet flies at the pace of its slowest ship", () => s.FastAhead = false);
        _ahead = Choice("Fast ahead", "Faster ships fly at their own pace, wait at the target and rejoin when the slow ones arrive", () => s.FastAhead = true);
        _shipsRow = Row("Ships", _together, _ahead);
        box.AddChild(_shipsRow);
        _addEnd = Choice("New end", "Ctrl+right-click sets a new destination; the old one becomes a waypoint on the way", () => s.AddAtEnd = true);
        _addMid = Choice("Mid point", "Ctrl+right-click adds a waypoint on the way, keeping the destination", () => s.AddAtEnd = false);
        _ctrlRow = Row("Ctrl+R-click", _addEnd, _addMid);
        box.AddChild(_ctrlRow);
        _hint = Ui.Label("Drag a waypoint or the end of the route to move it", 13, Ui.Dim);
        box.AddChild(_hint);
        var key = new HBoxContainer();
        key.AddThemeConstantOverride("separation", 10);
        key.AddChild(Ui.Label("Line:", 13, Ui.Dim));
        key.AddChild(Ui.Label("speeding up", 13, MapView.SpeedUp));
        key.AddChild(Ui.Label("braking", 13, MapView.Braking));
        key.AddChild(Ui.Label("coasting (thin)", 13, Ui.Player));
        _keyRow = key;
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

    private static string ChaseLabel(ChaseMode mode) => mode switch
    {
        ChaseMode.MatchSpeed => "Match speed (keep this gap)",
        ChaseMode.GunRange => $"Close to gun range ({GapText(mode)})",
        ChaseMode.MissileRange => $"Close to missile range ({GapText(mode)})",
        ChaseMode.ShadowGuns => $"Shadow outside their guns ({GapText(mode)})",
        ChaseMode.ShadowMissiles => $"Shadow outside their missiles ({GapText(mode)})",
        ChaseMode.Overtake => "Overtake (fly past them)",
        _ => mode.ToString(),
    };

    private static string GapText(ChaseMode mode) =>
        FlightPlanner.ChaseDistance(mode) is double d ? $"{d * 1000:#,0} km" : "";

    private static string ChaseTip(ChaseMode mode) => mode switch
    {
        ChaseMode.MatchSpeed => "Match their speed and course where you are. Keeps following their last seen track while out of sight",
        ChaseMode.GunRange => "Close in to railgun range and keep pace. On Engage the fleet then fights at close range",
        ChaseMode.MissileRange => "Sit just inside missile launch range and keep pace: destroyers fire, their railguns cannot reach",
        ChaseMode.ShadowGuns => "Keep pace just outside the reach of their railguns",
        ChaseMode.ShadowMissiles => "Keep pace just outside their missile launch range: watch them without being shot at",
        ChaseMode.Overtake => "Fly past them, to one side so you never hit them, faster than they are moving (the speed setting sets how much faster), then carry on",
        _ => "",
    };

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

    /// <summary>Both fleets' speeds, so it is clear why a chase can or cannot work.</summary>
    private static string ChaseInfo(World w, Fleet? fleet, int targetId, PlanSettings s)
    {
        var sighting = Sensors.Find(w, Side.Player, targetId, seat: true);
        if (fleet == null || sighting == null) return "";
        var theirs = sighting.Velocity.Length;
        var seen = sighting.Visible ? "in sight" : $"last seen {Format.Hours(w.Hour - sighting.Hour)} ago, assumed still on that track";
        var perf = FleetPerformance.Of(w, fleet);
        var slowest = w.ShipsOf(fleet).OrderBy(Fleets.ShipSpeed).FirstOrDefault();
        var gap = Vec2.Distance(fleet.Position, sighting.Guess(w.Hour));
        var lines = new List<string>
        {
            $"{sighting.Name}: {Format.Speed(theirs)} ({seen}), {gap * 1000:#,0} km away",
            $"This fleet: top {Format.Speed(perf.MaxSpeed)}, {Format.Speed(perf.MaxSpeed * s.Speed)} at this setting"
            + (slowest != null && w.ShipsOf(fleet).Skip(1).Any() ? $" (set by its slowest ship, {slowest.Name})" : ""),
            "It re-aims when it sees them change course.",
        };
        return string.Join("\n", lines);
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
        Mark(_sling, s.Route == RouteKind.Slingshot && p.Waypoints.Count == 0, p.Waypoints.Count == 0);
        Mark(_brake, s.Arrival == ArrivalStyle.Brake);
        Mark(_pass, s.Arrival == ArrivalStyle.FastPass);
        var canOrbit = s.Arrival == ArrivalStyle.Brake && p.TargetBodyId != null;
        var chasing = p.TargetFleetId != null;
        if (chasing) _title.Text = $"Chase: {fleet?.Name} after {p.TargetName}";
        _routeRow.Visible = !chasing;
        _arriveRow.Visible = !chasing;
        _thenRow.Visible = !chasing;
        _ctrlRow.Visible = !chasing;
        _hint.Visible = !chasing;
        _keyRow.Visible = !chasing;
        _chaseRow.Visible = chasing;
        _chaseInfo.Visible = chasing;
        if (chasing)
        {
            _updating = true;
            for (var i = 0; i < _chase.ItemCount; i++)
            {
                var mode = (ChaseMode)_chase.GetItemId(i);
                p.ChaseProblems.TryGetValue(mode, out var problem);
                _chase.SetItemText(i, ChaseLabel(mode) + (problem != null ? "  (can't)" : ""));
                _chase.SetItemTooltip(i, problem ?? ChaseTip(mode));
                if (mode == s.Chase) _chase.Select(i);
            }
            _updating = false;
            _chase.TooltipText = ChaseTip(s.Chase);
            _chaseInfo.Text = ChaseInfo(w, fleet, p.TargetFleetId!.Value, s);
        }
        Mark(_orbit, canOrbit && s.Hold == HoldStyle.Orbit, canOrbit);
        Mark(_drift, !canOrbit || s.Hold == HoldStyle.Drift, canOrbit);
        var canAhead = !chasing && fleet != null && _screen.HasFastShips(fleet) && s.Arrival == ArrivalStyle.Brake;
        _shipsRow.Visible = !chasing && fleet != null && _screen.HasFastShips(fleet);
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
            _result.Text = (plan?.Problem ?? "Working it out...")
                           + (fleet != null && fleet.Velocity.Length > perf.DeltaVLeft
                               ? $"\nThe fleet is moving at {Format.Speed(fleet.Velocity.Length)} with fuel for only {Format.Speed(perf.DeltaVLeft)} of speed change. Stop (X) brakes as much as it can."
                               : "");
            _result.AddThemeColorOverride("font_color", Ui.Danger);
            return;
        }
        var now = w.Hour;
        var waitDays = (f.DepartHour - now) / 24;
        var lines = new List<string>
        {
            waitDays < 0.05 ? "Leave: now" : $"Leave: {GameTime.Format(f.DepartHour)} (in {Format.Hours(f.DepartHour - now)})",
            $"Trip: {Format.Hours(f.ArriveHour - f.DepartHour)} · arrive {GameTime.Format(f.ArriveHour)}",
            $"Distance: {Format.Distance(PathLength(f))} flown" + (fleet != null ? $" ({Format.Distance(Vec2.Distance(fleet.Position, f.Legs.Count > 0 ? f.Legs[^1].PositionAt(f.Legs[^1].End) : fleet.Position))} in a straight line)" : ""),
            $"Fuel: {Format.Speed(plan.DeltaV)} of speed change, {plan.FuelShare:P0} of what the fleet has",
        };
        if (f.FlybyBodyId is int via && w.FindBody(via) is Body swing)
            lines.Insert(1, $"Swing past {swing.Name} {GameTime.Format(f.FlybyHour)}" + (f.FlybyGain > 0.5 ? $" (+{Format.Speed(f.FlybyGain)} for free)" : ""));
        // What is left afterwards, and whether that is enough to stop again (a fly-by ends still moving).
        var left = Math.Max(0, plan.DeltaVLeft - plan.DeltaV);
        var endSpeed = f.Legs.Count > 0 ? f.Legs[^1].VelocityAt(f.Legs[^1].End).Length : 0;
        lines.Add($"Fuel left after: {Format.Speed(left)} of speed change");
        var stranded = endSpeed > 1e-6 && left < endSpeed;
        if (endSpeed > 1e-6) lines.Add(stranded
            ? $"WARNING: arrives at {Format.Speed(endSpeed)} with fuel for only {Format.Speed(left)}: it could not stop again"
            : $"Arrives at {Format.Speed(endSpeed)}; enough fuel left to stop");
        if (canAhead && s.FastAhead) lines.Add("Fast ships go ahead, wait there and rejoin; times above are for the slowest ship");
        if (!plan.Ok) lines.Add(plan.Problem ?? "Cannot fly this");
        if (!plan.Ok && fleet != null && fleet.Velocity.Length > perf.DeltaVLeft)
            lines.Add($"It is moving at {Format.Speed(fleet.Velocity.Length)} with fuel for only {Format.Speed(perf.DeltaVLeft)} of speed change: Stop (X) brakes as much as it can.");
        _result.Text = string.Join("\n", lines);
        _result.AddThemeColorOverride("font_color", !plan.Ok ? Ui.Danger : stranded ? Ui.Rival : Ui.Text);
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
    private Button _watch = null!;
    private Battle? _shown;

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
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        box.AddChild(buttons);
        _watch = Ui.SmallButton("Watch replay", () => { if (_shown != null) _screen.OpenReplay(_shown); }, "Watch the battle again, with the moments that mattered marked");
        buttons.AddChild(_watch);
        buttons.AddChild(Ui.SmallButton("Close (Esc)", () => _screen.CloseReport(), "Close the report; R opens the last one again"));
    }

    public void Refresh(Battle b)
    {
        _shown = b;
        _watch.Disabled = b.Frames.Count == 0;
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
            lines.Add($"  Missiles: {s.MissilesFired} fired, {s.MissileHits} hit, {s.ShotDown} shot down, {s.Dodged} dodged, "
                      + (s.Decoyed > 0 ? $"{s.Decoyed} drawn off by heat sinks, " : "") + $"{s.RanDry} ran dry or lost their target");
        if (s.ShieldSoaked > 0) lines.Add($"  Shields soaked: {s.ShieldSoaked:0} damage");
        lines.Add($"  Damage done: {s.DamageDealt:0}");
        if (s.Knocked.Count > 0) lines.Add($"  Knocked out: {string.Join(", ", s.Knocked)}");
        lines.Add(s.ShipsLost.Count > 0 ? $"  Ships lost: {string.Join(", ", s.ShipsLost)}" : "  Ships lost: none");
        return string.Join("\n", lines);
    }
}
