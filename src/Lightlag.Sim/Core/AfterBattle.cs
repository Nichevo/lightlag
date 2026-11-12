using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>The after-battle steps, in order.</summary>
public enum AfterStep { Rescue, Capture, Salvage, Home }

/// <summary>How the fleet gets home after a fight.</summary>
public enum HomeChoice { Nothing, MendHere, SendDamagedHome, AllHome, Tow, Scuttle }

public sealed class AfterTask
{
    public AfterStep Step { get; set; }
    public bool On { get; set; } = true;
    /// <summary>"queued", "doing", "done" or "skipped".</summary>
    public string State { get; set; } = "queued";
    public double HoursLeft { get; set; }
    public string Note { get; set; } = "";
    /// <summary>The wrecks this step works on, picked when the battle ended (their boats follow the drift).</summary>
    public List<int> WreckIds { get; set; } = new();

    /// <summary>Rescue: the escape pods and lifeboats in reach when the battle ended.</summary>
    public List<int> BoatIds { get; set; } = new();
}

/// <summary>A fleet that held the field clearing up after a battle (see AfterBattle).</summary>
public sealed class Aftermath
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public int FleetId { get; set; }
    public string FleetName { get; set; } = "";
    public int BattleId { get; set; }
    public Vec2 Where { get; set; }
    public double StartHour { get; set; }
    public List<AfterTask> Tasks { get; set; } = new();

    /// <summary>The way home Auto picks and why; the one in force (the player's, if picked); whether it waits for the player and until when.</summary>
    public HomeChoice AutoHome { get; set; }
    public string Why { get; set; } = "";
    public HomeChoice? Home { get; set; }
    public bool HomeByHand { get; set; }
    public double HomeDueHour { get; set; }
    public List<string> Notes { get; set; } = new();
    public bool Done { get; set; }
    public double? EndHour { get; set; }
}

/// <summary>A line for the Needs you list (see Stewards).</summary>
public delegate void NeedFn(NeedLevel level, string key, string text, int? bodyId, int? fleetId, GameCommand? act, string? label);

/// <summary>
/// After a battle, each fleet that held the field clears up in order: rescue (pick up pods: your crews
/// keep their rank, enemy crews become prisoners and give intel), capture (board crippled enemies),
/// salvage (strip the wrecks), then get home: mend here, send the damaged home (with an escort if the
/// rule says), take the whole fleet home, tow the crippled, or scuttle a ship that can't be saved.
/// Capture and salvage are skipped when enemy relief could arrive before they are done. Auto (the
/// After battle area) runs it all; with Ask me it waits for the player's pick of the way home.
/// </summary>
public static class AfterBattle
{
    private static AfterData D => War.D.After;

    public static string StepName(AfterStep s) => s switch
    {
        AfterStep.Rescue => "Rescue",
        AfterStep.Capture => "Capture",
        AfterStep.Salvage => "Salvage",
        _ => "Repair and go home",
    };

    public static string HomeName(HomeChoice h) => h switch
    {
        HomeChoice.MendHere => "Mend here",
        HomeChoice.SendDamagedHome => "Send the damaged home",
        HomeChoice.AllHome => "Whole fleet home",
        HomeChoice.Tow => "Tow the crippled",
        HomeChoice.Scuttle => "Scuttle",
        _ => "Carry on",
    };

    public static string HomeWhat(HomeChoice h) => h switch
    {
        HomeChoice.MendHere => "Field repairs to 60 % on every bar; the fleet stays on station but is weaker if attacked",
        HomeChoice.SendDamagedHome => "Crippled and badly hurt ships head for the nearest yard (with an escort if the rule says); the rest carry on",
        HomeChoice.AllHome => "Everyone limps back together at the slowest ship's speed; the station is left open",
        HomeChoice.Tow => "A healthy ship tows each crippled one home: saves the ship, but both crawl",
        HomeChoice.Scuttle => "Blow up a ship that can't be saved, so the enemy can't capture or salvage it; the crew leave first",
        _ => "Nothing to mend: the fleet carries on",
    };

