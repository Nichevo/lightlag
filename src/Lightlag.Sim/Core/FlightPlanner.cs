namespace Lightlag.Sim.Core;

/// <summary>What the player asks for in the route planner.</summary>
public sealed record FlightRequest(
    int? TargetBodyId,
    Vec2 Point,
    double SpeedFraction,
    ArrivalStyle Arrival,
    HoldStyle HoldAfter,
    long DepartTick,
    RouteKind Route = RouteKind.Direct);

/// <summary>The planner's answer: the flight, what it costs, or why it cannot be flown.</summary>
public sealed record FlightPlan(bool Ok, string? Problem, Flight? Flight, double DeltaV, double DeltaVLeft)
{
    /// <summary>Share of the fleet's fuel this trip uses (0..1, can be above 1 when it is not enough).</summary>
    public double FuelShare => DeltaVLeft > 0 ? DeltaV / DeltaVLeft : double.PositiveInfinity;

    public static FlightPlan Fail(string problem, double dvLeft = 0) => new(false, problem, null, 0, dvLeft);
}

/// <summary>
/// Plans flights. A flight burns up to cruise speed, coasts, and (unless it is a fast pass) brakes to
/// a stop. Flying faster costs more fuel (each burn costs the speed it adds or removes) but gets there
/// sooner. Targets move, so the planner aims at where the target will be on arrival.
/// The game, the AI and the tests all plan through here, so what the planner shows is what flies.
/// </summary>
public static class FlightPlanner
{
    public const double MinSpeedFraction = 0.2;
    public const int WaitSearchDays = 120;
    private const double MaxSearchHours = 3 * 365.25 * 24;

    public static FlightPlan Plan(World w, Fleet fleet, FlightRequest req)
    {
        var perf = FleetPerformance.Of(w, fleet);
        if (perf.MaxSpeed <= 0 || perf.Accel <= 0) return FlightPlan.Fail("The fleet has no ships that can fly");
        double now = w.Tick;
        var legs = new List<FlightLeg>();
        double dv = 0;

        // A fleet still moving (after a fast pass, or mid-flight) first brakes to a stop.
        var ready = now;
        Vec2? restPoint = null;
        if (fleet.Velocity.Length > 1e-9)
        {
            var v = fleet.Velocity;
            var tb = v.Length / perf.Accel;
            var brake = new FlightLeg(now, now + tb, fleet.Position, v, v.Normalized() * -perf.Accel);
            legs.Add(brake);
            dv += v.Length;
            ready = now + tb;
            restPoint = brake.PositionAt(brake.End);
        }

        var depart = Math.Max(ready, (double)req.DepartTick);
        var p0 = restPoint ?? PredictHoldPosition(w, fleet, depart);
        var cruise = Math.Clamp(req.SpeedFraction, MinSpeedFraction, 1.0) * perf.MaxSpeed;
        var a = perf.Accel;

        Body? body = null;
        if (req.TargetBodyId is int bodyId)
        {
            body = w.FindBody(bodyId);
            if (body == null) return FlightPlan.Fail("That target no longer exists", perf.DeltaVLeft);
        }

        Vec2 aim;
        if (body == null)
        {
            aim = req.Point;
        }
        else
        {
            var standoff = Fleets.OrbitRadiusFor(body);
            Vec2 AimAt(double hour)
            {
                var p = PredictBody(w, body, hour);
                var away = (p0 - p).Normalized();
                if (away == Vec2.Zero) away = new Vec2(1, 0);
                return p + away * standoff;
            }
            double Gap(double hour) => depart + Profile(Vec2.Distance(AimAt(hour), p0), cruise, a, req.Arrival).Total - hour;

            // Find the hour where "leave at depart and fly there" lands exactly on the target.
            var lo = depart;
            if (Gap(lo) > 0)
            {
                var step = 12.0;
                var hi = lo + step;
                while (Gap(hi) > 0)
                {
                    lo = hi;
                    hi += step;
                    if (hi - depart > MaxSearchHours)
                        return FlightPlan.Fail($"Too slow to catch {body.Name} at this speed", perf.DeltaVLeft);
                }
                for (var i = 0; i < 60; i++)
                {
                    var mid = (lo + hi) / 2;
                    if (Gap(mid) > 0) lo = mid; else hi = mid;
                }
                aim = AimAt(hi);
            }
            else
            {
                aim = AimAt(lo);
            }
        }

        var distance = Vec2.Distance(aim, p0);
        var profile = Profile(distance, cruise, a, req.Arrival);
        var dir = (aim - p0).Normalized();
        var t = depart;
        if (distance > 1e-9)
        {
            var burn = new FlightLeg(t, t + profile.AccelHours, p0, Vec2.Zero, dir * a);
            legs.Add(burn);
            t = burn.End;
            var p = burn.PositionAt(t);
            var vel = burn.VelocityAt(t);
            if (profile.CoastHours > 0)
            {
                var coast = new FlightLeg(t, t + profile.CoastHours, p, vel, Vec2.Zero);
                legs.Add(coast);
                t = coast.End;
                p = coast.PositionAt(t);
            }
            if (req.Arrival == ArrivalStyle.Brake)
            {
                var brake = new FlightLeg(t, t + profile.AccelHours, p, vel, dir * -a);
                legs.Add(brake);
                t = brake.End;
            }
            dv += profile.DeltaV;
        }

        var flight = new Flight
        {
            TargetBodyId = body?.Id,
            Target = aim,
            Route = req.Route,
            Arrival = req.Arrival,
            HoldAfter = req.Arrival == ArrivalStyle.FastPass || body == null ? HoldStyle.Drift : req.HoldAfter,
            SpeedFraction = Math.Clamp(req.SpeedFraction, MinSpeedFraction, 1.0),
            CruiseSpeed = profile.Peak,
            DepartHour = depart,
            ArriveHour = t,
            DeltaV = dv,
            Legs = legs,
        };
        if (dv > perf.DeltaVLeft + 1e-9)
            return new FlightPlan(false, "Not enough supplies for this trip: fly slower or refill at home", flight, dv, perf.DeltaVLeft);
        return new FlightPlan(true, null, flight, dv, perf.DeltaVLeft);
    }

