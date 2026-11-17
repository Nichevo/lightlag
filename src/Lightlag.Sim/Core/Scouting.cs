namespace Lightlag.Sim.Core;

/// <summary>
/// The Scout role: a fleet that works on its own to survey worlds its side hasn't, then looks in on enemy
/// worlds where its intel is oldest. It is cautious: it keeps to high orbit, never goes where armed enemies were
/// seen lately or to a world with guns or a starbase, and turns for home at the first armed contact close by.
/// Player fleets run it from Autonomy; the rival's mind runs its scout the same way.
/// </summary>
public static class Scouting
{
    /// <summary>An armed enemy seen this close to the scout (AU) sends it home.</summary>
    public const double DangerAu = 0.4;

    /// <summary>A world is unsafe while an armed enemy was seen within this distance (AU) of it in the last SafeDays.</summary>
    public const double SafeAu = 0.3, SafeDays = 5;

    /// <summary>After running from danger, the scout lies low at home this long (hours).</summary>
    public const double LayLowHours = 48;

    /// <summary>Hours it looks at an enemy world (a survey takes the survey time instead).</summary>
    public const double LookHours = 6;

    /// <summary>An enemy world is looked at again only after this many days.</summary>
    public const double RelookDays = 10;

    /// <summary>Supplies below this share: home to restock first.</summary>
    public const double RestockShare = 1.0 / 3;

    /// <summary>A fleet that can only scout: no weapons, no cargo (the start scout).</summary>
    public static bool ScoutOnly(World w, Fleet f) =>
        f.ShipIds.Count > 0 && w.ShipsOf(f).All(s => GameData.ShipClasses.TryGetValue(s.ClassId, out var c) && c.Id == ScoutClass);

    public const string ScoutClass = "scout";

    /// <summary>A sighting of an enemy fleet that can fight (or whose ships are not all known).</summary>
    public static bool Armed(Sighting s) =>
        s.Ships > 0 && (s.Classes.Count < s.Ships || s.Classes.Any(c => GameData.ShipClasses.TryGetValue(c, out var k) && (k.Railguns > 0 || k.Missiles > 0)));

    /// <summary>Is the world safe for a scout: no armed enemy seen near it lately, and, if an enemy holds it, no guns or starbase there?</summary>
    public static bool Safe(World w, Side side, Body b)
    {
        if (w.Sightings.Any(s => s.SeenBy == side && !s.Own && Armed(s) && w.Hour - s.Hour <= SafeDays * 24
                                 && Vec2.Distance(s.Position, b.Position) < SafeAu * Physics.MmPerAu)) return false;
        if (b.Owner is Side o && o != side)
        {
            if (Colonies.Sum(w, b, k => k.GunDamage) > 0) return false;
            if (w.Starbases.Any(st => st.Side == o && st.BodyId == b.Id)) return false;
        }
        return true;
    }

    /// <summary>Worlds worth surveying: ones that are worth holding, that the side hasn't surveyed.</summary>
    public static bool WantsSurvey(Body b, Side side) => b.Industry > 0 && !b.Surveyed[(int)side];

    /// <summary>Where the scout goes next: the nearest safe world to survey, else the safe enemy world looked at longest ago.</summary>
    public static Body? NextStop(World w, Fleet f)
    {
        var side = f.Side;
        var survey = w.Bodies.Where(b => WantsSurvey(b, side) && Safe(w, side, b))
            .OrderBy(b => Vec2.Distance(b.Position, f.Position)).ThenBy(b => b.Id).FirstOrDefault();
        if (survey != null) return survey;
        return w.Bodies.Where(b => b.Owner is Side o && o != side && Safe(w, side, b)
                                   && (!f.ScoutLooked.TryGetValue(b.Id, out var at) || w.Hour - at >= RelookDays * 24))
            .OrderBy(b => f.ScoutLooked.TryGetValue(b.Id, out var at) ? at : double.MinValue)
            .ThenBy(b => Vec2.Distance(b.Position, f.Position)).ThenBy(b => b.Id).FirstOrDefault();
    }

