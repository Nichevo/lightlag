using Lightlag.Sim.Logging;
namespace Lightlag.Sim.Core;

/// <summary>What the player asks for in the route planner.</summary>
public sealed record FlightRequest(
    int? TargetBodyId,
    Vec2 Point,
    double SpeedFraction,
    ArrivalStyle Arrival,
    HoldStyle HoldAfter,
    long DepartTick,
    RouteKind Route = RouteKind.Direct,
    int? TargetFleetId = null,
    List<Waypoint>? Waypoints = null,
    ChaseMode Chase = ChaseMode.None,
    int? FlybyBodyId = null);

/// <summary>
/// A point the route goes through on the way. The fleet flies through it and turns towards the next
/// point, or brakes to a stop there (and can wait). SpeedFraction sets the pace of the stretch that
/// ends here (null: the route's speed); a slow stretch is a long cold coast.
/// </summary>
/// Auto marks a turning point the planner added itself, to steer round a planet, moon or the star.
public sealed record Waypoint(Vec2 Point, bool Stop = false, double? SpeedFraction = null, double WaitHours = 0, bool Auto = false);

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
public static partial class FlightPlanner
{
    public const double MinSpeedFraction = 0.2;
    public const int WaitSearchDays = 120;
    private const double MaxSearchHours = 3 * 365.25 * 24;

    public static FlightPlan Plan(World w, Fleet fleet, FlightRequest req)
    {
        using var timed = SectionTimer.Time("route-planner");
        int? from = fleet.Hold == HoldStyle.Orbit && fleet.Velocity.Length <= 1e-9 ? fleet.OrbitBodyId : null;
        // A fleet redirected while it is still climbing out of, or settling into, a body (a flight between
        // moons skims them) leaves from that body, so it may pass through its surface on the way out.
        from ??= w.Bodies.FirstOrDefault(b => b.Radius > 0 && Vec2.Distance(fleet.Position, b.Position) < b.Radius)?.Id;
        FlightPlan Straight(FlightRequest r)
        {
            var p = PlanStraight(w, fleet, r);
            if (p.Flight != null) p.Flight.FromBodyId = from;
            return p;
        }
        if (req.Route == RouteKind.Slingshot && req.TargetFleetId == null)
            return req.FlybyBodyId != null ? Straight(req) : BestSlingshot(w, fleet, req).Plan;
        var plan = Straight(req);
        var steered = Avoidance.Steer(w, fleet, req, plan, Straight);
        // Leaving a planet for its own moon on the far side, the detour round the planet can turn into a
        // trip of months (the planet runs off along its orbit while the fleet goes round). Leaving a few
        // hours later, when the moon has swung clear, is far quicker: look for that.
        if (from != null && req.TargetFleetId == null && req.Chase == ChaseMode.None && req.Waypoints is not { Count: > 0 }
            && plan.Flight != null && steered.Flight != null && steered.Flight.ArriveHour - plan.Flight.ArriveHour > 48)
        {
            for (var h = 2; h <= 36; h += 2)
            {
                var later = req with { DepartTick = req.DepartTick + h };
                var first = Straight(later);
                if (first.Flight == null) continue;
                var tried = Avoidance.Steer(w, fleet, later, first, Straight);
                if (tried.Flight != null && tried.Flight.ArriveHour < steered.Flight!.ArriveHour - 1 && Avoidance.FirstHit(w, tried.Flight) == null)
                    steered = tried;
                if (steered.Flight!.ArriveHour - plan.Flight.ArriveHour <= 48) break;
            }
        }
        return steered;
    }

