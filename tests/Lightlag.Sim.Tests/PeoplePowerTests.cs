using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Plan 3 build 5, People and power: the planet grid (batteries, brownouts, shadow) and hungry people leaving.</summary>
public class PeoplePowerTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        Planets.EnsureStarted(sim.World);
        return sim;
    }

    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);
    private static void Hours(Simulation sim, double hours) => sim.StepMany((int)Math.Round(hours * GameTime.TicksPerDay / 24));

    private static Body Colony(Simulation sim, string key, double popK)
    {
        var b = BodyNamed(sim, key);
        b.Owner = Side.Player;
        b.PopK = popK;
        b.OwnShare = 1;
        b.Happiness = 60;
        return b;
    }

    private static Piece Put(Simulation sim, Body b, string kind)
    {
        var data = Planets.BuildingKind(kind)!;
        var (x, y, turned) = Colonies.BestSpot(b, data)!.Value;
        var p = Colonies.AddPiece(sim.World, b, data, x, y, turned, built: true);
        Colonies.SyncBuildings(b);
        return p;
    }

    private static List<(Piece, BuildingData)> Built(Body b) =>
        Colonies.Built(b).Select(p => (p, Planets.BuildingKind(p.Kind)!)).ToList();

    [Fact]
    public void Batteries_store_spare_power_and_carry_the_planet_when_it_runs_short()
    {
        var sim = NewGame();
        var w = sim.World;
        var kesh = Colony(sim, "kesh", 8);
        var battery = Put(sim, kesh, Grid.Battery);
        var guns = Put(sim, kesh, Planets.Guns);
        // Spare power fills the bank.
        for (var i = 0; i < 40; i++) Grid.Run(w, kesh, Built(kesh), 6);
        Assert.Equal(Grid.RoomOf(battery, Planets.BuildingKind(Grid.Battery)!), kesh.BatteryRoom, 6);
        Assert.True(kesh.BatteryCharge > 50);
        // The power goes (a fault, a fuel gap, a shadow): the bank runs the guns.
        var before = kesh.BatteryCharge;
        Grid.Run(w, kesh, Built(kesh), 0);
        Assert.True(guns.Powered);
        Assert.Equal(1, guns.PowerShare);
        Assert.Equal(guns.PowerShare * Planets.BuildingKind(Planets.Guns)!.Power, kesh.FromBattery, 6);
        Assert.Equal(before - kesh.FromBattery, kesh.BatteryCharge, 6);
        Assert.NotNull(Grid.HoursLeft(kesh));
        Assert.Contains("batteries", Grid.Summary(w, kesh));
        // Until it runs flat.
        for (var i = 0; i < 40; i++) Grid.Run(w, kesh, Built(kesh), 0);
        Assert.Equal(0, kesh.BatteryCharge, 6);
        Assert.False(guns.Powered);
        Assert.True(Grid.Shedding(w, kesh));
    }

    [Fact]
    public void Spare_power_charges_at_a_loss_and_never_past_the_room()
    {
        var sim = NewGame();
        var w = sim.World;
        var kesh = Colony(sim, "kesh", 8);
        Put(sim, kesh, Grid.Battery);
        Grid.Run(w, kesh, Built(kesh), 10);
        Assert.Equal(10 * Grid.D.ChargeEfficiency, kesh.BatteryCharge, 6);
        for (var i = 0; i < 100; i++) Grid.Run(w, kesh, Built(kesh), 10);
        Assert.Equal(kesh.BatteryRoom, kesh.BatteryCharge, 6);
    }

    [Fact]
    public void A_moon_behind_its_planet_is_in_shadow_and_its_solar_fields_go_dark()
    {
        var sim = NewGame();
        var w = sim.World;
        var brim = Colony(sim, "brim", 4);
        var tamber = BodyNamed(sim, "tamber");
        var sun = Sails.Star(w)!.Position;
        var away = (tamber.Position - sun) * (1 / Vec2.Distance(tamber.Position, sun));
        brim.Position = tamber.Position + away * (tamber.Radius + 50);
        Assert.True(Grid.Shaded(w, brim));
        // The planet itself is never shaded by its smaller moon.
        Assert.False(Grid.Shaded(w, tamber));
        brim.Position = tamber.Position - away * (tamber.Radius + 50);
        Assert.False(Grid.Shaded(w, brim));
    }

    [Fact]
    public void Solar_fields_in_shadow_make_nothing_and_the_summary_says_so()
    {
        var sim = NewGame();
        var w = sim.World;
        var brim = Colony(sim, "brim", 4);
        Put(sim, brim, "solar-field");
        Hours(sim, 1);
        var lit = brim.PowerMade;
        Assert.True(lit > Colonies.C.BasePower);
        // Hold the moon behind its planet for the next hour's work (positions move each tick, so test the field directly).
        var tamber = BodyNamed(sim, "tamber");
        var sun = Sails.Star(w)!.Position;
        var away = (tamber.Position - sun) * (1 / Vec2.Distance(tamber.Position, sun));
        brim.Position = tamber.Position + away * (tamber.Radius + 50);
        Assert.True(Grid.Shaded(w, brim));
        Grid.Run(w, brim, Built(brim), Colonies.C.BasePower);
        Assert.True(brim.InShadow);
        Assert.Equal(w.Tick, brim.LastShadowHour);
        Assert.Contains("IN SHADOW", Grid.Summary(w, brim));
    }

    [Fact]
    public void Short_of_power_low_buildings_are_shed_first_and_the_next_in_line_gets_a_brownout()
    {
        var sim = NewGame();
        var w = sim.World;
        var kesh = Colony(sim, "kesh", 20);
        var guns = Put(sim, kesh, Planets.Guns);
        var mine = Put(sim, kesh, Planets.Mine);
        mine.PowerUse = PowerUse.High;
        guns.PowerUse = PowerUse.Low;
        var gunDraw = Planets.BuildingKind(Planets.Guns)!.Power;
        var mineDraw = Planets.BuildingKind(Planets.Mine)!.Power;
        // Enough for the mine and half the guns.
        Grid.Run(w, kesh, Built(kesh), mineDraw + gunDraw * 0.5);
        Assert.Equal(1, mine.PowerShare);
        Assert.True(guns.Powered);
        Assert.Equal(0.5, guns.PowerShare, 6);
        Assert.Contains("brownout", Grid.Summary(w, kesh));
        // Too little for a brownout: the guns are shed.
        Grid.Run(w, kesh, Built(kesh), mineDraw + gunDraw * 0.1);
        Assert.False(guns.Powered);
        Assert.Contains("SHED", Grid.Summary(w, kesh));
        Assert.Contains(Planets.BuildingKind(Planets.Guns)!.Name, Grid.ShedList(kesh).Shed);
    }

    [Fact]
    public void The_steward_builds_a_battery_where_solar_fields_go_dark()
    {
        var sim = NewGame();
        var w = sim.World;
        var brim = Colony(sim, "brim", 10);
        Put(sim, brim, "solar-field");
        brim.LastShadowHour = w.Tick;
        brim.PowerMade = 20;
        brim.PowerWanted = 0;
        var want = Planner.PlanetWant(w, Side.Player, brim, hungry: false, sparesShort: false);
        Assert.Equal(Grid.Battery, want?.Kind);
        Put(sim, brim, Grid.Battery);
        Assert.NotEqual(Grid.Battery, Planner.PlanetWant(w, Side.Player, brim, hungry: false, sparesShort: false)?.Kind);
    }

    /// <summary>A hungry colony a few days in, and the player's home (fed) to flee to.</summary>
    private static (Body Hungry, Body Refuge) Famine(Simulation sim)
    {
        var w = sim.World;
        var home = w.Bodies.Single(b => b.Owner == Side.Player && b.Home);
        var kesh = Colony(sim, "kesh", 6);
        kesh.Hungry = true;
        kesh.HungryHours = Planets.D.LeaveStartHungryDays * 24 + 1;
        return (kesh, home);
    }

    [Fact]
    public void Hungry_people_trickle_away_to_a_fed_world_after_a_few_days()
    {
        var sim = NewGame();
        var w = sim.World;
        var (kesh, _) = Famine(sim);
        var home = Planets.RefugeWorlds(w, kesh, Side.Player).First();
        Assert.False(home.Hungry);
        var before = home.PopK;
        var moved = Planets.Migrate(w, kesh, Side.Player, 1);
        Assert.Equal(1, moved, 6);
        Assert.Equal(5, kesh.PopK, 6);
        Assert.Equal(before + 1, home.PopK, 6);
        Assert.Equal(1, home.NewcomersK, 6);
        Assert.Equal(home.Name, kesh.LeavingTo);
        // Newcomers pull happiness down a little where they land.
        Assert.Contains(Planets.TargetHappiness(w, home).Parts, p => p.Why == "Hungry newcomers" && p.Amount < 0);
    }

    [Fact]
    public void Planet_screen_note_counts_down_to_people_leaving()
    {
        var sim = NewGame();
        var w = sim.World;
        var kesh = Colony(sim, "kesh", 6);
        kesh.Hungry = true;
        kesh.HungryHours = 48;
        Assert.Contains("start leaving in 3", Planets.LeavingNote(w, kesh));
        kesh.Hungry = false;
        Assert.Null(Planets.LeavingNote(w, kesh));
    }

    [Fact]
    public void Long_hunger_empties_the_world_and_it_makes_nothing_until_settlers_land()
    {
        var sim = NewGame();
        var w = sim.World;
        var kesh = Colony(sim, "kesh", 6);
        var mine = Put(sim, kesh, Planets.Mine);
        // No food anywhere: no farms and empty stores keep the whole side hungry.
        foreach (var b in w.Bodies.Where(b => b.Owner == Side.Player))
        {
            b.FoodStock = 0;
            b.Pieces.RemoveAll(p => Planets.BuildingKind(p.Kind) is { Food: > 0 });
            Colonies.SyncBuildings(b);
        }
        kesh.HungryHours = Planets.D.LeaveAfterHungryDays * 24 - 1;
        kesh.Hungry = true;
        var homeBefore = w.Bodies.Where(b => b.Owner == Side.Player && b.Id != kesh.Id).Sum(b => b.PopK);
        Hours(sim, 2);
        Assert.True(kesh.Abandoned);
        Assert.Equal(Side.Player, kesh.Owner);
        Assert.Equal(0, Planets.IndustryPerDay(w, kesh));
        Assert.Equal(0, mine.Staff);
        Assert.Contains("ABANDONED", Planets.LeavingNote(w, kesh));
        Assert.True(w.Bodies.Where(b => b.Owner == Side.Player && b.Id != kesh.Id).Sum(b => b.PopK) > homeBefore);
    }

    [Fact]
    public void An_abandoned_world_comes_back_when_people_live_there_again()
    {
        var sim = NewGame();
        var w = sim.World;
        var kesh = Colony(sim, "kesh", 0);
        kesh.Abandoned = true;
        Hours(sim, 1);
        Assert.Equal(0, Planets.IndustryPerDay(w, kesh));
        kesh.PopK = 2;
        Hours(sim, 1);
        Assert.False(kesh.Abandoned);
        Assert.True(Planets.IndustryPerDay(w, kesh) > 0);
    }

    [Fact]
    public void Blockaded_hungry_people_cannot_leave()
    {
        var sim = NewGame();
        var w = sim.World;
        var kesh = Colony(sim, "kesh", 6);
        kesh.Hungry = true;
        kesh.HungryHours = Planets.D.LeaveStartHungryDays * 24 + 10;
        var rival = w.Fleets.First(f => f.Side == Side.Rival);
        rival.Position = kesh.Position;
        rival.Hold = HoldStyle.Orbit;
        rival.OrbitBodyId = kesh.Id;
        rival.Flight = null;
        Assert.True(Economy.Blockaded(w, kesh));
        Assert.Empty(Planets.RefugeWorlds(w, BodyNamed(sim, "orrin"), Side.Player).Where(b => b.Id == kesh.Id));
        Assert.Contains("nobody can leave", Planets.LeavingNote(w, kesh));
    }
}