    /// <summary>Is this fleet clearing up after a battle (wrecks and crippled ships are its business until it is done)?</summary>
    public static bool Busy(World w, Fleet f) => w.Aftermaths.Any(a => !a.Done && a.FleetId == f.Id);

    public static Aftermath? Of(World w, Fleet f) => w.Aftermaths.Find(a => !a.Done && a.FleetId == f.Id);

    private static IEnumerable<Wreck> Wrecks(World w, Aftermath a, Fleet f, bool crew) =>
        w.Wrecks.Where(x => Vec2.Distance(x.Position, f.Position) <= D.ReachMm && (crew ? x.CrewK > 1e-9 : x.Salvage.Count > 0)).OrderBy(x => x.Id);

    /// <summary>A battle is over: every fleet still on the field (not pulling back) starts clearing up.</summary>
    public static void OnBattleOver(Simulation sim, Battle b)
    {
        var w = sim.World;
        if (!w.Loop) return;
        foreach (var side in new[] { Side.Player, Side.Rival })
            foreach (var name in b.For(side).Fleets)
            {
                if (w.Fleets.Find(f => f.Side == side && f.Name == name && f.ShipIds.Count > 0) is not Fleet f || f.Retreating || Busy(w, f)) continue;
                Start(sim, f, b);
            }
        w.Aftermaths.RemoveAll(a => a.Done && w.Hour - (a.EndHour ?? a.StartHour) > 10 * 24);
    }

    public static Aftermath Start(Simulation sim, Fleet f, Battle? b)
    {
        var w = sim.World;
        var a = new Aftermath { Id = w.NextId++, Side = f.Side, FleetId = f.Id, FleetName = f.Name, BattleId = b?.Id ?? 0, Where = f.Position, StartHour = w.Hour };
        var pods = Wrecks(w, a, f, crew: true).ToList();
        var boats = w.Lifeboats.Where(b => Vec2.Distance(b.Position, f.Position) <= D.ReachMm).OrderBy(b => b.Id).ToList();
        var salvage = Wrecks(w, a, f, crew: false).ToList();
        var targets = Boarders.Targets(w, f).Where(t => Boarders.CanBoard(w, f, t) == null).ToList();
        var later = targets.Count * War.D.Boarding.DockHours + salvage.Count * D.SalvageHoursPerWreck;
        var rescues = pods.Count + boats.Count;
        var relief = Ground.ReliefNear(w, f.Side, f.Position, rescues * D.RescueHoursPerWreck + later + 2);
        var ours = pods.Where(p => p.Side == f.Side).Sum(p => p.CrewK) + boats.Where(b => b.Side == f.Side).Sum(b => b.CrewK);
        var theirs = pods.Where(p => p.Side != f.Side).Sum(p => p.CrewK) + boats.Where(b => b.Side != f.Side).Sum(b => b.CrewK);
        a.Tasks.Add(new AfterTask
        {
            Step = AfterStep.Rescue, WreckIds = pods.Select(x => x.Id).ToList(), BoatIds = boats.Select(b => b.Id).ToList(),
            HoursLeft = rescues * D.RescueHoursPerWreck, State = rescues == 0 ? "skipped" : "queued",
            Note = rescues == 0 ? "Nobody in the water" : $"Pick up {ours * 1000:0} of our crew and {theirs * 1000:0} enemy crew as prisoners"
                                                          + $" ({boats.Count} lot{(boats.Count == 1 ? "" : "s")} of pods and lifeboats)",
        });
        a.Tasks.Add(new AfterTask
        {
            Step = AfterStep.Capture, State = targets.Count == 0 || relief ? "skipped" : "queued",
            Note = targets.Count == 0 ? (Boarders.Targets(w, f).Any() ? "Crippled enemies, but no one to board them with" : "No crippled enemies to board")
                : relief ? "Skipped: enemy relief could arrive first" : $"Board {string.Join(", ", targets.Select(t => t.Name))}",
        });
        a.Tasks.Add(new AfterTask
        {
            Step = AfterStep.Salvage, WreckIds = salvage.Select(x => x.Id).ToList(), HoursLeft = salvage.Count * D.SalvageHoursPerWreck, State = salvage.Count == 0 || relief ? "skipped" : "queued",
            Note = salvage.Count == 0 ? "No wrecks in reach" : relief ? "Skipped: enemy relief could arrive first"
                : $"Strip {salvage.Count} wreck{(salvage.Count == 1 ? "" : "s")}: " + string.Join(", ", salvage.SelectMany(x => x.Salvage).GroupBy(kv => kv.Key)
                    .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Sum(kv => kv.Value):0} {Colonies.GoodName(g.Key).ToLowerInvariant()}")),
        });
        var (home, why) = AutoPick(w, f);
        a.AutoHome = home;
        a.Why = why;
        a.Tasks.Add(new AfterTask { Step = AfterStep.Home, Note = HomeName(home) });
        if (War.AutoCalls(w, f.Side, AutoArea.AfterBattle, fleetId: f.Id)) a.Home = home;
        a.HomeDueHour = w.Hour + 12;
        w.Aftermaths.Add(a);
        // A crippled enemy would limp away while the pods are picked up: the first boarding party goes over at once (it grapples the ship).
        if (a.Tasks[1].State == "queued")
        {
            Boarders.Start(sim, f, targets[0], BoardApproach.Dock, BoardGoal.Bridge);
            a.Tasks[1].State = "doing";
            a.Notes.Add($"Boarding {targets[0].Name}");
        }
        sim.Log.Info("after", $"{f.Name} clears up after the battle", new { fleetId = f.Id, aftermath = a.Id, pods = pods.Count, boats = boats.Count, targets = targets.Count, wrecks = salvage.Count,
            relief, home = home.ToString(), why });
        return a;
    }

