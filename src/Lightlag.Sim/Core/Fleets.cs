namespace Lightlag.Sim.Core;

/// <summary>What a fleet does when it is not flying: circle a body, or sit with engines cold.</summary>
public enum HoldStyle { Orbit, Drift }

/// <summary>How a flight ends: brake to a stop at the target, or fly through it at speed (cheaper, faster, no stop).</summary>
public enum ArrivalStyle { Brake, FastPass }

/// <summary>Standing order: Engage closes to weapon range and fires (breaking off any flight); Hold keeps its course and fires at what comes in range.</summary>
public enum Stance { Engage, Hold }

/// <summary>How a fleet fires its railguns: Focused (aimed slugs) or Spread (a burst in a fan, harder to dodge, then the guns cool).</summary>
public enum FireMode { Focused, Spread }

/// <summary>How a fleet dodges railgun fire: not at all, a cheap sidestep, or a hard burn that clears a fan (costs more fuel).</summary>
public enum DodgeMode { Off, Sidestep, HardBurn }

/// <summary>Direct leaves now; Wait leaves when the trip is shortest (the target swings closer).</summary>
public enum RouteKind { Direct, Wait }

/// <summary>
/// How a fleet chases an enemy fleet. Match speed keeps the gap it has and moves with it; Gun range
/// closes to railgun range and keeps pace; Missile range keeps pace just inside missile launch range;
/// Shadow keeps pace just outside the range of the enemy's railguns or missiles; Overtake flies through
/// it faster than it is moving. Only Gun range hands over to close combat (Engage); the others hold
/// their distance and fire at what is in reach.
/// </summary>
public enum ChaseMode { None, MatchSpeed, GunRange, MissileRange, ShadowGuns, ShadowMissiles, Overtake, Join }

/// <summary>
/// A standing order to chase an enemy fleet. The flight is planned against where the enemy is expected to
/// be (its last seen track), and planned again only when a new burn is seen, so the chase goes on while
/// it is out of sight.
/// </summary>
public sealed class ChaseOrder
{
    public int TargetFleetId { get; set; }
    public ChaseMode Mode { get; set; }
    public double SpeedFraction { get; set; }

    /// <summary>The enemy track the current flight was planned against.</summary>
    public Vec2 TargetAt { get; set; }
    public Vec2 TargetVelocity { get; set; }
    public double PlannedHour { get; set; }

    /// <summary>Why the last attempt to re-aim failed (null when it worked).</summary>
    public string? Problem { get; set; }

    public ChaseOrder Copy() => (ChaseOrder)MemberwiseClone();
}

/// <summary>
/// A group of ships that flies as one, at the pace of its slowest ship. Its ships keep a loose
/// formation around the fleet's position.
/// </summary>
public sealed class Fleet
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Side Side { get; set; }
    public List<int> ShipIds { get; set; } = new();
    public Vec2 Position { get; set; }

    /// <summary>Mm per hour. Zero when holding; not zero after a fast pass (drifting on).</summary>
    public Vec2 Velocity { get; set; }

    public HoldStyle Hold { get; set; } = HoldStyle.Drift;

    /// <summary>Which way the ships point (radians): along their last thrust, so braking ships face backwards.</summary>
    public double Facing { get; set; }

    /// <summary>Main engines fired during the last step (lights the fleet up on enemy sensors).</summary>
    public bool Thrusting { get; set; }

    /// <summary>When the main engines last fired, and whether they are still hot from it (see sensors.warmHours).</summary>
    public double LastBurnHour { get; set; } = -1e9;
    public bool Warm { get; set; }

    /// <summary>When holding in orbit: the body, how far out, and where on the circle.</summary>
    public int? OrbitBodyId { get; set; }
    public double OrbitRadius { get; set; }
    public double OrbitAngle { get; set; }

    /// <summary>The flight under way or waiting to leave, if any.</summary>
    public Flight? Flight { get; set; }

    /// <summary>What the fleet does when enemies come into contact.</summary>
    public Stance Stance { get; set; } = Stance.Engage;

    /// <summary>Dodge railgun fire (costs fuel per slug; stops when supplies run low).</summary>
    public DodgeMode Dodge { get; set; } = DodgeMode.Sidestep;

    /// <summary>How its railguns fire.</summary>
    public FireMode Fire { get; set; } = FireMode.Focused;

    /// <summary>How it was last seen firing its railguns, and when (what enemies can tell about its fire).</summary>
    public FireMode? LastVolley { get; set; }
    public double LastVolleyHour { get; set; } = -1e9;

    /// <summary>Break off and fly home when the fleet's armour falls below half.</summary>
    public bool RetreatWhenDamaged { get; set; } = true;
    public bool Retreating { get; set; }

    /// <summary>Closing in on an enemy, or braking after a fight (moved by velocity, outside any flight plan).</summary>
    public bool Maneuvering { get; set; }

    /// <summary>Join this fleet once both are stopped together (after "fast ships go ahead" or "gather").</summary>
    public int? JoinFleetId { get; set; }

    /// <summary>Chasing an enemy fleet: re-aimed whenever it is seen to change course.</summary>
    public ChaseOrder? Chase { get; set; }

    public bool InFlight => Flight != null;
}

