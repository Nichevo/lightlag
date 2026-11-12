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

    private static SlingshotData S => GameData.Slingshots;
    private const double KmSToMmPerHour = 3.6;

    /// <summary>Planets light enough that a flyby does nothing worth planning are skipped (Earth masses).</summary>
    public const double MinFlybyMassEarth = 0.5;

    /// <summary>Closest pass, in planet radii (at least the orbit height): wide enough that the swing reads as a curve round the planet, not a graze.</summary>
    public const double FlybyRadii = 4;

    public static double FlybyRadius(Body b) => Math.Max(b.Radius * FlybyRadii, Fleets.OrbitRadiusFor(b));

    /// <summary>Planets worth a flyby for this trip: heavy enough, and not the target itself.</summary>
    public static IEnumerable<Body> FlybyCandidates(World w, FlightRequest req) =>
        w.Bodies.Where(b => b.Kind == BodyKind.Planet && b.Id != req.TargetBodyId && b.Gm >= MinFlybyMassEarth * Physics.GmEarth);

    /// <summary>The slingshot that arrives soonest, over every planet worth a flyby (or a failed plan).</summary>
    public static (FlightPlan Plan, Body? Via) BestSlingshot(World w, Fleet fleet, FlightRequest req)
    {
        var direct = Plan(w, fleet, req with { Route = RouteKind.Direct, FlybyBodyId = null });
        FlightPlan? best = null;
        Body? via = null;
        var bestScore = double.MinValue;
        var any = false;
        // Each planet at the speed asked for, and gentler: a slower start that the swing makes up saves fuel.
        foreach (var b in FlybyCandidates(w, req))
        foreach (var slower in S.FuelSaverSpeeds)
        {
            var speed = Math.Max(MinSpeedFraction, req.SpeedFraction * slower);
            if (slower < 1 && speed >= req.SpeedFraction - 1e-9) continue;
            var plan = Plan(w, fleet, req with { Route = RouteKind.Slingshot, FlybyBodyId = b.Id, SpeedFraction = speed });
            if (plan is not { Ok: true, Flight: not null }) continue;
            any = true;
            if (!Beats(plan, direct)) continue;
            // Time and fuel saved, each as a share of the direct route's; time counts double, since a slingshot is flown to get there sooner.
            var score = direct.Flight == null ? -plan.Flight.ArriveHour
                : 2 * (direct.Flight.ArriveHour - plan.Flight.ArriveHour) / Math.Max(1, direct.Flight.ArriveHour - direct.Flight.DepartHour)
                  + (direct.DeltaV - plan.DeltaV) / Math.Max(1e-9, direct.DeltaV);
            if (score > bestScore) { bestScore = score; best = plan; via = b; }
        }
        if (best != null) return (best, via);
        return (FlightPlan.Fail(any ? "No swing past a planet beats the direct route right now" : "No planet on the way gives a useful slingshot",
            direct.DeltaVLeft), null);
    }

    /// <summary>Whether a slingshot is worth flying instead of the direct route.</summary>
    public static bool Beats(FlightPlan sling, FlightPlan direct)
    {
        if (sling.Flight == null) return false;
        if (direct.Flight == null) return true;
        var trip = Math.Max(1, direct.Flight.ArriveHour - direct.Flight.DepartHour);
        var saved = (direct.Flight.ArriveHour - sling.Flight.ArriveHour) / trip;
        if (saved >= S.MinTimeGain && sling.DeltaV <= direct.DeltaV * (1.25 + saved)) return true;
        return sling.Flight.ArriveHour <= direct.Flight.ArriveHour + trip * 0.05 && sling.DeltaV <= direct.DeltaV * (1 - S.MinFuelGain);
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

        // Try a few burns for the stretch to the planet and a spread of bends at it, and keep the plan that
        // costs least: travel time, with fuel counted against it (a gentler burn out lets the swing add the speed).
        StretchPlan? first = null, second = null;
        Vec2 vIn = Vec2.Zero, vOut = Vec2.Zero;
        double t1 = 0, swingBurn = 0;
        var bestCost = double.MaxValue;
        string? problem = null;
        foreach (var share in S.Departs)
        {
            var out1 = Stretch(p0, Vec2.Zero, t0, a, cruise * share, double.PositiveInfinity, FlybyAim);
            if (out1 == null) { problem ??= $"Cannot reach {planet.Name}"; continue; }
            var (p1, vi, t) = (out1.EndP, out1.EndV, out1.EndT);
            // The swing: in the planet's frame the speed stays, the direction bends by up to maxBend.
            var vPlanet = PredictBody(w, planet, t + 0.5) - PredictBody(w, planet, t - 0.5);
            var vInf = vi - vPlanet;
            var speed = vInf.Length;
            if (speed < 1e-6) { problem ??= $"Too slow to swing past {planet.Name}"; continue; }
            // The swing: in the planet's frame the fleet leaves bent round by up to maxBend. The burn held back
            // on the way out is spent at the closest pass, deep in the planet's pull, where it adds more speed
            // than it would out in open space (the Oberth effect).
            // Game physics: the heavier the planet, the harder it pulls and the more speed it hands over.
            var weight = S.Weight(planet.Gm);
            var gm = planet.Gm * (1 + (S.AssistStrength - 1) * weight);
            var e = 1 + rp * speed * speed / gm;           // eccentricity of the pass
            var maxBend = 2 * DetMath.Atan2(1 / e, Math.Sqrt(1 - 1 / (e * e)));
            var burn = cruise * (1 - share);
            var escapeSq = 2 * gm / rp;
            var atPass = Math.Sqrt(speed * speed + escapeSq) + burn;
            var leaving = Math.Sqrt(Math.Max(speed * speed, atPass * atPass - escapeSq)) + S.GiantKickKmS * KmSToMmPerHour * weight;
            // Gravity assist navigation: the swing hands over a fifth more speed.
            if (w.Loop && Tech.Has(w, fleet.Side, "gravity-assist")) leaving = speed + (leaving - speed) * GameData.Research("gravity-assist").Factor;
            var outInf = vInf * (leaving / speed);
            for (var i = 0; i <= FlybySteps; i++)
            {
                var angle = -maxBend + 2 * maxBend * i / FlybySteps;
                var (c, sn) = (DetMath.Cos(angle), DetMath.Sin(angle));
                var bent = vPlanet + new Vec2(outInf.X * c - outInf.Y * sn, outInf.X * sn + outInf.Y * c);
                var trial = Stretch(p1, bent, t, a, Math.Max(cruise * share, bent.Length), EndSpeed(req.Arrival), aimFrom);
                if (trial == null) continue;
                var dv = out1.DeltaV + burn + trial.DeltaV;
                var cost = (trial.EndT - t0) * (1 + S.FuelHourWeight * dv / (2 * cruise));
                if (cost < bestCost) { bestCost = cost; first = out1; second = trial; vIn = vi; vOut = bent; t1 = t; swingBurn = burn; }
            }
        }
        if (first == null || second == null)
            return FlightPlan.Fail(problem ?? $"Cannot reach the target after swinging past {planet.Name}", perf.DeltaVLeft);
        var p1Best = first.EndP;
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
        var plan = Finish(w, req, body, enemy, perf, legs, second.Aim, cruise, t0, first.DeltaV + swingBurn + second.DeltaV);
        if (plan.Flight is Flight flight)
        {
            flight.Route = RouteKind.Slingshot;
            flight.FlybyBodyId = planet.Id;
            flight.FlybyHour = t1;
            flight.FlybyPoint = p1Best;
            flight.FlybyBurn = swingBurn;
            flight.FlybyGain = vOut.Length - vIn.Length;
        }
        return plan;
    }
}
