using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The battle view's left column: your ships, grouped by fleet. Each ship has labelled bars (Hull, Weapons, Engines,
/// Defence, Crew, Heat), missiles and heat sinks left, Eject sink, and a tick: tick ships and press Split to make them
/// a fleet of their own (with its own tab and orders). Hover a ship for its situation. Big fleets fold each ship to one line.
/// </summary>
public partial class OurShipsColumn : VBoxContainer
{
    private readonly GameScreen _screen;
    private readonly VBoxContainer _list = new();
    private readonly Button _split, _splitRun, _splitHold;
    private readonly HashSet<int> _ticked = new();
    private readonly List<Action<World>> _live = new();
    private string _key = "";

    /// <summary>More ships than this and each ship folds to one line.</summary>
    public const int CompactOver = 6;

    public OurShipsColumn(GameScreen screen)
    {
        _screen = screen;
        AddThemeConstantOverride("separation", 4);
        AddChild(Ui.Label("YOUR SHIPS", 12, Ui.Dim));
        _list.AddThemeConstantOverride("separation", 4);
        AddChild(_list);
        _split = Ui.SmallButton("", () => SplitTicked(SplitThen.OwnOrders), "Make the ticked ships a fleet of their own: it gets its own tab and orders (for example to finish off a damaged enemy). In a fight it rejoins when done");
        _splitRun = Ui.SmallButton("Split and run", () => SplitTicked(SplitThen.RunHome), "Split the ticked ships off and send them to the nearest yard at once; they rejoin once mended");
        _splitHold = Ui.SmallButton("Split and hold back", () => SplitTicked(SplitThen.HoldBack),
            "Split the ticked ships off to hold back from this enemy: just inside missile range if they carry missiles, else just outside their railguns. They rejoin once the fight is over");
        var splits = new HFlowContainer();
        splits.AddThemeConstantOverride("h_separation", 4);
        foreach (var b in new[] { _split, _splitRun, _splitHold })
        {
            b.AddThemeFontSizeOverride("font_size", 12);
            splits.AddChild(b);
        }
        AddChild(splits);
    }

    public void Refresh(World w, List<Fleet> fleets, int? lit)
    {
        var ships = fleets.SelectMany(f => w.ShipsOf(f)).ToList();
        _ticked.RemoveWhere(id => ships.All(s => s.Id != id));
        var key = string.Join(";", fleets.Select(f => $"{f.Id}:{string.Join(",", f.ShipIds)}"));
        if (key != _key && !Ui.MouseHeld)
        {
            _key = key;
            _live.Clear();
            Ui.ClearChildren(_list);
            var compact = ships.Count > CompactOver;
            if (compact) _list.AddChild(Ui.Label("Bars: Hull · Weapons · Engines · Defence · Crew · Heat (hover a ship for more)", 10, Ui.Dim));
            foreach (var f in fleets)
            {
                _list.AddChild(FleetHead(f.Id));
                foreach (var s in w.ShipsOf(f)) _list.AddChild(compact ? CompactStrip(s.Id) : Strip(s.Id));
            }
            if (fleets.Count == 0) _list.AddChild(Ui.Label("No ships of yours here (select a fleet to see its ships)", 12, Ui.Dim));
        }
        foreach (var live in _live) live(w);
        foreach (var c in _list.GetChildren())
            if (c is Control ctl && ctl.HasMeta("fleet"))
                ctl.Modulate = lit == null || ctl.GetMeta("fleet").AsInt32() == lit ? Colors.White : new Color(1, 1, 1, 0.55f);
        _split.Text = _ticked.Count == 0 ? "Tick ships to split them off" : $"Split {_ticked.Count} ticked ship{(_ticked.Count == 1 ? "" : "s")} into a new fleet";
        _split.Disabled = _ticked.Count == 0;
        _splitRun.Disabled = _splitHold.Disabled = _ticked.Count == 0;
        _splitRun.Visible = _splitHold.Visible = _ticked.Count > 0;
    }

