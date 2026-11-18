using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;
using SimEngagement = Lightlag.Sim.Core.Engagement;

namespace Lightlag.Game;

// The cards the battle view ships with. Each is a small class: when it shows, its rows, and its folded line.
// RailgunCard and HeatCard are the simplest to copy for a new weapon or system.

/// <summary>How to close (or not): burn, the approach choices, arrive together, what to do on contact, support ships.</summary>
public sealed class MoveCard : BattleCard
{
    public override string Id => "move";
    public override string Title => "Move";
    public override int Order => 10;
    public override bool Shows(CardContext c) => true;

    public override void Build(CardBuilder b)
    {
        var burn = new HBoxContainer();
        burn.AddThemeConstantOverride("separation", 3);
        var burnLabel = Ui.Label("Burn", 11, Ui.Dim);
        burnLabel.CustomMinimumSize = new Vector2(CardBuilder.LabelWidth, 0);
        burn.AddChild(burnLabel);
        var burnButtons = new List<(Button B, double Share)>();
        foreach (var (name, share, tip) in new[]
                 {
                     ("Gentle", 0.4, "40 % thrust and top speed: slower, saves fuel, a dimmer engine flare"),
                     ("Cruise", 0.7, "70 % thrust and top speed"),
                     ("Hard", 1.0, "Full thrust, then brake: closes fastest, burns the most fuel and lights you up"),
                 })
        {
            var bt = CardBuilder.Small(name, tip, () => Main.Instance.Screen?.SetEngagementBurn(share));
            burn.AddChild(bt);
            burnButtons.Add((bt, share));
        }
        b.Custom(burn, c =>
        {
            burn.Visible = c.Info != null;
            foreach (var (bt, share) in burnButtons) bt.Modulate = Math.Abs(share - c.Screen.EngagementBurn) < 1e-6 ? Ui.Player : new Color(1, 1, 1, 0.7f);
        });

        // The heat clock for each burn while under fire (radiators in): picking a burn is a heat choice too.
        b.Line(c => c.Info == null || c.Fleets.Count == 0 ? "" : "Overheats under fire: " + string.Join(" · ", new[] { ("Gentle", 0.4), ("Cruise", 0.7), ("Hard", 1.0) }
            .Select(x => $"{x.Item1} {(Heat.HoursToOverheat(c.World, c.Lead, x.Item2, radiatorsIn: true, combatBurn: true) is double h ? Format.Hours(h) : "never")}")), Ui.Dim);

        // On contact goes on the Burn line; the approach choices (and Leave support behind) four across under it.
        b.SameRow();
        b.Choice("On contact", new (string, string, Stance)[]
            {
                ("Engage", "Close in and fight whatever comes into contact. In orbit it slides round the planet into railgun reach.", Stance.Engage),
                ("If stronger", $"Fight only when clearly stronger ({Combat.StrongerBy:0.#}x their power; even odds are enough in an orbit standoff, where it slides in)", Stance.EngageIfStronger),
                ("Hold", "Keep course and fire only at what comes into reach. In orbit it slides away to keep the planet between it and an enemy that would fight", Stance.Hold),
                ("Stay put", "Hold position: stay exactly where it is, in orbit too: fire at what comes into reach, never slide", Stance.HoldPosition),
            },
            (c, f) => c.Pending<SetStanceCommand>(f)?.Stance ?? f.Stance,
            (f, s) => new SetStanceCommand(f.Id, s, f.RetreatWhenDamaged), "stance");
        var grid = new ApproachGrid();
        b.Custom(grid, grid.Refresh);
        b.Line(c => c.Info == null ? "No enemy in sight or on record: nothing to close on." : "", Ui.Dim);
    }

    public override string Folded(CardContext c)
    {
        var doing = c.Info?.Options.FirstOrDefault(o => o.Current)?.Label ?? (c.Info == null ? "No enemy in sight" : "No approach set");
        var stances = c.Fleets.Select(f => c.Pending<SetStanceCommand>(f)?.Stance ?? f.Stance).Distinct().ToList();
        return $"{doing}\nOn contact: {(stances.Count > 1 ? "mixed" : StanceName(stances[0]))}";
    }

    public static string StanceName(Stance s) => s switch
    {
        Stance.Engage => "engage",
        Stance.EngageIfStronger => "engage if stronger",
        Stance.Hold => "hold course",
        Stance.HoldPosition => "hold position",
        Stance.Avoid => "avoid",
        _ => "head home",
    };
}

/// <summary>
/// The approach choices in three columns: each lit when in force, greyed with the reason when it can't be done. Hovering
/// one draws its path on the map; the line under the grid says what it does.
/// </summary>
public partial class ApproachGrid : GridContainer
{
    private readonly List<(Button B, string Key)> _buttons = new();
    private string _keys = "";
    private string? _hover;
    private CardContext? _c;
    private readonly List<(Fleet Fleet, EngagementInfo Info, SimEngagement.TogetherPlan Plan)> _together = new();
    private ulong _togetherAt;

    public ApproachGrid()
    {
        Columns = 4;
        TooltipText = "Hover a choice to see its path on the map; click to give the order";
        AddThemeConstantOverride("h_separation", 3);
        AddThemeConstantOverride("v_separation", 3);
    }

