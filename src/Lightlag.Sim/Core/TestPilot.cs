namespace Lightlag.Sim.Core;

/// <summary>
/// A stand-in player for headless runs until the real rival AI exists (M6): every so often it sends
/// fleets to random bodies, splits, merges and gathers them. It issues ordinary commands, so its games replay like any other.
/// Its own random numbers are separate from the world's, so it never changes how the world rolls.
/// </summary>
public sealed class TestPilot
{
    private readonly SimRandom _rng;

    public TestPilot(ulong seed) => _rng = new SimRandom(seed ^ 0x9E3779B97F4A7C15UL, stream: 7);

    public double OrderChancePerDay { get; set; } = 0.3;

    /// <summary>Play the game with this pilot until the given hour (finishing any battle-time steps).</summary>
    public void PlayUntil(Simulation sim, long tick)
    {
        while (sim.World.Tick < tick || sim.World.Sub != 0)
        {
            Act(sim);
            sim.Step();
        }
    }

    /// <summary>Call before each Step.</summary>
    public void Act(Simulation sim)
    {
        var world = sim.World;
        if (world.Tick % GameTime.TicksPerDay != 0 || world.Sub != 0) return;
        foreach (var fleet in world.Fleets.ToList())
        {
            if (!_rng.Chance(OrderChancePerDay)) continue;
            var roll = _rng.Range(0, 12);
            if (roll == 0 && fleet.ShipIds.Count > 1)
            {
                sim.Issue(new SplitFleetCommand(fleet.Id, new List<int> { fleet.ShipIds[^1] }) { Source = "test-pilot" });
                continue;
            }
            if (roll == 1)
            {
                var friend = world.Fleets.FirstOrDefault(o => Fleets.CanMerge(fleet, o) == null);
                if (friend != null) sim.Issue(new MergeFleetsCommand(fleet.Id, friend.Id) { Source = "test-pilot" });
                continue;
            }
            if (roll == 3)
            {
                var friend = world.Fleets.FirstOrDefault(o => o.Side == fleet.Side && o.Id != fleet.Id);
                if (friend != null) sim.Issue(new GatherFleetsCommand(fleet.Id, new List<int> { friend.Id }, _rng.Range(0.3, 1.0)) { Source = "test-pilot" });
                continue;
            }
            if (roll == 2 && fleet.Velocity.Length > 0) { sim.Issue(new StopFleetCommand(fleet.Id) { Source = "test-pilot" }); continue; }
            // Mostly: fly somewhere, often home to refill.
            var targets = world.Bodies.Where(b => b.Kind != BodyKind.Star).ToList();
            var home = world.Bodies.FirstOrDefault(b => b.Owner == fleet.Side);
            var target = home != null && _rng.Chance(0.3) ? home : targets[_rng.Range(0, targets.Count)];
            var request = new FlightRequest(target.Id, target.Position, _rng.Range(0.2, 1.0),
                _rng.Chance(0.25) ? ArrivalStyle.FastPass : ArrivalStyle.Brake,
                _rng.Chance(0.8) ? HoldStyle.Orbit : HoldStyle.Drift,
                world.Tick + (_rng.Chance(0.2) ? _rng.Range(0, 10) * GameTime.TicksPerDay : 0));
            // Like a player, it only sends flights the planner says it can fly.
            if (roll == 5)
            {
                // A detour: through (or stopping at) a point off to one side of the way there.
                var mid = (fleet.Position + target.Position) * 0.5 + new Vec2(_rng.Range(-1.0, 1.0), _rng.Range(-1.0, 1.0)) * 20000;
                request = request with { Waypoints = new List<Waypoint> { new(mid, _rng.Chance(0.5), _rng.Chance(0.5) ? 0.2 : null, _rng.Chance(0.3) ? 12 : 0) } };
            }
            if (!FlightPlanner.Plan(world, fleet, request).Ok) continue;
            if (roll == 4 && request.Arrival == ArrivalStyle.Brake)
                sim.Issue(new SendAheadCommand(fleet.Id, request) { Source = "test-pilot" });
            else
                sim.Issue(new PlanFlightCommand(fleet.Id, request) { Source = "test-pilot" });
        }
    }
}