    private void SplitTicked(SplitThen then)
    {
        var w = Main.Instance.Sim!.World;
        foreach (var g in _ticked.Select(w.FindShip).OfType<Ship>().GroupBy(s => s.FleetId))
        {
            // Splitting every ship of a fleet would leave it empty: keep one back.
            var ids = g.Select(s => s.Id).ToList();
            if (w.FindFleet(g.Key) is Fleet f && ids.Count >= f.ShipIds.Count) ids.RemoveAt(0);
            if (ids.Count > 0) _screen.IssueNow(new SplitOffCommand(g.Key, ids, then), "split ticked ships");
        }
        _ticked.Clear();
    }

    private Control FleetHead(int fleetId)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        row.SetMeta("fleet", fleetId);
        var name = Ui.Label("", 12, Ui.Player);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        name.CustomMinimumSize = new Vector2(200, 0);
        row.AddChild(name);
        var rejoin = CardBuilder.Small("Rejoin", "Fly back and merge into the fleet it split from", () =>
        {
            var w = Main.Instance.Sim!.World;
            if (w.FindFleet(fleetId) is { RejoinFleetId: int into } && w.FindFleet(into) != null)
                _screen.IssueNow(new GatherFleetsCommand(into, new List<int> { fleetId }, 1.0), "rejoin");
        });
        row.AddChild(rejoin);
        var whenDone = CardBuilder.Small("", "When its task is done (the fight over, its target gone): fly back and merge into the fleet it split from", () =>
        {
            var w = Main.Instance.Sim!.World;
            if (w.FindFleet(fleetId) is { RejoinFleetId: int } f)
                _screen.IssueNow(new SetRejoinWhenCommand(f.Id, WhenDone(f) == RejoinWhen.Done ? RejoinWhen.Never : RejoinWhen.Done), "rejoin when done");
        });
        row.AddChild(whenDone);
        _live.Add(w =>
        {
            if (w.FindFleet(fleetId) is not Fleet f) { name.Text = ""; rejoin.Visible = false; return; }
            var all = w.ShipsOf(f).ToList();
            var hull = all.Count == 0 ? 0 : all.Average(x => x.Armour / Math.Max(1, Tech.MaxArmour(w, x)));
            var racks = all.Sum(x => Tech.Racks(w, x));
            name.Text = $"{f.Name} · {all.Count} ship{(all.Count == 1 ? "" : "s")} · hull {hull:P0}"
                        + (racks > 0 ? $" · missiles {all.Sum(x => x.Missiles)}/{racks}" : "") + $" · sinks {all.Sum(x => x.HeatSinks)}"
                        + (f.Retreating ? " · PULLING BACK" : f.HoldOn ? " · holding on" : "");
            var into = f.RejoinFleetId is int r ? w.FindFleet(r) : null;
            rejoin.Visible = into != null && into.Id != f.Id;
            if (into != null) rejoin.Text = $"Rejoin {into.Name}";
            // Mended-then-rejoin (a ship pulled out hurt) has its own wording in the fleet panel; here only done/never.
            var when = WhenDone(f);
            whenDone.Visible = rejoin.Visible && when != RejoinWhen.Mended;
            whenDone.Text = when == RejoinWhen.Done ? "when done: rejoin ✓" : "when done: rejoin";
            whenDone.Modulate = when == RejoinWhen.Done ? Ui.Player : new Color(1, 1, 1, 0.7f);
        });
        return row;
    }

    /// <summary>The fleet's rejoin rule, counting an order not yet applied.</summary>
    private static RejoinWhen WhenDone(Fleet f) =>
        Main.Instance.Sim!.PendingCommands.OfType<SetRejoinWhenCommand>().LastOrDefault(c => c.FleetId == f.Id)?.When ?? f.RejoinWhen;

    private CheckBox Tick(int shipId)
    {
        var tick = new CheckBox { FocusMode = FocusModeEnum.None, TooltipText = "Tick to split this ship off with the others ticked" };
        tick.Toggled += on => { if (on) _ticked.Add(shipId); else _ticked.Remove(shipId); };
        _live.Add(_ => tick.SetPressedNoSignal(_ticked.Contains(shipId)));
        return tick;
    }

    private Label NameLabel(int shipId, int size)
    {
        var name = Ui.Label("", size, Ui.Player);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.ClipText = true;
        name.MouseFilter = MouseFilterEnum.Pass;
        name.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) _screen.ShipCardId = shipId; };
        return name;
    }

    private Control Strip(int shipId)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 2);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 4);
        head.AddChild(Tick(shipId));
        var name = NameLabel(shipId, 13);
        head.AddChild(name);
        var state = Ui.Label("", 11, Ui.Dim);
        head.AddChild(state);
        v.AddChild(head);
        var bars = Bars(full: true);
        v.AddChild(bars.Grid);
        var foot = new HBoxContainer();
        foot.AddThemeConstantOverride("separation", 6);
        var extra = Ui.Label("", 11, Ui.Text);
        extra.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foot.AddChild(extra);
        var eject = CardBuilder.Small("Eject sink", "Dump a heat sink from this ship now: drops its heat at once, and can draw off a missile closing in", () =>
        {
            if (Main.Instance.Sim!.World.FindShip(shipId) is Ship sh) _screen.IssueNow(new EjectShipSinkCommand(sh.FleetId, shipId), "eject heat sink");
        });
        foot.AddChild(eject);
        v.AddChild(foot);
        var panel = Ui.Panel(v, 6);
        _live.Add(w =>
        {
            if (w.FindShip(shipId) is not Ship s) { panel.Modulate = new Color(1, 1, 1, 0.35f); state.Text = "lost"; return; }
            name.Text = $"{s.Name} ({s.Code})";
            bars.Set(w, s);
            var cls = Fitting.Spec(s);
            var bits = new List<string>();
            if (cls.Missiles > 0) bits.Add($"Missiles {s.Missiles}/{Tech.Racks(w, s)}");
            bits.Add($"Sinks {s.HeatSinks}");
            if (Heat.MaxShield(w, s) > 0) bits.Add($"Shield {s.Shield / Math.Max(1e-6, Heat.MaxShield(w, s)):P0}");
            extra.Text = string.Join(" · ", bits);
            extra.AddThemeColorOverride("font_color", cls.Missiles > 0 && s.Missiles == 0 ? Ui.Warn : Ui.Text);
            StateLabel(state, s);
            eject.Disabled = s.HeatSinks <= 0 || s.Heat <= 0;
            eject.Modulate = s.Overheated && s.HeatSinks > 0 ? Ui.Danger : Colors.White;
            panel.TooltipText = Situation(w, s);
        });
        return panel;
    }

    private Control CompactStrip(int shipId)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        row.AddChild(Tick(shipId));
        var name = NameLabel(shipId, 12);
        name.CustomMinimumSize = new Vector2(110, 0);
        row.AddChild(name);
        var bars = Bars(full: false);
        row.AddChild(bars.Grid);
        var extra = Ui.Label("", 10, Ui.Dim);
        extra.CustomMinimumSize = new Vector2(60, 0);
        row.AddChild(extra);
        _live.Add(w =>
        {
            if (w.FindShip(shipId) is not Ship s) { row.Modulate = new Color(1, 1, 1, 0.35f); return; }
            name.Text = s.Crippled ? $"{s.Code} CRIPPLED" : s.Overheated ? $"{s.Code} HOT" : $"{s.Name} ({s.Code})";
            name.AddThemeColorOverride("font_color", s.Crippled || s.Overheated ? Ui.Danger : Ui.Player);
            bars.Set(w, s);
            extra.Text = (Fitting.Spec(s).Missiles > 0 ? $"m{s.Missiles} " : "") + $"s{s.HeatSinks}";
            extra.TooltipText = "m: missiles left · s: heat sinks left";
            name.TooltipText = Situation(w, s);
        });
        return row;
    }

    internal static void StateLabel(Label state, Ship s)
    {
        state.Text = s.Crippled ? "CRIPPLED" : s.Overheated ? "OVERHEATED" : s.Disabled is Module m ? $"{Combat.ModuleName(m)} out" : "";
        state.AddThemeColorOverride("font_color", s.Crippled || s.Overheated ? Ui.Danger : Ui.Dim);
    }

    /// <summary>The six labelled bars of a ship. Full: three across, with words; compact: six small ones in a row.</summary>
    internal static ShipStatBars Bars(bool full) => new(full);

    /// <summary>A ship's situation in words, for hovering it: what its fleet is doing, who is near and in reach, what is coming in.</summary>
    internal static string Situation(World w, Ship s)
    {
        var lines = new List<string> { $"{s.Name} ({s.Code}), {Fitting.Spec(s).Name}" };
        if (w.FleetOf(s) is Fleet f)
        {
            lines.Add($"{f.Name}: {FleetPanel.DoingNow(w, f)}");
            if (Combat.NearestEnemyFleet(w, f, GameData.Combat.ContactRangeMm * 4) is Fleet e && Sensors.Sees(w, Side.Player, e))
            {
                var d = Vec2.Distance(e.Position, s.Position);
                var reach = new List<string>();
                if (d <= Tech.RailRange(w, Side.Player)) reach.Add("in your railgun reach");
                if (d <= Secrecy.RailRange(w, e)) reach.Add("in their railgun reach");
                if (d <= GameData.Combat.Missile.LaunchRangeMm) reach.Add("in missile range");
                lines.Add($"Nearest enemy {e.Name}, {Format.Distance(d)} away" + (reach.Count > 0 ? $": {string.Join(", ", reach)}" : ""));
            }
        }
        var incoming = w.Missiles.Count(m => m.TargetShipId == s.Id);
        if (incoming > 0) lines.Add($"{incoming} missile{(incoming == 1 ? "" : "s")} coming at it");
        var cap = Math.Max(1e-6, Heat.Capacity(s));
        lines.Add($"Hull {s.Armour / Math.Max(1, Tech.MaxArmour(w, s)):P0} · heat {s.Heat / cap:P0} · supplies {s.Supplies / Math.Max(1e-6, Fitting.Spec(s).Supplies):P0} · crew {s.Crew:P0}");
        if (s.Knocked.Count > 0) lines.Add($"Knocked out: {string.Join(", ", s.Knocked.Select(Combat.ModuleName))}");
        if (s.Overheated) lines.Add("OVERHEATED: weapons hold fire until it cools; eject a sink");
        lines.Add("Click for its ship card");
        return string.Join("\n", lines);
    }
}

