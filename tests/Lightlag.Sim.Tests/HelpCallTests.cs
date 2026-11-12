using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Planets, cargo runs and fleets call for help; free fleets with a role answer within their rules.</summary>
public class HelpCallTests
{
    private static Simulation NewGame()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    /// <summary>The player's fleet split into a guard of (count) ships that stays at home, and the enemy fleet placed near a planet of ours.</summary>
    private static (Fleet Guard, Fleet Enemy, Body Planet) Setup(Simulation sim, int enemyShips = 1)
    {
        var w = sim.World;
        var home = Economy.HomeOf(w, Side.Player)!;
        var guard = w.Fleets.First(f => f.Side == Side.Player);
        var enemy = w.Fleets.First(f => f.Side == Side.Rival);
        // Trim the enemy to the ships wanted (the rest go home out of the way).
        if (enemy.ShipIds.Count > enemyShips)
        {
            new SplitFleetCommand(enemy.Id, enemy.ShipIds.Skip(enemyShips).ToList()).Apply(w, sim);
            var rest = w.Fleets.Last(f => f.Side == Side.Rival);
            rest.Position = new Vec2(-900_000, -900_000);
            rest.Flight = null;
            Fleets.EnterDrift(rest);
        }
        guard.Flight = null;
        guard.Chase = null;
        enemy.Flight = null;
        enemy.Chase = null;
        Fleets.EnterDrift(enemy);
        enemy.Velocity = Vec2.Zero;
        // Not quite in contact with the guard: a planet sees it close by.
        enemy.Position = home.Position + new Vec2(0.05 * Physics.MmPerAu, 0);
        Sensors.Reveal(w, Side.Player, enemy);
        return (guard, enemy, home);
    }

    [Fact]
    public void A_planet_that_sees_an_enemy_close_calls_for_help()
    {
        var sim = NewGame();
        var (_, enemy, home) = Setup(sim);
        HelpCalls.Update(sim);
        var call = HelpCalls.CallAbout(sim.World, Side.Player, enemy.Id);
        Assert.NotNull(call);
        Assert.True(call!.Reasons.HasFlag(CallReason.EnemyNear));
        // The nearest planet of ours makes the call (the home moon can be nearer than home itself).
        var nearest = sim.World.Bodies.Where(b => b.Owner == Side.Player).OrderBy(b => Vec2.Distance(b.Position, enemy.Position)).ThenBy(b => b.Id).First();
        Assert.Equal(nearest.Id, call.BodyId);
        _ = home;
    }

    [Fact]
    public void A_fleet_with_no_role_does_not_answer()
    {
        var sim = NewGame();
        var (guard, _, _) = Setup(sim);
        HelpCalls.Update(sim);
        Assert.Null(guard.AnsweringCallId);
    }

    [Fact]
    public void A_defence_fleet_answers_a_planet_and_goes_after_the_enemy()
    {
        var sim = NewGame();
        var (guard, enemy, _) = Setup(sim);
        new SetFleetRoleCommand(guard.Id, FleetRole.Defence).Apply(sim.World, sim);
        HelpCalls.Update(sim);
        Assert.NotNull(guard.AnsweringCallId);
        Assert.True(guard.Flight != null || guard.Chase != null);
        Assert.NotNull(HelpCalls.HandledBy(sim.World, Side.Player, enemy.Id));
    }

    [Fact]
    public void An_anti_piracy_fleet_leaves_a_planet_call_to_others()
    {
        var sim = NewGame();
        var (guard, _, _) = Setup(sim);
        new SetFleetRoleCommand(guard.Id, FleetRole.AntiPiracy).Apply(sim.World, sim);
        HelpCalls.Update(sim);
        Assert.Null(guard.AnsweringCallId);
    }

    [Fact]
    public void A_fleet_out_of_range_stays()
    {
        var sim = NewGame();
        var (guard, enemy, _) = Setup(sim);
        new SetFleetRoleCommand(guard.Id, FleetRole.Support, new RoleRules { Range = RoleRange.Near }).Apply(sim.World, sim);
        guard.Position = enemy.Position + new Vec2(0.5 * Physics.MmPerAu, 0);
        HelpCalls.Update(sim);
        Assert.Null(guard.AnsweringCallId);
    }

    [Fact]
    public void Odds_the_rules_do_not_allow_mean_no_answer_and_no_handling()
    {
        var sim = NewGame();
        var w = sim.World;
        var (guard, enemy, _) = Setup(sim, enemyShips: 99);
        // One ship of ours against the whole enemy fleet, wanting clearly better odds.
        new SplitFleetCommand(guard.Id, guard.ShipIds.Skip(1).ToList()).Apply(w, sim);
        foreach (var other in w.Fleets.Where(f => f.Side == Side.Player && f.Id != guard.Id)) other.Position = new Vec2(900_000, 900_000);
        new SetFleetRoleCommand(guard.Id, FleetRole.Defence, new RoleRules { Odds = RoleOdds.Stronger }).Apply(w, sim);
        Assert.True(HelpCalls.EnemyPower(HelpCalls.SightingOf(w, Side.Player, enemy.Id)!) > Stats.Power(w, guard));
        HelpCalls.Update(sim);
        Assert.Null(guard.AnsweringCallId);
        Assert.Null(HelpCalls.HandledBy(w, Side.Player, enemy.Id));
    }

