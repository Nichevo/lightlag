namespace Lightlag.Sim.Core;

/// <summary>Light lag as chosen at game start: on or off, and how fast light is (1 = real, 0.1 = ten times slower).</summary>
public sealed record LightSetup(bool On = true, double SpeedFactor = 1.0);

/// <summary>Numbers for light lag (ship-classes.json, "light").</summary>
public sealed record LightData(double SpeedKmPerSecond = 299792.458, double CommandRangeAu = 0.2);

/// <summary>Light lag for this game, plus the orders still on their way.</summary>
public sealed class LightState
{
    public bool On { get; set; }
    public double SpeedFactor { get; set; } = 1;

    /// <summary>Orders sent but not yet arrived, oldest first.</summary>
    public List<Transit> InTransit { get; set; } = new();

    /// <summary>Sighting reports on their way home to each side's command.</summary>
    public List<Report> Reports { get; set; } = new();

    /// <summary>When each report stream ("side:fleetId") last sent, and whether it was in sight then.</summary>
    public Dictionary<string, ReportMark> LastSent { get; set; } = new();
}

/// <summary>A sighting (or own fleet's position) travelling home at light speed. Gone: the fleet is no longer tracked.</summary>
public sealed class Report
{
    public Side To { get; set; }
    public double ArriveHour { get; set; }
    public bool Gone { get; set; }
    public Sighting Snapshot { get; set; } = new();
}

public sealed record ReportMark(double Hour, bool Visible);

/// <summary>An order travelling at light speed from a side's flagship to where it takes effect.</summary>
public sealed class Transit
{
    public Side Side { get; set; }
    public double SentHour { get; set; }
    public double ArriveHour { get; set; }
    public GameCommand Command { get; set; } = null!;
}

/// <summary>
/// Light lag. Each side runs its empire from its flagship: a ship that starts at home and flies with
/// one fleet. Orders leave the flagship at light speed and take effect when they arrive; fleets within
/// the command range of the flagship are close enough to count as live (no lag). With light lag off
/// every delay is zero and the game plays as before.
/// </summary>
public static class Light
{
    public static LightData Data => GameData.Light;

    /// <summary>Light speed in megametres per game hour, for this game.</summary>
    public static double MmPerHour(World w) => Data.SpeedKmPerSecond * 3.6 * Math.Max(1e-6, w.Light.SpeedFactor);

    /// <summary>The command range in Mm (fleets this close to the flagship have no lag).</summary>
    public static double CommandRangeMm(World w, Side side) => Data.CommandRangeAu * Physics.MmPerAu;

    /// <summary>The side's flagship, if it still has one.</summary>
    public static Ship? Flagship(World w, Side side) =>
        Economy.Of(w, side).FlagshipId is int id && w.FindShip(id) is Ship s && s.Side == side ? s : null;

    /// <summary>Where the side's command sits: its flagship, else its shipyard, else the star.</summary>
    public static Vec2 Seat(World w, Side side)
    {
        if (Flagship(w, side) is Ship s && w.FleetOf(s) is Fleet f) return f.Position;
        return Economy.ShipyardOf(w, side)?.Position ?? Vec2.Zero;
    }

    /// <summary>Hours for a signal between the side's flagship and this point (0 within command range or with lag off).</summary>
    public static double DelayHours(World w, Side side, Vec2 at)
    {
        if (!w.Light.On || !at.IsFinite) return 0;
        var d = Vec2.Distance(Seat(w, side), at);
        return d <= CommandRangeMm(w, side) ? 0 : d / MmPerHour(w);
    }

    /// <summary>
    /// Can a signal pass straight between the side's flagship and this point? Planets, moons and the star
    /// block it; the body the flagship itself orbits does not (it is always just round the corner).
    /// </summary>
    public static bool SignalClear(World w, Side side, Vec2 at)
    {
        if (!w.Light.On) return true;
        var seat = Seat(w, side);
        var home = w.Bodies.Where(b => Vec2.Distance(b.Position, seat) < b.Radius * 2 + 1).Select(b => (int?)b.Id).FirstOrDefault();
        return Sensors.LineOfSight(w, seat, at, home);
    }