/// <summary>Hull, Weapons, Engines, Defence, Crew and Heat bars for one ship.</summary>
public sealed class ShipStatBars
{
    public readonly GridContainer Grid;
    private readonly StatBar _hull, _weapons, _engines, _defence, _crew, _heat;

    public ShipStatBars(bool full)
    {
        Grid = new GridContainer { Columns = full ? 3 : 6 };
        Grid.AddThemeConstantOverride("h_separation", 3);
        Grid.AddThemeConstantOverride("v_separation", 2);
        StatBar Make(string word, string letter) => new(full ? word : letter) { CustomMinimumSize = full ? new Vector2(100, 14) : new Vector2(26, 12) };
        _hull = Make("Hull", "H");
        _weapons = Make("Weapons", "W");
        _engines = Make("Engines", "E");
        _defence = Make("Defence", "D");
        _crew = Make("Crew", "C");
        _heat = Make("Heat", "Ht");
        foreach (var b in new[] { _hull, _weapons, _engines, _defence, _crew, _heat }) Grid.AddChild(b);
    }

    public void Set(World w, Ship s, bool guess = false)
    {
        var cls = Fitting.Spec(s);
        var tag = guess ? " (as last seen)" : "";
        _hull.Set(s.Armour / Math.Max(1, Tech.MaxArmour(w, s)), $"Hull {s.Armour:0} of {Tech.MaxArmour(w, s):0}{tag}");
        var armed = new List<string>();
        if (cls.Railguns > 0) armed.Add(s.Out(Module.Railguns) ? "railguns OUT" : "railguns");
        if (cls.Lances > 0) armed.Add(s.Out(Module.Railguns) ? "lances OUT" : "lances");
        if (cls.Missiles > 0) armed.Add(s.Out(Module.Missiles) ? "missiles OUT" : "missiles");
        _weapons.Set(s.Weapons, $"Weapons {s.Weapons:P0}: {(armed.Count > 0 ? string.Join(", ", armed) : "unarmed")}", s.Out(Module.Railguns) || s.Out(Module.Missiles));
        _engines.Set(s.Engines, $"Engines {s.Engines:P0}", s.Out(Module.Engines));
        _defence.Set(s.Defence, $"Defence (point defence, shields) {s.Defence:P0}" + (cls.PointDefence > 0 && s.Out(Module.PointDefence) ? ": point defence OUT" : ""), s.Out(Module.PointDefence));
        _crew.Set(s.Crew, $"Crew {s.Crew:P0}");
        var cap = Math.Max(1e-6, Heat.Capacity(s));
        _heat.Set(s.Heat / cap, s.Overheated ? "OVERHEATED: weapons hold fire until it cools; eject a sink" : $"Heat {s.Heat / cap:P0} of capacity", s.Overheated, hot: true);
    }
}

