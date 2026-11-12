using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Plan 3 build 2: pulse lances, mines, planet squadrons, the missile counter-race, rival research, secrecy and reach guesses.</summary>
public class WeaponsTests
{
    private static Simulation Sandbox(SandboxSetup setup, ulong seed = 3)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), sandbox: setup);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Starts.Classic(Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory()));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        sim.World.Minds.Clear();
        return sim;
    }

    private static void Learn(Simulation sim, Side side, params string[] ids)
    {
        foreach (var id in ids) if (!Tech.Has(sim.World, side, id)) Economy.Of(sim.World, side).Researched.Add(id);
        Tech.Refresh(sim.World);
    }

    private static Fleet Home(Simulation sim, Side side = Side.Player) => sim.World.Fleets.First(f => f.Side == side);

    // ---- Pulse lance ----

    [Fact]
    public void Lance_mounts_fit_a_frigate_and_count_as_lances()
    {
        var cls = GameData.ShipClass("gun-frigate");
        var fit = Fitting.StandardFit(cls);
        var w = Enumerable.Range(0, fit.Count).First(i => cls.SlotList[i].Type == "W");
        fit[w] = Beams.LanceModule;
        var spec = Fitting.Compute(cls, fit);
        Assert.Equal(2, spec.Lances);
        Assert.Equal(0, spec.Railguns);
    }

    [Fact]
    public void Lances_fire_without_being_dodged_and_heat_both_ships()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 1, PlayerDestroyers: 0, RivalFrigates: 1, RivalDestroyers: 0, DistanceMm: 18));
        var w = sim.World;
        var mine = w.Ships.First(s => s.Side == Side.Player);
        var cls = GameData.ShipClass(mine.ClassId);
        var fit = Fitting.StandardFit(cls);
        for (var i = 0; i < fit.Count; i++) if (fit[i] == "railgun") fit[i] = Beams.LanceModule;
        Fitting.Apply(w, mine, fit);
        Home(sim).AutoSinks = false;
        var target = w.Ships.First(s => s.Side == Side.Rival);
        sim.World.FleetOf(target)!.AutoSinks = false;
        for (var i = 0; i < 60; i++) sim.Step();
        var stats = w.Battles[0].For(Side.Player);
        Assert.True(stats.LanceShots > 0);
        Assert.True(stats.LanceHits > 0);
        Assert.Equal(0, stats.RailShots);
        Assert.True(mine.Heat > 0);
    }

    // ---- Mines ----

    [Fact]
    public void A_path_enters_a_field_only_from_outside()
    {
        Assert.True(Mines.Enters(new Vec2(-10, 0), new Vec2(10, 0), Vec2.Zero, 2));
        Assert.False(Mines.Enters(new Vec2(-10, 5), new Vec2(10, 5), Vec2.Zero, 2));
        Assert.False(Mines.Enters(new Vec2(0, 1), new Vec2(10, 0), Vec2.Zero, 2)); // already inside
    }

    [Fact]
    public void Crossing_a_field_strikes_ships_and_iff_spares_its_own_side()
    {
        var sim = NewGame();
        var w = sim.World;
        var rival = Home(sim, Side.Rival);
        var mine = Home(sim, Side.Player);
        var at = rival.Position + new Vec2(200, 0);
        w.Minefields.Add(new Minefield { Id = w.NextId++, Side = Side.Player, Position = at, RadiusMm = 4, StartRadiusMm = 4, Mines = 100, LaidHour = w.Hour, Iff = true });
        var armourBefore = w.ShipsOf(rival).Sum(s => s.Armour + s.Shield);
        var ownBefore = w.ShipsOf(mine).Sum(s => s.Armour + s.Shield);
        var from = at - new Vec2(50, 0);
        rival.Position = at;
        mine.Position = at;
        Mines.Update(sim, new List<(Fleet, Vec2)> { (rival, from), (mine, from) }, 1e-6);
        Assert.True(w.ShipsOf(rival).Sum(s => s.Armour + s.Shield) < armourBefore || w.Ships.Count(s => s.Side == Side.Rival) < 3);
        Assert.Equal(ownBefore, w.ShipsOf(mine).Sum(s => s.Armour + s.Shield), 6);
        Assert.True(w.Minefields[0].Mines < 100);
        Assert.True(Mines.Known(w.Minefields[0], Side.Rival));
    }

    [Fact]
    public void Laying_mines_in_orbit_uses_missiles_and_rings_the_planet()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = Home(sim);
        var missiles = w.ShipsOf(f).Sum(s => s.Missiles);
        Assert.True(missiles > 0);
        sim.Issue(new LayMinesCommand(f.Id, 2));
        sim.Step();
        Assert.Empty(w.Minefields); // not researched yet
        Learn(sim, Side.Player, Mines.Idea);
        sim.Issue(new LayMinesCommand(f.Id, 2));
        sim.Step();
        var field = Assert.Single(w.Minefields);
        Assert.Equal(f.OrbitBodyId, field.BodyId);
        Assert.True(field.Mines >= 1);
        Assert.False(field.Iff);
        Assert.Equal(missiles - (int)field.Mines, w.ShipsOf(f).Sum(s => s.Missiles));
        Assert.True(Mines.Known(field, Side.Player));
        Assert.False(Mines.Known(field, Side.Rival));
    }

    // ---- The counter-race ----

    [Fact]
    public void Hijack_needs_research_spares_dumb_missiles_and_iff_cuts_it()
    {
        var sim = NewGame();
        var w = sim.World;
        Assert.Equal(0, Countermeasures.HijackChance(w, Side.Rival, 2));
        Learn(sim, Side.Player, Countermeasures.Hijack);
        Assert.Equal(0, Countermeasures.HijackChance(w, Side.Rival, 1));
        var open = Countermeasures.HijackChance(w, Side.Rival, 2);
        Assert.True(open > 0);
        Assert.True(Countermeasures.HijackChance(w, Side.Rival, 4) < open);
        Learn(sim, Side.Rival, Countermeasures.Iff);
        Assert.Equal(open * 0.25, Countermeasures.HijackChance(w, Side.Rival, 2), 9);
    }

    [Fact]
    public void Jamming_leaves_dumb_missiles_alone()
    {
        var c = GameData.Weapons.C;
        Assert.Equal(0, c.Jam(1));
        Assert.True(c.Jam(2) > c.Jam(4));
    }

    [Fact]
    public void Dumb_salvos_and_per_ship_heat_sinks_are_orders()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = Home(sim);
        sim.Issue(new SetSalvoGuidanceCommand(f.Id, true));
        var ships = w.ShipsOf(f).ToList();
        var sinks = ships.Select(s => s.HeatSinks).ToList();
        sim.Step();
        Assert.True(f.DumbSalvos);
        sinks = ships.Select(s => s.HeatSinks).ToList();
        new EjectShipSinkCommand(f.Id, ships[0].Id).Apply(w, sim); // at once: in home orbit the racks refill each hour
        Assert.Equal(sinks[0] - 1, ships[0].HeatSinks);
        for (var i = 1; i < ships.Count; i++) Assert.Equal(sinks[i], ships[i].HeatSinks);
    }

    // ---- Keeping tech secret ----

    [Fact]
    public void Holding_back_hides_the_newest_weapons_and_range_until_brought_out()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = Home(sim);
        Assert.False(Secrecy.Holding(w, f)); // nothing advanced yet: early fights are unchanged
        Learn(sim, Side.Player, Tech.FastRailguns, Tech.RailgunMk3, Tech.MissileBrain2, Tech.MissileBrain3);
        Assert.Equal(new[] { Tech.MissileBrain2, Tech.MissileBrain3 }.OrderBy(x => x), Secrecy.Advanced(w, Side.Player).OrderBy(x => x));
        sim.Issue(new SetAdvancedCommand(Side.Player, new List<string> { Tech.RailgunMk3, Tech.MissileBrain3 }));
        sim.Step();
        var ship = w.ShipsOf(f).First(s => Fitting.Spec(s).Railguns > 0);
        ship.RailGrade = 2;
        Assert.True(Secrecy.Holding(w, f));
        Assert.Equal(GameData.Combat.Railgun.Damage, Secrecy.RailDamage(w, f, ship), 9);
        Assert.Equal(2, Secrecy.BrainCap(w, f));
        Assert.Equal(GameData.Weapons.K.HideRangeShare, Secrecy.ReachShare(w, f), 9);
        f.AdvancedOut = true;
        Assert.True(Secrecy.RailDamage(w, f, ship) > GameData.Combat.Railgun.Damage);
        Assert.Equal(5, Secrecy.BrainCap(w, f));
        Assert.Equal(1, Secrecy.ReachShare(w, f));
        f.AdvancedOut = false;
        sim.Issue(new SetSecrecyCommand(f.Id, WeaponsShown.ShowAll, new SecrecyRules()));
        sim.Step();
        Assert.False(Secrecy.Holding(w, f));
    }

    [Fact]
    public void Ideas_held_back_all_fight_are_not_learned()
    {
        var b = new BattleSide();
        b.Note(Secrecy.HeldKey, Tech.RailgunMk3);
        b.Note(Secrecy.HeldKey, Tech.MissileBrain3);
        b.Note(Secrecy.ShownKey, Tech.MissileBrain3);
        Assert.Equal(new[] { Tech.RailgunMk3 }, Secrecy.HeldAllFight(b).ToArray());
    }

    // ---- Learning the rival's research ----

    [Fact]
    public void Rival_ideas_go_seen_studied_unlocked_and_unlock_without_prerequisites()
    {
        var sim = NewGame();
        var w = sim.World;
        RivalTech.Teach(sim, Side.Player, Tech.RailgunMk3, 1, "test");
        Assert.Equal(RivalStage.None, RivalTech.StageOf(w, Side.Player, Tech.RailgunMk3)); // the rival hasn't got it
        Learn(sim, Side.Rival, Tech.FastRailguns, Tech.RailgunMk3);
        var before = Research.BaseHours(w, Side.Player, Tech.RailgunMk3);
        RivalTech.Teach(sim, Side.Player, Tech.RailgunMk3, 0.1, "test");
        Assert.Equal(RivalStage.Seen, RivalTech.StageOf(w, Side.Player, Tech.RailgunMk3));
        Assert.False(Research.Open(w, Side.Player, Tech.RailgunMk3));
        RivalTech.Teach(sim, Side.Player, Tech.RailgunMk3, 0.3, "test");
        Assert.Equal(RivalStage.Studied, RivalTech.StageOf(w, Side.Player, Tech.RailgunMk3));
        Assert.True(Research.Seen(w, Side.Player, Tech.RailgunMk3));
        RivalTech.Teach(sim, Side.Player, Tech.RailgunMk3, 0.7, "test");
        Assert.Equal(RivalStage.Unlocked, RivalTech.StageOf(w, Side.Player, Tech.RailgunMk3));
        Assert.True(Research.Open(w, Side.Player, Tech.RailgunMk3));
        Assert.Null(Economy.CanQueue(w, Side.Player, OrderKind.Research, Tech.RailgunMk3));
        Assert.True(Research.BaseHours(w, Side.Player, Tech.RailgunMk3) < before);
    }

    [Fact]
    public void A_stripped_wreck_teaches_the_parts_aboard()
    {
        var sim = NewGame();
        var w = sim.World;
        Learn(sim, Side.Rival, Tech.MissileBrain2, Tech.PointDefence2);
        RivalTech.FromWreck(sim, Side.Player, "missile-destroyer");
        Assert.True(RivalTech.Progress(w, Side.Player, Tech.MissileBrain2) > 0);
        Assert.True(RivalTech.Progress(w, Side.Player, Tech.PointDefence2) > 0);
    }

    // ---- Reach as a guess ----

    [Fact]
    public void Reach_is_a_guess_until_that_fleet_is_fought()
    {
        var sim = NewGame();
        var w = sim.World;
        var theirs = Home(sim, Side.Rival);
        var g = RivalTech.Guess(w, Side.Player, theirs.Id);
        Assert.Contains("own tech", g.How);
        Intel.Of(w, Side.Player).Reach.Add(new ReachFact { FleetId = theirs.Id, Rail = 28, Missile = 42, Hour = w.Hour, Fights = 1 });
        g = RivalTech.Guess(w, Side.Player, theirs.Id);
        Assert.Equal(28, g.Rail);
        Assert.Equal(0, g.Plus);
        var other = RivalTech.Guess(w, Side.Player, theirs.Id + 999);
        Assert.Equal(0.25, other.Plus);
    }

    // ---- Planet squadrons ----

    [Fact]
    public void A_fighter_base_sends_squadrons_at_enemies_close_by()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Economy.HomeOf(w, Side.Player)!;
        var data = Planets.BuildingKind(StrikeCraft.Base)!;
        var (x, y, turned) = Colonies.BestSpot(home, data)!.Value;
        Colonies.AddPiece(w, home, data, x, y, turned, built: true);
        Colonies.SyncBuildings(home);
        Assert.Equal(1, StrikeCraft.Bombers(home));
        var rival = Home(sim, Side.Rival);
        rival.Flight = null;
        rival.Hold = HoldStyle.Drift;
        rival.OrbitBodyId = null;
        rival.Velocity = Vec2.Zero;
        rival.Position = home.Position + new Vec2(30, 0);
        foreach (var s in w.ShipsOf(rival)) s.Position = rival.Position + s.FormationOffset;
        rival.Stance = Stance.Hold;
        Sensors.Update(sim);
        StrikeCraft.Hourly(sim);
        Assert.Equal(1, Economy.Of(w, Side.Player).Totals.Sorties);
        Assert.True(StrikeCraft.BombCover(home) > 1);
    }

    // ---- Saves and replays ----

    [Fact]
    public void The_new_orders_replay_exactly()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 11, GameLog.Memory());
        sim.StepMany(10);
        var f = Home(sim);
        var ship = sim.World.ShipsOf(f).First();
        sim.Issue(new SetSecrecyCommand(f.Id, WeaponsShown.NeverShow, new SecrecyRules { HideRange = false }));
        sim.Issue(new SetAdvancedCommand(Side.Player, new List<string> { Tech.RailgunMk3 }));
        sim.Issue(new SetSalvoGuidanceCommand(f.Id, true));
        sim.Issue(new EjectShipSinkCommand(f.Id, ship.Id));
        sim.Issue(new LayMinesCommand(f.Id, 2));
        sim.StepMany(GameTime.TicksPerDay);
        Assert.Equal(WeaponsShown.NeverShow, f.Shown);
        Assert.True(f.DumbSalvos);
        var result = Lightlag.Sim.Replay.ReplayRunner.Run(Lightlag.Sim.Replay.ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(result.Exact, result.Message);
    }
}
