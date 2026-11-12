namespace Lightlag.Sim.Core;

/// <summary>
/// Fleets chasing an enemy fleet. The chase flight is planned against the enemy's last seen track and
/// planned again only when the enemy is seen to change course (a new burn), so the chase carries on
/// while it is out of sight. It ends when the enemy is gone or forgotten, after an overtake, or on any
/// other order.
/// </summary>
public static class Chases
{
    /// <summary>How far (Mm) the enemy may stray from the planned track before the chase re-aims.</summary>
    public const double ReaimDriftMm = 1.0;

    public static ChaseOrder Order(World w, Fleet f, int targetId, ChaseMode mode, double speedFraction)
    {
        var s = Sensors.Find(w, f.Side, targetId);
        var own = w.FindFleet(targetId) is { } t && t.Side == f.Side ? t : null; // joining one of its own fleets
        return new ChaseOrder
        {
            TargetFleetId = targetId,
            Mode = mode,
            SpeedFraction = speedFraction,
            TargetAt = Sensors.Predict(w, f.Side, targetId, w.Hour) ?? Vec2.Zero,
            TargetVelocity = own?.Velocity ?? s?.Velocity ?? Vec2.Zero,
            PlannedHour = w.Hour,
        };
    }

    /// <summary>Only a gun-range chase (or no chase) hands over to close combat; the others keep their distance.</summary>
    public static bool LetsEngage(Fleet f) => f.Chase is not { } c || c.Mode == ChaseMode.GunRange;

    /// <summary>Where the current chase flight expects the enemy to be now.</summary>
    public static Vec2 Expected(ChaseOrder c, double hour) => c.TargetAt + c.TargetVelocity * (hour - c.PlannedHour);

    /// <summary>How long a chase goes on without closing on its quarry before the fleet gives up.</summary>
    public const double GiveUpHours = 10 * 24;