    /// <summary>Once an hour for each scout-role fleet.</summary>
    public static void Work(Simulation sim, Fleet f)
    {
        var w = sim.World;
        if (f.ShipIds.Count == 0) return;
        if (f.Stance != Stance.Avoid) Do(sim, new SetStanceCommand(f.Id, Stance.Avoid, true));
        if (f.OrbitHeight != OrbitHeight.High) f.OrbitHeight = OrbitHeight.High;
        var hub = Hub(w, f);

        // Danger close by: run for home and lie low.
        var danger = w.Sightings.Where(s => s.SeenBy == f.Side && !s.Own && Armed(s) && (s.Visible || w.Hour - s.Hour <= 3)
                                            && Vec2.Distance(s.Guess(w.Hour), f.Position) < DangerAu * Physics.MmPerAu)
            .OrderBy(s => Vec2.Distance(s.Guess(w.Hour), f.Position)).ThenBy(s => s.FleetId).FirstOrDefault();
        if (danger != null && hub != null && f.MissionBodyId != hub.Id)
        {
            f.MissionBodyId = hub.Id;
            f.MissionUntil = w.Hour + LayLowHours;
            Fly(sim, f, hub, 1.0);
            Autonomy.Report(sim, f, $"{f.Name} saw armed {danger.Name} close by and turns for {hub.Name}.");
            return;
        }
        if (f.Flight != null || f.Maneuvering || f.Retreating || f.Chase != null || f.JoinFleetId != null) return;

        // Lying low, or waiting at a stop.
        if (f.MissionBodyId is int at && w.FindBody(at) is Body here && Economy.InOrbitAt(f, here))
        {
            if (WantsSurvey(here, f.Side) && Safe(w, f.Side, here)) return; // the survey is under way
            if (f.MissionUntil <= 0) { f.MissionUntil = w.Hour + LookHours; return; } // just arrived: look a while
            if (w.Hour < f.MissionUntil) return;
            if (here.Owner is Side o && o != f.Side) f.ScoutLooked[here.Id] = w.Hour;
        }

        // Low on supplies: home first.
        if (hub != null && Combat.SupplyShare(w, f) < RestockShare && !Economy.InOrbitAt(f, hub))
        {
            f.MissionBodyId = hub.Id;
            f.MissionUntil = w.Hour + 24;
            Fly(sim, f, hub, 0.6);
            return;
        }

        var next = NextStop(w, f);
        if (next == null)
        {
            // Nothing safe to do: wait at home and look again tomorrow.
            if (hub != null && !Economy.InOrbitAt(f, hub)) Fly(sim, f, hub, 0.6);
            f.MissionBodyId = hub?.Id;
            f.MissionUntil = w.Hour + 24;
            return;
        }
        f.MissionBodyId = next.Id;
        f.MissionUntil = 0; // the look starts when it arrives
        if (!Economy.InOrbitAt(f, next) && !Fly(sim, f, next, 0.6)) f.ScoutLooked[next.Id] = w.Hour; // can't get there: try others first
    }

    /// <summary>What the scout is doing, for its fleet panel: "Surveying Kesh" / "Looking at Rust (last seen 14 days ago)".</summary>
    public static string Status(World w, Fleet f)
    {
        if (f.MissionBodyId is not int id || w.FindBody(id) is not Body b) return "Scouting: choosing where to go";
        var arrived = Economy.InOrbitAt(f, b);
        if (b.Owner == f.Side && w.Hour < f.MissionUntil && arrived) return "Scouting: lying low at " + b.Name;
        if (WantsSurvey(b, f.Side))
            return arrived ? FormattableString.Invariant($"Surveying {b.Name}: {Math.Max(0, Research.T.SurveyHours - b.SurveyHours[(int)f.Side]):0} h left") : $"Scouting: on the way to survey {b.Name}";
        if (b.Owner is Side o && o != f.Side)
            return (arrived ? "Looking at " : "On the way to look at ") + b.Name
                   + (f.ScoutLooked.TryGetValue(b.Id, out var at) ? FormattableString.Invariant($" (last looked {(w.Hour - at) / 24:0} days ago)") : " (never looked at)");
        return arrived ? $"Scouting: at {b.Name}" : $"Scouting: on the way to {b.Name}";
    }

    /// <summary>The nearest of the side's hubs (home, shipyard or starbase world), else its home.</summary>
    private static Body? Hub(World w, Fleet f) =>
        Planets.Hubs(w, f.Side).OrderBy(h => Vec2.Distance(h.Position, f.Position)).ThenBy(h => h.Id).FirstOrDefault() ?? Economy.HomeOf(w, f.Side);

    private static bool Fly(Simulation sim, Fleet f, Body b, double speed)
    {
        Do(sim, new PlanFlightCommand(f.Id, new FlightRequest(b.Id, b.Position, speed, ArrivalStyle.Brake, HoldStyle.Orbit, sim.World.Tick)));
        if (f.Flight != null) return true;
        return speed > 0.3 && Fly(sim, f, b, 0.3);
    }

    private static void Do(Simulation sim, GameCommand command) => (command with { Source = "auto" }).Apply(sim.World, sim);
}
