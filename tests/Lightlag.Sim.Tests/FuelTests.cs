using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class FuelTests
{
    private static Simulation NewGame(ulong seed = 8, bool fuelTypes = true)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: new GameOptions(FuelTypes: fuelTypes));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static SideState Player(Simulation sim) => Economy.Of(sim.World, Side.Player);
    private static Fleet Home(Simulation sim) => sim.World.Fleets.First(f => f.Side == Side.Player);
    private static Body Yard(Simulation sim) => Economy.ShipyardOf(sim.World, Side.Player)!;
    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    private static void PlaceInOrbit(Simulation sim, Fleet f, Body b)
    {
        f.Flight = null;
        f.Position = b.Position + new Vec2(Fleets.OrbitRadiusFor(b), 0);
        Fleets.EnterOrbit(f, b);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
    }

    private static void SetEngine(Simulation sim, Ship s, string engine)
    {
        var fit = Fitting.FitOf(s).ToList();
        fit[GameData.ShipClass(s.ClassId).SlotList.ToList().FindIndex(x => x.Type == "E")] = engine;
        Fitting.Apply(sim.World, s, fit);
    }

    [Fact]
    public void Every_engine_burns_known_fuels_and_the_standard_fuel_is_listed()
    {
        Assert.Contains(Fuels.All, f => f.Id == Fuels.Standard);
        Assert.Equal(6, Fuels.All.Count);
        foreach (var m in GameData.Modules.Values.Where(m => m.Slot == "E"))
        {
            Assert.NotEmpty(Fuels.Burns(m));
            Assert.All(Fuels.Burns(m), f => Assert.Contains(Fuels.All, x => x.Id == f));
        }
        // Every fuel but the standard one and water is made by some building or station.
        foreach (var f in Fuels.All.Where(f => f.Id != Fuels.Standard && !f.Anywhere))
            Assert.True(GameData.Economy.Planets!.Buildings!.Any(b => b.Makes?.ContainsKey(f.Id) == true)
                        || GameData.Economy.Stations!.Any(s => s.Makes?.ContainsKey(f.Id) == true), f.Id);
    }

    [Fact]
    public void A_fuel_changes_thrust_reach_and_burn_heat()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = Home(sim);
        var ship = w.ShipsOf(f).First(s => s.ClassId == "gun-frigate");
        var accel = FleetPerformance.Of(w, new Fleet { ShipIds = { ship.Id } }).Accel;
        var perDv = Tech.SuppliesPerDeltaV(w, ship);
        Assert.Equal(1, Fuels.Thrust(w, ship));
        SetEngine(sim, ship, "engine-mk2");
        ship.Fuel = "fission-pellets";
        Assert.True(Tech.SuppliesPerDeltaV(w, ship) < perDv, "fission goes further per tank");
        Assert.True(Fuels.BurnHeat(w, ship) > 1, "and runs hotter");
        ship.Fuel = "water";
        Assert.True(Fuels.Thrust(w, ship) < 1, "water pushes weakly");
        Assert.True(Fuels.Data("water").Signature < 1, "and shows dimly");
        Assert.True(accel > 0);
    }

    [Fact]
    public void Refuelling_takes_the_fuel_out_of_the_planet_store()
    {
        var sim = NewGame();
        var f = Home(sim);
        var yard = Yard(sim);
        PlaceInOrbit(sim, f, yard);
        var ship = sim.World.ShipsOf(f).First();
        ship.Supplies = 0;
        var before = Colonies.Held(yard, Fuels.Standard);
        Days(sim, 1);
        Assert.True(ship.Supplies > 0);
        Assert.True(Colonies.Held(yard, Fuels.Standard) < before);
        Assert.True(Economy.Of(sim.World, Side.Player).Totals.GoodsOut[Fuels.Standard]["ship fuel"] > 0);
    }

    [Fact]
    public void Auto_fills_up_with_a_better_fuel_the_engine_takes_and_hands_back_the_old_one()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = Home(sim);
        var yard = Yard(sim);
        PlaceInOrbit(sim, f, yard);
        var ship = w.ShipsOf(f).First(s => s.ClassId == "gun-frigate");
        SetEngine(sim, ship, "engine-mk2");
        ship.Supplies = Fitting.MaxSupplies(w, ship) * 0.2;
        yard.Stock["fission-pellets"] = 50;
        var fuel = Colonies.Held(yard, Fuels.Standard);
        var share = Fuels.Refuel(w, ship, f, yard, 5);
        Assert.Equal("fission-pellets", ship.Fuel);
        Assert.Equal(1, share);
        Assert.True(Colonies.Held(yard, Fuels.Standard) > fuel, "the hydrogen left in the tanks went back to the store");
        Assert.True(Colonies.Held(yard, "fission-pellets") < 50);
    }

    [Fact]
    public void A_ship_with_a_quarter_tank_or_more_keeps_its_fuel()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = Home(sim);
        var yard = Yard(sim);
        var ship = w.ShipsOf(f).First(s => s.ClassId == "gun-frigate");
        SetEngine(sim, ship, "engine-mk2");
        ship.Supplies = Fitting.MaxSupplies(w, ship) * 0.5;
        yard.Stock["fission-pellets"] = 50;
        Assert.Equal(Fuels.Standard, Fuels.Pick(w, ship, f, yard, 5));
        yard.Stock.Remove(Fuels.Standard);
        Assert.Equal(Fuels.Standard, Fuels.Pick(w, ship, f, yard, 5)); // none of its own fuel here, but no pouring back and forth
    }

    [Fact]
    public void A_fleet_told_a_fuel_uses_it_and_quiet_jobs_prefer_a_dim_burn()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = Home(sim);
        var yard = Yard(sim);
        var ship = w.ShipsOf(f).First(s => s.ClassId == "gun-frigate");
        ship.Supplies = 0;
        yard.Stock["water"] = 100;
        Assert.Equal(Fuels.Standard, Fuels.Pick(w, ship, f, yard, 10));
        sim.Issue(new SetFleetFuelCommand(Side.Player, f.Id, "water"));
        sim.Step();
        Assert.Equal("water", f.Fuel);
        Assert.Equal("water", Fuels.Pick(w, ship, f, yard, 10));
        f.Fuel = "";
        f.Radiators = RadiatorMode.Cold;
        Assert.Equal("water", Fuels.Pick(w, ship, f, yard, 10));
    }

    [Fact]
    public void Without_stock_only_water_comes_at_the_slow_pace_and_other_fuels_trickle()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = Home(sim);
        var yard = Yard(sim);
        var ship = w.ShipsOf(f).First(s => s.ClassId == "gun-frigate");
        yard.Stock.Clear();
        yard.Stock["spares"] = 1; // a store, but no fuel of any kind
        ship.Supplies = 0;
        var share = Fuels.Refuel(w, ship, f, yard, 10);
        Assert.Equal("water", ship.Fuel);
        Assert.Equal(Colonies.C.NoFuelRefill, share, 6);
        SetEngine(sim, ship, "engine-mk3");
        Assert.Equal("", ship.Fuel); // Mk III can't burn water: back to its usual fuel
        Assert.Equal(Colonies.C.NoFuelRefill * 0.25, Fuels.Refuel(w, ship, f, yard, 10), 6);
    }

    [Fact]
    public void Tankers_load_each_fuel_from_the_store_and_hand_out_only_the_matching_one()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = Home(sim);
        var yard = Yard(sim);
        PlaceInOrbit(sim, f, yard);
        var frigate = w.ShipsOf(f).First(s => s.ClassId == "gun-frigate");
        SetEngine(sim, frigate, "engine-mk2");
        frigate.Fuel = "fission-pellets";
        var tanker = Economy.NewShip(w, Side.Player, "tanker");
        f.ShipIds.Add(tanker.Id);
        tanker.FleetId = f.Id;
        tanker.CargoFuel = 0;
        tanker.CargoFuels.Clear();
        yard.Stock["fission-pellets"] = 100;
        var standard = Colonies.Held(yard, Fuels.Standard);
        Fuels.Load(w, tanker, 40, yard);
        Assert.True(Fuels.CargoOf(tanker, "fission-pellets") > 0);
        Assert.True(Fuels.CargoOf(tanker, Fuels.Standard) > 0);
        Assert.True(Colonies.Held(yard, "fission-pellets") < 100 && Colonies.Held(yard, Fuels.Standard) < standard);
        Assert.Equal(tanker.CargoFuels.Values.Sum(), tanker.CargoFuel, 6);
        Assert.Null(Fuels.Problem(w, tanker));
        var pellets = Fuels.CargoOf(tanker, "fission-pellets");
        Assert.Equal(pellets, Fuels.TakeCargo(tanker, "fission-pellets", 1000), 6);
        Assert.Equal(0, Fuels.TakeCargo(tanker, "deuterium", 5));
    }

    [Fact]
    public void Only_ships_on_the_same_fuel_share_it()
    {
        var sim = NewGame();
        var w = sim.World;
        var ships = new[] { Economy.NewShip(w, Side.Player, "gun-frigate"), Economy.NewShip(w, Side.Player, "gun-frigate") }.ToList();
        var f = Fleets.Create(w, Side.Player, ships, w.Bodies.Single(b => b.Key == "verdant").Position * 1.3);
        foreach (var s in ships) s.Position = f.Position + s.FormationOffset;
        SetEngine(sim, ships[0], "engine-mk2");
        ships[0].Fuel = "water";
        ships[0].Supplies = 0;
        ships[1].Supplies = Fitting.MaxSupplies(w, ships[1]);
        f.Flight = null;
        sim.Step();
        Assert.True(ships[0].Supplies < 1e-6, $"the water ship got {ships[0].Supplies:0.0} from a ship on {Fuels.IdOf(ships[1])}");
        ships[0].Fuel = "";
        sim.Step();
        Assert.True(ships[0].Supplies > 0);
    }

    [Fact]
    public void Better_engines_wait_until_the_side_can_get_their_fuel()
    {
        var sim = NewGame();
        sim.Step();
        var w = sim.World;
        Player(sim).Researched.AddRange(new[] { Tech.EngineMk2, "engine-mk3", "deuterium-extraction", "engine-mk4" });
        Assert.Equal("engine-mk3", Fitting.BestEngine(w, Side.Player));
        Assert.Contains("deuterium", Planner.FuelsWanted(w, Side.Player));
        Yard(sim).Stock["deuterium"] = 40;
        Assert.Equal("engine-mk4", Fitting.BestEngine(w, Side.Player));
        var ship = Economy.NewShip(w, Side.Player, "gun-frigate");
        Assert.Equal("deuterium", ship.Fuel);
    }

    [Fact]
    public void With_fuel_types_off_every_engine_burns_the_one_fuel()
    {
        var sim = NewGame(fuelTypes: false);
        var w = sim.World;
        Assert.False(w.Rules.FuelTypes);
        var ship = w.ShipsOf(Home(sim)).First(s => s.ClassId == "gun-frigate");
        SetEngine(sim, ship, "engine-mk4");
        Assert.Equal(new[] { Fuels.Standard }, Fuels.Burns(w, ship));
        Assert.Equal(1, Fuels.Reach(w, ship));
        Player(sim).Researched.AddRange(new[] { Tech.EngineMk2, "engine-mk3", "deuterium-extraction", "engine-mk4" });
        Assert.Equal("engine-mk4", Fitting.BestEngine(w, Side.Player));
        Assert.Empty(Planner.FuelsWanted(w, Side.Player));
    }

    [Fact]
    public void Fuel_drawn_at_a_planet_counts_as_its_use_and_fades()
    {
        var sim = NewGame();
        var yard = Yard(sim);
        Fuels.NoteDraw(yard, Fuels.Standard, 10);
        Assert.True(Colonies.UsePerDay(yard, Fuels.Standard) > 0);
        var before = yard.FuelUse[Fuels.Standard];
        Fuels.FadeDraws(sim.World);
        Assert.True(yard.FuelUse[Fuels.Standard] < before);
    }

    [Fact]
    public void A_short_game_with_fuel_types_keeps_every_rule_and_replays()
    {
        var sim = NewGame(seed: 21);
        Days(sim, 30);
        Assert.Equal(0, sim.RuleViolations);
        Assert.All(sim.World.Ships, s => Assert.Null(Fuels.Problem(sim.World, s)));
    }
}
