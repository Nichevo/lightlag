using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class StartAndMunitionsTests
{
    private static Simulation NewGame(ulong seed = 8, GameOptions? options = null)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: options);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        Planets.EnsureStarted(sim.World);
        return sim;
    }

    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);
    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    private static Piece Plant(Simulation sim, Body b)
    {
        var data = Planets.BuildingKind(Munitions.Plant)!;
        var (x, y, turned) = Colonies.BestSpot(b, data)!.Value;
        var p = Colonies.AddPiece(sim.World, b, data, x, y, turned, built: true);
        Colonies.SyncBuildings(b);
        return p;
    }

    [Fact]
    public void Every_start_has_a_moon_outpost_and_a_home_transport_that_hauls_on_its_own()
    {
        foreach (var random in new[] { false, true })
            for (ulong seed = 1; seed <= 4; seed++)
            {
                var sim = NewGame(seed, new GameOptions(RandomStarts: random));
                var w = sim.World;
                foreach (var side in new[] { Side.Player, Side.Rival })
                {
                    var home = w.Bodies.Single(b => b.Home && b.Owner == side);
                    var moon = w.Bodies.Single(b => b.ParentId == home.Id && b.Owner == side);
                    Assert.Equal(Planets.D.OutpostStartK, moon.PopK);
                    Assert.Contains(Planets.Mine, moon.Buildings);
                    // A supply tender sails with the war fleet; another waits at home, free to haul.
                    Assert.Contains(w.Fleets, f => f.Side == side && w.ShipsOf(f).Any(s => s.ClassId == "supply-tender") && w.ShipsOf(f).Any(Planner.Warship));
                    Assert.Contains(Colonies.FreeHaulers(w, side), f => w.ShipsOf(f).All(s => s.ClassId == "supply-tender"));
                }
            }
    }

    [Fact]
    public void The_supply_tender_needs_no_research_and_carries_fuel_ammunition_and_food()
    {
        var sim = NewGame();
        Assert.Null(Economy.CanQueue(sim.World, Side.Player, OrderKind.Ship, "supply-tender"));
        var tender = sim.World.Ships.First(s => s.Side == Side.Player && s.ClassId == "supply-tender");
        Assert.True(SupplyLines.IsSupplyShip(tender));
        Assert.True(tender.CargoFuel > 0 && tender.CargoAmmo > 0 && tender.CargoFood > 0);
        Assert.True(Fitting.Spec(tender).MaxSpeedMmPerHour >= GameData.ShipClass("gun-frigate").MaxSpeedMmPerHour);
    }

    [Fact]
    public void Research_alone_does_not_upgrade_missiles_until_a_plant_is_set_to_best_and_refitted()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = w.Bodies.Single(b => b.Home && b.Owner == Side.Player);
        var plant = Plant(sim, home);
        plant.PowerUse = PowerUse.High; // the home's power is nearly all spoken for at the start
        Assert.Equal(PlantMode.Basic, plant.PlantMode);
        Economy.Of(w, Side.Player).Researched.Add(Tech.MissileBrain2);
        w.MissileBrain[(int)Side.Player] = 2;
        Munitions.OnResearched(sim, Side.Player);
        sim.Step();
        // Researched, but the plant still makes basic missiles: new ships and reloads get brain 1.
        Assert.Equal((1, 1), Munitions.Made(w, Side.Player));
        var ship = Economy.NewShip(w, Side.Player, "missile-destroyer");
        w.Ships.Remove(ship); // only looked at, not put in a fleet
        Assert.Equal(1, Munitions.Brain(w, ship));

        // Set to Best: it refits, then needs more staff and power, and makes brain 2 missiles.
        sim.Issue(new SetPlantModeCommand(Side.Player, home.Id, plant.Id, PlantMode.Best));
        sim.ApplyPending();
        Assert.True(plant.RefitHoursLeft > 0);
        Days(sim, Colonies.C.RefitDays + 1);
        Assert.Equal(2, plant.MissileGrade);
        Assert.True(Munitions.DrawFactor(plant) > 1);
        Assert.True(Munitions.Made(w, Side.Player).Missile == 2, $"powered {plant.Powered} staff {plant.Staff} power {home.PowerUsed}/{home.PowerMade} wanted {home.PowerWanted}");
        // Ships already out keep their basic missiles until they reload at a yard.
        Assert.Equal(1, ship.MissileGrade);
        var fresh = Economy.NewShip(w, Side.Player, "missile-destroyer");
        w.Ships.Remove(fresh);
        Assert.Equal(2, Munitions.Brain(w, fresh));
    }

    [Fact]
    public void A_basic_plant_makes_more_plain_ammunition_than_one_on_best()
    {
        var sim = NewGame();
        var home = sim.World.Bodies.Single(b => b.Home && b.Owner == Side.Player);
        var plant = Plant(sim, home);
        Assert.Equal(Munitions.BasicBoost, Munitions.MakeFactor(plant));
        plant.PlantMode = PlantMode.Best;
        Assert.Equal(1, Munitions.MakeFactor(plant));
    }

    [Fact]
    public void Railgun_mk3_damage_needs_mk3_slugs_loaded()
    {
        var sim = NewGame();
        var w = sim.World;
        Economy.Of(w, Side.Player).Researched.Add(Tech.RailgunMk3);
        var ship = w.Ships.First(s => s.Side == Side.Player && s.ClassId == "gun-frigate");
        ship.RailGrade = 1;
        var plain = Tech.RailDamage(w, ship);
        ship.RailGrade = 2;
        Assert.True(Tech.RailDamage(w, ship) > plain);
    }

    [Fact]
    public void Automation_upgrades_a_plant_when_the_enemy_is_seen_with_better_point_defence()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = w.Bodies.Single(b => b.Home && b.Owner == Side.Player);
        var plant = Plant(sim, home);
        Economy.Of(w, Side.Player).Researched.Add(Tech.MissileBrain2);
        w.MissileBrain[(int)Side.Player] = 2;
        Assert.False(Munitions.Pressed(w, Side.Player));
        Intel.Of(w, Side.Player).Tech.Add(new TechFact { Part = "point-defence", Level = 2, Hour = w.Hour });
        Assert.True(Munitions.Pressed(w, Side.Player));
        Days(sim, 1);
        Assert.Equal(PlantMode.Best, plant.PlantMode);
    }
}
