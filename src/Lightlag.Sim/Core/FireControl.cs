namespace Lightlag.Sim.Core;

/// <summary>
/// Firing rules: what a fleet's weapons go for. Nearest (the default); the flagship (if known or
/// suspected); the largest ship; the weakest (finish off what is nearly dead); engines (fewer hits, but
/// hits knock out engines far more often, so the target cannot run or close); split fire (each ship takes
/// a different target, spreading the damage and the dodging).
/// </summary>
public static class FireControl
{
    /// <summary>The ship this one's railguns (or missiles) go for under its fleet's rule, within range; null if none in reach.</summary>
    public static Ship? Pick(Simulation sim, Ship ship, Fleet fleet, double range)
    {
        var w = sim.World;
        if (fleet.FireAt == FireAt.Flagship && Flagships.FlagTarget(sim, ship, range) is Ship flag) return flag;
        var targets = InReach(w, ship, range);
        if (targets.Count == 0) return null;
        switch (fleet.FireAt)
        {
            case FireAt.Largest:
                return targets.OrderByDescending(s => Fitting.Spec(s).Cost).ThenBy(s => Vec2.Distance(s.Position, ship.Position)).ThenBy(s => s.Id).First();
            case FireAt.Weakest:
                return targets.OrderBy(s => s.Armour + s.Shield).ThenBy(s => s.Id).First();
            case FireAt.Split:
                var ordered = targets.OrderBy(s => s.Id).ToList();
                var index = Math.Max(0, fleet.ShipIds.IndexOf(ship.Id));
                return ordered[index % ordered.Count];
            default:
                return targets.OrderBy(s => Vec2.Distance(s.Position, ship.Position)).ThenBy(s => s.Id).First();
        }
    }

    /// <summary>Hit chance factor for the rule: aiming for engines means fewer hits.</summary>
    public static double AimFactor(Fleet f) => f.FireAt == FireAt.Engines ? GameData.Heat.EnginesHitFactor : 1;

    /// <summary>Enemy ships this ship's side can see, within range.</summary>
    public static List<Ship> InReach(World w, Ship ship, double range)
    {
        var seen = w.Fleets.Where(f => f.Side != ship.Side && Sensors.Sees(w, ship.Side, f)).Select(f => f.Id).ToHashSet();
        return w.Ships.Where(s => s.Side != ship.Side && seen.Contains(s.FleetId) && Vec2.Distance(s.Position, ship.Position) <= range).ToList();
    }

    public static string Name(FireAt f) => f switch
    {
        FireAt.Flagship => "Go for the flagship",
        FireAt.Largest => "Largest first",
        FireAt.Weakest => "Finish off",
        FireAt.Engines => "Engines",
        FireAt.Split => "Split fire",
        _ => "Nearest",
    };
}
