using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>An enemy cargo or supply fleet seen under way (the raw stuff lanes are made from).</summary>
public sealed class LaneObs
{
    public Side SeenBy { get; set; }
    public int FleetId { get; set; }
    public Vec2 Position { get; set; }
    public Vec2 Velocity { get; set; }
    public double Hour { get; set; }
}

/// <summary>
/// A supply lane one side has worked out from sightings: enemy cargo ships running between two bodies.
/// It is a moving channel: its ends are the two bodies where they are now, so it swings round as they
/// orbit. Its width grows with the time since the last sighting; confidence grows with sightings and fades.
/// </summary>
public sealed class Lane
{
    public int Id { get; set; }
    public Side SeenBy { get; set; }
    public int FromBodyId { get; set; }
    public int ToBodyId { get; set; }
    public int Seen { get; set; }
    public double FirstHour { get; set; }
    public double LastHour { get; set; }
    public double Speed { get; set; }
}

public enum ZoneKind { Raid, Guard, Picket }

/// <summary>Drift in zone (minimal nudges, nearly no fuel) or keep station (hold still, a little fuel a day).</summary>
public enum ZoneHold { Drift, StationKeep }

/// <summary>
/// A zone a side has drawn: a raiding zone (prey on passing enemy cargo), a guard zone (fight what comes
/// in) or a picket line (watch and warn). Laid over a lane (or between two bodies) it slides with them.
/// </summary>
public sealed class Zone
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public ZoneKind Kind { get; set; }
    public string Name { get; set; } = "";
    public Vec2 Center { get; set; }
    public double RadiusMm { get; set; } = 150;

    /// <summary>On a lane: the two bodies, and how far along from the first (0..1).</summary>
    public int? FromBodyId { get; set; }
    public int? ToBodyId { get; set; }
    public double T { get; set; } = 0.5;

    public ZoneHold Hold { get; set; } = ZoneHold.Drift;
    public bool Prey { get; set; } = true;
    public int Catches { get; set; }
    public double LastWarnHour { get; set; } = -1e9;
}

/// <summary>A place where a side lost cargo lately (auto transport keeps away, or sends an escort).</summary>
public sealed record LossSpot(Side Side, Vec2 At, double Hour);

/// <summary>
/// Supply lanes, ambushes, hunts and zones. Sightings of enemy cargo ships under way are turned into
/// lanes each day. A fleet given a zone (Ambush) or a pair of enemy planets (Hunt) goes there, drifts
/// in the zone with small nudges (or keeps station, for a little fuel a day) and, if it preys on passing
/// ships, goes after enemy cargo that comes within reach. Cargo lost to raiders is gone (half of it
/// taken by the raider when it is close); automatic transport keeps away from places of recent losses,
/// or sends an escort with the freighter.
/// </summary>
public static class Lanes
{
    public const double ForgetDays = 30;
    public const double BaseWidthMm = 30;
    public const double WidthPerHourMm = 1.5;
    public const double MaxWidthMm = 400;
    public const double KeepStationPerDay = 0.01; // share of full supplies a day
    public const double LossDays = 10;
    public const double LossRadiusMm = 0.08 * 149_598; // about 0.08 AU (Mm per AU)

    /// <summary>Only cargo or supply ships (as far as the sighting shows): what raiders hunt.</summary>
    public static bool CargoOnly(World w, Fleet f) => f.ShipIds.Count > 0 && w.ShipsOf(f).All(s => Fitting.Spec(s) is { Cargo: > 0 } c && c.Railguns == 0 && c.Missiles == 0);

    public static bool CargoSighting(Sighting s) =>
        s.Ships > 0 && s.Classes.Count >= s.Ships && s.Classes.All(c => GameData.ShipClasses.TryGetValue(c, out var k) && k.Cargo > 0 && k.Railguns == 0 && k.Missiles == 0);

    public static double Confidence(World w, Lane l) =>
        Math.Clamp(Math.Min(1, l.Seen / 4.0) * (1 - (w.Hour - l.LastHour) / (ForgetDays * 24)), 0, 1);

    public static double WidthMm(World w, Lane l) => Math.Min(MaxWidthMm, BaseWidthMm + WidthPerHourMm * (w.Hour - l.LastHour));

    /// <summary>Days between ships on the lane (null with one sighting).</summary>
    public static double? EveryDays(Lane l) => l.Seen >= 2 ? Math.Max(0.5, (l.LastHour - l.FirstHour) / (l.Seen - 1) / 24) : null;

