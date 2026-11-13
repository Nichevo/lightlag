using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Xunit;

namespace Lightlag.Sim.Tests;

public class EconomyTests
{
    private static Simulation NewGame(ulong seed = 8, bool classic = true)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: new GameOptions(Budget: false)); // one pool: these tests predate the Industry budget (see BudgetTests)
        if (classic) Starts.Classic(sim);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet FleetOf(Simulation sim, Side side) => sim.World.Fleets.First(f => f.Side == side);
    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);
    private static SideState Player(Simulation sim) => Economy.Of(sim.World, Side.Player);
    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    /// <summary>Puts a fleet straight into orbit at a body (test setup, not gameplay).</summary>
    private static void PlaceInOrbit(Simulation sim, Fleet f, Body b)
    {
        f.Flight = null;
        f.Position = b.Position + new Vec2(Fleets.OrbitRadiusFor(b), 0);
        Fleets.EnterOrbit(f, b);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
    }

    [Fact]
    public void Each_side_starts_with_a_shipyard_and_industry()
    {
        var sim = NewGame();
        Assert.True(sim.World.Loop);
        Assert.Equal(Side.Player, Economy.ShipyardOf(sim.World, Side.Player)?.Owner);
        Assert.Equal("verdant", Economy.ShipyardOf(sim.World, Side.Player)?.Key);
        Assert.Equal("rust", Economy.ShipyardOf(sim.World, Side.Rival)?.Key);
        Assert.Equal(GameData.Economy.StartIndustry, Player(sim).Industry);
        Assert.Equal(15, Economy.Claimable(sim.World).Count());
    }

    [Fact]
    public void Held_planets_pay_industry_every_day()
    {
        var sim = NewGame();
        var start = Player(sim).Industry;
        double expected = start;
        for (var h = 0; h < 10 * GameTime.TicksPerDay; h++)
        {
            sim.Step();
            expected += (Economy.IncomePerDay(sim.World, Side.Player) - SupplyLines.UpkeepPerDay(sim.World, Side.Player)) / 24;
        }
        Assert.Equal(expected, Player(sim).Industry, 6);
        // The home's base industry plus its mine, with full staff.
        Assert.True(Economy.IncomePerDay(sim.World, Side.Player) >= BodyNamed(sim, "verdant").Industry + 3);
    }

    [Fact]
    public void The_shipyard_builds_a_ship_that_joins_the_fleet_at_home()
    {
        var sim = NewGame();
        var home = FleetOf(sim, Side.Player);
        var ships = home.ShipIds.Count;
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, "gun-frigate"));
        sim.Step();
        var frigate = GameData.ShipClass("gun-frigate");
        Assert.Equal(GameData.Economy.StartIndustry - frigate.Cost + (Economy.IncomePerDay(sim.World, Side.Player) - SupplyLines.UpkeepPerDay(sim.World, Side.Player)) / 24, Player(sim).Industry, 6);
        Days(sim, frigate.Cost / GameData.Economy.BuildIndustryPerDay + 0.1);
        Assert.Equal(ships + 1, home.ShipIds.Count);
        Assert.Empty(sim.World.Orders);
        Assert.Contains(sim.World.ShipsOf(home), s => s.HullNumber == 3 && s.Code == "FF1A-03");
    }

    [Fact]
    public void A_ship_built_for_a_fleet_far_away_flies_out_and_joins_it()
    {
        var sim = NewGame();
        var fleet = FleetOf(sim, Side.Player);
        var ships = fleet.ShipIds.Count;
        PlaceInOrbit(sim, fleet, BodyNamed(sim, "vela"));
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, "gun-frigate", fleet.Id));
        var frigate = GameData.ShipClass("gun-frigate");
        Days(sim, frigate.Cost / GameData.Economy.BuildIndustryPerDay + 0.1);
        // Launched as its own fleet, on its way to Vela.
        var player = sim.World.Fleets.Where(f => f.Side == Side.Player).ToList();
        Assert.Equal(2, player.Count);
        var reinforcement = player.Single(f => f.Id != fleet.Id);
        Assert.Equal(fleet.Id, reinforcement.JoinFleetId);
        Assert.NotNull(reinforcement.Flight);
        Days(sim, reinforcement.Flight!.ArriveHour / 24 - sim.World.Hour / 24 + 1);
        Assert.Single(sim.World.Fleets, f => f.Side == Side.Player);
        Assert.Equal(ships + 1, fleet.ShipIds.Count);
    }

    [Fact]
    public void The_fleet_a_ship_joins_can_be_changed_while_it_is_built()
    {
        var sim = NewGame(classic: false);
        var fleet = FleetOf(sim, Side.Player);
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, "gun-frigate"));
        sim.Step();
        var order = sim.World.Orders.Single();
        Assert.Null(order.JoinFleetId);
        sim.Issue(new SetOrderFleetCommand(Side.Player, order.Id, fleet.Id));
        sim.Issue(new SetOrderFleetCommand(Side.Player, order.Id, FleetOf(sim, Side.Rival).Id)); // not ours: refused
        sim.Step();
        Assert.Equal(fleet.Id, order.JoinFleetId);
        // The fleet is at the shipyard, so the new ship simply joins it there.
        Days(sim, GameData.ShipClass("gun-frigate").Cost / GameData.Economy.BuildIndustryPerDay + 0.1);
        Assert.Single(sim.World.Fleets, f => f.Side == Side.Player && !sim.World.ShipsOf(f).All(Colonies.IsHauler)); // and the home transport
        var replay = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(replay.Exact, replay.Message);
    }

    [Fact]
    public void A_ship_waits_until_there_is_enough_industry()
    {
        var sim = NewGame();
        Player(sim).Industry = 10;
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, "missile-destroyer"));
        Days(sim, 2);
        Assert.False(sim.World.Orders.Single().Paid);
        Player(sim).Industry = 200;
        sim.Step();
        Assert.True(sim.World.Orders.Single().Paid);
    }

    [Fact]
    public void Cancelling_a_started_order_gives_the_industry_back()
    {
        var sim = NewGame();
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, "gun-frigate"));
        sim.Step();
        var before = Player(sim).Industry;
        sim.Issue(new CancelOrderCommand(Side.Player, sim.World.Orders.Single().Id));
        sim.Step();
        Assert.Empty(sim.World.Orders);
        Assert.Equal(before + GameData.ShipClass("gun-frigate").Cost + (Economy.IncomePerDay(sim.World, Side.Player) - SupplyLines.UpkeepPerDay(sim.World, Side.Player)) / 24, Player(sim).Industry, 6);
    }

    [Fact]
    public void Research_finishes_and_changes_the_numbers()
    {
        var sim = NewGame();
        var ship = sim.World.ShipsOf(FleetOf(sim, Side.Player)).First();
        var before = Tech.SuppliesPerDeltaV(sim.World, ship);
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Research, Tech.EfficientDrives));
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Research, Tech.EfficientDrives)); // a second one is refused
        Days(sim, Research.BaseHours(sim.World, Side.Player, Tech.EfficientDrives) / 24 + 0.1);
        Assert.Contains(Tech.EfficientDrives, Player(sim).Researched);
        Assert.Equal(before * GameData.Research(Tech.EfficientDrives).Factor, Tech.SuppliesPerDeltaV(sim.World, ship), 9);
        Assert.Null(Economy.CanQueue(sim.World, Side.Player, OrderKind.Research, Tech.QuietDrives));
        Assert.NotNull(Economy.CanQueue(sim.World, Side.Player, OrderKind.Research, Tech.EfficientDrives));
    }

    [Fact]
    public void Missile_brain_research_upgrades_the_missiles()
    {
        var sim = NewGame();
        Player(sim).Industry = 1000;
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Research, Tech.MissileBrain2));
        Days(sim, Research.BaseHours(sim.World, Side.Player, Tech.MissileBrain2) / 24 + 0.1);
        Assert.Equal(2, sim.World.MissileBrain[(int)Side.Player]);
        Assert.Equal(1, sim.World.MissileBrain[(int)Side.Rival]);
    }

    [Fact]
    public void Holding_orbit_for_ten_days_claims_a_planet()
    {
        var sim = NewGame();
        var vela = BodyNamed(sim, "vela");
        PlaceInOrbit(sim, FleetOf(sim, Side.Player), vela);
        Days(sim, GameData.Economy.ClaimDays - 1);
        Assert.Null(vela.Owner);
        Assert.Equal(Side.Player, vela.ClaimSide);
        Days(sim, 1.1);
        Assert.Equal(Side.Player, vela.Owner);
        var income = Economy.IncomePerDay(sim.World, Side.Player);
        // An empty planet you hold makes part of its industry until settlers arrive.
        Assert.Equal(Planets.IndustryPerDay(sim.World, BodyNamed(sim, "verdant")) + vela.Industry * Planets.D.UnsettledShare, income, 9);
    }

    [Fact]
    public void Taking_a_planet_the_other_side_holds_takes_longer()
    {
        var sim = NewGame();
        var vela = BodyNamed(sim, "vela");
        vela.Owner = Side.Rival;
        PlaceInOrbit(sim, FleetOf(sim, Side.Player), vela);
        Days(sim, GameData.Economy.ClaimDays + 1);
        Assert.Equal(Side.Rival, vela.Owner);
        Assert.Equal(Side.Player, vela.ClaimSide);
        Days(sim, GameData.Economy.ClaimHeldDays - GameData.Economy.ClaimDays);
        Assert.Equal(Side.Player, vela.Owner);
    }

    [Fact]
    public void An_enemy_in_orbit_stops_the_claim()
    {
        var sim = NewGame();
        var vela = BodyNamed(sim, "vela");
        PlaceInOrbit(sim, FleetOf(sim, Side.Player), vela);
        var rival = FleetOf(sim, Side.Rival);
        rival.Stance = Stance.Hold;
        FleetOf(sim, Side.Player).Stance = Stance.Hold;
        PlaceInOrbit(sim, rival, vela);
        rival.Position = vela.Position - new Vec2(Fleets.OrbitRadiusFor(vela), 0);
        Fleets.EnterOrbit(rival, vela);
        // Strip both fleets' weapons so they share the orbit without a fight.
        foreach (var s in sim.World.Ships) { s.Missiles = 0; s.Disabled = Module.Railguns; }
        Days(sim, GameData.Economy.ClaimDays + 2);
        Assert.Null(vela.Owner);
        Assert.Null(vela.ClaimSide);
    }

    [Fact]
    public void An_undefended_shipyard_falls_and_its_side_loses()
    {
        var sim = NewGame();
        var rust = BodyNamed(sim, "rust");
        var rival = FleetOf(sim, Side.Rival);
        // Send the defenders far away and park the player's fleet at the rival shipyard.
        rival.Flight = null;
        Fleets.EnterDrift(rival);
        rival.Position = new Vec2(0, 30 * Physics.MmPerAu);
        PlaceInOrbit(sim, FleetOf(sim, Side.Player), rust);
        rust.ShipyardArmour = 100;
        Days(sim, 3);
        Assert.False(rust.Shipyard);
        Assert.Equal(Side.Player, sim.World.Winner);
        Assert.Contains("rival shipyard", sim.World.WinReason);
        var income = Player(sim).Industry;
        Days(sim, 2);
        Assert.Equal(income, Player(sim).Industry); // the game is over: nothing more happens to the score
    }

    [Fact]
    public void Holding_most_of_the_system_for_thirty_days_wins()
    {
        var sim = NewGame();
        var w = sim.World;
        foreach (var b in Economy.Claimable(w).Where(b => b.Owner == null)) b.Owner = Side.Player;
        BodyNamed(sim, "rust").PopK = 40; // enough to staff its farms and its mines
        foreach (var ship in w.Ships.Where(s => s.Side == Side.Rival).ToList()) Combat.Destroy(sim, ship);
        Economy.Of(w, Side.Rival).FlagshipId = null;
        var share = Planets.Control(w, Side.Player).Share;
        var c = Planets.Control(w, Side.Player);
        Assert.True(share >= GameData.Economy.Planets!.TipShare && share <= GameData.Economy.Planets.WinShare, $"{c}");
        sim.Step();
        Assert.True(Planets.Tipped(w, Side.Player), $"{Planets.Control(w, Side.Player)} before {c}");
        Days(sim, Planets.D.WinAfterDays - 1);
        Assert.Null(w.Winner);
        Days(sim, 1.1);
        Assert.Equal(Side.Player, w.Winner);
    }

    [Fact]
    public void The_sandbox_has_no_economy()
    {
        var sim = Starts.Classic(Simulation.NewGame(Scenarios.TestSystem, 3, GameLog.Memory(), sandbox: new SandboxSetup()));
        Assert.False(sim.World.Loop);
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, "gun-frigate"));
        sim.StepMany(48);
        Assert.Empty(sim.World.Orders);
        Assert.Null(sim.World.Winner);
    }

    [Fact]
    public void A_game_with_building_and_research_replays_exactly()
    {
        var sim = NewGame(21, classic: false);
        var pilot = new TestPilot(21) { OrderChancePerDay = 0.5 };
        pilot.PlayUntil(sim, 120 * GameTime.TicksPerDay);
        Assert.Contains(sim.Replay.Commands, c => c.Command is QueueOrderCommand);
        Assert.True(sim.World.Ships.Count > 6);
        var result = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(result.Exact, result.Message);
    }
}
