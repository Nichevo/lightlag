using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Planet boards, power, goods, refits and transport (see Colonies).</summary>
public class ColonyTests
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
    private static void Days(Simulation sim, double days) => Hours(sim, days * 24);

    /// <summary>Hands an empty world to the player with some people on it (test setup).</summary>
    private static Body Colony(Simulation sim, string key, double popK)
    {
        var b = BodyNamed(sim, key);
        b.Owner = Side.Player;
        b.PopK = popK;
        b.OwnShare = 1;
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

    [Fact]
    public void Boards_come_from_the_seed_and_homes_start_laid_out()
    {
        var a = NewGame(5);
        var b = NewGame(5);
        foreach (var body in a.World.Bodies)
            Assert.Equal(body.Board?.Tiles, b.World.Bodies.Single(x => x.Id == body.Id).Board?.Tiles);
        var verdant = BodyNamed(a, "verdant");
        Assert.Equal((10, 6), (verdant.Board!.W, verdant.Board.H));
        Assert.Equal(Planets.D.HomeBuildings!.Count, Colonies.Built(verdant).Count());
        Assert.Contains(verdant.Board.Deposits, d => d.Mineral == "iron");
        Assert.True(Colonies.Held(verdant, "spares") > 0);
        var kesh = BodyNamed(a, "kesh");
        Assert.Equal((4, 3), (kesh.Board!.W, kesh.Board.H));
        Assert.NotEmpty(kesh.Board.Deposits);
        Assert.Null(BodyNamed(a, "tamber").Board); // a gas giant has no ground
    }

    [Fact]
    public void Pieces_keep_to_the_board_their_ground_and_each_other()
    {
        var sim = NewGame();
        var kesh = Colony(sim, "kesh", 4);
        var mine = Planets.BuildingKind(Planets.Mine)!;
        var deposit = kesh.Board!.Deposits[0];
        var bare = Enumerable.Range(0, kesh.Board.W * kesh.Board.H).Select(i => (X: i % kesh.Board.W, Y: i / kesh.Board.W))
            .First(t => kesh.Board.At(t.X, t.Y) == 'l' && kesh.Board.DepositAt(t.X, t.Y) == null);
        Assert.Equal("Must sit on a deposit", Colonies.FitProblem(kesh, mine, bare.X, bare.Y, false));
        Assert.Null(Colonies.FitProblem(kesh, mine, deposit.X, deposit.Y, false));
        Assert.NotNull(Colonies.FitProblem(kesh, Planets.BuildingKind("workshop")!, kesh.Board.W - 1, 0, false)); // 2 wide at the edge
        sim.Issue(new BuildOnPlanetCommand(Side.Player, kesh.Id, Planets.Mine, deposit.X, deposit.Y));
        sim.ApplyPending();
        Assert.Equal("Something is already there", Colonies.FitProblem(kesh, mine, deposit.X, deposit.Y, false));
        Days(sim, mine.Days + 0.5);
        var built = Assert.Single(Colonies.Built(kesh));
        Assert.Equal(Planets.Mine, built.Kind);
        Assert.Contains(Planets.Mine, kesh.Buildings);
        Assert.True(Colonies.Held(kesh, deposit.Mineral) > 0); // it digs what lies under it
    }

    [Fact]
    public void Buildings_queue_behind_each_other_and_wait_for_their_Industry()
    {
        var sim = NewGame();
        var verdant = sim.World.Bodies.First(b => b.Key == "verdant");
        var me = Economy.Of(sim.World, Side.Player);
        var mine = Planets.BuildingKind(Planets.Mine)!;
        var farm = Planets.BuildingKind(Planets.Farm)!;
        me.Industry = mine.Cost + 1; // enough for the first only
        sim.Issue(new BuildOnPlanetCommand(Side.Player, verdant.Id, Planets.Mine, Queue: true));
        sim.Issue(new BuildOnPlanetCommand(Side.Player, verdant.Id, Planets.Farm, Queue: true));
        sim.ApplyPending(); // no step: home orders take effect at once, even while paused
        var first = verdant.Pieces.Single(p => !p.Built && p.Kind == Planets.Mine);
        var second = verdant.Pieces.Single(p => !p.Built && p.Kind == Planets.Farm);
        Assert.False(first.Waiting);
        Assert.True(second.Waiting);
        Assert.Equal(1, me.Industry, 6); // the queued farm is not paid for yet
        Assert.Equal("queued behind the building under way", Colonies.QueuedWhy(sim.World, verdant, second));
        Days(sim, mine.Days + 0.5);
        Assert.True(first.Built);
        Assert.True(second.Waiting || me.Industry >= 0); // it starts once the Industry is there
        Days(sim, farm.Cost / Math.Max(0.1, Economy.IncomePerDay(sim.World, Side.Player)) + farm.Days + 2);
        Assert.True(second.Built);
        Assert.Empty(sim.CheckRules());
    }

    [Fact]
    public void A_queued_building_can_be_taken_out_of_the_queue_for_nothing()
    {
        var sim = NewGame();
        var verdant = sim.World.Bodies.First(b => b.Key == "verdant");
        var me = Economy.Of(sim.World, Side.Player);
        me.Industry = 0;
        sim.Issue(new BuildOnPlanetCommand(Side.Player, verdant.Id, Planets.Farm, Queue: true));
        sim.ApplyPending();
        var queued = verdant.Pieces.Single(p => p.Waiting);
        Assert.StartsWith("waiting for", Colonies.QueuedWhy(sim.World, verdant, queued));
        sim.Issue(new DemolishCommand(Side.Player, verdant.Id, Planets.Farm, queued.Id));
        sim.ApplyPending();
        Assert.DoesNotContain(queued, verdant.Pieces);
        Assert.Equal(0, me.Industry, 6);
    }

    [Fact]
    public void Short_of_power_the_lowest_priority_building_stops_first()
    {
        var sim = NewGame();
        var kesh = Colony(sim, "kesh", 8);
        var mine = Put(sim, kesh, Planets.Mine);
        var guns = Put(sim, kesh, Planets.Guns);
        Hours(sim, 1);
        // Base power 6 runs one 4-power building: in Balanced, defence comes before industry.
        // The 2 left over run the mine on a brownout, at half its draw and half its output.
        Assert.True(guns.Powered);
        Assert.Equal(1, guns.PowerShare);
        Assert.Equal(0.5, mine.PowerShare, 6);
        var full = Colonies.Output(sim.World, kesh, mine) / mine.PowerShare;
        Assert.Equal(full * 0.5, Colonies.Output(sim.World, kesh, mine), 6);
        sim.Issue(new SetPriorityCommand(Side.Player, kesh.Id, "war-output"));
        Hours(sim, 1);
        Assert.Equal(1, mine.PowerShare);
        Assert.Equal(0.5, guns.PowerShare, 6);
    }

    [Fact]
    public void A_buildings_own_power_setting_beats_the_area_order_and_off_frees_its_power()
    {
        var sim = NewGame();
        var kesh = Colony(sim, "kesh", 8);
        var mine = Put(sim, kesh, Planets.Mine);
        var guns = Put(sim, kesh, Planets.Guns);
        Hours(sim, 1);
        Assert.Equal(1, guns.PowerShare);
        Assert.True(mine.PowerShare < 1);
        Assert.Contains("Brownout: Mine at 50", Colonies.PowerNote(sim.World, kesh));
        // High on the mine puts it ahead of the guns (works while paused: a home order applies at once).
        sim.Issue(new SetPowerUseCommand(Side.Player, kesh.Id, mine.Id, PowerUse.High));
        sim.ApplyPending();
        Assert.Equal(PowerUse.High, mine.PowerUse);
        Hours(sim, 1);
        Assert.Equal(1, mine.PowerShare);
        Assert.True(guns.PowerShare < 1);
        // Off on the mine: it takes no power or staff, so the guns run and nothing is short.
        sim.Issue(new SetPowerUseCommand(Side.Player, kesh.Id, mine.Id, PowerUse.Off));
        Hours(sim, 1);
        Assert.False(mine.Powered);
        Assert.Equal(0, mine.Staff);
        Assert.True(guns.Powered);
        Assert.Null(Colonies.PowerNote(sim.World, kesh));
        Assert.Empty(sim.CheckRules());
    }

    [Fact]
    public void Without_spare_parts_buildings_wear_and_work_less()
    {
        var sim = NewGame();
        var kesh = Colony(sim, "kesh", 8);
        var guns = Put(sim, kesh, Planets.Guns);
        kesh.Stock["spares"] = 0;
        Days(sim, 10);
        Assert.True(guns.Condition < 1);
        var worn = Colonies.Output(sim.World, kesh, guns);
        kesh.Stock["spares"] = 20;
        Days(sim, 5);
        Assert.True(Colonies.Output(sim.World, kesh, guns) > worn);
    }

    [Fact]
    public void Research_refits_buildings_for_a_few_days_then_they_work_harder()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        var mine = Colonies.Built(verdant).First(p => p.Kind == Planets.Mine);
        var before = Colonies.Output(sim.World, verdant, mine);
        Economy.Of(sim.World, Side.Player).Researched.Add("automation");
        Colonies.OnResearched(sim, Side.Player, "automation");
        Assert.True(mine.RefitHoursLeft > 0);
        Hours(sim, 1);
        Assert.True(Colonies.Output(sim.World, verdant, mine) < before);
        Days(sim, Colonies.C.RefitDays + 1);
        Assert.Equal(1, mine.Level);
        Assert.True(Colonies.Output(sim.World, verdant, mine) > before);
    }

    [Fact]
    public void A_fixed_run_carries_rare_earths_home()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        var kesh = Colony(sim, "kesh", 4);
        kesh.Stock["rare-earths"] = 40;
        var freighter = Economy.NewShip(sim.World, Side.Player, "freighter");
        Fleets.Create(sim.World, Side.Player, new List<Ship> { freighter }, verdant.Position + new Vec2(Fleets.OrbitRadiusFor(verdant), 0));
        sim.Issue(new FixedRunCommand(Side.Player, kesh.Id, verdant.Id, "rare-earths", 20));
        for (var day = 0; day < 120 && Colonies.Held(verdant, "rare-earths") < 1; day++) Days(sim, 1);
        Assert.True(Colonies.Held(verdant, "rare-earths") >= 1);
        Assert.True(Colonies.Held(kesh, "rare-earths") < 40);
    }

    [Fact]
    public void A_freighter_shot_down_loses_its_cargo()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        var kesh = Colony(sim, "kesh", 4);
        kesh.Stock["rare-earths"] = 40;
        var freighter = Economy.NewShip(sim.World, Side.Player, "freighter");
        var f = Fleets.Create(sim.World, Side.Player, new List<Ship> { freighter }, kesh.Position + new Vec2(Fleets.OrbitRadiusFor(kesh), 0));
        Fleets.EnterOrbit(f, kesh);
        freighter.Position = f.Position + freighter.FormationOffset;
        sim.Issue(new FixedRunCommand(Side.Player, kesh.Id, verdant.Id, "rare-earths", 20));
        for (var hour = 0; hour < 24 * 20 && !sim.World.Shipments.Any(s => s.Loaded); hour++) Hours(sim, 1);
        Assert.Contains(sim.World.Shipments, s => s.Loaded);
        Combat.Destroy(sim, freighter);
        Hours(sim, 1);
        Assert.Empty(sim.World.Shipments);
        Assert.Equal(1, Economy.Of(sim.World, Side.Player).CargoLost);
        Assert.True(Colonies.Held(verdant, "rare-earths") < 1);
        Assert.Contains(sim.World.Light.Notes, n => n.Text.Contains("lost with its cargo"));
    }

    [Fact]
    public void New_commands_round_trip_through_a_replay()
    {
        var cmds = new GameCommand[]
        {
            new SetPriorityCommand(Side.Player, 3, "custom", new List<string> { "food", "power" }),
            new SetSiegeStockCommand(Side.Player, 3, 60),
            new SetAutoTransportCommand(Side.Player, false),
            new FixedRunCommand(Side.Player, 3, 4, "iron", 20, 7),
        };
        foreach (var c in cmds)
        {
            var json = System.Text.Json.JsonSerializer.Serialize<GameCommand>(c);
            Assert.Equal(c.GetType(), System.Text.Json.JsonSerializer.Deserialize<GameCommand>(json)!.GetType());
        }
    }

    [Fact]
    public void A_damaged_fleet_at_the_yard_adds_its_repair_bill_to_the_yard_wants()
    {
        var sim = NewGame();
        sim.Step();
        var w = sim.World;
        var home = Economy.HomeOf(w, Side.Player)!;
        var fleet = w.Fleets.First(f => f.Side == Side.Player && f.ShipIds.Count > 0 && f.OrbitBodyId == home.Id && f.Flight == null);
        var before = Colonies.YardBills(w, home, Side.Player).GetValueOrDefault("alloys");
        var ship = w.ShipsOf(fleet).First();
        ship.Armour = Tech.MaxArmour(w, ship) * 0.5;
        var after = Colonies.YardBills(w, home, Side.Player).GetValueOrDefault("alloys");
        Assert.True(after > before, $"alloys bill {before} -> {after}");
        Colonies.UpdateWants(w);
        Assert.True(Colonies.Want(home, "alloys") >= after - 1e-6);
    }
}
