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

    public static World Create(string name, ulong seed, SimRandom rng, bool randomStarts = false, bool threeSides = false, MapSetup? map = null)
    {
        if (map is { Seeded: true } || name == MapMaker.Name) return CreateSeeded(map ?? new MapSetup(), seed, rng, threeSides);
        if (!All.Contains(name)) throw new ArgumentException($"Unknown scenario '{name}'. Known: {string.Join(", ", All)}");
        var data = GameData.LoadSystem(name + ".json");
        // The second rival's start ships only come in a three-race game.
        if (!threeSides) data = data with { Ships = (data.Ships ?? new()).Where(s => s.Side != Side.Third).ToList() };
        if (randomStarts) data = RandomHomes(data, rng);
        var w = FromData(data, seed, rng);

        Economy.Start(w);
        return w;
    }

    /// <summary>
    /// A seeded map (see MapMaker): the system and its fair homes come from the map's seed; which side gets which
    /// home, and where everything is on its orbit on day 1, from the game's seed. The start ships are Hesper's.
    /// </summary>
    private static World CreateSeeded(MapSetup map, ulong seed, SimRandom rng, bool threeSides)
    {
        var sides = threeSides ? new[] { Side.Player, Side.Rival, Side.Third } : new[] { Side.Player, Side.Rival };
        var made = MapMaker.Make(map, sides.Length);
        var homes = made.Homes.ToList();
        for (var i = homes.Count - 1; i > 0; i--)
        {
            var j = rng.Range(0, i + 1);
            (homes[i], homes[j]) = (homes[j], homes[i]);
        }
        if (map.SwapHomes && homes.Count >= 2) (homes[0], homes[1]) = (homes[1], homes[0]);
        var at = sides.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => homes[Math.Min(x.i, homes.Count - 1)]);
        var template = GameData.LoadSystem(TestSystem + ".json").Ships ?? new();
        var ships = template.Where(s => at.ContainsKey(s.Side)).Select(s => s with { Near = at[s.Side] }).ToList();
        var data = made.Data with { Ships = ships };
        GameData.Validate(data, MapMaker.Name);

        var w = FromData(data, seed, rng, spreadHomesMm: MapMaker.MinHomeDays(made.Type) * MapMaker.CruiseMmPerDay);
        w.Map = new MapInfo
        {
            Type = made.Type, Chosen = map.Type, Size = map.Size, Seed = map.Seed, SwapHomes = map.SwapHomes,
            StarLight = made.StarLight, FlareFactor = made.FlareFactor, HomeScores = made.Scores.Select(x => Math.Round(x, 1)).ToList(), Tries = made.Tries,
        };
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

    /// <summary>
    /// Builds the world from map data. spreadHomesMm (seeded maps): homes that start closer than this are turned
    /// half way round the star (their moons with them), so nobody starts on the rival's doorstep.
    /// </summary>
    public static World FromData(SystemData data, ulong seed, SimRandom rng, double spreadHomesMm = 0)
    {
        var w = new World { Scenario = data.Name, Seed = seed };
        if ((data.Ships ?? new()).Any(s => s.Side == Side.Third)) w.Sides = w.Sides.Append(new SideState { Side = Side.Third }).ToArray();
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
        if (spreadHomesMm > 0) SpreadHomes(w, data, ids, spreadHomesMm);

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
            // A fleet of supply ships (the home transport) starts on the Supply role (computer-run sides ignore roles).
            if (HelpCalls.SupplyOnly(w, fleet)) { fleet.Role = FleetRole.Supply; FleetNames.Refresh(w, fleet); }
            // The start scout works on its own from day one: surveys first, then a careful look at the rival.
            if (Scouting.ScoutOnly(w, fleet)) { fleet.Role = FleetRole.Scout; fleet.Stance = Stance.Avoid; fleet.OrbitHeight = OrbitHeight.High; FleetNames.Refresh(w, fleet); }
            Fleets.Arrange(w, fleet);
        }
        return w;
    }

    private static void SpreadHomes(World w, SystemData data, Dictionary<string, Body> ids, double minMm)
    {
        var homes = (data.Ships ?? new()).Where(s => s.Fleet == null).Select(s => s.Near).Distinct().Select(k => ids[k]).ToList();
        for (var round = 0; round < 6; round++)
        {
            var moved = false;
            for (var i = 0; i < homes.Count; i++)
                for (var j = i + 1; j < homes.Count; j++)
                {
                    if (Vec2.Distance(homes[i].Position, homes[j].Position) >= minMm) continue;
                    var turned = homes[j].Angle + Math.PI * (round % 2 == 0 ? 1 : 0.5);
                    homes[j].Angle = turned >= 2 * Math.PI ? turned - 2 * Math.PI : turned;
                    Simulation.UpdateBodyPositions(w);
                    moved = true;
                }
            if (!moved) return;
        }
    }
}

/// <summary>What the battle sandbox sets up: each side's ships, missile brains and standing orders, and how far apart.</summary>
public sealed record SandboxSetup(
    int PlayerFrigates = 3, int PlayerDestroyers = 1, int RivalFrigates = 2, int RivalDestroyers = 2,
    int PlayerBrain = 1, int RivalBrain = 1, Stance PlayerStance = Stance.Engage, Stance RivalStance = Stance.Hold,
    double DistanceMm = 150, bool RivalKnowsWhere = true);
