using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

// ---- Data (Data/transport.json) ------------------------------------------------------------

/// <summary>Sail ships: how sunlight and laser stations push them. See transport.json.</summary>
public sealed record SailData(double SunAu = 1, double MaxLight = 3, double LaserPush = 2, double LaserRangeMm = 450000, double MinPush = 0.08, int Samples = 7);

/// <summary>One kind of satellite: its cost, what it sees, and whether it relays signals.</summary>
public sealed record SatelliteKind(string Id, string Name, string Short, double Cost, string Effect = "", double SensorMm = 0, bool Relay = false);

/// <summary>Satellite numbers. See transport.json.</summary>
public sealed record SatelliteData(string Needs = "satellites", double SpeedMmPerHour = 300, int MaxPerSide = 12, double SeenMm = 0.25,
    double KillRangeMm = 30, double KillPerHour = 0.35, double LiftGoods = 4, double KeepLostDays = 10, List<SatelliteKind>? Kinds = null);

/// <summary>The lift to orbit. See transport.json.</summary>
public sealed record LiftData(double IndustryPerGood = 0.15, double RefRadiusMm = 31.85, double MaxWell = 3, double MassDriver = 0.5);

public sealed record TransportData(SailData? Sails = null, SatelliteData? Satellites = null, LiftData? Lift = null);

// ---- Sails ---------------------------------------------------------------------------------

/// <summary>
/// Sail ships (Plan 3 build 4). They carry no fuel worth the name: sunlight pushes them, strongest near the
/// star and falling with the square of the distance, and nothing at all in the shadow of a planet or moon.
/// A laser station of their side that can see them pushes them as well, which is how sails work out among
/// the giants. They are slow, but silent: there is no plume to see, so a sailing fleet shows like a cold one.
/// Sails can't slingshot. A route is flown with the average push along it (see FlightPlanner).
/// </summary>
public static class Sails
{
    public static SailData D => GameData.Transport.Sails ?? new SailData();

    public const string LaserStation = "laser";

    public static bool Is(Ship s) => GameData.ShipClasses.TryGetValue(s.ClassId, out var c) && c.Sail;
    public static bool Is(ShipClass c) => c.Sail;

    public static bool Any(World w, Fleet f) => w.ShipsOf(f).Any(Is);
    public static bool All(World w, Fleet f) => f.ShipIds.Count > 0 && w.ShipsOf(f).All(Is);

    public static Body? Star(World w)
    {
        foreach (var b in w.Bodies) if (b.Kind == BodyKind.Star) return b;
        return null;
    }

    /// <summary>Sunlight here as a share of full sunlight at SunAu (0 in a shadow). The bodies named are left out of the shadow check.</summary>
    public static double Sunlight(World w, Vec2 at, int? ignore = null, int? alsoIgnore = null)
    {
        var star = Star(w);
        var sun = star?.Position ?? Vec2.Zero;
        var d = Vec2.Distance(at, sun);
        var au = Math.Max(d / Physics.MmPerAu, 1e-3);
        if (Shaded(w, sun, at, ignore, alsoIgnore)) return 0;
        var light = D.SunAu * D.SunAu / (au * au);
        return Math.Min(D.MaxLight, light);
    }

    /// <summary>Is a planet or moon (other than the two left out) between the star and the point?</summary>
    private static bool Shaded(World w, Vec2 sun, Vec2 at, int? a, int? b)
    {
        var d = at - sun;
        var len2 = Vec2.Dot(d, d);
        foreach (var body in w.Bodies)
        {
            if (body.Kind == BodyKind.Star || body.Id == a || body.Id == b) continue;
            var t = len2 > 1e-18 ? Math.Clamp(Vec2.Dot(body.Position - sun, d) / len2, 0, 1) : 0;
            if (t <= 0 || t >= 1) continue;
            if (Vec2.Distance(sun + d * t, body.Position) < body.Radius) return true;
        }
        return false;
    }

