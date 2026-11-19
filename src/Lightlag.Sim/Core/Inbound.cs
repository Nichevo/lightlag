namespace Lightlag.Sim.Core;

/// <summary>
/// Early warning: enemy fleets whose seen course points at one of the side's planets or fleets, worked out from
/// command's picture (what the player's map shows). A fleet seen burning away from a far planet, then lost
/// once it coasts cold, still leaves a track: while that track is fresh it still counts, marked out of sight.
/// </summary>
public static class Inbound
{
    /// <summary>A track this old or newer still warns of a fleet heading for a planet (trips take weeks).</summary>
    public const double PlanetTrackHours = 10 * 24;

    /// <summary>A track this old or newer still warns of a fleet heading for one of your fleets (fleets move, so less).</summary>
    public const double FleetTrackHours = 48;

    /// <summary>Further off than this is not worth a warning yet.</summary>
    public const double MaxHours = 45 * 24;

    /// <summary>One enemy fleet heading for one of the side's planets: when it gets there, and how fresh the news is.</summary>
    public sealed record ToPlanet(int FleetId, string Name, int Ships, int BodyId, string BodyName, double Hours, bool InSight, double SeenHoursAgo);

    /// <summary>Every known enemy fleet whose course points at one of the side's planets, soonest first.</summary>
    public static List<ToPlanet> Planets(World w, Side side)
    {
        var list = new List<ToPlanet>();
        foreach (var s in Sensors.Known(w, seat: true))
        {
            if (s.SeenBy != side || s.Own || w.Hour - s.Hour > PlanetTrackHours) continue;
            if (Chases.HeadingFor(w, side, s, seat: true, nearestPass: true) is not var (body, hours) || hours > MaxHours) continue;
            list.Add(new ToPlanet(s.FleetId, s.Name, s.Ships, body.Id, body.Name, hours, s.Visible, Math.Max(0, w.Hour - s.Hour)));
        }
        return list.OrderBy(t => t.Hours).ThenBy(t => t.FleetId).ToList();
    }

    /// <summary>Is this sighting fresh enough to warn that it is heading for one of the side's fleets?</summary>
    public static bool FreshForFleets(World w, Sighting s) => s.Visible || w.Hour - s.Hour <= FleetTrackHours;
}
