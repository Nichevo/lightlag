using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>How the boarders get across: cut through the hull (quick), dock at an airlock (slower, stronger), or a cyber attack (AI-run ships only).</summary>
public enum BoardApproach { Breach, Dock, Cyber }

/// <summary>What the boarders make for first: the bridge (the ship), or the magazine (slower, but no scuttle charge).</summary>
public enum BoardGoal { Bridge, Magazine }

/// <summary>
/// A boarding action: a fleet sends a party over to a crippled enemy ship (or one with no engines and no
/// guns). The outcome is a formula (the two sides' strength, ranks and research) played out over a few
/// hours, with one to three calls along the way: the crew seals the bridge, a scuttle charge is armed,
/// hostages are taken. Auto makes the calls unless the player asks to (the Boarding calls area).
/// </summary>
public sealed class Boarding
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public int FleetId { get; set; }
    public int TargetShipId { get; set; }
    public string TargetName { get; set; } = "";
    public string TargetClass { get; set; } = "";
    public BoardApproach Approach { get; set; }
    public BoardGoal Goal { get; set; }

    /// <summary>Boarders (k): from troop ships, and parties from the fleet's crewed warships (ship id, crew share sent).</summary>
    public double Marines { get; set; }
    public double TroopsK { get; set; }
    public List<BoardParty> Parties { get; set; } = new();
    public double MarineRank { get; set; } = 1;
    public double Defenders { get; set; }

    /// <summary>Strength factors from the calls (attackers, defenders), and hours planned; progress runs 0 to 1.</summary>
    public double Edge { get; set; } = 1;
    public double DefEdge { get; set; } = 1;
    public double Hours { get; set; }
    public double Progress { get; set; }
    public double StartHour { get; set; }
    public double PrisonerFactor { get; set; } = 1;

    /// <summary>Points of progress where calls come up (from the start), and the calls made or waiting.</summary>
    public List<double> CallsAt { get; set; } = new();
    public List<string> CallKinds { get; set; } = new();
    public List<WarCall> Calls { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    /// <summary>Set when it is over: "taken", "beaten off", "scuttled", "called off", "lost touch".</summary>
    public string? Outcome { get; set; }
    public double? EndHour { get; set; }
}

public sealed record BoardParty(int ShipId, double Share, double CrewK);

/// <summary>Boarding: who can be boarded, how strong each side is, and the hourly play-out (see the Boarding class).</summary>
public static class Boarders
{
    private static BoardingData D => War.D.Boarding;

    /// <summary>Can't fight back: crippled, or with engines out and no working weapon.</summary>
    public static bool Helpless(Ship s) =>
        s.Crippled || s.Out(Module.Engines) && (Fitting.Spec(s).Railguns == 0 || s.Out(Module.Railguns)) && (s.Missiles == 0 || s.Out(Module.Missiles));

    public static bool BeingBoarded(World w, int shipId) => w.Boardings.Any(b => b.Outcome == null && b.TargetShipId == shipId);

    /// <summary>A fleet whose ship is being boarded is grappled: it can't fly off (see Hourly).</summary>
    public static bool Grappled(World w, Fleet f) => w.Boardings.Any(b => b.Outcome == null && f.ShipIds.Contains(b.TargetShipId));

    /// <summary>Holds a boarded ship beside the boarding fleet: on its own, not flying, keeping pace.</summary>
    private static void Grapple(Simulation sim, Fleet captor, Ship target)
    {
        var w = sim.World;
        if (w.FleetOf(target) is not Fleet tf) return;
        if (tf.ShipIds.Count > 1)
        {
            new SplitFleetCommand(tf.Id, new List<int> { target.Id }) { Source = "boarding" }.Apply(w, sim);
            tf = w.FleetOf(target)!;
        }
        tf.Flight = null;
        tf.Chase = null;
        tf.Retreating = false;
        tf.Maneuvering = false;
        tf.JoinFleetId = null;
        tf.Hold = captor.Hold;
        tf.OrbitBodyId = captor.OrbitBodyId;
        tf.OrbitRadius = captor.OrbitRadius;
        tf.OrbitAngle = captor.OrbitAngle;
        tf.Velocity = captor.Velocity;
        tf.Position = captor.Position + new Vec2(3, 0);
        foreach (var s in w.ShipsOf(tf)) s.Position = tf.Position + s.FormationOffset;
    }

