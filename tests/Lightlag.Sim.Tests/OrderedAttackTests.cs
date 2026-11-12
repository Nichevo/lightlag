using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>A fleet sent after an enemy fights it, even outgunned, until the pull-back rules say otherwise.</summary>
public class OrderedAttackTests
{
    private static (Simulation Sim, Fleet Lone, Fleet Enemy) Setup()
    {
        var sim = Starts.Classic(Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory()));
        var w = sim.World;
        var home = w.Fleets.First(f => f.Side == Side.Player);
        new SplitFleetCommand(home.Id, new List<int> { home.ShipIds[0] }).Apply(w, sim);
        var lone = w.Fleets.Single(f => f.Side == Side.Player && f.ShipIds.Count == 1);
        var enemy = w.Fleets.First(f => f.Side == Side.Rival);
        // Put the enemy right beside the lone ship, drifting in open space.
        foreach (var f in new[] { lone, enemy })
        {
            f.Flight = null;
            Fleets.EnterDrift(f);
            f.Velocity = Vec2.Zero;
        }
        lone.Position = new Vec2(50_000, 50_000);
        enemy.Position = lone.Position + new Vec2(GameData.Combat.ContactRangeMm * 0.5, 0);
        Assert.True(BattleRules.Strength(w, enemy) > BattleRules.OutgunnedRatio * BattleRules.Strength(w, lone));
        return (sim, lone, enemy);
    }

    [Fact]
    public void A_lone_ship_met_by_a_much_stronger_enemy_slips_away()
    {
        var (sim, lone, enemy) = Setup();
        BattleRules.Check(sim, lone, enemy);
        Assert.True(lone.Retreating);
    }

    [Fact]
    public void A_lone_ship_sent_after_that_enemy_stays_and_fights()
    {
        var (sim, lone, enemy) = Setup();
        Sensors.Reveal(sim.World, Side.Player, enemy);
        lone.Chase = Chases.Order(sim.World, lone, enemy.Id, ChaseMode.GunRange, 1.0);
        Assert.True(BattleRules.SentAfter(sim.World, lone, enemy));
        BattleRules.Check(sim, lone, enemy);
        Assert.False(lone.Retreating);
    }
}