/// <summary>
/// The battle view's right column: each enemy fleet in the fight, by what you know. In sight: a strip per ship (solid
/// bars). Out of sight: the ships you made out before as dashed rows ("seen 2 h ago"), then any you only counted; after
/// 24 hours "lost track". A guess at their missiles and heat sinks left. An odds bar at the top.
/// </summary>
public partial class EnemyColumn : VBoxContainer
{
    private readonly GameScreen _screen;
    private readonly OddsBar _odds = new();
    private readonly Label _oddsText;
    private readonly VBoxContainer _list = new();
    private readonly List<Action<World>> _live = new();
    private string _key = "";

    /// <summary>Hidden ships stay listed this long after the last sighting, then show "lost track".</summary>
    public const double GhostHours = 24;

    public EnemyColumn(GameScreen screen)
    {
        _screen = screen;
        AddThemeConstantOverride("separation", 4);
        AddChild(Ui.Label("ENEMY: WHAT YOU KNOW", 12, Ui.Dim));
        AddChild(_odds);
        _oddsText = Ui.Label("", 11, Ui.Dim);
        _oddsText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _oddsText.CustomMinimumSize = new Vector2(300, 0);
        AddChild(_oddsText);
        _list.AddThemeConstantOverride("separation", 4);
        AddChild(_list);
    }