    public void Refresh(CardContext c)
    {
        _c = c;
        Visible = c.Info != null;
        if (c.Info is not EngagementInfo e) return;
        RefreshTogether(c, e);
        var support = c.Ships.Any(Fleets.Unarmed) || c.Fleets.Any(f => f.LeaveSupportBehind);
        var keys = string.Join(",", e.Options.Select(o => o.Key)) + "|" + string.Join(",", _together.Select(t => t.Fleet.Id)) + $"|{support}";
        if (keys != _keys && !Ui.MouseHeld)
        {
            _keys = keys;
            foreach (var (bt, _) in _buttons) bt.QueueFree();
            _buttons.Clear();
            foreach (var o in e.Options) Add(o.Key);
            foreach (var t in _together) Add($"together:{t.Fleet.Id}");
            if (support) Add(SupportKey);
        }
        foreach (var (bt, key) in _buttons)
        {
            if (key == SupportKey)
            {
                var on = c.Fleets.Select(f => c.Pending<SetLeaveSupportCommand>(f)?.On ?? f.LeaveSupportBehind).Distinct().ToList();
                bt.Text = on.Count > 1 ? "Leave support: mixed" : on[0] ? "Leave support ✓" : "Leave support";
                bt.Modulate = on.Count > 1 ? Ui.Warn : on[0] ? Ui.Player : new Color(1, 1, 1, 0.7f);
                bt.TooltipText = "When this fleet goes in to fight (Burn and engage, Hold at missile range, Burn past), its unarmed ships such as supply tenders split off and wait, then fly to rejoin once the fight is over. The fleet then burns at its warships' pace";
                continue;
            }
            if (key.StartsWith("together:"))
            {
                var id = int.Parse(key[9..]);
                var t = _together.FirstOrDefault(x => x.Fleet.Id == id);
                if (t.Fleet == null) continue;
                bt.Text = $"Arrive with {t.Fleet.Name}";
                bt.Disabled = t.Plan.Problem != null;
                bt.TooltipText = t.Plan.Problem is string why ? $"Can't: {why}"
                    : $"The faster one eases its burn so both reach them together, about {SimEngagement.Duration(Math.Max(0, t.Plan.ArriveHour - c.World.Hour))} from now";
                bt.Modulate = bt.Disabled ? new Color(1, 1, 1, 0.5f) : Colors.White;
                continue;
            }
            var states = c.Fleets.Select(f => c.Infos.TryGetValue(f.Id, out var i) ? i.Options.FirstOrDefault(o => o.Key == key) : null).ToList();
            var lead = e.Options.FirstOrDefault(o => o.Key == key);
            if (lead == null) continue;
            var current = states.Where(s => s != null).Select(s => s!.Current).Distinct().ToList();
            bt.Text = Short(lead.Key, lead.Label) + (current.Count > 1 ? " (some)" : "");
            bt.Disabled = states.All(s => s == null || s.Problem != null);
            bt.TooltipText = lead.Problem != null ? $"{lead.Label}: can't. {lead.Problem}" : $"{lead.Label}: {lead.Summary}";
            bt.Modulate = current.Count == 1 && current[0] ? Ui.Player : current.Count > 1 ? Ui.Warn : bt.Disabled ? new Color(1, 1, 1, 0.5f) : Colors.White;
        }
    }

    private void Add(string key)
    {
        var bt = new Button { FocusMode = FocusModeEnum.None, Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(104, 22), ClipText = true };
        bt.AddThemeFontSizeOverride("font_size", 11);
        bt.Pressed += () => Main.Instance.RunGuarded("approach order", () => Choose(key));
        bt.MouseEntered += () => { _hover = key; if (key != SupportKey) Preview(key); };
        bt.MouseExited += () => { if (_hover == key) { _hover = null; Main.Instance.Screen?.SetBattlePreview(null, null); } };
        AddChild(bt);
        _buttons.Add((bt, key));
    }

    /// <summary>Hover: draw the lead fleet's path for this choice on the map.</summary>
    private void Preview(string key)
    {
        if (_c?.Info is not EngagementInfo e || Main.Instance.Screen is not GameScreen screen) return;
        if (e.Options.FirstOrDefault(o => o.Key == key)?.Request is not FlightRequest req) { screen.SetBattlePreview(null, null); return; }
        var plan = FlightPlanner.Plan(_c.World, e.Mine, req with { DepartTick = _c.World.Tick });
        screen.SetBattlePreview(e.Mine.Id, plan.Ok ? plan.Flight : null);
    }

    private const string SupportKey = "support";

    /// <summary>Short button words, so four fit across (the full name leads the tooltip).</summary>
    private static string Short(string key, string label) => key switch
    {
        "engage" => "Close in",
        "missiles" => "Missile range",
        "rails" => "Rail range",
        _ => label,
    };