    /// <summary>Auto's way home, from the damage, the fleet's job and battle rule, and whether the enemy is near.</summary>
    public static (HomeChoice Choice, string Why) AutoPick(World w, Fleet f)
    {
        var ships = w.ShipsOf(f).ToList();
        var crippled = ships.Where(s => s.Crippled).ToList();
        var hurt = ships.Where(s => !s.Crippled && Wounds.Worst(w, s) < 0.5).ToList();
        var enemy = Ground.ReliefNear(w, f.Side, f.Position, 48);
        var job = f.Mission != Mission.None || f.ZoneId != null;
        var rule = BattleRules.Effective(f);
        var yard = BattleRules.NearestYard(w, f.Side, f.Position);
        if (crippled.Count == 0 && hurt.Count == 0)
            return ships.Any(s => Wounds.Worst(w, s) < 0.9) ? (HomeChoice.MendHere, "Only light damage: field repairs while on station")
                : (HomeChoice.Nothing, "No real damage");
        if (crippled.Count + hurt.Count == ships.Count)
            return (HomeChoice.AllHome, "Every ship is hurt: the whole fleet heads for a yard together");
        if (crippled.Count > 0)
        {
            var days = yard == null ? double.PositiveInfinity
                : Vec2.Distance(yard.Position, f.Position) / Math.Max(1e-6, crippled.Min(s => s.MaxSpeed) * Wounds.D.CrippledSpeed * 0.5) / 24;
            if (enemy && days > 20) return (HomeChoice.Scuttle, $"A crippled ship is about {days:0} days from a yard with the enemy close: scuttle it rather than lose it to them");
            if (crippled.Any(s => s.Out(Module.Engines))) return (HomeChoice.Tow, "A crippled ship has no engines: a healthy ship tows it home");
            if (!job && enemy && rule.Who == PullWho.WholeFleet) return (HomeChoice.AllHome, "The enemy is close and the fleet has no job to hold: all go home");
            return (HomeChoice.SendDamagedHome, job ? "The fleet has a job to hold: only the damaged go home" + (rule.Escort ? ", with an escort" : "")
                : "The damaged go home" + (rule.Escort ? " with an escort" : "") + "; the rest carry on");
        }
        return enemy ? (HomeChoice.SendDamagedHome, "Badly hurt ships with the enemy near: they go home, the rest stay")
            : (HomeChoice.MendHere, "No enemy near: field repairs here before moving on");
    }