    public void Refresh(World w, List<Fleet> ours, List<int> enemyIds)
    {
        var sightings = enemyIds.Select(id => Sensors.Find(w, Side.Player, id, seat: true)).OfType<Sighting>().ToList();
        var key = string.Join(";", sightings.Select(s => $"{s.FleetId}:{s.Visible}:{s.Ships}:{s.Classes.Count}:{Gone(w, s)}:{Cleared(s)}"
                                                         + (s.Visible && w.FindFleet(s.FleetId) is Fleet f ? ":" + string.Join(",", f.ShipIds) : "")));
        if (key != _key && !Ui.MouseHeld)
        {
            _key = key;
            _live.Clear();
            Ui.ClearChildren(_list);
            foreach (var s in sightings) Fill(w, s);
            if (sightings.Count == 0) _list.AddChild(Ui.Label("No enemy fleet in sight or on record here", 12, Ui.Dim));
        }
        foreach (var live in _live) live(w);

        // Odds: your power in this fight against theirs, as the sim reckons it for "engage if stronger" (a guess for fleets out of sight).
        var mine = ours.Sum(f => Stats.Power(w, f));
        var theirs = sightings.Sum(s => s.Visible && w.FindFleet(s.FleetId) is Fleet f ? Stats.Power(w, f) : HelpCalls.EnemyPower(s));
        var guess = sightings.Any(s => !s.Visible || s.Classes.Count < s.Ships);
        _odds.Set(mine, theirs, guess);
        var ratio = theirs > 0 ? mine / theirs : double.PositiveInfinity;
        _oddsText.Text = theirs <= 0 ? "Odds: they have nothing that fights" :
            $"Odds {(double.IsInfinity(ratio) ? "∞" : ratio.ToString("0.0"))} to 1 {(ratio >= 1 ? "for you" : "against you")}"
            + (guess ? " (a guess: some of their ships are unseen)" : "")
            + $" · Engage if stronger wants {Combat.StrongerBy:0.#} to 1";
    }

    private static bool Gone(World w, Sighting s) => !s.Visible && w.Hour - s.Hour > GhostHours;
    private bool Cleared(Sighting s) => !s.Visible && _screen.GhostCleared(s.FleetId, s.Hour);

