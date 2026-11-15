using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>
/// Stores and holding zones: equal food and range, tenders that feed and restock themselves, supply runs that
/// keep away from danger, and fleets that drift in their zones with a few short burns.
/// </summary>
public class SupplyAndZoneTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);

    private static Fleet NewFleet(Simulation sim, Side side, Vec2 at, params string[] classes)
    {
        var ships = classes.Select(c => Economy.NewShip(sim.World, side, c)).ToList();
        var f = Fleets.Create(sim.World, side, ships, at);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
        return f;
    }

    private static Vec2 OpenSpace(Simulation sim) => BodyNamed(sim, "verdant").Position * 1.3;

    [Fact]
    public void Every_crewed_warship_carries_the_same_days_of_food_and_the_same_range()
    {
        var warships = new[] { "escort", "gun-frigate", "missile-destroyer", "cruiser", "picket", "siege-monitor", "capital" };
        foreach (var id in warships)
        {
            var c = GameData.ShipClass(id);
            Assert.Equal(120, c.FoodDays);
            Assert.InRange(c.Supplies / c.SuppliesPerDeltaV, 7900, 8100);
        }
    }

    [Fact]
    public void A_supply_tender_feeds_its_own_crew_from_its_hold()
    {
        var sim = NewGame();
        var f = NewFleet(sim, Side.Player, OpenSpace(sim), "gun-frigate", "supply-tender");
        f.TenderStays = true;
        var tender = sim.World.ShipsOf(f).Single(s => s.ClassId == "supply-tender");
        tender.Food = 1;
        var held = tender.CargoFood;
        sim.StepMany(48);
        Assert.True(tender.Food > 1, $"food {tender.Food}");
        Assert.True(tender.CargoFood < held);
        Assert.False(SupplyLines.Hungry(tender));
    }

    [Fact]
    public void A_capital_ship_draws_more_food_from_a_tender_than_an_escort()
    {
        var sim = NewGame();
        var at = OpenSpace(sim);
        var a = NewFleet(sim, Side.Player, at, "escort", "supply-tender");
        var b = NewFleet(sim, Side.Player, at + new Vec2(0, 5000), "capital", "supply-tender");
        foreach (var f in new[] { a, b })
        {
            f.TenderStays = true;
            foreach (var s in sim.World.ShipsOf(f).Where(s => !SupplyLines.IsSupplyShip(s))) s.Food = 60;
        }
        double Hold(Fleet f) => sim.World.ShipsOf(f).Single(SupplyLines.IsSupplyShip).CargoFood;
        double Food(Fleet f) => sim.World.ShipsOf(f).Single(s => !SupplyLines.IsSupplyShip(s)).Food;
        var (a0, b0, fa, fb) = (Hold(a), Hold(b), Food(a), Food(b));
        sim.StepMany(2);
        // Days of food gained for each unit taken from the hold: the escort's small crew gets far more.
        var escort = (Food(a) - fa) / (a0 - Hold(a));
        var capital = (Food(b) - fb) / (b0 - Hold(b));
        Assert.True(escort > capital * 3, $"escort {escort:0.##} days a unit, capital {capital:0.##}");
    }

    [Fact]
    public void A_fleets_tender_leaves_to_restock_when_its_hold_runs_low_and_comes_back()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = NewFleet(sim, Side.Player, OpenSpace(sim), "gun-frigate", "gun-frigate", "supply-tender");
        var tender = w.ShipsOf(f).Single(s => s.ClassId == "supply-tender");
        tender.CargoFuel = 0; tender.CargoAmmo = 0; tender.CargoFood = 0; tender.CargoFuels.Clear();
        sim.StepMany(2);
        Assert.DoesNotContain(tender.Id, f.ShipIds);
        var run = w.FleetOf(tender)!;
        Assert.Equal(f.Id, run.TenderOf);
        Assert.Equal(f.Id, run.SupplyRunFor);
        Assert.NotNull(run.Flight);
        for (var i = 0; i < 120 * 24 && !f.ShipIds.Contains(tender.Id); i++) sim.Step();
        Assert.Contains(tender.Id, f.ShipIds);
        Assert.True(SupplyLines.CargoShare(tender) > 0.5);
    }

    [Fact]
    public void A_tender_told_to_stay_does_not_leave()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = NewFleet(sim, Side.Player, OpenSpace(sim), "gun-frigate", "supply-tender");
        f.TenderStays = true;
        var tender = w.ShipsOf(f).Single(s => s.ClassId == "supply-tender");
        tender.CargoFuel = 0; tender.CargoAmmo = 0; tender.CargoFood = 0; tender.CargoFuels.Clear();
        sim.StepMany(24);
        Assert.Contains(tender.Id, f.ShipIds);
    }

    [Fact]
    public void Armed_enemies_seen_near_a_fleet_make_its_supplies_wait_at_a_safe_point()
    {
        var sim = NewGame();
        var w = sim.World;
        var at = OpenSpace(sim);
        var fleet = NewFleet(sim, Side.Player, at, "gun-frigate");
        var enemy = NewFleet(sim, Side.Rival, at + new Vec2(30, 0), "gun-frigate", "gun-frigate");
        enemy.LetRetreatGo = true; // our lone frigate pulls back from the pair: keep it alive so the supply run has a fleet to serve
        w.Sightings.Add(new Sighting { SeenBy = Side.Player, Of = Side.Rival, FleetId = enemy.Id, Name = enemy.Name, Ships = 2, Position = enemy.Position,
            Hour = w.Hour, SpottedHour = w.Hour, Visible = true, Classes = Sensors.ClassesOf(w, enemy) });
        Assert.NotNull(SupplyLines.DangerAt(w, Side.Player, fleet.Position));
        Assert.Null(SupplyLines.DangerAt(w, Side.Player, at + new Vec2(0, 0.5 * Physics.MmPerAu)));
        var home = Economy.HomeOf(w, Side.Player)!;
        var run = NewFleet(sim, Side.Player, home.Position + new Vec2(5, 0), "tanker");
        sim.Issue(new SetSupplyRunCommand(Side.Player, run.Id, fleet.Id));
        sim.ApplyPending();
        sim.StepMany(3);
        Assert.Contains("waiting", run.SupplyRunProblem ?? "");
        // It never heads for the fleet itself.
        Assert.True(run.Flight == null || run.Flight.TargetFleetId != fleet.Id);
        Assert.True(Vec2.Distance(run.Position, fleet.Position) > SupplyLines.DangerAu * Physics.MmPerAu * 0.9 || run.Flight == null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_fleet_drifting_in_its_zone_makes_only_a_few_short_burns(bool pinned)
    {
        var sim = NewGame();
        var w = sim.World;
        var pale = BodyNamed(sim, "pale");
        var at = pinned ? pale.Position + new Vec2(400, 0) : OpenSpace(sim);
        var f = NewFleet(sim, Side.Player, at, "gun-frigate", "gun-frigate");
        if (pinned) f.Velocity = Combat.BodyVelocity(w, pale);
        sim.Issue(new CreateZoneCommand(Side.Player, ZoneKind.Raid, at, 300, FleetId: f.Id, AnchorBodyId: pinned ? pale.Id : null));
        sim.ApplyPending();
        var zone = Assert.Single(w.Zones);
        var fuel0 = w.ShipsOf(f).Sum(s => s.Supplies);
        var burns = 0;
        var outside = 0;
        for (var day = 0; day < 10; day++)
        {
            sim.StepMany(GameTime.TicksPerDay);
            burns += Lanes.BurnsLastDay(w, f);
            if (Vec2.Distance(f.Position, zone.Center) > zone.RadiusMm * 1.05) outside++;
        }
        Assert.True(burns <= 10, $"{burns} burns in 10 days");
        Assert.True(outside <= 1, $"outside the zone on {outside} days");
        var used = fuel0 - w.ShipsOf(f).Sum(s => s.Supplies);
        Assert.True(used < fuel0 * 0.15, $"used {used:0.#} of {fuel0:0}");
    }

    [Fact]
    public void A_roaming_fleet_pushes_without_braking()
    {
        var sim = NewGame();
        var w = sim.World;
        var at = OpenSpace(sim);
        var f = NewFleet(sim, Side.Player, at, "gun-frigate");
        sim.Issue(new CreateZoneCommand(Side.Player, ZoneKind.Raid, at, 300, Hold: ZoneHold.Roam, FleetId: f.Id));
        sim.ApplyPending();
        var fuel0 = w.ShipsOf(f).Sum(s => s.Supplies);
        sim.StepMany(10 * GameTime.TicksPerDay);
        Assert.Null(f.Flight);
        var used = fuel0 - w.ShipsOf(f).Sum(s => s.Supplies);
        Assert.True(used < fuel0 * 0.1, $"used {used:0.#} of {fuel0:0}");
    }

    [Fact]
    public void A_zone_can_change_its_job_and_a_guard_takes_on_only_what_it_can_beat()
    {
        var sim = NewGame();
        var w = sim.World;
        var at = OpenSpace(sim);
        var guard = NewFleet(sim, Side.Player, at, "gun-frigate");
        sim.Issue(new CreateZoneCommand(Side.Player, ZoneKind.Raid, at, 300, FleetId: guard.Id));
        sim.ApplyPending();
        var z = Assert.Single(w.Zones);
        Assert.StartsWith("Raiding zone", z.Name);
        sim.Issue(new EditZoneCommand(Side.Player, z.Id, z.Center, z.RadiusMm, null, null, z.T, z.Hold, z.Prey, ZoneKind.Guard));
        sim.ApplyPending();
        Assert.Equal(ZoneKind.Guard, z.Kind);
        Assert.StartsWith("Guard zone", z.Name);
        // A much stronger enemy comes in: the lone frigate lets it be.
        var big = NewFleet(sim, Side.Rival, at + new Vec2(100, 0), "cruiser", "cruiser", "gun-frigate");
        w.Sightings.Add(new Sighting { SeenBy = Side.Player, Of = Side.Rival, FleetId = big.Id, Name = big.Name, Ships = 3, Position = big.Position,
            Hour = w.Hour, SpottedHour = w.Hour, Visible = true, Classes = Sensors.ClassesOf(w, big) });
        sim.StepMany(2);
        Assert.True(guard.Flight?.TargetFleetId != big.Id && guard.Chase?.TargetFleetId != big.Id);
    }
}
