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
                if (!f.Retreating)
                    sim.Log.Info("fleet", $"{f.Name} loses track of its quarry and stops chasing", new { fleetId = f.Id, targetFleetId = c.TargetFleetId });
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
        var turned = (into.Velocity - c.TargetVelocity).Length > Math.Max(0.5, 0.02 * into.Velocity.Length)
                     || Vec2.Distance(into.Position, Expected(c, now)) > ReaimDriftMm;
        if (!turned && f.Flight != null) return;
        var plan = FlightPlanner.PlanChase(w, f, into, ChaseMode.Join, c.SpeedFraction, FleetPerformance.Of(w, f));
        c.TargetAt = into.Position;
        c.TargetVelocity = into.Velocity;
        c.PlannedHour = now;
        if (plan is { Ok: true, Flight: Flight flight })
        {
            f.Flight = flight;
            c.Problem = null;
        }
        else if (c.Problem != plan.Problem)
        {
            c.Problem = plan.Problem;
            sim.Log.Warn("fleet", $"{f.Name} cannot catch up with {into.Name}", new { fleetId = f.Id, intoFleetId = into.Id, problem = plan.Problem });
        }
    }
}