    private void Fill(World w, Sighting s)
    {
        var id = s.FleetId;
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 4);
        var name = Ui.Label("", 12, Ui.Rival);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        name.CustomMinimumSize = new Vector2(220, 0);
        head.AddChild(name);
        if (!s.Visible)
            head.AddChild(CardBuilder.Small("Clear", "Forget the ships last seen here (they come back when seen again)", () => _screen.ClearGhost(id, s.Hour)));
        _list.AddChild(head);
        var guess = Ui.Label("", 11, Ui.Dim);
        guess.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        guess.CustomMinimumSize = new Vector2(300, 0);
        _list.AddChild(guess);
        _live.Add(world =>
        {
            if (Sensors.Find(world, Side.Player, id, seat: true) is not Sighting now) return;
            var age = world.Hour - now.Hour;
            name.Text = $"{now.Name} · {now.Ships} ship{(now.Ships == 1 ? "" : "s")} · " + (now.Visible ? "in sight" : $"not seen for {Lightlag.Sim.Core.Engagement.Duration(age)}");
            guess.Text = Guess(world, now);
        });

        if (Cleared(s)) { _list.AddChild(Ui.Label("  (cleared: shows again when seen)", 11, Ui.Dim)); return; }
        if (Gone(w, s))
        {
            _list.AddChild(Ui.Label($"  Lost track: last seen over {GhostHours:0} h ago", 11, Ui.Dim));
            return;
        }
        if (s.Visible && w.FindFleet(id) is Fleet fleet)
        {
            foreach (var ship in w.ShipsOf(fleet)) _list.AddChild(SeenStrip(ship.Id));
            return;
        }
        // Out of sight: the ships made out before, dashed, then any only counted.
        foreach (var g in s.Classes.GroupBy(c => c).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var cls = GameData.ShipClasses.TryGetValue(g.Key, out var k) ? k : null;
            _list.AddChild(new GhostRow($"{g.Count()}× {cls?.Name ?? g.Key}{(cls != null ? $" ({cls.Short})" : "")}", () =>
                $"seen {Lightlag.Sim.Core.Engagement.Duration(Math.Max(0, Main.Instance.Sim!.World.Hour - s.ClassesHour))} ago" + (cls != null ? $" · {Arms(cls)}" : "")));
        }
        var counted = s.Ships - s.Classes.Count;
        if (counted > 0)
            _list.AddChild(new GhostRow($"{counted} more ship{(counted == 1 ? "" : "s")}, class unknown", () => $"come within {Format.Distance(Sensors.IdentifyRange(Main.Instance.Sim!.World, Side.Player))} to tell"));
    }

    private static string Arms(ShipClass k)
    {
        var arms = new List<string>();
        if (k.Railguns > 0) arms.Add($"{k.Railguns} railgun{(k.Railguns == 1 ? "" : "s")}");
        if (k.Lances > 0) arms.Add($"{k.Lances} lance{(k.Lances == 1 ? "" : "s")}");
        if (k.Missiles > 0) arms.Add("missiles");
        if (k.PointDefence > 0) arms.Add("point defence");
        return arms.Count > 0 ? string.Join(", ", arms) : "unarmed";
    }

    /// <summary>
    /// Their missiles and heat sinks left, as a guess: full racks and sinks for the classes made out, less the missiles
    /// seen fired and heat sinks seen thrown in this fight. Ships never made out add nothing.
    /// </summary>
    private static string Guess(World w, Sighting s)
    {
        var classes = s.Classes.Select(c => GameData.ShipClasses.TryGetValue(c, out var k) ? k : null).OfType<ShipClass>().ToList();
        if (classes.Count == 0) return "Missiles and heat sinks left: unknown until you make out their ships";
        var foes = w.ActiveBattle is Battle b && b.Foes(Side.Player).Fleets.Contains(s.Name) ? b.Foes(Side.Player) : null;
        var fired = foes?.MissilesFired ?? 0;
        var thrown = foes?.SinksThrown ?? 0;
        var racks = classes.Sum(k => k.Missiles);
        var sinks = classes.Sum(k => k.HeatSinks);
        var unknown = s.Ships > classes.Count ? $", plus {s.Ships - classes.Count} unknown ship{(s.Ships - classes.Count == 1 ? "" : "s")}" : "";
        var missiles = racks == 0 ? "no missile racks seen" : $"up to {Math.Max(0, racks - fired)} missiles left" + (fired > 0 ? $" ({fired} seen fired)" : "");
        var sinksLeft = Math.Max(0, sinks - thrown);
        var sinkText = sinksLeft == 0 ? "NO heat sinks left (all seen thrown)" : $"up to {sinksLeft} heat sinks" + (thrown > 0 ? $" ({thrown} seen thrown)" : "");
        return $"Guess: {missiles}, {sinkText}{unknown}";
    }

    private Control SeenStrip(int shipId)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 2);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        var name = Ui.Label("", 12, Ui.Rival);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.ClipText = true;
        name.MouseFilter = MouseFilterEnum.Pass;
        name.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) _screen.ShipCardId = shipId; };
        head.AddChild(name);
        var state = Ui.Label("", 11, Ui.Dim);
        head.AddChild(state);
        v.AddChild(head);
        var bars = new ShipStatBars(full: true);
        v.AddChild(bars.Grid);
        var panel = Ui.Panel(v, 6);
        _live.Add(w =>
        {
            if (w.FindShip(shipId) is not Ship s) { panel.Modulate = new Color(1, 1, 1, 0.35f); state.Text = "gone"; return; }
            name.Text = $"{s.Name} ({s.Code}), {Fitting.Spec(s).Name}";
            bars.Set(w, s);
            OurShipsColumn.StateLabel(state, s);
            panel.TooltipText = OurShipsColumn.Situation(w, s);
        });
        return panel;
    }
}