    private void Choose(string key)
    {
        if (_c == null) return;
        var sim = _c.Sim;
        if (key == SupportKey)
        {
            var on = _c.Fleets.All(f => _c.Pending<SetLeaveSupportCommand>(f)?.On ?? f.LeaveSupportBehind);
            _c.Issue(f => new SetLeaveSupportCommand(f.Id, !on), "leave support behind");
            return;
        }
        if (key.StartsWith("together:"))
        {
            var id = int.Parse(key[9..]);
            var t = _together.FirstOrDefault(x => x.Fleet.Id == id);
            if (t.Plan is not { First: FlightRequest a, Second: FlightRequest b2 } || _c.Info == null) return;
            sim.Issue(new PlanFlightCommand(_c.Lead.Id, a with { DepartTick = sim.World.Tick }));
            sim.Issue(new PlanFlightCommand(id, b2 with { DepartTick = sim.World.Tick }));
            Main.Instance.Toast($"{_c.Lead.Name} and {t.Fleet.Name} close in together");
            return;
        }
        foreach (var f in _c.Fleets)
        {
            if (!_c.Infos.TryGetValue(f.Id, out var info)) continue;
            var o = info.Options.FirstOrDefault(x => x.Key == key);
            if (key == SimEngagement.LetGoKey) { _c.Screen.IssueNow(new StopFleetCommand(f.Id), "let them go"); continue; }
            if (o?.Request is not FlightRequest req || o.Problem != null) continue;
            _c.Screen.IssueNow(new PlanFlightCommand(f.Id, req with { DepartTick = sim.World.Tick }), "approach order");
        }
        var label = _c.Info?.Options.FirstOrDefault(x => x.Key == key)?.Label ?? key;
        Main.Instance.Toast($"{(_c.All ? "All fleets" : _c.Lead.Name)}: {label.ToLowerInvariant()}");
    }

    /// <summary>Your other fleets on the same enemy: a plan to arrive together with each. Worked out about once a second.</summary>
    private void RefreshTogether(CardContext c, EngagementInfo e)
    {
        var now = Time.GetTicksMsec();
        if (now - _togetherAt < 1000 && _together.All(t => c.Infos.ContainsKey(t.Fleet.Id))) return;
        _togetherAt = now;
        _together.Clear();
        if (c.All) return;
        foreach (var (id, info) in c.Infos)
            if (id != e.Mine.Id && info.EnemyId == e.EnemyId && c.World.FindFleet(id) is Fleet other)
                _together.Add((other, info, SimEngagement.Together(c.World, e.Mine, other, e.EnemyId, e.Burn)));
    }
}

/// <summary>Railguns: aimed or spread fire, long-range shots, and when they reach.</summary>
public sealed class RailgunCard : BattleCard
{
    public override string Id => "railguns";
    public override string Title => "Railguns";
    public override int Order => 20;
    public override Color? Reach => EngagementRings.Railguns;
    public override bool Shows(CardContext c) => c.Ships.Any(s => Fitting.Spec(s).Railguns > 0);

    public override void Build(CardBuilder b)
    {
        var sp = GameData.Combat.Spread;
        b.Choice("Fire", new (string, string, FireMode)[]
            {
                ("Focused", "Aimed slugs: the most hits on a ship flying straight", FireMode.Focused),
                ("Spread", sp == null ? "A fan of slugs: harder to dodge" : $"Each gun fires {sp.SlugsPerGun} slugs at once in a fan {sp.FanMm * 1000:#,0} km wide: each hits less often but a sidestep hardly helps. The guns then cool for {sp.Cooldown:0} reloads.", FireMode.Spread),
            },
            (c, f) => c.Pending<SetFireModeCommand>(f)?.Fire ?? f.Fire,
            (f, m) => new SetFireModeCommand(f.Id, m), "fire mode");
        b.SameRow();
        b.Toggle("Long range",
            $"Also fire beyond normal reach, out to {Format.Distance(Heat.D.LongRangeMm)}: slugs fly so long that they hit only a ship holding its course (at best about {GameData.Combat.Railgun.Accuracy * Heat.D.LongAccuracy:P0}), at half the rate. It makes them dodge and burn fuel.",
            (c, f) => c.Pending<SetFireControlCommand>(f)?.LongShots ?? f.LongShots,
            (f, on) => new SetFireControlCommand(f.Id, on, Main.Instance.Sim?.PendingCommands.OfType<SetFireControlCommand>().LastOrDefault(x => x.FleetId == f.Id)?.Launch ?? f.MissileLaunch), "long-range fire");
        b.SameRow();
        b.Toggle("Let fleeing go",
            "On: when an enemy fleet pulls back, stop shooting at it (unless you chase it), so fights end sooner and fewer ships are lost on both sides. Off (the default): keep firing at it while it is still in reach.",
            (c, f) => c.Pending<SetLetRetreatGoCommand>(f)?.LetGo ?? f.LetRetreatGo,
            (f, on) => new SetLetRetreatGoCommand(f.Id, on), "letting a retreating fleet go");
        b.Line(c =>
        {
            var guns = c.Ships.Where(s => Fitting.Spec(s).Railguns > 0).ToList();
            var working = guns.Count(s => !s.Out(Module.Railguns));
            var reach = $"Reach {Format.Distance(Tech.RailRange(c.World, Lightlag.Sim.Core.Side.Player))} · {working} of {guns.Count} ships firing";
            return c.Info is EngagementInfo e ? $"{reach}\n{BattleText.Soon("In reach", e.OurRailguns)}" : reach;
        }, Ui.Dim);
    }

    public override string Folded(CardContext c)
    {
        var fire = c.Fleets.Select(f => c.Pending<SetFireModeCommand>(f)?.Fire ?? f.Fire).Distinct().ToList();
        var mode = fire.Count > 1 ? "mixed" : fire[0] == FireMode.Spread ? "Spread" : "Focused";
        return $"{mode}\n{(c.Info is EngagementInfo e ? BattleText.Soon("in reach", e.OurRailguns) : "")}";
    }
}

