using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class SupplyLineTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Starts.Classic(Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory()));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet Home(Simulation sim) => sim.World.Fleets.First(f => f.Side == Side.Player);
    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);
    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    private static void PlaceInOrbit(Simulation sim, Fleet f, Body b)
    {
        f.Flight = null;
        f.Position = b.Position + new Vec2(Fleets.OrbitRadiusFor(b), 0);
        Fleets.EnterOrbit(f, b);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
    }

    [Fact]
    public void New_ships_start_with_full_stocks()
    {
        var sim = NewGame();
        foreach (var s in sim.World.Ships)
        {
            Assert.Equal(SupplyLines.MaxAmmo(s), s.Ammo);
            Assert.Equal(SupplyLines.MaxFood(s), s.Food);
        }
    }

    [Fact]
    public void Crews_eat_away_from_home_and_go_hungry_when_the_food_runs_out()
    {
        var sim = NewGame();
        var f = Home(sim);
        PlaceInOrbit(sim, f, BodyNamed(sim, "pale"));
        var ship = sim.World.ShipsOf(f).First();
        ship.Food = 1.5;
        Days(sim, 1);
        Assert.InRange(ship.Food, 0.4, 0.6);
        Assert.False(SupplyLines.Hungry(ship));
        Days(sim, 1);
        Assert.True(SupplyLines.Hungry(ship));
    }

    [Fact]
    public void A_held_planet_restocks_food_and_ammunition()
    {
        var sim = NewGame();
        var ship = sim.World.ShipsOf(Home(sim)).First(s => SupplyLines.MaxAmmo(s) > 0);
        ship.Ammo = 0;
        ship.Food = 0;
        Days(sim, 4);
        Assert.True(ship.Ammo > SupplyLines.MaxAmmo(ship) * 0.5);
        Assert.True(ship.Food > SupplyLines.MaxFood(ship) * 0.5);
    }

    [Fact]
    public void Railgun_shots_use_ammunition_not_fuel()
    {
        var sim = NewGame();
        var ship = sim.World.ShipsOf(Home(sim)).First(s => SupplyLines.MaxAmmo(s) > 0);
        Assert.True(SupplyLines.MaxAmmo(ship) > 0);
        Assert.Equal(Fitting.Spec(ship).Railguns * SupplyLines.AmmoPerRailgun, SupplyLines.MaxAmmo(ship));
    }

    [Fact]
    public void A_tanker_in_a_fleet_tops_up_the_others()
    {
        var sim = NewGame();
        var f = Home(sim);
        PlaceInOrbit(sim, f, BodyNamed(sim, "pale"));
        var tanker = Economy.NewShip(sim.World, Side.Player, "tanker");
        f.ShipIds.Add(tanker.Id);
        tanker.FleetId = f.Id;
        Fleets.Arrange(sim.World, f);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
        var thirsty = sim.World.ShipsOf(f).First(s => !SupplyLines.IsSupplyShip(s));
        thirsty.Supplies = 1;
        var cargo = tanker.CargoFuel;
        Days(sim, 1);
        Assert.True(tanker.CargoFuel < cargo);
        Assert.True(thirsty.Supplies > 1);
    }

    [Fact]
    public void A_tanker_in_a_fleet_reloads_spent_missiles_from_its_ammunition()
    {
        var sim = NewGame();
        var f = Home(sim);
        PlaceInOrbit(sim, f, BodyNamed(sim, "pale"));
        var launcher = sim.World.ShipsOf(f).FirstOrDefault(s => Tech.Racks(sim.World, s) > 0);
        if (launcher == null)
        {
            launcher = Economy.NewShip(sim.World, Side.Player, "destroyer");
            f.ShipIds.Add(launcher.Id);
            launcher.FleetId = f.Id;
        }
        var tanker = Economy.NewShip(sim.World, Side.Player, "tanker");
        f.ShipIds.Add(tanker.Id);
        tanker.FleetId = f.Id;
        Fleets.Arrange(sim.World, f);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
        launcher.Missiles = 0;
        var cargo = tanker.CargoAmmo;
        Assert.True(cargo >= 2);
        Days(sim, 1);
        Assert.True(launcher.Missiles > 0);
        Assert.True(launcher.Missiles <= Tech.Racks(sim.World, launcher));
        Assert.True(tanker.CargoAmmo < cargo);
    }

    [Fact]
    public void A_supply_run_flies_out_and_delivers()
    {
        var sim = NewGame();
        var f = Home(sim);
        var pale = BodyNamed(sim, "pale");
        PlaceInOrbit(sim, f, pale);
        foreach (var s in sim.World.ShipsOf(f)) { s.Food = 2; s.Ammo = 0; }
        var yard = Economy.ShipyardOf(sim.World, Side.Player)!;
        var tanker = Economy.NewShip(sim.World, Side.Player, "tanker");
        var freighter = Economy.NewShip(sim.World, Side.Player, "freighter");
        var run = Fleets.Create(sim.World, Side.Player, new[] { tanker, freighter }, yard.Position);
        PlaceInOrbit(sim, run, yard);
        sim.Issue(new SetSupplyRunCommand(Side.Player, run.Id, f.Id));
        Days(sim, 40);
        Assert.True(sim.Log.Tail().Any(l => l.Contains("sets out with supplies")), string.Join("\n", sim.Log.Tail().Where(l => l.Contains("upply") || l.Contains("Fleet")).TakeLast(30)));
        var ships = sim.World.ShipsOf(f).Where(s => !SupplyLines.IsSupplyShip(s)).ToList();
        Assert.Contains(ships, s => s.Ammo > 0);
        Assert.All(ships, s => Assert.True(s.Food > 0, $"{s.Name} still hungry"));
    }

    private static Fleet SupplyRunAtHome(Simulation sim, Fleet target, string cls = "supply-tender")
    {
        var yard = Economy.ShipyardOf(sim.World, Side.Player)!;
        var run = Fleets.Create(sim.World, Side.Player, new[] { Economy.NewShip(sim.World, Side.Player, cls) }, yard.Position);
        PlaceInOrbit(sim, run, yard);
        sim.Issue(new SetSupplyRunCommand(Side.Player, run.Id, target.Id));
        return run;
    }

    private static void Coasting(Simulation sim, Fleet f, Vec2 at, Vec2 velocity)
    {
        f.Flight = null;
        Fleets.EnterDrift(f);
        f.Position = at;
        f.Velocity = velocity;
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
    }

    [Fact]
    public void A_supply_run_stops_alongside_a_coasting_fleet_and_keeps_its_own_fleet()
    {
        var sim = NewGame();
        var f = Home(sim);
        Coasting(sim, f, BodyNamed(sim, "pale").Position + new Vec2(20000, 0), new Vec2(0, 30));
        foreach (var s in sim.World.ShipsOf(f)) s.Food = 2;
        var run = SupplyRunAtHome(sim, f);
        var met = false;
        for (var d = 0; d < 30 && !met; d++)
        {
            Days(sim, 1);
            met = sim.Log.Tail().Any(l => l.Contains("comes alongside"));
        }
        Assert.True(met, string.Join("\n", sim.Log.Tail().Where(l => l.Contains("upply") || l.Contains(run.Name)).TakeLast(20)));
        Assert.NotNull(sim.World.FindFleet(run.Id));                       // not merged into the fleet it supplies
        Assert.DoesNotContain(sim.World.ShipsOf(f), SupplyLines.IsSupplyShip);
        Days(sim, 0.5);
        Assert.All(sim.World.ShipsOf(f).Where(s => !SupplyLines.IsSupplyShip(s)), s => Assert.True(s.Food > 2, $"{s.Name} got no food"));
    }

    [Fact]
    public void A_fast_supply_run_passing_through_the_handover_distance_counts_as_close()
    {
        var target = Home(NewGame());
        var run = new Fleet { Position = target.Position + new Vec2(200, 10), Velocity = target.Velocity + new Vec2(300, 0) };
        Assert.True(SupplyLines.PassedClose(run, target));                  // was 100 Mm on the other side an hour ago
        run.Position = target.Position + new Vec2(200, 80);
        Assert.False(SupplyLines.PassedClose(run, target));
    }

    [Fact]
    public void A_supply_tender_refuels_its_own_tanks_from_its_fuel_cargo()
    {
        var sim = NewGame();
        var w = sim.World;
        var tender = Economy.NewShip(w, Side.Player, "supply-tender");
        var run = Fleets.Create(w, Side.Player, new[] { tender }, new Vec2(0, 0));
        Coasting(sim, run, BodyNamed(sim, "pale").Position + new Vec2(30000, 0), Vec2.Zero);
        tender.Supplies = Fitting.MaxSupplies(w, tender) * 0.1;
        var cargo = tender.CargoFuel;
        Days(sim, 1.0 / 24 * 2);
        // Tops up from its hold (here the whole fuel cargo is less than the room in its tanks).
        Assert.True(tender.Supplies > Fitting.MaxSupplies(w, tender) * 0.1 + cargo * 0.9, $"tanks at {tender.Supplies / Fitting.MaxSupplies(w, tender):P0}");
        Assert.True(tender.CargoFuel < cargo);
    }

    [Fact]
    public void A_supply_run_low_on_its_own_fuel_goes_home_to_refuel()
    {
        var sim = NewGame();
        var f = Home(sim);
        PlaceInOrbit(sim, f, BodyNamed(sim, "pale"));
        var run = SupplyRunAtHome(sim, f, "freighter");                    // carries food only, no fuel for itself
        Coasting(sim, run, BodyNamed(sim, "pale").Position + new Vec2(30000, 0), Vec2.Zero);
        foreach (var s in sim.World.ShipsOf(run)) s.Supplies = Fitting.MaxSupplies(sim.World, s) * 0.2;
        Days(sim, 1.0 / 24 * 2);
        Assert.Contains(sim.Log.Tail(), l => l.Contains("Supply run goes home to refuel"));
        Assert.NotNull(run.Flight);
        Assert.Contains("refuel", SupplyLines.RunStatus(sim.World, f) ?? "");
    }

    [Fact]
    public void Ships_cost_upkeep()
    {
        var sim = NewGame();
        Assert.True(SupplyLines.UpkeepPerDay(sim.World, Side.Player) > 0);
    }
}