    /// <summary>Boarders a fleet can send: troops aboard (k), and a party from each crewed warship (a share of its crew).</summary>
    public static (double TroopsK, List<BoardParty> Parties) Available(World w, Fleet f)
    {
        var troops = Planets.TroopsAboard(w, f);
        var parties = w.ShipsOf(f).Where(s => Crews.Crewed(s) && Planner.Warship(s) && !s.Crippled && s.Crew > 0.3)
            .OrderBy(s => s.Id).Select(s => new BoardParty(s.Id, D.PartyShare, War.CrewK(s) * D.PartyShare * s.Crew)).ToList();
        return (troops, parties);
    }

    /// <summary>The crippled (or helpless) enemy ships this fleet could board now.</summary>
    public static IEnumerable<Ship> Targets(World w, Fleet f) =>
        w.Ships.Where(s => s.Side != f.Side && Helpless(s) && Vec2.Distance(s.Position, f.Position) <= D.RangeMm && Sensors.Sees(w, f.Side, w.FleetOf(s)!))
            .OrderBy(s => Vec2.Distance(s.Position, f.Position)).ThenBy(s => s.Id);

    /// <summary>Why this fleet can't board that ship now (null: it can).</summary>
    public static string? CanBoard(World w, Fleet f, Ship? target, BoardApproach approach = BoardApproach.Breach)
    {
        if (!w.Loop) return "Only in a full game";
        if (target == null || target.Side == f.Side) return "Pick an enemy ship";
        if (!Helpless(target)) return "Only a crippled ship, or one with its engines and guns out, can be boarded";
        if (Vec2.Distance(target.Position, f.Position) > D.RangeMm) return $"Get within {D.RangeMm:0} Mm first";
        if (BeingBoarded(w, target.Id)) return "Already being boarded";
        if (w.Boardings.Any(b => b.Outcome == null && b.FleetId == f.Id)) return "This fleet is already boarding a ship";
        if (w.Fleets.Any(o => o.Side == target.Side && Combat.Armed(w, o) && Vec2.Distance(o.Position, target.Position) <= D.RangeMm))
            return "Enemy guns still cover it: the boats would be shot down";
        var ai = !Crews.Crewed(target);
        if (ai && approach != BoardApproach.Cyber) return "An AI-run ship has no crew to fight: use a cyber attack";
        if (!ai && approach == BoardApproach.Cyber) return "A cyber attack only works on AI-run ships";
        var (troops, parties) = Available(w, f);
        if (troops + parties.Sum(p => p.CrewK) < 0.01) return "No troops aboard and no crew to spare for a boarding party";
        return null;
    }

    /// <summary>Strength of each side (attackers, defenders) as things stand.</summary>
    public static (double Att, double Def) Strength(World w, Boarding b)
    {
        var target = w.FindShip(b.TargetShipId);
        var tech = (Tech.Has(w, b.Side, "boarding-marines") ? D.MarinesTech : 1) * (b.TroopsK > 0 ? Math.Min(1.5, Tech.Factor(w, b.Side, "marines")) : 1);
        var approach = b.Approach == BoardApproach.Dock ? D.DockEdge : 1;
        var att = b.Marines * Crews.At(War.D.Crews.Boarding, Crews.RankOf(b.MarineRank)) * tech * approach * b.Edge;
        var def = b.Approach == BoardApproach.Cyber ? b.Marines * 0.8 : b.Defenders * (target != null ? Crews.BoardFactor(target) : 1) * b.DefEdge;
        return (att, def);
    }

    public static double WinChance(World w, Boarding b)
    {
        var (a, d) = Strength(w, b);
        return Math.Clamp(a / Math.Max(1e-9, a + d), 0.05, 0.95);
    }

