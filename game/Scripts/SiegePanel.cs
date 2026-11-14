using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The battle view (U) laid out for a planet attack: the planet as the floor and the orbit shells as bands
/// (low, high, far) with each fleet drawn in its shell, the planet's own fighters (its station, rail batteries,
/// silos, fighter base and dome) beside it with what still shoots, a siege clock, and the orders that only exist
/// here. The per-ship strips stay as they are on either side. Only draws and issues commands.
/// </summary>
public partial class SiegePanel : PanelContainer
{
    private readonly GameScreen _screen;
    private Bands _bands = null!;
    private Label _title = null!, _defenders = null!, _clock = null!, _countdown = null!, _steps = null!;
    private VBoxContainer _threats = null!;
    private string _threatKey = "";
    private Button _low = null!, _high = null!, _far = null!, _land = null!, _board = null!;
    private MenuButton _bomb = null!;
    private int? _fleetId, _bodyId;

    public SiegePanel() : this(null!) { }
    public SiegePanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 10);
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
        v.AddThemeConstantOverride("separation", 4);
        _title = Ui.Label("", 14, Ui.Text);
        v.AddChild(_title);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        _bands = new Bands { CustomMinimumSize = new Vector2(250, 150) };
        row.AddChild(_bands);
        _defenders = Ui.Label("", 12, Ui.Text);
        _defenders.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _defenders.CustomMinimumSize = new Vector2(290, 0);
        row.AddChild(_defenders);
        v.AddChild(row);
        _clock = Ui.Label("", 12, Ui.Dim);
        _clock.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_clock);
        // The countdown: when troops may land and when the planet may give up, in days.
        _countdown = Ui.Label("", 13, Ui.Warn);
        _countdown.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_countdown);
        // Enemy fleets in reach of the fleet in orbit, with what it can do about them.
        _threats = new VBoxContainer();
        _threats.AddThemeConstantOverride("separation", 2);
        v.AddChild(_threats);
        var orders = new HFlowContainer();
        orders.AddThemeConstantOverride("h_separation", 4);
        Button Height(string text, OrbitHeight h, string tip) => Ui.SmallButton(text, () =>
        {
            if (Fleet() is Fleet f) _screen.IssueNow(new SetOrbitHeightCommand(f.Id, h), "orbit height");
        }, tip);
        _low = Height("Hold low orbit", OrbitHeight.Low, "Blockade, bomb and land from here: every gun reaches you");
        _high = Height("Stand off high", OrbitHeight.High, "Still blockades, bombs slower; only silos and station missiles reach");
        _far = Height("Far orbit", OrbitHeight.Far, "Out of reach (bar an orbital ring): watch and wait for relief, but no blockade");
        _bomb = new MenuButton { Text = "Bomb ▾", Flat = false, FocusMode = FocusModeEnum.None, TooltipText = "Pick a target: farms, mines or guns (bombing the people leaves a grudge)" };
        _bomb.AddThemeFontSizeOverride("font_size", 13);
        foreach (var (label, target) in new[] { ("Only blockade", BombTarget.None), ("Bomb farms", BombTarget.Farms), ("Bomb mines", BombTarget.Mines), ("Bomb guns", BombTarget.Guns), ("Bomb the people", BombTarget.People) })
            _bomb.GetPopup().AddItem(label, (int)target);
        _bomb.GetPopup().IdPressed += id => Main.Instance.RunGuarded("bombing", () =>
        {
            if (Fleet() is Fleet f) _screen.IssueNow(new SetBombingCommand(f.Id, (BombTarget)(int)id), "bombing");
        });
        _land = Ui.SmallButton("Land troops", () =>
        {
            if (Fleet() is Fleet f && _bodyId is int b) _screen.IssueNow(new LandTroopsCommand(f.Id, b, null), "landing");
        }, "");
        _board = Ui.SmallButton("Board the station", () =>
        {
            if (Fleet() is Fleet f && _bodyId is int b && Starbases.At(Main.Instance.Sim!.World, b) is Starbase st) _screen.IssueNow(new BoardStationCommand(f.Id, st.Id), "boarding a station");
        }, "");
        foreach (var c in new Control[] { _low, _high, _far, _bomb, _land, _board }) orders.AddChild(c);
        v.AddChild(orders);
        _steps = Ui.Label("", 12, Ui.Dim);
        _steps.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_steps);
        AddChild(v);
    }

    private Fleet? Fleet() => _fleetId is int id ? Main.Instance.Sim?.World.FindFleet(id) : null;

    /// <summary>The planet fight to show: the selected (or a battle) fleet of yours in orbit of an enemy planet, or an enemy besieging yours.</summary>
    public static (Body Planet, Fleet? Mine)? Siege(World w, IEnumerable<Fleet> fleets, Fleet? selected)
    {
        foreach (var f in (selected != null ? new[] { selected } : Array.Empty<Fleet>()).Concat(fleets))
        {
            if (f.Hold != HoldStyle.Orbit || f.OrbitBodyId is not int id || w.FindBody(id) is not Body b || !Economy.InOrbitAt(f, b) || b.Owner == null) continue;
            if (f.Side == Side.Player && b.Owner != Side.Player) return (b, f);
            if (f.Side != Side.Player && b.Owner == Side.Player) return (b, w.Fleets.FirstOrDefault(o => o.Side == Side.Player && Economy.InOrbitAt(o, b)));
        }
        return null;
    }

    public void Refresh(World w, Body b, Fleet? mine)
    {
        _bodyId = b.Id;
        _fleetId = mine?.Id;
        var attacking = b.Owner != Side.Player;
        _title.Text = attacking ? $"ATTACK ON {b.Name.ToUpperInvariant()}" : $"{b.Name.ToUpperInvariant()} UNDER ATTACK";
        _bands.World = w;
        _bands.Body = b;
        _bands.QueueRedraw();

        // The planet's fighters: its station, guns and the rest, with what still shoots.
        var lines = new List<string>();
        if (Starbases.At(w, b.Id) is Starbase st)
            lines.Add((st.Side == b.Owner ? "STATION " : "TAKEN STATION ") + Starbases.Summary(w, st));
        int Count(string kind) => Colonies.Built(b).Count(p => p.Kind == kind);
        var rails = Count(Planets.Guns);
        var silos = Count("missile-silo");
        lines.Add(rails + silos > 0
            ? $"GROUND GUNS {rails} rail batter{(rails == 1 ? "y" : "ies")} (low orbit, {Starbases.D.RailReachMm:0} Mm), {silos} silo{(silos == 1 ? "" : "s")} (high orbit, {Starbases.D.SiloReachMm:0} Mm)"
            : "GROUND GUNS silenced or none");
        if (Count("fighter-base") > 0) lines.Add($"FIGHTER BASE {Count("fighter-base")}");
        if (Count("shield-dome") > 0) lines.Add($"SHIELD DOME {Count("shield-dome")} (bombing gets through less)");
        lines.Add($"GARRISON about {Planets.GarrisonOf(b):0.#}k" + (b.LandingFactor > 1 ? $" (x{b.LandingFactor:0} while the fortress stands)" : ""));
        _defenders.Text = string.Join("\n", lines);

        // The siege clock.
        var clock = new List<string>();
        if (Economy.Blockaded(w, b)) { } // the countdown line below gives the day
        else if (Planets.Contested(w, b)) clock.Add("Orbit contested: no blockade while its guns or station fire");
        else clock.Add("No blockade");
        clock.Add($"happiness {b.Happiness:0} (gives up below {Planets.SurrenderLine(w, attacking ? Side.Player : Side.Rival):0})");
        clock.Add($"food {Colonies.Held(b, "food"):0}");
        if (Ground.HelpComing(w, b) is var (helper, days)) clock.Add($"relief: {helper.Name} in {days:0.#} days");
        _clock.Text = string.Join(" · ", clock);
        _countdown.Text = Countdown(w, b, attacking ? Side.Player : b.BlockadedBy ?? Side.Rival, attacking);
        Threats(w, b, mine);

        var f = mine;
        foreach (var c in new Control[] { _low, _high, _far, _bomb, _land, _board }) c.Visible = f != null && attacking;
        _steps.Visible = f != null && attacking;
        if (f == null || !attacking) return;
        _steps.Text = Steps(w, f, b);
        static Color On(bool on) => on ? Ui.Player : new Color(1, 1, 1, 0.7f);
        _low.Modulate = On(f.OrbitHeight == OrbitHeight.Low);
        _high.Modulate = On(f.OrbitHeight == OrbitHeight.High);
        _far.Modulate = On(f.OrbitHeight == OrbitHeight.Far);
        _bomb.Text = f.Bombing == BombTarget.None ? "Bomb ▾" : $"Bombing {f.Bombing.ToString().ToLowerInvariant()} ▾";
        var land = Planets.CanLand(w, f, b);
        _land.Disabled = land != null;
        _land.TooltipText = land ?? $"Land {Planets.TroopsAboard(w, f):0.#}k troops, Auto picks the plan";
        var station = Starbases.At(w, b.Id);
        _board.Visible = station != null && station.Side != f.Side;
        if (_board.Visible)
        {
            var why = Starbases.CanBoard(w, f, station);
            _board.Disabled = why != null;
            _board.TooltipText = why ?? $"{Starbases.GarrisonK(w, station!):0.#}k hold it; take it and its hull, stores and slipway are yours";
        }
    }

    /// <summary>
    /// The siege countdown in days: when the besieger's troops may land and when the planet may give up (never before
    /// MinBlockadeDays, and only once happiness is under the surrender line, which falls about BlockadedHappinessPerDay a day).
    /// </summary>
    private static string Countdown(World w, Body b, Side besieger, bool ours)
    {
        if (!Economy.Blockaded(w, b)) return ours ? "COUNTDOWN not started: the clock runs once the blockade holds (armed ships in low or high orbit, no guns firing)." : "";
        var day = b.BlockadeHours / 24;
        var parts = new List<string> { $"BLOCKADE DAY {day:0.#}" };
        var land = Planets.LandWaitDays(w, besieger, b);
        parts.Add(land > 0 ? $"troops may land in {land:0.#} days (day {Planets.D.LandAfterBlockadeDays:0})" : "troops may land NOW");
        var line = Planets.SurrenderLine(w, besieger);
        var minLeft = Math.Max(0, Planets.D.MinBlockadeDays - day);
        var gap = b.Happiness - line;
        var rate = Math.Max(0.1, Planets.D.BlockadedHappinessPerDay);
        var unhappy = gap > 0 ? gap / rate : 0;
        var giveUp = Math.Max(minLeft, unhappy);
        parts.Add(giveUp <= 0.05
            ? "it may give up any hour now"
            : $"it may give up in about {giveUp:0} days (not before day {Planets.D.MinBlockadeDays:0}; happiness {b.Happiness:0} must fall below {line:0}" + (Ground.HelpComing(w, b) != null ? ", slower while relief is coming" : "") + ")");
        return string.Join(" · ", parts);
    }

    /// <summary>What the fleet in orbit can do next, in plain words, with when each becomes possible.</summary>
    private static string Steps(World w, Fleet f, Body b)
    {
        var steps = new List<string> { "Your options:" };
        steps.Add(f.OrbitHeight == OrbitHeight.Far ? "· Far orbit keeps you safe but does NOT blockade: drop to high or low orbit to start the clock." : "· Keep holding: the countdown above runs while you blockade.");
        steps.Add("· Bomb farms to starve it (it gives up sooner), guns to silence them, mines to cut its output; bombing people leaves a grudge.");
        var land = Planets.CanLand(w, f, b);
        steps.Add(land == null ? "· Land troops now: Auto picks the plan." : $"· Land troops: {land}.");
        steps.Add("· Lift the blockade: give the fleet any other order (fly off, or Fight it ▸ on a fleet above).");
        return string.Join("\n", steps);
    }

    /// <summary>Enemy fleets in reach of the fleet in orbit: how far, what reaches them, why fire may be held, and a button to fight.</summary>
    private void Threats(World w, Body b, Fleet? mine)
    {
        var enemies = mine == null ? new List<Fleet>()
            : w.Fleets.Where(o => o.Side != mine.Side && !Economy.InOrbitAt(o, b) && Sensors.Sees(w, mine.Side, o)
                                  && Vec2.Distance(o.Position, mine.Position) <= GameData.Combat.ContactRangeMm).OrderBy(o => Vec2.Distance(o.Position, mine.Position)).ToList();
        var key = string.Join(",", enemies.Select(o => o.Id)) + "|" + mine?.Id;
        if (key != _threatKey)
        {
            _threatKey = key;
            foreach (var c in _threats.GetChildren()) c.QueueFree();
            foreach (var o in enemies)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                var text = Ui.Label("", 12, Ui.Rival);
                text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                text.SetMeta("fleet", o.Id);
                row.AddChild(text);
                var id = o.Id;
                row.AddChild(Ui.SmallButton("Fight it ▸", () => { if (Fleet() is Fleet f) _screen.OpenEngagement(f.Id, id); },
                    "Open the battle view against this fleet: chase it, hold at missile range, burn past, dodge, long-range fire. Choosing to chase lifts the blockade."));
                _threats.AddChild(row);
            }
        }
        foreach (var row in _threats.GetChildren().OfType<HBoxContainer>())
        {
            var text = row.GetChild<Label>(0);
            if (mine == null || w.FindFleet((int)text.GetMeta("fleet")) is not Fleet o) continue;
            var d = Vec2.Distance(o.Position, mine.Position);
            var missiles = w.ShipsOf(mine).Any(s => s.Missiles > 0) ? Heat.LaunchRange(mine) : 0;
            var rails = Secrecy.RailRange(w, mine);
            var reach = d <= rails ? "in railgun and missile reach: your ships fire on it from orbit"
                : missiles > 0 && d <= missiles ? $"in missile reach: your ships launch at it from orbit (guns at {rails * 1000:#,0} km)"
                : missiles > 0 ? $"out of reach: missiles launch at {missiles * 1000:#,0} km, guns at {rails * 1000:#,0} km"
                : $"out of reach: guns at {rails * 1000:#,0} km";
            var held = o.Retreating && mine.Chase?.TargetFleetId != o.Id ? " HOLDING FIRE: it is pulling back, and your fleets let a retreating fleet go (Fight it ▸ to chase)." : "";
            text.Text = $"{o.Name} ({o.ShipIds.Count} ships) {d * 1000:#,0} km away, {reach}.{held}";
        }
    }

    /// <summary>The planet as the floor and the three shells above it, with each fleet in its shell.</summary>
    private partial class Bands : Control
    {
        public World? World;
        public Body? Body;

        public override void _Draw()
        {
            if (World is not World w || Body is not Body b) return;
            var size = Size;
            var font = ThemeDB.FallbackFont;
            // Floor: the planet.
            var floor = new Rect2(0, size.Y - 26, size.X, 26);
            DrawRect(floor, Ui.SideColor(b.Owner) * new Color(1, 1, 1, 0.35f));
            DrawString(font, new Vector2(6, size.Y - 9), $"{b.Name} (ground guns, garrison)", HorizontalAlignment.Left, -1, 11, Ui.Text);
            var shells = new[] { (OrbitHeight.Low, "LOW · rails, silos, station"), (OrbitHeight.High, "HIGH · silos, station missiles"), (OrbitHeight.Far, "FAR · out of reach") };
            var bandH = (size.Y - 30) / 3;
            for (var i = 0; i < shells.Length; i++)
            {
                var y = size.Y - 30 - (i + 1) * bandH;
                DrawRect(new Rect2(0, y, size.X, bandH - 2), new Color(0.3f, 0.45f, 0.7f, 0.10f + 0.05f * (2 - i)));
                DrawString(font, new Vector2(6, y + 12), shells[i].Item2, HorizontalAlignment.Left, -1, 10, Ui.Dim);
                var x = 8f;
                foreach (var f in w.Fleets.Where(f => Economy.InOrbitAt(f, b) && f.OrbitHeight == shells[i].Item1).OrderBy(f => f.Side).ThenBy(f => f.Id))
                {
                    var label = $"{f.Name} ({f.ShipIds.Count})";
                    DrawString(font, new Vector2(x, y + bandH - 8), label, HorizontalAlignment.Left, -1, 11, Ui.SideColor(f.Side));
                    x += Mathf.Min(150, 7 * label.Length + 12);
                }
            }
            // The station sits in low orbit.
            if (Starbases.At(w, b.Id) is Starbase st)
            {
                var y = size.Y - 30 - bandH;
                var c = Ui.SideColor(st.Side);
                DrawRect(new Rect2(size.X - 70, y + 4, 60, bandH - 10), c * new Color(1, 1, 1, 0.5f));
                DrawString(font, new Vector2(size.X - 66, y + bandH / 2 + 4), Starbases.ClassOf(st).Short, HorizontalAlignment.Left, -1, 12, Ui.Text);
            }
        }
    }
}
