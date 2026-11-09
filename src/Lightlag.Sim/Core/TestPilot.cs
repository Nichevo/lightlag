namespace Lightlag.Sim.Core;

/// <summary>
/// A stand-in player for headless runs until the real rival AI exists (M6): every so often it sends
/// ships to random bodies. It issues ordinary commands, so its games replay like any other.
/// Its own random numbers are separate from the world's, so it never changes how the world rolls.
/// </summary>
public sealed class TestPilot
{
    private readonly SimRandom _rng;

    public TestPilot(ulong seed) => _rng = new SimRandom(seed ^ 0x9E3779B97F4A7C15UL, stream: 7);

    public double OrderChancePerDay { get; set; } = 0.3;

    /// <summary>Call once per tick, before Step.</summary>
    public void Act(Simulation sim)
    {
        var world = sim.World;
        if (world.Tick % GameTime.TicksPerDay != 0) return;
        foreach (var ship in world.Ships)
        {
            if (!_rng.Chance(OrderChancePerDay)) continue;
            var targets = world.Bodies.Where(b => b.Kind != BodyKind.Star).ToList();
            var target = targets[_rng.Range(0, targets.Count)];
            sim.Issue(new MoveShipCommand(ship.Id, target.Position.X, target.Position.Y) { Source = "test-pilot" });
        }
    }
}