    /// <summary>The side's working laser stations that can reach this point (in range and in sight).</summary>
    public static IEnumerable<Body> Lasers(World w, Side side, Vec2 at, int? ignore = null)
    {
        foreach (var (_, b) in Economy.Stations(w, side, LaserStation))
            if (Vec2.Distance(b.Position, at) <= D.LaserRangeMm && Sensors.LineOfSight(w, b.Position, at, b.Id, ignore)) yield return b;
    }

    /// <summary>The push on a sail here: sunlight plus any laser station in reach, capped (1 = full sunlight at SunAu).</summary>
    public static double PushAt(World w, Side side, Vec2 at, int? ignore = null, int? alsoIgnore = null)
    {
        var push = Sunlight(w, at, ignore, alsoIgnore);
        if (w.Stations.Count > 0 && Lasers(w, side, at, ignore).Any()) push += D.LaserPush;
        return Math.Min(D.MaxLight + D.LaserPush, push);
    }

    /// <summary>The push on a fleet's sails where it is now (the body it orbits casts no shadow on it: it is always round the corner).</summary>
    public static double PushOn(World w, Fleet f) => PushAt(w, f.Side, f.Position, f.OrbitBodyId ?? NearBody(w, f.Position));

    private static int? NearBody(World w, Vec2 p)
    {
        foreach (var b in w.Bodies)
            if (b.Kind != BodyKind.Star && Vec2.Distance(b.Position, p) < b.Radius * 3 + 1) return b.Id;
        return null;
    }

    /// <summary>The average push along a straight route (the end bodies cast no shadow on their own ends).</summary>
    public static double AlongRoute(World w, Side side, Vec2 from, Vec2 to, int? fromBody = null, int? toBody = null)
    {
        var n = Math.Max(2, D.Samples);
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / (n - 1);
            var p = from + (to - from) * t;
            sum += PushAt(w, side, p, t < 0.5 ? fromBody ?? toBody : toBody ?? fromBody, t < 0.5 ? toBody : fromBody);
        }
        return sum / n;
    }

    /// <summary>
    /// The fleet's performance for a route to this point: its sail ships take the average push along the way.
    /// Fleets without sails are unchanged.
    /// </summary>
    public static FleetPerformance ForRoute(World w, Fleet f, FlightRequest req, out double push)
    {
        push = 1;
        if (!Any(w, f)) return FleetPerformance.Of(w, f);
        var to = RouteEnd(w, f, req);
        push = AlongRoute(w, f.Side, f.Position, to, f.OrbitBodyId ?? NearBody(w, f.Position), req.TargetBodyId);
        return FleetPerformance.Of(w, f, push);
    }

    private static Vec2 RouteEnd(World w, Fleet f, FlightRequest req)
    {
        if (req.TargetBodyId is int id && w.FindBody(id) is Body b) return b.Position;
        if (req.TargetFleetId is int fid && Sensors.Predict(w, f.Side, fid, w.Hour) is Vec2 p) return p;
        if (req.Waypoints is { Count: > 0 } wps && !req.Point.IsFinite) return wps[^1].Point;
        return req.Point.IsFinite ? req.Point : f.Position;
    }

    /// <summary>Why a sail fleet can't fly this route, or null (enough push on average).</summary>
    public static string? RouteProblem(World w, Fleet f, double push)
    {
        if (!Any(w, f) || push >= D.MinPush) return null;
        var au = Vec2.Distance(f.Position, Star(w)?.Position ?? Vec2.Zero) / Physics.MmPerAu;
        return Tech.Has(w, f.Side, "laser-sails")
            ? $"Too little light for the sails on this route ({push:P0} of the push at {D.SunAu:0} AU): build a laser station that can see it"
            : $"Too little light for the sails on this route ({push:P0} of the push at {D.SunAu:0} AU; {au:0.#} AU out): research Laser sails for laser stations";
    }

    /// <summary>One line on a fleet's sails for its panel: the push now and where it comes from.</summary>
    public static string? Status(World w, Fleet f)
    {
        if (!Any(w, f)) return null;
        var ignore = f.OrbitBodyId ?? NearBody(w, f.Position);
        var sun = Sunlight(w, f.Position, ignore);
        var lasers = w.Stations.Count > 0 ? Lasers(w, f.Side, f.Position, ignore).Select(b => b.Name).ToList() : new List<string>();
        var au = Vec2.Distance(f.Position, Star(w)?.Position ?? Vec2.Zero) / Physics.MmPerAu;
        var parts = new List<string>();
        parts.Add(sun <= 0 ? "IN SHADOW: no sunlight" : $"sunlight {sun:P0} ({au:0.##} AU)");
        if (lasers.Count > 0) parts.Add($"laser from {string.Join(", ", lasers)}");
        var push = PushOn(w, f);
        var note = push < D.MinPush ? " · too weak to set out: wait for light or a laser station" : "";
        return $"Sails: push {push:P0} of full · " + string.Join(" · ", parts) + note + (All(w, f) ? " · silent (no plume)" : "");
    }
}

