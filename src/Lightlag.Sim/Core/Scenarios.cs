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

        var counters = new Dictionary<string, int>();
        foreach (var group in data.Ships ?? new())
        {
            var cls = GameData.ShipClass(group.Class);
            var near = ids[group.Near];
            for (var i = 0; i < group.Count; i++)
            {
                var key = $"{group.Side}:{cls.Id}";
                counters[key] = counters.GetValueOrDefault(key) + 1;
                // Somewhere in high orbit around the start body, a few thousand km out.
                var angle = rng.Range(0, Math.PI * 2);
                var dist = near.Radius * 3 + rng.Range(0.0, 2000.0);
                var ship = new Ship
                {
                    Id = w.NextId++,
                    Name = $"{cls.Short}-{(group.Side == Side.Player ? "" : "R")}{counters[key]}",
                    Side = group.Side, ClassId = cls.Id, MaxSpeed = cls.MaxSpeedMmPerHour,
                    Position = near.Position + new Vec2(DetMath.Cos(angle), DetMath.Sin(angle)) * dist,
                    HoldBodyId = near.Id,
                };
                ship.HoldOffset = ship.Position - near.Position;
                w.Ships.Add(ship);
            }
        }
        return w;
    }
}
