using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>
/// Fleet management: moving ships between fleets however far apart (at once, flying over, or waiting until closer),
/// the fleet lock, new ships waiting at the yard, names with their place, and which supply fleets look after a fleet.
/// </summary>
public class FleetManagementTests
{
    private static Simulation NewGame()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);
    private static Fleet Home(Simulation sim) => sim.World.Fleets.First(f => f.Side == Side.Player);
    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    private static void PlaceInOrbit(Simulation sim, Fleet f, Body b, double angle = 0)
    {
        f.Flight = null;
        f.Chase = null;
        f.Velocity = Vec2.Zero;
        f.Position = b.Position + new Vec2(DetMath.Cos(angle), DetMath.Sin(angle)) * Fleets.OrbitRadiusFor(b);
        Fleets.EnterOrbit(f, b);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
    }

    private static Fleet NewFleet(Simulation sim, Body at, params string[] classes)
    {
        var ships = classes.Select(c => Economy.NewShip(sim.World, Side.Player, c)).ToList();
        var f = Fleets.Create(sim.World, Side.Player, ships, at.Position);
        PlaceInOrbit(sim, f, at, 1.0);
        return f;
    }

    private static Fleet Drifting(Simulation sim, Vec2 at, params string[] classes)
    {
        var ships = classes.Select(c => Economy.NewShip(sim.World, Side.Player, c)).ToList();
        var f = Fleets.Create(sim.World, Side.Player, ships, at);
        Fleets.EnterDrift(f);
        f.Velocity = Vec2.Zero;
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
        return f;
    }

    private static Ship Warship(World w, Fleet f) => w.ShipsOf(f).First(s => !SupplyLines.IsSupplyShip(s));

    [Fact]
    public void Ships_fly_to_a_far_fleet_as_a_detachment_and_join_it()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Home(sim);
        var far = NewFleet(sim, BodyNamed(sim, "vela"), "gun-frigate");
        var ship = Warship(w, home);
        var est = Transfers.Estimate(w, home, new[] { ship.Id }, far);
        Assert.False(est.Together);
        Assert.Null(est.Problem);
        Assert.True(est.Hours > 0);

        sim.Issue(new TransferShipsCommand(home.Id, new List<int> { ship.Id }, far.Id));
        sim.ApplyPending();
        var going = w.FindFleet(ship.FleetId)!;
        Assert.NotEqual(home.Id, going.Id);
        Assert.Equal(far.Id, going.TransferTo);
        Assert.Equal(far.Id, going.JoinFleetId);
        Assert.Equal($"To {far.Name}", going.Name);
        Assert.Equal(FleetRole.None, going.Role);
        Assert.Contains(Transfers.Status(w, far), s => s.Contains(ship.Name) && s.Contains("joining"));
        Assert.Contains(Transfers.Status(w, going), s => s.StartsWith($"Flying to join {far.Name}"));

        for (var day = 0; day < 300 && ship.FleetId != far.Id; day++) Days(sim, 1);
        Assert.Equal(far.Id, ship.FleetId);
        Assert.Null(w.FindFleet(going.Id));
    }

    [Fact]
    public void A_far_transfer_replays_exactly()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Home(sim);
        var ship = Warship(w, home);
        var vela = BodyNamed(sim, "vela");
        sim.Issue(new SplitFleetCommand(home.Id, new List<int> { ship.Id }));
        sim.ApplyPending();
        var away = w.FindFleet(ship.FleetId)!;
        sim.Issue(new PlanFlightCommand(away.Id, new FlightRequest(vela.Id, vela.Position, 0.8, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick)));
        for (var day = 0; day < 200 && (away.Flight != null || away.OrbitBodyId != vela.Id); day++) Days(sim, 1);
        Assert.Equal(vela.Id, away.OrbitBodyId);
        sim.Issue(new TransferShipsCommand(away.Id, new List<int> { ship.Id }, home.Id));
        for (var day = 0; day < 300 && ship.FleetId != home.Id; day++) Days(sim, 1);
        Assert.Equal(home.Id, ship.FleetId);
        var replay = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(replay.Exact, replay.Message);
    }

    [Fact]
    public void Ships_at_the_same_place_move_at_once()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Home(sim);
        var other = NewFleet(sim, BodyNamed(sim, "verdant"), "gun-frigate");
        other.Position = home.Position + new Vec2(100, 0);
        var ship = Warship(w, home);
        Assert.True(Transfers.Estimate(w, home, new[] { ship.Id }, other).Together);
        sim.Issue(new TransferShipsCommand(home.Id, new List<int> { ship.Id }, other.Id));
        sim.ApplyPending();
        Assert.Equal(other.Id, ship.FleetId);
        Assert.Contains(ship.Id, other.ShipIds);
    }

    [Fact]
    public void Wait_until_closer_keeps_the_ships_until_the_other_fleet_is_within_a_day()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Home(sim);
        var far = NewFleet(sim, BodyNamed(sim, "vela"), "gun-frigate");
        var ship = Warship(w, home);
        Assert.True(Transfers.Estimate(w, home, new[] { ship.Id }, far).Long);

        sim.Issue(new TransferShipsCommand(home.Id, new List<int> { ship.Id }, far.Id, WaitUntilClose: true));
        sim.ApplyPending();
        Assert.Equal(home.Id, ship.FleetId);
        Assert.Single(home.Transfers);
        Assert.Contains(Transfers.Status(w, home), s => s.Contains("wait to join"));
        Days(sim, 2);
        Assert.Equal(home.Id, ship.FleetId);

        // The other fleet comes home: the waiting ship joins it.
        PlaceInOrbit(sim, far, BodyNamed(sim, "verdant"), 0.02);
        far.Position = home.Position + new Vec2(100, 0);
        Days(sim, 1);
        Assert.Equal(far.Id, ship.FleetId);
        Assert.Empty(home.Transfers);
    }

    [Fact]
    public void A_locked_fleet_refuses_gathers_and_joins_but_takes_its_own_ships_back()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Home(sim);
        sim.Issue(new SetFleetLockCommand(Side.Player, home.Id, true));
        sim.ApplyPending();
        Assert.True(home.Locked);
        Assert.Equal(home.ShipIds.OrderBy(x => x), home.Roster.OrderBy(x => x));
        var count = home.ShipIds.Count;

        // Gather into it: refused, the other fleet says why.
        var other = NewFleet(sim, BodyNamed(sim, "verdant"), "gun-frigate");
        other.Position = home.Position + new Vec2(200, 0);
        sim.Issue(new GatherFleetsCommand(home.Id, new List<int> { other.Id }, 0.7));
        sim.ApplyPending();
        Assert.NotNull(w.FindFleet(other.Id));
        Assert.Contains("locked", other.Refused);
        Assert.Equal(count, home.ShipIds.Count);

        // A join order set some other way is refused when it would merge.
        other.JoinFleetId = home.Id;
        sim.Step();
        Assert.NotNull(w.FindFleet(other.Id));
        Assert.Null(other.JoinFleetId);
        Assert.Equal(count, home.ShipIds.Count);

        // Its own ship split off and coming back is fine.
        var own = Warship(w, home);
        new SplitFleetCommand(home.Id, new List<int> { own.Id }).Apply(w, sim);
        var away = w.FindFleet(own.FleetId)!;
        Assert.NotEqual(home.Id, away.Id);
        away.JoinFleetId = home.Id;
        sim.Step();
        Assert.Equal(home.Id, own.FleetId);

        // Moving a ship in by hand on the fleets screen is allowed, and it is kept from then on.
        var hand = Warship(w, other);
        sim.Issue(new TransferShipsCommand(other.Id, new List<int> { hand.Id }, home.Id));
        sim.ApplyPending();
        Assert.Equal(home.Id, hand.FleetId);
        Assert.Contains(hand.Id, home.Roster);
    }

    [Fact]
    public void New_ships_wait_at_the_yard_instead_of_joining_a_fleet_with_a_job()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: new GameOptions(Budget: false));
        Starts.Classic(sim);
        var w = sim.World;
        var home = Home(sim);
        var verdant = BodyNamed(sim, "verdant");
        sim.Issue(new SetFleetRoleCommand(home.Id, FleetRole.Defence));
        sim.ApplyPending();
        var count = home.ShipIds.Count;
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, "gun-frigate"));
        sim.ApplyPending();
        Assert.NotEmpty(w.Orders);
        for (var d = 0; d < 60 && w.Orders.Count > 0; d++) Days(sim, 1);
        Assert.Empty(w.Orders);
        Assert.Equal(count, home.ShipIds.Count);
        var pool = w.Fleets.Single(f => f.Side == Side.Player && f.YardPoolBodyId == verdant.Id);
        Assert.Equal($"New ships at {verdant.Name}", pool.Name);

        // The next ship launched there joins the same fleet.
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, "gun-frigate"));
        sim.ApplyPending();
        for (var d = 0; d < 60 && w.Orders.Count > 0; d++) Days(sim, 1);
        Assert.Equal(2, pool.ShipIds.Count);
        Assert.Equal(count, home.ShipIds.Count);
    }

    [Fact]
    public void Role_fleets_are_named_for_the_planet_they_look_after()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Home(sim);
        Economy.Of(w, Side.Player).FlagshipId = null; // the flagship's fleet is the "Flagship fleet" whatever its role
        var verdant = BodyNamed(sim, "verdant");
        PlaceInOrbit(sim, home, verdant);
        sim.Issue(new SetFleetRoleCommand(home.Id, FleetRole.Defence));
        sim.ApplyPending();
        Assert.Equal($"{verdant.Name} defence", home.Name);
        sim.Issue(new SetFleetRoleCommand(home.Id, FleetRole.Support));
        sim.ApplyPending();
        Assert.Equal($"{verdant.Name} response", home.Name);
        Assert.Equal("Response", HelpCalls.RoleName(FleetRole.Support));
        Assert.Equal("Patrol", HelpCalls.RoleName(FleetRole.AntiPiracy));
        Assert.Equal("Guard", HelpCalls.RoleName(FleetRole.ForwardGuard));

        // Out in open space with no place yet: the plain name.
        var lone = Drifting(sim, verdant.Position * 1.3, "gun-frigate");
        new SetFleetRoleCommand(lone.Id, FleetRole.Defence).Apply(w, sim);
        Assert.Equal("Defence fleet", FleetNames.KindOf(lone.Name));
    }

    [Fact]
    public void A_supply_fleet_serves_the_fleets_on_its_list_first()
    {
        var sim = NewGame();
        var w = sim.World;
        var verdant = BodyNamed(sim, "verdant");
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player && f.Role == FleetRole.Supply)) f.Role = FleetRole.None;
        var supply = NewFleet(sim, verdant, "tanker");
        foreach (var s in w.ShipsOf(supply)) SupplyLines.FillCargo(s);
        supply.Role = FleetRole.Supply;
        var near = Drifting(sim, verdant.Position + new Vec2(0.15 * Physics.MmPerAu, 0), "gun-frigate");
        var farther = Drifting(sim, verdant.Position + new Vec2(0, 0.4 * Physics.MmPerAu), "gun-frigate");
        foreach (var f in new[] { near, farther })
            foreach (var s in w.ShipsOf(f)) s.Supplies = Fitting.MaxSupplies(w, s) * 0.3;
        Assert.True(SupplyLines.Short(w, near) && SupplyLines.Short(w, farther));

        // The picture before: both would be served (Covered); the farther one is "after its list" once a list exists.
        Assert.Equal(CoverVerdict.Covered, SupplyCover.Of(w, farther).Verdict);
        sim.Issue(new SetSupplyFirstCommand(supply.Id, new List<int> { farther.Id }));
        sim.ApplyPending();
        Assert.Equal(new[] { farther.Id }, supply.SupplyFirst);
        Assert.Contains(SupplyCover.Of(w, near).Sources, x => x.FleetId == supply.Id && x.State.Contains("after its list"));

        for (var i = 0; i < 3 && supply.SupplyRunFor == null; i++) sim.Step();
        Assert.Equal(farther.Id, supply.SupplyRunFor);
    }

    [Fact]
    public void The_supply_picture_says_who_looks_after_a_fleet()
    {
        var sim = NewGame();
        var w = sim.World;
        var verdant = BodyNamed(sim, "verdant");
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player && f.Role == FleetRole.Supply)) f.Role = FleetRole.None;
        // Far out with nothing: None.
        var alone = Drifting(sim, verdant.Position + new Vec2(2.5 * Physics.MmPerAu, 0), "gun-frigate");
        Assert.Equal(CoverVerdict.None, SupplyCover.Of(w, alone).Verdict);
        // With its own tender: Thin.
        var tended = Drifting(sim, verdant.Position + new Vec2(-2.5 * Physics.MmPerAu, 0), "gun-frigate", "tanker");
        var p = SupplyCover.Of(w, tended);
        Assert.Equal(CoverVerdict.Thin, p.Verdict);
        Assert.Contains(p.Sources, x => x.Kind == "tender");
        // A Supply-role fleet in range: Covered; one out of range says so.
        var supply = NewFleet(sim, verdant, "tanker");
        supply.Role = FleetRole.Supply;
        var close = Drifting(sim, verdant.Position + new Vec2(0.2 * Physics.MmPerAu, 0), "gun-frigate");
        Assert.Equal(CoverVerdict.Covered, SupplyCover.Of(w, close).Verdict);
        Assert.Contains(SupplyCover.Of(w, alone).Sources, x => x.FleetId == supply.Id && x.State.StartsWith("out of range"));
    }
}
