using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Supply lanes from sightings, raiding zones that slide with their bodies, ambushes, hunts and captured cargo.</summary>
public class LaneTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);

    /// <summary>A new fleet of these ships for a side, stopped at a point in space (test setup).</summary>
    private static Fleet NewFleet(Simulation sim, Side side, Vec2 at, params string[] classes)
    {
        var ships = classes.Select(c => Economy.NewShip(sim.World, side, c)).ToList();
        var f = Fleets.Create(sim.World, side, ships, at);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
        return f;
    }

    /// <summary>A point out in empty space, a third of an AU outside Verdant's orbit.</summary>
    private static Vec2 OpenSpace(Simulation sim) => BodyNamed(sim, "verdant").Position * 1.3;

    private static void ToNextDay(Simulation sim) => sim.StepMany(GameTime.TicksPerDay - (int)(sim.World.Tick % GameTime.TicksPerDay));

    [Fact]
    public void Cargo_seen_under_way_between_two_enemy_planets_makes_a_lane()
    {
        var sim = NewGame();
        sim.Step();
        var verdant = BodyNamed(sim, "verdant");
        var pale = BodyNamed(sim, "pale");
        pale.Owner = Side.Player;
        var dir = (pale.Position - verdant.Position).Normalized();
        sim.World.LaneObs.Add(new LaneObs
        {
            SeenBy = Side.Rival, FleetId = 999, Position = verdant.Position + (pale.Position - verdant.Position) * 0.5, Velocity = Combat.BodyVelocity(sim.World, verdant) + dir * 20, Hour = sim.World.Hour,
        });
        ToNextDay(sim);
        var lane = Assert.Single(sim.World.Lanes);
        Assert.Equal(Side.Rival, lane.SeenBy);
        Assert.Equal(new[] { verdant.Id, pale.Id }.OrderBy(i => i), new[] { lane.FromBodyId, lane.ToBodyId });
        Assert.True(Lanes.Confidence(sim.World, lane) > 0);
        Assert.Single(lane.Recent);
        Assert.Empty(sim.World.LaneObs);
    }

    [Fact]
    public void A_zone_on_a_run_slides_with_its_bodies()
    {
        var sim = NewGame();
        sim.Step();
        var verdant = BodyNamed(sim, "verdant");
        var rust = BodyNamed(sim, "rust");
        sim.Issue(new CreateZoneCommand(Side.Rival, ZoneKind.Raid, Vec2.Zero, 300, verdant.Id, rust.Id, 0.5));
        sim.ApplyPending();
        var zone = Assert.Single(sim.World.Zones);
        var before = Lanes.CenterOf(sim.World, zone);
        sim.StepMany(5 * GameTime.TicksPerDay);
        var now = Lanes.PointOn(sim.World, verdant.Id, rust.Id, 0.5)!.Value;
        Assert.True(Vec2.Distance(zone.Center, now) < 1e-6);
        Assert.True(Vec2.Distance(before, now) > 100);
    }

    [Fact]
    public void A_raider_holding_a_zone_goes_after_cargo_that_comes_in()
    {
        var sim = NewGame();
        sim.Step();
        var at = OpenSpace(sim);
        var raider = NewFleet(sim, Side.Rival, at, "gun-frigate", "gun-frigate");
        sim.Issue(new CreateZoneCommand(Side.Rival, ZoneKind.Raid, at, 150, FleetId: raider.Id));
        sim.ApplyPending();
        Assert.Equal(Mission.Ambush, raider.Mission);
        var freighter = NewFleet(sim, Side.Player, at + new Vec2(GameData.Combat.ContactRangeMm * 1.6, 0), "freighter");
        // Under way (burning, so it shows up well beyond a cold ship's tiny signature).
        sim.Issue(new PlanFlightCommand(freighter.Id, new FlightRequest(BodyNamed(sim, "verdant").Id, BodyNamed(sim, "verdant").Position, 0.6,
            ArrivalStyle.Brake, HoldStyle.Orbit, sim.World.Tick)));
        sim.ApplyPending();
        for (var i = 0; i < 12 && raider.Flight == null && raider.Chase == null && !Heat.InBattle(sim.World, raider); i++) sim.Step();
        Assert.Equal(Stance.Engage, raider.Stance);
        Assert.True(raider.Flight?.TargetFleetId == freighter.Id || raider.Chase?.TargetFleetId == freighter.Id || Heat.InBattle(sim.World, raider));
    }

    [Fact]
    public void A_raider_lets_warships_pass_when_it_preys_on_cargo()
    {
        var sim = NewGame();
        sim.Step();
        var at = OpenSpace(sim);
        var raider = NewFleet(sim, Side.Rival, at, "gun-frigate");
        sim.Issue(new CreateZoneCommand(Side.Rival, ZoneKind.Raid, at, 150, FleetId: raider.Id));
        sim.ApplyPending();
        var warship = NewFleet(sim, Side.Player, at + new Vec2(GameData.Combat.ContactRangeMm * 1.6, 0), "gun-frigate");
        sim.Issue(new PlanFlightCommand(warship.Id, new FlightRequest(BodyNamed(sim, "verdant").Id, BodyNamed(sim, "verdant").Position, 0.6,
            ArrivalStyle.Brake, HoldStyle.Orbit, sim.World.Tick)));
        sim.ApplyPending();
        sim.StepMany(3);
        Assert.True(Sensors.Sees(sim.World, Side.Rival, warship));
        Assert.Null(raider.Flight?.TargetFleetId);
    }

    [Fact]
    public void Keeping_station_costs_fuel_and_drifting_does_not()
    {
        var sim = NewGame();
        sim.Step();
        var at = OpenSpace(sim);
        var drifter = NewFleet(sim, Side.Rival, at, "gun-frigate");
        var keeper = NewFleet(sim, Side.Rival, at + new Vec2(0, 2000), "gun-frigate");
        sim.Issue(new CreateZoneCommand(Side.Rival, ZoneKind.Picket, at, 150, FleetId: drifter.Id));
        sim.Issue(new CreateZoneCommand(Side.Rival, ZoneKind.Picket, at + new Vec2(0, 2000), 150, Hold: ZoneHold.StationKeep, FleetId: keeper.Id));
        sim.ApplyPending();
        var d0 = sim.World.ShipsOf(drifter).Sum(s => s.Supplies);
        var k0 = sim.World.ShipsOf(keeper).Sum(s => s.Supplies);
        sim.StepMany(GameTime.TicksPerDay);
        var drifted = d0 - sim.World.ShipsOf(drifter).Sum(s => s.Supplies);
        var kept = k0 - sim.World.ShipsOf(keeper).Sum(s => s.Supplies);
        Assert.True(kept > drifted + 1e-6, $"kept {kept}, drifted {drifted}");
        Assert.Equal(Stance.Avoid, drifter.Stance);
    }

    [Fact]
    public void Hunt_draws_a_raiding_zone_on_the_run()
    {
        var sim = NewGame();
        sim.Step();
        var fleet = sim.World.Fleets.First(f => f.Side == Side.Player);
        var rust = BodyNamed(sim, "rust");
        var flint = BodyNamed(sim, "flint");
        sim.Issue(new HuntCommand(fleet.Id, rust.Id, flint.Id));
        sim.ApplyPending();
        var zone = Assert.Single(sim.World.Zones);
        Assert.Equal(ZoneKind.Raid, zone.Kind);
        Assert.Equal(zone.Id, fleet.ZoneId);
        Assert.Equal(Mission.Hunt, fleet.Mission);
        sim.Issue(new DeleteZoneCommand(Side.Player, zone.Id));
        sim.ApplyPending();
        Assert.Null(fleet.ZoneId);
        Assert.Equal(Mission.None, fleet.Mission);
        sim.CheckRules();
    }

    [Fact]
    public void A_freighter_lost_to_a_raider_gives_up_half_its_cargo_and_marks_the_run()
    {
        var sim = NewGame();
        sim.Step();
        var verdant = BodyNamed(sim, "verdant");
        var pale = BodyNamed(sim, "pale");
        var at = OpenSpace(sim);
        NewFleet(sim, Side.Rival, at, "gun-frigate");
        var freighter = NewFleet(sim, Side.Player, at + new Vec2(20, 0), "freighter");
        sim.World.Shipments.Add(new Shipment
        {
            Id = sim.World.NextId++, Side = Side.Player, FleetId = freighter.Id, FromBodyId = verdant.Id, ToBodyId = pale.Id,
            Cargo = new Dictionary<string, double> { ["alloys"] = 40 }, Loaded = true, StartHour = sim.World.Hour,
        });
        double RivalAlloys() => sim.World.Bodies.Where(b => b.Owner == Side.Rival).Sum(b => Colonies.Held(b, "alloys"));
        var held = RivalAlloys();
        Combat.Destroy(sim, sim.World.ShipsOf(freighter).Single());
        var totals = Economy.Of(sim.World, Side.Rival).Totals;
        Assert.Equal(1, totals.AmbushCatches);
        Assert.Equal(20, totals.CargoCaptured, 6);
        Assert.Equal(held + 20, RivalAlloys(), 6);
        var spot = Assert.Single(sim.World.LossSpots);
        Assert.Equal(Side.Player, spot.Side);
    }

    [Fact]
    public void Old_lanes_are_forgotten()
    {
        var sim = NewGame();
        sim.Step();
        var verdant = BodyNamed(sim, "verdant");
        var pale = BodyNamed(sim, "pale");
        sim.World.Lanes.Add(new Lane { Id = sim.World.NextId++, SeenBy = Side.Rival, FromBodyId = verdant.Id, ToBodyId = pale.Id, Seen = 3, FirstHour = 0, LastHour = 0 });
        sim.StepMany((int)(Lanes.ForgetDays + 1) * GameTime.TicksPerDay);
        Assert.Empty(sim.World.Lanes);
    }
}
