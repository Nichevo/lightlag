namespace Lightlag.Sim.Core;

/// <summary>
/// Fleets that act without orders: carrying out their mission (hold, patrol, raid, escort, scout), and,
/// when cut off from command by light lag, making their own choices by their autonomy level. Runs for
/// sides without an AI mind (the AI's own fleets are run by its mind). Each fleet judges only from what
/// its side's sensors show it now (not command's older picture). Every decision is reported home, and
/// the report arrives at light speed. An order from command always wins: a fleet makes no choices of
/// its own for a while after one reaches it, and a direct move order ends its mission.
/// </summary>
public static class Autonomy
{
    /// <summary>A fleet makes no choices of its own for this long after an order reaches it (hours).</summary>
    public const double OrderRespectHours = 12;

    /// <summary>An enemy this much stronger (by sighting) is one to run from.</summary>
    public const double RunFromRatio = 1.25;

    /// <summary>A Free fleet goes after an enemy it outguns this much, within this range (AU).</summary>
    public const double AttackRatio = 1.5;
    public const double AttackRangeAu = 0.3;

    /// <summary>Hours of warning that count: an enemy heading for the fleet within this is a threat.</summary>
    public const double ThreatHours = 24;

    /// <summary>How long a patrol or scout waits at each stop (hours).</summary>
    public const double PatrolDwellHours = 12;
    public const double ScoutDwellHours = 6;

    /// <summary>Notes are kept this long after they arrive (hours), for the News menu and replays.</summary>
    public const double KeepNotesHours = 10 * 24;

    private static double Au => Physics.MmPerAu;

    /// <summary>Is this side's fleet run by an AI mind (so not by autonomy)?</summary>
    private static bool Minded(World w, Side side) => Mind.Of(w, side) != null;