    /// <summary>Plans the flight in straight stretches, without looking for planets in the way.</summary>
    private static FlightPlan PlanStraight(World w, Fleet fleet, FlightRequest req)
    {
        var perf = FleetPerformance.Of(w, fleet);
        if (perf.MaxSpeed <= 0 || perf.Accel <= 0) return FlightPlan.Fail("The fleet has no ships that can fly");
        var now = w.Hour;
        var legs = new List<FlightLeg>();
        double dv = 0;

        Body? body = null;
        if (req.TargetBodyId is int bodyId)
        {
            body = w.FindBody(bodyId);
            if (body == null) return FlightPlan.Fail("That target no longer exists", perf.DeltaVLeft);
        }

        Fleet? enemy = null;
        if (req.TargetFleetId is int fleetId)
        {
            enemy = w.FindFleet(fleetId);
            if (enemy == null) return FlightPlan.Fail("That fleet is gone", perf.DeltaVLeft);
            if (Sensors.Predict(w, fleet.Side, enemy.Id, now) == null) return FlightPlan.Fail("That fleet has not been seen", perf.DeltaVLeft);
            body = null;
        }

        var standoffFor = body != null ? Fleets.OrbitRadiusFor(body)
            : enemy != null && req.Arrival == ArrivalStyle.Brake ? GameData.Combat.EngageRangeMm * 0.75 : 0;
        Vec2 AimFrom(Vec2 from, double hour)
        {
            if (body == null && enemy == null) return req.Point;
            var p = body != null ? PredictBody(w, body, hour) : Sensors.Predict(w, fleet.Side, enemy!.Id, hour)!.Value;
            var away = (from - p).Normalized();
            if (away == Vec2.Zero) away = new Vec2(1, 0);
            return p + away * standoffFor;
        }

        if (enemy != null && req.Chase != ChaseMode.None)
            return PlanChase(w, fleet, enemy, req.Chase, req.SpeedFraction, perf);

        if (req.Route == RouteKind.Slingshot && req.FlybyBodyId is int flyby)
            return PlanSlingshot(w, fleet, req, perf, body, enemy, AimFrom, flyby);

        if (req.Waypoints is { Count: > 0 })
            return PlanWaypoints(w, fleet, req, perf, body, enemy, AimFrom);

        // A fleet already moving and told to go now swings round onto the new course in one curving burn.
        if (fleet.Velocity.Length > 1e-9 && req.DepartTick <= now + 1e-9)
        {
            var cruiseNow = Math.Clamp(req.SpeedFraction, MinSpeedFraction, 1.0) * perf.MaxSpeed;
            if (Redirect(fleet.Position, fleet.Velocity, now, perf.Accel, cruiseNow, req.Arrival, AimFrom) is { } turn)
                return Finish(w, req, body, enemy, perf, turn.Legs, turn.Aim, turn.Speed, now, turn.DeltaV);
        }

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

        Vec2 aim;
        if (body == null && enemy == null)
        {
            aim = req.Point;
        }
        else
        {
            // Bodies: arrive at orbit height. Fleets: brake just inside railgun range, or fly straight through.
            var standoff = body != null ? Fleets.OrbitRadiusFor(body)
                : req.Arrival == ArrivalStyle.Brake ? GameData.Combat.EngageRangeMm * 0.75 : 0;
            var targetName = body?.Name ?? enemy!.Name;
            Vec2 AimAt(double hour)
            {
                var p = body != null ? PredictBody(w, body, hour) : Sensors.Predict(w, fleet.Side, enemy!.Id, hour)!.Value;
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
                        return FlightPlan.Fail($"Too slow to catch {targetName} at this speed", perf.DeltaVLeft);
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

        return Finish(w, req, body, enemy, perf, legs, aim, profile.Peak, depart, dv);
    }

    private static FlightPlan Finish(World w, FlightRequest req, Body? body, Fleet? enemy, FleetPerformance perf,
        List<FlightLeg> legs, Vec2 aim, double cruise, double depart, double dv)
    {
        var flight = new Flight
        {
            TargetBodyId = body?.Id,
            TargetFleetId = enemy?.Id,
            Target = aim,
            Route = req.Route,
            Arrival = req.Arrival,
            HoldAfter = req.Arrival == ArrivalStyle.FastPass || body == null ? HoldStyle.Drift : req.HoldAfter,
            SpeedFraction = Math.Clamp(req.SpeedFraction, MinSpeedFraction, 1.0),
            CruiseSpeed = cruise,
            DepartHour = depart,
            ArriveHour = legs.Count > 0 ? legs[^1].End : depart,
            DeltaV = dv,
            Legs = legs,
        };
        if (dv > perf.DeltaVLeft + 1e-9)
            return new FlightPlan(false, $"Not enough supplies for this trip (needs {Kms(dv)} of speed change, has {Kms(perf.DeltaVLeft)}): fly slower or refill at home", flight, dv, perf.DeltaVLeft);
        return new FlightPlan(true, null, flight, dv, perf.DeltaVLeft);
    }

    private sealed record Turn(List<FlightLeg> Legs, Vec2 Aim, double Speed, double DeltaV);

    /// <summary>One stretch of a route: the legs, where it ended up aiming, and the state at its end.</summary>
    private sealed record StretchPlan(List<FlightLeg> Legs, Vec2 Aim, double DeltaV, Vec2 EndP, Vec2 EndV, double EndT);

    /// <summary>A route through waypoints: each stretch is planned from where and how fast the last one ended.</summary>
    private static FlightPlan PlanWaypoints(World w, Fleet fleet, FlightRequest req, FleetPerformance perf, Body? body, Fleet? enemy,
        Func<Vec2, double, Vec2> aimFrom)
    {
        var now = w.Hour;
        var a = perf.Accel;
        Vec2 p, v;
        double t;
        if (fleet.Velocity.Length > 1e-9) { p = fleet.Position; v = fleet.Velocity; t = now; }
        else { t = Math.Max(now, req.DepartTick); p = PredictHoldPosition(w, fleet, t); v = Vec2.Zero; }
        var depart = t;
        var legs = new List<FlightLeg>();
        double dv = 0;
        double Speed(double? fraction) => Math.Clamp(fraction ?? req.SpeedFraction, MinSpeedFraction, 1.0) * perf.MaxSpeed;

        var points = req.Waypoints!;
        var start = p;
        var reached = new List<double>();
        for (var i = 0; i < points.Count; i++)
        {
            var wp = points[i];
            var cruiseHere = Speed(wp.SpeedFraction);
            var endSpeed = 0.0;
            if (!wp.Stop)
            {
                // Fly through at a speed the turn onto the next stretch allows, not flat out.
                var prev = i == 0 ? start : points[i - 1].Point;
                var next = i + 1 < points.Count ? points[i + 1].Point : aimFrom(wp.Point, t);
                var nextCruise = Speed(i + 1 < points.Count ? points[i + 1].SpeedFraction : null);
                endSpeed = CornerSpeed(prev, wp.Point, next, a, Math.Min(cruiseHere, nextCruise));
            }
            var stretch = Stretch(p, v, t, a, cruiseHere, endSpeed, (_, _) => wp.Point);
            if (stretch == null) return FlightPlan.Fail($"Cannot reach waypoint {i + 1}", perf.DeltaVLeft);
            legs.AddRange(stretch.Legs);
            dv += stretch.DeltaV;
            (p, v, t) = (stretch.EndP, stretch.EndV, stretch.EndT);
            reached.Add(t);
            if (wp.Stop && wp.WaitHours > 0)
            {
                legs.Add(new FlightLeg(t, t + wp.WaitHours, p, Vec2.Zero, Vec2.Zero));
                t += wp.WaitHours;
            }
        }
        var cruise = Speed(null);
        var last = Stretch(p, v, t, a, cruise, EndSpeed(req.Arrival), aimFrom);
        if (last == null) return FlightPlan.Fail("Cannot reach the target from the last waypoint", perf.DeltaVLeft);
        legs.AddRange(last.Legs);
        dv += last.DeltaV;
        var plan = Finish(w, req, body, enemy, perf, legs, last.Aim, cruise, depart, dv);
        if (plan.Flight is Flight flight)
        {
            flight.Waypoints.AddRange(points.Select(x => x.Point));
            flight.WaypointPlan.AddRange(points);
            flight.WaypointHours.AddRange(reached);
        }
        return plan;
    }

    /// <summary>
    /// How fast to pass a waypoint so the turn onto the next stretch stays tight: straight on keeps full
    /// speed, a sharp corner slows right down. The turn's swing past the corner is kept to about a
    /// quarter of the shorter stretch.
    /// </summary>
    private static double CornerSpeed(Vec2 prev, Vec2 at, Vec2 next, double a, double cruise)
    {
        var inDir = (at - prev).Normalized();
        var outDir = (next - at).Normalized();
        if (inDir == Vec2.Zero || outDir == Vec2.Zero) return 0;
        var cos = Math.Clamp(Vec2.Dot(inDir, outDir), -1, 1);
        var halfSin = Math.Sqrt((1 - cos) / 2); // sin of half the turn angle
        if (halfSin < 1e-6) return cruise;
        var shorter = Math.Min(Vec2.Distance(prev, at), Vec2.Distance(at, next));
        return Math.Min(cruise, Math.Sqrt(0.25 * shorter * a / (2 * halfSin)));
    }

    /// <summary>
    /// Fly from a point at a velocity to a (possibly moving) aim point: a curving burn if already
    /// moving, otherwise burn, coast and (when braking) brake, timed to meet the target where it will be.
    /// </summary>
    private static StretchPlan? Stretch(Vec2 p, Vec2 v, double t, double a, double cruise, double endSpeed, Func<Vec2, double, Vec2> aimFrom)
    {
        var legs = new List<FlightLeg>();
        double dv = 0;
        if (v.Length > 1e-9)
        {
            if (Redirect(p, v, t, a, cruise, endSpeed, aimFrom) is { } turn)
                return Ended(turn.Legs, turn.Aim, turn.DeltaV, p, v, t);
            // Cannot curve onto it (too close to stop in time, say): brake to a stop first.
            var tb = v.Length / a;
            var brake = new FlightLeg(t, t + tb, p, v, v.Normalized() * -a);
            legs.Add(brake);
            dv += v.Length;
            p = brake.PositionAt(brake.End);
            v = Vec2.Zero;
            t = brake.End;
        }

        double Gap(double hour) => t + Profile(Vec2.Distance(aimFrom(p, hour), p), cruise, a, endSpeed).Total - hour;
        var lo = t;
        var hi = t;
        if (Gap(lo) > 0)
        {
            hi = lo + 12;
            while (Gap(hi) > 0)
            {
                lo = hi;
                hi += 12;
                if (hi - t > MaxSearchHours) return null;
            }
            for (var i = 0; i < 60; i++)
            {
                var mid = (lo + hi) / 2;
                if (Gap(mid) > 0) lo = mid; else hi = mid;
            }
        }
        var aim = aimFrom(p, hi);
        var distance = Vec2.Distance(aim, p);
        var profile = Profile(distance, cruise, a, endSpeed);
        if (distance > 1e-9)
        {
            var dir = (aim - p).Normalized();
            var burn = new FlightLeg(t, t + profile.AccelHours, p, Vec2.Zero, dir * a);
            legs.Add(burn);
            var s = burn.End;
            var q = burn.PositionAt(s);
            var vel = burn.VelocityAt(s);
            if (profile.CoastHours > 0)
            {
                var coast = new FlightLeg(s, s + profile.CoastHours, q, vel, Vec2.Zero);
                legs.Add(coast);
                s = coast.End;
                q = coast.PositionAt(s);
            }
            if (profile.BrakeHours > 0) legs.Add(new FlightLeg(s, s + profile.BrakeHours, q, vel, dir * -a));
            dv += profile.DeltaV;
        }
        return Ended(legs, aim, dv, p, v, t);
    }

    private static StretchPlan Ended(List<FlightLeg> legs, Vec2 aim, double dv, Vec2 p, Vec2 v, double t)
    {
        if (legs.Count == 0) return new StretchPlan(legs, aim, dv, p, v, t);
        var last = legs[^1];
        return new StretchPlan(legs, aim, dv, last.PositionAt(last.End), last.VelocityAt(last.End), last.End);
    }

    /// <summary>
    /// Plans a curving change of course for a moving fleet: one burn that turns its velocity straight
    /// at the target (at cruise speed), then coast and, if braking, a brake to arrive at rest. Null
    /// when that does not work out (too close to brake in time, or it never settles), and the fleet
    /// brakes to a stop first instead.
    /// </summary>
    private static Turn? Redirect(Vec2 p, Vec2 v, double now, double a, double cruise, ArrivalStyle arrival, Func<Vec2, double, Vec2> aimFrom) =>
        Redirect(p, v, now, a, cruise, EndSpeed(arrival), aimFrom);

    /// <summary>Rounds of the damped search for the heading out of a turn (fewer are run once it settles).</summary>
    private const int TurnRounds = 200;

    /// <summary>The very same heading, bit for bit (so stopping early never changes a plan).</summary>
    private static bool Same(Vec2 x, Vec2 y) =>
        BitConverter.DoubleToInt64Bits(x.X) == BitConverter.DoubleToInt64Bits(y.X) && BitConverter.DoubleToInt64Bits(x.Y) == BitConverter.DoubleToInt64Bits(y.Y);

    private static Turn? Redirect(Vec2 p, Vec2 v, double now, double a, double cruise, double endSpeed, Func<Vec2, double, Vec2> aimFrom)
    {
        var speed = cruise;
        var aim = aimFrom(p, now);
        for (var outer = 0; outer < 300; outer++)
        {
            // Pick the direction so that, where the turn ends, the fleet points straight at the aim point.
            var dir = (aim - p).Normalized();
            if (dir == Vec2.Zero) return null;
            Vec2 p1, vd;
            double t1;
            var before = dir; // the heading one round back, to spot a settled (or two-way) loop
            for (var i = 0; i < TurnRounds; i++)
            {
                vd = dir * speed;
                t1 = (vd - v).Length / a;
                p1 = p + (v + vd) * (0.5 * t1);
                var next = (aim - p1).Normalized();
                if (next == Vec2.Zero) return null;
                // Damped, so a slow turn out of a fast or opposite heading settles instead of see-sawing.
                var blended = (dir + (next - dir) * 0.5).Normalized();
                var now1 = blended == Vec2.Zero ? next : blended;
                // Each round depends only on the heading, so once a heading repeats every later round
                // repeats too: stop early with the heading the last round would have ended on. This gives
                // exactly the same answer as running every round, many times faster.
                if (Same(now1, dir)) { dir = now1; break; }
                if (Same(now1, before))
                {
                    // Flipping between two headings: the last round lands on whichever matches its parity.
                    if ((TurnRounds - 1 - i) % 2 == 1) now1 = dir;
                    dir = now1;
                    break;
                }
                before = dir;
                dir = now1;
            }
            vd = dir * speed;
            t1 = (vd - v).Length / a;
            p1 = p + (v + vd) * (0.5 * t1);
            var d = Vec2.Distance(aim, p1);
            if (d > 1e-6 && Vec2.Dot((aim - p1).Normalized(), dir) < 0.9999)
            {
                // No heading at this speed ends the turn pointing at the aim: try a slower course.
                speed *= 0.7;
                if (speed < cruise * 0.02) return null;
                continue;
            }
            if (Vec2.Dot(aim - p1, vd) <= 0)
            {
                // The turn at this speed swings past the aim point: turn onto a slower course instead.
                speed *= 0.7;
                if (speed < cruise * 0.02) return null;
                continue;
            }

            var end = Math.Min(endSpeed, speed);
            var brakeDist = (speed * speed - end * end) / (2 * a);
            if (d < brakeDist)
            {
                // Too close to slow down in time at this speed: go slower and try again.
                speed = Math.Sqrt(2 * a * d + end * end) * 0.9;
                if (speed < cruise * 0.02) return null;
                continue;
            }
            var coast = (d - brakeDist) / speed;
            var brake = (speed - end) / a;
            var arrive = now + t1 + coast + brake;
            var newAim = aimFrom(p1, arrive);
            if (Vec2.Distance(newAim, aim) > 1e-3)
            {
                // Damped: a moving target makes plain repeats overshoot back and forth.
                aim = aim + (newAim - aim) * 0.6;
                continue;
            }

            var legs = new List<FlightLeg>();
            var turn = new FlightLeg(now, now + t1, p, v, t1 > 0 ? (vd - v) * (1 / t1) : Vec2.Zero);
            if (t1 > 0) legs.Add(turn);
            var t = now + t1;
            if (coast > 0) { legs.Add(new FlightLeg(t, t + coast, p1, vd, Vec2.Zero)); t += coast; }
            if (brake > 0) legs.Add(new FlightLeg(t, t + brake, p1 + vd * coast, vd, dir * -a));
            if (legs.Count == 0) return null;
            return new Turn(legs, aim, speed, (vd - v).Length + (speed - end));
        }
        return null;
    }

    /// <summary>How far from the enemy a chase settles (null: keep the gap there is now).</summary>
    public static double? ChaseDistance(ChaseMode mode)
    {
        var c = GameData.Combat;
        return mode switch
        {
            ChaseMode.GunRange => c.EngageRangeMm * 0.75,
            ChaseMode.MissileRange => c.Missile.LaunchRangeMm * 0.8,
            ChaseMode.ShadowGuns => c.Railgun.RangeMm * 1.25,
            ChaseMode.ShadowMissiles => c.Missile.LaunchRangeMm * 1.1,
            ChaseMode.Overtake => OvertakePassMm,
            ChaseMode.Join => JoinDistanceMm,
            _ => null,
        };
    }

    /// <summary>
    /// How wide of the enemy an overtake passes: off to one side, never through them (ships at these
    /// speeds would not survive a collision), but still inside railgun range so the guns get a go.
    /// </summary>
    public static double OvertakePassMm => GameData.Combat.Railgun.RangeMm * 0.5;

    /// <summary>How close a fleet flying to join a moving fleet comes alongside before the two merge.</summary>
    public const double JoinDistanceMm = 0.5;

    /// <summary>A speed for messages, in km/s like the screens show.</summary>
    public static string Kms(double mmPerHour) => $"{mmPerHour / 3.6:#,0} km/s";

    public static string ChaseName(ChaseMode mode) => mode switch
    {
        ChaseMode.MatchSpeed => "match speed",
        ChaseMode.GunRange => "close to gun range",
        ChaseMode.MissileRange => "close to missile range",
        ChaseMode.ShadowGuns => "shadow outside their guns",
        ChaseMode.ShadowMissiles => "shadow outside their missiles",
        ChaseMode.Overtake => "overtake",
        ChaseMode.Join => "catch up and join",
        _ => "attack",
    };

    /// <summary>
    /// Plans a chase. It is worked out as if the enemy stood still (relative to it, on its last seen
    /// track), then turned back into a real flight: the fleet ends at the chosen distance moving with
    /// the enemy, or (overtake) passes beside it, OvertakePassMm off, at the chosen extra speed. Fuel is the same either way.
    /// Fails, saying why, when the fleet is too slow or short of fuel.
    /// </summary>
    public static FlightPlan PlanChase(World w, Fleet fleet, Fleet enemy, ChaseMode mode, double speedFraction, FleetPerformance perf, double? standoffMm = null)
    {
        using var timed = SectionTimer.Time("route-planner");
        var plan = PlanChaseDirect(w, fleet, enemy, mode, speedFraction, perf, standoffMm);
        if (plan.Flight == null || Avoidance.FirstHit(w, plan.Flight) is not BodyHit hit) return plan;
        // The chase line runs into a body: first fly past it, then the chase is aimed again from there.
        var beside = hit.BodyAt + hit.Side * Fleets.ClearanceFor(hit.Body);
        var around = PlanStraight(w, fleet, new FlightRequest(null, beside, speedFraction, ArrivalStyle.FastPass, HoldStyle.Drift, w.Tick));
        if (around.Flight == null || Avoidance.FirstHit(w, around.Flight) != null) return plan;
        around.Flight.TargetFleetId = enemy.Id;
        around.Flight.Chase = mode;
        around.Flight.Detour = true;
        return around;
    }

    private static FlightPlan PlanChaseDirect(World w, Fleet fleet, Fleet enemy, ChaseMode mode, double speedFraction, FleetPerformance perf, double? standoffMm = null)
    {
        var now = w.Hour;
        var side = fleet.Side;
        var sighting = Sensors.Find(w, side, enemy.Id);
        var at = Sensors.Predict(w, side, enemy.Id, now);
        if (at == null) return FlightPlan.Fail("That fleet has not been seen", perf.DeltaVLeft);
        var vt = sighting?.Velocity ?? enemy.Velocity;
        var theirSpeed = vt.Length;
        var fraction = Math.Clamp(speedFraction, MinSpeedFraction, 1.0);
        var top = perf.MaxSpeed * fraction;
        string Slow()
        {
            var slowest = w.ShipsOf(fleet).OrderBy(Fleets.ShipSpeed).FirstOrDefault();
            var fastest = w.ShipsOf(fleet).Select(Fleets.ShipSpeed).DefaultIfEmpty(0).Max();
            var why = $"{enemy.Name} is moving at {Kms(theirSpeed)}; this fleet goes {Kms(top)} at this speed setting";
            if (fraction < 1 && perf.MaxSpeed > theirSpeed * 1.05) return why + ": try a faster speed";
            if (slowest != null && fastest > theirSpeed * 1.05)
                return why + $" because its slowest ship, {slowest.Name}, tops out at {Kms(Fleets.ShipSpeed(slowest))}: split off the faster ships to chase";
            return why + ", and none of its ships is fast enough";
        }

        var r = fleet.Position - at.Value;
        var u = fleet.Velocity - vt;
        var standoff = standoffMm ?? ChaseDistance(mode) ?? r.Length;
        var endSpeed = mode == ChaseMode.Overtake ? double.PositiveInfinity : 0;
        // Keep the fleet at least slightly faster than the enemy when it has to catch up.
        if (mode != ChaseMode.MatchSpeed && top <= theirSpeed * 1.02 && Vec2.Dot(vt, r) > 0 && r.Length > standoff * 1.05)
            return FlightPlan.Fail($"Too slow to catch up: {Slow()}", perf.DeltaVLeft);
        if (theirSpeed > top * 1.001 && mode != ChaseMode.Overtake)
            return FlightPlan.Fail($"Cannot keep pace: {Slow()}", perf.DeltaVLeft);
        var away = r.Normalized();
        if (away == Vec2.Zero) away = new Vec2(1, 0);
        // Overtake aims beside them, on the side the fleet is already drifting towards.
        var beside = new Vec2(-away.Y, away.X);
        if (Vec2.Dot(beside, u) < 0) beside = beside * -1;
        var aim = mode == ChaseMode.Overtake ? beside * standoff : away * standoff;

        // The speed setting is also how hard it burns: a gentle chase thrusts less (a dimmer flare), not just slower at the top.
        var accel = perf.Accel * Math.Max(0.25, fraction);
        // Try the fastest closing speed that keeps the real speed within the fleet's limit.
        var closing = top + theirSpeed;
        for (var attempt = 0; attempt < 24; attempt++, closing *= 0.8)
        {
            if (closing < perf.MaxSpeed * 0.01) break;
            StretchPlan? stretch;
            if (mode == ChaseMode.MatchSpeed || r.Length < 1e-6 || Vec2.Distance(r, aim) < 0.05)
            {
                // Already where it wants to be: just match the enemy's velocity.
                var dvMatch = u.Length;
                var t1 = dvMatch / perf.Accel;
                var legsMatch = new List<FlightLeg>();
                if (t1 > 1e-9) legsMatch.Add(new FlightLeg(now, now + t1, r, u, u.Normalized() * -perf.Accel));
                stretch = Ended(legsMatch, r + u * (t1 / 2), dvMatch, r, u, now);
            }
            else
                stretch = Stretch(r, u, now, accel, closing, endSpeed, (_, _) => aim);
            if (stretch == null) continue;
            var legs = stretch.Legs.Select(l => new FlightLeg(l.Start, l.End, l.P0 + at.Value + vt * (l.Start - now), l.V0 + vt, l.A)).ToList();
            var fastest = legs.Count == 0 ? 0 : legs.Max(l => Math.Max(l.V0.Length, l.VelocityAt(l.End).Length));
            if (fastest > Math.Max(top, theirSpeed + closing * 0.01) * 1.001 && attempt < 23) continue;
            if (legs.Count == 0)
                legs.Add(new FlightLeg(now, now + 0.01, fleet.Position, fleet.Velocity, Vec2.Zero));
            var end = legs[^1];
            var flight = new Flight
            {
                TargetFleetId = enemy.Id,
                Target = end.PositionAt(end.End),
                Route = RouteKind.Direct,
                Arrival = ArrivalStyle.FastPass, // it ends still moving (with the enemy, or past it)
                HoldAfter = HoldStyle.Drift,
                SpeedFraction = fraction,
                CruiseSpeed = Math.Min(top, closing),
                DepartHour = now,
                ArriveHour = end.End,
                DeltaV = stretch.DeltaV,
                Legs = legs,
                Chase = mode,
            };
            if (stretch.DeltaV > perf.DeltaVLeft + 1e-9)
                return new FlightPlan(false,
                    $"Not enough supplies to {ChaseName(mode)} (needs {Kms(stretch.DeltaV)} of speed change, has {Kms(perf.DeltaVLeft)})"
                    + (mode != ChaseMode.MatchSpeed ? ": try a slower speed or Match speed" : ""), flight, stretch.DeltaV, perf.DeltaVLeft);
            return new FlightPlan(true, null, flight, stretch.DeltaV, perf.DeltaVLeft);
        }
        return FlightPlan.Fail($"Cannot {ChaseName(mode)}: {Slow()}", perf.DeltaVLeft);
    }

    /// <summary>
    /// A flight that only brakes a moving fleet to a stop where it is heading. Short of fuel, it brakes as
    /// much as the fuel allows and drifts on slower. Null if it is not moving or has no fuel at all.
    /// </summary>
    public static Flight? BrakeToStop(World w, Fleet fleet)
    {
        var perf = FleetPerformance.Of(w, fleet);
        var v = fleet.Velocity;
        var change = Math.Min(v.Length, perf.DeltaVLeft * 0.999);
        if (v.Length < 1e-9 || perf.Accel <= 0 || change < 1e-6) return null;
        var now = w.Hour;
        var brake = new FlightLeg(now, now + change / perf.Accel, fleet.Position, v, v.Normalized() * -perf.Accel);
        return new Flight
        {
            Target = brake.PositionAt(brake.End),
            Arrival = change >= v.Length - 1e-9 ? ArrivalStyle.Brake : ArrivalStyle.FastPass, // short of fuel: still moving at the end
            HoldAfter = HoldStyle.Drift,
            SpeedFraction = 1,
            DepartHour = now,
            ArriveHour = brake.End,
            DeltaV = change,
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
    public static FlightPlan PlanRoute(World w, Fleet fleet, FlightRequest req) => req.Route switch
    {
        RouteKind.Wait => PlanWait(w, fleet, req),
        _ => Plan(w, fleet, req with { DepartTick = w.Tick }),
    };

    private readonly record struct ProfileResult(double AccelHours, double CoastHours, double Peak, double DeltaV, double Total)
    {
        public double BrakeHours { get; init; } = AccelHours;
    }

    /// <summary>Speed at the end of a stretch: 0 to brake to a stop, infinity to fly through at full speed.</summary>
    private static double EndSpeed(ArrivalStyle arrival) => arrival == ArrivalStyle.Brake ? 0 : double.PositiveInfinity;

    private static ProfileResult Profile(double d, double cruise, double a, ArrivalStyle arrival) => Profile(d, cruise, a, EndSpeed(arrival));

    /// <summary>
    /// Burn, coast, brake timings for a distance, starting from rest and ending at endSpeed (0 = stop,
    /// infinity = no brake). BrakeHours is how long the final brake takes (down to endSpeed).
    /// </summary>
    private static ProfileResult Profile(double d, double cruise, double a, double endSpeed)
    {
        if (d <= 1e-9) return new ProfileResult(0, 0, 0, 0, 0);
        var full = Math.Min(cruise, Math.Sqrt(2 * d * a));
        if (endSpeed >= full)
        {
            // No braking: burn up to speed and coast through.
            var ta = full / a;
            var coast = Math.Max(0, (d - full * full / (2 * a)) / full);
            return new ProfileResult(ta, coast, full, full, ta + coast) { BrakeHours = 0 };
        }
        var peak = Math.Min(cruise, Math.Sqrt((2 * d * a + endSpeed * endSpeed) / 2));
        var up = peak / a;
        var down = (peak - endSpeed) / a;
        var cruiseDist = d - peak * peak / (2 * a) - (peak * peak - endSpeed * endSpeed) / (2 * a);
        var coastHours = Math.Max(0, cruiseDist / peak);
        return new ProfileResult(up, coastHours, peak, peak + (peak - endSpeed), up + coastHours + down) { BrakeHours = down };
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
            var angle = f.OrbitAngle + 2 * Math.PI * (hour - w.Hour) / Fleets.OrbitPeriodHours(body, f.OrbitRadius);
            return PredictBody(w, body, hour) + new Vec2(DetMath.Cos(angle), DetMath.Sin(angle)) * f.OrbitRadius;
        }
        return f.Position + f.Velocity * (hour - w.Hour);
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