/// <summary>Missiles: how far out to launch, how many at one ship, and how many are left.</summary>
public sealed class MissileCard : BattleCard
{
    public override string Id => "missiles";
    public override string Title => "Missiles";
    public override int Order => 30;
    public override Color? Reach => EngagementRings.Missiles;
    public override bool Shows(CardContext c) => c.Ships.Any(s => Fitting.Spec(s).Missiles > 0);

    public override void Build(CardBuilder b)
    {
        var far = GameData.Combat.Missile.LaunchRangeMm;
        b.Choice("Launch", new (string, string, MissileLaunch)[]
            {
                ("Far", $"Launch at the edge of reach, {Format.Distance(far)}: early, but they have long to shoot them down", MissileLaunch.Far),
                ("Mid", $"Wait until {Format.Distance(Math.Min(Heat.D.MidLaunchMm, far))}: fewer shot down or dodged", MissileLaunch.Mid),
                ("Close", $"Wait until {Format.Distance(Math.Min(Heat.D.CloseLaunchMm, far))}: hardest to stop, but you are in their reach too", MissileLaunch.Close),
            },
            (c, f) => c.Pending<SetFireControlCommand>(f)?.Launch ?? f.MissileLaunch,
            (f, l) => new SetFireControlCommand(f.Id, Main.Instance.Sim?.PendingCommands.OfType<SetFireControlCommand>().LastOrDefault(x => x.FleetId == f.Id)?.LongShots ?? f.LongShots, l), "missile launch");
        b.Choice("Per target", new (string, string, int)[]
            {
                ("2", "At most 2 missiles in flight at one ship; the rest wait for another target", 2),
                ("4", "At most 4 missiles in flight at one ship; the rest wait for another target", 4),
                ("All", "No limit: salvos keep going at a target until it is finished", 0),
            },
            (c, f) => c.Pending<SetMissileBudgetCommand>(f)?.PerTarget ?? f.MissilesPerTarget,
            (f, n) => new SetMissileBudgetCommand(f.Id, n), "missile budget");
        b.Line(c =>
        {
            var left = c.Ships.Sum(s => s.Missiles);
            var racks = c.Ships.Sum(s => Tech.Racks(c.World, s));
            var line = left <= 0 ? "OUT OF MISSILES" : $"Left {left} of {racks}";
            return c.Info is EngagementInfo e && left > 0 ? $"{line} · {BattleText.Soon("in launch range", e.OurMissiles)}" : line;
        }, colorNow: c => c.Ships.Sum(s => s.Missiles) <= 0 ? Ui.Danger : Ui.Dim);
    }

    public override string Folded(CardContext c)
    {
        var left = c.Ships.Sum(s => s.Missiles);
        return left <= 0 ? "OUT" : $"{left} of {c.Ships.Sum(s => Tech.Racks(c.World, s))} left";
    }
}

/// <summary>Pulse lances: short-reach beams that fire on their own at anything in reach.</summary>
public sealed class LanceCard : BattleCard
{
    public override string Id => "lances";
    public override string Title => "Pulse lances";
    public override int Order => 35;
    public override bool Shows(CardContext c) => c.Ships.Any(s => Fitting.Spec(s).Lances > 0);

    public override void Build(CardBuilder b) =>
        b.Line(c =>
        {
            var n = c.Ships.Sum(s => s.Out(Module.Railguns) ? 0 : Fitting.Spec(s).Lances);
            return $"{n} lance{(n == 1 ? "" : "s")} working\nReach {Format.Distance(GameData.Weapons.L.RangeMm)}: fire by themselves at anything that close";
        }, Ui.Dim, lines: 2);

    public override string Folded(CardContext c) => $"Reach {Format.Distance(GameData.Weapons.L.RangeMm)}";
}

/// <summary>Defence: dodging, point defence, what is coming in, supplies.</summary>
public sealed class DefenceCard : BattleCard
{
    public override string Id => "defence";
    public override string Title => "Defence";
    public override int Order => 40;
    public override Color? Reach => EngagementRings.PointDefence;
    public override bool Shows(CardContext c) => true;

