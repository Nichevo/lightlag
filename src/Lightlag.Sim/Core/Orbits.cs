namespace Lightlag.Sim.Core;

/// <summary>
/// Game pace for orbits. True Kepler periods made planets run at a quarter to a half of a ship's speed and the
/// gas giant's moons faster still, so every trip chased a moving target. Orbits are slowed at game start: moons
/// always by MoonSlowdown, planets by the start choice (default 5x). Ship speeds, distances and ships' own
/// orbits round a body are unchanged. Slower orbits also mean weaker slingshots (they feed on the planet's speed).
/// </summary>
public static class Orbits
{
    /// <summary>Moons (anything round a planet) always go round this many times slower than true time.</summary>
    public const double MoonSlowdown = 5;

    /// <summary>Planets (anything round the sun) go round this many times slower unless the start choice says otherwise.</summary>
    public const double DefaultPlanetSlowdown = 5;

    /// <summary>The planet slowdowns offered at game start.</summary>
    public static readonly double[] PlanetChoices = { 1, 3, 5 };

    /// <summary>Stretch every orbit's period once, when a new game is made. Positions now are untouched.</summary>
    public static void Slow(World w, double planetSlowdown)
    {
        planetSlowdown = Math.Clamp(planetSlowdown, 1, 20);
        w.Rules.PlanetOrbitSlowdown = planetSlowdown;
        foreach (var b in w.Bodies)
        {
            if (b.ParentId is not int pid || b.OrbitPeriodDays <= 0) continue;
            var parent = w.FindBody(pid);
            b.OrbitPeriodDays *= parent?.ParentId != null ? MoonSlowdown : planetSlowdown;
        }
    }
}
