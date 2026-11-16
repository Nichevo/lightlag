using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>
/// Fixes from the playtest reports of the heat review: planets in battle time, start supply roles, heat under fire,
/// the heat clock, sinks seen thrown, the ship queue, the habitat pod and "being sorted" shortages.
/// </summary>
public class Reports1116Tests
{
    private static Simulation Sandbox(SandboxSetup setup, ulong seed = 3)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), sandbox: setup);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Simulation Game(ulong seed = 7)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    [Fact]
    public void Planets_move_every_battle_step_not_once_an_hour()
    {
        var sim = Sandbox(new SandboxSetup(DistanceMm: 30));
        var w = sim.World;
        var steps = 0;
        for (var i = 0; i < 400 && steps < 30; i++)
        {
            sim.Step();
            if (w.Sub == 0) continue;
            steps++;
            foreach (var b in w.Bodies.Where(b => b.ParentId != null))
                Assert.True(Vec2.Distance(b.Position, FlightPlanner.PredictBody(w, b, w.Hour)) < 1e-6, $"{b.Name} lags its orbit in battle time");
        }
        Assert.True(steps > 0, "no battle-time steps ran");
        Assert.Empty(sim.CheckRules());
    }

    [Fact]
    public void Fleets_of_supply_ships_start_on_the_Supply_role()
    {
        var sim = Game();
        var w = sim.World;
        var supply = w.Fleets.Where(f => HelpCalls.SupplyOnly(w, f)).ToList();
        Assert.NotEmpty(supply);
        Assert.All(supply, f => Assert.Equal(FleetRole.Supply, f.Role));
        Assert.All(w.Fleets.Where(f => !HelpCalls.SupplyOnly(w, f)), f => Assert.NotEqual(FleetRole.Supply, f.Role));
    }

    [Fact]
    public void Auto_radiators_stay_out_until_enemy_guns_can_reach()
    {
        var sim = Sandbox(new SandboxSetup(DistanceMm: 30));
        var w = sim.World;
        var mine = w.Fleets.First(f => f.Side == Side.Player);
        var theirs = w.Fleets.First(f => f.Side == Side.Rival);
        theirs.LongShots = false;
        for (var i = 0; i < 400 && w.ActiveBattle == null; i++) sim.Step();
        Assert.NotNull(w.ActiveBattle);
        Assert.True(Heat.InBattle(w, mine));
        var reach = Secrecy.RailRange(w, theirs) * Heat.UnderFireMargin;
        var gap = Vec2.Distance(mine.Position, theirs.Position);
        var fire = w.Missiles.Any(m => m.Side != Side.Player && w.FindShip(m.TargetShipId)?.FleetId == mine.Id);
        Assert.Equal(gap <= reach || fire, Heat.UnderFire(w, mine));
        // Moved well out of their reach with nothing coming: radiators out on Auto, in on Run cold.
        w.Missiles.RemoveAll(m => m.Side != Side.Player);
        theirs.Position = mine.Position + new Vec2(reach * 2, 0);
        Assert.False(Heat.UnderFire(w, mine));
        var ship = w.ShipsOf(mine).First();
        Assert.True(Heat.RadiatorsOut(w, mine, ship) || ship.Overheated);
        theirs.Position = mine.Position + new Vec2(reach * 0.5, 0);
        Assert.True(Heat.UnderFire(w, mine));
        if (!ship.Overheated) Assert.False(Heat.RadiatorsOut(w, mine, ship));
    }

    [Fact]
    public void The_heat_clock_is_shorter_for_a_harder_burn_and_counts_sinks()
    {
        var sim = Sandbox(new SandboxSetup());
        var w = sim.World;
        var f = w.Fleets.First(x => x.Side == Side.Player);
        var hard = Heat.HoursToOverheat(w, f, 1.0, radiatorsIn: true, combatBurn: true);
        var gentle = Heat.HoursToOverheat(w, f, 0.4, radiatorsIn: true, combatBurn: true);
        Assert.NotNull(hard);
        Assert.NotNull(gentle);
        Assert.True(hard < gentle);
        // Radiators out shed more than a burn makes: it never overheats.
        Assert.Null(Heat.HoursToOverheat(w, f, 0.4, radiatorsIn: false, combatBurn: false));
        // Without sinks the clock runs out sooner.
        f.AutoSinks = true;
        var withSinks = Heat.HoursToOverheat(w, f, 1.0, true, true)!.Value;
        foreach (var s in w.ShipsOf(f)) s.HeatSinks = 0;
        Assert.True(Heat.HoursToOverheat(w, f, 1.0, true, true)!.Value < withSinks);
    }

    [Fact]
    public void Warships_carry_twice_the_old_heat_sinks_and_the_new_research_adds_more()
    {
        Assert.Equal(4, GameData.ShipClass("gun-frigate").HeatSinks);
        Assert.Equal(6, GameData.ShipClass("missile-destroyer").HeatSinks);
        var sim = Game();
        var w = sim.World;
        var destroyer = w.Ships.First(s => s.Side == Side.Player && s.ClassId == "missile-destroyer");
        var racks = Tech.Racks(w, destroyer);
        Economy.Of(w, Side.Player).Researched.AddRange(new[] { "ejectable-heat-sinks", "sink-lockers", "deep-magazines" });
        Tech.Refresh(w);
        Assert.Equal(1 + 3, destroyer.TechSinks); // +1, and half of 6 more
        Assert.Equal(6 + 4, SupplyLines.MaxSinks(destroyer));
        Assert.Equal((int)Math.Round(racks * 1.25), Tech.Racks(w, destroyer));
    }

    [Fact]
    public void Sinks_thrown_in_a_fight_are_counted_for_the_other_side_to_see()
    {
        var sim = Sandbox(new SandboxSetup(DistanceMm: 30));
        var w = sim.World;
        for (var i = 0; i < 400 && w.ActiveBattle == null; i++) sim.Step();
        var battle = w.ActiveBattle!;
        var theirs = w.Fleets.First(f => f.Side == Side.Rival && battle.Foes(Side.Player).Fleets.Contains(f.Name));
        var ship = w.ShipsOf(theirs).First(s => s.HeatSinks > 0);
        var before = battle.For(Side.Rival).SinksThrown;
        Heat.EjectSink(sim, theirs, ship, "test");
        Assert.Equal(before + 1, battle.For(Side.Rival).SinksThrown);
        Assert.Equal(before + 1, battle.Foes(Side.Player).SinksThrown);
    }

    [Fact]
    public void Queued_ships_can_be_moved_up_and_down_the_queue()
    {
        var sim = Game();
        var w = sim.World;
        foreach (var key in new[] { "gun-frigate", "missile-destroyer", "supply-tender" })
            new QueueOrderCommand(Side.Player, OrderKind.Ship, key).Apply(w, sim);
        var queue = Budget.ShipQueue(w, Side.Player).Where(o => !o.Paid).ToList();
        Assert.True(queue.Count >= 3);
        var last = queue[^1];
        new MoveShipOrderCommand(Side.Player, last.Id, -1).Apply(w, sim);
        var after = Budget.ShipQueue(w, Side.Player).Where(o => !o.Paid).ToList();
        Assert.Equal(queue.Count - 2, after.IndexOf(last));
        new MoveShipOrderCommand(Side.Player, last.Id, 1).Apply(w, sim);
        Assert.Equal(queue.Select(o => o.Id), Budget.ShipQueue(w, Side.Player).Where(o => !o.Paid).Select(o => o.Id));
        // The yard starts the first in the queue: move the last to the top and it is the one taken.
        new MoveShipOrderCommand(Side.Player, last.Id, -1).Apply(w, sim);
        new MoveShipOrderCommand(Side.Player, last.Id, -1).Apply(w, sim);
        Assert.Equal(last.Id, Budget.ShipQueue(w, Side.Player).First(o => !o.Paid).Id);
    }

    [Fact]
    public void The_habitat_pod_fits_one_tile()
    {
        var pod = Planets.BuildingKind("habitat-pod");
        Assert.NotNull(pod);
        Assert.Equal(1, pod!.W);
        Assert.Equal(1, pod.H);
        Assert.True(pod.HousingK > 0);
        Assert.True(pod.HousingK < Planets.BuildingKind("habitat")!.HousingK);
    }

    [Fact]
    public void A_shortage_already_being_fixed_says_so()
    {
        var sim = Game();
        var w = sim.World;
        var home = w.Bodies.First(b => b.Owner == Side.Player && b.Home);
        Assert.Null(SupplyChain.Underway(w, Side.Player, "food"));
        var piece = new Piece { Id = w.NextId++, Kind = Planets.Farm, X = 0, Y = 0, W = 1, H = 1, HoursLeft = 60 };
        home.Pieces.Add(piece);
        var line = SupplyChain.Underway(w, Side.Player, "food");
        Assert.NotNull(line);
        Assert.Contains("being built at " + home.Name, line);
        Assert.Contains("2.5 days left", line);
    }
}