    public override void Build(CardBuilder b)
    {
        var c0 = GameData.Combat;
        var sp = c0.Spread;
        b.Choice("Dodge", new (string, string, DodgeMode)[]
            {
                ("Off", "Fly straight: no fuel spent on dodging", DodgeMode.Off),
                ("Sidestep", $"Jink as slugs come in: aimed slugs hit {c0.DodgeFactor:P0} as often" + (sp != null ? $", a spread's fan still {sp.SidestepVsSpread:P0}" : "")
                             + $". Costs {FlightPlanner.Kms(c0.DodgeDeltaV)} of fuel per slug. Works less close up, where slugs arrive too fast to dodge.", DodgeMode.Sidestep),
                ("Hard burn", sp == null ? "Burn hard out of the line of fire" : $"Burn hard out of the line of fire: aimed slugs hit {sp.HardVsFocused:P0} as often, a spread's fan {sp.HardVsSpread:P0}. Costs {sp.HardCost:0}x the fuel of a sidestep. Works less close up.", DodgeMode.HardBurn),
            },
            (c, f) => c.Pending<SetDodgeCommand>(f)?.Dodge ?? f.Dodge,
            (f, d) => new SetDodgeCommand(f.Id, d), "dodge");
        var fd = Formations.D;
        b.Choice("PD for", new (string, string, PdGuard)[]
            {
                ("Fleet", $"Each point defence mount fires at missiles aimed at any ship of the fleet: fully within {fd.PdCoverMm * 1000:#,0} km of it, at {fd.PdOutsideFactor:P0} further out. Every mount that fires warms up a little", PdGuard.Fleet),
                ("Self", $"Each mount fires only at missiles aimed at its own ship, {fd.PdSelfFactor:0.#}x as hard: that ship is safer, the rest get no help from it, and less heat builds up. Best for a lone ship or one spread far from the others", PdGuard.Self),
            },
            (c, f) => c.Pending<SetPdGuardCommand>(f)?.Guard ?? f.PdGuard,
            (f, g) => new SetPdGuardCommand(f.Id, g), "point defence guard");
        b.Line(c =>
        {
            var ships = c.Ships.ToList();
            var pd = ships.Count(s => Fitting.Spec(s).PointDefence > 0 && s.Disabled != Module.PointDefence);
            var supplies = c.Fleets.Min(f => Combat.SupplyShare(c.World, f));
            // Most urgent first: the card shows one line, the rest are in its tooltip.
            var lines = new List<string>();
            if (c.Info is EngagementInfo e) lines.Add(BattleText.Incoming(c.World, e));
            if (c.Fleets.Any(f => f.Dodge != DodgeMode.Off && !Combat.Dodging(c.World, f)))
                lines.Add($"Dodging stopped: supplies below {GameData.Combat.DodgeStopsBelow:P0}");
            foreach (var f in c.Fleets)
            {
                if (Combat.Surprised(c.World, f))
                    lines.Add($"CAUGHT UNAWARES: no return fire or dodging for {(f.SurprisedUntilHour!.Value - c.World.Hour) * 60:0} more min");
                else if (Combat.Unseen(c.World, f))
                    lines.Add($"Unseen: your first shots catch them unawares for {Combat.SurpriseHours * 60:0} min");
            }
            lines.Sort((x, y) => Urgent(y).CompareTo(Urgent(x)));
            lines.Add($"Supplies {supplies:P0}");
            lines.Add($"Point defence on {pd} of {ships.Count} ships, reach {Format.Distance(GameData.Combat.PointDefence.RangeMm)}");
            return string.Join("\n", lines.Distinct());
        }, Ui.Dim);
    }

    private static int Urgent(string line) => line.StartsWith("CAUGHT", StringComparison.Ordinal) ? 3 : line.StartsWith("Incoming", StringComparison.Ordinal) ? 2
        : line.StartsWith("Dodging stopped", StringComparison.Ordinal) ? 1 : 0;

    public override string Folded(CardContext c)
    {
        var d = c.Fleets.Select(f => c.Pending<SetDodgeCommand>(f)?.Dodge ?? f.Dodge).Distinct().ToList();
        var dodge = d.Count > 1 ? "mixed" : d[0] switch { DodgeMode.Off => "No dodge", DodgeMode.Sidestep => "Sidestep", _ => "Hard burn" };
        var g = c.Fleets.Select(f => c.Pending<SetPdGuardCommand>(f)?.Guard ?? f.PdGuard).Distinct().ToList();
        var guard = g.Count > 1 ? "PD mixed" : g[0] == PdGuard.Self ? "PD self" : "PD fleet";
        return $"{dodge} · {guard}\nSupplies {c.Fleets.Min(f => Combat.SupplyShare(c.World, f)):P0}";
    }
}

/// <summary>Heat: radiators, heat sinks thrown by hand or on their own, and how many are left.</summary>
public sealed class HeatCard : BattleCard
{
    public override string Id => "heat";
    public override string Title => "Heat";
    public override int Order => 50;
    public override bool Shows(CardContext c) => true;

    public override void Build(CardBuilder b)
    {
        b.Choice("Radiators", new (string, string, RadiatorMode)[]
            {
                ("Auto", "Pulled in only while under fire (enemy guns in reach or missiles coming), out otherwise", RadiatorMode.Auto),
                ("Out", "Out under fire too: sheds heat 30 times faster, but hits do 15 % more damage", RadiatorMode.Out),
                ("Run cold", "Pulled in and quiet: hard to spot, but heat builds up", RadiatorMode.Cold),
            },
            (c, f) => c.Pending<SetHeatCommand>(f)?.Radiators ?? f.Radiators,
            (f, r) => new SetHeatCommand(f.Id, r, Main.Instance.Sim?.PendingCommands.OfType<SetHeatCommand>().LastOrDefault(x => x.FleetId == f.Id)?.AutoSinks ?? f.AutoSinks), "radiators");
        b.Toggle("Auto sinks", "Ships throw heat sinks on their own when they run hot or missiles close in",
            (c, f) => c.Pending<SetHeatCommand>(f)?.AutoSinks ?? f.AutoSinks,
            (f, on) => new SetHeatCommand(f.Id, Main.Instance.Sim?.PendingCommands.OfType<SetHeatCommand>().LastOrDefault(x => x.FleetId == f.Id)?.Radiators ?? f.Radiators, on), "auto sinks");
        b.SameRow();
        b.Actions(("Eject all sinks", "Every ship throws a heat sink now: sheds heat at once and can draw off missiles closing in", f => new EjectHeatSinksCommand(f.Id), false));
        b.Line(c =>
        {
            var ships = c.Ships.ToList();
            if (ships.Count == 0) return "";
            var hottest = ships.Max(s => s.Heat / Math.Max(1e-6, Heat.Capacity(s)));
            var sinks = ships.Sum(s => s.HeatSinks);
            var hf = c.Lead;
            var radiators = hf.Radiators switch
            {
                RadiatorMode.Out => " · radiators OUT: cool, but hits do 15 % more",
                RadiatorMode.Cold => " · running cold",
                _ => Heat.UnderFire(c.World, hf) ? " · radiators in: under fire" : " · radiators out: no enemy guns in reach",
            };
            return (sinks == 0 ? "OUT OF HEAT SINKS (a supply tender or a planet of yours reloads them)" : $"Sinks left {sinks}")
                   + $" · hottest ship {hottest:P0}" + radiators + (ships.Any(s => s.Overheated) ? " · OVERHEATED" : "");
        }, colorNow: c => c.Ships.Any(s => s.Overheated) || c.Ships.Any() && c.Ships.Sum(s => s.HeatSinks) == 0 ? Ui.Danger : Ui.Dim);
    }