    /// <summary>Hourly: each fleet's current step goes on; when the last is done the plan ends.</summary>
    public static void Hourly(Simulation sim)
    {
        var w = sim.World;
        foreach (var a in w.Aftermaths.Where(a => !a.Done).ToList())
        {
            var f = w.FindFleet(a.FleetId);
            if (f == null || f.Side != a.Side || f.ShipIds.Count == 0) { End(sim, a, "the fleet is gone"); continue; }
            if (Heat.InBattle(w, f)) { End(sim, a, "a new fight started"); continue; }
            var task = a.Tasks.FirstOrDefault(t => t.State is "queued" or "doing");
            if (task == null) { End(sim, a, "all done"); continue; }
            if (f.Flight != null || f.Retreating)
            {
                // Ordered away (or pulled back): what is left undone stays undone.
                End(sim, a, "the fleet moved on");
                continue;
            }
            if (!task.On) { task.State = "skipped"; task.Note = "Unticked"; continue; }
            task.State = "doing";
            switch (task.Step)
            {
                case AfterStep.Rescue:
                case AfterStep.Salvage:
                    task.HoursLeft -= GameTime.HoursPerTick;
                    if (task.HoursLeft > 1e-9) break;
                    if (task.Step == AfterStep.Rescue)
                        foreach (var lb in w.Lifeboats.Where(b => task.BoatIds.Contains(b.Id) && Vec2.Distance(b.Position, f.Position) <= D.ReachMm * 2).ToList())
                            Lifeboats.PickUp(sim, f, lb);
                    foreach (var wr in w.Wrecks.Where(x => task.WreckIds.Contains(x.Id)).ToList())
                    {
                        if (task.Step == AfterStep.Rescue) Wounds.Rescue(sim, f, wr);
                        else Wounds.Strip(sim, f, wr);
                        if (wr.Salvage.Count == 0 && wr.CrewK <= 0) w.Wrecks.Remove(wr);
                    }
                    task.State = "done";
                    break;
                case AfterStep.Capture:
                    if (w.Boardings.Any(b => b.Outcome == null && b.FleetId == f.Id)) break;
                    if (Boarders.Targets(w, f).FirstOrDefault(t => Boarders.CanBoard(w, f, t) == null) is Ship target)
                    {
                        Boarders.Start(sim, f, target, BoardApproach.Dock, BoardGoal.Bridge);
                        a.Notes.Add($"Boarding {target.Name}");
                    }
                    else task.State = "done";
                    break;
                case AfterStep.Home:
                    if (a.Home == null)
                    {
                        if (w.Hour < a.HomeDueHour) break;
                        a.Home = a.AutoHome;
                        a.Notes.Add("Auto picked the way home (no answer in time)");
                    }
                    GoHome(sim, a, f, a.Home.Value);
                    task.Note = HomeName(a.Home.Value);
                    task.State = "done";
                    break;
            }
        }
    }