/// <summary>
/// A planned flight: a chain of constant-thrust pieces (burn, coast, brake). The fleet's position at
/// any hour comes straight from these, so flights replay exactly.
/// </summary>
public sealed class Flight
{
    public int? TargetBodyId { get; set; }
    public int? TargetFleetId { get; set; }
    public Vec2 Target { get; set; }
    public RouteKind Route { get; set; }
    public ArrivalStyle Arrival { get; set; }
    public HoldStyle HoldAfter { get; set; }
    public double SpeedFraction { get; set; }
    public double CruiseSpeed { get; set; }

    /// <summary>A chase flight (ends moving with the enemy, or past it).</summary>
    public ChaseMode Chase { get; set; }

    /// <summary>Game hour the main burn starts. Before that the fleet keeps its hold.</summary>
    public double DepartHour { get; set; }
    public double ArriveHour { get; set; }

    /// <summary>Total change of speed the flight needs (Mm/h): what costs fuel.</summary>
    public double DeltaV { get; set; }
    public bool Started { get; set; }
    public List<FlightLeg> Legs { get; set; } = new();

    /// <summary>Points the route was asked to pass through (for drawing).</summary>
    public List<Vec2> Waypoints { get; set; } = new();

    /// <summary>The waypoints with their settings, so the route can be opened and changed in flight.</summary>
    public List<Waypoint> WaypointPlan { get; set; } = new();

    /// <summary>Game hour each waypoint is reached (same order as WaypointPlan).</summary>
    public List<double> WaypointHours { get; set; } = new();

    /// <summary>The waypoints not reached yet at this hour.</summary>
    public IEnumerable<Waypoint> WaypointsAhead(double hour) =>
        WaypointPlan.Where((_, i) => i >= WaypointHours.Count || WaypointHours[i] > hour);

    public double EndHour => Legs.Count > 0 ? Legs[^1].End : ArriveHour;

    public Flight Copy()
    {
        var f = (Flight)MemberwiseClone();
        f.Legs = new List<FlightLeg>(Legs);
        f.Waypoints = new List<Vec2>(Waypoints);
        f.WaypointPlan = new List<Waypoint>(WaypointPlan);
        f.WaypointHours = new List<double>(WaypointHours);
        return f;
    }
}

/// <summary>A stretch of flight with constant thrust: position = P0 + V0 t + A t^2 / 2.</summary>
public sealed record FlightLeg(double Start, double End, Vec2 P0, Vec2 V0, Vec2 A)
{
    public Vec2 PositionAt(double hour)
    {
        var t = Math.Clamp(hour, Start, End) - Start;
        return P0 + V0 * t + A * (0.5 * t * t);
    }

