namespace Lightlag.Sim.Core;

/// <summary>What a fleet does when it is not flying: circle a body, or sit with engines cold.</summary>
public enum HoldStyle { Orbit, Drift }

/// <summary>How a flight ends: brake to a stop at the target, or fly through it at speed (cheaper, faster, no stop).</summary>
public enum ArrivalStyle { Brake, FastPass }

/// <summary>
/// Standing order for contact. Engage closes to weapon range and fires (breaking off any flight); Hold
/// keeps its course and fires at what comes in range; EngageIfStronger engages only when the fleet
/// outguns what it meets (else holds course); HoldPosition stops where it is and fires without
/// chasing; Avoid flies home as soon as an enemy is seen coming for it; ReturnHome breaks off and
/// flies home on contact.
/// </summary>
public enum Stance { Engage, Hold, EngageIfStronger, HoldPosition, Avoid, ReturnHome }

/// <summary>
/// A fleet's standing mission, carried out without further orders: Hold a body; Patrol a planet and its
/// moons; Raid an enemy-held body; Escort one of its own fleets; Scout the planets in turn.
/// </summary>
public enum Mission { None, Hold, Patrol, Raid, Escort, Scout, Ambush, Hunt }

/// <summary>
/// How much a fleet decides for itself when command is far away. Strict: only its standing orders
/// (reflexes). Sensible: also runs from a stronger enemy when an order could not arrive in time.
/// Free: whenever cut off, also runs from danger and goes after weaker enemies near it.
/// </summary>
public enum AutonomyLevel { Strict, Sensible, Free }

/// <summary>How a fleet fires its railguns: Focused (aimed slugs) or Spread (a burst in a fan, harder to dodge, then the guns cool).</summary>
public enum FireMode { Focused, Spread }

/// <summary>How a fleet dodges railgun fire: not at all, a cheap sidestep, or a hard burn that clears a fan (costs more fuel).</summary>
public enum DodgeMode { Off, Sidestep, HardBurn }