// ---- Satellites ----------------------------------------------------------------------------

/// <summary>Where a satellite sits: low orbit round its body, or that body's leading or trailing point (60 degrees ahead or behind on its orbit).</summary>
public enum SatSpot { Orbit, Leading, Trailing }

/// <summary>A satellite: on its way (Arrived false) from where it was launched, then in place at its spot.</summary>
public sealed class Satellite
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public string Kind { get; set; } = "";
    public int BodyId { get; set; }
    public SatSpot Spot { get; set; }
    public Vec2 Position { get; set; }
    public bool Arrived { get; set; }
    public double LaunchHour { get; set; }
    public int FromBodyId { get; set; }

    /// <summary>Seen by the other side last hour (it can be shot down; it shows on their map).</summary>
    public bool Seen { get; set; }

    /// <summary>Shot down: when and by whom (kept a while so the owner hears of it). Lost ones do nothing.</summary>
    public double? LostHour { get; set; }
    public string? LostTo { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Satellites (Plan 3 build 4): small, cheap and fragile. Sensor satellites watch a patch of space and
/// relay satellites pass signals round planets, from the orbit of any body (yours, nobody's or the enemy's)
/// or from a planet's leading or trailing point. They are launched from a body you hold and pay its lift.
/// The enemy sees one only close up; an armed fleet that does shoots it down.
/// </summary>
public static class Satellites
{
    public static SatelliteData D => GameData.Transport.Satellites ?? new SatelliteData();
    public static IReadOnlyList<SatelliteKind> Kinds => D.Kinds ?? new List<SatelliteKind>();
    public static SatelliteKind? KindOf(string id) => Kinds.FirstOrDefault(k => k.Id == id);

    /// <summary>The side's satellites in place and working.</summary>
    public static IEnumerable<Satellite> Working(World w, Side side) => w.Satellites.Where(s => s.Side == side && s.Arrived && s.LostHour == null);

    public static IEnumerable<Satellite> Live(World w, Side side) => w.Satellites.Where(s => s.Side == side && s.LostHour == null);

    /// <summary>Where a spot is now: low orbit over the body, or 60 degrees ahead of or behind it round what it orbits.</summary>
    public static Vec2 SpotAt(World w, Body b, SatSpot spot, int seed = 0) => SpotAt(w, b, spot, b.Position, seed);

    private static Vec2 SpotAt(World w, Body b, SatSpot spot, Vec2 bodyAt, int seed)
    {
        if (spot == SatSpot.Orbit || b.ParentId is not int pid || w.FindBody(pid) is not Body parent)
        {
            // Sits a little above the orbit height ships use, on a side set by its number (so several don't stack).
            var r = Fleets.OrbitRadiusFor(b) * 1.2;
            var k = seed % 6;
            var (c, s) = Turn60(k);
            return bodyAt + new Vec2(r * c, r * s);
        }
        var rel = bodyAt - parent.Position;
        // Leading: ahead on the orbit. Bodies go round anticlockwise (the angle grows), so ahead is +60 degrees.
        var sign = spot == SatSpot.Leading ? 1 : -1;
        var cos = 0.5;
        var sin = sign * 0.86602540378443865;
        return parent.Position + new Vec2(rel.X * cos - rel.Y * sin, rel.X * sin + rel.Y * cos);
    }

    /// <summary>cos and sin of k times 60 degrees (exact constants: no trig in the simulation).</summary>
    private static (double Cos, double Sin) Turn60(int k) => (k % 6) switch
    {
        0 => (1, 0),
        1 => (0.5, 0.86602540378443865),
        2 => (-0.5, 0.86602540378443865),
        3 => (-1, 0),
        4 => (-0.5, -0.86602540378443865),
        _ => (0.5, -0.86602540378443865),
    };

    /// <summary>Where the satellite is now (in place: its spot, moving with its body).</summary>
    public static Vec2 PositionOf(World w, Satellite s) =>
        s.Arrived && w.FindBody(s.BodyId) is Body b ? SpotAt(w, b, s.Spot, s.Id) : s.Position;

    /// <summary>The body a satellite in low orbit sits by (it does not block its own view), or null.</summary>
    public static int? Anchor(Satellite s) => s.Arrived && s.Spot == SatSpot.Orbit ? s.BodyId : null;

    public static string SpotName(World w, Satellite s) => SpotName(w.FindBody(s.BodyId)?.Name ?? "?", s.Spot);

    public static string SpotName(string body, SatSpot spot) => spot switch
    {
        SatSpot.Leading => $"{body}'s leading point",
        SatSpot.Trailing => $"{body}'s trailing point",
        _ => $"orbit of {body}",
    };

    /// <summary>The body a satellite for this spot is launched from: the side's held body nearest to it (one with people, a yard or a store).</summary>
    public static Body? LaunchSite(World w, Side side, Vec2 to) =>
        w.Bodies.Where(b => b.Owner == side && (b.Shipyard || b.PopK > 0.05))
            .OrderBy(b => Vec2.Distance(b.Position, to)).ThenBy(b => b.Id).FirstOrDefault();

    /// <summary>What a satellite costs launched from this body: its own Industry plus the lift.</summary>
    public static double CostOf(World w, SatelliteKind k, Body? from) => k.Cost + (from != null ? Lift.CostPerGood(w, from) * D.LiftGoods : 0);

    /// <summary>Why this satellite can't be launched now, or null if it can.</summary>
    public static string? CanLaunch(World w, Side side, string kind, int bodyId, SatSpot spot)
    {
        if (!w.Loop) return "There is no economy in the battle sandbox";
        if (w.Winner != null) return "The game is over";
        if (KindOf(kind) is not SatelliteKind k) return $"Unknown satellite '{kind}'";
        if (D.Needs.Length > 0 && !Tech.Has(w, side, D.Needs)) return $"Research {GameData.Research(D.Needs).Name} first";
        if (w.FindBody(bodyId) is not Body b) return "That body is gone";
        if (spot != SatSpot.Orbit && (b.ParentId == null || b.Kind == BodyKind.Star)) return "Only planets and moons have leading and trailing points";
        if (k.Relay && !w.Light.On) return "Relays matter only with light lag on";
        if (Live(w, side).Count() >= D.MaxPerSide) return $"At most {D.MaxPerSide} satellites at once";
        if (Live(w, side).Any(s => s.Kind == kind && s.BodyId == bodyId && s.Spot == spot)) return $"A {k.Name.ToLowerInvariant()} is already there";
        var from = LaunchSite(w, side, b.Position);
        if (from == null) return "No world of yours to launch it from";
        var cost = CostOf(w, k, from);
        if (Economy.Of(w, side).Industry < cost) return $"Needs {cost:0} Industry";
        return null;
    }

    /// <summary>Each hour: satellites on their way fly on; the enemy sees them close up and shoots them down; old losses are forgotten.</summary>
    public static void Hourly(Simulation sim)
    {
        var w = sim.World;
        if (w.Satellites.Count == 0) return;
        foreach (var s in w.Satellites.ToList())
        {
            if (s.LostHour is double lost)
            {
                if (w.Hour - lost > D.KeepLostDays * 24) w.Satellites.Remove(s);
                continue;
            }
            if (w.FindBody(s.BodyId) is not Body b) { Lose(sim, s, "its body is gone"); continue; }
            if (!s.Arrived) Fly(sim, s, b);
            var at = PositionOf(w, s);
            s.Seen = Sides.Enemies(w, s.Side).Any(e => SeenBy(w, e, at, Anchor(s)));
            if (!s.Seen) continue;
            foreach (var e in w.Fleets.OrderBy(f => f.Id))
            {
                if (e.Side == s.Side || !e.Position.IsFinite || Vec2.Distance(e.Position, at) > D.KillRangeMm || !Combat.Armed(w, e)) continue;
                if (sim.Rng.NextDouble() < Math.Min(1, D.KillPerHour))
                {
                    Lose(sim, s, e.Name);
                    break;
                }
            }
        }
    }

    private static void Fly(Simulation sim, Satellite s, Body b)
    {
        var w = sim.World;
        var target = SpotAt(w, b, s.Spot, s.Id);
        var gap = target - s.Position;
        var step = D.SpeedMmPerHour;
        if (gap.Length <= step)
        {
            s.Arrived = true;
            s.Position = target;
            sim.Log.Info("satellite", $"{s.Name} is in place", new { satId = s.Id, side = s.Side.ToString(), s.Kind, s.BodyId, spot = s.Spot.ToString(), hours = w.Hour - s.LaunchHour });
        }
        else s.Position += gap.Normalized() * step;
    }

    /// <summary>Does the side see a satellite here? Only close up: its sensors reach SeenMm of their range (a tiny, cold target).</summary>
    public static bool SeenBy(World w, Side side, Vec2 at, int? anchor)
    {
        foreach (var f in w.Fleets)
        {
            if (f.Side != side || !f.Position.IsFinite) continue;
            if (Vec2.Distance(f.Position, at) <= Sensors.SensorOf(w, f) * D.SeenMm && Sensors.LineOfSight(w, f.Position, at, anchor)) return true;
        }
        foreach (var b in w.Bodies)
        {
            if (b.Owner != side) continue;
            if (Vec2.Distance(b.Position, at) <= Sensors.PlanetSensorOf(w, side, b) * D.SeenMm && Sensors.LineOfSight(w, b.Position, at, b.Id, anchor)) return true;
        }
        foreach (var o in Working(w, side))
        {
            var k = KindOf(o.Kind);
            if (k == null || k.SensorMm <= 0) continue;
            var p = PositionOf(w, o);
            if (Vec2.Distance(p, at) <= k.SensorMm * Tech.Factor(w, side, Tech.LongSensors) * D.SeenMm && Sensors.LineOfSight(w, p, at, Anchor(o), anchor)) return true;
        }
        return false;
    }

    private static void Lose(Simulation sim, Satellite s, string to)
    {
        var w = sim.World;
        s.LostHour = w.Hour;
        s.LostTo = to;
        Economy.Of(w, s.Side).Totals.SatellitesLost++;
        s.Position = PositionOf(w, s);
        sim.Log.Info("satellite", $"{s.Name} is lost", new { satId = s.Id, side = s.Side.ToString(), s.Kind, s.BodyId, to });
    }

    /// <summary>Sensors the side's working sensor satellites add: where, how far they see, and the body they sit by.</summary>
    public static IEnumerable<(Vec2 At, double Sensor, int? Body)> Observers(World w, Side side)
    {
        foreach (var s in Working(w, side))
            if (KindOf(s.Kind) is { SensorMm: > 0 } k)
                yield return (PositionOf(w, s), k.SensorMm * Tech.Factor(w, side, Tech.LongSensors) * Races.Of(w, side).Sensors, Anchor(s));
    }

    /// <summary>The side's working relay satellites, for signals (see Light): where each is and the body it sits by.</summary>
    public static IEnumerable<(Vec2 At, int? Body)> Relays(World w, Side side)
    {
        foreach (var s in Working(w, side))
            if (KindOf(s.Kind) is { Relay: true }) yield return (PositionOf(w, s), Anchor(s));
    }

    /// <summary>One line for a satellite in a list: what and where, and how it is doing.</summary>
    public static string Describe(World w, Satellite s)
    {
        var k = KindOf(s.Kind);
        var where = SpotName(w, s);
        if (s.LostHour is double lost) return $"{s.Name}: LOST {Format(w.Hour - lost)} ago at {where} ({(s.LostTo is string to ? "shot down by " + to : "gone")})";
        if (!s.Arrived)
        {
            var b = w.FindBody(s.BodyId);
            var left = b != null ? Vec2.Distance(s.Position, SpotAt(w, b, s.Spot, s.Id)) / D.SpeedMmPerHour : 0;
            return $"{s.Name}: on its way to {where}, about {Format(left)} left";
        }
        return $"{s.Name} at {where}: working" + (s.Seen ? " · SEEN by the enemy" : "") + (k?.SensorMm > 0 ? $" · sees {k.SensorMm:0} Mm sensor class" : "");
    }

    private static string Format(double hours) => hours < 48 ? $"{hours:0} h" : $"{hours / 24:0.#} days";
}

/// <summary>Launch a satellite from the side's nearest world to a body's orbit, or to its leading or trailing point. Paid now.</summary>
public sealed record LaunchSatelliteCommand(Side Side, string Kind, int BodyId, SatSpot Spot) : GameCommand
{
    /// <summary>The order goes to where the satellite is launched.</summary>
    public override (Side Side, Vec2 At)? Destination(World world) =>
        (Side, world.FindBody(BodyId) is Body b && Satellites.LaunchSite(world, Side, b.Position) is Body from ? from.Position : Light.Seat(world, Side));

    public override void Apply(World world, Simulation sim)
    {
        if (Satellites.CanLaunch(world, Side, Kind, BodyId, Spot) is string problem)
        {
            sim.Log.Warn("satellite", $"Can't launch a satellite: {problem}", new { side = Side.ToString(), Kind, BodyId, Spot = Spot.ToString(), Source });
            return;
        }
        var b = world.FindBody(BodyId)!;
        var k = Satellites.KindOf(Kind)!;
        var from = Satellites.LaunchSite(world, Side, b.Position)!;
        var cost = Satellites.CostOf(world, k, from);
        var state = Economy.Of(world, Side);
        state.Industry -= cost;
        state.Totals.IndustrySpent += cost;
        state.Totals.LiftSpent += cost - k.Cost;
        state.Totals.SatellitesLaunched++;
        var number = world.ShipNumbers.GetValueOrDefault($"{Side.ToString().ToLowerInvariant()}:sat") + 1;
        world.ShipNumbers[$"{Side.ToString().ToLowerInvariant()}:sat"] = number;
        var sat = new Satellite
        {
            Id = world.NextId++, Side = Side, Kind = Kind, BodyId = BodyId, Spot = Spot, Position = from.Position, LaunchHour = world.Hour,
            FromBodyId = from.Id, Name = $"{k.Short.Substring(0, 1).ToUpperInvariant()}{k.Short.Substring(1)} sat {number}",
        };
        world.Satellites.Add(sat);
        sim.Log.Info("satellite", $"{Side} launches a {k.Name.ToLowerInvariant()}", new { side = Side.ToString(), satId = sat.Id, Kind, BodyId, Spot = Spot.ToString(), from = from.Id, cost, Source });
    }
}

/// <summary>Switch a satellite off and let it fall (frees its place under the limit). A home order: at once.</summary>
public sealed record ScrapSatelliteCommand(Side Side, int SatelliteId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        var s = world.Satellites.Find(x => x.Id == SatelliteId && x.Side == Side);
        if (s == null)
        {
            sim.Log.Warn("satellite", "Scrap for a satellite that does not exist", new { side = Side.ToString(), SatelliteId, Source });
            return;
        }
        world.Satellites.Remove(s);
        sim.Log.Info("satellite", $"{Side} scraps {s.Name}", new { side = Side.ToString(), SatelliteId, Source });
    }
}