    public Vec2 VelocityAt(double hour)
    {
        var t = Math.Clamp(hour, Start, End) - Start;
        return V0 + A * t;
    }

    public bool IsBurn => A.X != 0 || A.Y != 0;

    public FlightLeg Shifted(Vec2 by) => this with { P0 = P0 + by };
}

/// <summary>What a fleet can do: the slowest ship sets the pace, the thirstiest ship sets the range.</summary>
public readonly record struct FleetPerformance(double MaxSpeed, double Accel, double DeltaVLeft)
{
    public static FleetPerformance Of(World w, Fleet f)
    {
        double speed = double.MaxValue, accel = double.MaxValue, dv = double.MaxValue;
        foreach (var s in w.ShipsOf(f))
        {
            // A ship with its engines knocked out limps at half speed and half thrust.
            var engines = s.Disabled == Module.Engines ? 0.5 : 1.0;
            speed = Math.Min(speed, s.MaxSpeed * engines);
            accel = Math.Min(accel, s.Accel * engines);
            dv = Math.Min(dv, s.Supplies / Tech.SuppliesPerDeltaV(w, s));
        }
        if (speed == double.MaxValue) return new FleetPerformance(0, 0, 0);
        return new FleetPerformance(speed, accel, dv);
    }
}

/// <summary>Fleet bookkeeping shared by commands and the scenario builder.</summary>
public static class Fleets
{
    /// <summary>Distance between ships in the loose formation.</summary>
    public const double FormationSpacing = 2; // Mm (2,000 km), well inside railgun range

    /// <summary>How close two fleets must be to merge.</summary>
    public const double MergeDistance = 3000; // Mm

    /// <summary>Orbits take at least this long, so a circling fleet moves smoothly at hour steps.</summary>
    public const double MinOrbitHours = 72;

    /// <summary>How far a split-off fleet moves away, so the two are not drawn on top of each other.</summary>
    public const double SplitGap = 300; // Mm, well inside merge distance
    /// <summary>The same with enemies close by, so a split never steps out of a fight.</summary>
    public const double CloseSplitGap = 10; // Mm
    /// <summary>In orbit the split-off fleet moves ahead along the circle, by this much at most.</summary>
    public const double MaxSplitAngle = Math.PI / 4; // 45 degrees

    public static Fleet Create(World w, Side side, IEnumerable<Ship> ships, Vec2 position)
    {
        var number = ++w.FleetNumbers[(int)side];
        var fleet = new Fleet
        {
            Id = w.NextId++,
            Name = side == Side.Player ? $"Fleet {number}" : $"Rival fleet {number}",
            Side = side,
            Position = position,
        };
        foreach (var s in ships)
        {
            fleet.ShipIds.Add(s.Id);
            s.FleetId = fleet.Id;
        }
        w.Fleets.Add(fleet);
        Arrange(w, fleet);
        return fleet;
    }

    /// <summary>Lays the ships out: first in the middle, then rings of 6, 12, 18...</summary>
    public static void Arrange(World w, Fleet f)
    {
        var slot = 0;
        foreach (var s in w.ShipsOf(f))
        {
            s.FormationOffset = SlotOffset(slot++);
            s.Position = f.Position + s.FormationOffset;
        }
    }

    public static Vec2 SlotOffset(int slot)
    {
        if (slot == 0) return Vec2.Zero;
        var ring = 1;
        var first = 1;
        while (slot >= first + ring * 6) { first += ring * 6; ring++; }
        var angle = (slot - first) * 2 * Math.PI / (ring * 6);
        return new Vec2(DetMath.Cos(angle), DetMath.Sin(angle)) * (ring * FormationSpacing);
    }

    /// <summary>The usual orbit height round a body: three body radii, but at least a 3-day orbit.</summary>
    public static double OrbitRadiusFor(Body b)
    {
        var minByPeriod = DetMath.Cbrt(b.Gm * (MinOrbitHours / (2 * Math.PI)) * (MinOrbitHours / (2 * Math.PI)));
        return Math.Max(b.Radius * 3, minByPeriod);
    }

