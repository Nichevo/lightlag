namespace Lightlag.Sim.Core;

/// <summary>
/// Builds a new game from a system data file. Starts always have some randomness (where each body is
/// on its orbit, how ships are spread) taken from the seed, so one game's replay does not tell you
/// the next game.
/// </summary>
public static class Scenarios
{
    public const string TestSystem = "test-system";
    public const string BattleSandbox = "battle-sandbox";

    public static IReadOnlyList<string> All { get; } = new[] { TestSystem };

    public static World Create(string name, ulong seed, SimRandom rng, bool randomStarts = false)
    {
        if (!All.Contains(name)) throw new ArgumentException($"Unknown scenario '{name}'. Known: {string.Join(", ", All)}");
        var data = GameData.LoadSystem(name + ".json");
        if (randomStarts) data = RandomHomes(data, rng);
        var w = FromData(data, seed, rng);
        Economy.Start(w);
        return w;
    }

    /// <summary>
    /// The battle sandbox: the test system for scenery, and two fleets set up by hand facing each other
    /// in open space between Verdant's and Rust's orbits. Homes stay as usual, so damaged fleets can retreat.
    /// </summary>
    public static World Sandbox(SandboxSetup setup, ulong seed, SimRandom rng)
    {
        var data = GameData.LoadSystem(TestSystem + ".json");
        var w = FromData(data with { Ships = new() }, seed, rng);
        w.Scenario = BattleSandbox;
        Economy.AddShipyard(w.Bodies.Single(b => b.Key == "verdant"), Side.Player);
        Economy.AddShipyard(w.Bodies.Single(b => b.Key == "rust"), Side.Rival);
        w.MissileBrain[(int)Side.Player] = Math.Clamp(setup.PlayerBrain, 1, 2);
        w.MissileBrain[(int)Side.Rival] = Math.Clamp(setup.RivalBrain, 1, 2);

        // Open space at 1.25 AU, on a line facing each other, with a little random sideways offset.
        var center = new Vec2(0, -1.25 * Physics.MmPerAu);
        var half = Math.Max(1, setup.DistanceMm) / 2;
        var jitter = new Vec2(0, rng.Range(-2.0, 2.0));
        AddSandboxFleet(w, Side.Player, setup.PlayerFrigates, setup.PlayerDestroyers, center + new Vec2(-half, 0), setup.PlayerStance);
        AddSandboxFleet(w, Side.Rival, setup.RivalFrigates, setup.RivalDestroyers, center + new Vec2(half, 0) + jitter, setup.RivalStance);

        // Both sides start knowing where the other fleet is (scouted before the battle), but not whether
        // it can still be seen: that is up to the sensors from here on.
        foreach (var f in w.Fleets)
            foreach (var other in w.Fleets.Where(o => o.Side != f.Side))
                if (setup.RivalKnowsWhere || f.Side == Side.Player) Sensors.Reveal(w, f.Side, other);
        return w;
    }

    private static void AddSandboxFleet(World w, Side side, int frigates, int destroyers, Vec2 at, Stance stance)
    {
        var ships = new List<Ship>();
        void Add(string classId, int count)
        {
            for (var i = 1; i <= count; i++) ships.Add(Economy.NewShip(w, side, classId));
        }
        Add("gun-frigate", Math.Clamp(frigates, 0, 12));
        Add("missile-destroyer", Math.Clamp(destroyers, 0, 12));
        if (ships.Count == 0) return;
        var fleet = Fleets.Create(w, side, ships, at);
        fleet.Stance = stance;
        Fleets.EnterDrift(fleet);
    }

    /// <summary>
    /// Random starts: each side's start ships (and so its home and shipyard) move to a different planet
    /// that can hold a home's people (a planet, not a moon or giant, of a type that holds at least 30k)
    /// and has a moon to start a mining outpost on (see Planets.EnsureStarted).
    /// </summary>
    public static SystemData RandomHomes(SystemData data, SimRandom rng)
    {
        var homes = data.Bodies.Where(b => b.Kind == BodyKind.Planet && HomeTypes.Contains(b.Type)
                                           && data.Bodies.Any(m => m.Parent == b.Id && m.Kind == BodyKind.Moon)).Select(b => b.Id).ToList();
        var sides = (data.Ships ?? new()).Select(s => s.Side).Distinct().ToList();
        if (homes.Count < sides.Count) return data;
        var picked = new Dictionary<Side, string>();
        foreach (var side in sides)
        {
            var at = rng.Range(0, homes.Count);
            picked[side] = homes[at];
            homes.RemoveAt(at);
        }
        return data with { Ships = data.Ships!.Select(s => s with { Near = picked[s.Side] }).ToList() };
    }

    /// <summary>World types a home can be on.</summary>
    public static readonly string[] HomeTypes = { "terran", "desert", "cloudy", "rocky" };