    /// <summary>Hours until the next ship is due (null: unknown).</summary>
    public static double? NextDueHours(World w, Lane l) => EveryDays(l) is double d ? Math.Max(0, l.LastHour + d * 24 - w.Hour) : null;

    /// <summary>A point a share t along the lane between two bodies as they are now.</summary>
    public static Vec2? PointOn(World w, int? from, int? to, double t) =>
        from is int a && to is int b && w.FindBody(a) is Body fa && w.FindBody(b) is Body fb ? fa.Position + (fb.Position - fa.Position) * t : null;

    private static double DistToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var len = ab.X * ab.X + ab.Y * ab.Y;
        var t = len > 1e-9 ? Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len, 0, 1) : 0;
        return Vec2.Distance(p, a + ab * t);
    }

    /// <summary>Hourly: note enemy cargo under way; daily: turn the notes into lanes and forget old ones.</summary>
    public static void Observe(Simulation sim)
    {
        var w = sim.World;
        foreach (var s in w.Sightings.Where(s => !s.Own && s.Visible && w.Hour - s.Hour < 1 && s.Velocity.Length > 1 && CargoSighting(s)))
        {
            if (w.LaneObs.Any(o => o.SeenBy == s.SeenBy && o.FleetId == s.FleetId && w.Hour - o.Hour < 6)) continue;
            w.LaneObs.Add(new LaneObs { SeenBy = s.SeenBy, FleetId = s.FleetId, Position = s.Position, Velocity = s.Velocity, Hour = w.Hour });
        }
        if (w.Tick % GameTime.TicksPerDay != 0) return;
        foreach (var o in w.LaneObs.ToList())
        {
            w.LaneObs.Remove(o);
            var enemy = Combat.Other(o.SeenBy);
            var ends = w.Bodies.Where(b => b.Owner == enemy).ToList();
            // Where the bodies were when the ship was seen (they have moved on since).
            var then = ends.ToDictionary(b => b.Id, b => FlightPlanner.PredictBody(w, b, o.Hour));
            Body? Best(Vec2 dir) => ends.Where(b => Vec2.Distance(then[b.Id], o.Position) > 1)
                .Select(b => (b, cos: ((then[b.Id] - o.Position).X * dir.X + (then[b.Id] - o.Position).Y * dir.Y) / Vec2.Distance(then[b.Id], o.Position)))
                .Where(x => x.cos > 0.9).OrderByDescending(x => x.cos).ThenBy(x => x.b.Id).Select(x => x.b).FirstOrDefault();
            // Heading against the space it moves through: the nearest of those bodies' motion taken off.
            var near = ends.OrderBy(b => Vec2.Distance(then[b.Id], o.Position)).ThenBy(b => b.Id).FirstOrDefault();
            var frame = near == null ? Vec2.Zero : FlightPlanner.PredictBody(w, near, o.Hour + 1) - then[near.Id];
            var rel = o.Velocity - frame;
            if (rel.Length < 0.5) continue;
            var dir = rel.Normalized();
            if (Best(dir) is not Body to || Best(dir * -1) is not Body from || to.Id == from.Id) continue;
            var (a, b) = from.Id < to.Id ? (from.Id, to.Id) : (to.Id, from.Id);
            var lane = w.Lanes.Find(l => l.SeenBy == o.SeenBy && l.FromBodyId == a && l.ToBodyId == b);
            if (lane == null)
            {
                lane = new Lane { Id = w.NextId++, SeenBy = o.SeenBy, FromBodyId = a, ToBodyId = b, FirstHour = o.Hour };
                w.Lanes.Add(lane);
                sim.Log.Info("lanes", $"{o.SeenBy} spots a supply lane", new { side = o.SeenBy.ToString(), lane = lane.Id, from = from.Name, to = to.Name });
            }
            lane.Seen++;
            lane.LastHour = Math.Max(lane.LastHour, o.Hour);
            lane.Speed = o.Velocity.Length;
        }
        w.Lanes.RemoveAll(l => w.Hour - l.LastHour > ForgetDays * 24 || w.FindBody(l.FromBodyId) == null || w.FindBody(l.ToBodyId) == null);
        w.LossSpots.RemoveAll(l => w.Hour - l.Hour > LossDays * 24);
    }

    /// <summary>The zone's centre now (it slides with its lane).</summary>
    public static Vec2 CenterOf(World w, Zone z) => PointOn(w, z.FromBodyId, z.ToBodyId, z.T) ?? z.Center;

    /// <summary>The side's lanes that pass through the zone.</summary>
    public static IEnumerable<Lane> LanesThrough(World w, Zone z) => w.Lanes.Where(l => l.SeenBy == z.Side
        && PointOn(w, l.FromBodyId, l.ToBodyId, 0) is Vec2 a && PointOn(w, l.FromBodyId, l.ToBodyId, 1) is Vec2 b
        && DistToSegment(CenterOf(w, z), a, b) <= z.RadiusMm + WidthMm(w, l) / 2);

    /// <summary>For the zone panel: catches so far, hours until the next ship is due, and the chance of catching it.</summary>
    public static (int Catches, double? NextDue, double Chance) Info(World w, Zone z)
    {
        var lanes = LanesThrough(w, z).ToList();
        var due = lanes.Select(l => NextDueHours(w, l)).Where(h => h != null).Min();
        var fleets = w.Fleets.Where(f => f.ZoneId == z.Id && f.ShipIds.Count > 0).ToList();
        if (lanes.Count == 0 || fleets.Count == 0) return (z.Catches, due, 0);
        var lane = lanes.OrderByDescending(l => Confidence(w, l)).ThenBy(l => l.Id).First();
        var cover = Math.Min(1, 2 * z.RadiusMm / Math.Max(1, WidthMm(w, lane)));
        var fast = fleets.Max(f => FleetPerformance.Of(w, f).MaxSpeed) >= lane.Speed ? 1 : 0.5;
        return (z.Catches, due, Math.Round(cover * fast * Confidence(w, lane), 2));
    }

    /// <summary>Fuel a day for holding the zone (station keeping; drifting costs only the odd nudge).</summary>
    public static double FuelPerDay(World w, Fleet f, Zone z) =>
        z.Hold == ZoneHold.StationKeep ? w.ShipsOf(f).Sum(s => Fitting.MaxSupplies(w, s)) * KeepStationPerDay : 0;

    /// <summary>Hourly: fleets on zones hold them and prey on what passes; zones without fleets on a lost lane go.</summary>
    public static void UpdateZones(Simulation sim)
    {
        var w = sim.World;
        foreach (var z in w.Zones.ToList())
        {
            if (z.FromBodyId is int a && w.FindBody(a) == null || z.ToBodyId is int b && w.FindBody(b) == null) { w.Zones.Remove(z); continue; }
            z.Center = CenterOf(w, z);
        }
        foreach (var f in w.Fleets.Where(f => f.ZoneId != null && f.ShipIds.Count > 0).OrderBy(f => f.Id).ToList())
        {
            if (w.Zones.Find(z => z.Id == f.ZoneId) is not Zone z) { f.ZoneId = null; if (f.Mission is Mission.Ambush or Mission.Hunt) f.Mission = Mission.None; continue; }
            if (f.Retreating || f.Refit != null || Heat.InBattle(w, f) || f.Chase != null || f.JoinFleetId != null) continue;
            var center = z.Center;
            // Prey or guard: go after what comes into the zone.
            var reach = z.RadiusMm + GameData.Combat.ContactRangeMm;
            var target = w.Fleets.Where(o => o.Side != f.Side && o.ShipIds.Count > 0 && Sensors.Sees(w, f.Side, o) && Vec2.Distance(o.Position, center) <= reach
                                             && (z.Kind == ZoneKind.Raid && z.Prey && CargoOnly(w, o) || z.Kind == ZoneKind.Guard && Combat.Armed(w, o)))
                .OrderBy(o => Vec2.Distance(o.Position, f.Position)).ThenBy(o => o.Id).FirstOrDefault();
            if (target != null && Combat.Armed(w, f) && FleetPerformance.Of(w, f).DeltaVLeft > 1)
            {
                f.Stance = Stance.Engage;
                new PlanFlightCommand(f.Id, new FlightRequest(null, target.Position, 1.0, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick,
                    TargetFleetId: target.Id, Chase: ChaseMode.GunRange)) { Source = "zone" }.Apply(w, sim);
                sim.Log.Info("lanes", $"{f.Name} goes after {target.Name} in {z.Name}", new { fleetId = f.Id, target = target.Id, zone = z.Id });
                continue;
            }
            if (z.Kind == ZoneKind.Picket && f.Stance != Stance.Avoid) f.Stance = Stance.Avoid;
            if (f.Flight != null || f.Maneuvering) continue;
            var d = Vec2.Distance(f.Position, center);
            var drift = f.Position + (f.Velocity - VelocityOf(w, z)) * 12 - center; // where it drifts to in half a day, against the zone
            if (d > z.RadiusMm || z.Hold == ZoneHold.Drift && drift.Length > z.RadiusMm * 0.9)
            {
                // A nudge back in (a slow, cheap burn), or the flight out to the zone.
                var speed = d > z.RadiusMm * 2 ? 0.6 : 0.15;
                new PlanFlightCommand(f.Id, new FlightRequest(null, center, speed, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick)) { Source = "zone" }.Apply(w, sim);
                continue;
            }
            if (z.Hold == ZoneHold.StationKeep)
            {
                var keep = FuelPerDay(w, f, z) / 24 / Math.Max(1, f.ShipIds.Count);
                foreach (var s in w.ShipsOf(f)) s.Supplies = Math.Max(0, s.Supplies - keep);
            }
        }
    }

    /// <summary>How fast a zone moves (a lane zone moves with its bodies), so drifting is judged against it.</summary>
    private static Vec2 VelocityOf(World w, Zone z)
    {
        if (z.FromBodyId is not int a || z.ToBodyId is not int b || w.FindBody(a) is not Body fa || w.FindBody(b) is not Body fb) return Vec2.Zero;
        return Combat.BodyVelocity(w, fa) * (1 - z.T) + Combat.BodyVelocity(w, fb) * z.T;
    }

    /// <summary>A cargo ship's fleet is gone with its load: the raider nearby takes half, the rest is lost; zones count the catch.</summary>
    public static void OnCargoLost(Simulation sim, Fleet hauler, Shipment s)
    {
        var w = sim.World;
        var at = hauler.Position;
        w.LossSpots.Add(new LossSpot(s.Side, at, w.Hour));
        var raider = w.Fleets.Where(f => f.Side != s.Side && f.ShipIds.Count > 0 && Combat.Armed(w, f) && Vec2.Distance(f.Position, at) <= GameData.Combat.ContactRangeMm * 2)
            .OrderBy(f => Vec2.Distance(f.Position, at)).ThenBy(f => f.Id).FirstOrDefault();
        if (raider == null) return;
        var t = Economy.Of(w, raider.Side).Totals;
        t.AmbushCatches++;
        Choices.Note(w, raider.Side, "raid", "caught a freighter", "", $"ships {raider.ShipIds.Count}");
        if (w.ActiveBattle is Battle fight && fight.For(raider.Side).Fleets.Contains(raider.Name)) fight.For(raider.Side).CargoCaught++;
        if (raider.ZoneId is int zid && w.Zones.Find(z => z.Id == zid) is Zone zone) zone.Catches++;
        if (s.Loaded && w.Bodies.Where(b => b.Owner == raider.Side && b.Board != null).OrderBy(b => Vec2.Distance(b.Position, at)).ThenBy(b => b.Id).FirstOrDefault() is Body store)
        {
            foreach (var (good, amount) in s.Cargo.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                store.Stock[good] = Colonies.Held(store, good) + amount / 2;
                t.CargoCaptured += amount / 2;
            }
            sim.Log.Info("lanes", $"{raider.Name} takes cargo from a {s.Side} freighter", new { fleetId = raider.Id, store = store.Name, cargo = s.Cargo });
        }
    }

    /// <summary>Has the side lost cargo near the straight run between these two bodies lately?</summary>
    public static bool RiskyRun(World w, Side side, Body from, Body to) =>
        w.LossSpots.Any(l => l.Side == side && w.Hour - l.Hour < LossDays * 24 && DistToSegment(l.At, from.Position, to.Position) < LossRadiusMm);

    /// <summary>Enemy armed fleets seen near one of the side's runs under way: (enemy sighting, from, to).</summary>
    public static IEnumerable<(Sighting Enemy, Body From, Body To)> RaidersNearRuns(World w, Side side)
    {
        foreach (var s in w.Shipments.Where(s => s.Side == side).OrderBy(s => s.Id))
        {
            if (w.FindBody(s.FromBodyId) is not Body a || w.FindBody(s.ToBodyId) is not Body b) continue;
            foreach (var e in w.Sightings.Where(e => e.SeenBy == side && !e.Own && w.Hour - e.Hour < 6 && !CargoSighting(e)).OrderBy(e => e.FleetId))
                if (DistToSegment(e.Position, a.Position, b.Position) < LossRadiusMm) yield return (e, a, b);
        }
    }
}

