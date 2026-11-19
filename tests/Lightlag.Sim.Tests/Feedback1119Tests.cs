using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Playtest feedback 2026-11-19: research costs, repairs and help, the low-ammunition rule, ship orbits, Response watch.</summary>
public class Feedback1119Tests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Simulation Sandbox(SandboxSetup setup, ulong seed = 3)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), sandbox: setup);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet PlayerFleet(Simulation sim) => sim.World.Fleets.Where(f => f.Side == Side.Player).OrderByDescending(f => f.ShipIds.Count).First();

    private static void MoveAway(Simulation sim, Fleet f, double au = 0.6)
    {
        f.Flight = null;
        Fleets.EnterDrift(f);
        f.Velocity = Vec2.Zero;
        f.Position = Economy.HomeOf(sim.World, f.Side)!.Position + new Vec2(au * Physics.MmPerAu, 0);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
    }

    [Fact]
    public void Split_says_why_not_and_training_is_paid_from_buildings()
    {
        var sim = NewGame();
        sim.Step();
        var w = sim.World;
        var team = Economy.Of(w, Side.Player).Teams[0];
        sim.Issue(new SplitTeamCommand(Side.Player, team.Id, "sensors"));
        sim.Step();
        // Both halves are under twice the minimum: the reason says how to fix it.
        var why = Research.CanSplit(w, Side.Player, team.Id, "drives");
        Assert.NotNull(why);
        Assert.Contains("train it", why);
        var before = Budget.Of(w, Side.Player).ResearchSpent;
        Economy.Of(w, Side.Player).Industry += 500;
        Budget.Of(w, Side.Player).Pots[(int)Pot.Buildings].Saved += 500;
        sim.Issue(new TrainTeamCommand(Side.Player, team.Id));
        sim.Step();
        Assert.Equal(before + Research.TrainCost(w, Side.Player), Budget.Of(w, Side.Player).ResearchSpent, 3);
    }

    [Fact]
    public void Send_for_repair_splits_the_ship_off_to_the_nearest_yard_and_it_will_rejoin()
    {
        var sim = NewGame();
        var w = sim.World;
        w.Minds.Clear();
        var f = PlayerFleet(sim);
        Assert.True(f.ShipIds.Count >= 2);
        MoveAway(sim, f);
        var hurt = w.ShipsOf(f).First();
        hurt.Armour *= 0.5;
        Assert.True(BattleRules.Hurt(w, hurt));
        sim.Issue(new SendForRepairCommand(f.Id, new List<int> { hurt.Id }) { Source = "player" });
        sim.ApplyPending();
        var g = w.FleetOf(hurt)!;
        Assert.NotEqual(f.Id, g.Id);
        Assert.True(g.Retreating);
        Assert.Equal(f.Id, g.RejoinFleetId);
        Assert.Equal(BattleRules.NearestYard(w, Side.Player, g.Position)!.Id, g.Flight?.TargetBodyId);
    }

    [Fact]
    public void Send_help_flies_a_free_ship_out_and_a_tender_patches_a_crippled_ship()
    {
        var sim = NewGame();
        var w = sim.World;
        w.Minds.Clear();
        var f = PlayerFleet(sim);
        var lame = w.ShipsOf(f).First();
        sim.Issue(new SplitFleetCommand(f.Id, new List<int> { lame.Id }));
        sim.ApplyPending();
        var hurt = w.FleetOf(lame)!;
        MoveAway(sim, hurt, 0.2);
        lame.Armour = 0;
        lame.Crippled = true;
        Assert.Null(BattleRules.HelpProblem(w, hurt));
        sim.Issue(new SendHelpCommand(hurt.Id) { Source = "player" });
        sim.ApplyPending();
        var helper = w.Fleets.Single(g => g.Helper);
        Assert.Equal(hurt.Id, helper.JoinFleetId);

        // A tender's crew patch the hull until the ship can move again.
        for (var h = 0; h < 24 * 6 && lame.Crippled; h++) Wounds.TenderPatch(w, lame, Wounds.D.TenderHullPerDay);
        Assert.False(lame.Crippled);
        Assert.True(lame.Armour <= Tech.MaxArmour(w, lame) * Wounds.D.MendBarsTo + 1e-6);
    }

    [Fact]
    public void Low_ammunition_waits_until_every_missile_is_away_and_never_at_long_odds()
    {
        var sim = Sandbox(new SandboxSetup(0, 2, 0, 1, RivalStance: Stance.Hold, DistanceMm: 25));
        var w = sim.World;
        var f = w.Fleets.First(x => x.Side == Side.Player);
        var ships = w.ShipsOf(f).ToList();
        foreach (var s in ships) { s.Ammo = 0; s.Missiles = 0; }
        ships[0].Missiles = 1;
        Assert.False(BattleRules.OutOfShots(w, f)); // one missile left: fire it first
        ships[0].Missiles = 0;
        Assert.True(BattleRules.OutOfShots(w, f));
        // No enemy close by (or only far weaker ones): no reason to leave for ammunition.
        foreach (var e in w.Fleets.Where(e => e.Side == Side.Rival)) e.Position = f.Position + new Vec2(10 * GameData.Combat.ContactRangeMm, 0);
        Assert.True(BattleRules.FarStronger(w, f));
        Assert.Null(BattleRules.Trigger(w, f, PullRule.For(PullPreset.Cautious)) is string why && why.Contains("ammunition") ? why : null);
    }

    [Fact]
    public void Fleets_in_orbit_take_at_least_two_days_a_lap_at_the_same_height()
    {
        var sim = NewGame();
        foreach (var b in sim.World.Bodies.Where(b => b.ParentId != null))
        {
            var r = Fleets.OrbitRadiusFor(b);
            Assert.True(r >= b.Radius);
            Assert.True(Fleets.OrbitPeriodHours(b, r) >= Fleets.FleetOrbitHours - 1e-9);
        }
    }

    [Fact]
    public void Auto_does_not_flip_a_fleets_pull_back_rule_within_a_day()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = PlayerFleet(sim);
        f.AutoRuleHour = w.Hour;
        var preset = f.Rule.Preset;
        var other = preset == PullPreset.Cautious ? PullPreset.Standard : PullPreset.Cautious;
        var offered = new List<GameCommand>();
        BattleRules.Steward(sim, Side.Player, (area, key, text, cmd, label, bodyId, fleetId, advice) => { offered.Add(cmd); return true; });
        // Whatever Auto would pick now, it waits (unless the fleet is at home or a blockaded colony).
        if (BattleRules.Situation(w, f) is not ("home" or "colony")) Assert.Empty(offered);
        Assert.NotEqual(preset, other);
    }
}