    public override string Folded(CardContext c) => $"{c.Ships.Sum(s => s.HeatSinks)} sinks left";
}

/// <summary>Formation: the shape the fleet flies in. Narrow: four lines.</summary>
public sealed class FormationCard : BattleCard
{
    private static readonly Formation[] Shapes = { Formation.Auto, Formation.Staggered, Formation.Wall, Formation.Line, Formation.Column };
    public override string Id => "formation";
    public override string Title => "Formation";
    public override int Order => 60;
    public override bool Shows(CardContext c) => c.Ships.Count() > 1;

    public override void Build(CardBuilder b)
    {
        b.Dropdown("Shape", Shapes.Select(s => (Formations.Name(s), FleetRules.ShapeTip(s), s)).ToList(),
            (c, f) => c.Pending<SetFormationCommand>(f)?.Formation ?? f.Formation,
            (f, s) => new SetFormationCommand(f.Id, s), "formation");
        b.SameRow();
        b.Custom(ProtectPicker(out var refresh), refresh);
        b.Choice("Cover", new (string, string, CoverStyle)[]
            {
                ("Loose", "No special care: the protected ship only gets the safest slot and point defence first", CoverStyle.Loose),
                ("Screen", $"Other ships put themselves in the way: {Flagships.D.ScreenChance:P0} of the shots at the protected ship hit one of them instead", CoverStyle.Screen),
                ("Close", $"Tight round it: {Flagships.D.CloseChance:P0} of the shots at it hit another ship and its point defence works harder, but the fleet dodges worse", CoverStyle.Close),
            },
            (c, f) => c.Pending<SetCoverCommand>(f)?.Cover ?? f.Cover,
            (f, cover) => new SetCoverCommand(f.Id, cover, f.FireAt), "cover");
        b.Line(c => (c.All ? string.Join("\n", c.Fleets.Select(f => $"{f.Name}: {Formations.Name(Formations.Shape(c.World, f))}")) : FleetRules.FormationText(c.World, c.Lead))
                    + "\n" + string.Join("\n", c.Fleets.Select(f => (c.All ? f.Name + ": " : "") + $"protects {BattleMoves.ProtectText(c.World, f)}")),
            colorNow: c => c.Fleets.Any(f => Formations.BlockedShips(c.World, f) > 0) ? Ui.Warn : Ui.Dim);
    }

    /// <summary>
    /// The Protect drop-down: the flagship (as before), the most damaged ship (Auto), nobody, or (one fleet on the tab) a
    /// ship by name. Its list follows the fleet's ships, so it is a custom row rather than a fixed drop-down.
    /// </summary>
    private static Control ProtectPicker(out Action<CardContext> refresh)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 3);
        var tag = Ui.Label("Protect", 11, Ui.Dim);
        tag.CustomMinimumSize = new Vector2(CardBuilder.LabelWidth, 0);
        row.AddChild(tag);
        var pick = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 22), FitToLongestItem = true };
        pick.AddThemeFontSizeOverride("font_size", 11);
        row.AddChild(pick);
        var items = new List<(ProtectPick Pick, int? Ship)>();
        var key = "";
        CardContext? last = null;
        pick.ItemSelected += i =>
        {
            if (last is not CardContext c || i < 0 || i >= items.Count) return;
            var (p, ship) = items[(int)i];
            Main.Instance.RunGuarded("protect", () =>
                c.Issue(f => p == ProtectPick.Ship ? (f.ShipIds.Contains(ship!.Value) ? new ProtectShipCommand(f.Id, p, ship) : null) : new ProtectShipCommand(f.Id, p), "protect"));
        };
        refresh = c =>
        {
            last = c;
            var ships = c.All ? new List<Ship>() : c.World.ShipsOf(c.Lead).ToList();
            var k = string.Join(",", ships.Select(s => s.Id));
            if (k != key && !pick.GetPopup().Visible)
            {
                key = k;
                items.Clear();
                pick.Clear();
                void Add(string text, string tip, ProtectPick p, int? ship = null)
                {
                    pick.AddItem(text, items.Count);
                    pick.SetItemTooltip(items.Count, tip);
                    items.Add((p, ship));
                }
                Add("Most damaged (Auto)", "The ship with the least hull left takes the safest slot, point defence puts it first, and (in Screen or Close cover) others take fire meant for it", ProtectPick.Worst);
                Add("Flagship only", "Only the flagship, when it is in this fleet: point defence puts it first and (in Screen or Close cover) others take fire meant for it, but it keeps its place in the formation", ProtectPick.Flagship);
                Add("Nobody", "Every ship takes its place by size; no ship gets special care", ProtectPick.None);
                foreach (var s in ships) Add(s.Name, $"Protect {s.Name} ({s.Code}) whatever its state", ProtectPick.Ship, s.Id);
            }
            var now = c.Fleets.Select(f => c.Pending<ProtectShipCommand>(f) is ProtectShipCommand p ? (p.Pick, p.ShipId) : BattleMoves.Protecting(c.World, f)).Distinct().ToList();
            var mixed = now.Count > 1;
            tag.Text = mixed ? "Protect (mixed)" : "Protect";
            tag.AddThemeColorOverride("font_color", mixed ? Ui.Warn : Ui.Dim);
            var at = mixed ? -1 : items.FindIndex(x => x.Pick == now[0].Item1 && (x.Pick != ProtectPick.Ship || x.Ship == now[0].Item2));
            if (pick.Selected != at && !pick.GetPopup().Visible) pick.Select(at);
        };
        return row;
    }

    public override string Folded(CardContext c) => string.Join(", ", c.Fleets.Select(f => Formations.Name(Formations.Shape(c.World, f))).Distinct());
}