    /// <summary>Picks each side's flagship at the start: the first destroyer at home, else the first ship.</summary>
    public static void ChooseFlagships(World w)
    {
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var mine = w.Ships.Where(s => s.Side == side).ToList();
            var pick = mine.FirstOrDefault(s => s.ClassId == "missile-destroyer") ?? mine.FirstOrDefault();
            Economy.Of(w, side).FlagshipId = pick?.Id;
        }
    }

    /// <summary>
    /// The flagship was lost: command moves to the nearest surviving destroyer (else any ship), or to
    /// home if none is left.
    /// </summary>
    public static void Succeed(Simulation sim, Side side, Vec2 lostAt)
    {
        var w = sim.World;
        var next = w.Ships.Where(s => s.Side == side)
            .OrderBy(s => s.ClassId == "missile-destroyer" ? 0 : 1)
            .ThenBy(s => Vec2.Distance(s.Position, lostAt)).ThenBy(s => s.Id).FirstOrDefault();
        Economy.Of(w, side).FlagshipId = next?.Id;
        sim.Log.Info("light", next != null ? $"{side} command passes to {next.Name}" : $"{side} command falls back to home",
            new { side = side.ToString(), shipId = next?.Id });
    }

    /// <summary>Sends an order: it takes effect now if the target is live, else it is queued until its light arrives.</summary>
    public static bool Send(Simulation sim, GameCommand command)
    {
        var w = sim.World;
        if (!w.Light.On || command.Destination(w) is not (Side side, Vec2 at)) return false;
        var delay = DelayHours(w, side, at);
        if (delay <= 0) return false;
        w.Light.InTransit.Add(new Transit { Side = side, SentHour = w.Hour, ArriveHour = w.Hour + delay, Command = command });
        sim.Log.Info("light", $"{side} order on its way", new { side = side.ToString(), command = command.GetType().Name, delayHours = delay });
        return true;
    }

    /// <summary>How often a moving contact sends a fresh report home (hours).</summary>
    public const double ReportEveryHours = 0.1;

    /// <summary>
    /// Sends home what each side's fleets and planets see now, and its own fleets' positions, as reports
    /// that arrive when their light does; then updates each command's picture with the reports that
    /// have arrived. Does nothing with light lag off.
    /// </summary>
    public static void Relay(Simulation sim)
    {
        var w = sim.World;
        if (!w.Light.On) return;
        var now = w.Hour;
        var marks = w.Light.LastSent;
        var live = new HashSet<string>();
        void Send(Sighting snap)
        {
            var key = $"{(int)snap.SeenBy}:{snap.FleetId}";
            live.Add(key);
            var delay = DelayHours(w, snap.SeenBy, snap.Position);
            // A planet, moon or the star between it and the flagship blocks the signal: no news until it clears.
            if (delay > 0 && !SignalClear(w, snap.SeenBy, snap.Position)) { live.Add(key); return; }
            // Live news (within command range) goes every step; news from afar every few minutes.
            if (marks.TryGetValue(key, out var last) && last.Visible == snap.Visible
                && (snap.Hour <= last.Hour || delay > 0 && snap.Hour - last.Hour < ReportEveryHours - 1e-9)) return;
            if (last != null && !snap.Visible && !last.Visible) return; // nothing new about a fleet out of sight
            marks[key] = new ReportMark(snap.Hour, snap.Visible);
            w.Light.Reports.Add(new Report { To = snap.SeenBy, ArriveHour = now + delay, Snapshot = snap.Copy() });
        }
        foreach (var s in w.Sightings) Send(s);
        foreach (var f in w.Fleets)
        {
            if (!f.Position.IsFinite) continue;
            Send(new Sighting
            {
                SeenBy = f.Side, Own = true, FleetId = f.Id, Name = f.Name, Ships = f.ShipIds.Count, Position = f.Position,
                Velocity = f.Velocity, State = Sensors.StateOf(f), Hour = now, SpottedHour = now, Visible = true,
                Classes = Sensors.ClassesOf(w, f), ClassesHour = now,
            });
        }
        // Streams that stopped (a fleet destroyed, merged or lost track of) send a last word: gone.
        foreach (var key in marks.Keys.Where(k => !live.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            var parts = key.Split(':');
            var side = (Side)int.Parse(parts[0]);
            var id = int.Parse(parts[1]);
            marks.Remove(key);
            var at = Known(w, side, id)?.Position ?? Seat(w, side);
            w.Light.Reports.Add(new Report
            {
                To = side, Gone = true, ArriveHour = now + DelayHours(w, side, at),
                Snapshot = new Sighting { SeenBy = side, FleetId = id, Hour = now },
            });
        }

        // Reports that have arrived update the picture: newer news replaces older, whatever order it comes in.
        var due = w.Light.Reports.Where(r => r.ArriveHour <= now + 1e-9).ToList();
        if (due.Count == 0) return;
        w.Light.Reports.RemoveAll(r => r.ArriveHour <= now + 1e-9);
        foreach (var r in due.OrderBy(r => r.ArriveHour))
        {
            var snap = r.Snapshot;
            var i = w.Picture.FindIndex(p => p.SeenBy == r.To && p.FleetId == snap.FleetId);
            if (r.Gone)
            {
                if (i >= 0 && w.Picture[i].Hour <= snap.Hour) w.Picture.RemoveAt(i);
                continue;
            }
            if (i < 0) w.Picture.Add(snap);
            else if (w.Picture[i].Hour <= snap.Hour)
            {
                // Coming into sight: keep the time it was first spotted from the report itself.
                w.Picture[i] = snap;
            }
        }
    }

    /// <summary>What a side's command knows of a fleet (its own or an enemy's), under light lag.</summary>
    public static Sighting? Known(World w, Side side, int fleetId) => w.Picture.Find(p => p.SeenBy == side && p.FleetId == fleetId);

    /// <summary>How old the side's news of a fleet is, in hours (0 when there is no lag).</summary>
    public static double AgeHours(World w, Side side, int fleetId) =>
        !w.Light.On ? 0 : Known(w, side, fleetId) is Sighting s ? Math.Max(0, w.Hour - s.Hour) : double.PositiveInfinity;

    /// <summary>Carries out every order whose light has arrived, in the order they arrive.</summary>
    public static void Deliver(Simulation sim)
    {
        var w = sim.World;
        if (w.Light.InTransit.Count == 0) return;
        // An order whose fleet is behind a body waits until the line clears (it keeps its place in the queue).
        var due = w.Light.InTransit.Where(t => t.ArriveHour <= w.Hour + 1e-9
                                               && (t.Command.Destination(w) is not (Side side, Vec2 at) || SignalClear(w, side, at)))
            .OrderBy(t => t.ArriveHour).ToList();
        foreach (var t in due)
        {
            w.Light.InTransit.Remove(t);
            t.Command.Apply(w, sim);
        }
    }
}