/// <summary>Draw a zone (raid, guard or picket), optionally on a lane between two bodies (it slides with them).</summary>
public sealed record CreateZoneCommand(Side Side, ZoneKind Kind, Vec2 Center, double RadiusMm, int? FromBodyId = null, int? ToBodyId = null,
    double T = 0.5, ZoneHold Hold = ZoneHold.Drift, int? FleetId = null) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        var z = new Zone
        {
            Id = world.NextId++, Side = Side, Kind = Kind, Center = Center, RadiusMm = Math.Clamp(RadiusMm, 20, 2000),
            FromBodyId = FromBodyId, ToBodyId = ToBodyId, T = Math.Clamp(T, 0, 1), Hold = Hold,
        };
        z.Name = $"{(Kind == ZoneKind.Raid ? "Raiding" : Kind == ZoneKind.Guard ? "Guard" : "Picket")} zone {world.Zones.Count(o => o.Side == Side) + 1}";
        world.Zones.Add(z);
        Choices.Note(world, Side, "zone", Kind.ToString().ToLowerInvariant());
        sim.Log.Info("lanes", $"{Side} draws {z.Name}", new { side = Side.ToString(), zone = z.Id, Kind, Center, RadiusMm, FromBodyId, ToBodyId, Source });
        if (FleetId is int f) new SetZoneFleetCommand(f, z.Id) { Source = Source }.Apply(world, sim);
    }
}