    /// <summary>A flight that only brakes a moving fleet to a stop where it is heading. Null if it is not moving or lacks the fuel.</summary>
    public static Flight? BrakeToStop(World w, Fleet fleet)
    {
        var perf = FleetPerformance.Of(w, fleet);
        var v = fleet.Velocity;
        if (v.Length < 1e-9 || perf.Accel <= 0 || v.Length > perf.DeltaVLeft + 1e-9) return null;
        double now = w.Tick;
        var brake = new FlightLeg(now, now + v.Length / perf.Accel, fleet.Position, v, v.Normalized() * -perf.Accel);
        return new Flight
        {
            Target = brake.PositionAt(brake.End),
            Arrival = ArrivalStyle.Brake,
            HoldAfter = HoldStyle.Drift,
            SpeedFraction = 1,
            DepartHour = now,
            ArriveHour = brake.End,
            DeltaV = v.Length,
            Started = true,
            Legs = new List<FlightLeg> { brake },
        };
    }

    /// <summary>
    /// The Wait route: tries leaving on each of the next days and picks the departure with the shortest
    /// trip (the target swings closer). Returns the Direct plan if waiting never helps.
    /// </summary>
    public static FlightPlan PlanWait(World w, Fleet fleet, FlightRequest req, int days = WaitSearchDays)
    {
        FlightPlan? best = null;
        double bestTrip = double.MaxValue;
        for (var d = 0; d <= days; d++)
        {
            var plan = Plan(w, fleet, req with { DepartTick = w.Tick + d * GameTime.TicksPerDay, Route = RouteKind.Wait });
            if (plan.Flight == null) continue;
            var trip = plan.Flight.ArriveHour - plan.Flight.DepartHour;
            // Only wait a day longer if it saves a meaningful amount of flying.
            if (trip < bestTrip - 1) { best = plan; bestTrip = trip; }
        }
        return best ?? Plan(w, fleet, req with { Route = RouteKind.Wait });
    }