/// <summary>Direct leaves now; Wait leaves when the trip is shortest (the target swings closer).</summary>
/// <summary>Direct: leave now. Wait: leave when the trip is shortest. Slingshot: one flyby of a planet on the way (see Slingshots).</summary>
public enum RouteKind { Direct, Wait, Slingshot }

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

    /// <summary>When the chase began (0: not yet noted); a chase that has not caught its quarry in Chases.GiveUpHours is dropped.</summary>
    public double StartHour { get; set; }

    /// <summary>The current flight goes round a body first: aim the chase again once it ends.</summary>
    public bool Detouring { get; set; }

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
    /// <summary>Old standing order, kept for saves and the rival's stance orders; pull-back rules (Rule) decide now.</summary>
    public bool RetreatWhenDamaged { get; set; } = true;

    /// <summary>When the fleet pulls back (see BattleRules). Set by hand it is pinned: automation leaves it.</summary>
    public PullRule Rule { get; set; } = PullRule.For(PullPreset.Standard);
    public bool RuleByHand { get; set; }

    /// <summary>"Hold on": ignore the pull-back rule until this fight is over.</summary>
    public bool HoldOn { get; set; }

    /// <summary>The fight in hand: strength and ships when it started (0: not fighting), ships lost since.</summary>
    public double FightPower { get; set; }
    public int FightShips { get; set; }
    public int FightLost { get; set; }
    public double FightPulledPower { get; set; }
    public double FightLastHour { get; set; }

    /// <summary>Why it last pulled back (for the fleet panel and news).</summary>
    public string? PullReason { get; set; }

    /// <summary>Sent for refit (see Refits); null when not.</summary>
    public RefitPlan? Refit { get; set; }

    /// <summary>A ship (or group) that pulled out: rejoin this fleet once mended.</summary>
    public int? RejoinFleetId { get; set; }

    /// <summary>The zone this fleet holds (Ambush or Hunt mission), if any.</summary>
    public int? ZoneId { get; set; }
    public bool Retreating { get; set; }

    /// <summary>Closing in on an enemy, or braking after a fight (moved by velocity, outside any flight plan).</summary>
    public bool Maneuvering { get; set; }

    /// <summary>Join this fleet once both are stopped together (after "fast ships go ahead" or "gather").</summary>
    public int? JoinFleetId { get; set; }

    /// <summary>Chasing an enemy fleet: re-aimed whenever it is seen to change course.</summary>
    public ChaseOrder? Chase { get; set; }

    /// <summary>Its mission, the body or fleet it is about, how far through it is, and when it moves on.</summary>
    public Mission Mission { get; set; }
    public int? MissionBodyId { get; set; }
    public int? MissionFleetId { get; set; }
    public int MissionLeg { get; set; }
    public double MissionUntil { get; set; }

    /// <summary>How it guards its protected ships, and what its weapons go for.</summary>
    public CoverStyle Cover { get; set; }
    public FireAt FireAt { get; set; }

    /// <summary>The shape it keeps (Auto: picked from loadout and threat), and the shape, front and ship count it was last laid out for.</summary>
    public Formation Formation { get; set; }
    public Formation LaidShape { get; set; }
    public double LaidFront { get; set; }
    public int LaidCount { get; set; }

    /// <summary>Heat: where its radiators are, and whether ships throw heat sinks on their own (running hot, missiles closing).</summary>
    public RadiatorMode Radiators { get; set; }
    public bool AutoSinks { get; set; } = true;

    /// <summary>Railguns fire beyond their normal reach (forces the enemy to dodge, rarely hits), and when missiles launch.</summary>
    public bool LongShots { get; set; }
    public MissileLaunch MissileLaunch { get; set; }

    /// <summary>Supplies: the fuel (speed change) a slow coast to the nearest planet its side holds would take, and whether it is on its way there to refill (see Supply).</summary>
    public double ReserveDeltaV { get; set; }
    public bool Resupplying { get; set; }

    /// <summary>Supply fleets on an Auto supply run: the fleet they keep topped up (see SupplyLines).</summary>
    /// <summary>The supply run was given by automation (so automation may change it; a hand-given one is left alone).</summary>
    public bool AutoRun { get; set; }

    public int? SupplyRunFor { get; set; }

    /// <summary>Colony ships: carrying people from one planet to another (see Planets).</summary>
    public PeopleRun? PeopleRun { get; set; }

    /// <summary>High orbit is out of reach of planet guns, but only low orbit can bomb or land troops.</summary>
    public OrbitHeight OrbitHeight { get; set; }

    /// <summary>What the fleet bombs when it holds low orbit over an enemy planet (None: it only blockades).</summary>
    public BombTarget Bombing { get; set; }

    /// <summary>The body whose orbit it broke to fight, until it orbits something again (claims and bombardment need orbit).</summary>
    public int? LeftOrbitBodyId { get; set; }

    /// <summary>How much it decides for itself when cut off from command.</summary>
    public AutonomyLevel Autonomy { get; set; } = AutonomyLevel.Sensible;

    /// <summary>When the last direct order from command reached it (its own choices wait a while after one).</summary>
    public double LastOrderHour { get; set; } = -1e9;

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

    /// <summary>The player's own waypoints not reached yet (leaves out the planner's turns round planets).</summary>
    public IEnumerable<Waypoint> ChosenWaypointsAhead(double hour) => WaypointsAhead(hour).Where(p => !p.Auto);

    /// <summary>
    /// The body the fleet was orbiting when it left. Flights leave orbit and arrive at one from rest
    /// (not moving with the planet), so in the first and last LaunchHours the planet itself can sweep
    /// past the fleet; that is not counted as flying into it.
    /// </summary>
    public int? FromBodyId { get; set; }

    /// <summary>A slingshot flight: the planet it swings past, when, and where (see Slingshots).</summary>
    public int? FlybyBodyId { get; set; }
    public double FlybyHour { get; set; }
    public Vec2 FlybyPoint { get; set; }
    /// <summary>Speed gained from the flyby for free (no fuel), Mm per hour.</summary>
    public double FlybyGain { get; set; }

    /// <summary>Speed change burned at the closest pass of the slingshot (charged as it passes).</summary>
    public double FlybyBurn { get; set; }

    /// <summary>A chase flight that first goes round a body in the way; the chase is aimed again when it ends.</summary>
    public bool Detour { get; set; }
    public const double LaunchHours = 3;

    public bool Launching(World w, Body b, double hour)
    {
        var system = PlanetOf(w, b.Id);
        return (FromBodyId is int from && PlanetOf(w, from) == system && hour < DepartHour + LaunchHours)
            || (TargetBodyId is int to && PlanetOf(w, to) == system && hour > EndHour - LaunchHours);
    }

    /// <summary>The planet a body belongs to (itself, or a moon's planet); the star is its own.</summary>
    private static int PlanetOf(World w, int bodyId)
    {
        var b = w.FindBody(bodyId);
        while (b?.ParentId is int pid && w.FindBody(pid) is Body parent && parent.ParentId != null) b = parent;
        return b?.Id ?? bodyId;
    }

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
            // Engines damage slows a ship; knocked out it limps at half speed, crippled at a quarter.
            var engines = Wounds.SpeedFactor(s);
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
    public const double MinOrbitHours = 12;
    public const double LowOrbitRadii = 1.5;

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
            Autonomy = w.Sides[(int)side].DefaultAutonomy,
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

    /// <summary>Lays the ships out in the fleet's formation (see Formations).</summary>
    public static void Arrange(World w, Fleet f)
    {
        Formations.Layout(w, f, Formations.Shape(w, f), Formations.Front(w, f));
        foreach (var s in w.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
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

    /// <summary>
    /// The usual orbit round a body: low (half a body radius up), and never higher than half railgun range
    /// above the surface, so a fleet in orbit can always fire on the ground; but at least a 12-hour orbit.
    /// </summary>
    public static double OrbitRadiusFor(Body b)
    {
        var minByPeriod = DetMath.Cbrt(b.Gm * (MinOrbitHours / (2 * Math.PI)) * (MinOrbitHours / (2 * Math.PI)));
        var height = Math.Min(b.Radius * (LowOrbitRadii - 1), GameData.Combat.Railgun.RangeMm * 0.5);
        return Math.Max(b.Radius + height, minByPeriod);
    }

    /// <summary>How wide a route turns round a body in its way.</summary>
    public static double ClearanceFor(Body b) => Math.Max(OrbitRadiusFor(b), b.Radius * 2);

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
    public static double ShipSpeed(Ship s) => s.MaxSpeed * Wounds.SpeedFactor(s);

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
        var (flight, chase, problem) = PlanJoin(world, f, into, speedFraction);
        if (problem != null) return problem;
        f.Flight = flight;
        f.Chase = chase ? Chases.Order(world, f, into.Id, ChaseMode.Join, speedFraction) : null;
        return null;
    }

    /// <summary>
    /// Why this fleet could not set off to join that one now (null if it can, or is already with it).
    /// Changes nothing: the screen asks this before it says the order is under way.
    /// </summary>
    public static string? JoinProblem(World world, Fleet f, Fleet into, double speedFraction)
    {
        if (f.Id == into.Id) return "it is the same fleet";
        if (f.Side != into.Side) return "they are on different sides";
        return CanJoin(f, into) ? null : PlanJoin(world, f, into, speedFraction).Problem;
    }

    private static (Flight? Flight, bool Chase, string? Problem) PlanJoin(World world, Fleet f, Fleet into, double speedFraction)
    {
        if (Meeting(world, into, speedFraction) is not FlightRequest request)
        {
            // It is drifting on: chase it on intercept tracks, matching its speed, and join on arrival.
            var chase = FlightPlanner.PlanChase(world, f, into, ChaseMode.Join, speedFraction, FleetPerformance.Of(world, f));
            return chase.Ok && chase.Flight != null ? (chase.Flight, true, null) : (null, false, chase.Problem ?? "no intercept");
        }
        var plan = FlightPlanner.Plan(world, f, request);
        return plan.Ok && plan.Flight != null ? (plan.Flight, false, null) : (null, false, plan.Problem ?? "no route");
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
        f.LeftOrbitBodyId = null;
        f.OrbitRadius = Math.Max(offset.Length, body.Radius * LowOrbitRadii);
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

public enum OrbitHeight { Low, High }

public enum BombTarget { None, Farms, Mines, Guns, People }

/// <summary>A colony fleet's errand: load people at From, carry them to To, and (Repeat) go back for more.</summary>
public sealed class PeopleRun
{
    public int FromBodyId { get; set; }
    public int ToBodyId { get; set; }
    public bool Repeat { get; set; }

    /// <summary>Set by Auto top-up rather than by a direct order.</summary>
    public bool Auto { get; set; }

    /// <summary>A settler ship's single trip: it lands with its people and is gone.</summary>
    public bool OneTrip { get; set; }

    /// <summary>The fleet the settler ship waits for at its start, to fly with it (null: none, or already together).</summary>
    public int? EscortFleetId { get; set; }
}
