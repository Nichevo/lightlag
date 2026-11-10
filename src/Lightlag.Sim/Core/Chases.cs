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
        return new ChaseOrder
        {
            TargetFleetId = targetId,
            Mode = mode,
            SpeedFraction = speedFraction,
            TargetAt = Sensors.Predict(w, f.Side, targetId, w.Hour) ?? Vec2.Zero,
            TargetVelocity = s?.Velocity ?? Vec2.Zero,
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
            var s = Sensors.Find(w, f.Side, c.TargetFleetId);
            if (target == null || s == null || f.Retreating)
            {
                f.Chase = null;
                if (!f.Retreating)
                    sim.Log.Info("fleet", $"{f.Name} loses track of its quarry and stops chasing", new { fleetId = f.Id, targetFleetId = c.TargetFleetId });
                continue;
            }
            if (f.Maneuvering) continue; // in a fight: combat steers
            if (c.Mode == ChaseMode.Overtake && f.Flight == null)
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
            if (!turned && !adrift) continue;

            var perf = FleetPerformance.Of(w, f);
            var plan = FlightPlanner.PlanChase(w, f, target, c.Mode, c.SpeedFraction, perf);
            c.TargetAt = guess;
            c.TargetVelocity = s.Velocity;
            c.PlannedHour = now;
            if (plan is { Ok: true, Flight: Flight flight })
            {
                f.Flight = flight;
                c.Problem = null;
                sim.Log.Info("fleet", $"{f.Name} re-aims its chase", new
                {
                    fleetId = f.Id, targetFleetId = target.Id, mode = c.Mode.ToString(), flight.ArriveHour, plan.DeltaV, turned, adrift,
                });
            }
            else if (c.Problem != plan.Problem)
            {
                c.Problem = plan.Problem;
                sim.Log.Warn("fleet", $"{f.Name} cannot keep up the chase", new { fleetId = f.Id, targetFleetId = target.Id, problem = plan.Problem });
            }
        }
    }
}
