namespace Lightlag.Sim.Core;

/// <summary>
/// Slingshots: a route with one flyby of a planet on the way. Seen from the planet, the fleet leaves
/// as fast as it came but bent round by gravity; seen from the star, the planet's own motion is added,
/// so the bend can speed the fleet up (or point it at the target) for no fuel. How far it can bend
/// depends on the planet's mass, how close the pass is and how fast the fleet is going: big planets
/// (Tamber, Coldwell) bend a lot, small ones hardly at all, and a slower approach bends more.
/// The planner flies to the flyby point without braking, swings, then curves onto the target.
/// </summary>
public static partial class FlightPlanner
{
    /// <summary>How many angles the planner tries for the bend.</summary>
    private const int FlybySteps = 12;

    /// <summary>How much an hour of burning counts against an hour of travel when choosing the bend.</summary>
    private const double FuelHourWeight = 0.25;

    /// <summary>A slingshot is only offered when it beats the direct route by this share of the time, or saves this share of the fuel without arriving later.</summary>
    public const double MinSlingshotTimeGain = 0.03, MinSlingshotFuelGain = 0.1;

    /// <summary>Planets light enough that a flyby does nothing worth planning are skipped (Earth masses).</summary>
    public const double MinFlybyMassEarth = 0.5;

    /// <summary>Closest pass, in planet radii (at least the orbit height).</summary>
    public const double FlybyRadii = 2;

    public static double FlybyRadius(Body b) => Math.Max(b.Radius * FlybyRadii, Fleets.OrbitRadiusFor(b));

    /// <summary>Planets worth a flyby for this trip: heavy enough, and not the target itself.</summary>
    public static IEnumerable<Body> FlybyCandidates(World w, FlightRequest req) =>
        w.Bodies.Where(b => b.Kind == BodyKind.Planet && b.Id != req.TargetBodyId && b.Gm >= MinFlybyMassEarth * Physics.GmEarth);

    /// <summary>The slingshot that arrives soonest, over every planet worth a flyby (or a failed plan).</summary>
    public static (FlightPlan Plan, Body? Via) BestSlingshot(World w, Fleet fleet, FlightRequest req)
    {
        FlightPlan? best = null;
        Body? via = null;
        foreach (var b in FlybyCandidates(w, req))
        {
            var plan = Plan(w, fleet, req with { Route = RouteKind.Slingshot, FlybyBodyId = b.Id });
            if (plan is not { Ok: true, Flight: not null }) continue;
            if (best == null || plan.Flight.ArriveHour < best.Flight!.ArriveHour - 1e-6) { best = plan; via = b; }
        }
        if (best?.Flight != null && !Beats(best, Plan(w, fleet, req with { Route = RouteKind.Direct, FlybyBodyId = null })))
            return (FlightPlan.Fail("No swing past a planet beats the direct route right now", best.DeltaVLeft), null);
        return (best ?? FlightPlan.Fail("No planet on the way gives a useful slingshot"), via);
    }

    /// <summary>Whether a slingshot is worth flying instead of the direct route.</summary>
    public static bool Beats(FlightPlan sling, FlightPlan direct)
    {
        if (sling.Flight == null) return false;
        if (direct.Flight == null) return true;
        var trip = Math.Max(1, direct.Flight.ArriveHour - direct.Flight.DepartHour);
        if (sling.Flight.ArriveHour <= direct.Flight.ArriveHour - trip * MinSlingshotTimeGain) return true;
        return sling.Flight.ArriveHour <= direct.Flight.ArriveHour + 1 && sling.DeltaV <= direct.DeltaV * (1 - MinSlingshotFuelGain);
    }