    public static double OrbitPeriodHours(Body b, double radius) =>
        Math.Max(MinOrbitHours, Physics.OrbitPeriodHours(radius, b.Gm));

    /// <summary>
    /// The body a fleet at this point could orbit: the closest one whose neighbourhood it is in
    /// (a planet's moon system or 3,000,000 km; a small zone round a moon).
    /// </summary>
    public static Body? OrbitableNear(World w, Vec2 at)
    {
        Body? best = null;
        var bestDist = double.MaxValue;
        foreach (var b in w.Bodies)
        {
            if (b.Kind == BodyKind.Star) continue;
            var reach = Reach(w, b);
            var d = Vec2.Distance(at, b.Position);
            if (d <= reach && d < bestDist) { best = b; bestDist = d; }
        }
        return best;
    }

    public static double Reach(World w, Body b)
    {
        var reach = b.Kind == BodyKind.Moon ? Math.Max(b.Radius * 20, b.OrbitRadius * 0.1) : 3000.0;
        foreach (var m in w.Bodies)
            if (m.ParentId == b.Id) reach = Math.Max(reach, m.OrbitRadius * 1.2);
        return Math.Max(reach, OrbitRadiusFor(b) * 1.5);
    }

    /// <summary>A ship's own top speed (half with its engines knocked out).</summary>
    public static double ShipSpeed(Ship s) => s.MaxSpeed * (s.Disabled == Module.Engines ? 0.5 : 1.0);

    /// <summary>Why two fleets cannot merge, or null if they can.</summary>
    public static string? CanMerge(Fleet a, Fleet b)
    {
        if (a.Id == b.Id) return "it is the same fleet";
        if (a.Side != b.Side) return "they are on different sides";
        if ((a.InFlight && a.Flight!.Started) || (b.InFlight && b.Flight!.Started)) return "both must be stopped";
        if (a.Velocity.Length > 1e-9 || b.Velocity.Length > 1e-9) return "both must be stopped";
        if (Vec2.Distance(a.Position, b.Position) > MergeDistance) return "they are too far apart";
        return null;
    }

    /// <summary>
    /// Can this fleet join that one now? Same side, both stopped, and close together, or both circling
    /// the same body.
    /// </summary>
    public const string DriftingProblem = "the fleet to join is drifting on; stop it first";

    /// <summary>Where to meet a fleet: where it is flying to, the body it circles, or the point where it waits.</summary>
    public static FlightRequest? Meeting(World world, Fleet into, double speedFraction)
    {
        if (into.Flight is Flight f && f.TargetFleetId == null && f.Arrival == ArrivalStyle.Brake)
            return new FlightRequest(f.TargetBodyId, f.Target, speedFraction, ArrivalStyle.Brake, f.HoldAfter, world.Tick);
        if (into.Flight != null || into.Velocity.Length > 1e-9) return null;
        if (into.Hold == HoldStyle.Orbit && into.OrbitBodyId is int body && world.FindBody(body) is Body b)
            return new FlightRequest(b.Id, b.Position, speedFraction, ArrivalStyle.Brake, HoldStyle.Orbit, world.Tick);
        return new FlightRequest(null, into.Position, speedFraction, ArrivalStyle.Brake, HoldStyle.Drift, world.Tick);
    }

    /// <summary>Plan a flight for a fleet to where another fleet will be stopped. Returns why not, or null when on its way.</summary>
    public static string? FlyToJoin(World world, Fleet f, Fleet into, double speedFraction)
    {
        if (Meeting(world, into, speedFraction) is not FlightRequest request)
        {
            // It is drifting on: chase it on intercept tracks, matching its speed, and join on arrival.
            var perf = FleetPerformance.Of(world, f);
            var chase = FlightPlanner.PlanChase(world, f, into, ChaseMode.Join, speedFraction, perf);
            if (!chase.Ok || chase.Flight == null) return chase.Problem ?? "no intercept";
            f.Flight = chase.Flight;
            f.Chase = Chases.Order(world, f, into.Id, ChaseMode.Join, speedFraction);
            return null;
        }
        var plan = FlightPlanner.Plan(world, f, request);
        if (!plan.Ok || plan.Flight == null) return plan.Problem ?? "no route";
        f.Flight = plan.Flight;
        f.Chase = null;
        return null;
    }