// ---- Lift to orbit -------------------------------------------------------------------------

/// <summary>
/// The lift to orbit (Plan 3 build 4). Goods loaded at a body for the trip out (freighters, tankers, supply
/// ships) and satellites launched from it pay for the climb in Industry, by the depth of its gravity well:
/// an Earth-sized world costs the full price, small moons and asteroids almost nothing. The ladder cuts it:
/// a working mass driver, then a skyhook over the world, then a space elevator; the best one counts.
/// </summary>
public static class Lift
{
    public static LiftData D => GameData.Transport.Lift ?? new LiftData();

    /// <summary>How deep the body's gravity well is: its mass over its radius, 1 for an Earth-sized world (at most MaxWell).</summary>
    public static double Well(Body b)
    {
        if (b.Kind == BodyKind.Star || b.Radius <= 0) return D.MaxWell;
        var earth = Physics.GmEarth / D.RefRadiusMm;
        return Math.Min(D.MaxWell, b.Gm / b.Radius / earth);
    }

    /// <summary>The share of the lift paid at this body (1 by rocket; less with the ladder), and what gives it.</summary>
    public static (double Share, string By) Ladder(World w, Body b)
    {
        var best = (1.0, "rockets");
        if (b.Owner is Side side)
        {
            foreach (var st in w.Stations)
                if (st.Side == side && st.BodyId == b.Id && w.Hour >= st.ReadyHour && Economy.StationKind(st.Kind) is { LiftCut: > 0 } k && k.LiftCut < best.Item1)
                    best = (k.LiftCut, k.Name.ToLowerInvariant());
            if (D.MassDriver < best.Item1 && Colonies.Built(b).Any(p => Planets.BuildingKind(p.Kind)?.LaunchPerDay > 0 && p.Powered))
                best = (D.MassDriver, "mass driver");
        }
        return best;
    }

