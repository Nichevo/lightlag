using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Bugs found by the Plan 3 hardening batches (AI-vs-AI games, one and ten years, two and three races).</summary>
public class Plan3HardeningTests
{
    private static (Simulation Sim, Fleet Mine, Fleet Theirs) Sandbox(double distance)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 5, GameLog.Memory(),
            sandbox: new SandboxSetup(2, 0, 1, 0, 1, 1, Stance.Engage, Stance.Hold, distance));
        var mine = sim.World.Fleets.First(f => f.Side == Side.Player);
        var theirs = sim.World.Fleets.First(f => f.Side == Side.Rival);
        Sensors.Reveal(sim.World, Side.Player, theirs);
        return (sim, mine, theirs);
    }

    [Fact]
    public void A_fleet_out_of_rail_slugs_does_not_close_in_and_its_empty_guns_do_not_keep_the_battle_going()
    {
        var (sim, mine, theirs) = Sandbox(18);
        var w = sim.World;
        Assert.True(Combat.WouldEngage(w, mine, theirs));
        foreach (var s in w.ShipsOf(mine)) s.Ammo = 0;
        foreach (var s in w.ShipsOf(theirs)) { s.Ammo = 0; s.Missiles = 0; }
        Assert.False(Combat.WouldEngage(w, mine, theirs));
        // Neither side can shoot: after the first contact the battle goes quiet (a standoff), not an endless fight.
        while (w.Hour < 6) sim.Step();
        Assert.True(w.ActiveBattle == null || Combat.Standoff(w), $"missiles {w.Missiles.Count}, maneuvering {mine.Maneuvering}/{theirs.Maneuvering}, last action {w.ActiveBattle?.LastActionHour} now {w.Hour}, my missiles {w.ShipsOf(mine).Sum(s => s.Missiles)}");
        Assert.Equal(0, (w.ActiveBattle ?? w.Battles.Last()).For(Side.Player).RailShots);
    }

    [Fact]
    public void A_supply_ship_reloading_missiles_never_hands_out_ammunition_it_no_longer_has()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 3, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        var w = sim.World;
        var fleet = w.Fleets.First(f => f.Side == Side.Player && w.ShipsOf(f).Any(s => s.ClassId == "missile-destroyer") && w.ShipsOf(f).Any(SupplyLines.IsSupplyShip));
        var tender = w.ShipsOf(fleet).First(SupplyLines.IsSupplyShip);
        var destroyer = w.ShipsOf(fleet).First(s => s.ClassId == "missile-destroyer");
        // The destroyer comes first (lowest supplies), wants a missile and no slugs; the others want slugs.
        destroyer.Supplies = 1;
        destroyer.Ammo = SupplyLines.MaxAmmo(destroyer);
        destroyer.Missiles = 0;
        foreach (var s in w.ShipsOf(fleet).Where(s => s != tender && s != destroyer)) s.Ammo = 0;
        tender.CargoAmmo = 1.5;
        sim.Step();
        Assert.True(tender.CargoAmmo >= -1e-9, $"cargo ammunition {tender.CargoAmmo}");
        Assert.Equal(0, sim.RuleViolations);
    }

    [Fact]
    public void The_choice_log_may_hold_a_whole_long_game()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 3, GameLog.Memory());
        var w = sim.World;
        for (var i = 0; i < RuleBook.ListLimit + 50; i++) Choices.Note(w, Side.Player, "test", $"choice {i}");
        Assert.Equal(RuleBook.ListLimit + 50, w.Choices.Count);
        Assert.Empty(RuleBook.Default().Check(w));
    }
}
