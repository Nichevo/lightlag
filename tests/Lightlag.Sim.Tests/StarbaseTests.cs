using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>
/// The station line: tiers built and upgraded, yards that gate hull length, the orbit shells (low, high, far),
/// the station serving friendly fleets, its guns and umbrella, boarding and capture, scuttling, the fortress
/// landing factor, moons under a planet's guns and orbital mine upkeep.
/// </summary>
public class StarbaseTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Starts.Classic(Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory()));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        sim.World.Minds.Clear();
        return sim;
    }

    private static Body Named(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);

    private static Fleet InOrbit(Simulation sim, Side side, Body b, params string[] classes)
    {
        var ships = classes.Select(c => Economy.NewShip(sim.World, side, c)).ToList();
        foreach (var s in ships.Where(Planets.IsTroopShip)) s.CargoTroops = Planets.TroopHold(s);
        var f = Fleets.Create(sim.World, side, ships, b.Position);
        f.Flight = null;
        f.Position = b.Position + new Vec2(Fleets.OrbitRadiusFor(b), 0);
        Fleets.EnterOrbit(f, b);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
        return f;
    }

    private static Starbase Station(Simulation sim, Side side, Body b, string classId)
    {
        var w = sim.World;
        foreach (var t in Starbases.D.Tiers.Where(t => t.Tier <= Starbases.TierOf(classId).Tier)) foreach (var n in t.Needs ?? new List<string>()) if (!Economy.Of(w, side).Researched.Contains(n)) Economy.Of(w, side).Researched.Add(n);
        Economy.Of(w, side).Industry += GameData.ShipClass(classId).Cost + 1;
        new BuildStarbaseCommand(side, b.Id, classId).Apply(w, sim);
        var st = Starbases.Of(w, b.Id, side)!;
        st.ReadyHour = w.Hour;
        return st;
    }

    private static Body Home(Simulation sim) => Economy.ShipyardOf(sim.World, Side.Player)!;

    private static Body RivalColony(Simulation sim)
    {
        var vela = Named(sim, "vela");
        vela.Owner = Side.Rival;
        vela.PopK = 30;
        vela.Happiness = 60;
        Planets.EnsureStarted(sim.World);
        return vela;
    }

    [Fact]
    public void A_station_costs_its_tier_and_an_upgrade_costs_the_difference_in_half_the_days()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Home(sim);
        var me = Economy.Of(w, Side.Player);
        me.Industry = 1000;
        Assert.Contains("Research", Starbases.CanBuild(w, Side.Player, home.Id, "station-defence"));
        new BuildStarbaseCommand(Side.Player, home.Id, "station-platform").Apply(w, sim);
        var st = Assert.Single(w.Starbases);
        Assert.Equal(960, me.Industry, 6);
        Assert.Equal(w.Hour + 5 * 24, st.ReadyHour, 6);
        Assert.NotNull(Starbases.CanBuild(w, Side.Player, home.Id, "station-platform")); // one per body
        st.ReadyHour = w.Hour;
        me.Researched.Add("ground-launchers");
        Assert.Equal(110, Starbases.UpgradeCost(st, "station-defence"), 6);
        new RefitStarbaseCommand(Side.Player, st.Id, "station-defence", Designer.StandardId).Apply(w, sim);
        Assert.Equal(850, me.Industry, 6);
        Assert.Equal(14 * 12, st.Refit!.TotalHours, 6);
        // Tiers go one at a time.
        Assert.NotNull(Starbases.CanRefit(w, Side.Player, st, "station-fortress", Designer.StandardId));
        sim.StepMany(14 * 12 + 1);
        Assert.Equal("station-defence", st.ClassId);
        Assert.Null(st.Refit);
        Assert.Equal(Starbases.MaxArmour(st), st.Armour, 6);
    }

    [Fact]
    public void Long_hulls_need_a_big_enough_yard_and_go_to_the_slipway_that_fits()
    {
        var sim = NewGame();
        var w = sim.World;
        var me = Economy.Of(w, Side.Player);
        me.Researched.Add(Tech.CruiserHull);
        me.Industry = 2000;
        var home = Home(sim);
        Assert.Equal(180, Starbases.YardLength(w, Side.Player, home), 6);
        Assert.Contains("great yard", Economy.CanQueue(w, Side.Player, OrderKind.Ship, "cruiser"));
        Assert.Null(Economy.CanQueue(w, Side.Player, OrderKind.Ship, "gun-frigate"));
        // A starbase over another world the player holds: the cruiser is laid down there.
        var vela = Named(sim, "vela");
        vela.Owner = Side.Player;
        vela.PopK = 10;
        Station(sim, Side.Player, vela, "station-starbase");
        Assert.Equal(260, Starbases.YardLength(w, Side.Player, vela), 6);
        Assert.Equal(vela, Starbases.YardFor(w, Side.Player, "cruiser"));
        Assert.Null(Economy.CanQueue(w, Side.Player, OrderKind.Ship, "cruiser"));
        new QueueOrderCommand(Side.Player, OrderKind.Ship, "cruiser").Apply(w, sim);
        var order = w.Orders.Single(o => o.Key == "cruiser");
        Assert.Equal(vela, Economy.YardFor(w, order));
    }

    [Fact]
    public void Far_orbit_is_out_of_reach_and_does_nothing()
    {
        var sim = NewGame();
        var w = sim.World;
        var vela = RivalColony(sim);
        var fort = Station(sim, Side.Rival, vela, "station-defence");
        var f = InOrbit(sim, Side.Player, vela, "gun-frigate", "gun-frigate");
        f.OrbitHeight = OrbitHeight.Far;
        f.Bombing = BombTarget.Farms;
        var armour = w.ShipsOf(f).Sum(s => s.Armour);
        sim.StepMany(24);
        Assert.Equal(armour, w.ShipsOf(f).Sum(s => s.Armour), 6);
        Assert.Equal(Starbases.MaxArmour(fort), fort.Armour, 6);
        Assert.False(Economy.Blockaded(w, vela));
        Assert.False(Starbases.Besieging(f, vela));
        // High orbit: the station's guns reach it, unless told to fire at low orbit only.
        f.OrbitHeight = OrbitHeight.High;
        new SetStationPlanCommand(Side.Rival, fort.Id, StationFire.LowOnly, false).Apply(w, sim);
        armour = w.ShipsOf(f).Sum(s => s.Armour);
        sim.StepMany(6);
        Assert.Equal(armour, w.ShipsOf(f).Sum(s => s.Armour), 6);
        new SetStationPlanCommand(Side.Rival, fort.Id, StationFire.Both, false).Apply(w, sim);
        sim.StepMany(6);
        Assert.True(w.ShipsOf(f).Sum(s => s.Armour) < armour);
    }

    [Fact]
    public void High_orbit_bombs_at_a_share_of_low_orbit()
    {
        double Bombed(OrbitHeight h)
        {
            var sim = NewGame();
            var vela = RivalColony(sim);
            var data = Planets.BuildingKind(Planets.Farm)!;
            for (var i = 0; i < 4; i++)
            {
                var (x, y, turned) = Colonies.BestSpot(vela, data)!.Value;
                Colonies.AddPiece(sim.World, vela, data, x, y, turned, built: true);
            }
            Colonies.SyncBuildings(vela);
            var f = InOrbit(sim, Side.Player, vela, "missile-destroyer", "missile-destroyer");
            f.OrbitHeight = h;
            new SetBombingCommand(f.Id, BombTarget.People).Apply(sim.World, sim);
            var pop = vela.PopK;
            sim.StepMany(12);
            return pop - vela.PopK;
        }
        var low = Bombed(OrbitHeight.Low);
        var high = Bombed(OrbitHeight.High);
        Assert.True(low > 0);
        Assert.True(high > 0 && high < low);
    }

    [Fact]
    public void A_station_shields_and_serves_a_friendly_fleet_under_it_and_undocking_takes_hours()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Home(sim);
        var st = Station(sim, Side.Player, home, "station-starbase");
        var fleet = w.Fleets.First(f => f.Side == Side.Player && Combat.Armed(w, f));
        Fleets.EnterOrbit(fleet, home);
        fleet.OrbitHeight = OrbitHeight.Low;
        sim.Step();
        Assert.Equal(Starbases.Spec(st).PointDefence, Starbases.Umbrella(w, fleet), 6);
        Assert.Equal(home, Starbases.StationedAt(w, fleet));
        Assert.True(Colonies.RepairsHere(w, home, Side.Player));
        var ship = w.ShipsOf(fleet).First();
        ship.Armour = Tech.MaxArmour(w, ship) / 2;
        sim.Step();
        Assert.True(fleet.Docked);
        var mended = ship.Armour;
        Assert.True(mended > Tech.MaxArmour(w, ship) / 2);
        // Ordered off while mending: it takes the undock hours to get away.
        var vela = Named(sim, "vela");
        new PlanFlightCommand(fleet.Id, new FlightRequest(vela.Id, vela.Position, 1, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick)).Apply(w, sim);
        Assert.NotNull(fleet.Flight);
        Assert.True(fleet.Flight!.DepartHour >= w.Hour + Starbases.D.UndockHours - 1e-6);
    }

    [Fact]
    public void A_fleet_under_its_planets_station_answers_a_help_call_at_once()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Home(sim);
        Station(sim, Side.Player, home, "station-platform");
        var fleet = w.Fleets.First(f => f.Side == Side.Player && Combat.Armed(w, f));
        Fleets.EnterOrbit(fleet, home);
        fleet.OrbitHeight = OrbitHeight.Low;
        fleet.Role = FleetRole.Supply; // a role that would not answer a call elsewhere
        Assert.Equal(home, Starbases.StationedAt(w, fleet));
        Assert.Null(HelpCalls.WhyNot(w, fleet, new HelpCall { Side = Side.Player, BodyId = home.Id, Position = home.Position }, checkFree: false));
    }

    [Fact]
    public void Boarders_take_a_station_with_its_hull_and_its_guns_fire_at_half_until_their_ammunition_arrives()
    {
        var sim = NewGame();
        var w = sim.World;
        var vela = RivalColony(sim);
        var st = Station(sim, Side.Rival, vela, "station-platform");
        st.ScuttleIfTaken = false;
        var troops = Enumerable.Repeat("troop-ship", 3).Prepend("gun-frigate").ToArray();
        var f = InOrbit(sim, Side.Player, vela, troops);
        f.OrbitHeight = OrbitHeight.Low;
        f.Stance = Stance.HoldPosition;
        Assert.Null(Starbases.CanBoard(w, f, st));
        f.OrbitHeight = OrbitHeight.High;
        Assert.Contains("low orbit", Starbases.CanBoard(w, f, st));
        f.OrbitHeight = OrbitHeight.Low;
        new BoardStationCommand(f.Id, st.Id).Apply(w, sim);
        var boarding = Assert.Single(w.Boardings);
        Assert.Equal(st.Id, boarding.StationId);
        for (var h = 0; h < 24 * 4 && boarding.Outcome == null; h++) sim.Step();
        Assert.NotNull(boarding.Outcome);
        if (st.Side == Side.Player)
        {
            Assert.Contains(st, w.Starbases);
            Assert.True(st.ForeignAmmo);
            Assert.Equal(Starbases.D.EmptyGunShare, Starbases.GunShare(st), 6);
            Assert.Equal(Side.Rival, vela.Owner); // the planet is not taken with it
        }
    }

    [Fact]
    public void Capture_tows_off_the_hull_on_the_slipway()
    {
        var sim = NewGame();
        var w = sim.World;
        var vela = RivalColony(sim);
        var st = Station(sim, Side.Rival, vela, "station-starbase");
        var order = new WorkOrder { Id = w.NextId++, Side = Side.Rival, Kind = OrderKind.Ship, Key = "cruiser", Paid = true, HoursLeft = 50, TotalHours = 100, YardBodyId = vela.Id };
        w.Orders.Add(order);
        var f = InOrbit(sim, Side.Player, vela, "gun-frigate");
        var ships = w.Ships.Count(s => s.Side == Side.Player);
        Starbases.Capture(sim, st, f);
        Assert.Equal(Side.Player, st.Side);
        Assert.DoesNotContain(order, w.Orders);
        Assert.Equal(ships + 1, w.Ships.Count(s => s.Side == Side.Player));
        var prize = w.Ships.Last(s => s.Side == Side.Player);
        Assert.Equal("cruiser", prize.ClassId);
        Assert.True(prize.Armour < Tech.MaxArmour(w, prize));
    }

    [Fact]
    public void Scuttling_removes_the_station_and_ends_a_boarding()
    {
        var sim = NewGame();
        var w = sim.World;
        var st = Station(sim, Side.Player, Home(sim), "station-platform");
        new ScuttleStarbaseCommand(Side.Rival, st.Id).Apply(w, sim); // not theirs
        Assert.Contains(st, w.Starbases);
        new ScuttleStarbaseCommand(Side.Player, st.Id).Apply(w, sim);
        Assert.Empty(w.Starbases);
    }

    [Fact]
    public void A_fortress_doubles_the_garrison_a_landing_must_beat()
    {
        var sim = NewGame();
        var w = sim.World;
        var vela = RivalColony(sim);
        Station(sim, Side.Rival, vela, "station-fortress");
        sim.Step();
        Assert.Equal(2, vela.LandingFactor, 6);
        var doubled = Planets.GarrisonOf(vela);
        vela.LandingFactor = 1;
        Assert.Equal(doubled / 2, Planets.GarrisonOf(vela), 6);
    }

    [Fact]
    public void A_moon_under_an_enemy_planets_guns_cannot_be_landed_on_or_claimed()
    {
        var sim = NewGame();
        var w = sim.World;
        var moon = w.Bodies.Where(m => m.Kind == BodyKind.Moon && m.ParentId is int).OrderBy(m => m.OrbitRadius).First();
        var planet = w.FindBody(moon.ParentId!.Value)!;
        planet.Owner = Side.Rival;
        planet.PopK = Math.Max(planet.PopK, 10);
        Assert.Null(Starbases.CoveredBy(w, moon, Side.Player));
        Station(sim, Side.Rival, planet, "station-starbase");
        var covered = moon.OrbitRadius <= Starbases.D.SiloReachMm;
        Assert.Equal(covered, Starbases.CoveredBy(w, moon, Side.Player) == planet);
        if (covered) Assert.Contains("guns", Starbases.CoverProblem(w, moon, Side.Player));
        Assert.Null(Starbases.CoveredBy(w, moon, Side.Rival)); // never against its own side
    }

    [Fact]
    public void A_ring_covers_far_orbit_and_cuts_the_lift()
    {
        var sim = NewGame();
        var w = sim.World;
        var vela = RivalColony(sim);
        var ring = Station(sim, Side.Rival, vela, "station-ring");
        Assert.True(Starbases.LiftCut(ring) > 0);
        Assert.Equal(360, Starbases.YardM(w, ring), 6);
        var f = InOrbit(sim, Side.Player, vela, "gun-frigate");
        f.OrbitHeight = OrbitHeight.Far;
        var armour = w.ShipsOf(f).Sum(s => s.Armour);
        sim.StepMany(3);
        Assert.True(!w.Fleets.Contains(f) || w.ShipsOf(f).Sum(s => s.Armour) < armour);
    }

    [Fact]
    public void Orbital_mines_drift_thin_and_cost_upkeep()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Home(sim);
        var field = new Minefield { Id = w.NextId++, Side = Side.Player, BodyId = home.Id, Position = home.Position, Mines = 100, LaidHour = w.Hour - 48 };
        w.Minefields.Add(field);
        var industry = Economy.Of(w, Side.Player).Industry;
        Starbases.Update(sim);
        Assert.True(field.Mines < 100);
        Assert.True(Economy.Of(w, Side.Player).Industry < industry + 1e-9);
    }

    [Fact]
    public void The_rival_builds_the_station_line_at_home_and_replays_exactly()
    {
        var a = Simulation.NewGame(Scenarios.TestSystem, 11, GameLog.Memory(), options: GameOptions.VsRival(Difficulty.Normal));
        var b = Simulation.NewGame(Scenarios.TestSystem, 11, GameLog.Memory(), options: GameOptions.VsRival(Difficulty.Normal));
        a.StepMany(24 * 40);
        b.StepMany(24 * 40);
        Assert.Contains(a.World.Starbases, s => s.Side == Side.Rival);
        Assert.Equal(a.World.Starbases.Select(s => (s.Id, s.ClassId, s.BodyId)), b.World.Starbases.Select(s => (s.Id, s.ClassId, s.BodyId)));
        static string World(Simulation s) => System.Text.RegularExpressions.Regex.Replace(s.ToSave().ToJson(), "\"savedAtUtc\": \"[^\"]*\"", "");
        Assert.Equal(World(a), World(b));
    }
}