    [Fact]
    public void A_direct_order_takes_a_fleet_off_its_call()
    {
        var sim = NewGame();
        var (guard, _, home) = Setup(sim);
        new SetFleetRoleCommand(guard.Id, FleetRole.Defence).Apply(sim.World, sim);
        HelpCalls.Update(sim);
        Assert.NotNull(guard.AnsweringCallId);
        sim.Carry(new StopFleetCommand(guard.Id));
        Assert.Null(guard.AnsweringCallId);
    }

    [Fact]
    public void When_the_call_is_over_the_fleet_goes_back_to_its_post()
    {
        var sim = NewGame();
        var w = sim.World;
        var (guard, enemy, home) = Setup(sim);
        new SetFleetRoleCommand(guard.Id, FleetRole.Defence).Apply(w, sim);
        HelpCalls.Update(sim);
        Assert.NotNull(guard.AnsweringCallId);
        // The enemy is gone from sight for good.
        w.Sightings.RemoveAll(s => s.FleetId == enemy.Id);
        guard.Chase = null;
        guard.Flight = null;
        guard.Position = home.Position + new Vec2(0.2 * Physics.MmPerAu, 0);
        Fleets.EnterDrift(guard);
        guard.Velocity = Vec2.Zero;
        HelpCalls.Update(sim);
        Assert.Null(guard.AnsweringCallId);
        Assert.Equal(home.Id, guard.Flight?.TargetBodyId);
    }

    [Fact]
    public void A_patrol_goes_back_to_patrolling_after_a_call()
    {
        var sim = NewGame();
        var w = sim.World;
        var (guard, enemy, home) = Setup(sim);
        new SetFleetRoleCommand(guard.Id, FleetRole.Defence).Apply(w, sim);
        guard.Mission = Mission.Patrol;
        guard.MissionBodyId = home.Id;
        HelpCalls.Update(sim);
        Assert.NotNull(guard.AnsweringCallId);
        Assert.Equal(Mission.None, guard.Mission);
        w.Sightings.RemoveAll(s => s.FleetId == enemy.Id);
        HelpCalls.Update(sim);
        Assert.Null(guard.AnsweringCallId);
        Assert.Equal(Mission.Patrol, guard.Mission);
        Assert.Equal(home.Id, guard.MissionBodyId);
    }

    [Fact]
    public void Calls_and_roles_survive_a_save()
    {
        var sim = NewGame();
        var (guard, enemy, _) = Setup(sim);
        new SetFleetRoleCommand(guard.Id, FleetRole.Support, new RoleRules { Range = RoleRange.Anywhere, Odds = RoleOdds.Any, ReturnAfter = false }).Apply(sim.World, sim);
        HelpCalls.Update(sim);
        var back = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        var g = back.World.FindFleet(guard.Id)!;
        Assert.Equal(FleetRole.Support, g.Role);
        Assert.Equal(RoleRange.Anywhere, g.RoleRules.Range);
        Assert.False(g.RoleRules.ReturnAfter);
        Assert.Equal(guard.AnsweringCallId, g.AnsweringCallId);
        Assert.NotNull(HelpCalls.CallAbout(back.World, Side.Player, enemy.Id));
        Assert.Equal(sim.StateHash(), back.StateHash());
    }

    [Fact]
    public void Ships_move_between_fleets_at_the_same_place()
    {
        var sim = NewGame();
        var w = sim.World;
        var fleet = w.Fleets.First(f => f.Side == Side.Player);
        var count = fleet.ShipIds.Count;
        Assert.True(count >= 3);
        new SplitFleetCommand(fleet.Id, new List<int> { fleet.ShipIds[0] }).Apply(w, sim);
        var other = w.Fleets.Last(f => f.Side == Side.Player);
        other.Position = fleet.Position;
        var moving = fleet.ShipIds[0];
        new MoveShipsCommand(fleet.Id, new List<int> { moving }, other.Id).Apply(w, sim);
        Assert.Contains(moving, other.ShipIds);
        Assert.DoesNotContain(moving, fleet.ShipIds);
        Assert.Equal(other.Id, w.FindShip(moving)!.FleetId);
        Assert.Equal(count, fleet.ShipIds.Count + other.ShipIds.Count);
        Assert.Empty(sim.CheckRules());
    }

