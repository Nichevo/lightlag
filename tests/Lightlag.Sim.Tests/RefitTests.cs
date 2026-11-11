using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Send for refit, yard bays, and replenishment (supply runs, lost ships offered for rebuilding).</summary>
public class RefitTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet Home(Simulation sim) => sim.World.Fleets.First(f => f.Side == Side.Player);

    /// <summary>Strip one module from a ship so it is off its class's design.</summary>
    private static void Age(Simulation sim, Ship s)
    {
        var fit = Fitting.FitOf(s).ToList();
        var slot = fit.FindIndex(m => m != "" && !GameData.ShipClass(s.ClassId).SlotList[fit.IndexOf(m)].Type.Equals("E"));
        fit[slot] = "";
        Fitting.Apply(sim.World, s, fit);
        Assert.True(Refits.Outdated(sim.World, s));
    }

    [Fact]
    public void A_fleet_sent_for_refit_takes_bays_pays_and_comes_out_on_the_current_design()
    {
        var sim = NewGame();
        sim.Step();
        var f = Home(sim);
        var ships = sim.World.ShipsOf(f).Where(Planner.Warship).ToList();
        foreach (var s in ships) Age(sim, s);
        var industry = Economy.Of(sim.World, Side.Player).Industry = 1000;
        sim.Issue(new SendForRefitCommand(Side.Player, f.Id));
        sim.StepMany(2);
        Assert.NotNull(f.Refit);
        Assert.True(Economy.Of(sim.World, Side.Player).Industry < industry);
        var yard = Economy.ShipyardOf(sim.World, Side.Player)!;
        Assert.True(ships.Count(s => s.RefitHoursLeft > 0) <= Refits.Bays(sim.World, yard, Side.Player));
        Assert.NotNull(Refits.Status(sim.World, f));
        sim.StepMany(GameTime.TicksPerDay * 12);
        Assert.Null(f.Refit);
        Assert.All(ships, s => Assert.False(Refits.Outdated(sim.World, s)));
    }

    [Fact]
    public void Nothing_to_refit_and_no_refits_in_battle()
    {
        var sim = NewGame();
        sim.Step();
        var f = Home(sim);
        Assert.Equal("Every ship is on its current design", Refits.CanSend(sim.World, f));
        Age(sim, sim.World.ShipsOf(f).First(Planner.Warship));
        f.FightPower = 100;
        Assert.Equal("No refits in battle", Refits.CanSend(sim.World, f));
    }

    [Fact]
    public void Refit_half_sends_half_the_fleet()
    {
        var sim = NewGame();
        sim.Step();
        var f = Home(sim);
        var count = f.ShipIds.Count;
        foreach (var s in sim.World.ShipsOf(f).Where(Planner.Warship).ToList()) Age(sim, s);
        sim.Issue(new SendForRefitCommand(Side.Player, f.Id, Half: true));
        sim.ApplyPending();
        var going = sim.World.Fleets.Single(x => x.Side == Side.Player && x.Refit != null);
        Assert.NotEqual(f.Id, going.Id);
        Assert.Equal((count + 1) / 2, going.ShipIds.Count);
    }

    [Fact]
    public void Refits_wait_when_the_bays_are_full()
    {
        var sim = NewGame();
        sim.Step();
        var f = Home(sim);
        var yard = Economy.ShipyardOf(sim.World, Side.Player)!;
        foreach (var s in sim.World.ShipsOf(f)) { Age(sim, s); s.Armour = Tech.MaxArmour(sim.World, s) * 0.5; } // every ship under repair fills the bays
        Assert.True(Refits.BaysBusy(sim.World, yard, Side.Player) >= Refits.Bays(sim.World, yard, Side.Player));
        Economy.Of(sim.World, Side.Player).Industry = 1000;
        sim.Issue(new SendForRefitCommand(Side.Player, f.Id));
        sim.StepMany(2);
        Assert.Equal(RefitStage.Waiting, f.Refit!.Stage);
        Assert.All(sim.World.ShipsOf(f), s => Assert.Equal(0, s.RefitHoursLeft));
    }

    [Fact]
    public void Auto_supply_sends_a_free_tanker_to_a_fleet_running_low()
    {
        var sim = NewGame();
        sim.Step();
        var f = Home(sim);
        var tankerShip = Economy.NewShip(sim.World, Side.Player, "tanker");
        var tanker = Fleets.Create(sim.World, Side.Player, new[] { tankerShip }, f.Position);
        tanker.Hold = f.Hold; tanker.OrbitBodyId = f.OrbitBodyId; tanker.OrbitRadius = f.OrbitRadius; tanker.OrbitAngle = f.OrbitAngle;
        var pale = sim.World.Bodies.Single(b => b.Key == "pale");
        f.Flight = null;
        f.Position = pale.Position + new Vec2(Fleets.OrbitRadiusFor(pale), 0);
        Fleets.EnterOrbit(f, pale);
        foreach (var s in sim.World.ShipsOf(f)) { s.Position = f.Position + s.FormationOffset; s.Supplies = Fitting.MaxSupplies(sim.World, s) * 0.2; }
        sim.StepMany(Stewards.EveryTicks * 2);
        Assert.Equal(f.Id, tanker.SupplyRunFor);
        Assert.True(tanker.AutoRun);
        Assert.Contains(Economy.Of(sim.World, Side.Player).AutoNotes, n => n.Area == AutoArea.Supply);
    }

    [Fact]
    public void A_lost_ship_offered_for_rebuilding_joins_its_old_fleet()
    {
        var sim = NewGame();
        sim.Step();
        var f = Home(sim);
        var ship = sim.World.ShipsOf(f).First(Planner.Warship);
        Combat.Destroy(sim, ship);
        sim.StepMany(Stewards.EveryTicks);
        var need = Economy.Of(sim.World, Side.Player).Needs.Single(n => n.Key == "lost:" + ship.Name);
        var act = Assert.IsType<QueueOrderCommand>(need.Act);
        Assert.Equal(f.Id, act.JoinFleetId);
    }
}
