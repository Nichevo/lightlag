using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>What a fleet is doing, as far as enemy sensors can tell.</summary>
public enum SignatureState { Cold, Orbit, Burn }

/// <summary>
/// What one side knows about one enemy fleet: where it was and how it moved when last seen. While
/// the fleet is in sight the sighting is refreshed every step; after that it goes stale and is
/// forgotten after a while (or once the side sees that the fleet is gone).
/// </summary>
public sealed class Sighting
{
    public Side SeenBy { get; set; }
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

    /// <summary>Where it would be at this hour if it kept going as last seen.</summary>
    public Vec2 Guess(double hour) => Position + Velocity * Math.Max(0, hour - Hour);
}

/// <summary>
/// Passive sensors. Every fleet has a signature: tiny when cold (drifting or coasting with engines
/// off), a little more holding orbit, and huge with the main engines burning. A sensor sees a fleet
/// out to its range times the square root of that signature, unless a planet, moon or the star is in
/// the way. Each side's fleets and its home planet carry sensors. Combat only targets what is seen.
/// </summary>
public static class Sensors
{
    private static SensorData S => GameData.Sensors;

    public static SignatureState StateOf(Fleet f)
    {
        if (f.Thrusting) return SignatureState.Burn;
        if (f.Hold == HoldStyle.Orbit && f.OrbitBodyId != null && f.Flight is not { Started: true }) return SignatureState.Orbit;
        return SignatureState.Cold;
    }

    public static double StateFactor(SignatureState state) => state switch
    {
        SignatureState.Burn => S.Burn,
        SignatureState.Orbit => S.Orbit,
        _ => S.Cold,
    };

    /// <summary>The fleet's size on sensors before what it is doing: its largest ship, plus a little for each other ship.</summary>
    public static double SizeOf(World w, Fleet f)
    {
        double largest = 0;
        var count = 0;
        foreach (var s in w.ShipsOf(f))
        {
            largest = Math.Max(largest, GameData.ShipClass(s.ClassId).SignatureSize);
            count++;
        }
        return count == 0 ? 0 : largest * (1 + S.ExtraShip * (count - 1));
    }

    public static double Signature(World w, Fleet f) => SizeOf(w, f) * StateFactor(StateOf(f));

    /// <summary>The fleet's best sensor range (for a signature of 1).</summary>
    public static double SensorOf(World w, Fleet f)
    {
        double best = 0;
        foreach (var s in w.ShipsOf(f)) best = Math.Max(best, GameData.ShipClass(s.ClassId).SensorMm);
        return best;
    }

    /// <summary>How far a sensor of this strength sees a fleet with this signature.</summary>
    public static double Range(double sensorMm, double signature) => sensorMm * Math.Sqrt(Math.Max(0, signature));

    /// <summary>Is the straight line between two points clear of every planet, moon and the star?</summary>
    public static bool LineOfSight(World w, Vec2 from, Vec2 to, int? ignoreBodyId = null)
    {
        var d = to - from;
        var len2 = Vec2.Dot(d, d);
        foreach (var b in w.Bodies)
        {
            if (b.Id == ignoreBodyId) continue;
            var t = len2 > 1e-18 ? Math.Clamp(Vec2.Dot(b.Position - from, d) / len2, 0, 1) : 0;
            if (t <= 0 || t >= 1) continue; // the body is not between them
            var closest = from + d * t;
            if (Vec2.Distance(closest, b.Position) < b.Radius) return false;
        }
        return true;
    }

    /// <summary>Can this side see that fleet right now?</summary>
    public static bool Sees(World w, Side side, Fleet f) =>
        f.Side == side || (Find(w, side, f.Id) is { Visible: true });

    public static Sighting? Find(World w, Side seenBy, int fleetId) =>
        w.Sightings.Find(s => s.SeenBy == seenBy && s.FleetId == fleetId);

    /// <summary>
    /// Where this side expects an enemy fleet to be at an hour: circling with it if it is seen holding
    /// orbit, otherwise in a straight line from where it was last seen. Null if it has never been seen.
    /// </summary>
    public static Vec2? Predict(World w, Side side, int fleetId, double hour)
    {
        if (w.FindFleet(fleetId) is Fleet f && f.Side == side) return Combat.PredictFleet(w, f, hour);
        var s = Find(w, side, fleetId);
        if (s == null) return null;
        if (s.Visible && s.State == SignatureState.Orbit && w.FindFleet(fleetId) is { Hold: HoldStyle.Orbit } orbiting)
            return FlightPlanner.PredictHoldPosition(w, orbiting, hour);
        return s.Guess(hour);
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
                observers.Add((b.Position, S.PlanetSensorMm, b.Id));

            foreach (var enemy in w.Fleets.Where(f => f.Side != side))
            {
                if (!enemy.Position.IsFinite) continue;
                var signature = Signature(w, enemy);
                var seen = observers.Any(o => Vec2.Distance(o.At, enemy.Position) <= Range(o.Sensor, signature)
                                              && LineOfSight(w, o.At, enemy.Position, o.Body));
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
            }
        }

        // Forget fleets seen to be gone (destroyed or merged in sight) and very old sightings.
        w.Sightings.RemoveAll(s => now - s.Hour > S.ForgetAfterHours
                                   || (w.FindFleet(s.FleetId) == null && s.Visible));
        foreach (var s in w.Sightings.Where(s => w.FindFleet(s.FleetId) == null)) s.Visible = false;
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
    }
}