    /// <summary>Plans with whichever route the request names.</summary>
    public static FlightPlan PlanRoute(World w, Fleet fleet, FlightRequest req) =>
        req.Route == RouteKind.Wait ? PlanWait(w, fleet, req) : Plan(w, fleet, req with { DepartTick = w.Tick });

    private readonly record struct ProfileResult(double AccelHours, double CoastHours, double Peak, double DeltaV, double Total);

    /// <summary>Burn, coast, brake timings for a distance, starting from rest.</summary>
    private static ProfileResult Profile(double d, double cruise, double a, ArrivalStyle arrival)
    {
        if (d <= 1e-9) return new ProfileResult(0, 0, 0, 0, 0);
        if (arrival == ArrivalStyle.Brake)
        {
            var peak = Math.Min(cruise, Math.Sqrt(d * a));
            var ta = peak / a;
            var coast = Math.Max(0, (d - peak * peak / a) / peak);
            return new ProfileResult(ta, coast, peak, 2 * peak, 2 * ta + coast);
        }
        else
        {
            var peak = Math.Min(cruise, Math.Sqrt(2 * d * a));
            var ta = peak / a;
            var coast = Math.Max(0, (d - peak * peak / (2 * a)) / peak);
            return new ProfileResult(ta, coast, peak, peak, ta + coast);
        }
    }

    /// <summary>Where a body will be at a future game hour (circular orbits, so this is exact).</summary>
    public static Vec2 PredictBody(World w, Body b, double hour)
    {
        if (b.ParentId is not int pid || w.FindBody(pid) is not Body parent) return b.Position;
        var angle = b.Angle;
        if (b.OrbitPeriodDays > 0) angle += 2 * Math.PI * (hour - w.Tick) / (b.OrbitPeriodDays * 24);
        return PredictBody(w, parent, hour) + new Vec2(DetMath.Cos(angle), DetMath.Sin(angle)) * b.OrbitRadius;
    }

    /// <summary>Where a holding fleet will be at a future hour if left alone.</summary>
    public static Vec2 PredictHoldPosition(World w, Fleet f, double hour)
    {
        if (f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int id && w.FindBody(id) is Body body)
        {
            var angle = f.OrbitAngle + 2 * Math.PI * (hour - w.Tick) / Fleets.OrbitPeriodHours(body, f.OrbitRadius);
            return PredictBody(w, body, hour) + new Vec2(DetMath.Cos(angle), DetMath.Sin(angle)) * f.OrbitRadius;
        }
        return f.Position + f.Velocity * (hour - w.Tick);
    }

    /// <summary>Where a fleet on this flight is at a game hour (null before it leaves).</summary>
    public static (Vec2 Position, Vec2 Velocity)? FlightState(Flight f, double hour)
    {
        if (f.Legs.Count == 0 || hour < f.Legs[0].Start) return null;
        FlightLeg leg = f.Legs[0];
        foreach (var l in f.Legs)
        {
            if (l.Start > hour) break;
            leg = l;
        }
        if (hour <= leg.End) return (leg.PositionAt(hour), leg.VelocityAt(hour));
        // Past the end of a leg: between legs the fleet is at rest; after a fast pass it carries on.
        var end = leg.PositionAt(leg.End);
        var v = leg.VelocityAt(leg.End);
        return (end + v * (hour - leg.End), v);
    }

    /// <summary>Speed change (fuel) burned between two game hours.</summary>
    public static double DeltaVBetween(Flight f, double from, double to)
    {
        double dv = 0;
        foreach (var l in f.Legs)
        {
            if (!l.IsBurn) continue;
            var overlap = Math.Min(l.End, to) - Math.Max(l.Start, from);
            if (overlap > 0) dv += l.A.Length * overlap;
        }
        return dv;
    }
}
