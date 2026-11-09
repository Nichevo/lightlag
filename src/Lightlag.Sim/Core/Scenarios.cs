namespace Lightlag.Sim.Core;

/// <summary>
/// Hand-made test maps. Starts always have some randomness (planet positions, ship spread) taken
/// from the seed, so knowing one game's replay does not tell you the next game.
/// </summary>
public static class Scenarios
{
    public const string TestSystem = "test-system";

    public static IReadOnlyList<string> All { get; } = new[] { TestSystem };

    public static World Create(string name, ulong seed, SimRandom rng) => name switch
    {
        TestSystem => CreateTestSystem(seed, rng),
        _ => throw new ArgumentException($"Unknown scenario '{name}'. Known: {string.Join(", ", All)}"),
    };

    private static World CreateTestSystem(ulong seed, SimRandom rng)
    {
        var w = new World { Scenario = TestSystem, Seed = seed };
        int Id() => w.NextId++;

        var sun = new Body { Id = Id(), Name = "Sol", Kind = BodyKind.Star, Radius = 696 };
        w.Bodies.Add(sun);

        Body Planet(string name, double orbitMm, double periodDays, double radius, Body parent, BodyKind kind = BodyKind.Planet)
        {
            var b = new Body
            {
                Id = Id(), Name = name, Kind = kind, ParentId = parent.Id,
                OrbitRadius = orbitMm, OrbitPeriodDays = periodDays, Radius = radius,
                Angle = rng.Range(0, Math.PI * 2),
            };
            w.Bodies.Add(b);
            return b;
        }

        Planet("Cinder", 58_000, 88, 2.4, sun);
        var home = Planet("Verdant", 150_000, 365, 6.4, sun);
        Planet("Pale", 384, 27.3, 1.7, home, BodyKind.Moon);
        var rival = Planet("Rust", 228_000, 687, 3.4, sun);
        Planet("Tethys", 778_000, 4333, 70, sun);

        Simulation.UpdateBodyPositions(w);

        void Ships(Body near, Side side, string prefix)
        {
            for (var i = 0; i < 2; i++)
            {
                var offset = new Vec2(rng.Range(-2000.0, 2000.0), rng.Range(-2000.0, 2000.0));
                w.Ships.Add(new Ship
                {
                    Id = Id(), Name = $"{prefix} {i + 1}", Side = side,
                    Position = near.Position + offset, MaxSpeed = 500,
                });
            }
        }

        Ships(home, Side.Player, "Frigate");
        Ships(rival, Side.Rival, "Raider");
        return w;
    }
}
