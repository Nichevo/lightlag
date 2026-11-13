namespace Lightlag.Sim.Core;

/// <summary>
/// A careless stand-in player for headless runs and tests (it plays any side the computer rival does not run): every so often it sends
/// fleets to random bodies (sometimes to claim a planet or attack the enemy shipyard), splits, merges and
/// gathers them, and queues ships and research. It issues ordinary commands, so its games replay like any other.
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
        if (world.Loop) Manage(sim);
        foreach (var fleet in world.Fleets.Where(f => Mind.Of(world, f.Side) == null).ToList())
        {
            // A fleet part way through claiming a planet usually stays to finish.
            if (fleet.OrbitBodyId is int at && world.FindBody(at) is { ClaimSide: Side claimer } && claimer == fleet.Side && _rng.Chance(0.85)) continue;
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
            var home = Economy.HomeOf(world, fleet.Side);
            var target = home != null && _rng.Chance(0.3) ? home : targets[_rng.Range(0, targets.Count)];
            var enemyYard = Economy.ShipyardOf(world, Sides.MainEnemy(world, fleet.Side));
            if (roll == 6 && enemyYard != null) target = enemyYard;
            var unclaimed = Economy.Claimable(world).Where(b => b.Owner != fleet.Side && !b.Shipyard).ToList();
            if (roll == 7 && unclaimed.Count > 0) target = unclaimed[_rng.Range(0, unclaimed.Count)];
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
            if (roll is 6 or 7) request = request with { Arrival = ArrivalStyle.Brake, HoldAfter = HoldStyle.Orbit };
            if (!FlightPlanner.Plan(world, fleet, request).Ok) continue;
            if (roll == 4 && request.Arrival == ArrivalStyle.Brake)
                sim.Issue(new SendAheadCommand(fleet.Id, request) { Source = "test-pilot" });
            else
                sim.Issue(new PlanFlightCommand(fleet.Id, request) { Source = "test-pilot" });
        }
    }

    /// <summary>Spends Industry like a careless player: queues ships and research, now and then cancels one.</summary>
    private void Manage(Simulation sim)
    {
        var world = sim.World;
        foreach (var side in Sides.InPlay(world).Where(s => Mind.Of(world, s) == null))
        {
            var orders = world.Orders.Where(o => o.Side == side).ToList();
            if (orders.Count(o => o.Kind == OrderKind.Ship) < 2 && _rng.Chance(0.5))
            {
                var cls = _rng.Chance(0.6) ? "gun-frigate" : "missile-destroyer";
                if (Economy.CanQueue(world, side, OrderKind.Ship, cls) == null)
                    sim.Issue(new QueueOrderCommand(side, OrderKind.Ship, cls) { Source = "test-pilot" });
            }
            if (!orders.Any(o => o.Kind == OrderKind.Research))
            {
                var open = GameData.Economy.Research.Where(r => Economy.CanQueue(world, side, OrderKind.Research, r.Id) == null).ToList();
                if (open.Count > 0)
                    sim.Issue(new QueueOrderCommand(side, OrderKind.Research, open[_rng.Range(0, open.Count)].Id) { Source = "test-pilot" });
            }
            var teams = Economy.Of(world, side).Teams;
            if (_rng.Chance(0.02))
            {
                var field = Research.Fields[_rng.Range(0, Research.Fields.Length)];
                var team = teams.Count > 0 ? teams[_rng.Range(0, teams.Count)] : null;
                GameCommand? cmd = _rng.Range(0, 5) switch
                {
                    0 => new FoundTeamCommand(side, field),
                    1 when team != null => new TrainTeamCommand(side, team.Id),
                    2 when team != null => new SplitTeamCommand(side, team.Id, field),
                    3 when team != null => new SetTeamKeptCommand(side, team.Id, new List<string> { field }),
                    4 when team != null && orders.FirstOrDefault(o => o.Kind == OrderKind.Research) is WorkOrder r => new SetOrderTeamCommand(side, r.Id, team.Id),
                    _ => null,
                };
                if (cmd != null) sim.Issue(cmd with { Source = "test-pilot" });
            }
            if (orders.Count > 0 && _rng.Chance(0.03))
                sim.Issue(new CancelOrderCommand(side, orders[_rng.Range(0, orders.Count)].Id) { Source = "test-pilot" });
        }
    }
}
