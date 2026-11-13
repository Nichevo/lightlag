using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Fleets are named for their role or ships; the player can name them; zones can be pinned to a planet and managed.</summary>
public class FleetNameTests
{
    private static Simulation NewGame()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory());
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
    public void New_fleets_are_named_for_their_ships()
    {
        var sim = NewGame();
        var at = OpenSpace(sim);
        Assert.Equal("Supply fleet", FleetNames.KindOf(NewFleet(sim, Side.Player, at, "tanker").Name));
        Assert.Equal("Settler fleet", NewFleet(sim, Side.Player, at, "colony-ship").Name);
        Assert.Equal("Troop fleet", NewFleet(sim, Side.Player, at, "troop-ship").Name);
        Assert.Equal("Invasion fleet", NewFleet(sim, Side.Player, at, "troop-ship", "gun-frigate").Name);
        Assert.Equal("Scout fleet", NewFleet(sim, Side.Player, at, "picket").Name);
        Assert.StartsWith("Rival fleet", NewFleet(sim, Side.Rival, at, "gun-frigate").Name);
    }

    [Fact]
    public void Fleets_of_a_kind_are_numbered_and_numbers_are_reused()
    {
        var sim = NewGame();
        var at = OpenSpace(sim);
        var existing = sim.World.Fleets.Count(f => f.Side == Side.Player && FleetNames.KindOf(f.Name) == "Battle fleet");
        var a = NewFleet(sim, Side.Player, at, "gun-frigate");
        var b = NewFleet(sim, Side.Player, at, "gun-frigate");
        Assert.NotEqual(a.Name, b.Name);
        Assert.Equal("Battle fleet", FleetNames.KindOf(a.Name));
        Assert.Equal("Battle fleet", FleetNames.KindOf(b.Name));
        var names = sim.World.Fleets.Where(f => f.Side == Side.Player).Select(f => f.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(existing + 2, sim.World.Fleets.Count(f => f.Side == Side.Player && FleetNames.KindOf(f.Name) == "Battle fleet"));
    }

    [Fact]
    public void A_role_renames_the_fleet_unless_the_player_named_it()
    {
        var sim = NewGame();
        var at = OpenSpace(sim);
        var f = NewFleet(sim, Side.Player, at, "gun-frigate", "gun-frigate");
        sim.Issue(new SetFleetRoleCommand(f.Id, FleetRole.Defence));
        sim.ApplyPending();
        Assert.Equal("Defence fleet", FleetNames.KindOf(f.Name));
        sim.Issue(new SetFleetRoleCommand(f.Id, FleetRole.AntiPiracy));
        sim.ApplyPending();
        Assert.Equal("Pirate patrol", FleetNames.KindOf(f.Name));

        sim.Issue(new RenameFleetCommand(Side.Player, f.Id, "Home Guard"));
        sim.ApplyPending();
        Assert.Equal("Home Guard", f.Name);
        Assert.True(f.NamedByPlayer);
        sim.Issue(new SetFleetRoleCommand(f.Id, FleetRole.Support));
        sim.ApplyPending();
        sim.StepMany(GameTime.TicksPerDay);
        Assert.Equal("Home Guard", f.Name);

        // An empty name goes back to the automatic one.
        sim.Issue(new RenameFleetCommand(Side.Player, f.Id, ""));
        sim.ApplyPending();
        Assert.False(f.NamedByPlayer);
        Assert.Equal("Support fleet", FleetNames.KindOf(f.Name));
    }

    [Fact]
    public void A_fleet_name_already_in_use_is_refused()
    {
        var sim = NewGame();
        var at = OpenSpace(sim);
        var a = NewFleet(sim, Side.Player, at, "gun-frigate");
        var b = NewFleet(sim, Side.Player, at, "tanker");
        var old = b.Name;
        sim.Issue(new RenameFleetCommand(Side.Player, b.Id, a.Name.ToUpperInvariant()));
        sim.ApplyPending();
        Assert.Equal(old, b.Name);
        Assert.NotNull(FleetNames.Problem(sim.World, b, a.Name));
    }

    [Fact]
    public void Splitting_off_supply_ships_names_the_new_fleet_for_them()
    {
        var sim = NewGame();
        var at = OpenSpace(sim);
        var f = NewFleet(sim, Side.Player, at, "gun-frigate", "tanker");
        Assert.Equal("Battle fleet", FleetNames.KindOf(f.Name));
        var tanker = sim.World.ShipsOf(f).Single(s => s.ClassId == "tanker");
        new SplitFleetCommand(f.Id, new List<int> { tanker.Id }).Apply(sim.World, sim);
        var split = sim.World.FleetOf(tanker)!;
        Assert.Equal("Supply fleet", FleetNames.KindOf(split.Name));
        Assert.Equal("Battle fleet", FleetNames.KindOf(f.Name));
    }

    [Fact]
    public void Old_style_names_are_replaced_within_the_hour()
    {
        var sim = NewGame();
        var f = sim.World.Fleets.First(x => x.Side == Side.Player);
        f.Name = "Fleet 1"; // as in a save from before fleets were named for their role
        sim.StepMany(GameTime.TicksPerDay / 24 + 1);
        Assert.NotEqual("Fleet 1", f.Name);
    }

    [Fact]
    public void A_pinned_zone_moves_with_its_planet_and_keeps_its_place_beside_it()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        var at = verdant.Position * 1.05; // a little outside Verdant's orbit
        sim.Issue(new CreateZoneCommand(Side.Player, ZoneKind.Raid, at, 300, AnchorBodyId: verdant.Id));
        sim.ApplyPending();
        var zone = Assert.Single(sim.World.Zones, z => z.Side == Side.Player);
        Assert.Equal(verdant.Id, zone.AnchorBodyId);
        var startGap = Vec2.Distance(Lanes.CenterOf(sim.World, zone), verdant.Position);
        var startDist = Lanes.CenterOf(sim.World, zone).Length;
        var before = verdant.Position;
        sim.StepMany(20 * GameTime.TicksPerDay);
        Assert.True(Vec2.Distance(before, verdant.Position) > 1000, "Verdant should have moved along its orbit");
        Assert.True(Vec2.Distance(zone.Center, Lanes.CenterOf(sim.World, zone)) < 1, "the zone's centre follows each hour");
        Assert.Equal(startGap, Vec2.Distance(zone.Center, verdant.Position), 3);
        // Still outside the orbit, about as far from the sun as before.
        Assert.Equal(startDist, zone.Center.Length, startDist * 0.01);
    }

    [Fact]
    public void Zones_can_be_renamed_pinned_unpinned_and_deleted()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        var at = verdant.Position * 1.05;
        sim.Issue(new CreateZoneCommand(Side.Player, ZoneKind.Raid, at, 300));
        sim.ApplyPending();
        var zone = Assert.Single(sim.World.Zones, z => z.Side == Side.Player);
        Assert.Null(zone.AnchorBodyId);
        Assert.Equal(verdant.Id, Lanes.AnchorFor(sim.World, at)?.Id);

        sim.Issue(new RenameZoneCommand(Side.Player, zone.Id, "Verdant gate"));
        sim.Issue(new PinZoneCommand(Side.Player, zone.Id, verdant.Id));
        sim.ApplyPending();
        Assert.Equal("Verdant gate", zone.Name);
        Assert.Equal(verdant.Id, zone.AnchorBodyId);
        Assert.Equal(at.X, Lanes.CenterOf(sim.World, zone).X, 3);

        sim.StepMany(5 * GameTime.TicksPerDay);
        var held = zone.Center;
        sim.Issue(new PinZoneCommand(Side.Player, zone.Id, null));
        sim.ApplyPending();
        Assert.Null(zone.AnchorBodyId);
        sim.StepMany(2 * GameTime.TicksPerDay);
        Assert.Equal(held, zone.Center);

        sim.Issue(new DeleteZoneCommand(Side.Player, zone.Id));
        sim.ApplyPending();
        Assert.DoesNotContain(sim.World.Zones, z => z.Side == Side.Player);
    }

    [Fact]
    public void A_roaming_fleet_moves_about_its_zone_with_small_burns()
    {
        var sim = NewGame();
        var at = OpenSpace(sim);
        var f = NewFleet(sim, Side.Player, at, "gun-frigate");
        sim.Issue(new CreateZoneCommand(Side.Player, ZoneKind.Raid, at, 400, Hold: ZoneHold.Roam, FleetId: f.Id));
        sim.ApplyPending();
        var zone = Assert.Single(sim.World.Zones, z => z.Side == Side.Player);
        var spots = new List<Vec2>();
        var flights = 0;
        var wasFlying = false;
        for (var day = 0; day < 8 * 24; day++)
        {
            sim.StepMany(GameTime.TicksPerDay / 24);
            if (f.Flight != null && !wasFlying) flights++;
            wasFlying = f.Flight != null;
            spots.Add(f.Position - zone.Center);
            Assert.True(Vec2.Distance(f.Position, zone.Center) < zone.RadiusMm * 2.5, "stays round its zone");
        }
        Assert.True(flights >= 3, $"roams with burns now and then ({flights})");
        Assert.True(spots.Max(p => p.X) - spots.Min(p => p.X) > zone.RadiusMm * 0.3 || spots.Max(p => p.Y) - spots.Min(p => p.Y) > zone.RadiusMm * 0.3,
            "covers part of the zone");
    }
}
