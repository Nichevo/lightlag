namespace Lightlag.Sim.Core;

/// <summary>
/// Builds a new game from a system data file. Starts always have some randomness (where each body is
/// on its orbit, how ships are spread) taken from the seed, so one game's replay does not tell you
/// the next game.
/// </summary>
public static class Scenarios
{
    public const string TestSystem = "test-system";

    public static IReadOnlyList<string> All { get; } = new[] { TestSystem };

    public static World Create(string name, ulong seed, SimRandom rng)
    {
        if (!All.Contains(name)) throw new ArgumentException($"Unknown scenario '{name}'. Known: {string.Join(", ", All)}");
        return FromData(GameData.LoadSystem(name + ".json"), seed, rng);
    }

    public static World FromData(SystemData data, ulong seed, SimRandom rng)
    {
        var w = new World { Scenario = data.Name, Seed = seed };
        var ids = new Dictionary<string, Body>();

        foreach (var d in data.Bodies)
        {
            var parent = d.Parent != null ? ids[d.Parent] : null;
            var radius = d.OrbitRadiusMm;
            var b = new Body
            {
                Id = w.NextId++, Key = d.Id, Name = d.Name, Kind = d.Kind, Type = d.Type,
                Color = d.Color, Atmosphere = d.Atmosphere, Gm = d.Gm, Radius = d.RadiusMm,
                ParentId = parent?.Id,
                OrbitRadius = parent != null ? radius : 0,
                OrbitPeriodDays = parent != null ? Physics.OrbitPeriodHours(radius, parent.Gm) / 24 : 0,
                Angle = parent != null ? rng.Range(0, Math.PI * 2) : 0,
            };
            ids[d.Id] = b;
            w.Bodies.Add(b);
        }

        foreach (var belt in data.Belts ?? new())
        {
            w.Belts.Add(new Belt
            {
                Key = belt.Id, Name = belt.Name, ParentId = ids[belt.Parent].Id, Color = belt.Color,
                InnerRadius = belt.InnerAu * Physics.MmPerAu, OuterRadius = belt.OuterAu * Physics.MmPerAu,
            });
        }

        Simulation.UpdateBodyPositions(w);

        // Each side's ships start as one fleet per start body, in orbit, and that body is their home.
        var counters = new Dictionary<string, int>();
        foreach (var start in (data.Ships ?? new()).GroupBy(g => (g.Side, g.Near)))
        {
            var near = ids[start.Key.Near];
            near.Owner = start.Key.Side;
            var ships = new List<Ship>();
            foreach (var group in start)
            {
                var cls = GameData.ShipClass(group.Class);
                for (var i = 0; i < group.Count; i++)
                {
                    var key = $"{group.Side}:{cls.Id}";
                    counters[key] = counters.GetValueOrDefault(key) + 1;
                    var ship = new Ship
                    {
                        Id = w.NextId++,
                        Name = $"{cls.Short}-{(group.Side == Side.Player ? "" : "R")}{counters[key]}",
                        Side = group.Side, ClassId = cls.Id,
                        MaxSpeed = cls.MaxSpeedMmPerHour, Accel = cls.AccelMmPerHour2, Supplies = cls.Supplies,
                    };
                    ships.Add(ship);
                    w.Ships.Add(ship);
                }
            }
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
