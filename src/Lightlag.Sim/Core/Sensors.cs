using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>What a fleet is doing, as far as enemy sensors can tell.</summary>
public enum SignatureState { Cold, Orbit, Burn, Warm }

/// <summary>
/// What one side knows about one enemy fleet: where it was and how it moved when last seen. While
/// the fleet is in sight the sighting is refreshed every step; after that it goes stale and is
/// forgotten after a while (or once the side sees that the fleet is gone).
/// </summary>
public sealed class Sighting
{
    public Side SeenBy { get; set; }

    /// <summary>A report on one of the side's own fleets (only in a light-lag picture, see Light).</summary>
    public bool Own { get; set; }

    public int FleetId { get; set; }
    public string Name { get; set; } = "";
    public int Ships { get; set; }
    public Vec2 Position { get; set; }
    public Vec2 Velocity { get; set; }
    public SignatureState State { get; set; }

    /// <summary>When it was last seen.</summary>
    public double Hour { get; set; }

    /// <summary>When it most recently came into sight (to notice new contacts).</summary>
    public double SpottedHour { get; set; }

    /// <summary>In sight right now.</summary>
    public bool Visible { get; set; }

    /// <summary>
    /// Each ship's class, sorted, once the fleet has been seen close enough to tell (empty until then).
    /// Kept from the last close look, so it can be out of date if the fleet has changed since.
    /// </summary>
    public List<string> Classes { get; set; } = new();

    /// <summary>When the classes were last seen (meaningless while Classes is empty).</summary>
    public double ClassesHour { get; set; }

    /// <summary>Whether it is taken to carry the enemy flagship: the belief, how strong, and the ship once confirmed.</summary>
    public FlagKnown Flag { get; set; }
    public double FlagScore { get; set; }
    public int? FlagShipId { get; set; }

    /// <summary>Where it would be at this hour if it kept going as last seen.</summary>
    public Vec2 Guess(double hour) => Position + Velocity * Math.Max(0, hour - Hour);

    public Sighting Copy()
    {
        var c = (Sighting)MemberwiseClone();
        c.Classes = new List<string>(Classes);
        return c;
    }
}

/// <summary>
/// Passive sensors. Every fleet has a signature: tiny when cold (drifting or coasting with engines
/// off), a little more holding orbit, more while its engines are still warm after a burn, and huge
/// with the main engines burning. A sensor sees a fleet
/// out to its range times the square root of that signature, unless a planet, moon or the star is in
/// the way. Each side's fleets and its home planet carry sensors. Combat only targets what is seen.
/// </summary>
public static class Sensors
{
    private static SensorData S => GameData.Sensors;

    public static SignatureState StateOf(Fleet f)
    {
        if (f.Thrusting) return SignatureState.Burn;
        if (f.Warm) return SignatureState.Warm;
        if (f.Hold == HoldStyle.Orbit && f.OrbitBodyId != null && f.Flight is not { Started: true }) return SignatureState.Orbit;
        return SignatureState.Cold;
    }

    public static double StateFactor(SignatureState state) => state switch
    {
        SignatureState.Burn => S.Burn,
        SignatureState.Orbit => S.Orbit,
        SignatureState.Warm => S.Warm,
        _ => S.Cold,
    };

    /// <summary>The fleet's size on sensors before what it is doing: its largest ship, plus a little for each other ship.</summary>
    public static double SizeOf(World w, Fleet f)
    {
        double largest = 0;
        var count = 0;
        foreach (var s in w.ShipsOf(f))
        {
            largest = Math.Max(largest, Fitting.Spec(s).SignatureSize);
            count++;
        }
        return count == 0 ? 0 : largest * (1 + S.ExtraShip * (count - 1));
    }

    public static double Signature(World w, Fleet f)
    {
        var state = StateOf(f);
        var quiet = state == SignatureState.Burn ? Tech.Factor(w, f.Side, Tech.QuietDrives) * Fuels.Signature(w, f) : 1;
        var solo = state == SignatureState.Cold && f.ShipIds.Count == 1 ? S.SoloCold : 1;
        return SizeOf(w, f) * solo * StateFactor(state) * quiet * (state == SignatureState.Burn ? 1 : Heat.SignatureFactor(w, f))
               * Tech.SignatureFactor(w, f.Side, state == SignatureState.Burn);
    }