    public static bool CanJoin(Fleet f, Fleet into)
    {
        if (f.Id == into.Id || f.Side != into.Side) return false;
        if (Moving(f) || Moving(into)) return false;
        if (f.Hold == HoldStyle.Orbit && into.Hold == HoldStyle.Orbit && f.OrbitBodyId != null && f.OrbitBodyId == into.OrbitBodyId) return true;
        return Vec2.Distance(f.Position, into.Position) <= MergeDistance;
    }

    private static bool Moving(Fleet f) => (f.InFlight && f.Flight!.Started) || f.Velocity.Length > 1e-9;

    /// <summary>Moves all of the other fleet's ships into this one and removes the other fleet.</summary>
    public static void Absorb(World w, Fleet into, Fleet other)
    {
        foreach (var s in w.ShipsOf(other).ToList())
        {
            into.ShipIds.Add(s.Id);
            s.FleetId = into.Id;
        }
        other.ShipIds.Clear();
        w.Fleets.Remove(other);
        foreach (var f in w.Fleets)
            if (f.JoinFleetId == other.Id) f.JoinFleetId = f.Id == into.Id ? null : into.Id;
        Arrange(w, into);
    }

    /// <summary>
    /// Moves a just-split fleet a little away from where it split off: ahead along its orbit, or
    /// sideways to its course. Only a short way when enemies are near.
    /// </summary>
    public static void SetApart(World w, Fleet f)
    {
        var nearEnemy = w.Fleets.Any(o => o.Side != f.Side && Vec2.Distance(o.Position, f.Position) <= GameData.Combat.ContactRangeMm * 2);
        var gap = nearEnemy ? CloseSplitGap : SplitGap;
        if (f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int id && w.FindBody(id) is Body body && f.OrbitRadius > 0)
        {
            // A waiting flight still leaves from where the fleet it split from would have been.
            f.OrbitAngle += Math.Min(MaxSplitAngle, gap / f.OrbitRadius);
            if (f.OrbitAngle >= Math.PI * 2) f.OrbitAngle -= Math.PI * 2;
            f.Position = FlightPlanner.PredictBody(w, body, w.Hour) + new Vec2(DetMath.Cos(f.OrbitAngle), DetMath.Sin(f.OrbitAngle)) * f.OrbitRadius;
        }
        else
        {
            var speed = f.Velocity.Length;
            var side = speed > 1e-9 ? new Vec2(-f.Velocity.Y / speed, f.Velocity.X / speed) : new Vec2(0, -1);
            var by = side * gap;
            f.Position += by;
            if (f.Flight is Flight flight)
            {
                flight.Legs = flight.Legs.Select(l => l.Shifted(by)).ToList();
                if (flight.TargetBodyId == null && flight.TargetFleetId == null) flight.Target += by;
            }
        }
        Arrange(w, f);
    }

    /// <summary>Starts circling a body from where the fleet is now.</summary>
    public static void EnterOrbit(Fleet f, Body body)
    {
        var offset = f.Position - body.Position;
        f.Hold = HoldStyle.Orbit;
        f.OrbitBodyId = body.Id;
        f.OrbitRadius = Math.Max(offset.Length, body.Radius * 1.5);
        f.OrbitAngle = DetMath.Atan2(offset.Y, offset.X);
        f.Velocity = Vec2.Zero;
    }

    public static void EnterDrift(Fleet f)
    {
        f.Hold = HoldStyle.Drift;
        f.OrbitBodyId = null;
        f.OrbitRadius = 0;
        f.OrbitAngle = 0;
    }
}