/// <summary>Move or reshape a zone, change how it is held and whether it preys on passing ships.</summary>
public sealed record EditZoneCommand(Side Side, int ZoneId, Vec2 Center, double RadiusMm, int? FromBodyId, int? ToBodyId, double T, ZoneHold Hold, bool Prey) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.Zones.Find(z => z.Id == ZoneId && z.Side == Side) is not Zone z) return;
        z.Center = Center;
        z.RadiusMm = Math.Clamp(RadiusMm, 20, 2000);
        z.FromBodyId = FromBodyId;
        z.ToBodyId = ToBodyId;
        z.T = Math.Clamp(T, 0, 1);
        z.Hold = Hold;
        z.Prey = Prey;
        sim.Log.Info("lanes", $"{z.Name} changed", new { zone = z.Id, Center, RadiusMm, Hold, Prey, Source });
    }
}

public sealed record DeleteZoneCommand(Side Side, int ZoneId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        world.Zones.RemoveAll(z => z.Id == ZoneId && z.Side == Side);
        foreach (var f in world.Fleets.Where(f => f.ZoneId == ZoneId)) { f.ZoneId = null; f.Mission = Mission.None; }
    }
}

/// <summary>Give a fleet a zone to hold (Ambush mission), or none.</summary>
public sealed record SetZoneFleetCommand(int FleetId, int? ZoneId) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Zone order") is not Fleet f) return;
        if (ZoneId is int id && world.Zones.Find(z => z.Id == id && z.Side == f.Side) is null) return;
        f.ZoneId = ZoneId;
        f.Mission = ZoneId == null ? Mission.None : Mission.Ambush;
        f.MissionBodyId = null;
        sim.Log.Info("lanes", ZoneId == null ? $"{f.Name} leaves its zone" : $"{f.Name} holds a zone", new { FleetId, ZoneId, Source });
    }
}

/// <summary>Hunt: prey on cargo running between two enemy planets (a raiding zone on the run between them, sliding with them).</summary>
public sealed record HuntCommand(int FleetId, int FromBodyId, int ToBodyId) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Hunt order") is not Fleet f || world.FindBody(FromBodyId) is not Body a || world.FindBody(ToBodyId) is not Body b) return;
        var length = Vec2.Distance(a.Position, b.Position);
        new CreateZoneCommand(f.Side, ZoneKind.Raid, (a.Position + b.Position) * 0.5, Math.Clamp(length * 0.25, 100, 1500), a.Id, b.Id, 0.5, ZoneHold.Drift, f.Id) { Source = Source }.Apply(world, sim);
        f.Mission = Mission.Hunt;
    }
}