    /// <summary>The fleet's best sensor range (for a signature of 1).</summary>
    public static double SensorOf(World w, Fleet f)
    {
        double best = 0;
        foreach (var s in w.ShipsOf(f)) best = Math.Max(best, Fitting.Spec(s).SensorMm);
        return best * Tech.Factor(w, f.Side, Tech.LongSensors);
    }

    /// <summary>
    /// How much further sensors see a fleet because its track is known: CuedFactor while it is still near
    /// where the sighting says it should be (the search box grows the longer ago it was seen), else 1.
    /// This also holds a contact once made: a fleet in sight stays in sight out to the cued range, so a
    /// fleet sitting near the edge of spotting range does not flicker in and out.
    /// </summary>
    public static double Cue(World w, Sighting? s, Vec2 actual, double hour)
    {
        if (s == null || S.CuedFactor <= 1) return 1;
        return Vec2.Distance(s.Guess(hour), actual) <= CueRadius(s, hour) ? S.CuedFactor : 1;
    }

    public static double CueRadius(Sighting s, double hour) => S.CueRadiusMm + S.CueGrowthMmPerHour * Math.Max(0, hour - s.Hour);

    /// <summary>How close one of this side's fleets or planets must be to tell an enemy ship's class.</summary>
    public static double IdentifyRange(World w, Side side) => S.IdentifyMm * Tech.Factor(w, side, Tech.LongSensors) * Tech.Factor(w, side, Tech.SignalsAnalysis);

    public static List<string> ClassesOf(World w, Fleet f) => w.ShipsOf(f).Select(s => s.ClassId).OrderBy(c => c, StringComparer.Ordinal).ToList();

    /// <summary>"1 Gun frigate, 2 Missile destroyers" from a sighting's classes, or null if not identified yet.</summary>
    public static string? Describe(Sighting s)
    {
        if (s.Classes.Count == 0) return null;
        return string.Join(", ", s.Classes.GroupBy(c => c).Select(g =>
        {
            var name = GameData.ShipClasses.TryGetValue(g.Key, out var k) ? k.Name : g.Key;
            return $"{g.Count()} {name}{(g.Count() > 1 ? "s" : "")}";
        }));
    }

    /// <summary>How far a sensor of this strength sees a fleet with this signature.</summary>
    public static double Range(double sensorMm, double signature) => sensorMm * Math.Sqrt(Math.Max(0, signature));

    /// <summary>Is the straight line between two points clear of every planet, moon and the star?</summary>
    public static bool LineOfSight(World w, Vec2 from, Vec2 to, int? ignoreBodyId = null, int? alsoIgnore = null)
    {
        var d = to - from;
        var len2 = Vec2.Dot(d, d);
        foreach (var b in w.Bodies)
        {
            if (b.Id == ignoreBodyId || b.Id == alsoIgnore) continue;
            var t = len2 > 1e-18 ? Math.Clamp(Vec2.Dot(b.Position - from, d) / len2, 0, 1) : 0;
            if (t <= 0 || t >= 1) continue; // the body is not between them
            var closest = from + d * t;
            if (Vec2.Distance(closest, b.Position) < b.Radius) return false;
        }
        return true;
    }

    /// <summary>Can this side see that fleet right now?</summary>
    /// <summary>
    /// Is this fleet in sight of the side? By default, as its fleets out there see it (for fighting and
    /// chasing); with seat set, as the side's command knows it, which under light lag is the picture the
    /// last reports carried home (for the player's map and the rival's plans).
    /// </summary>
    public static bool Sees(World w, Side side, Fleet f, bool seat = false) =>
        f.Side == side || (Find(w, side, f.Id, seat) is { Visible: true });

    public static Sighting? Find(World w, Side seenBy, int fleetId, bool seat = false) =>
        Known(w, seat).Find(s => s.SeenBy == seenBy && s.FleetId == fleetId);

