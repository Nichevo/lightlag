namespace Lightlag.Sim.Core;

/// <summary>
/// Keeping enough fuel to get home. Every hour a fleet away from its own planets works out the
/// fuel a slow, cold coast to the nearest planet its side holds would take (any held planet refuels).
/// When its fuel falls to that reserve (plus a margin) it breaks off whatever it is doing, stops firing
/// and dodging, and coasts there to refill, and it reports that it has done so. A fleet that already
/// cannot make it says it is stranded.
/// </summary>
public static class Supply
{
    /// <summary>The margin kept on top of the reserve.</summary>
    public const double Margin = 1.25;

    /// <summary>Supplies a fleet needs back before it counts as resupplied (share of full).</summary>
    public const double RefilledAt = 0.9;

    /// <summary>Hourly: the reserve for fleets away from home, and whether a fleet heading off to refill is full again.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop) return;
        foreach (var f in w.Fleets.ToList())
        {
            if (f.Resupplying)
            {
                if (Combat.SupplyShare(w, f) >= RefilledAt)
                {
                    f.Resupplying = false;
                    f.ReserveDeltaV = 0;
                    sim.Log.Info("supply", $"{f.Name} has refilled", new { fleetId = f.Id });
                }
                continue;
            }
            f.ReserveDeltaV = Estimate(w, f);
        }
    }

    /// <summary>
    /// A quick estimate of the fuel a slow coast to the nearest planet this side holds takes: match the
    /// planet's motion, speed up to a slow cruise and brake again. 0 when already in orbit at one, or none is held.
    /// </summary>
    public static double Estimate(World w, Fleet f)
    {
        if (f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int at && w.FindBody(at)?.Owner == f.Side) return 0;
        var body = w.Bodies.Where(b => b.Owner == f.Side).OrderBy(b => Vec2.Distance(b.Position, f.Position)).FirstOrDefault();
        if (body == null) return 0;
        var cruise = FlightPlanner.MinSpeedFraction * FleetPerformance.Of(w, f).MaxSpeed;
        return 2 * cruise + (Sensors.MotionOf(w, f) - Combat.BodyVelocity(w, body)).Length;
    }

    /// <summary>Every step (before steering): a fleet down to its reserve breaks off and coasts to refill.</summary>
    public static void Check(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop) return;
        foreach (var f in w.Fleets.ToList())
        {
            if (f.Resupplying || f.ReserveDeltaV <= 0) continue;
            var left = FleetPerformance.Of(w, f).DeltaVLeft;
            if (left > f.ReserveDeltaV * Margin) continue;
            GoRefill(sim, f);
        }
    }

    /// <summary>The nearest planet this side holds by fuel, and the fuel a slow coast there takes; null if none.</summary>
    public static (Body Body, double DeltaV)? Nearest(World w, Fleet f)
    {
        (Body, double)? best = null;
        foreach (var b in w.Bodies.Where(b => b.Owner == f.Side).OrderBy(b => Vec2.Distance(b.Position, f.Position)).Take(3))
        {
            var plan = FlightPlanner.Plan(w, f, Request(w, b));
            if (plan.Flight == null) continue;
            if (best is not (_, double bestDv) || plan.DeltaV < bestDv) best = (b, plan.DeltaV);
        }
        return best;
    }

    private static FlightRequest Request(World w, Body b, double speed = FlightPlanner.MinSpeedFraction) =>
        new(b.Id, b.Position, speed, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick);

    /// <summary>Speeds tried for the trip home, fastest first: the quickest one the fuel left can pay for.</summary>
    private static readonly double[] RefillSpeeds = { 0.5, 0.3, 0.2 };

    /// <summary>Breaks off and coasts to the nearest held planet to refill, and says so.</summary>
    public static void GoRefill(Simulation sim, Fleet f)
    {
        var w = sim.World;
        Sparks.Fire(sim, f.Side, "fleet-dry");
        f.Resupplying = true;
        f.Maneuvering = false;
        f.Chase = null;
        f.Mission = Mission.None;
        var target = Nearest(w, f);
        FlightPlan? plan = null;
        if (target is (Body b, _))
        {
            var left = FleetPerformance.Of(w, f).DeltaVLeft;
            foreach (var speed in RefillSpeeds.Where(v => v > FlightPlanner.MinSpeedFraction))
            {
                var fast = FlightPlanner.Plan(w, f, Request(w, b, speed));
                if (fast is { Ok: true, Flight: not null } && fast.DeltaV <= left) { plan = fast; break; }
            }
            plan ??= FlightPlanner.Plan(w, f, Request(w, b));
        }
        if (plan is { Ok: true, Flight: Flight flight })
        {
            f.Flight = flight;
            var body = target!.Value.Body;
            Autonomy.Report(sim, f, $"{f.Name} is low on supplies: breaks off and coasts to {body.Name} to refill (arrives in {Hours(flight.ArriveHour - w.Hour)})", NoteKind.Supply);
        }
        else
        {
            Autonomy.Report(sim, f, $"{f.Name} is out of supplies and drifting: it cannot reach a planet you hold", NoteKind.Supply);
        }
    }

    private static string Hours(double h) => h < 48 ? FormattableString.Invariant($"{h:0} hours") : FormattableString.Invariant($"{h / 24:0.#} days");
}
