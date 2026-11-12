using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Xunit;

namespace Lightlag.Sim.Tests;

public class PlanetTests
{
    private static Simulation NewGame(ulong seed = 8, GameOptions? options = null)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: options);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);
    private static Fleet Home(Simulation sim, Side side = Side.Player) => sim.World.Fleets.First(f => f.Side == side);

    /// <summary>Puts a finished building on a body's board at its best spot.</summary>
    private static Piece Put(Simulation sim, Body b, string kind)
    {
        Planets.EnsureStarted(sim.World);
        var data = Planets.BuildingKind(kind)!;
        var (x, y, turned) = Colonies.BestSpot(b, data)!.Value;
        var p = Colonies.AddPiece(sim.World, b, data, x, y, turned, built: true);
        Colonies.SyncBuildings(b);
        return p;
    }
    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    private static void PlaceInOrbit(Simulation sim, Fleet f, Body b)
    {
        f.Flight = null;
        f.Position = b.Position + new Vec2(Fleets.OrbitRadiusFor(b), 0);
        Fleets.EnterOrbit(f, b);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
    }

    /// <summary>A new fleet of these ships for a side, in orbit at a body (test setup).</summary>
    private static Fleet NewFleet(Simulation sim, Side side, Body at, params string[] classes)
    {
        var ships = classes.Select(c => Economy.NewShip(sim.World, side, c)).ToList();
        var f = Fleets.Create(sim.World, side, ships, at.Position);
        PlaceInOrbit(sim, f, at);
        return f;
    }

    [Fact]
    public void Homes_start_with_people_and_buildings_and_the_rest_is_empty()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        Assert.True(verdant.Home);
        Assert.Equal(Planets.D.HomeStartK, verdant.PopK);
        Assert.Contains(Planets.Farm, verdant.Buildings);
        Assert.Contains(Planets.Mine, verdant.Buildings);
        Assert.True(BodyNamed(sim, "rust").PopK > 0);
        Assert.All(sim.World.Bodies.Where(b => !b.Home), b => Assert.Equal(0, b.PopK));
        Assert.Equal(0, Planets.Cap(BodyNamed(sim, "tamber")));
        Assert.True(Planets.Settleable(BodyNamed(sim, "kesh")));
        Assert.Equal(BodyKind.Asteroid, BodyNamed(sim, "kesh").Kind);
    }

    [Fact]
    public void Fed_people_grow_and_are_content()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        Days(sim, 20);
        Assert.False(verdant.Hungry);
        Assert.True(verdant.PopK > Planets.D.HomeStartK);
        Assert.True(verdant.Happiness > Planets.D.HappinessStart);
    }

    [Fact]
    public void Without_farms_people_go_hungry_and_shrink()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        verdant.Pieces.RemoveAll(p => Planets.BuildingKind(p.Kind)?.Food > 0);
        Colonies.SyncBuildings(verdant);
        verdant.FoodStock = 0;
        Days(sim, 5);
        Assert.True(verdant.Hungry);
        Assert.True(verdant.PopK < Planets.D.HomeStartK);
        Assert.True(verdant.Happiness < Planets.D.HappinessStart);
    }

    [Fact]
    public void Settlers_ordered_for_an_empty_asteroid_settle_it_and_their_ship_lands_for_good()
    {
        var sim = NewGame();
        var kesh = BodyNamed(sim, "kesh");
        // Settler ships are not built from the ship list, only by a settle order.
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, Planets.SettlerClass));
        sim.Issue(new SettleCommand(Side.Player, kesh.Id));
        sim.ApplyPending();
        var order = Assert.Single(sim.World.Orders);
        Assert.Equal(kesh.Id, order.SettleBodyId);
        Assert.NotNull(Planets.CanSettle(sim.World, Side.Player, kesh.Id)); // already bound there
        for (var day = 0; day < 120 && kesh.Owner == null; day++) Days(sim, 1);
        Days(sim, 1);
        Assert.Equal(Side.Player, kesh.Owner);
        Assert.True(kesh.PopK > 5);
        Assert.Equal(1, kesh.OwnShare, 6);
        Assert.DoesNotContain(sim.World.Ships, s => s.Side == Side.Player && Planets.IsColonyShip(s));
        Assert.DoesNotContain(sim.World.Fleets, f => f.Side == Side.Player && f.PeopleRun != null);
    }

    [Fact]
    public void Settler_status_follows_the_ship_from_the_queue_to_landing()
    {
        var sim = NewGame();
        var kesh = BodyNamed(sim, "kesh");
        Assert.Null(Planets.SettlerStatus(sim.World, Side.Player, kesh.Id));
        sim.Issue(new SettleCommand(Side.Player, kesh.Id));
        sim.ApplyPending();
        var seen = new List<string>();
        for (var hour = 0; hour < 120 * 24 && kesh.Owner == null; hour++)
        {
            if (Planets.SettlerStatus(sim.World, Side.Player, kesh.Id) is string status && !seen.Contains(status.Split(':')[0])) seen.Add(status.Split(':')[0]);
            sim.Step();
        }
        Assert.Equal(Side.Player, kesh.Owner);
        Assert.Contains(seen, s => s.StartsWith("Settler ship being built", StringComparison.Ordinal));
        Assert.Contains(seen, s => s.Contains("lands in", StringComparison.Ordinal));
        Days(sim, 1);
        Assert.Null(Planets.SettlerStatus(sim.World, Side.Player, kesh.Id));
    }

    [Fact]
    public void Auto_top_up_orders_settlers_by_itself()
    {
        var sim = NewGame();
        var kesh = BodyNamed(sim, "kesh");
        kesh.Owner = Side.Player;
        kesh.PopK = 1;
        kesh.OwnShare = 1;
        sim.Issue(new SetAutoTopUpCommand(Side.Player, kesh.Id, true));
        sim.StepMany(6);
        Assert.Contains(sim.World.Orders, o => o.SettleBodyId == kesh.Id && o.Auto);
        Days(sim, 120);
        Assert.True(kesh.PopK > Planets.Cap(kesh) * 0.8, $"{kesh.PopK}");
    }

    [Fact]
    public void An_escort_fetches_the_settler_ship_flies_it_out_and_stays_in_orbit()
    {
        var sim = NewGame();
        var kesh = BodyNamed(sim, "kesh");
        var pale = BodyNamed(sim, "pale");
        var escort = Home(sim);
        PlaceInOrbit(sim, escort, pale); // away from the shipyard: it has to come and fetch the settlers
        sim.Issue(new SettleCommand(Side.Player, kesh.Id, escort.Id));
        for (var day = 0; day < 150 && kesh.Owner == null; day++) Days(sim, 1);
        Days(sim, 1);
        Assert.Equal(Side.Player, kesh.Owner);
        Assert.Contains(escort, sim.World.Fleets);
        Assert.True(Economy.InOrbitAt(escort, kesh));
        Assert.DoesNotContain(sim.World.Ships, s => s.Side == Side.Player && Planets.IsColonyShip(s));
        Assert.Single(sim.World.Fleets, f => f.Side == Side.Player);
    }

    [Fact]
    public void Settlers_on_a_taken_planet_raise_the_share_of_your_own_people()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        var pale = BodyNamed(sim, "pale");
        pale.Owner = Side.Player;
        pale.PopK = 5;
        pale.OwnShare = 0;
        sim.Issue(new SettleCommand(Side.Player, pale.Id));
        Days(sim, 30);
        Assert.DoesNotContain(sim.World.Fleets, f => f.Side == Side.Player && f.PeopleRun != null);
        Assert.InRange(pale.OwnShare, 0.3, 1);
        Assert.True(pale.PopK > 5);
    }

    [Fact]
    public void An_unhappy_blockaded_planet_surrenders_but_not_to_a_side_that_bombed_its_people()
    {
        var sim = NewGame();
        var vela = BodyNamed(sim, "vela");
        vela.Owner = Side.Rival;
        vela.PopK = 20;
        vela.FoodStock = 0;
        vela.Happiness = Planets.D.SurrenderBelow + 2;
        var fleet = Home(sim);
        PlaceInOrbit(sim, fleet, vela);
        Assert.True(Economy.Blockaded(sim.World, vela));
        Days(sim, 5);
        Assert.Equal(Side.Player, vela.Owner);
        Assert.Equal(0, vela.OwnShare);

        var sim2 = NewGame();
        var vela2 = BodyNamed(sim2, "vela");
        vela2.Owner = Side.Rival;
        vela2.PopK = 20;
        vela2.Happiness = Planets.D.SurrenderBelow + 2;
        vela2.Grudge = Side.Player;
        PlaceInOrbit(sim2, Home(sim2), vela2);
        Days(sim2, 10);
        Assert.Equal(Side.Rival, vela2.Owner);
        Assert.False(vela2.Owner == null);
        // Blockaded, it makes nothing for its holder.
        Assert.Equal(0, Planets.IndustryPerDay(sim2.World, vela2));
    }

    [Fact]
    public void A_peopled_planet_cannot_be_claimed_by_orbit_alone()
    {
        var sim = NewGame();
        var vela = BodyNamed(sim, "vela");
        vela.Owner = Side.Rival;
        vela.PopK = 20;
        vela.Happiness = 90;
        PlaceInOrbit(sim, Home(sim), vela);
        Days(sim, GameData.Economy.ClaimHeldDays + 2);
        Assert.Equal(Side.Rival, vela.Owner);
    }

    [Fact]
    public void Bombing_people_leaves_a_grudge_and_bombing_farms_knocks_them_out()
    {
        var sim = NewGame();
        var vela = BodyNamed(sim, "vela");
        vela.Owner = Side.Rival;
        vela.PopK = 30;
        vela.Happiness = 90;
        Put(sim, vela, Planets.Farm);
        var fleet = Home(sim);
        PlaceInOrbit(sim, fleet, vela);
        sim.Issue(new SetBombingCommand(fleet.Id, BombTarget.Farms));
        Days(sim, 3);
        Assert.DoesNotContain(Planets.Farm, vela.Buildings);
        Assert.Null(vela.Grudge);
        Assert.Equal(BombTarget.None, fleet.Bombing);
        sim.Issue(new SetBombingCommand(fleet.Id, BombTarget.People));
        Days(sim, 1);
        Assert.Equal(Side.Player, vela.Grudge);
        Assert.True(vela.PopK < 30);
    }

    [Fact]
    public void Troops_landed_from_low_orbit_take_a_planet()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        var vela = BodyNamed(sim, "vela");
        vela.Owner = Side.Rival;
        vela.PopK = 40;
        vela.Happiness = 90;
        vela.Grudge = Side.Player;
        var troops = NewFleet(sim, Side.Player, verdant, "troop-ship", "troop-ship");
        Days(sim, 1);
        Assert.Equal(2 * GameData.ShipClass("troop-ship").Cargo, Planets.TroopsAboard(sim.World, troops), 6);
        PlaceInOrbit(sim, troops, vela);
        Assert.NotNull(Planets.CanLand(sim.World, Home(sim), vela)); // the warships carry no troops
        sim.Issue(new SetOrbitHeightCommand(troops.Id, OrbitHeight.High));
        sim.StepMany(2); // under light lag the order takes a few minutes to get there
        Assert.NotNull(Planets.CanLand(sim.World, troops, vela));
        sim.Issue(new SetOrbitHeightCommand(troops.Id, OrbitHeight.Low));
        sim.Issue(new LandTroopsCommand(troops.Id, vela.Id));
        sim.StepMany(2);
        Assert.Equal(Side.Player, vela.LandingSide);
        Days(sim, 8);
        Assert.Equal(Side.Player, vela.Owner);
        Assert.Null(vela.LandingSide);
    }

    [Fact]
    public void Planet_guns_hit_ships_in_low_orbit_but_not_high()
    {
        var sim = NewGame();
        var vela = BodyNamed(sim, "vela");
        vela.Owner = Side.Rival;
        vela.PopK = 20;
        vela.Happiness = 90;
        Put(sim, vela, Planets.Guns);
        var fleet = Home(sim);
        sim.Issue(new SetOrbitHeightCommand(fleet.Id, OrbitHeight.High));
        sim.Step();
        PlaceInOrbit(sim, fleet, vela);
        var armour = sim.World.ShipsOf(fleet).Sum(s => s.Armour);
        Days(sim, 1);
        Assert.Equal(armour, sim.World.ShipsOf(fleet).Sum(s => s.Armour), 6);
        sim.Issue(new SetOrbitHeightCommand(fleet.Id, OrbitHeight.Low));
        Days(sim, 1);
        Assert.True(sim.World.Ships.Where(s => s.Side == Side.Player).Sum(s => s.Armour) < armour);
    }

    [Fact]
    public void Troops_can_board_a_ship_on_a_blockaded_slipway()
    {
        var sim = NewGame();
        var rust = BodyNamed(sim, "rust");
        sim.Issue(new QueueOrderCommand(Side.Rival, OrderKind.Ship, "gun-frigate"));
        sim.Step();
        Assert.True(sim.World.Orders.Single(o => o.Side == Side.Rival).Paid);
        var rival = Home(sim, Side.Rival);
        foreach (var ship in sim.World.ShipsOf(rival).ToList()) Combat.Destroy(sim, ship);
        Economy.Of(sim.World, Side.Rival).FlagshipId = null;
        var fleet = Home(sim);
        var troops = NewFleet(sim, Side.Player, BodyNamed(sim, "verdant"), "troop-ship");
        Days(sim, 1);
        sim.Issue(new MergeFleetsCommand(fleet.Id, troops.Id));
        sim.Step();
        PlaceInOrbit(sim, fleet, rust);
        var ships = fleet.ShipIds.Count;
        sim.Issue(new BoardSlipwayCommand(fleet.Id));
        sim.Step();
        Assert.Equal(ships + 1, fleet.ShipIds.Count);
        Assert.DoesNotContain(sim.World.Orders, o => o.Side == Side.Rival);
    }

    [Fact]
    public void Buildings_need_people_room_and_industry()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        var vela = BodyNamed(sim, "vela");
        Assert.Null(Planets.CanBuild(sim.World, Side.Player, verdant.Id, Planets.Mine));
        Assert.NotNull(Planets.CanBuild(sim.World, Side.Player, vela.Id, Planets.Mine));
        Assert.NotNull(Planets.CanBuild(sim.World, Side.Player, BodyNamed(sim, "rust").Id, Planets.Mine));
        var income = Economy.IncomePerDay(sim.World, Side.Player);
        sim.Issue(new BuildOnPlanetCommand(Side.Player, verdant.Id, Planets.Mine));
        Days(sim, Planets.BuildingKind(Planets.Mine)!.Days + 0.1);
        Assert.Equal(2, Planets.Count(verdant, Planets.Mine));
        Assert.True(Economy.IncomePerDay(sim.World, Side.Player) > income + 2.5);
    }

    [Fact]
    public void Random_starts_and_spread_people_are_game_start_options()
    {
        var homes = new HashSet<string>();
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var sim = NewGame(seed, new GameOptions(RandomStarts: true, StartPopulation: true));
            var mine = Economy.ShipyardOf(sim.World, Side.Player)!;
            var theirs = Economy.ShipyardOf(sim.World, Side.Rival)!;
            Assert.NotEqual(mine.Id, theirs.Id);
            Assert.Contains(mine.Type, Scenarios.HomeTypes);
            Assert.True(mine.Home && mine.PopK > 0);
            homes.Add(mine.Key);
            Assert.Contains(sim.World.Bodies, b => !b.Home && b.Owner == null && b.PopK > 0);
            Days(sim, 2);
        }
        Assert.True(homes.Count > 1);
    }

    [Fact]
    public void With_supply_lines_off_ships_keep_full_ammunition_and_food()
    {
        var sim = NewGame(options: new GameOptions(SupplyLines: false, Happiness: false));
        var fleet = Home(sim);
        PlaceInOrbit(sim, fleet, BodyNamed(sim, "vela"));
        Days(sim, 5);
        Assert.All(sim.World.ShipsOf(fleet), s => Assert.Equal(SupplyLines.MaxFood(s), s.Food));
        Assert.Equal(Planets.D.HappinessStart, BodyNamed(sim, "verdant").Happiness);
    }

    [Fact]
    public void A_game_with_people_and_buildings_replays_exactly()
    {
        var sim = NewGame(31);
        var verdant = BodyNamed(sim, "verdant");
        var warships = Home(sim);
        var kesh = BodyNamed(sim, "kesh");
        sim.Issue(new SettleCommand(Side.Player, kesh.Id));
        sim.Issue(new BuildOnPlanetCommand(Side.Player, verdant.Id, Planets.Farm));
        Days(sim, GameData.ShipClass("colony-ship").Cost / GameData.Economy.BuildIndustryPerDay + 0.2);
        // The new settler ship forms a fleet of its own rather than joining the warships.
        var colony = sim.World.Fleets.Single(f => f.Side == Side.Player && f.Id != warships.Id);
        Assert.All(sim.World.ShipsOf(colony), s => Assert.True(Planets.IsColonyShip(s)));
        sim.Issue(new SetAutoTopUpCommand(Side.Player, verdant.Id, false));
        Days(sim, 75);
        Assert.Equal(Side.Player, kesh.Owner);
        Assert.True(kesh.PopK > 0);
        var replay = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(replay.Exact, replay.Message);
    }

    [Fact]
    public void A_trip_from_a_planet_to_its_own_moon_never_takes_months()
    {
        var sim = NewGame();
        var pale = BodyNamed(sim, "pale");
        var f = Home(sim);
        for (var h = 0; h < 48; h += 3)
        {
            var plan = FlightPlanner.Plan(sim.World, f, new FlightRequest(pale.Id, pale.Position, 0.7, ArrivalStyle.Brake, HoldStyle.Orbit, sim.World.Tick));
            Assert.True(plan.Ok);
            Assert.InRange(plan.Flight!.ArriveHour - sim.World.Hour, 1, 5 * 24);
            Assert.Null(Avoidance.FirstHit(sim.World, plan.Flight));
            sim.StepMany(3);
        }
    }

    [Fact]
    public void A_save_from_before_people_gets_its_homes_peopled_on_load()
    {
        var sim = NewGame();
        // Strip the save back to how an older build wrote it: no people, no home marks, no start flag.
        var json = sim.ToSave().ToJson();
        var save = Lightlag.Sim.Save.SaveGame.FromJson(json);
        save.World.PeopleStarted = false;
        foreach (var b in save.World.Bodies) { b.PopK = 0; b.Home = false; b.Buildings.Clear(); }
        var loaded = Simulation.FromSave(Lightlag.Sim.Save.SaveGame.FromJson(save.ToJson()), GameLog.Memory());
        loaded.StrictRules = true;
        loaded.RuleCheckEveryTicks = 1;
        loaded.Step();
        var verdant = loaded.World.Bodies.Single(b => b.Key == "verdant");
        Assert.True(verdant.Home);
        Assert.True(verdant.PopK > 0);
        Assert.Contains(Planets.Farm, verdant.Buildings);
    }
}