    /// <summary>Send a boarding party: marines leave the fleet, the calls along the way are rolled now.</summary>
    public static Boarding Start(Simulation sim, Fleet f, Ship target, BoardApproach approach, BoardGoal goal)
    {
        var w = sim.World;
        var (troops, parties) = Available(w, f);
        var crewRank = parties.Count > 0 ? parties.Average(p => w.FindShip(p.ShipId)!.Xp) : 1;
        foreach (var p in parties) w.FindShip(p.ShipId)!.Crew -= p.Share * w.FindShip(p.ShipId)!.Crew;
        var taken = Planets.TakeTroops(w, f, troops);
        var marines = taken + parties.Sum(p => p.CrewK);
        var b = new Boarding
        {
            Id = w.NextId++, Side = f.Side, FleetId = f.Id, TargetShipId = target.Id, TargetName = target.Name, TargetClass = target.ClassId,
            Approach = approach, Goal = goal, Marines = marines, TroopsK = taken, Parties = parties,
            MarineRank = marines > 0 ? (taken * Math.Max(1, crewRank) + parties.Sum(p => p.CrewK) * crewRank) / marines : 1,
            Defenders = War.CrewK(target) * Math.Max(0.1, target.Crew), StartHour = w.Hour,
        };
        var (a, d) = Strength(w, b);
        b.Hours = (approach == BoardApproach.Dock ? D.DockHours : D.BreachHours) * (goal == BoardGoal.Magazine ? 1.3 : 1) * (1 + 0.5 * d / Math.Max(1e-9, a));
        var kinds = new List<string> { "seal", "hostages" };
        if (goal == BoardGoal.Bridge && approach != BoardApproach.Cyber) kinds.Add("scuttle");
        var n = Math.Min(kinds.Count, 1 + sim.Rng.Range(0, 3));
        for (var i = 0; i < n; i++)
        {
            var k = kinds[sim.Rng.Range(0, kinds.Count)];
            kinds.Remove(k);
            b.CallKinds.Add(k);
            b.CallsAt.Add(Math.Round((i + 1.0) / (n + 1), 3));
        }
        w.Boardings.Add(b);
        Grapple(sim, f, target);
        while (w.Boardings.Count > 30 && w.Boardings.FindIndex(x => x.Outcome != null) is int old and >= 0) w.Boardings.RemoveAt(old);
        Economy.Of(w, f.Side).Totals.Boardings++;
        b.Notes.Add($"{marines * 1000:0} boarders cross to {target.Name} ({Approach(approach)}, making for the {Goal(goal)})");
        Choices.Note(w, f.Side, "boarding", target.ClassId, f.Name, approach.ToString());
        sim.Log.Info("boarding", $"{f.Name} boards {target.Name}", new { boardingId = b.Id, fleetId = f.Id, shipId = target.Id, marines = Math.Round(marines, 3),
            defenders = Math.Round(b.Defenders, 3), chance = Math.Round(WinChance(w, b), 2), hours = Math.Round(b.Hours, 1), calls = b.CallKinds });
        return b;
    }

    public static string Approach(BoardApproach a) => a switch { BoardApproach.Dock => "docking at an airlock", BoardApproach.Cyber => "cyber attack", _ => "breaching the hull" };
    public static string Goal(BoardGoal g) => g == BoardGoal.Magazine ? "magazine" : "bridge";

    private static WarCall MakeCall(World w, Boarding b, string kind)
    {
        var auto = War.AutoCalls(w, b.Side, AutoArea.Boarding, fleetId: b.FleetId);
        var chance = WinChance(w, b);
        return kind switch
        {
            "seal" => War.Call(w, kind, $"The crew of {b.TargetName} seals the bridge", chance >= 0.6 ? 0 : 1, auto, 2,
                ("Cut through", "Faster and harder, more boarders hurt"), ("Wait them out", "Slower; the defenders tire")),
            "scuttle" => War.Call(w, kind, $"A scuttle charge is armed on {b.TargetName}", 1, auto, 1,
                ("Rush the charge", "1 in 5 it blows: ship and some boarders lost"), ("Cut the power", "Safe, a quarter slower"), ("Pull back", "Call the boarding off")),
            _ => War.Call(w, kind, $"Defenders on {b.TargetName} take hostages", 0, auto, 2,
                ("Negotiate", "Slower; more prisoners, defenders weaken"), ("Storm", "Faster; few prisoners")),
        };
    }

    /// <summary>A call is made (by the player or Auto): its effect on the boarding.</summary>
    public static void Apply(Simulation sim, Boarding b, WarCall c)
    {
        var pick = c.Picked ?? c.AutoPick;
        var label = c.Options[Math.Clamp(pick, 0, c.Options.Count - 1)];
        b.Notes.Add($"{(c.ByAuto ? "Auto chose" : "You chose")}: {label}");
        switch (c.Kind, pick)
        {
            case ("seal", 0): b.Edge *= 1.15; b.Hours *= 0.9; b.Marines *= 0.95; break;
            case ("seal", _): b.Hours *= 1.3; b.DefEdge *= 0.85; break;
            case ("scuttle", 0):
                if (sim.Rng.NextDouble() < 0.2) { End(sim, b, "scuttled", "The charge blows before they reach it: the ship is lost"); return; }
                b.Edge *= 1.05;
                break;
            case ("scuttle", 1): b.Hours *= 1.25; break;
            case ("scuttle", _): End(sim, b, "called off", "The boarders pull back"); return;
            case ("hostages", 0): b.Hours *= 1.2; b.PrisonerFactor *= 1.5; b.DefEdge *= 0.9; break;
            default: b.Edge *= 1.1; b.PrisonerFactor *= 0.5; break;
        }
        sim.Log.Info("boarding", $"Boarding call: {label}", new { boardingId = b.Id, kind = c.Kind, pick, byAuto = c.ByAuto });
    }