/// <summary>An enemy ship known from an earlier sighting but out of sight now: a dashed outline, dimmed.</summary>
public partial class GhostRow : Control
{
    private readonly string _title;
    private readonly Func<string> _detail;
    public GhostRow(string title, Func<string> detail)
    {
        _title = title;
        _detail = detail;
        CustomMinimumSize = new Vector2(300, 34);
        MouseFilter = MouseFilterEnum.Pass;
        TooltipText = "Out of sight: as last seen. It may have moved, been hurt or mended since.";
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var c = new Color(Ui.Rival, 0.6f);
        var r = new Rect2(Vector2.Zero, Size);
        foreach (var (a, b) in new[] { (r.Position, new Vector2(r.End.X, r.Position.Y)), (new Vector2(r.End.X, r.Position.Y), r.End), (r.End, new Vector2(r.Position.X, r.End.Y)), (new Vector2(r.Position.X, r.End.Y), r.Position) })
            DrawDashedLine(a, b, c, 1, 5);
        var font = ThemeDB.FallbackFont;
        DrawString(font, new Vector2(8, 14), _title, HorizontalAlignment.Left, Size.X - 12, 12, c);
        DrawString(font, new Vector2(8, 28), _detail(), HorizontalAlignment.Left, Size.X - 12, 10, Ui.Dim);
    }
}

/// <summary>Your power against theirs as one bar: blue from the left, orange from the right.</summary>
public partial class OddsBar : Control
{
    private double _mine, _theirs;
    private bool _guess;
    public OddsBar() => CustomMinimumSize = new Vector2(300, 10);

    public void Set(double mine, double theirs, bool guess)
    {
        if (Math.Abs(mine - _mine) < 1e-3 && Math.Abs(theirs - _theirs) < 1e-3 && guess == _guess) return;
        (_mine, _theirs, _guess) = (mine, theirs, guess);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var total = _mine + _theirs;
        var share = total > 0 ? (float)(_mine / total) : 0.5f;
        DrawRect(new Rect2(0, 0, Size.X * share, Size.Y), new Color(Ui.Player, 0.8f));
        DrawRect(new Rect2(Size.X * share, 0, Size.X * (1 - share), Size.Y), new Color(Ui.Rival, _guess ? 0.45f : 0.8f));
        var x = Size.X / (1 + (float)Combat.StrongerBy) * (float)Combat.StrongerBy; // where "engage if stronger" starts
        DrawLine(new Vector2(x, -2), new Vector2(x, Size.Y + 2), Colors.White, 1);
    }
}
