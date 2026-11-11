using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Five damage bars, crippled ships, wrecks, repairs and pull-back rules.</summary>
public class DamageTests
{
    private static Simulation Sandbox(SandboxSetup setup, ulong seed = 3)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), sandbox: setup);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet FleetOf(Simulation sim, Side side) => sim.World.Fleets.First(f => f.Side == side);

    private static Battle Fight(Simulation sim, int maxHours = 72)
    {
        var end = sim.World.Tick + maxHours;
        while (sim.World.Tick < end && (sim.World.Battles.Count == 0 || sim.World.ActiveBattle != null)) sim.Step();
        Assert.NotEmpty(sim.World.Battles);
        return sim.World.Battles[0];
    }

    [Fact]
    public void A_hit_takes_hull_and_one_other_bar()
    {
        var sim = Sandbox(new SandboxSetup(1, 0, 1, 0));
        var ship = sim.World.ShipsOf(FleetOf(sim, Side.Player)).First();
        var max = Tech.MaxArmour(sim.World, ship);
        Wounds.Hit(sim, null, ship, max * 0.2, Side.Rival, missile: false, engines: false);
        Assert.Equal(max * 0.8, ship.Armour, 6);
        var worn = new[] { ship.Weapons, ship.Engines, ship.Defence, ship.Crew }.Count(v => v < 1);
        Assert.Equal(1, worn);
        Assert.False(ship.Crippled);
    }

    [Fact]
    public void Aiming_for_engines_wears_the_engines_and_slows_the_ship()
    {
        var sim = Sandbox(new SandboxSetup(1, 0, 1, 0));
        var ship = sim.World.ShipsOf(FleetOf(sim, Side.Player)).First();
        var speed = Fleets.ShipSpeed(ship);
        Wounds.Hit(sim, null, ship, Tech.MaxArmour(sim.World, ship) * 0.3, Side.Rival, missile: false, engines: true);
        Assert.True(ship.Engines < 1);
        Assert.True(Fleets.ShipSpeed(ship) < speed);
    }

    [Fact]
    public void Hull_at_zero_cripples_and_more_hits_destroy()
    {
        var sim = Sandbox(new SandboxSetup(2, 0, 1, 0));
        var ship = sim.World.ShipsOf(FleetOf(sim, Side.Player)).First();
        var max = Tech.MaxArmour(sim.World, ship);
        Wounds.Hit(sim, null, ship, ship.Armour + max * 0.05, Side.Rival, missile: true, engines: false);
        Assert.True(ship.Crippled);
        Assert.Equal(0, ship.Armour);
        Assert.True(ship.Out(Module.Railguns) && ship.Out(Module.Missiles) && ship.Out(Module.PointDefence));
        Assert.Equal(Wounds.D.CrippledSpeed, Wounds.SpeedFactor(ship));
        Assert.NotNull(sim.World.FindShip(ship.Id));
        sim.CheckRules();
        Wounds.Hit(sim, null, ship, max, Side.Rival, missile: false, engines: false);
        Assert.Null(sim.World.FindShip(ship.Id));
    }

    [Fact]
    public void A_huge_hit_destroys_outright()
    {
        var sim = Sandbox(new SandboxSetup(2, 0, 1, 0));
        var ship = sim.World.ShipsOf(FleetOf(sim, Side.Player)).First();
        Wounds.Hit(sim, null, ship, ship.Armour + Tech.MaxArmour(sim.World, ship), Side.Rival, missile: false, engines: false);
        Assert.Null(sim.World.FindShip(ship.Id));
    }

    [Fact]
    public void A_crippled_ship_pulls_out_alone_and_the_fleet_fights_on()
    {
        var sim = Sandbox(new SandboxSetup(4, 0, 1, 0, RivalStance: Stance.Hold, DistanceMm: 25));
        var fleet = FleetOf(sim, Side.Player);
        var ship = sim.World.ShipsOf(fleet).First();
        sim.Step();
        Wounds.Hit(sim, null, ship, ship.Armour + 1, Side.Rival, missile: false, engines: false);
        sim.Step();
        var group = sim.World.FleetOf(ship)!;
        Assert.NotEqual(fleet.Id, group.Id);
        Assert.True(group.Retreating);
        Assert.Equal(fleet.Id, group.RejoinFleetId);
        Assert.Equal(3, fleet.ShipIds.Count);
        Assert.False(fleet.Retreating, fleet.PullReason);
    }

    [Fact]
    public void Standard_pulls_back_after_a_third_of_its_strength_and_to_the_last_does_not()
    {
        foreach (var (preset, pulls) in new[] { (PullPreset.Standard, true), (PullPreset.ToTheLast, false) })
        {
            var sim = Sandbox(new SandboxSetup(3, 0, 1, 0, RivalStance: Stance.Hold, DistanceMm: 25));
            var fleet = FleetOf(sim, Side.Player);
            sim.Issue(new SetPullRuleCommand(fleet.Id, preset));
            sim.Step();
            Assert.True(fleet.FightPower > 0);
            foreach (var s in sim.World.ShipsOf(fleet)) s.Armour *= 0.45; // about 38 % of the power, ships still over the hull line
            sim.Step();
            Assert.Equal(pulls, fleet.Retreating);
            if (pulls) Assert.Contains("strength", fleet.PullReason);
        }
    }

    [Fact]
    public void Hold_on_ignores_the_rule_and_pull_back_now_goes_at_once()
    {
        var sim = Sandbox(new SandboxSetup(3, 0, 1, 0, RivalStance: Stance.Hold, DistanceMm: 25));
        var fleet = FleetOf(sim, Side.Player);
        sim.Step();
        sim.Issue(new HoldOnCommand(fleet.Id, true));
        sim.ApplyPending();
        foreach (var s in sim.World.ShipsOf(fleet)) s.Armour *= 0.55;
        sim.Step();
        Assert.False(fleet.Retreating);
        sim.Issue(new PullBackNowCommand(fleet.Id));
        sim.ApplyPending();
        Assert.True(fleet.Retreating);
    }

    [Fact]
    public void A_split_fleet_keeps_its_rule()
    {
        var sim = Sandbox(new SandboxSetup(3, 0, 1, 0));
        var fleet = FleetOf(sim, Side.Player);
        sim.Issue(new SetPullRuleCommand(fleet.Id, PullPreset.FightOn));
        sim.Issue(new SplitFleetCommand(fleet.Id, new List<int> { fleet.ShipIds[0] }));
        sim.ApplyPending();
        var other = sim.World.Fleets.Single(f => f.Side == Side.Player && f.Id != fleet.Id);
        Assert.Equal(PullPreset.FightOn, other.Rule.Preset);
        Assert.True(other.RuleByHand);
    }

    [Fact]
    public void A_fleet_pulling_back_is_let_go()
    {
        var sim = Sandbox(new SandboxSetup(1, 0, 3, 0, PlayerStance: Stance.Hold, RivalStance: Stance.Engage, DistanceMm: 25));
        var mine = FleetOf(sim, Side.Player);
        var theirs = FleetOf(sim, Side.Rival);
        sim.Step();
        Assert.True(Combat.WouldEngage(sim.World, theirs, mine));
        mine.Retreating = true;
        Assert.False(Combat.WouldEngage(sim.World, theirs, mine));
    }

    [Fact]
    public void Battles_end_with_a_named_ending()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 4, PlayerDestroyers: 0, RivalFrigates: 0, RivalDestroyers: 2,
            PlayerStance: Stance.Hold, DistanceMm: 45));
        var battle = Fight(sim);
        Assert.NotNull(battle.Ending);
        Assert.True(battle.For(Side.Player).ShipsIn > 0 && battle.For(Side.Rival).ShipsIn > 0);
    }

    [Fact]
    public void Yard_repairs_cost_alloys_and_go_slowly_without()
    {
        var sim = NewGame();
        sim.Step();
        var yard = Economy.ShipyardOf(sim.World, Side.Player)!;
        var ship = sim.World.Ships.First(s => s.Side == Side.Player && GameData.ShipClass(s.ClassId).Materials?.ContainsKey("alloys") == true);
        var max = Tech.MaxArmour(sim.World, ship);
        ship.Armour = max * 0.4;
        yard.Stock["alloys"] = 100;
        Wounds.YardRepair(sim.World, ship, 24, yard);
        var withAlloys = ship.Armour;
        Assert.True(Colonies.Held(yard, "alloys") < 100);
        ship.Armour = max * 0.4;
        yard.Stock["alloys"] = 0;
        Wounds.YardRepair(sim.World, ship, 24, yard);
        Assert.True(ship.Armour < withAlloys);
        Assert.True(ship.Armour > max * 0.4);
    }

    [Fact]
    public void A_destroyed_ship_leaves_a_wreck_that_a_passing_fleet_salvages()
    {
        var sim = NewGame();
        sim.Step();
        var fleet = FleetOf(sim, Side.Player);
        var ship = sim.World.ShipsOf(fleet).First(s => Planner.Warship(s));
        Combat.Destroy(sim, ship);
        var wreck = Assert.Single(sim.World.Wrecks);
        Assert.True(wreck.Salvage.ContainsKey("alloys"));
        sim.StepMany(GameTime.TicksPerDay);
        Assert.Empty(sim.World.Wrecks);
        Assert.True(Economy.Of(sim.World, Side.Player).Totals.Salvaged > 0);
    }

    [Fact]
    public void Auto_keeps_home_guards_fighting_to_the_last()
    {
        var sim = NewGame();
        sim.StepMany(GameTime.TicksPerDay / 2);
        var fleet = FleetOf(sim, Side.Player);
        Assert.Equal("home", BattleRules.Situation(sim.World, fleet));
        Assert.Equal(PullPreset.ToTheLast, fleet.Rule.Preset);
        Assert.False(fleet.RuleByHand);
    }

    [Fact]
    public void The_same_hand_pull_back_choice_again_and_again_offers_a_new_default()
    {
        var sim = NewGame();
        sim.StepMany(3);
        var fleet = FleetOf(sim, Side.Player);
        for (var i = 0; i < GameData.Ai.Automation!.HabitChanges; i++) sim.Issue(new SetPullRuleCommand(fleet.Id, PullPreset.FightOn));
        sim.StepMany(6);
        var need = Assert.Single(Economy.Of(sim.World, Side.Player).Needs, n => n.Key == "learn-pull:home:FightOn");
        sim.Issue(new AcceptNeedCommand(Side.Player, need.Key));
        sim.ApplyPending();
        Assert.Equal(PullPreset.FightOn, BattleRules.AutoPick(sim.World, Side.Player, "home"));
    }
}