    /// <summary>Hourly: each boarding moves on, its calls come up, and it ends when the progress is full.</summary>
    public static void Hourly(Simulation sim)
    {
        var w = sim.World;
        foreach (var b in w.Boardings.Where(b => b.Outcome == null).ToList())
        {
            var f = w.FindFleet(b.FleetId);
            var target = w.FindShip(b.TargetShipId);
            if (target == null || target.Side == b.Side) { End(sim, b, "lost touch", "The target is gone"); continue; }
            if (f == null || f.Side != b.Side || Vec2.Distance(f.Position, target.Position) > D.RangeMm * 1.5)
            {
                End(sim, b, "lost touch", "The fleet moved away: the boarders come back");
                continue;
            }
            Grapple(sim, f, target);
            b.Progress = Math.Min(1, b.Progress + 1 / Math.Max(1, b.Hours));
            for (var i = 0; i < b.CallKinds.Count; i++)
                if (b.Calls.Count == i && b.Progress >= b.CallsAt[i])
                {
                    var call = MakeCall(w, b, b.CallKinds[i]);
                    b.Calls.Add(call);
                    sim.Log.Info("boarding", "Boarding call comes up", new { boardingId = b.Id, kind = call.Kind, auto = call.DueHour <= w.Hour });
                }
            foreach (var c in War.Due(w, b.Calls))
            {
                Apply(sim, b, c);
                if (b.Outcome != null) break;
            }
            if (b.Outcome != null || b.Progress < 1 || b.Calls.Any(c => c.Open) || b.Calls.Count < b.CallKinds.Count) continue;
            Resolve(sim, b, f, target);
        }
    }

    /// <summary>The player (or anyone) makes a waiting call.</summary>
    public static string? Choose(Simulation sim, Boarding b, int callId, int option)
    {
        if (b.Outcome != null) return "The boarding is over";
        if (b.Calls.Find(c => c.Id == callId) is not WarCall c || !c.Open) return "No such call waiting";
        if (option < 0 || option >= c.Options.Count) return "No such option";
        c.Picked = option;
        Apply(sim, b, c);
        return null;
    }

    private static void Resolve(Simulation sim, Boarding b, Fleet f, Ship target)
    {
        var w = sim.World;
        var (a, d) = Strength(w, b);
        var chance = WinChance(w, b);
        var won = sim.Rng.NextDouble() < chance;
        if (!won)
        {
            b.Marines *= 0.3; // the few who get back
            target.Crew = Math.Max(0.05, target.Crew * (1 - 0.5 * a / Math.Max(1e-9, a + d)));
            Economy.Of(w, b.Side).Totals.BoardingsFailed++;
            End(sim, b, "beaten off", $"The defenders of {target.Name} beat the boarders off");
            Sparks.Fire(sim, b.Side, "landing-failed");
            return;
        }
        var loser = target.Side;
        b.Marines *= 1 - 0.5 * d / Math.Max(1e-9, a + d);
        var prisoners = b.Approach == BoardApproach.Cyber ? 0 : b.Defenders * D.PrisonerShare * b.PrisonerFactor;
        var prize = Math.Min(b.Marines, War.CrewK(target) * D.PrizeCrew);
        b.Marines -= prize;
        Capture(sim, target, f, b.MarineRank * 0.7);
        Spoils.TakePrisoners(sim, b.Side, prisoners, target.Name);
        Spoils.Scrap(sim, b.Side, $"the fittings of {target.Name}");
        Economy.Of(w, b.Side).Totals.ShipsCaptured++;
        Economy.Of(w, loser).Totals.ShipsLost++;
        End(sim, b, "taken", $"{target.Name} is taken: {prisoners * 1000:0} prisoners, a prize crew aboard");
    }