    /// <summary>Once an hour: each fleet with a mission works on it; fleets cut off from command make their own choices.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        foreach (var f in w.Fleets.ToList())
        {
            if (f.ShipIds.Count == 0 || Minded(w, f.Side) || w.FindFleet(f.Id) == null) continue;
            if (Choose(sim, f)) continue;
            Work(sim, f);
        }
        w.Light.Notes.RemoveAll(n => w.Hour - n.ArriveHour > KeepNotesHours);
    }

    /// <summary>A mission has just been set: start on it at once.</summary>
    public static void Start(Simulation sim, Fleet f)
    {
        if (f.Mission == Mission.Scout && f.Stance is Stance.Engage or Stance.EngageIfStronger)
            Do(sim, new SetStanceCommand(f.Id, Stance.Avoid, f.RetreatWhenDamaged));
        if (f.Mission == Mission.Raid && f.Stance != Stance.Engage)
            Do(sim, new SetStanceCommand(f.Id, Stance.Engage, true));
        if (!Minded(sim.World, f.Side)) Work(sim, f);
    }

    /// <summary>An order from command has reached the fleet: note the time, and a direct move ends its mission.</summary>
    public static void Heard(Simulation sim, GameCommand command)
    {
        if (command is not IFleetCommand fc || command.Source.StartsWith("ai:", StringComparison.Ordinal) || command.Source == "auto") return;
        if (sim.World.FindFleet(fc.FleetId) is not Fleet f) return;
        f.LastOrderHour = sim.World.Hour;
        if (f.Mission != Mission.None && command is PlanFlightCommand or StopFleetCommand or SetHoldCommand or GatherFleetsCommand or SendAheadCommand)
        {
            f.Mission = Mission.None;
            sim.Log.Info("fleet", $"{f.Name} mission ends: new orders", new { fleetId = f.Id });
        }
    }

    /// <summary>Is the fleet cut off from command, and by how many hours of light?</summary>
    public static bool CutOff(World w, Fleet f, out double delay)
    {
        delay = Light.DelayHours(w, f.Side, f.Position);
        if (!w.Light.On) return false;
        if (delay > 0 && !Light.SignalClear(w, f.Side, f.Position)) delay = double.PositiveInfinity;
        return delay > 0;
    }

    /// <summary>The fleet's own judgement of an enemy's strength, from the sighting alone.</summary>
    public static double PowerOf(Sighting s) => s.Ships * GameData.Ai.EnemyShipValue;

    // ---- Own choices -----------------------------------------------------------------------

    private static bool Choose(Simulation sim, Fleet f)
    {
        var w = sim.World;
        if (f.Autonomy == AutonomyLevel.Strict || f.Retreating || f.Maneuvering || f.JoinFleetId != null) return false;
        if (w.Hour - f.LastOrderHour < OrderRespectHours || !CutOff(w, f, out var delay)) return false;
        var own = Stats.Power(w, f);
        var seen = w.Sightings.Where(s => s.SeenBy == f.Side && !s.Own && (s.Visible || w.Hour - s.Hour <= 3)).ToList();

        // Danger: a stronger enemy close by or coming for it. Sensible waits for orders while there is time
        // for them (the news there and an order back); Free acts at once.
        foreach (var s in seen.OrderBy(s => Vec2.Distance(s.Guess(w.Hour), f.Position)).ThenBy(s => s.FleetId))
        {
            if (PowerOf(s) < own * RunFromRatio) continue;
            var gap = Vec2.Distance(s.Guess(w.Hour), f.Position);
            var eta = gap <= GameData.Combat.ContactRangeMm * 2 ? 0 : s.Visible ? Sensors.HeadingFor(s, f, w.Hour, Sensors.MotionOf(w, f)) : null;
            if (eta is not double hours || hours > ThreatHours) continue;
            if (f.Autonomy == AutonomyLevel.Sensible && hours > 2 * delay + 1) continue;
            if (Combat.GoingHome(w, f)) return false;
            Combat.Retreat(sim, f, $"runs from {s.Name} and heads home");
            Report(sim, f, $"{f.Name} saw {s.Name} ({s.Ships} ships) coming"
                + (hours > 0 ? $", about {hours:0} h out" : " close by") + $"; outgunned, it is heading home.");
            return true;
        }

        // Opportunity (Free only): a weaker enemy within reach.
        if (f.Autonomy != AutonomyLevel.Free || f.Chase != null || own <= 0) return false;
        if (Combat.ArmourShare(w, f) < 0.7 || !Combat.HasWorking(w, f, Module.Railguns)) return false;
        var prey = seen.Where(s => s.Visible && own >= PowerOf(s) * AttackRatio && Vec2.Distance(s.Guess(w.Hour), f.Position) <= AttackRangeAu * Au)
            .OrderBy(s => Vec2.Distance(s.Guess(w.Hour), f.Position)).ThenBy(s => s.FleetId).FirstOrDefault();
        if (prey == null) return false;
        var at = prey.Guess(w.Hour);
        var request = new FlightRequest(null, at, 0.8, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick, TargetFleetId: prey.FleetId, Chase: ChaseMode.GunRange);
        Do(sim, new PlanFlightCommand(f.Id, request));
        if (f.Flight == null) return false;
        if (f.Stance != Stance.Engage) Do(sim, new SetStanceCommand(f.Id, Stance.Engage, f.RetreatWhenDamaged));
        Report(sim, f, $"{f.Name} saw {prey.Name} ({prey.Ships} ships) nearby and outguns it; it is going after it.");
        return true;
    }

    // ---- Missions ---------------------------------------------------------------------------

    private static bool Idle(Fleet f) => f.Flight == null && !f.Maneuvering && !f.Retreating && f.Chase == null && f.JoinFleetId == null;

    private static void Work(Simulation sim, Fleet f)
    {
        var w = sim.World;
        if (f.Mission == Mission.None) return;
        if (f.Mission == Mission.Escort) { Escort(sim, f); return; }
        if (!Idle(f)) return;

        // Worn down: home to refit first, then back to the mission.
        var home = Economy.HomeOf(w, f.Side);
        if (home != null)
        {
            var atHome = Economy.InOrbitAt(f, home);
            if (!atHome && (Combat.ArmourShare(w, f) < 0.6 || Combat.SupplyShare(w, f) < 0.35))
            {
                if (Fly(sim, f, home, 0.6)) Report(sim, f, $"{f.Name} is worn down and goes home to refit before carrying on ({f.Mission}).");
                return;
            }
            if (atHome && f.MissionBodyId != home.Id && (Combat.ArmourShare(w, f) < 0.9 || Combat.SupplyShare(w, f) < 0.7)) return;
        }

        switch (f.Mission)
        {
            case Mission.Hold:
            case Mission.Raid:
                if (w.FindBody(f.MissionBodyId ?? -1) is not Body b) { End(sim, f, "its target is gone"); return; }
                if (!Economy.InOrbitAt(f, b) && !Fly(sim, f, b, f.Mission == Mission.Raid ? 0.8 : 0.6))
                    End(sim, f, $"it cannot reach {b.Name}");
                break;
            case Mission.Patrol:
                Rounds(sim, f, PatrolStops(w, f), PatrolDwellHours);
                break;
            case Mission.Scout:
                Rounds(sim, f, ScoutStops(w, f), ScoutDwellHours);
                break;
        }
    }

    /// <summary>Visit each stop in turn, waiting a while at each.</summary>
    private static void Rounds(Simulation sim, Fleet f, List<Body> stops, double dwell)
    {
        var w = sim.World;
        if (stops.Count == 0) { End(sim, f, "it has nowhere to go"); return; }
        var stop = stops[f.MissionLeg % stops.Count];
        if (!Economy.InOrbitAt(f, stop))
        {
            f.MissionUntil = 0;
            if (Fly(sim, f, stop, 0.5)) return;
            f.MissionLeg++; // can't get there from here: try the next one later
            return;
        }
        if (f.MissionUntil <= 0) { f.MissionUntil = w.Hour + dwell; return; }
        if (w.Hour < f.MissionUntil) return;
        f.MissionLeg++;
        f.MissionUntil = 0;
        if (stops.Count > 1) Fly(sim, f, stops[f.MissionLeg % stops.Count], 0.5);
    }

    /// <summary>A patrol covers the planet and its moons (a moon's patrol covers its planet's system).</summary>
    public static List<Body> PatrolStops(World w, Fleet f)
    {
        if (w.FindBody(f.MissionBodyId ?? -1) is not Body b) return new();
        var planet = b.Kind == BodyKind.Moon && b.ParentId is int p && w.FindBody(p) is Body parent ? parent : b;
        var stops = new List<Body> { planet };
        stops.AddRange(w.Bodies.Where(m => m.ParentId == planet.Id && m.Kind == BodyKind.Moon && m.Radius > 0).OrderBy(m => m.OrbitRadius).ThenBy(m => m.Id));
        return stops;
    }

    /// <summary>A scout looks at the planets not its own, the enemy's first, nearest first from home.</summary>
    public static List<Body> ScoutStops(World w, Fleet f)
    {
        var from = Economy.HomeOf(w, f.Side)?.Position ?? f.Position;
        return w.Bodies.Where(b => b.Kind == BodyKind.Planet && b.Owner != f.Side)
            .OrderBy(b => b.Owner == null ? 1 : 0).ThenBy(b => Vec2.Distance(b.Position, from)).ThenBy(b => b.Id).ToList();
    }

    /// <summary>Keep with the escorted fleet: go where it is going, or to where it waits.</summary>
    private static void Escort(Simulation sim, Fleet f)
    {
        var w = sim.World;
        if (w.FindFleet(f.MissionFleetId ?? -1) is not Fleet lead || lead.Side != f.Side || lead.ShipIds.Count == 0)
        {
            End(sim, f, "the fleet it escorted is gone");
            return;
        }
        if (f.Maneuvering || f.Retreating || f.Chase != null || f.JoinFleetId != null) return;
        int? bodyId = lead.Flight != null ? lead.Flight.TargetBodyId : lead.Hold == HoldStyle.Orbit ? lead.OrbitBodyId : null;
        if (bodyId is int id && w.FindBody(id) is Body b)
        {
            if (Economy.InOrbitAt(f, b) || f.Flight?.TargetBodyId == id) return;
            Fly(sim, f, b, lead.Flight != null ? 1.0 : 0.6);
            return;
        }
        // The lead is in open space: match it there.
        var at = lead.Flight != null ? lead.Flight.Target : lead.Position;
        if (Vec2.Distance(f.Position, at) < 5 || f.Flight != null && Vec2.Distance(f.Flight.Target, at) < 5) return;
        Do(sim, new PlanFlightCommand(f.Id, new FlightRequest(null, at, 1.0, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick)));
    }

    private static void End(Simulation sim, Fleet f, string why)
    {
        f.Mission = Mission.None;
        Report(sim, f, $"{f.Name} ends its mission: {why}.");
    }

    private static bool Fly(Simulation sim, Fleet f, Body b, double speed)
    {
        Do(sim, new PlanFlightCommand(f.Id, new FlightRequest(b.Id, b.Position, speed, ArrivalStyle.Brake, HoldStyle.Orbit, sim.World.Tick)));
        if (f.Flight != null) return true;
        return speed > 0.3 && Fly(sim, f, b, 0.3);
    }

    /// <summary>The fleet's own decisions are commands too, applied at once (a replay makes them again).</summary>
    private static void Do(Simulation sim, GameCommand command) => (command with { Source = "auto" }).Apply(sim.World, sim);

    /// <summary>Sends a decision home: it reaches command when its light does.</summary>
    public static void Report(Simulation sim, Fleet f, string text)
    {
        var w = sim.World;
        var delay = Light.DelayHours(w, f.Side, f.Position);
        w.Light.Notes.Add(new FleetNote(w.Light.NextNoteId++, f.Side, f.Id, w.Hour, w.Hour + delay, text));
        Economy.Of(w, f.Side).Totals.OwnDecisions++;
        BattleRecords.Mark(w, f, MarkKind.Decision, text);
        sim.Log.Info("autonomy", text, new { fleetId = f.Id, side = f.Side.ToString(), mission = f.Mission.ToString(), autonomy = f.Autonomy.ToString(), delayHours = delay });
    }
}