    /// <summary>Once an hour: end finished chases and re-aim those whose target has changed course.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        var now = w.Hour;
        foreach (var f in w.Fleets.Where(f => f.Chase != null).ToList())
        {
            var c = f.Chase!;
            var target = w.FindFleet(c.TargetFleetId);
            if (c.Mode == ChaseMode.Join)
            {
                Join(sim, f, c, target);
                continue;
            }
            var s = Sensors.Find(w, f.Side, c.TargetFleetId);
            if (target == null || s == null || f.Retreating)
            {
                f.Chase = null;
                if (!f.Retreating) LostQuarry(sim, f, c, target == null);
                continue;
            }
            if (c.StartHour <= 0) c.StartHour = now;
            c.LastBodyId = s.OrbitBodyId;
            // Closing in counts as progress however long it takes (a far quarry holding orbit takes weeks to reach);
            // a quarry that keeps ahead for days is let go: endless chases kept battles from ever ending.
            var gapNow = Vec2.Distance(f.Position, Sensors.Predict(w, f.Side, c.TargetFleetId, now) ?? s.Guess(now));
            if (c.ClosestHour <= 0 || gapNow < c.ClosestGap * 0.98) { c.ClosestGap = gapNow; c.ClosestHour = now; }
            if (now - c.ClosestHour > GiveUpHours && !f.Maneuvering)
            {
                f.Chase = null;
                sim.Log.Info("fleet", $"{f.Name} gives up the chase", new { fleetId = f.Id, targetFleetId = c.TargetFleetId, hours = Math.Round(now - c.StartHour) });
                Autonomy.Report(sim, f, $"{f.Name} gives up chasing {s.Name}: it has not closed in for {GiveUpHours / 24:0} days. It flies on to where it was heading and waits there.", NoteKind.StandingOrder);
                continue;
            }
            if (f.Maneuvering) continue; // in a fight: combat steers
            var detoured = c.Detouring && f.Flight is not { Detour: true };
            if (c.Mode == ChaseMode.Overtake && f.Flight == null && !detoured)
            {
                f.Chase = null;
                sim.Log.Info("fleet", $"{f.Name} has overtaken {target.Name}", new { fleetId = f.Id, targetFleetId = target.Id });
                continue;
            }
            var guess = Sensors.Predict(w, f.Side, c.TargetFleetId, now) ?? s.Guess(now);
            var turned = s.Hour >= c.PlannedHour && ((s.Velocity - c.TargetVelocity).Length > Math.Max(0.5, 0.02 * s.Velocity.Length)
                                       || Vec2.Distance(guess, Expected(c, now)) > ReaimDriftMm);
            // A chase that has arrived but drifted off its spot (after a fight, say) sets off again.
            var want = FlightPlanner.ChaseDistance(c.Mode);
            var adrift = f.Flight == null && want is double d && Math.Abs(Vec2.Distance(f.Position, guess) - d) > Math.Max(2, d * 0.25);
            if (!turned && !adrift && !detoured) continue;

            var perf = FleetPerformance.Of(w, f);
            var plan = FlightPlanner.PlanChase(w, f, target, c.Mode, c.SpeedFraction, perf);
            c.TargetAt = guess;
            c.TargetVelocity = s.Velocity;
            c.PlannedHour = now;
            c.Detouring = false;
            if (plan is { Ok: true, Flight: Flight flight })
            {
                // Re-aimed just after leaving orbit: still climbing away from the planet it left, so it keeps the
                // launch allowance for its first hours.
                if (f.Flight is { FromBodyId: int from } old && flight.FromBodyId == null && now < old.DepartHour + Flight.LaunchHours)
                    flight.FromBodyId = from;
                f.Flight = flight;
                c.Detouring = flight.Detour;
                c.Problem = null;
                sim.Log.Info("fleet", $"{f.Name} re-aims its chase", new
                {
                    fleetId = f.Id, targetFleetId = target.Id, mode = c.Mode.ToString(), flight.ArriveHour, plan.DeltaV, turned, adrift,
                });
            }
            else if (c.Problem != plan.Problem)
            {
                // The wording carries live numbers (the quarry's speed), so only a new kind of problem is logged.
                if (Kind(c.Problem) != Kind(plan.Problem))
                    sim.Log.Warn("fleet", $"{f.Name} cannot keep up the chase", new { fleetId = f.Id, targetFleetId = target.Id, problem = plan.Problem });
                c.Problem = plan.Problem;
            }
        }
    }

    private static string? Kind(string? problem) => problem?.Split(':')[0];

    /// <summary>
    /// The quarry is gone (destroyed or merged) or its track was lost. A quarry last seen holding orbit is
    /// looked for at that planet: the fleet flies there and takes up orbit. Otherwise it flies on to where the
    /// quarry was expected and waits there. Either way command is told why the chase ended.
    /// </summary>
    private static void LostQuarry(Simulation sim, Fleet f, ChaseOrder c, bool gone)
    {
        var w = sim.World;
        var name = w.FindFleet(c.TargetFleetId)?.Name ?? "its quarry";
        sim.Log.Info("fleet", $"{f.Name} loses track of its quarry and stops chasing", new { fleetId = f.Id, targetFleetId = c.TargetFleetId, gone, c.LastBodyId });
        if (gone)
        {
            Autonomy.Report(sim, f, $"{f.Name} stops chasing: {name} is gone", NoteKind.StandingOrder);
            return;
        }
        if (c.LastBodyId is int bodyId && w.FindBody(bodyId) is Body body)
        {
            new PlanFlightCommand(f.Id, new FlightRequest(bodyId, body.Position, Math.Max(0.5, c.SpeedFraction), ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick))
                { Source = "auto" }.Apply(w, sim);
            Autonomy.Report(sim, f, $"{f.Name} lost sight of {name}, last seen in orbit at {body.Name}: it goes on to {body.Name} to look for it", NoteKind.StandingOrder);
            return;
        }
        Autonomy.Report(sim, f, $"{f.Name} lost track of {name}: it flies on to where it was last expected and waits there", NoteKind.StandingOrder);
    }

    /// <summary>Joining one of its own fleets that is moving: merge once alongside, else re-aim when it changes course.</summary>
    private static void Join(Simulation sim, Fleet f, ChaseOrder c, Fleet? into)
    {
        var w = sim.World;
        var now = w.Hour;
        if (into == null || into.Side != f.Side || f.Retreating)
        {
            f.Chase = null;
            f.JoinFleetId = null;
            sim.Log.Info("fleet", $"{f.Name} stops flying to join: that fleet is gone", new { fleetId = f.Id, targetFleetId = c.TargetFleetId });
            return;
        }
        var gap = Vec2.Distance(f.Position, into.Position);
        if (gap <= FlightPlanner.JoinDistanceMm * 4 && (f.Velocity - into.Velocity).Length <= 2)
        {
            f.Chase = null;
            f.Flight = null;
            sim.Log.Info("fleet", $"{f.Name} catches up and joins {into.Name}", new { fleetId = f.Id, intoFleetId = into.Id });
            Fleets.Absorb(w, into, f);
            return;
        }
        if (f.Maneuvering) return;
        // The fleet to join has settled into orbit (or is flying to stop at a planet): stop chasing a point that
        // circles the planet and fly to the planet instead; the two merge once both hold orbit there.
        if (Fleets.Meeting(w, into, c.SpeedFraction) is { TargetBodyId: int meetAt } && w.FindBody(meetAt) is Body body)
        {
            var problem = Fleets.FlyToJoin(w, f, into, c.SpeedFraction);
            f.JoinFleetId = into.Id;
            if (problem == null)
            {
                sim.Log.Info("fleet", $"{f.Name} goes to {body.Name} to join {into.Name}", new { fleetId = f.Id, intoFleetId = into.Id, bodyId = body.Id });
                return;
            }
            f.Chase = c;
        }
        var turned = (into.Velocity - c.TargetVelocity).Length > Math.Max(0.5, 0.02 * into.Velocity.Length)
                     || Vec2.Distance(into.Position, Expected(c, now)) > ReaimDriftMm;
        if (!turned && f.Flight != null) return;
        var perf = FleetPerformance.Of(w, f);
        var plan = FlightPlanner.PlanChase(w, f, into, ChaseMode.Join, c.SpeedFraction, perf);
        // Too slow at the speed it was given: a join may burn at full speed to catch up.
        if (!plan.Ok && c.SpeedFraction < 1) plan = FlightPlanner.PlanChase(w, f, into, ChaseMode.Join, 1.0, perf);
        c.TargetAt = into.Position;
        c.TargetVelocity = into.Velocity;
        c.PlannedHour = now;
        if (plan is { Ok: true, Flight: Flight flight })
        {
            f.Flight = flight;
            c.Problem = null;
            c.StuckSinceHour = null;
            return;
        }
        // The wording carries live numbers (the other fleet's speed), so only a new kind of problem is logged.
        if (Kind(c.Problem) != Kind(plan.Problem))
            sim.Log.Warn("fleet", $"{f.Name} cannot catch up with {into.Name}", new { fleetId = f.Id, intoFleetId = into.Id, problem = plan.Problem });
        c.Problem = plan.Problem;
        c.StuckSinceHour ??= now;
        if (now - c.StuckSinceHour.Value >= JoinGiveUpHours)
        {
            f.Chase = null;
            f.JoinFleetId = null;
            sim.Log.Info("fleet", $"{f.Name} gives up joining {into.Name}", new { fleetId = f.Id, intoFleetId = into.Id, problem = plan.Problem });
            Autonomy.Report(sim, f, $"{f.Name} cannot catch up with {into.Name} ({plan.Problem}) and stops trying: order {into.Name} to stop or wait, then join again",
                NoteKind.StandingOrder);
        }
    }

    /// <summary>A join chase that has found no way to catch up for this long gives up and says why.</summary>
    public const double JoinGiveUpHours = 24;
}