    /// <summary>How close a moon can orbit its planet: outside the Roche limit and clear of the surface.</summary>
    public static double ClosestMoonOrbit(BodyData planet, BodyData moon, double scale)
    {
        var roche = moon.Gm > 0 ? moon.RadiusMm * scale * DetMath.Cbrt(2 * planet.Gm / moon.Gm) : 0;
        return Math.Max(roche, SystemData.MoonClearance * planet.RadiusMm * scale);
    }

    public static World FromData(SystemData data, ulong seed, SimRandom rng)
    {
        var w = new World { Scenario = data.Name, Seed = seed };
        var ids = new Dictionary<string, Body>();
        var rocks = new SimRandom(seed, 77);

        var scale = data.SizeScale > 0 ? data.SizeScale : 1;
        // Enlarged planets push their moons out, by gravity: a moon can't orbit inside its Roche limit (the
        // planet's tides would tear it apart) or skim the planet. All of a planet's moons move out by the same
        // factor, so they keep their order; orbit times still follow from the planet's mass.
        var push = new Dictionary<string, double>();
        foreach (var group in data.Bodies.Where(d => d.Parent != null && data.Bodies.First(p => p.Id == d.Parent).Parent != null).GroupBy(d => d.Parent!))
        {
            var planet = data.Bodies.First(p => p.Id == group.Key);
            push[group.Key] = Math.Max(1, group.Max(m => ClosestMoonOrbit(planet, m, scale) / m.OrbitRadiusMm));
        }

        foreach (var d in data.Bodies)
        {
            var parent = d.Parent != null ? ids[d.Parent] : null;
            var radius = d.OrbitRadiusMm * (d.Parent != null && push.TryGetValue(d.Parent, out var k) ? k : 1);
            var b = new Body
            {
                Id = w.NextId++, Key = d.Id, Name = d.Name, Kind = d.Kind, Type = d.Type,
                Color = d.Color, Atmosphere = d.Atmosphere, Gm = d.Gm, Radius = d.RadiusMm * scale,
                ParentId = parent?.Id,
                OrbitRadius = parent != null ? radius : 0,
                OrbitPeriodDays = parent != null ? Physics.OrbitPeriodHours(radius, parent.Gm) / 24 : 0,
                // Asteroids came later: they take their places from a stream of their own, so older seeds keep their maps.
                Angle = parent != null ? (d.Kind == BodyKind.Asteroid ? rocks : rng).Range(0, Math.PI * 2) : 0,
                Industry = d.Industry ?? 0,
            };
            ids[d.Id] = b;
            w.Bodies.Add(b);
        }

        foreach (var belt in data.Belts ?? new())
        {
            var parent = ids[belt.Parent];
            var radii = belt.InnerRadii > 0;
            w.Belts.Add(new Belt
            {
                Key = belt.Id, Name = belt.Name, ParentId = parent.Id, Color = belt.Color, Kind = belt.Kind, Density = belt.Density,
                InnerRadius = radii ? belt.InnerRadii * parent.Radius : belt.InnerAu * Physics.MmPerAu,
                OuterRadius = radii ? belt.OuterRadii * parent.Radius : belt.OuterAu * Physics.MmPerAu,
            });
        }

        Simulation.UpdateBodyPositions(w);

        // Each side's ships start as one fleet per start body, in orbit, and that body is their home with a shipyard.
        // Ships given a fleet name (the home transport) make a fleet of their own there.
        foreach (var start in (data.Ships ?? new()).OrderBy(g => g.Fleet != null).GroupBy(g => (g.Side, g.Near, g.Fleet)))
        {
            var near = ids[start.Key.Near];
            if (!near.Shipyard && start.Key.Fleet == null) Economy.AddShipyard(near, start.Key.Side);
            var ships = new List<Ship>();
            foreach (var group in start)
                for (var i = 0; i < group.Count; i++) ships.Add(Economy.NewShip(w, group.Side, group.Class));
            var fleet = Fleets.Create(w, start.Key.Side, ships, near.Position);
            fleet.Hold = HoldStyle.Orbit;
            fleet.OrbitBodyId = near.Id;
            fleet.OrbitRadius = Fleets.OrbitRadiusFor(near);
            fleet.OrbitAngle = rng.Range(0, Math.PI * 2);
            fleet.Position = near.Position + new Vec2(DetMath.Cos(fleet.OrbitAngle), DetMath.Sin(fleet.OrbitAngle)) * fleet.OrbitRadius;
            Fleets.Arrange(w, fleet);
        }
        return w;
    }
}

/// <summary>What the battle sandbox sets up: each side's ships, missile brains and standing orders, and how far apart.</summary>
public sealed record SandboxSetup(
    int PlayerFrigates = 3, int PlayerDestroyers = 1, int RivalFrigates = 2, int RivalDestroyers = 2,
    int PlayerBrain = 1, int RivalBrain = 1, Stance PlayerStance = Stance.Engage, Stance RivalStance = Stance.Hold,
    double DistanceMm = 150, bool RivalKnowsWhere = true);
