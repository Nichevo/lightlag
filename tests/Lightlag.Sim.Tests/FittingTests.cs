using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class FittingTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static SideState Player(Simulation sim) => Economy.Of(sim.World, Side.Player);
    private static void Hour(Simulation sim) => sim.StepMany((int)Math.Round(GameTime.TicksPerDay / 24.0));
    private static Ship FirstShip(Simulation sim, string cls) => sim.World.Ships.First(s => s.Side == Side.Player && s.ClassId == cls);

    [Fact]
    public void Every_class_standard_fit_is_inside_its_budgets_and_gives_the_class_numbers()
    {
        foreach (var cls in GameData.ShipClasses.Values)
        {
            Assert.NotEmpty(cls.SlotList);
            var std = Fitting.StandardFit(cls);
            Assert.True(Fitting.PowerUsed(cls, std) <= cls.Power, cls.Id);
            Assert.True(Fitting.HeatUsed(cls, std) <= cls.HeatBudget, cls.Id);
            Assert.Equal(cls, Fitting.Compute(cls, std));
        }
    }

    [Fact]
    public void Swapping_railguns_for_missile_racks_changes_the_ship()
    {
        var cls = GameData.ShipClass("gun-frigate");
        var fit = Fitting.StandardFit(cls);
        fit[0] = "missile-rack";
        var spec = Fitting.Compute(cls, fit);
        Assert.Equal(0, spec.Railguns);
        Assert.Equal(16, spec.Missiles);
        Assert.Equal(cls.PointDefence, spec.PointDefence);
    }

    [Fact]
    public void A_refit_at_the_shipyard_costs_industry_and_changes_the_ship()
    {
        var sim = NewGame();
        Hour(sim);
        Player(sim).Industry = 1000;
        var ship = FirstShip(sim, "gun-frigate");
        var utility = GameData.ShipClass("gun-frigate").SlotList.ToList().FindIndex(x => x.Type == "U");
        var before = Fitting.Spec(ship).SensorMm;
        sim.Issue(new SetFitCommand(Side.Player, ship.Id, utility, "sensor-suite"));
        Hour(sim);
        Assert.Equal(before + GameData.Module("sensor-suite")!.SensorMm, Fitting.Spec(ship).SensorMm);
        Assert.True(Player(sim).Industry < 1000);
    }

    [Fact]
    public void A_fit_over_the_power_budget_is_refused()
    {
        var sim = NewGame();
        var cls = GameData.ShipClass("escort");
        var fit = Fitting.StandardFit(cls);
        fit[0] = "shield-emitter"; // a pair: 6 power, plus point defence 2 and the engine 2 = 10 of 9
        Assert.NotNull(Fitting.CanFit(sim.World, Side.Player, cls, fit));
    }

    [Fact]
    public void Better_engines_wait_for_research_and_then_new_ships_get_them()
    {
        var sim = NewGame();
        Hour(sim);
        var cls = GameData.ShipClass("gun-frigate");
        var engine = cls.SlotList.ToList().FindIndex(x => x.Type == "E");
        Assert.NotNull(Fitting.CanUse(sim.World, Side.Player, cls, engine, "engine-mk2"));
        Assert.Equal("engine-mk1", Fitting.BestEngine(sim.World, Side.Player));
        Player(sim).Researched.Add(Tech.EngineMk2);
        Assert.Null(Fitting.CanUse(sim.World, Side.Player, cls, engine, "engine-mk2"));
        var fit = Fitting.AutoFit(sim.World, Side.Player, cls);
        Assert.Equal("engine-mk2", fit[engine]);
        Assert.True(Fitting.Compute(cls, fit).AccelMmPerHour2 > cls.AccelMmPerHour2);
    }

    [Fact]
    public void Engine_mk2_burns_cooler()
    {
        var sim = NewGame();
        var ship = FirstShip(sim, "gun-frigate");
        Assert.Equal(1, Fitting.BurnHeatFactor(ship));
        var fit = Fitting.FitOf(ship).ToList();
        fit[GameData.ShipClass(ship.ClassId).SlotList.ToList().FindIndex(x => x.Type == "E")] = "engine-mk2";
        Fitting.Apply(sim.World, ship, fit);
        Assert.Equal(0.8, Fitting.BurnHeatFactor(ship), 9);
    }

    [Fact]
    public void Gentle_burns_make_less_heat_than_hard_ones()
    {
        var f = new Fleet { Flight = new Flight { SpeedFraction = 0.4 } };
        var hard = new Fleet { Flight = new Flight { SpeedFraction = 1.0 } };
        Assert.True(Heat.BurnShare(f) < Heat.BurnShare(hard));
        Assert.Equal(1, Heat.BurnShare(hard), 9);
    }

    [Fact]
    public void New_hulls_wait_for_their_research()
    {
        var sim = NewGame();
        Hour(sim);
        Assert.NotNull(Economy.CanQueue(sim.World, Side.Player, OrderKind.Ship, "cruiser"));
        Player(sim).Researched.Add(Tech.CruiserHull);
        // A 260 m hull also needs a great yard: a starbase's slipway over a world you hold.
        Assert.Contains("great yard", Economy.CanQueue(sim.World, Side.Player, OrderKind.Ship, "cruiser"));
        var home = Economy.ShipyardOf(sim.World, Side.Player)!;
        var cls = GameData.ShipClass("station-starbase");
        sim.World.Starbases.Add(new Starbase { Id = sim.World.NextId++, Side = Side.Player, BodyId = home.Id, ClassId = cls.Id, Fit = Fitting.StandardFit(cls), Armour = cls.Armour });
        Assert.Null(Economy.CanQueue(sim.World, Side.Player, OrderKind.Ship, "cruiser"));
    }

    [Fact]
    public void A_saved_design_is_used_for_new_ships()
    {
        var sim = NewGame();
        Hour(sim);
        var cls = GameData.ShipClass("gun-frigate");
        var fit = Fitting.StandardFit(cls);
        fit[cls.SlotList.ToList().FindIndex(x => x.Type == "U")] = "fuel-tank";
        sim.Issue(new SetDesignCommand(Side.Player, cls.Id, fit));
        Hour(sim);
        var ship = Economy.NewShip(sim.World, Side.Player, cls.Id);
        Assert.Equal(cls.Supplies + GameData.Module("fuel-tank")!.Supplies, Fitting.Spec(ship).Supplies);
        Assert.Equal(Fitting.MaxSupplies(sim.World, ship), ship.Supplies);
        sim.World.Ships.Remove(ship);
    }
}
