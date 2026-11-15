using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>The battle moves: hold at rail range, point defence for fleet or self, protect a ship, split and run or hold back, rejoin when done.</summary>
public class BattleMovesTests
{
    private static Simulation Sandbox(SandboxSetup setup, ulong seed = 3)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), sandbox: setup);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Simulation Game(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet FleetOf(Simulation sim, Side side) => sim.World.Fleets.First(f => f.Side == side);

    [Fact]
    public void Every_chase_mode_has_a_name()
    {
        foreach (var mode in Enum.GetValues<ChaseMode>().Where(m => m != ChaseMode.None))
            Assert.NotEqual("attack", FlightPlanner.ChaseName(mode));
    }

    [Fact]
    public void Hold_at_rail_range_is_offered_and_sits_inside_railgun_reach()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 2, PlayerDestroyers: 0, RivalFrigates: 1, RivalDestroyers: 0,
            PlayerStance: Stance.Hold, RivalStance: Stance.Hold, DistanceMm: 90));
        var w = sim.World;
        var mine = FleetOf(sim, Side.Player);
        var info = Engagement.Of(w, mine, FleetOf(sim, Side.Rival).Id)!;
        var rails = info.Options.Single(o => o.Key == "rails");
        Assert.Null(rails.Problem);
        Assert.Equal(ChaseMode.RailRange, rails.Request!.Chase);
        var hold = Combat.RailHold(w, mine);
        Assert.InRange(hold, Secrecy.RailRange(w, mine) * 0.8, Secrecy.RailRange(w, mine) * 0.9);
        Assert.True(hold > GameData.Weapons.L.RangeMm * 1.2); // outside pulse lances
    }

    [Fact]
    public void Holding_at_rail_range_backs_off_when_they_charge_while_fuel_lasts()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 2, PlayerDestroyers: 0, RivalFrigates: 1, RivalDestroyers: 0,
            PlayerStance: Stance.Engage, RivalStance: Stance.Hold, DistanceMm: 18));
        var w = sim.World;
        var mine = FleetOf(sim, Side.Player);
        var them = FleetOf(sim, Side.Rival);
        mine.Chase = Chases.Order(w, mine, them.Id, ChaseMode.RailRange, 1.0);
        Assert.Equal(them.Id, Combat.RailHoldTooClose(w, mine)?.Id);
        var before = Vec2.Distance(mine.Position, them.Position);
        for (var i = 0; i < 3; i++) sim.Step();
        Assert.True(Vec2.Distance(mine.Position, them.Position) > before, "it should open the gap, not close in");
        Assert.False(Combat.WouldEngage(w, mine, them));

        // Low on supplies it stands where it is.
        foreach (var s in w.ShipsOf(mine)) s.Supplies = Fitting.MaxSupplies(w, s) * 0.2;
        mine.Maneuvering = false;
        Assert.Null(Combat.RailHoldTooClose(w, mine));
    }

    [Fact]
    public void Point_defence_on_self_only_covers_its_own_ship_but_harder()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 3, PlayerDestroyers: 0, RivalFrigates: 1, RivalDestroyers: 0));
        var w = sim.World;
        var f = FleetOf(sim, Side.Player);
        var ships = w.ShipsOf(f).ToList();
        var (a, b) = (ships[0], ships[1]);
        Assert.Equal(1, Formations.PdShare(f, a, a));
        Assert.True(Formations.PdShare(f, a, b) > 0);
        new SetPdGuardCommand(f.Id, PdGuard.Self).Apply(w, sim);
        Assert.Equal(Formations.D.PdSelfFactor, Formations.PdShare(f, a, a));
        Assert.Equal(0, Formations.PdShare(f, a, b));
    }

    [Fact]
    public void The_most_damaged_ship_is_protected_and_takes_the_safest_slot()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 4, PlayerDestroyers: 0, RivalFrigates: 1, RivalDestroyers: 0));
        var w = sim.World;
        var f = FleetOf(sim, Side.Player);
        f.Formation = Formation.Staggered;
        var hurt = w.ShipsOf(f).OrderBy(s => s.Id).First();
        Assert.Null(Flagships.Worst(w, f));
        hurt.Armour = Tech.MaxArmour(w, hurt) * 0.4;
        Assert.Equal(hurt.Id, Flagships.Worst(w, f)?.Id);
        Assert.True(Flagships.IsProtected(w, hurt));
        Assert.False(Flagships.GuardsSomething(w, f)); // the Auto mark is no tell for enemy flagship hunters

        Formations.Keep(w, f);
        Assert.Equal(hurt.Id, f.LaidProtect);
        var front = Formations.Front(w, f);
        var ahead = new Vec2(DetMath.Cos(front), DetMath.Sin(front));
        var fwd = w.ShipsOf(f).ToDictionary(s => s.Id, s => Vec2.Dot(s.FormationOffset, ahead));
        Assert.Equal(fwd.Values.Min(), fwd[hurt.Id], 6);

        // Picking a ship by hand wins, and puts a loose fleet into Screen cover.
        var pick = w.ShipsOf(f).OrderBy(s => s.Id).Last();
        new ProtectShipCommand(f.Id, ProtectPick.Ship, pick.Id).Apply(w, sim);
        Assert.Equal((ProtectPick.Ship, (int?)pick.Id), BattleMoves.Protecting(w, f));
        Assert.Equal(pick.Id, Formations.Guarded(w, f));
        Assert.False(Flagships.IsProtected(w, hurt));
        Assert.Equal(CoverStyle.Screen, f.Cover);

        new ProtectShipCommand(f.Id, ProtectPick.None).Apply(w, sim);
        Assert.Equal(0, Formations.Guarded(w, f));
    }

    [Fact]
    public void Split_and_hold_back_keeps_its_distance_and_rejoins_when_done()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 2, PlayerDestroyers: 1, RivalFrigates: 2, RivalDestroyers: 0,
            PlayerStance: Stance.Hold, RivalStance: Stance.Hold, DistanceMm: 60));
        var w = sim.World;
        var f = FleetOf(sim, Side.Player);
        var destroyer = w.ShipsOf(f).First(s => Fitting.Spec(s).Missiles > 0);
        new SplitOffCommand(f.Id, new List<int> { destroyer.Id }, SplitThen.HoldBack).Apply(w, sim);
        var g = w.FleetOf(destroyer)!;
        Assert.NotEqual(f.Id, g.Id);
        Assert.Equal(f.Id, g.RejoinFleetId);
        Assert.Equal(RejoinWhen.Done, g.RejoinWhen);
        Assert.Equal(Stance.Hold, g.Stance);
        Assert.Equal(ChaseMode.MissileRange, g.Chase?.Mode);
        Assert.False(BattleMoves.Done(w, g, f) && Heat.InBattle(w, g));
    }

    [Fact]
    public void Split_and_run_sends_the_ships_to_a_yard_to_rejoin_once_mended()
    {
        var sim = Game();
        var w = sim.World;
        var f = w.Fleets.Where(x => x.Side == Side.Player && x.ShipIds.Count >= 2).OrderBy(x => x.Id).First();
        var ship = w.ShipsOf(f).OrderBy(s => s.Id).Last();
        new SplitOffCommand(f.Id, new List<int> { ship.Id }, SplitThen.RunHome).Apply(w, sim);
        var g = w.FleetOf(ship)!;
        Assert.NotEqual(f.Id, g.Id);
        Assert.True(g.Retreating || Economy.HomeOf(w, Side.Player) is Body home && Economy.InOrbitAt(g, home));
        Assert.Equal(f.Id, g.RejoinFleetId);
        Assert.Equal(RejoinWhen.Mended, g.RejoinWhen);
    }

    [Fact]
    public void A_plain_split_away_from_a_fight_stays_and_a_done_group_flies_back()
    {
        var sim = Game();
        var w = sim.World;
        var f = w.Fleets.Where(x => x.Side == Side.Player && x.ShipIds.Count >= 2).OrderBy(x => x.Id).First();
        var ship = w.ShipsOf(f).OrderBy(s => s.Id).Last();
        new SplitOffCommand(f.Id, new List<int> { ship.Id }, SplitThen.OwnOrders).Apply(w, sim);
        var g = w.FleetOf(ship)!;
        Assert.Null(g.RejoinFleetId);
        Assert.Equal(RejoinWhen.Never, g.RejoinWhen);

        // Told to rejoin when done (nothing left to do): it heads back at the next hour.
        g.RejoinFleetId = f.Id;
        new SetRejoinWhenCommand(g.Id, RejoinWhen.Done).Apply(w, sim);
        Assert.True(BattleMoves.Done(w, g, f));
        BattleMoves.Hourly(sim);
        Assert.True(g.JoinFleetId == f.Id || w.FindFleet(g.Id) == null || w.FleetOf(ship)!.Id == f.Id);
    }
}