    /// <summary>A ship changes hands: it leaves its fleet and forms a prize fleet beside the captor (crippled, with a thin crew).</summary>
    public static void Capture(Simulation sim, Ship target, Fleet captor, double crewXp)
    {
        var w = sim.World;
        var loser = target.Side;
        if (w.FleetOf(target) is Fleet old)
        {
            old.ShipIds.Remove(target.Id);
            if (old.ShipIds.Count == 0) w.Fleets.Remove(old);
            else Fleets.Arrange(w, old);
        }
        target.Side = captor.Side;
        target.Crew = D.PrizeCrew;
        target.Xp = Math.Max(0, crewXp);
        target.Protect = null;
        target.CargoTroops = 0;
        target.CargoPeople = 0;
        var prize = Fleets.Create(w, captor.Side, new[] { target }, captor.Position + new Vec2(2, 2));
        prize.Name = $"Prize {target.Name}";
        prize.Velocity = captor.Velocity;
        prize.Hold = captor.Hold;
        prize.OrbitBodyId = captor.OrbitBodyId;
        prize.OrbitRadius = captor.OrbitRadius;
        prize.OrbitAngle = captor.OrbitAngle;
        prize.Rule = PullRule.For(PullPreset.Cautious);
        if (Economy.Of(w, loser).FlagshipId == target.Id) Light.Succeed(sim, loser, target.Position);
        sim.Log.Info("boarding", $"{captor.Side} takes {target.Name}", new { shipId = target.Id, prizeFleet = prize.Id, from = loser.ToString() });
    }

    /// <summary>The boarding ends: the boarders who are left go back to their ships.</summary>
    private static void End(Simulation sim, Boarding b, string outcome, string note)
    {
        var w = sim.World;
        b.Outcome = outcome;
        b.EndHour = w.Hour;
        b.Notes.Add(note);
        foreach (var c in b.Calls.Where(c => c.Open)) { c.Picked = c.AutoPick; c.ByAuto = true; }
        var start = b.TroopsK + b.Parties.Sum(p => p.CrewK);
        var back = start > 1e-9 ? Math.Clamp(b.Marines / start, 0, 1) : 0;
        if (outcome == "scuttled") back *= 0.6;
        if (w.FindFleet(b.FleetId) is Fleet f && f.Side == b.Side)
        {
            var troops = b.TroopsK * back;
            foreach (var s in w.ShipsOf(f).Where(Planets.IsTroopShip))
            {
                var put = Math.Min(troops, Planets.TroopHold(s) - s.CargoTroops);
                s.CargoTroops += put;
                troops -= put;
            }
            foreach (var p in b.Parties)
                if (w.FindShip(p.ShipId) is Ship s && s.Side == b.Side && War.CrewK(s) > 0)
                    s.Crew = Math.Min(1, s.Crew + p.CrewK * back / War.CrewK(s));
        }
        Economy.Of(w, b.Side).Totals.TroopsLostK += start * (1 - back);
        if (outcome == "scuttled" && w.FindShip(b.TargetShipId) is Ship t) { Economy.Of(w, t.Side).Totals.Scuttled++; Combat.Destroy(sim, t); }
        sim.Log.Info("boarding", $"Boarding of {b.TargetName}: {outcome}", new { boardingId = b.Id, outcome, back = Math.Round(back, 2) });
    }
}

/// <summary>Board a crippled enemy ship from this fleet.</summary>
public sealed record BoardShipCommand(int FleetId, int TargetShipId, BoardApproach Approach = BoardApproach.Breach, BoardGoal Goal = BoardGoal.Bridge) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Boarding") is not Fleet f) return;
        var target = world.FindShip(TargetShipId);
        if (Boarders.CanBoard(world, f, target, Approach) is string problem)
        {
            sim.Log.Warn("boarding", $"Can't board: {problem}", new { FleetId, TargetShipId, Source });
            return;
        }
        Boarders.Start(sim, f, target!, Approach, Goal);
    }
}

/// <summary>Make a waiting call in a boarding.</summary>
public sealed record BoardingCallCommand(int BoardingId, int CallId, int Option) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) =>
        world.Boardings.Find(b => b.Id == BoardingId) is Boarding b && world.FindFleet(b.FleetId) is Fleet f ? (b.Side, f.Position) : null;

    public override void Apply(World world, Simulation sim)
    {
        var problem = world.Boardings.Find(b => b.Id == BoardingId) is Boarding b ? Boarders.Choose(sim, b, CallId, Option) : "No such boarding";
        if (problem != null) sim.Log.Warn("boarding", $"Can't make that call: {problem}", new { BoardingId, CallId, Option, Source });
    }
}
