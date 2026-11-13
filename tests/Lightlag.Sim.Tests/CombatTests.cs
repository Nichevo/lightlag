using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Xunit;

namespace Lightlag.Sim.Tests;

public class CombatTests
{
    private static Simulation Sandbox(SandboxSetup setup, ulong seed = 3)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), sandbox: setup);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet FleetOf(Simulation sim, Side side) => sim.World.Fleets.First(f => f.Side == side);

    /// <summary>Plays until the first battle is over (or the time runs out).</summary>
    private static Battle Fight(Simulation sim, int maxHours = 48)
    {
        var end = sim.World.Tick + maxHours;
        while (sim.World.Tick < end && (sim.World.Battles.Count == 0 || sim.World.ActiveBattle != null)) sim.Step();
        Assert.NotEmpty(sim.World.Battles);
        return sim.World.Battles[0];
    }

    [Fact]
    public void Sandbox_sets_up_the_two_fleets()
    {
        var sim = Sandbox(new SandboxSetup(3, 1, 0, 2));
        Assert.Equal(Scenarios.BattleSandbox, sim.World.Scenario);
        Assert.Equal(4, FleetOf(sim, Side.Player).ShipIds.Count);
        Assert.Equal(2, FleetOf(sim, Side.Rival).ShipIds.Count);
        Assert.InRange(Vec2.Distance(FleetOf(sim, Side.Player).Position, FleetOf(sim, Side.Rival).Position), 148, 152);
    }

    [Fact]
    public void Battles_are_recorded_to_watch_again()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 4, PlayerDestroyers: 0, RivalFrigates: 0, RivalDestroyers: 2,
            PlayerStance: Stance.Hold, DistanceMm: 45));
        var battle = Fight(sim);
        Assert.True(battle.Frames.Count >= 2);
        Assert.Equal(battle.StartHour, battle.Frames[0].Hour, 6);
        if (battle.EndHour is double end) Assert.Equal(end, battle.Frames[^1].Hour, 6);
        Assert.Contains(battle.Frames, f => f.Missiles.Count > 0);
        Assert.Contains(battle.Frames, f => f.Shots.Count > 0);
        Assert.Equal(6, battle.Roster.Count);
        Assert.All(battle.Frames.Zip(battle.Frames.Skip(1)), p => Assert.True(p.Second.Hour > p.First.Hour));
        foreach (var lost in battle.For(Side.Player).ShipsLost)
            Assert.Contains(battle.Marks, m => m.Kind == MarkKind.ShipLost && m.Text.StartsWith(lost));
    }

    [Fact]
    public void Missile_ships_unseen_by_their_target_get_the_first_shots_and_catch_it_unawares()
    {
        // Cold, quiet fleets 55,000 km apart: neither ship's own sensors see that far. The player is told where
        // the rival is (as a planet's sensors might), so its missiles reach out first; the rival sees nothing.
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 0, PlayerDestroyers: 2, RivalFrigates: 2, RivalDestroyers: 0,
            PlayerStance: Stance.Hold, RivalStance: Stance.Hold, DistanceMm: 55, RivalKnowsWhere: false));
        var w = sim.World;
        var rival = FleetOf(sim, Side.Rival);
        var player = FleetOf(sim, Side.Player);
        for (var i = 0; i < 4 && rival.SurprisedUntilHour == null; i++)
        {
            Sensors.Reveal(w, Side.Player, rival);
            sim.Step();
        }
        Assert.NotNull(rival.SurprisedUntilHour);
        Assert.True(w.Battles.Single().For(Side.Player).Ambushes > 0);
        Assert.Null(player.SurprisedUntilHour);
    }

    [Fact]
    public void A_fleet_caught_unawares_neither_fires_nor_dodges_for_a_while()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 0, PlayerDestroyers: 2, RivalFrigates: 2, RivalDestroyers: 0,
            PlayerStance: Stance.Hold, RivalStance: Stance.Hold, DistanceMm: 30));
        var rival = FleetOf(sim, Side.Rival);
        Assert.False(Combat.Unseen(sim.World, rival));
        while (sim.World.ActiveBattle == null && sim.World.Tick < 5) sim.Step();
        rival.SurprisedUntilHour = sim.World.Hour + Combat.SurpriseHours;
        Assert.True(Combat.Surprised(sim.World, rival));
        while (sim.World.Hour < rival.SurprisedUntilHour - 0.01) sim.Step();
        var battle = sim.World.Battles.Single();
        Assert.Equal(0, battle.For(Side.Rival).RailShots);
        Assert.Equal(0, battle.For(Side.Rival).MissilesFired);
        Assert.True(battle.For(Side.Player).RailShots + battle.For(Side.Player).MissilesFired > 0, $"{battle.StartHour} {sim.World.Hour}");
        // Then it is itself again.
        sim.StepMany(2);
        Assert.False(Combat.Surprised(sim.World, rival));
    }

    [Fact]
    public void Fleets_out_of_contact_run_whole_hours()
    {
        var sim = Sandbox(new SandboxSetup(DistanceMm: 5000));
        sim.Step();
        Assert.False(sim.InBattleTime);
        Assert.Equal(1, sim.World.Tick);
    }

    [Fact]
    public void Destroyers_fire_missiles_and_point_defence_shoots_some_down()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 4, PlayerDestroyers: 0, RivalFrigates: 0, RivalDestroyers: 2,
            PlayerStance: Stance.Hold, DistanceMm: 45)); // inside the range where cold ships are seen
        var battle = Fight(sim);
        var rival = battle.For(Side.Rival);
        Assert.True(rival.MissilesFired > 0);
        Assert.Equal(rival.MissilesFired, rival.MissileHits + rival.ShotDown + rival.Dodged + rival.RanDry + rival.Decoyed);
        Assert.True(rival.ShotDown > 0, "point defence never stopped a missile");
        Assert.True(battle.For(Side.Player).ShipsLost.Count > 0 || rival.DamageDealt > 0);
    }

    [Fact]
    public void Frigates_close_in_and_win_against_a_lone_destroyer()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 3, PlayerDestroyers: 0, RivalFrigates: 0, RivalDestroyers: 1,
            PlayerStance: Stance.Engage, DistanceMm: 40));
        var battle = Fight(sim, 24 * 3);
        var player = battle.For(Side.Player);
        Assert.True(player.RailHits > 0, "railguns never hit");
        Assert.True(battle.For(Side.Rival).ShipsLost.Count == 1 || sim.World.Fleets.First(f => f.Side == Side.Rival).Retreating);
    }

    [Fact]
    public void An_attack_order_flies_the_fleet_to_the_enemy()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 2, PlayerDestroyers: 0, RivalFrigates: 1, RivalDestroyers: 0, DistanceMm: 400));
        var player = FleetOf(sim, Side.Player);
        var rival = FleetOf(sim, Side.Rival);
        var req = new FlightRequest(null, rival.Position, 0.5, ArrivalStyle.Brake, HoldStyle.Drift, 0, TargetFleetId: rival.Id);
        var plan = FlightPlanner.Plan(sim.World, player, req);
        Assert.True(plan.Ok, plan.Problem);
        sim.Issue(new PlanFlightCommand(player.Id, req));
        var battle = Fight(sim);
        Assert.True(battle.For(Side.Player).RailShots > 0);
    }

    [Fact]
    public void A_fast_pass_gives_less_time_in_range_than_braking()
    {
        int Shots(ArrivalStyle style)
        {
            var sim = Sandbox(new SandboxSetup(PlayerFrigates: 2, PlayerDestroyers: 0, RivalFrigates: 2, RivalDestroyers: 0,
                PlayerStance: Stance.Hold, RivalStance: Stance.Hold, DistanceMm: 1200), seed: 5);
            var player = FleetOf(sim, Side.Player);
            var rival = FleetOf(sim, Side.Rival);
            sim.Issue(new PlanFlightCommand(player.Id, new FlightRequest(null, rival.Position, 1, style, HoldStyle.Drift, 0, TargetFleetId: rival.Id)));
            while (sim.World.Hour < 30) sim.Step();
            return sim.World.Battles.Sum(b => b.For(Side.Player).RailShots);
        }
        var fast = Shots(ArrivalStyle.FastPass); var brake = Shots(ArrivalStyle.Brake);
        Assert.True(fast < brake, $"fast {fast} brake {brake}");
    }

    [Fact]
    public void Battles_replay_exactly()
    {
        var sim = Sandbox(new SandboxSetup(DistanceMm: 45), seed: 9);
        var player = FleetOf(sim, Side.Player);
        for (var i = 0; i < 400; i++) sim.Step();                     // into battle time
        Assert.NotEqual(0, sim.World.Sub);
        sim.Issue(new SetHoldCommand(player.Id, HoldStyle.Drift)); // a command given in battle time
        sim.StepMany(30);
        Assert.NotEmpty(sim.Replay.Commands);
        Assert.Contains(sim.Replay.Commands, c => c.Sub != 0);
        var result = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(result.Exact, result.Message);
        Assert.Equal(GameTime.TicksPerDay, result.ReachedTick); // replays run to the last daily checkpoint
    }

    [Fact]
    public void Damaged_fleets_retreat_home()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 1, PlayerDestroyers: 0, RivalFrigates: 0, RivalDestroyers: 0, DistanceMm: 60));
        var player = FleetOf(sim, Side.Player);
        var ship = sim.World.ShipsOf(player).First();
        // Put a rival in sight and knock the frigate down to a third of its armour.
        sim = Sandbox(new SandboxSetup(PlayerFrigates: 1, PlayerDestroyers: 0, RivalFrigates: 1, RivalDestroyers: 0,
            RivalStance: Stance.Hold, DistanceMm: 25));
        player = FleetOf(sim, Side.Player);
        ship = sim.World.ShipsOf(player).First();
        ship.Armour = 30;
        sim.StepMany(1);
        Assert.True(player.Retreating);
        Assert.Equal(sim.World.Bodies.Single(b => b.Key == "verdant").Id, player.Flight?.TargetBodyId);
    }

    [Fact]
    public void Ships_repair_and_rearm_at_home()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 4, GameLog.Memory());
        var fleet = sim.World.Fleets.First(f => f.Side == Side.Player);
        var dd = sim.World.ShipsOf(fleet).First(s => s.ClassId == "missile-destroyer");
        dd.Armour = 20;
        dd.Missiles = 0;
        dd.Disabled = Module.Engines;
        sim.StepMany(12 * GameTime.TicksPerDay);
        Assert.Equal(160, dd.Armour, 6);
        Assert.Null(dd.Disabled);
        Assert.True(dd.Missiles > 0);
    }

    [Fact]
    public void Healthy_battles_break_no_rules()
    {
        foreach (var seed in new ulong[] { 1, 2, 3 })
        {
            var sim = Sandbox(new SandboxSetup(4, 2, 3, 3, 2, 1, Stance.Engage, Stance.Engage, 80), seed);
            sim.StepMany(48);
            Assert.Equal(0, sim.RuleViolations);
        }
    }
}