    /// <summary>The sightings as the fleets see them now, or (seat) as command knows them (see Light).</summary>
    public static List<Sighting> Known(World w, bool seat) => seat && w.Light.On ? w.Picture : w.Sightings;

    /// <summary>
    /// Where this side expects an enemy fleet to be at an hour: circling with it if it is seen holding
    /// orbit, otherwise in a straight line from where it was last seen. Null if it has never been seen.
    /// </summary>
    public static Vec2? Predict(World w, Side side, int fleetId, double hour, bool seat = false)
    {
        if (w.FindFleet(fleetId) is Fleet f && f.Side == side) return Combat.PredictFleet(w, f, hour);
        var s = Find(w, side, fleetId, seat);
        if (s == null) return null;
        if (s.Visible && s.State == SignatureState.Orbit && (!seat || !w.Light.On) && w.FindFleet(fleetId) is { Hold: HoldStyle.Orbit } orbiting)
            return FlightPlanner.PredictHoldPosition(w, orbiting, hour);
        return s.Guess(hour);
    }

    /// <summary>How far a body the side holds sees: full range at the shipyard or with a sensor station, else like a ship.</summary>
    public static double PlanetSensorOf(World w, Side side, Body b)
    {
        var station = Economy.Stations(w, side, Economy.SensorStation).Any(x => x.Body.Id == b.Id)
                      || Colonies.Built(b).Any(p => Planets.BuildingKind(p.Kind)?.Sees == true && p.Powered);
        var full = b.Shipyard || S.PlanetBareSensorMm <= 0 || station;
        // Deep-space telescopes: stations and arrays see half again as far.
        return (full ? S.PlanetSensorMm : S.PlanetBareSensorMm) * Tech.Factor(w, side, Tech.LongSensors)
               * (station ? Tech.Factor(w, side, "deep-space-telescopes") : 1);
    }

    /// <summary>How a fleet is really moving now: a fleet in orbit moves with its body (its Velocity is zero).</summary>
    public static Vec2 MotionOf(World w, Fleet f) =>
        f.Flight == null && f.Hold == HoldStyle.Orbit ? FlightPlanner.PredictHoldPosition(w, f, w.Hour + 1) - FlightPlanner.PredictHoldPosition(w, f, w.Hour) : f.Velocity;

    /// <summary>
    /// Does this enemy fleet look as if it is coming for that fleet? Judged only from what was seen: its
    /// track, relative to the fleet, closes on it and passes close by. Returns the hours until it gets
    /// there, or null. Ignores enemies that are barely moving or more than a month away.
    /// </summary>
    public static double? HeadingFor(Sighting s, Fleet mine, double hour, Vec2? mineVelocity = null)
    {
        var them = s.Guess(hour);
        var gap = mine.Position - them;
        var d = gap.Length;
        var rel = s.Velocity - (mineVelocity ?? mine.Velocity);
        if (d < 1e-6 || rel.Length < 5) return null;
        var closing = Vec2.Dot(gap, rel) / d;
        if (closing <= rel.Length * 0.5) return null;
        var t = Vec2.Dot(gap, rel) / Vec2.Dot(rel, rel); // hours to the closest approach
        var miss = Vec2.Distance(them + rel * t, mine.Position);
        if (miss > Math.Max(GameData.Combat.Missile.LaunchRangeMm, d * 0.05)) return null;
        var eta = d / closing;
        return eta > 30 * 24 ? null : eta;
    }

    /// <summary>Works out what each side sees now, and refreshes or ages its sightings.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        var now = w.Hour;
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var observers = new List<(Vec2 At, double Sensor, int? Body)>();
            foreach (var f in w.Fleets.Where(f => f.Side == side))
                if (f.Position.IsFinite) observers.Add((f.Position, SensorOf(w, f), null));
            foreach (var b in w.Bodies.Where(b => b.Owner == side))
                observers.Add((b.Position, PlanetSensorOf(w, side, b), b.Id));

