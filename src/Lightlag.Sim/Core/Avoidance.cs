namespace Lightlag.Sim.Core;

/// <summary>Where a planned flight would run into a planet, moon or the star.</summary>
public sealed record BodyHit(Body Body, double Hour, Vec2 BodyAt, Vec2 Side);

/// <summary>
/// Keeps flights out of planets, moons and the star. The planner flies straight lines; when one passes
/// through a body, a turning point is added beside the body (at its orbit height) and the flight is
/// planned again. Bodies move along their orbits, so the check follows both the fleet and the body.
/// </summary>
public static class Avoidance
{
    private const int SamplesPerLeg = 160;
    private const int MaxDetours = 4;

    /// <summary>The first moment this flight is inside a body, or null if it stays clear.</summary>
    public static BodyHit? FirstHit(World w, Flight f)
    {
        if (f.Legs.Count == 0) return null;
        // Only bodies whose distance from the star overlaps the flight's can be in the way.
        var star = w.Bodies.FirstOrDefault(b => b.ParentId == null);
        var centre = star?.Position ?? Vec2.Zero;
        double lo = double.MaxValue, hi = 0, gap = 0;
        foreach (var leg in f.Legs)
        {
            var last = leg.PositionAt(leg.Start);
            for (var i = 0; i <= 8; i++)
            {
                var p = leg.PositionAt(leg.Start + (leg.End - leg.Start) * i / 8);
                var r = Vec2.Distance(p, centre);
                lo = Math.Min(lo, r);
                hi = Math.Max(hi, r);
                gap = Math.Max(gap, Vec2.Distance(p, last)); // a straight piece can dip this much closer
                last = p;
            }
        }
        lo -= gap;
        hi += gap;
        var candidates = new List<Body>();
        foreach (var b in w.Bodies)
        {
            if (b.Radius <= 0) continue;
            // How far from the star the body can be: its planet's orbit, give or take moon orbits.
            double reach = b.Radius;
            var top = b;
            while (top.ParentId is int pid && w.FindBody(pid) is Body parent && parent.ParentId != null)
            {
                reach += top.OrbitRadius;
                top = parent;
            }
            var from = top.ParentId != null ? top.OrbitRadius : 0;
            if (from + reach < lo || from - reach > hi) continue;
            candidates.Add(b);
        }
        if (candidates.Count == 0) return null;

        BodyHit? first = null;
        foreach (var b in candidates)
        {
            foreach (var leg in f.Legs)
            {
                if (first != null && leg.Start >= first.Hour) break;
                var dt = (leg.End - leg.Start) / SamplesPerLeg;
                if (dt <= 0) continue;
                var t0 = leg.Start;
                var r0 = leg.PositionAt(t0) - FlightPlanner.PredictBody(w, b, t0);
                for (var i = 1; i <= SamplesPerLeg; i++)
                {
                    var t1 = leg.Start + dt * i;
                    var c1 = FlightPlanner.PredictBody(w, b, t1);
                    var r1 = leg.PositionAt(t1) - c1;
                    // Closest point of the straight move from r0 to r1, relative to the body.
                    var d = r1 - r0;
                    var len2 = Vec2.Dot(d, d);
                    var s = len2 > 1e-18 ? Math.Clamp(-Vec2.Dot(r0, d) / len2, 0, 1) : 0;
                    var closest = r0 + d * s;
                    if (closest.Length < b.Radius && !f.Launching(w, b, t0 + dt * s))
                    {
                        var hour = t0 + dt * s;
                        if (first == null || hour < first.Hour)
                        {
                            // Pass on the side the line already leans to (square to the line of travel).
                            var across = len2 > 1e-18 ? closest - d * (Vec2.Dot(closest, d) / len2) : closest;
                            var side = across.Length > b.Radius * 1e-3 ? across.Normalized() : new Vec2(-d.Y, d.X).Normalized();
                            if (side == Vec2.Zero) side = new Vec2(1, 0);
                            first = new BodyHit(b, hour, FlightPlanner.PredictBody(w, b, hour), side);
                        }
                        break;
                    }
                    r0 = r1;
                    t0 = t1;
                }
            }
        }
        return first;
    }

    /// <summary>
    /// Plans again with turning points beside any body the flight would hit. Chases are left alone: they
    /// are re-aimed often. Returns the original plan if no clear route is found.
    /// </summary>
    public static FlightPlan Steer(World w, Fleet fleet, FlightRequest req, FlightPlan plan, Func<FlightRequest, FlightPlan> replan)
    {
        if (plan.Flight == null || req.Chase != ChaseMode.None) return plan;
        var best = plan;
        for (var tries = 0; tries < MaxDetours; tries++)
        {
            var hit = FirstHit(w, best.Flight!);
            if (hit == null) return best;
            var clearance = Fleets.OrbitRadiusFor(hit.Body) * (1 + 0.5 * tries);
            var detour = new Waypoint(hit.BodyAt + hit.Side * clearance, Auto: true);
            var points = new List<Waypoint>(req.Waypoints ?? new List<Waypoint>());
            var hours = best.Flight!.WaypointHours;
            var at = 0;
            while (at < points.Count && at < hours.Count && hours[at] <= hit.Hour) at++;
            points.Insert(at, detour);
            req = req with { Waypoints = points };
            var next = replan(req);
            if (next.Flight == null) return best;
            best = next;
        }
        return FirstHit(w, best.Flight!) == null ? best : plan;
    }
}