    /// <summary>Carries out the way home.</summary>
    private static void GoHome(Simulation sim, Aftermath a, Fleet f, HomeChoice choice)
    {
        var w = sim.World;
        var yard = BattleRules.NearestYard(w, f.Side, f.Position);
        var ships = w.ShipsOf(f).OrderBy(s => s.Id).ToList();
        var crippled = ships.Where(s => s.Crippled).ToList();
        var rule = BattleRules.Effective(f);
        a.Notes.Add($"{HomeName(choice)}{(a.HomeByHand ? " (your pick)" : "")}");
        switch (choice)
        {
            case HomeChoice.AllHome:
                if (yard != null) Combat.Retreat(sim, f, "heads home after the battle", yard, null, 0.5);
                break;
            case HomeChoice.SendDamagedHome:
            case HomeChoice.Tow:
            {
                var going = ships.Where(s => s.Crippled || Wounds.Worst(w, s) < 0.5).ToList();
                var tow = choice == HomeChoice.Tow;
                if (going.Count == 0) break;
                var healthy = ships.Where(s => !going.Contains(s)).OrderBy(s => Fitting.Spec(s).Cost).ThenBy(s => s.Id).ToList();
                if ((tow || rule.Escort) && healthy.Count > 1) going.Add(healthy[0]);
                if (going.Count >= ships.Count) { if (yard != null) Combat.Retreat(sim, f, "heads home after the battle", yard, null, tow ? D.TowSpeed : 0.5); break; }
                new SplitFleetCommand(f.Id, going.Select(s => s.Id).ToList()) { Source = "after" }.Apply(w, sim);
                if (w.FleetOf(going[0]) is Fleet g && g.Id != f.Id)
                {
                    g.RejoinFleetId = rule.Rejoin ? f.Id : null;
                    if (yard != null) Combat.Retreat(sim, g, tow ? "tows the crippled home" : "takes the damaged home", yard, null, tow ? D.TowSpeed : 0.5);
                }
                break;
            }
            case HomeChoice.Scuttle:
            {
                var lost = crippled.Count > 0 ? crippled : ships.Where(s => Wounds.Worst(w, s) < 0.2).ToList();
                if (lost.Count == ships.Count) lost = lost.Take(lost.Count - 1).ToList();
                foreach (var s in lost)
                {
                    // The crew leave first and come home with the fleet.
                    Economy.Of(w, f.Side).ReserveCrews.Add(s.Xp);
                    Economy.Of(w, f.Side).Totals.CrewsRescued += War.CrewK(s) * s.Crew;
                    Economy.Of(w, f.Side).Totals.Scuttled++;
                    Economy.Of(w, f.Side).Totals.ShipsLost++;
                    sim.Log.Info("after", $"{s.Name} is scuttled", new { shipId = s.Id, fleetId = f.Id });
                    Combat.Destroy(sim, s, wreck: false);
                }
                break;
            }
        }
        sim.Log.Info("after", $"{a.FleetName}: {HomeName(choice)}", new { aftermath = a.Id, choice = choice.ToString(), byHand = a.HomeByHand });
    }

    private static void End(Simulation sim, Aftermath a, string why)
    {
        a.Done = true;
        a.EndHour = sim.World.Hour;
        foreach (var t in a.Tasks.Where(t => t.State is "queued" or "doing")) { t.State = "skipped"; t.Note += $" ({why})"; }
        a.Notes.Add($"Finished: {why}");
        sim.Log.Info("after", $"{a.FleetName} is done clearing up", new { aftermath = a.Id, why });
    }

