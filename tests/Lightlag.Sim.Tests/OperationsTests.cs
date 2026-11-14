using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Fleet operations: who covers what, Cover it, patrol loops and the strike planner.</summary>
public class OperationsTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Body Named(Simulation sim, string key) => sim.World.Bodies.First(b => b.Key == key);
    private static Fleet Mine(Simulation sim) => sim.World.Fleets.First(f => f.Side == Side.Player);

    /// <summary>The rival's fleets far out of the way, so nothing fights.</summary>
    private static void RivalAway(Simulation sim)
    {
        foreach (var f in sim.World.Fleets.Where(f => f.Side != Side.Player))
        {
            f.Flight = null;
            f.Chase = null;
            Fleets.EnterDrift(f);
            f.Velocity = Vec2.Zero;
            f.Position = new Vec2(-30 * Physics.MmPerAu, -30 * Physics.MmPerAu);
        }
    }

    /// <summary>Split one warship off the player's first fleet into a second fleet (both at home).</summary>
    private static Fleet SplitOne(Simulation sim, Fleet f)
    {
        var armed = f.ShipIds.Last(id => Fitting.Spec(sim.World.FindShip(id)!) is { } c && (c.Railguns > 0 || c.Missiles > 0));
        new SplitFleetCommand(f.Id, new List<int> { armed }).Apply(sim.World, sim);
        return sim.World.Fleets.Last(o => o.Side == Side.Player);
    }

    [Fact]
    public void Places_list_our_planets_with_their_moons_and_our_cargo_runs()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Economy.HomeOf(w, Side.Player)!;
        var rust = Named(sim, "vela");
        rust.Owner = Side.Player;
        w.FixedRuns.Add(new FixedRun { Id = w.NextId++, Side = Side.Player, FromBodyId = home.Id, ToBodyId = rust.Id, Good = "food" });
        var places = Coverage.Places(w, Side.Player);
        var homePlace = Assert.Single(places, p => p.Kind == PlaceKind.Planet && p.BodyIds[0] == home.Id);
        // The home moon we hold belongs with its planet, not as a row of its own.
        foreach (var moon in w.Bodies.Where(b => b.Owner == Side.Player && b.ParentId == home.Id))
            Assert.Contains(moon.Id, homePlace.BodyIds);
        Assert.Contains(places, p => p.Kind == PlaceKind.Planet && p.BodyIds[0] == rust.Id);
        var run = Assert.Single(places, p => p.Kind == PlaceKind.Run);
        Assert.Equal($"{home.Name} to {rust.Name}", run.Name);
    }

    [Fact]
    public void A_planet_nobody_would_answer_for_is_open_and_raises_an_advice_need()
    {
        var sim = NewGame();
        var w = sim.World;
        
        var rust = Named(sim, "vela");
        rust.Owner = Side.Player;
        rust.PopK = 5;
        var place = Coverage.Find(w, Side.Player, $"planet:{rust.Id}")!;
        var j = Coverage.Judge(w, Side.Player, place);
        Assert.Equal(CoverState.Open, j.State);
        Assert.Contains("nothing", j.Why);

        // Needs you lists it at Advice level with Cover it as the act.
        sim.StepMany(12);
        var needs = Economy.Of(w, Side.Player).Needs;
        var need = Assert.Single(needs, n => n.Key == $"open:planet:{rust.Id}");
        Assert.Equal(NeedLevel.Advice, need.Level);
        Assert.IsType<CoverPlaceCommand>(need.Act);
        Assert.Equal("Places nobody covers", NeedKinds.KindName(NeedKinds.KindOf(need.Key)));
    }

    [Fact]
    public void A_fleet_in_orbit_covers_its_planet_and_one_far_away_is_thin()
    {
        var sim = NewGame();
        var w = sim.World;
        RivalAway(sim);
        var home = Economy.HomeOf(w, Side.Player)!;
        var guard = Mine(sim);
        var place = Coverage.Find(w, Side.Player, $"planet:{home.Id}")!;
        Assert.True(Coverage.OnSpot(w, guard, place));
        var here = Coverage.Judge(w, Side.Player, place);
        Assert.Contains(here.Who, c => c.FleetId == guard.Id && c.OnSpot);
        Assert.NotEqual(CoverState.Open, here.State);

        // A Defence fleet that would answer but is days away leaves a planet thin, not open.
        var rust = Named(sim, "vela");
        rust.Owner = Side.Player;
        rust.PopK = 5;
        new SetFleetRoleCommand(guard.Id, FleetRole.Defence, new RoleRules { Range = RoleRange.Anywhere, Odds = RoleOdds.Any }).Apply(w, sim);
        var far = Coverage.Judge(w, Side.Player, Coverage.Find(w, Side.Player, $"planet:{rust.Id}")!);
        Assert.Equal(CoverState.Thin, far.State);
        Assert.Contains(guard.Name, far.Why);

        // What would its leaving uncover? Home (it is the only fleet there).
        var worse = Coverage.Uncovered(w, Side.Player, new[] { guard.Id });
        Assert.Contains(worse, x => x.Now.Place.Key == place.Key);
    }

    [Fact]
    public void Cover_it_moves_the_fleet_and_shows_the_order_against_it()
    {
        var sim = NewGame();
        var w = sim.World;
        RivalAway(sim);
        var rust = Named(sim, "vela");
        rust.Owner = Side.Player;
        rust.PopK = 5;
        var fleet = Mine(sim);
        var key = $"planet:{rust.Id}";
        sim.Issue(new CoverPlaceCommand(fleet.Id, key));
        sim.ApplyPending();
        Assert.Equal(Mission.Hold, fleet.Mission);
        Assert.Equal(rust.Id, fleet.MissionBodyId);
        Assert.Equal(FleetRole.Defence, fleet.Role); // it had no role: it takes Planet defence
        Assert.NotNull(fleet.Flight);
        Assert.Equal(key, fleet.CoverKey);
        Assert.Contains(Operations.Status(w, fleet), s => s.StartsWith("Cover ") && s.Contains("on its way"));
        // On its way it already counts, with its arrival time.
        var j = Coverage.Judge(w, Side.Player, Coverage.Find(w, Side.Player, key)!);
        Assert.Contains(j.Who, c => c.FleetId == fleet.Id && !c.OnSpot);
        Assert.NotEqual(CoverState.Open, j.State);
        // A direct move afterwards ends the Cover order.
        var home = Economy.HomeOf(w, Side.Player)!;
        sim.Issue(new PlanFlightCommand(fleet.Id, new FlightRequest(home.Id, home.Position, 0.5, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick)));
        sim.ApplyPending();
        Assert.Null(fleet.CoverKey);
    }

    [Fact]
    public void Cover_it_on_a_cargo_run_sets_a_loop_and_an_anti_piracy_role()
    {
        var sim = NewGame();
        var w = sim.World;
        RivalAway(sim);
        var home = Economy.HomeOf(w, Side.Player)!;
        var rust = Named(sim, "vela");
        rust.Owner = Side.Player;
        w.FixedRuns.Add(new FixedRun { Id = w.NextId++, Side = Side.Player, FromBodyId = home.Id, ToBodyId = rust.Id, Good = "food" });
        var run = Coverage.Places(w, Side.Player).Single(p => p.Kind == PlaceKind.Run);
        var fleet = Mine(sim);
        sim.Issue(new CoverPlaceCommand(fleet.Id, run.Key));
        sim.ApplyPending();
        Assert.Equal(Mission.Patrol, fleet.Mission);
        Assert.Equal(4, fleet.Loop.Count);
        Assert.Equal(FleetRole.AntiPiracy, fleet.Role);
        Assert.Equal(run.Key, fleet.CoverKey);
    }

    [Fact]
    public void A_patrol_loop_visits_its_stops_in_order_and_waits_at_each()
    {
        var sim = NewGame();
        var w = sim.World;
        RivalAway(sim);
        var home = Economy.HomeOf(w, Side.Player)!;
        var moon = w.Bodies.First(b => b.ParentId == home.Id && b.Kind == BodyKind.Moon);
        var fleet = Mine(sim);
        var stops = new List<LoopStop> { new() { BodyId = home.Id, WaitHours = 6 }, new() { BodyId = moon.Id, WaitHours = 10 } };
        sim.Issue(new SetPatrolLoopCommand(fleet.Id, stops));
        sim.ApplyPending();
        Assert.Equal(Mission.Patrol, fleet.Mission);
        Assert.Equal(2, fleet.Loop.Count);
        Assert.Contains("one lap", Operations.LapText(w, fleet, fleet.Loop));

        // It waits at the first stop (it starts there), then flies to the moon, then waits there.
        var sawWait = false;
        var reachedMoon = false;
        for (var i = 0; i < 24 * 6 && !reachedMoon; i++)
        {
            sim.Step();
            if (fleet.MissionLeg % 2 == 0 && fleet.MissionUntil > w.Hour) sawWait = true;
            if (fleet.MissionLeg % 2 == 1 && Economy.InOrbitAt(fleet, moon)) reachedMoon = true;
        }
        Assert.True(sawWait, "it should wait at the first stop");
        Assert.True(reachedMoon, "it should get to the second stop");
        Assert.Contains(Operations.Status(w, fleet), s => s.Contains("loop stop 2 of 2"));
        // The replay runs the same.
        var replay = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(replay.Exact, replay.Message);
    }

    [Fact]
    public void A_loop_fleet_answering_a_call_comes_back_to_the_stop_it_left()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Economy.HomeOf(w, Side.Player)!;
        var moon = w.Bodies.First(b => b.ParentId == home.Id && b.Kind == BodyKind.Moon);
        var fleet = Mine(sim);
        RivalAway(sim);
        new SetFleetRoleCommand(fleet.Id, FleetRole.Defence, new RoleRules { Odds = RoleOdds.Any }).Apply(w, sim);
        new SetPatrolLoopCommand(fleet.Id, new List<LoopStop>
        {
            new() { BodyId = home.Id, WaitHours = 6 }, new() { BodyId = moon.Id, WaitHours = 6 }, new() { BodyId = home.Id, WaitHours = 6 },
        }).Apply(w, sim);
        fleet.MissionLeg = 1;
        // An enemy shows up near home: the call wins.
        var enemy = w.Fleets.First(f => f.Side == Side.Rival);
        enemy.Position = home.Position + new Vec2(0.05 * Physics.MmPerAu, 0);
        Sensors.Reveal(w, Side.Player, enemy);
        HelpCalls.Update(sim);
        Assert.NotNull(fleet.AnsweringCallId);
        Assert.Equal(Mission.None, fleet.Mission);
        Assert.Contains(Operations.Status(w, fleet), s => s.Contains("Off its loop"));
        // The enemy goes away; once the call is over the loop resumes at the same stop.
        enemy.Position = new Vec2(-30 * Physics.MmPerAu, -30 * Physics.MmPerAu);
        w.Sightings.RemoveAll(s => s.FleetId == enemy.Id && s.SeenBy == Side.Player);
        HelpCalls.Update(sim);
        Assert.Null(fleet.AnsweringCallId);
        Assert.Equal(Mission.Patrol, fleet.Mission);
        Assert.Equal(1, fleet.MissionLeg);
        Assert.Equal(3, fleet.Loop.Count);
    }

    [Fact]
    public void A_strike_arrives_together_and_its_plan_can_be_saved_and_sent_again()
    {
        var sim = NewGame();
        var w = sim.World;
        RivalAway(sim);
        var a = Mine(sim);
        var b = SplitOne(sim, a);
        var home = Economy.HomeOf(w, Side.Player)!;
        var rust = Named(sim, "rust");
        // Put the second fleet much nearer the target, so alone it would arrive well before the first.
        b.Flight = null;
        Fleets.EnterDrift(b);
        b.Velocity = Vec2.Zero;
        b.Position = home.Position + (rust.Position - home.Position) * 0.6;

        var soon = Strikes.Preview(w, Side.Player, new[] { a.Id, b.Id }, rust.Id, null, StrikeArrival.AsSoonAsEach, 0, 0.8);
        Assert.Null(soon.Problem);
        var la = soon.Legs.Single(l => l.FleetId == a.Id).ArriveHour;
        var lb = soon.Legs.Single(l => l.FleetId == b.Id).ArriveHour;
        Assert.True(lb < la - 2, $"alone the near fleet arrives first ({lb:0} vs {la:0})");

        var together = Strikes.Preview(w, Side.Player, new[] { a.Id, b.Id }, rust.Id, null, StrikeArrival.Together, 0, 0.8);
        Assert.Null(together.Problem);
        Assert.True(Math.Abs(together.Legs[0].ArriveHour - together.Legs[1].ArriveHour) < 2,
            $"together they arrive within two hours ({together.Legs[0].ArriveHour:0.0} and {together.Legs[1].ArriveHour:0.0})");
        Assert.Contains("guess", together.EnemyNote);

        var waves = Strikes.Preview(w, Side.Player, new[] { a.Id, b.Id }, rust.Id, null, StrikeArrival.Waves, 24, 0.8);
        var order = waves.Legs.OrderBy(l => l.ArriveHour).ToList();
        Assert.True(order[1].ArriveHour - order[0].ArriveHour >= 22, "waves a day apart");

        // Sending it: each fleet gets its leg and shows the strike.
        foreach (var leg in together.Legs)
            sim.Issue(new StrikeLegCommand(leg.FleetId, leg.Request!, "Strike on Rust", together.TargetName, leg.ArriveHour));
        sim.ApplyPending();
        Assert.NotNull(a.Strike);
        Assert.NotNull(b.Strike);
        Assert.Contains(Operations.Status(w, a), s => s.StartsWith("Strike on Rust"));
        Assert.True(Math.Abs(a.Flight!.ArriveHour - b.Flight!.ArriveHour) < 2);

        // Saved, it can be sent again later with times worked out afresh.
        sim.Issue(new SaveStrikePlanCommand(Side.Player, "", new List<int> { a.Id, b.Id }, rust.Id, null, StrikeArrival.Together, 12, 0.8));
        sim.ApplyPending();
        var plan = Assert.Single(w.StrikePlans);
        Assert.Equal("Strike on Rust", plan.Name);
        sim.StepMany(10);
        var again = Strikes.Preview(w, Side.Player, plan.FleetIds, plan.TargetBodyId, plan.TargetFleetId, plan.Arrival, plan.WaveGapHours, plan.Burn);
        Assert.Null(again.Problem);
        Assert.True(again.Legs.All(l => l.Request!.DepartTick >= w.Tick));
        sim.Issue(new DeleteStrikePlanCommand(Side.Player, plan.Id));
        sim.ApplyPending();
        Assert.Empty(w.StrikePlans);

        var replay = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(replay.Exact, replay.Message);
    }

    [Fact]
    public void Orders_work_while_paused_and_survive_a_save()
    {
        var sim = NewGame();
        var w = sim.World;
        RivalAway(sim);
        var home = Economy.HomeOf(w, Side.Player)!;
        var moon = w.Bodies.First(b => b.ParentId == home.Id && b.Kind == BodyKind.Moon);
        var fleet = Mine(sim);
        var tick = w.Tick;
        // Paused: no step, only the orders are applied.
        sim.Issue(new SetPatrolLoopCommand(fleet.Id, new List<LoopStop> { new() { BodyId = moon.Id, WaitHours = 8 }, new() { FromBodyId = home.Id, ToBodyId = moon.Id, T = 0.5, WaitHours = 4 } }));
        sim.Issue(new SaveStrikePlanCommand(Side.Player, "Test", new List<int> { fleet.Id }, Named(sim, "rust").Id, null, StrikeArrival.Waves, 6, 0.6));
        sim.ApplyPending();
        Assert.Equal(tick, w.Tick);
        Assert.Equal(2, fleet.Loop.Count);
        Assert.Single(w.StrikePlans);

        var loaded = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        var f2 = loaded.World.FindFleet(fleet.Id)!;
        Assert.Equal(2, f2.Loop.Count);
        Assert.Equal(8, f2.Loop[0].WaitHours);
        Assert.Equal(StrikeArrival.Waves, Assert.Single(loaded.World.StrikePlans).Arrival);
    }

    [Fact]
    public void Reach_lists_trip_times_and_burns_left()
    {
        var sim = NewGame();
        var fleet = Mine(sim);
        var (trips, burns, dv) = Operations.Reach(sim.World, fleet);
        Assert.NotEmpty(trips);
        Assert.All(trips, t => Assert.True(t.Hours > 0));
        Assert.True(dv > 0);
        Assert.True(burns >= 0);
    }
}
