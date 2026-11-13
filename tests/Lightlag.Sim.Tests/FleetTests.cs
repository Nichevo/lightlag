using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Xunit;

namespace Lightlag.Sim.Tests;

public class FleetTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Starts.Classic(Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory()));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet PlayerFleet(Simulation sim) => sim.World.Fleets.First(f => f.Side == Side.Player);
    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);

    private static FlightRequest To(Body b, double speed = 1, ArrivalStyle arrival = ArrivalStyle.Brake, HoldStyle hold = HoldStyle.Orbit) =>
        new(b.Id, b.Position, speed, arrival, hold, 0);

    private static void StepUntilArrived(Simulation sim, Fleet f, int maxDays = 400)
    {
        var end = sim.World.Tick + maxDays * GameTime.TicksPerDay;
        while (sim.World.Tick < end && (f.InFlight || sim.PendingCommands.Count > 0)) sim.Step();
        Assert.False(f.InFlight, "fleet never arrived");
    }

    [Fact]
    public void Each_side_starts_with_one_fleet_in_orbit_at_home()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        Assert.Equal(3, fleet.ShipIds.Count);
        Assert.Equal(HoldStyle.Orbit, fleet.Hold);
        Assert.Equal(Side.Player, BodyNamed(sim, "verdant").Owner);
        Assert.Equal(2, sim.World.Fleets.Count);
    }

    [Fact]
    public void Fleets_in_orbit_circle_their_planet()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        var home = BodyNamed(sim, "verdant");
        var angle = fleet.OrbitAngle;
        sim.StepMany(30);
        Assert.InRange(Vec2.Distance(fleet.Position, home.Position), fleet.OrbitRadius - 1e-6, fleet.OrbitRadius + 1e-6);
        Assert.NotEqual(angle, fleet.OrbitAngle);
        // A low orbit: half a planet radius up, a full circle in about a day.
        Assert.InRange(fleet.OrbitRadius, home.Radius * 1.4, home.Radius * 1.6);
        var period = Fleets.OrbitPeriodHours(home, fleet.OrbitRadius);
        Assert.InRange(period, 12, 48);
    }

    [Fact]
    public void A_fleet_flies_at_the_pace_of_its_slowest_ship()
    {
        var sim = NewGame();
        var perf = FleetPerformance.Of(sim.World, PlayerFleet(sim));
        Assert.Equal(GameData.ShipClass("missile-destroyer").MaxSpeedMmPerHour, perf.MaxSpeed);
        Assert.Equal(GameData.ShipClass("missile-destroyer").AccelMmPerHour2, perf.Accel);
    }

    [Fact]
    public void Flying_to_a_planet_ends_in_orbit_there_on_time()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        var vela = BodyNamed(sim, "vela");
        var plan = FlightPlanner.Plan(sim.World, fleet, To(vela));
        Assert.True(plan.Ok, plan.Problem);
        sim.Issue(new PlanFlightCommand(fleet.Id, To(vela)));
        StepUntilArrived(sim, fleet);
        Assert.Equal(HoldStyle.Orbit, fleet.Hold);
        Assert.Equal(vela.Id, fleet.OrbitBodyId);
        Assert.InRange(fleet.OrbitRadius, Fleets.OrbitRadiusFor(vela) * 0.95, Fleets.OrbitRadiusFor(vela) * 1.05);
        Assert.InRange((double)sim.World.Tick, plan.Flight!.ArriveHour, plan.Flight.ArriveHour + 2);
        Assert.All(sim.World.ShipsOf(fleet), s => Assert.True(s.Supplies < GameData.ShipClass(s.ClassId).Supplies));
    }

    [Fact]
    public void Faster_flights_arrive_sooner_and_burn_more_fuel()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        var target = BodyNamed(sim, "vela");
        var slow = FlightPlanner.Plan(sim.World, fleet, To(target, 0.4));
        var fast = FlightPlanner.Plan(sim.World, fleet, To(target, 1.0));
        Assert.True(fast.Flight!.ArriveHour < slow.Flight!.ArriveHour);
        Assert.True(fast.DeltaV > slow.DeltaV);
    }

    [Fact]
    public void A_fast_pass_is_cheaper_and_leaves_the_fleet_drifting_on()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        var target = BodyNamed(sim, "vela");
        var brake = FlightPlanner.Plan(sim.World, fleet, To(target));
        var pass = FlightPlanner.Plan(sim.World, fleet, To(target, arrival: ArrivalStyle.FastPass));
        Assert.True(pass.DeltaV < brake.DeltaV);
        Assert.True(pass.Flight!.ArriveHour < brake.Flight!.ArriveHour);
        sim.Issue(new PlanFlightCommand(fleet.Id, To(target, arrival: ArrivalStyle.FastPass)));
        StepUntilArrived(sim, fleet);
        Assert.Equal(HoldStyle.Drift, fleet.Hold);
        Assert.True(fleet.Velocity.Length > 100);
        var at = fleet.Position;
        sim.StepMany(10);
        Assert.True(Vec2.Distance(at, fleet.Position) > 1000);
    }

    [Fact]
    public void Stopping_a_moving_fleet_brakes_it()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        sim.Issue(new PlanFlightCommand(fleet.Id, To(BodyNamed(sim, "tamber"))));
        sim.StepMany(3 * GameTime.TicksPerDay);
        Assert.True(fleet.Velocity.Length > 0);
        sim.Issue(new StopFleetCommand(fleet.Id));
        StepUntilArrived(sim, fleet);
        Assert.Equal(0, fleet.Velocity.Length, 9);
        var at = fleet.Position;
        sim.StepMany(24);
        Assert.Equal(at, fleet.Position);
    }

    [Fact]
    public void Fleets_cannot_fly_further_than_their_supplies()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        foreach (var s in sim.World.ShipsOf(fleet)) s.Supplies = 1;
        var plan = FlightPlanner.Plan(sim.World, fleet, To(BodyNamed(sim, "tamber")));
        Assert.False(plan.Ok);
        Assert.Contains("supplies", plan.Problem);
        sim.Issue(new PlanFlightCommand(fleet.Id, To(BodyNamed(sim, "tamber"))));
        sim.Step();
        Assert.False(fleet.InFlight);
    }

    [Fact]
    public void Fleets_refill_in_orbit_at_home()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        foreach (var s in sim.World.ShipsOf(fleet)) s.Supplies = 1;
        sim.StepMany(10 * GameTime.TicksPerDay);
        Assert.All(sim.World.ShipsOf(fleet), s => Assert.Equal(GameData.ShipClass(s.ClassId).Supplies, s.Supplies, 6));
    }

    [Fact]
    public void Waiting_never_makes_the_trip_longer()
    {
        var sim = NewGame(21);
        var fleet = PlayerFleet(sim);
        foreach (var key in new[] { "cinder", "vela", "rust", "tamber" })
        {
            var req = To(BodyNamed(sim, key), 0.6);
            var direct = FlightPlanner.Plan(sim.World, fleet, req);
            var wait = FlightPlanner.PlanWait(sim.World, fleet, req);
            Assert.True(wait.Flight!.ArriveHour - wait.Flight.DepartHour <= direct.Flight!.ArriveHour - direct.Flight.DepartHour + 1e-6);
            Assert.True(wait.Flight.DepartHour >= direct.Flight.DepartHour);
        }
    }

    [Fact]
    public void A_waiting_fleet_stays_in_orbit_until_it_leaves()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        var req = To(BodyNamed(sim, "rust")) with { DepartTick = 5 * GameTime.TicksPerDay };
        sim.Issue(new PlanFlightCommand(fleet.Id, req));
        sim.StepMany(4 * GameTime.TicksPerDay);
        Assert.Equal(HoldStyle.Orbit, fleet.Hold);
        Assert.False(fleet.Flight!.Started);
        sim.StepMany(2 * GameTime.TicksPerDay);
        Assert.True(fleet.Flight!.Started);
    }

    [Fact]
    public void Fleets_split_and_merge_again()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        var leaving = fleet.ShipIds[0];
        sim.Issue(new SplitFleetCommand(fleet.Id, new List<int> { leaving }));
        sim.Step();
        var created = sim.World.Fleets.Single(f => f.Side == Side.Player && f.Id != fleet.Id);
        Assert.Equal(new[] { leaving }, created.ShipIds);
        Assert.Equal(2, fleet.ShipIds.Count);
        Assert.Equal(HoldStyle.Orbit, created.Hold);
        Assert.Equal("Battle fleet", FleetNames.KindOf(created.Name));

        sim.Issue(new MergeFleetsCommand(fleet.Id, created.Id));
        sim.Step();
        Assert.Equal(3, fleet.ShipIds.Count);
        Assert.Null(sim.World.FindFleet(created.Id));
        Assert.Equal(fleet.Id, sim.World.FindShip(leaving)!.FleetId);
    }

    [Fact]
    public void Far_apart_fleets_cannot_merge()
    {
        var sim = NewGame();
        var player = PlayerFleet(sim);
        var rival = sim.World.Fleets.First(f => f.Side == Side.Rival);
        Assert.NotNull(Fleets.CanMerge(player, rival));
        sim.Issue(new MergeFleetsCommand(player.Id, rival.Id));
        sim.Step();
        Assert.Equal(2, sim.World.Fleets.Count);
    }

    [Fact]
    public void A_drifting_fleet_can_be_put_into_orbit()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        sim.Issue(new SetHoldCommand(fleet.Id, HoldStyle.Drift));
        sim.StepMany(5);
        Assert.Equal(HoldStyle.Drift, fleet.Hold);
        sim.Issue(new SetHoldCommand(fleet.Id, HoldStyle.Orbit));
        sim.Step();
        Assert.Equal(HoldStyle.Orbit, fleet.Hold);
        Assert.Equal(BodyNamed(sim, "verdant").Id, fleet.OrbitBodyId);
    }

    [Fact]
    public void Flying_to_a_point_stops_there()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        var spot = new Vec2(0, 2.0 * Physics.MmPerAu);
        sim.Issue(new PlanFlightCommand(fleet.Id, new FlightRequest(null, spot, 1, ArrivalStyle.Brake, HoldStyle.Orbit, 0)));
        StepUntilArrived(sim, fleet);
        Assert.Equal(HoldStyle.Drift, fleet.Hold);
        Assert.True(Vec2.Distance(spot, fleet.Position) < 1e-3);
    }

    [Fact]
    public void Ships_keep_their_place_in_formation()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        sim.Issue(new PlanFlightCommand(fleet.Id, To(BodyNamed(sim, "vela"))));
        sim.StepMany(50);
        var offsets = sim.World.ShipsOf(fleet).Select(s => s.Position - fleet.Position).ToList();
        Assert.Equal(3, offsets.Distinct().Count());
        Assert.All(offsets, o => Assert.True(o.Length <= 2 * Fleets.FormationSpacing));
    }

    [Fact]
    public void Deterministic_maths_matches_the_system_library()
    {
        for (var i = -20; i <= 20; i++)
        {
            var y = i * 0.37;
            Assert.InRange(DetMath.Atan2(y, 1.3) - Math.Atan2(y, 1.3), -1e-14, 1e-14);
            Assert.InRange(DetMath.Atan2(1.1, y) - Math.Atan2(1.1, y), -1e-14, 1e-14);
            Assert.InRange(DetMath.Atan2(-y, -2) - Math.Atan2(-y, -2), -1e-14, 1e-14);
        }
        foreach (var v in new[] { 0.001, 0.5, 2, 27, 677_000, 2.15e8 })
            Assert.InRange(DetMath.Cbrt(v) / Math.Cbrt(v), 1 - 1e-14, 1 + 1e-14);
    }

    [Fact]
    public void A_split_off_fleet_moves_a_little_apart()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        sim.Issue(new SplitFleetCommand(fleet.Id, new List<int> { fleet.ShipIds[0] }));
        sim.Step();
        var created = sim.World.Fleets.Single(f => f.Side == Side.Player && f.Id != fleet.Id);
        var gap = Vec2.Distance(fleet.Position, created.Position);
        Assert.InRange(gap, 30, Fleets.SplitGap + 1);
        Assert.Equal(HoldStyle.Orbit, created.Hold);
        Assert.Null(Fleets.CanMerge(fleet, created));
    }

    [Fact]
    public void Fast_ships_go_ahead_and_join_up_on_arrival()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        var all = fleet.ShipIds.ToList();
        var vela = BodyNamed(sim, "vela");
        sim.Issue(new SendAheadCommand(fleet.Id, To(vela)));
        sim.Step();
        var ahead = sim.World.Fleets.Single(f => f.Side == Side.Player && f.Id != fleet.Id);
        Assert.Equal(fleet.Id, ahead.JoinFleetId);
        Assert.True(ahead.Flight!.ArriveHour < fleet.Flight!.ArriveHour, "the fast ships should arrive first");

        StepUntilArrived(sim, ahead);
        Assert.NotNull(sim.World.FindFleet(ahead.Id)); // waits for the slow ships
        StepUntilArrived(sim, fleet);
        sim.Step();
        Assert.Null(sim.World.FindFleet(ahead.Id));
        Assert.Equal(all.OrderBy(x => x), fleet.ShipIds.OrderBy(x => x));
        Assert.Equal(vela.Id, fleet.OrbitBodyId);
    }

    [Fact]
    public void Gathered_fleets_fly_together_and_merge()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        var vela = BodyNamed(sim, "vela");
        sim.Issue(new SplitFleetCommand(fleet.Id, new List<int> { fleet.ShipIds[0] }));
        sim.Step();
        var other = sim.World.Fleets.Single(f => f.Side == Side.Player && f.Id != fleet.Id);
        sim.Issue(new PlanFlightCommand(other.Id, To(vela)));
        StepUntilArrived(sim, other);

        sim.Issue(new GatherFleetsCommand(fleet.Id, new List<int> { other.Id }, 1));
        sim.Step();
        Assert.Equal(fleet.Id, other.JoinFleetId);
        StepUntilArrived(sim, other);
        sim.Step();
        Assert.Null(sim.World.FindFleet(other.Id));
        Assert.Equal(3, fleet.ShipIds.Count);
    }

    [Fact]
    public void A_fleet_chasing_to_join_one_that_settles_into_orbit_goes_to_the_planet_and_merges()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        var vela = BodyNamed(sim, "vela");
        sim.Issue(new SplitFleetCommand(fleet.Id, new List<int> { fleet.ShipIds[0] }));
        sim.Step();
        var other = sim.World.Fleets.Single(f => f.Side == Side.Player && f.Id != fleet.Id);
        // The fleet to join is drifting away when the order is given, so the joiner chases it...
        fleet.Flight = null;
        Fleets.EnterDrift(fleet);
        fleet.Velocity = new Vec2(0, 20);
        sim.Issue(new GatherFleetsCommand(fleet.Id, new List<int> { other.Id }, 1));
        sim.Step();
        Assert.Equal(ChaseMode.Join, other.Chase?.Mode);
        // ...and then it is sent to orbit Vela, where it keeps circling.
        sim.Issue(new PlanFlightCommand(fleet.Id, To(vela)));
        for (var h = 0; h < 120 * 24 && sim.World.FindFleet(other.Id) != null; h++) sim.Step();
        Assert.Null(sim.World.FindFleet(other.Id));
        Assert.Equal(3, fleet.ShipIds.Count);
        Assert.Equal(vela.Id, fleet.OrbitBodyId);
    }

    [Fact]
    public void A_join_chase_that_can_never_catch_up_gives_up_after_a_day_and_says_why()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        sim.Issue(new SplitFleetCommand(fleet.Id, new List<int> { fleet.ShipIds[0] }));
        sim.Step();
        var other = sim.World.Fleets.Single(f => f.Side == Side.Player && f.Id != fleet.Id);
        fleet.Flight = null;
        Fleets.EnterDrift(fleet);
        fleet.Velocity = new Vec2(0, 20);
        sim.Issue(new GatherFleetsCommand(fleet.Id, new List<int> { other.Id }, 0.5));
        sim.Step();
        Assert.Equal(ChaseMode.Join, other.Chase?.Mode);
        // The fleet to join now flies far faster than the joiner ever could.
        fleet.Velocity = new Vec2(0, 50000);
        for (var h = 0; h < 4 * 24 && other.Chase != null; h++) sim.Step();
        Assert.Null(other.Chase);
        Assert.Null(other.JoinFleetId);
        Assert.Contains(sim.World.Light.Notes, n => n.FleetId == other.Id && n.Text.Contains("stops trying"));
        Assert.True(sim.Log.Tail().Count(l => l.Contains("cannot catch up with")) <= 2);
    }

    [Fact]
    public void Losing_housing_moves_away_the_people_it_held()
    {
        var sim = NewGame();
        var home = sim.World.Bodies.First(b => b.Owner == Side.Player && b.Home);
        var data = Colonies.Kinds.First(k => k.HousingK > 0);
        var spot = Colonies.BestSpot(home, data);
        Assert.NotNull(spot);
        Colonies.AddPiece(sim.World, home, data, spot!.Value.Item1, spot.Value.Item2, spot.Value.Item3, built: true);
        Colonies.SyncBuildings(home);
        home.PopK = Planets.Cap(home);
        var before = sim.World.Bodies.Where(b => b.Owner == Side.Player).Sum(b => b.PopK);
        Colonies.RemoveOne(home, p => p.Kind == data.Id);
        sim.Step();
        Assert.True(home.PopK <= Planets.Cap(home) + 1e-9);
        // Those who no longer fit moved to the side's other worlds with room (here: the moon outpost), or were lost.
        Assert.True(sim.World.Bodies.Where(b => b.Owner == Side.Player).Sum(b => b.PopK) <= before);
        Assert.Empty(sim.CheckRules());
    }

    [Fact]
    public void A_join_that_cannot_be_flown_is_refused_up_front_and_changes_nothing()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        var vela = BodyNamed(sim, "vela");
        sim.Issue(new SplitFleetCommand(fleet.Id, new List<int> { fleet.ShipIds[0] }));
        sim.Step();
        var other = sim.World.Fleets.Single(f => f.Side == Side.Player && f.Id != fleet.Id);
        sim.Issue(new PlanFlightCommand(other.Id, To(vela)));
        StepUntilArrived(sim, other);

        Assert.Null(Fleets.JoinProblem(sim.World, fleet, other, 1));
        Assert.Null(fleet.Flight);

        foreach (var s in sim.World.ShipsOf(fleet)) s.Supplies = 0;
        Assert.Contains("supplies", Fleets.JoinProblem(sim.World, fleet, other, 1));
        Assert.Null(fleet.Flight);
        Assert.Null(fleet.Chase);
    }

    [Fact]
    public void Fleets_close_together_gather_at_once()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        sim.Issue(new SplitFleetCommand(fleet.Id, new List<int> { fleet.ShipIds[0], fleet.ShipIds[1] }));
        sim.Step();
        var other = sim.World.Fleets.Single(f => f.Side == Side.Player && f.Id != fleet.Id);
        sim.Issue(new GatherFleetsCommand(other.Id, new List<int> { fleet.Id }, 1));
        sim.Step();
        Assert.Null(sim.World.FindFleet(fleet.Id));
        Assert.Equal(3, other.ShipIds.Count);
    }

    [Fact]
    public void A_moving_fleet_curves_onto_a_new_course_without_stopping()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        sim.Issue(new PlanFlightCommand(fleet.Id, To(BodyNamed(sim, "vela"))));
        sim.StepMany(48);
        Assert.True(fleet.Velocity.Length > 1, "should be under way");

        var cinder = BodyNamed(sim, "cinder");
        var request = To(cinder) with { DepartTick = sim.World.Tick };
        var plan = FlightPlanner.Plan(sim.World, fleet, request);
        Assert.True(plan.Ok, plan.Problem);
        // No leg ends at rest except the last: the fleet never stops on the way.
        foreach (var leg in plan.Flight!.Legs.Take(plan.Flight.Legs.Count - 1))
            Assert.True(leg.VelocityAt(leg.End).Length > 1e-6);
        // Cheaper than braking to a stop and starting again.
        Assert.True(plan.DeltaV < fleet.Velocity.Length + 2 * plan.Flight.CruiseSpeed);

        sim.Issue(new PlanFlightCommand(fleet.Id, request));
        StepUntilArrived(sim, fleet);
        Assert.Equal(cinder.Id, fleet.OrbitBodyId);
        Assert.InRange(Vec2.Distance(fleet.Position, cinder.Position), 0, Fleets.OrbitRadiusFor(cinder) * 1.1);
    }

    [Fact]
    public void Ships_face_their_thrust_and_flip_to_brake()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        sim.Issue(new PlanFlightCommand(fleet.Id, To(BodyNamed(sim, "vela"))));
        sim.StepMany(2);
        var heading = DetMath.Atan2(fleet.Velocity.Y, fleet.Velocity.X);
        Assert.InRange(Math.Abs(Math.IEEERemainder(fleet.Facing - heading, 2 * Math.PI)), 0, 0.05);

        // Last hours of the flight: braking, so the ships face backwards.
        var brake = fleet.Flight!.Legs[^1];
        while (sim.World.Hour < brake.Start + 2) sim.Step();
        Assert.True(fleet.InFlight);
        heading = DetMath.Atan2(fleet.Velocity.Y, fleet.Velocity.X);
        Assert.InRange(Math.Abs(Math.IEEERemainder(fleet.Facing - heading, 2 * Math.PI)), Math.PI - 0.05, Math.PI);
    }

    [Fact]
    public void A_turn_towards_a_fast_inner_planet_still_curves()
    {
        // Heading out to Vela, then turned back towards Cinder, which moves fast: the plan must settle.
        var sim = NewGame(7);
        sim.StepMany(3 * GameTime.TicksPerDay);
        var fleet = PlayerFleet(sim);
        sim.Issue(new PlanFlightCommand(fleet.Id, To(BodyNamed(sim, "vela"), 0.7) with { DepartTick = sim.World.Tick }));
        sim.StepMany(30);
        var plan = FlightPlanner.Plan(sim.World, fleet, To(BodyNamed(sim, "cinder"), 0.7) with { DepartTick = sim.World.Tick });
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(sim.World.Hour, plan.Flight!.DepartHour);
        Assert.True(plan.Flight.Legs[0].VelocityAt(plan.Flight.Legs[0].End).Length > 1e-6);
    }

    private static void AssertContinuous(Flight f)
    {
        for (var i = 1; i < f.Legs.Count; i++)
        {
            var prev = f.Legs[i - 1];
            var next = f.Legs[i];
            Assert.Equal(prev.End, next.Start, 6);
            Assert.True(Vec2.Distance(prev.PositionAt(prev.End), next.P0) < 1e-3, $"leg {i} jumps");
            Assert.True(Vec2.Distance(prev.VelocityAt(prev.End), next.V0) < 1e-3, $"leg {i} changes speed instantly");
        }
    }

    [Fact]
    public void A_route_flies_through_its_waypoints()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        var vela = BodyNamed(sim, "vela");
        var side = fleet.Position + new Vec2(0, 20000);
        var request = To(vela) with { Waypoints = new List<Waypoint> { new(side) } };
        var plan = FlightPlanner.Plan(sim.World, fleet, request);
        Assert.True(plan.Ok, plan.Problem);
        AssertContinuous(plan.Flight!);
        var closest = Enumerable.Range(0, 2000).Select(k => plan.Flight.DepartHour + (plan.Flight.ArriveHour - plan.Flight.DepartHour) * k / 2000.0)
            .Min(h => Vec2.Distance(FlightPlanner.FlightState(plan.Flight, h)!.Value.Position, side));
        Assert.True(closest < 50, $"passed {closest} Mm from the waypoint");

        sim.Issue(new PlanFlightCommand(fleet.Id, request));
        StepUntilArrived(sim, fleet);
        Assert.Equal(vela.Id, fleet.OrbitBodyId);
    }

    [Fact]
    public void A_stop_waypoint_halts_and_waits_and_a_slow_stretch_coasts_cold()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        var vela = BodyNamed(sim, "vela");
        var stop = fleet.Position + new Vec2(15000, 0);
        var request = To(vela) with
        {
            Waypoints = new List<Waypoint> { new(stop, Stop: true, WaitHours: 10), new(stop + new Vec2(0, 15000), SpeedFraction: 0.2) },
        };
        var plan = FlightPlanner.Plan(sim.World, fleet, request);
        Assert.True(plan.Ok, plan.Problem);
        var f = plan.Flight!;
        AssertContinuous(f);
        // A 10-hour leg parked at the stop.
        Assert.Contains(f.Legs, l => !l.IsBurn && l.V0.Length < 1e-9 && Math.Abs(l.End - l.Start - 10) < 1e-6
                                     && Vec2.Distance(l.P0, stop) < 1e-3);
        // The slow stretch coasts at a fifth of top speed.
        var top = FleetPerformance.Of(sim.World, fleet).MaxSpeed;
        Assert.Contains(f.Legs, l => !l.IsBurn && Math.Abs(l.V0.Length - top * 0.2) < 1e-6);
        Assert.Equal(2, f.Waypoints.Count);
    }

    [Fact]
    public void Through_waypoints_at_sharp_corners_turn_tightly_instead_of_overshooting()
    {
        // A zig-zag like a playtest report: points tens of thousands of km apart, sharp turns.
        var sim = Starts.Classic(Simulation.NewGame(Scenarios.TestSystem, 36345455, GameLog.Memory(), sandbox: new SandboxSetup()));
        var fleet = sim.World.Fleets.First(f => f.Side == Side.Player);
        var o = fleet.Position;
        var pts = new[] { o + new Vec2(47, -33), o + new Vec2(54, 10), o + new Vec2(62, -23) };
        var target = o + new Vec2(54, -17);
        var request = new FlightRequest(null, target, 0.7, ArrivalStyle.Brake, HoldStyle.Drift, sim.World.Tick)
        {
            Waypoints = pts.Select(p => new Waypoint(p)).ToList(),
        };
        var plan = FlightPlanner.Plan(sim.World, fleet, request);
        Assert.True(plan.Ok, plan.Problem);
        var f = plan.Flight!;
        AssertContinuous(f);
        // Every point of the flight stays near the zig-zag through the waypoints.
        var corners = new[] { o }.Concat(pts).Append(target).ToArray();
        double Off(Vec2 q) => Enumerable.Range(0, corners.Length - 1).Min(k => SegmentDistance(q, corners[k], corners[k + 1]));
        var worst = Enumerable.Range(0, 3000).Select(k => f.DepartHour + (f.ArriveHour - f.DepartHour) * k / 3000.0)
            .Max(h => Off(FlightPlanner.FlightState(f, h)!.Value.Position));
        Assert.True(worst < 12, $"strays {worst:0.0} Mm off the route");
        // Passing through: no stretch starts from a standstill except the first (it turns, it doesn't stop and restart).
        Assert.DoesNotContain(f.Legs.Skip(1), l => l.V0.Length < 1e-6);
    }

    [Fact]
    public void A_flying_route_can_be_reopened_with_the_waypoints_still_ahead()
    {
        // Holding, so the rival (in sight thanks to the sandbox's known track) does not pull it into a fight.
        var sim = Starts.Classic(Simulation.NewGame(Scenarios.TestSystem, 36345455, GameLog.Memory(), sandbox: new SandboxSetup(PlayerStance: Stance.Hold)));
        var fleet = sim.World.Fleets.First(f => f.Side == Side.Player);
        var o = fleet.Position;
        var first = o + new Vec2(40, -30);
        var second = o + new Vec2(80, 0);
        var request = new FlightRequest(null, o + new Vec2(120, 20), 0.7, ArrivalStyle.Brake, HoldStyle.Drift, sim.World.Tick)
        {
            Waypoints = new List<Waypoint> { new(first), new(second, Stop: true, WaitHours: 5) },
        };
        sim.Issue(new PlanFlightCommand(fleet.Id, request));
        sim.Step();
        var flight = fleet.Flight!;
        Assert.Equal(2, flight.WaypointPlan.Count);
        Assert.Equal(2, flight.WaypointHours.Count);
        Assert.True(flight.WaypointHours[0] < flight.WaypointHours[1]);
        Assert.Equal(2, flight.WaypointsAhead(sim.World.Hour).Count());

        // Past the first waypoint only the second (with its stop and wait) is still ahead.
        sim.StepMany((int)Math.Ceiling(flight.WaypointHours[0] - sim.World.Hour) + 1);
        var ahead = fleet.Flight!.WaypointsAhead(sim.World.Hour).ToList();
        Assert.Equal(new Waypoint(second, Stop: true, WaitHours: 5), Assert.Single(ahead));

        // Sending the route again with the second waypoint moved replans from where the fleet is now.
        var moved = request with { DepartTick = sim.World.Tick, Waypoints = new List<Waypoint> { ahead[0] with { Point = second + new Vec2(0, 20) } } };
        sim.Issue(new PlanFlightCommand(fleet.Id, moved));
        sim.Step();
        Assert.Equal(second + new Vec2(0, 20), Assert.Single(fleet.Flight!.WaypointPlan).Point);
        var replay = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(replay.Exact, replay.Message);
    }

    private static double SegmentDistance(Vec2 q, Vec2 a, Vec2 b)
    {
        var d = b - a;
        var t = Math.Clamp(Vec2.Dot(q - a, d) / Math.Max(Vec2.Dot(d, d), 1e-12), 0, 1);
        return Vec2.Distance(q, a + d * t);
    }

    [Fact]
    public void A_route_through_the_star_turns_round_it()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        var star = sim.World.Bodies.Single(b => b.ParentId == null);
        fleet.Hold = HoldStyle.Drift;
        fleet.OrbitBodyId = null;
        fleet.Position = star.Position + new Vec2(-5000, 0);
        fleet.Velocity = Vec2.Zero;
        var aim = star.Position + new Vec2(5000, 0);
        var straight = new Flight { Legs = { new FlightLeg(sim.World.Hour, sim.World.Hour + 100, fleet.Position, new Vec2(100, 0), Vec2.Zero) } };
        Assert.Equal(star.Id, Avoidance.FirstHit(sim.World, straight)?.Body.Id);

        var plan = FlightPlanner.Plan(sim.World, fleet, new FlightRequest(null, aim, 0.5, ArrivalStyle.Brake, HoldStyle.Drift, sim.World.Tick));
        Assert.NotNull(plan.Flight);
        Assert.Null(Avoidance.FirstHit(sim.World, plan.Flight!));
        var turn = Assert.Single(plan.Flight!.WaypointPlan, p => p.Auto);
        Assert.True(Vec2.Distance(turn.Point, star.Position) >= star.Radius * 2);
        Assert.Empty(plan.Flight.ChosenWaypointsAhead(sim.World.Hour)); // the player's route has no waypoints to edit
        Assert.True(Vec2.Distance(plan.Flight.Legs[^1].PositionAt(plan.Flight.ArriveHour), aim) < 1);
    }

    [Fact]
    public void A_fleet_inside_a_planet_breaks_a_rule()
    {
        var sim = NewGame();
        var fleet = PlayerFleet(sim);
        fleet.Hold = HoldStyle.Drift;
        fleet.OrbitBodyId = null;
        fleet.Position = BodyNamed(sim, "verdant").Position;
        fleet.Flight = new Flight { Legs = { new FlightLeg(sim.World.Hour, sim.World.Hour + 10, fleet.Position, new Vec2(1, 0), Vec2.Zero) } };
        var broken = RuleBook.Default().Check(sim.World).ToList();
        Assert.Contains(broken, b => b.Rule == "flights-miss-bodies");
    }

    [Fact]
    public void Big_planets_push_their_moons_out_by_gravity()
    {
        var sim = NewGame();
        var tamber = BodyNamed(sim, "tamber");
        var brim = BodyNamed(sim, "brim");
        var rust = BodyNamed(sim, "rust");
        var flint = BodyNamed(sim, "flint");
        Assert.True(brim.OrbitRadius >= tamber.Radius * SystemData.MoonClearance - 1e-6);
        Assert.True(flint.OrbitRadius >= rust.Radius * SystemData.MoonClearance - 1e-6);
        // Moons keep their order, and orbiting fleets stay below them and within railgun reach of the ground.
        Assert.True(BodyNamed(sim, "glass").OrbitRadius > brim.OrbitRadius);
        Assert.True(Fleets.OrbitRadiusFor(tamber) < brim.OrbitRadius);
        Assert.True(Fleets.OrbitRadiusFor(tamber) - tamber.Radius <= GameData.Combat.Railgun.RangeMm * 0.5 + 1e-6);
    }
}
