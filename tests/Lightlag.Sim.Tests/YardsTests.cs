using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>
/// More than one shipyard (see Yards): berths on the ground and in orbit, Auto placing orders, pinned orders,
/// the dry dock upgrade, hulls lost with their yard, the home yard moving (and the stop), and losing only when
/// the last yard planet falls.
/// </summary>
public class YardsTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Starts.Classic(Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: new GameOptions(Budget: false)));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        sim.World.Minds.Clear();
        return sim;
    }

    private static Body Named(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);
    private static Body Home(Simulation sim) => Economy.ShipyardOf(sim.World, Side.Player)!;

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

    /// <summary>A colony of the player's with people, a dry dock and the stores to build: the second-yard site.</summary>
    private static Body Colony(Simulation sim, string key = "vela", double popK = 14)
    {
        var w = sim.World;
        var b = Named(sim, key);
        b.Owner = Side.Player;
        b.PopK = popK;
        b.Happiness = 60;
        Planets.EnsureStarted(w);
        var me = Economy.Of(w, Side.Player);
        foreach (var r in new[] { "orbital-docks", "colony-shipyards" }) if (!me.Researched.Contains(r)) me.Researched.Add(r);
        var dock = Planets.BuildingKind(Colonies.DryDock)!;
        var spot = Colonies.BestSpot(b, dock)!.Value;
        Colonies.AddPiece(w, b, dock, spot.Item1, spot.Item2, spot.Item3, built: true);
        Colonies.SyncBuildings(b);
        foreach (var good in new[] { "alloys", "electronics" }) b.Stock[good] = 400;
        return b;
    }

    private static WorkOrder Queue(Simulation sim, string key, int? yard = null, int? join = null)
    {
        new QueueOrderCommand(Side.Player, OrderKind.Ship, key, join, YardBodyId: yard).Apply(sim.World, sim);
        return sim.World.Orders.Last(o => o.Side == Side.Player && o.Kind == OrderKind.Ship);
    }

    [Fact]
    public void A_planet_with_a_starbase_builds_on_the_ground_and_in_orbit_at_once()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Home(sim);
        Station(sim, Side.Player, home, "station-starbase");
        Economy.Of(w, Side.Player).Researched.Add(Tech.CruiserHull);
        Economy.Of(w, Side.Player).Industry = 5000;
        foreach (var good in new[] { "alloys", "electronics" }) home.Stock[good] = 400;
        var berths = Yards.At(w, Side.Player, home).ToList();
        Assert.Equal(new[] { Yards.Ground, "orbit0" }, berths.Select(b => b.Key));
        var frigate = Queue(sim, "gun-frigate");
        var cruiser = Queue(sim, "cruiser");
        sim.Step();
        Assert.True(frigate.Paid);
        Assert.True(cruiser.Paid);
        Assert.Equal(Yards.Ground, frigate.Berth);
        Assert.Equal("orbit0", cruiser.Berth);
        // A third ship waits for a free berth.
        var third = Queue(sim, "gun-frigate");
        sim.Step();
        Assert.False(third.Paid);
    }

    [Fact]
    public void Auto_builds_near_the_fleet_the_ship_joins_and_a_pinned_order_waits_for_its_yard()
    {
        var sim = NewGame();
        var w = sim.World;
        Economy.Of(w, Side.Player).Industry = 5000;
        var home = Home(sim);
        foreach (var good in new[] { "alloys", "electronics" }) home.Stock[good] = 400;
        var vela = Colony(sim);
        Assert.Equal(2, YardViews(w));
        // A fleet out at Vela: a ship that joins it is built there.
        var ship = Economy.NewShip(w, Side.Player, "gun-frigate");
        var fleet = Fleets.Create(w, Side.Player, new[] { ship }, vela.Position);
        var near = Queue(sim, "gun-frigate", join: fleet.Id);
        var plain = Queue(sim, "gun-frigate");
        sim.Step();
        Assert.Equal(vela.Id, near.YardBodyId);
        Assert.Equal(home.Id, plain.YardBodyId);
        // Pinned to Vela: it waits there (Vela's dry dock is busy), it does not go home.
        var pinned = Queue(sim, "gun-frigate", yard: vela.Id);
        Assert.True(pinned.YardPinned);
        sim.Step();
        Assert.False(pinned.Paid);
        Assert.Equal(vela.Id, pinned.YardBodyId);
        // Handed back to Auto it takes a free berth elsewhere when one frees up.
        new SetOrderYardCommand(Side.Player, pinned.Id, null).Apply(w, sim);
        Assert.False(pinned.YardPinned);
    }

    private static int YardViews(World w) => w.Bodies.Count(b => Yards.At(w, Side.Player, b).Any());

    [Fact]
    public void A_dry_dock_is_upgraded_to_a_shipyard_and_the_home_yard_can_fall_without_losing()
    {
        var sim = NewGame();
        var w = sim.World;
        Economy.Of(w, Side.Player).Industry = 5000;
        var home = Home(sim);
        var vela = Colony(sim, popK: 8);
        Assert.Contains("10k people", Yards.CanUpgrade(w, Side.Player, vela.Id));
        vela.PopK = 14;
        Assert.Null(Yards.CanUpgrade(w, Side.Player, vela.Id));
        new UpgradeYardCommand(Side.Player, vela.Id).Apply(w, sim);
        Assert.NotNull(vela.YardUpgradeHoursLeft);
        Assert.Equal(360, vela.Stock["alloys"], 6);
        sim.StepMany(15 * 24 + 1);
        Assert.True(vela.Shipyard);
        Assert.Null(vela.YardUpgradeHoursLeft);
        Assert.Contains(Colonies.Shipyard, vela.Buildings);
        Assert.Equal(2, Yards.YardPlanets(w, Side.Player).Count);
        Assert.Equal(home, Yards.Home(w, Side.Player));
        // The home yard falls: Vela takes over, every yard stops for a while, and the game goes on.
        Yards.LoseShipyard(sim, home, Side.Player, "test");
        sim.Step();
        Assert.Null(w.Winner);
        Assert.Equal(vela, Economy.ShipyardOf(w, Side.Player));
        Assert.NotNull(Yards.Stopped(w, Side.Player));
        var order = Queue(sim, "gun-frigate");
        sim.Step();
        Assert.False(order.Paid);
        Assert.Contains("home yard fell", order.Waiting);
        sim.StepMany(10 * 24);
        Assert.Null(Yards.Stopped(w, Side.Player));
        Assert.True(order.Paid);
        Assert.Equal(vela.Id, order.YardBodyId);
        // The last yard falls: now the side is out.
        Yards.LoseShipyard(sim, vela, Side.Player, "test");
        sim.Step();
        Assert.Equal(Side.Rival, w.Winner);
    }

    [Fact]
    public void A_hull_on_a_slipway_is_lost_with_the_station_and_slipways_are_limited()
    {
        var sim = NewGame();
        var w = sim.World;
        var vela = Colony(sim);
        var st = Station(sim, Side.Player, vela, "station-starbase");
        var order = new WorkOrder { Id = w.NextId++, Side = Side.Player, Kind = OrderKind.Ship, Key = "gun-frigate", Paid = true, HoursLeft = 50, TotalHours = 100, YardBodyId = vela.Id, Berth = "orbit0" };
        w.Orders.Add(order);
        sim.Step();
        Assert.Contains(order, w.Orders);
        w.Starbases.Remove(st);
        sim.Step();
        Assert.DoesNotContain(order, w.Orders);
        // Three yard bays on one station: refused.
        var cls = GameData.ShipClass("station-starbase");
        var fit = Fitting.StandardFit(cls).ToList();
        var free = Enumerable.Range(0, fit.Count).Where(i => cls.SlotList[i].Type == "B" && GameData.Module(fit[i])?.YardM is not > 0).Take(2).ToList();
        fit[free[0]] = "great-slipway";
        Assert.DoesNotContain("yard bays", Fitting.CanFit(w, Side.Player, cls, fit) ?? "");
        fit[free[1]] = "dock-bay";
        Assert.Contains("yard bays", Fitting.CanFit(w, Side.Player, cls, fit));
    }

    [Fact]
    public void A_taken_shipyard_is_left_a_dry_dock_and_queued_ships_go_elsewhere()
    {
        var sim = NewGame();
        var w = sim.World;
        var vela = Colony(sim);
        new UpgradeYardCommand(Side.Player, vela.Id).Apply(w, sim);
        vela.YardUpgradeHoursLeft = 1;
        sim.Step();
        Assert.True(vela.Shipyard);
        var pinned = Queue(sim, "gun-frigate", yard: vela.Id);
        Planets.Take(sim, vela, Side.Rival, "test");
        Assert.False(vela.Shipyard);
        Assert.Contains(Colonies.DryDock, vela.Buildings);
        Assert.False(pinned.YardPinned);
        Assert.Null(pinned.YardBodyId);
        Assert.Null(w.Winner);
    }
}