    /// <summary>
    /// Needs you: a way home waiting for the player, calls waiting in boardings and invasions, and
    /// crippled or badly damaged fleets far from a yard that nobody is sending home.
    /// </summary>
    public static void Needs(World w, Side side, NeedFn need)
    {
        foreach (var a in w.Aftermaths.Where(a => !a.Done && a.Side == side && a.Home == null).OrderBy(a => a.Id))
            need(NeedLevel.Soon, $"after-home:{a.Id}",
                $"{a.FleetName} after the battle: pick how to get home (Auto would {HomeName(a.AutoHome).ToLowerInvariant()}, in {Math.Max(0, a.HomeDueHour - w.Hour):0} h)",
                null, a.FleetId, new SetHomeChoiceCommand(side, a.Id, a.AutoHome), "Go with Auto");
        foreach (var b in w.Boardings.Where(b => b.Outcome == null && b.Side == side).OrderBy(b => b.Id))
            foreach (var c in b.Calls.Where(c => c.Open))
                need(NeedLevel.Urgent, $"board-call:{c.Id}", $"Boarding {b.TargetName}: {c.Text}", null, b.FleetId,
                    new BoardingCallCommand(b.Id, c.Id, c.AutoPick), $"{c.Options[c.AutoPick]} (Auto)");
        foreach (var body in w.Bodies.Where(x => x.Front is { } fr && fr.Side == side).OrderBy(x => x.Id))
            foreach (var c in body.Front!.Calls.Where(c => c.Open))
                need(NeedLevel.Urgent, $"ground-call:{c.Id}", $"Invasion of {body.Name}: {c.Text}", body.Id, null,
                    new GroundCallCommand(side, body.Id, c.Id, c.AutoPick), $"{c.Options[c.AutoPick]} (Auto)");
        foreach (var f in w.Fleets.Where(f => f.Side == side && f.ShipIds.Count > 0).OrderBy(f => f.Id))
        {
            if (f.Retreating || f.Refit != null || Busy(w, f) || Heat.InBattle(w, f)) continue;
            if (f.OrbitBodyId is int id && w.FindBody(id) is Body at && Economy.InOrbitAt(f, at) && Colonies.RepairsHere(w, at, side)) continue;
            if (f.Flight?.TargetBodyId is int to && w.FindBody(to) is Body dest && Colonies.RepairsHere(w, dest, side)) continue;
            var ships = w.ShipsOf(f).ToList();
            var cripple = ships.FirstOrDefault(s => s.Crippled);
            var hurt = ships.Where(s => !s.Crippled && Wounds.Worst(w, s) < 0.4).OrderBy(s => s.Id).FirstOrDefault();
            if (cripple == null && hurt == null) continue;
            var who = cripple ?? hurt!;
            need(cripple != null ? NeedLevel.Soon : NeedLevel.Advice, $"hurt:{f.Id}",
                $"{who.Name} {(cripple != null ? "crippled" : "badly damaged")} far from a yard: send {f.Name} for repair?",
                null, f.Id, new SendForRefitCommand(side, f.Id), "Send for repair");
        }
    }
}

/// <summary>Tick or untick an after-battle step.</summary>
public sealed record SetAfterStepCommand(Side Side, int AftermathId, AfterStep Step, bool On) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) =>
        world.Aftermaths.Find(a => a.Id == AftermathId) is Aftermath a && world.FindFleet(a.FleetId) is Fleet f ? (Side, f.Position) : null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.Aftermaths.Find(a => a.Id == AftermathId && a.Side == Side && !a.Done) is not Aftermath a || a.Tasks.Find(t => t.Step == Step) is not AfterTask t
            || t.State is "done" or "skipped")
        {
            sim.Log.Warn("after", "Bad after-battle step order", new { side = Side.ToString(), AftermathId, Step, On, Source });
            return;
        }
        t.On = On;
        sim.Log.Info("after", $"{a.FleetName}: {AfterBattle.StepName(Step)} {(On ? "on" : "off")}", new { AftermathId, Step, On, Source });
    }
}

/// <summary>Pick the way home after a battle.</summary>
public sealed record SetHomeChoiceCommand(Side Side, int AftermathId, HomeChoice Choice) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) =>
        world.Aftermaths.Find(a => a.Id == AftermathId) is Aftermath a && world.FindFleet(a.FleetId) is Fleet f ? (Side, f.Position) : null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.Aftermaths.Find(a => a.Id == AftermathId && a.Side == Side && !a.Done) is not Aftermath a)
        {
            sim.Log.Warn("after", "Bad way-home order", new { side = Side.ToString(), AftermathId, Choice, Source });
            return;
        }
        a.Home = Choice;
        a.HomeByHand = Source == "player";
        sim.Log.Info("after", $"{a.FleetName} will {AfterBattle.HomeName(Choice).ToLowerInvariant()}", new { AftermathId, Choice, Source });
    }
}