    private static FlightPlan PlanSlingshot(World w, Fleet fleet, FlightRequest req, FleetPerformance perf, Body? body, Fleet? enemy,
        Func<Vec2, double, Vec2> aimFrom, int flybyId)
    {
        if (w.FindBody(flybyId) is not Body planet || !(planet.Gm > 0)) return FlightPlan.Fail("No such planet to swing past", perf.DeltaVLeft);
        if (fleet.Velocity.Length > 1e-9) return FlightPlan.Fail("Stop first: a slingshot is planned from rest", perf.DeltaVLeft);
        var now = w.Hour;
        var a = perf.Accel;
        var cruise = Math.Clamp(req.SpeedFraction, MinSpeedFraction, 1.0) * perf.MaxSpeed;
        var t0 = Math.Max(now, req.DepartTick);
        var p0 = PredictHoldPosition(w, fleet, t0);
        var rp = FlybyRadius(planet);

        // Pass the planet on the side that bends the course towards the target.
        var planetNow = PredictBody(w, planet, t0);
        var inDir = (planetNow - p0).Normalized();
        var onward = (aimFrom(planetNow, t0) - planetNow).Normalized();
        if (inDir == Vec2.Zero) return FlightPlan.Fail($"Already at {planet.Name}", perf.DeltaVLeft);
        var side = inDir.X * onward.Y - inDir.Y * onward.X >= 0 ? 1.0 : -1.0;
        Vec2 FlybyAim(Vec2 from, double hour)
        {
            var c = PredictBody(w, planet, hour);
            var dir = (c - from).Normalized();
            if (dir == Vec2.Zero) dir = inDir;
            return c + new Vec2(-dir.Y, dir.X) * (rp * side);
        }

        var first = Stretch(p0, Vec2.Zero, t0, a, cruise, double.PositiveInfinity, FlybyAim);
        if (first == null) return FlightPlan.Fail($"Cannot reach {planet.Name}", perf.DeltaVLeft);
        var (p1, vIn, t1) = (first.EndP, first.EndV, first.EndT);

        // The swing: in the planet's frame the speed stays, the direction bends by up to maxBend.
        var vPlanet = (PredictBody(w, planet, t1 + 0.5) - PredictBody(w, planet, t1 - 0.5));
        var vInf = vIn - vPlanet;
        var s = vInf.Length;
        if (s < 1e-6) return FlightPlan.Fail($"Too slow to swing past {planet.Name}", perf.DeltaVLeft);
        var e = 1 + rp * s * s / planet.Gm;           // eccentricity of the pass
        var maxBend = 2 * DetMath.Atan2(1 / e, Math.Sqrt(1 - 1 / (e * e)));
        // Try a spread of bends and keep the one that gets there soonest (fuel breaks ties).
        StretchPlan? second = null;
        Vec2 vOut = vIn;
        var bestCost = double.MaxValue;
        for (var i = 0; i <= FlybySteps; i++)
        {
            var angle = -maxBend + 2 * maxBend * i / FlybySteps;
            var (c, sn) = (DetMath.Cos(angle), DetMath.Sin(angle));
            var bent = vPlanet + new Vec2(vInf.X * c - vInf.Y * sn, vInf.X * sn + vInf.Y * c);
            var trial = Stretch(p1, bent, t1, a, Math.Max(cruise, bent.Length), EndSpeed(req.Arrival), aimFrom);
            if (trial == null) continue;
            var cost = trial.EndT + (first.DeltaV + trial.DeltaV) / Math.Max(a, 1e-9) * FuelHourWeight;
            if (cost < bestCost) { bestCost = cost; second = trial; vOut = bent; }
        }
        if (second == null) return FlightPlan.Fail($"Cannot reach the target after swinging past {planet.Name}", perf.DeltaVLeft);
        var legs = first.Legs.Concat(second.Legs).ToList();
        foreach (var leg in legs)
            for (var k = 0; k <= 16; k++)
            {
                var h = leg.Start + (leg.End - leg.Start) * k / 16;
                var at = leg.PositionAt(h);
                foreach (var b in w.Bodies)
                    if (b.Radius > 0 && Vec2.Distance(at, PredictBody(w, b, h)) < b.Radius * 1.05)
                        return FlightPlan.Fail($"The swing past {planet.Name} would run into {b.Name}", perf.DeltaVLeft);
            }
        var plan = Finish(w, req, body, enemy, perf, legs, second.Aim, cruise, t0, first.DeltaV + second.DeltaV);
        if (plan.Flight is Flight flight)
        {
            flight.Route = RouteKind.Slingshot;
            flight.FlybyBodyId = planet.Id;
            flight.FlybyHour = t1;
            flight.FlybyPoint = p1;
            flight.FlybyGain = vOut.Length - vIn.Length;
        }
        return plan;
    }
}