    [Fact]
    public void A_split_keeps_the_role()
    {
        var sim = NewGame();
        var w = sim.World;
        var fleet = w.Fleets.First(f => f.Side == Side.Player);
        new SetFleetRoleCommand(fleet.Id, FleetRole.AntiPiracy, new RoleRules { Odds = RoleOdds.Stronger }).Apply(w, sim);
        new SplitFleetCommand(fleet.Id, new List<int> { fleet.ShipIds[0] }).Apply(w, sim);
        var other = w.Fleets.Last(f => f.Side == Side.Player);
        Assert.Equal(FleetRole.AntiPiracy, other.Role);
        Assert.Equal(RoleOdds.Stronger, other.RoleRules.Odds);
    }

    /// <summary>A tanker of ours in open space, an enemy raider (1 ship) beside it, and our 3-ship anti-piracy fleet within reach.</summary>
    private static (Fleet Hunters, Fleet Tanker, Fleet Pirate, Fleet Pack) PirateSetup(Simulation sim)
    {
        var w = sim.World;
        var hunters = w.Fleets.First(f => f.Side == Side.Player);
        // The home fleet starts with a supply tender too: it stays at home out of the way.
        var support = w.ShipsOf(hunters).Where(s => !Planner.Warship(s)).Select(s => s.Id).ToList();
        if (support.Count > 0)
        {
            new SplitFleetCommand(hunters.Id, support).Apply(w, sim);
            var tender = w.Fleets.Last(f => f.Side == Side.Player);
            tender.Position = new Vec2(-900_000, 900_000);
            tender.Flight = null;
            Fleets.EnterDrift(tender);
        }
        Assert.Equal(3, hunters.ShipIds.Count);
        var tankerShip = Economy.NewShip(w, Side.Player, "tanker");
        var tanker = Fleets.Create(w, Side.Player, new[] { tankerShip }, new Vec2(300_000, 300_000));
        var rival = w.Fleets.First(f => f.Side == Side.Rival);
        new SplitFleetCommand(rival.Id, rival.ShipIds.Skip(1).ToList()).Apply(w, sim);
        var pack = w.Fleets.Last(f => f.Side == Side.Rival);
        var pirate = rival;
        foreach (var f in new[] { hunters, tanker, pirate, pack })
        {
            f.Flight = null;
            f.Chase = null;
            Fleets.EnterDrift(f);
            f.Velocity = Vec2.Zero;
        }
        pirate.Position = tanker.Position + new Vec2(GameData.Combat.ContactRangeMm * 0.8, 0);
        pack.Position = new Vec2(-900_000, 900_000);
        hunters.Position = tanker.Position + new Vec2(0, 0.2 * Physics.MmPerAu);
        Sensors.Reveal(w, Side.Player, pirate);
        new SetFleetRoleCommand(hunters.Id, FleetRole.AntiPiracy).Apply(w, sim);
        return (hunters, tanker, pirate, pack);
    }

    [Fact]
    public void Three_anti_piracy_ships_handle_one_pirate_without_asking()
    {
        var sim = NewGame();
        var (hunters, _, pirate, _) = PirateSetup(sim);
        Assert.Single(pirate.ShipIds);
        Assert.NotNull(HelpCalls.HandledBy(sim.World, Side.Player, pirate.Id));
        HelpCalls.Update(sim);
        var call = HelpCalls.CallAbout(sim.World, Side.Player, pirate.Id)!;
        Assert.True(call.Reasons.HasFlag(CallReason.Raiding));
        Assert.Equal(call.Id, hunters.AnsweringCallId);
        Assert.NotNull(HelpCalls.HandledBy(sim.World, Side.Player, pirate.Id));
    }

    [Fact]
    public void Running_into_a_bigger_pack_is_not_in_hand_and_asks_for_orders()
    {
        var sim = NewGame();
        var w = sim.World;
        var (hunters, tanker, pirate, pack) = PirateSetup(sim);
        HelpCalls.Update(sim);
        Assert.NotNull(hunters.AnsweringCallId);
        // The rest of the rival fleet joins the pirate: now 3 enemy ships against our 3, an even fight: it asks first.
        new MergeFleetsCommand(pirate.Id, pack.Id).Apply(w, sim); // too far: refused
        pack.Position = pirate.Position;
        new MergeFleetsCommand(pirate.Id, pack.Id).Apply(w, sim);
        Assert.Equal(3, w.ShipsOf(pirate).Count(Planner.Warship)); // and the rival's supply tender
        Sensors.Reveal(w, Side.Player, pirate);
        Assert.Null(HelpCalls.HandledBy(w, Side.Player, pirate.Id));
    }

    [Fact]
    public void A_role_game_runs_with_rules_checked()
    {
        var sim = NewGame();
        foreach (var f in sim.World.Fleets.Where(f => f.Side == Side.Player)) new SetFleetRoleCommand(f.Id, FleetRole.Support).Apply(sim.World, sim);
        sim.StepMany(20 * GameTime.TicksPerDay);
        Assert.Equal(0, sim.RuleViolations);
    }
}
