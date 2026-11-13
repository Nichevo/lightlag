using Lightlag.Sim.Core;

namespace Lightlag.Sim.Tests;

/// <summary>Test helpers for the start setup.</summary>
public static class Starts
{
    /// <summary>
    /// The start without the supply tenders (the one in each war fleet and the home transport) and the starting
    /// freighter, for tests written about fleet mechanics with the plain three-warship fleets.
    /// </summary>
    public static Simulation Classic(Simulation sim)
    {
        var w = sim.World;
        foreach (var s in w.Ships.Where(s => s.ClassId is "supply-tender" or "freighter").ToList())
        {
            w.FleetOf(s)?.ShipIds.Remove(s.Id);
            w.Ships.Remove(s);
        }
        w.Fleets.RemoveAll(f => f.ShipIds.Count == 0);
        // And without the home moons' outposts.
        Planets.EnsureStarted(w);
        foreach (var moon in w.Bodies.Where(b => b.Kind == BodyKind.Moon && b.Owner != null))
        {
            moon.Owner = null;
            moon.PopK = 0;
            moon.Pieces.Clear();
            moon.Stock.Clear();
            Colonies.SyncBuildings(moon);
        }
        return sim;
    }
}
