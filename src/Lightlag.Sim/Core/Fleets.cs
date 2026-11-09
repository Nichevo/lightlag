namespace Lightlag.Sim.Core;

/// <summary>What a fleet does when it is not flying: circle a body, or sit with engines cold.</summary>
public enum HoldStyle { Orbit, Drift }

/// <summary>How a flight ends: brake to a stop at the target, or fly through it at speed (cheaper, faster, no stop).</summary>
public enum ArrivalStyle { Brake, FastPass }

/// <summary>Direct leaves now; Wait leaves when the trip is shortest (the target swings closer).</summary>
public enum RouteKind { Direct, Wait }

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

    /// <summary>When holding in orbit: the body, how far out, and where on the circle.</summary>
    public int? OrbitBodyId { get; set; }
    public double OrbitRadius { get; set; }
    public double OrbitAngle { get; set; }

    /// <summary>The flight under way or waiting to leave, if any.</summary>
    public Flight? Flight { get; set; }

    public bool InFlight => Flight != null;
}

/// <summary>
/// A planned flight: a chain of constant-thrust pieces (burn, coast, brake). The fleet's position at
/// any hour comes straight from these, so flights replay exactly.
/// </summary>
public sealed class Flight
{
    public int? TargetBodyId { get; set; }
    public Vec2 Target { get; set; }
    public RouteKind Route { get; set; }
    public ArrivalStyle Arrival { get; set; }
    public HoldStyle HoldAfter { get; set; }
    public double SpeedFraction { get; set; }
    public double CruiseSpeed { get; set; }

    /// <summary>Game hour the main burn starts. Before that the fleet keeps its hold.</summary>
    public double DepartHour { get; set; }
    public double ArriveHour { get; set; }

    /// <summary>Total change of speed the flight needs (Mm/h): what costs fuel.</summary>
    public double DeltaV { get; set; }
    public bool Started { get; set; }
    public List<FlightLeg> Legs { get; set; } = new();

    public double EndHour => Legs.Count > 0 ? Legs[^1].End : ArriveHour;

    public Flight Copy()
    {
        var f = (Flight)MemberwiseClone();
        f.Legs = new List<FlightLeg>(Legs);
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
            speed = Math.Min(speed, s.MaxSpeed);
            accel = Math.Min(accel, s.Accel);
            var cls = GameData.ShipClass(s.ClassId);
            dv = Math.Min(dv, s.Supplies / cls.SuppliesPerDeltaV);
        }
        if (speed == double.MaxValue) return new FleetPerformance(0, 0, 0);
        return new FleetPerformance(speed, accel, dv);
    }
}

/// <summary>Fleet bookkeeping shared by commands and the scenario builder.</summary>
public static class Fleets
{
    /// <summary>Distance between ships in the loose formation.</summary>
    public const double FormationSpacing = 12; // Mm (12,000 km)

    /// <summary>How close two fleets must be to merge.</summary>
    public const double MergeDistance = 3000; // Mm

    /// <summary>Orbits take at least this long, so a circling fleet moves smoothly at hour steps.</summary>
    public const double MinOrbitHours = 72;

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