/// <summary>
/// Keeping tech secret: fight with common weapons and open fire short of true reach (hiding range), bringing the
/// advanced ones out only when hurt, losing or more enemies join; or always show everything, or never. The same
/// setting as Battle rules › Weapons shown on the fleet panel.
/// </summary>
public sealed class SecretsCard : BattleCard
{
    public override string Id => "secrets";
    public override string Title => "Secrets";
    public override int Order => 70;
    public override bool Shows(CardContext c) => true;

    private static SecrecyRules Rules(CardContext c, Fleet f) => c.Pending<SetSecrecyCommand>(f)?.Rules ?? f.Secrecy;
    private static WeaponsShown Shown(CardContext c, Fleet f) => c.Pending<SetSecrecyCommand>(f)?.Shown ?? f.Shown;

    // A rule switch: the fleet's rules with one changed, keeping its Weapons shown setting (and any order not yet applied).
    private static void Rule(CardBuilder b, string text, string tip, Func<SecrecyRules, bool> get, Action<SecrecyRules, bool> set) =>
        b.Toggle(text, tip, (c, f) => get(Rules(c, f)),
            (f, on) =>
            {
                var pending = Main.Instance.Sim?.PendingCommands.OfType<SetSecrecyCommand>().LastOrDefault(x => x.FleetId == f.Id);
                var r = (pending?.Rules ?? f.Secrecy).Copy();
                set(r, on);
                return new SetSecrecyCommand(f.Id, pending?.Shown ?? f.Shown, r);
            }, "secrecy rule", c => c.Fleets.Any(f => Shown(c, f) == WeaponsShown.HoldBack));

    public override void Build(CardBuilder b)
    {
        b.Choice("Weapons", new (string, string, WeaponsShown)[]
            {
                ("Hold back", "Fight with common weapons and open fire short of true reach; bring the advanced ones out when the switches below say so. What the rival never sees, it can't learn", WeaponsShown.HoldBack),
                ("Show all", "Always fire everything at full reach", WeaponsShown.ShowAll),
                ("Never", "Never use the advanced weapons, even when losing", WeaponsShown.NeverShow),
            },
            Shown, (f, m) => new SetSecrecyCommand(f.Id, m, f.Secrecy.Copy()), "weapons shown");
        Rule(b, "Hide range", $"Open fire at {GameData.Weapons.K.HideRangeShare:P0} of true reach until the advanced weapons come out, so they don't learn your real reach",
            r => r.HideRange, (r, on) => r.HideRange = on);
        b.SameRow();
        Rule(b, "Out when hurt", $"Bring them out when a ship falls below {GameData.Weapons.K.BelowHull:P0} hull", r => r.WhenHurt, (r, on) => r.WhenHurt = on);
        b.SameRow();
        Rule(b, "Out when losing", "Bring them out when the odds turn against you", r => r.WhenLosing, (r, on) => r.WhenLosing = on);
        b.SameRow();
        Rule(b, "Out when more join", "Bring them out when more enemy warships join the fight", r => r.WhenJoined, (r, on) => r.WhenJoined = on);
        b.Line(c => string.Join("\n", c.Fleets.Select(f => (c.All ? f.Name + ": " : "") + FleetRules.ShownText(c.World, f))),
            colorNow: c => c.Fleets.Any(f => f.AdvancedOut) ? Ui.Warn : Ui.Dim);
    }

    public override string Folded(CardContext c)
    {
        if (Secrecy.Advanced(c.World, Side.Player).Count == 0) return "Nothing to hold back yet";
        if (c.Fleets.Any(f => f.AdvancedOut)) return "Advanced weapons OUT";
        var modes = c.Fleets.Select(f => Shown(c, f)).Distinct().ToList();
        return modes.Count > 1 ? "mixed" : modes[0] switch
        {
            WeaponsShown.ShowAll => "Showing all",
            WeaponsShown.NeverShow => "Never showing",
            _ => c.Fleets.All(f => Rules(c, f).HideRange) ? "Holding back, range hidden" : "Holding back",
        };
    }
}

/// <summary>Orders for the fight as a whole: hold on past the pull-back rule, or pull back now.</summary>
public sealed class OrdersCard : BattleCard
{
    public override string Id => "orders";
    public override string Title => "Orders";
    public override int Order => 90;
    public override bool Shows(CardContext c) => true;