    /// <summary>Industry to lift one good off this body now.</summary>
    public static double CostPerGood(World w, Body b) => D.IndustryPerGood * Well(b) * Ladder(w, b).Share;

    /// <summary>Pays the lift for goods loaded at this body: Industry goes (never below zero) and the spend is counted.</summary>
    public static double Pay(World w, Side side, Body b, double goods)
    {
        if (!w.Loop || goods <= 1e-9) return 0;
        var cost = CostPerGood(w, b) * goods;
        if (cost <= 1e-9) return 0;
        var state = Economy.Of(w, side);
        var paid = Math.Min(cost, Math.Max(0, state.Industry));
        state.Industry -= paid;
        state.Totals.IndustrySpent += paid;
        state.Totals.LiftSpent += paid;
        return paid;
    }

    /// <summary>One line on a body's lift for its panel.</summary>
    public static string Describe(World w, Body b)
    {
        var (share, by) = Ladder(w, b);
        var well = Well(b);
        if (well < 0.01) return $"Lift to orbit: almost free (a shallow well) · {CostPerGood(w, b) * 100:0.##} Industry per 100 goods";
        return $"Lift to orbit: {CostPerGood(w, b) * 100:0.#} Industry per 100 goods shipped out (well {well:0.##} × Earth, by {by}"
               + (share < 1 ? $", {1 - share:P0} off" : "") + ")";
    }
}