            foreach (var enemy in w.Fleets.Where(f => f.Side != side))
            {
                if (!enemy.Position.IsFinite) continue;
                var signature = Signature(w, enemy);
                var cue = Cue(w, Find(w, side, enemy.Id), enemy.Position, now);
                var nearest = double.PositiveInfinity;
                foreach (var o in observers)
                {
                    var d = Vec2.Distance(o.At, enemy.Position);
                    if (d <= Range(o.Sensor, signature) * cue && d < nearest && LineOfSight(w, o.At, enemy.Position, o.Body)) nearest = d;
                }
                var seen = !double.IsPositiveInfinity(nearest);
                var sighting = Find(w, side, enemy.Id);
                if (!seen)
                {
                    if (sighting != null) sighting.Visible = false;
                    continue;
                }
                if (sighting == null)
                {
                    sighting = new Sighting { SeenBy = side, FleetId = enemy.Id };
                    w.Sightings.Add(sighting);
                }
                if (!sighting.Visible)
                {
                    sighting.SpottedHour = now;
                    sim.Log.Info("sensors", $"{side} spots {enemy.Name}", new
                    {
                        side = side.ToString(), fleetId = enemy.Id, state = StateOf(enemy).ToString(), at = enemy.Position,
                    });
                }
                sighting.Visible = true;
                sighting.Name = enemy.Name;
                sighting.Ships = enemy.ShipIds.Count;
                sighting.Position = enemy.Position;
                sighting.Velocity = enemy.Velocity;
                sighting.State = StateOf(enemy);
                sighting.Hour = now;
                if (nearest <= IdentifyRange(w, side))
                {
                    sighting.Classes = ClassesOf(w, enemy);
                    sighting.ClassesHour = now;
                }
            }

            // A track that points somewhere your sensors watch clearly, with nothing there, is wrong: the
            // fleet turned, stopped or merged out of sight. Drop it rather than show a ghost to fight.
            foreach (var lost in w.Sightings.Where(s => s.SeenBy == side && !s.Visible && now - s.Hour >= LostTrackAfterHours
                                                          && LookedAndNotThere(w, observers, s.Guess(now))).ToList())
            {
                w.Sightings.Remove(lost);
                sim.Log.Info("sensors", $"{side} loses track of {lost.Name}", new { side = side.ToString(), fleetId = lost.FleetId, lastSeenHour = lost.Hour });
            }
        }

        // Forget fleets seen to be gone (destroyed or merged in sight) and very old sightings.
        w.Sightings.RemoveAll(s => now - s.Hour > S.ForgetAfterHours
                                   || (w.FindFleet(s.FleetId) == null && s.Visible));
        foreach (var s in w.Sightings.Where(s => w.FindFleet(s.FleetId) == null)) s.Visible = false;
    }

    /// <summary>How long after last seeing a fleet an empty predicted spot counts as a lost track.</summary>
    public const double LostTrackAfterHours = 2;

    /// <summary>Would a fleet coasting cold at this spot be seen, so its absence there means the track is wrong?</summary>
    private static bool LookedAndNotThere(World w, List<(Vec2 At, double Sensor, int? Body)> observers, Vec2 at)
    {
        if (!at.IsFinite) return false;
        foreach (var o in observers)
            if (Vec2.Distance(o.At, at) <= Range(o.Sensor, S.Cold) && LineOfSight(w, o.At, at, o.Body)) return true;
        return false;
    }

    /// <summary>Records that a side knows where an enemy fleet is (sandbox starts, intelligence later).</summary>
    public static void Reveal(World w, Side to, Fleet enemy)
    {
        var s = Find(w, to, enemy.Id);
        if (s == null) { s = new Sighting { SeenBy = to, FleetId = enemy.Id }; w.Sightings.Add(s); }
        s.Name = enemy.Name;
        s.Ships = enemy.ShipIds.Count;
        s.Position = enemy.Position;
        s.Velocity = enemy.Velocity;
        s.State = StateOf(enemy);
        s.Hour = w.Hour;
        s.SpottedHour = w.Hour;
        s.Classes = ClassesOf(w, enemy);
        s.ClassesHour = w.Hour;
    }
}