    public override void Build(CardBuilder b)
    {
        b.Toggle("Hold on", "Ignore the pull-back rule and fight on; stops a pull-back already under way (click again to let the rule apply)",
            (c, f) => c.Pending<HoldOnCommand>(f)?.On ?? f.HoldOn, (f, on) => new HoldOnCommand(f.Id, on), "hold on");
        b.SameRow();
        b.Actions(("Pull back now", "Break off and leave the fight now, by the fleet's pull-back rule", f => new PullBackNowCommand(f.Id), true));
        // The pull-back rule itself, changed mid-fight. A looser rule by hand calls off a pull-back it would not have made.
        b.Choice<PullPreset?>("Pull-back rule", new (string, string, PullPreset?)[]
            {
                ("Auto", "The default for the fleet's situation (Empire, Automation)", null),
                (EmpireTabs.PresetName(PullPreset.Cautious), FleetRules.PresetTip(PullPreset.Cautious), PullPreset.Cautious),
                (EmpireTabs.PresetName(PullPreset.Standard), FleetRules.PresetTip(PullPreset.Standard), PullPreset.Standard),
                (EmpireTabs.PresetName(PullPreset.FightOn), FleetRules.PresetTip(PullPreset.FightOn), PullPreset.FightOn),
                (EmpireTabs.PresetName(PullPreset.ToTheLast), FleetRules.PresetTip(PullPreset.ToTheLast), PullPreset.ToTheLast),
            },
            (c, f) => c.Pending<SetPullRuleCommand>(f) is { } p ? p.Preset : f.RuleByHand ? f.Rule.Preset : null,
            (f, p) => new SetPullRuleCommand(f.Id, p), "pull-back rule");
        b.Line(c => string.Join("\n", c.Fleets.Select(f => (c.All ? f.Name + ": " : "") + FleetRules.RuleText(c.World, f))), Ui.Dim);
        b.Line(c => c.Info is EngagementInfo e ? BattleText.HoldingFire(c.World, c.Lead, e) ?? "" : "", Ui.Warn);
    }

    public override string Folded(CardContext c) =>
        c.Fleets.Any(f => f.Retreating) ? "Pulling back" : c.Fleets.Any(f => f.HoldOn) ? "Holding on" : "By the rule";
}

/// <summary>Words shared by the cards and the header.</summary>
public static class BattleText
{
    public static string Soon(string what, TimeWindow? t) =>
        t == null ? $"{what}: not before closest pass" : t.From < 1e-3 ? $"{what} now" : $"{what} in {SimEngagement.Duration(t.From)}";

    /// <summary>What railgun fire is coming in, and how much a dodge does against it at this range.</summary>
    public static string Incoming(World w, EngagementInfo e)
    {
        var enemy = w.FindFleet(e.EnemyId);
        if (enemy == null || !e.InSight || enemy.LastVolley is not FireMode fire || w.Hour - enemy.LastVolleyHour > 0.05)
            return "No railgun fire coming in";
        var d = Vec2.Distance(e.Mine.Position, enemy.Position);
        var secs = Combat.SlugSeconds(d, enemy, e.Mine);
        string Cut(DodgeMode m) => $"{1 - Combat.DodgeFactor(m, fire, secs):P0}";
        if (d > Secrecy.RailRange(w, enemy) + 1e-6)
            return $"Incoming: long-range slugs from {Format.Distance(d)}, {secs:0} s out: any dodge makes them miss";
        return $"Incoming: {(fire == FireMode.Spread ? "spread fans" : "aimed slugs")} in {secs:0} s. Sidestep cuts hits {Cut(DodgeMode.Sidestep)}, hard burn {Cut(DodgeMode.HardBurn)}";
    }

    /// <summary>Why this fleet is not firing at the enemy it faces, with what to do about it (null when nothing holds it back).</summary>
    public static string? HoldingFire(World w, Fleet f, EngagementInfo e)
    {
        if (w.FindFleet(e.EnemyId) is not Fleet enemy || !e.InSight) return null;
        if (f.Retreating && enemy.Chase?.TargetFleetId != f.Id)
            return "HOLDING FIRE: your fleet is pulling back, so it doesn't shoot. Pick an approach to fight again.";
        if (Combat.LetsGo(f, enemy))
            return $"HOLDING FIRE: {e.EnemyName} is pulling back, and Let fleeing go is on (Railguns card). Turn it off to keep firing while it is in reach, or chase it.";
        if (enemy.Retreating)
        {
            var reach = Math.Max(Tech.RailRange(w, f.Side), w.ShipsOf(f).Any(s => s.Missiles > 0) ? Heat.LaunchRange(f) : 0);
            var gap = Vec2.Distance(f.Position, enemy.Position);
            if (gap > reach && f.Chase?.TargetFleetId != enemy.Id)
                return $"OUT OF REACH: {e.EnemyName} is pulling back, {Format.Distance(gap)} away (your reach {Format.Distance(reach)}). Your fleet doesn't follow a fleeing enemy: chase it (Burn and engage) to catch it.";
        }
        var d = Vec2.Distance(f.Position, enemy.Position);
        var launch = Heat.LaunchRange(f);
        if (w.ShipsOf(f).Any(s => s.Missiles > 0) && d > launch && d <= GameData.Combat.Missile.LaunchRangeMm)
            return $"Missiles wait: launching from {Format.Distance(launch)} (they are {Format.Distance(d)} away). Set Launch to Far to fire now.";
        return null;
    }
}
