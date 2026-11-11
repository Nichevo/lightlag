using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class FormationTests
{
    private static Simulation Sandbox(SandboxSetup setup, ulong seed = 3)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), sandbox: setup);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet FleetOf(Simulation sim, Side side) => sim.World.Fleets.First(f => f.Side == side);

    private static int BlockedShooters(Simulation sim, Fleet f, Fleet enemy)
    {
        var target = sim.World.ShipsOf(enemy).First();
        return sim.World.ShipsOf(f).Count(s => Formations.Blocked(sim.World, s, target));
    }

    [Fact]
    public void Staggered_leaves_every_gun_clear_and_a_wall_blocks_the_guns_behind()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 6, PlayerDestroyers: 0, RivalFrigates: 1, RivalDestroyers: 0,
            PlayerStance: Stance.Hold, RivalStance: Stance.Hold, DistanceMm: 30));
        var mine = FleetOf(sim, Side.Player);
        var theirs = FleetOf(sim, Side.Rival);
        sim.Step();
        void Use(Formation shape)
        {
            sim.Issue(new SetFormationCommand(mine.Id, shape));
            sim.ApplyPending();
            foreach (var s in sim.World.ShipsOf(mine)) s.Position = mine.Position + s.FormationOffset;
        }
        Use(Formation.Staggered);
        Assert.Equal(Formation.Staggered, mine.LaidShape);
        Assert.Equal(0, BlockedShooters(sim, mine, theirs));
        Use(Formation.Wall);
        Assert.Equal(4, BlockedShooters(sim, mine, theirs)); // two in front, four behind them
        Use(Formation.Column);
        Assert.Equal(5, BlockedShooters(sim, mine, theirs));
        Use(Formation.Line);
        Assert.Equal(0, BlockedShooters(sim, mine, theirs));
    }

    [Fact]
    public void Auto_puts_missile_boats_behind_a_heavy_ship_and_gun_ships_in_a_line()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 3, PlayerDestroyers: 0, RivalFrigates: 2, RivalDestroyers: 0,
            PlayerStance: Stance.Hold, RivalStance: Stance.Hold, DistanceMm: 30));
        sim.Step();
        var w = sim.World;
        var mine = FleetOf(sim, Side.Player);
        Assert.Equal(Formation.Auto, mine.Formation);
        Assert.Equal(Formation.Line, Formations.Shape(w, mine)); // all guns, and the enemy has no missiles
        foreach (var s in w.ShipsOf(mine)) { s.ClassId = "missile-destroyer"; s.Fit.Clear(); }
        w.ShipsOf(mine).First().ClassId = "cruiser";
        foreach (var s in w.ShipsOf(mine)) s.Missiles = Fitting.Spec(s).Missiles;
        Assert.Equal(Formation.Wall, Formations.Shape(w, mine));
    }

    [Fact]
    public void Shield_emitters_take_part_of_a_hit_on_an_unshielded_neighbour()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 2, PlayerDestroyers: 0, RivalFrigates: 1, RivalDestroyers: 0));
        var w = sim.World;
        var mine = FleetOf(sim, Side.Player);
        var ships = w.ShipsOf(mine).ToList();
        ships[0].ClassId = "cruiser";
        ships[0].Fit.Clear();
        ships[0].Shield = 50;
        ships[1].Shield = 0;
        Assert.Same(ships[0], Formations.ShieldCover(w, ships[1]));
        ships[0].Shield = 0;
        Assert.Null(Formations.ShieldCover(w, ships[1]));
    }
}
