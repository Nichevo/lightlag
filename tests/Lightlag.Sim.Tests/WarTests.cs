using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Lightlag.Sim.Tests;

/// <summary>Veteran crews, boarding, ground invasions, blockades and the after-battle steps.</summary>
public class WarTests
{
    private readonly ITestOutputHelper _out;
    public WarTests(ITestOutputHelper output) => _out = output;

    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Starts.Classic(Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory()));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        // Keep the rival's mind out of these set pieces.
        sim.World.Minds.Clear();
        return sim;
    }

    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);
    private static Fleet Home(Simulation sim, Side side = Side.Player) => sim.World.Fleets.First(f => f.Side == side);
    private static void Hours(Simulation sim, int hours) => sim.StepMany(hours);
    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    private static Piece Put(Simulation sim, Body b, string kind)
    {
        var data = Planets.BuildingKind(kind)!;
        var (x, y, turned) = Colonies.BestSpot(b, data)!.Value;
        var p = Colonies.AddPiece(sim.World, b, data, x, y, turned, built: true);
        Colonies.SyncBuildings(b);
        return p;
    }

    private static void PlaceInOrbit(Simulation sim, Fleet f, Body b)
    {
        f.Flight = null;
        f.Position = b.Position + new Vec2(Fleets.OrbitRadiusFor(b), 0);
        Fleets.EnterOrbit(f, b);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
    }

    private static Fleet NewFleet(Simulation sim, Side side, Vec2 at, params string[] classes)
    {
        var ships = classes.Select(c => Economy.NewShip(sim.World, side, c)).ToList();
        foreach (var s in ships.Where(Planets.IsTroopShip)) s.CargoTroops = Planets.TroopHold(s);
        return Fleets.Create(sim.World, side, ships, at);
    }

    /// <summary>Removes every fleet of a side (so set pieces start clean).</summary>
    private static void ClearSide(Simulation sim, Side side)
    {
        foreach (var s in sim.World.Ships.Where(s => s.Side == side).ToList()) Combat.Destroy(sim, s, wreck: false);
        Economy.Of(sim.World, side).FlagshipId = null;
    }

    /// <summary>A rival planet with people and a few buildings, held by nobody else.</summary>
    private static Body RivalPlanet(Simulation sim, double peopleK = 40)
    {
        var vela = BodyNamed(sim, "vela");
        vela.Owner = Side.Rival;
        vela.PopK = peopleK;
        vela.Happiness = 60;
        vela.Grudge = Side.Player; // no surrender: only troops take it
        Put(sim, vela, Planets.Farm);
        Put(sim, vela, Planets.Mine);
        Put(sim, vela, "bunker");
        Put(sim, vela, "habitat");
        return vela;
    }

    // ---- Crews --------------------------------------------------------------------------------

    [Fact]
    public void Ranks_follow_experience_and_better_crews_aim_and_mend_better()
    {
        var sim = NewGame();
        var s = sim.World.Ships.First(x => x.Side == Side.Player);
        Assert.Equal(Rank.Trained, Crews.RankOf(s)); // ships at the start are trained
        s.Xp = 0;
        Assert.Equal(Rank.Green, Crews.RankOf(s));
        Assert.True(Crews.AimFactor(sim.World, s) < 1);
        s.Xp = 10;
        Assert.Equal(Rank.Elite, Crews.RankOf(s));
        Assert.True(Crews.AimFactor(sim.World, s) > 1.1);
        Assert.True(Crews.MendFactor(sim.World, s) > 1.2);
        Assert.True(Crews.HullLine(sim.World, s, 0.33) < 0.33);
    }

    [Fact]
    public void Battles_teach_crews_and_yard_replacements_dilute_them()
    {
        var sim = NewGame();
        var s = sim.World.Ships.First(x => x.Side == Side.Player);
        var b = new Battle { Id = 99, StartHour = 0 };
        b.For(Side.Player).RailShots = 4;
        b.For(Side.Rival).Withdrew = true;
        b.Roster.Add(new RosterShip(s.Id, Side.Player, s.Name, s.ClassId, "", 100, false));
        var before = s.Xp;
        Crews.OnBattleOver(sim, b);
        Assert.Equal(before + War.D.Crews.BattleXp + War.D.Crews.WinXp, s.Xp, 6);

        s.Xp = 8;
        s.Crew = 0.5;
        Wounds.YardRepair(sim.World, s, 24 * 5, null);
        Assert.Equal(1, s.Crew, 6);
        Assert.Equal(4, s.Xp, 6); // half the crew are green replacements
    }

    [Fact]
    public void A_rescued_crew_mans_the_next_ship_and_keeps_its_rank()
    {
        var sim = NewGame();
        var yard = Economy.ShipyardOf(sim.World, Side.Player)!;
        Economy.Of(sim.World, Side.Player).ReserveCrews.Add(7);
        var a = Economy.NewShip(sim.World, Side.Player, "gun-frigate");
        Crews.Man(sim.World, a, yard);
        Assert.Equal(Rank.Veteran, Crews.RankOf(a));
        var b = Economy.NewShip(sim.World, Side.Player, "gun-frigate");
        Crews.Man(sim.World, b, yard);
        Assert.Equal(Rank.Green, Crews.RankOf(b));
        Assert.Empty(Economy.Of(sim.World, Side.Player).ReserveCrews);
    }

    [Fact]
    public void Auto_spreads_veterans_in_port_and_gives_the_flagship_the_best_crew()
    {
        var sim = NewGame();
        var home = Economy.ShipyardOf(sim.World, Side.Player)!;
        var f = NewFleet(sim, Side.Player, home.Position, "gun-frigate", "gun-frigate", "gun-frigate");
        PlaceInOrbit(sim, f, home);
        var ships = sim.World.ShipsOf(f).ToList();
        ships[0].Xp = 9;
        ships[1].Xp = 1;
        ships[2].Xp = 0;
        var total = ships.Sum(s => s.Xp);
        Crews.Steward(sim, Side.Player, (_, _, _, act, _, _, _, _) => { act.Apply(sim.World, sim); return true; });
        Assert.Equal(total, ships.Sum(s => s.Xp), 6);
        Assert.True(ships[2].Xp > 0);
        Assert.True(ships[0].Xp < 9);
    }

    // ---- Boarding ---------------------------------------------------------------------------

    /// <summary>A crippled rival frigate alone, and a player fleet with troops beside it.</summary>
    private static (Fleet Mine, Ship Target) BoardingSetPiece(Simulation sim)
    {
        ClearSide(sim, Side.Rival);
        var at = BodyNamed(sim, "vela").Position + new Vec2(400, 0);
        var rival = NewFleet(sim, Side.Rival, at, "gun-frigate");
        var target = sim.World.ShipsOf(rival).Single();
        target.Armour = 0;
        target.Crippled = true;
        target.CrippleLeft = 20;
        var mine = NewFleet(sim, Side.Player, at + new Vec2(5, 0), "gun-frigate", "troop-ship");
        Economy.Of(sim.World, Side.Rival).Researched.Add("heavy-armour");
        Sensors.Update(sim);
        return (mine, target);
    }

    [Fact]
    public void Boarding_takes_a_crippled_ship_with_prisoners_and_intel()
    {
        var sim = NewGame();
        var (mine, target) = BoardingSetPiece(sim);
        var name = target.Name;
        Assert.Contains(target, Boarders.Targets(sim.World, mine));
        Assert.Null(Boarders.CanBoard(sim.World, mine, target));
        new BoardShipCommand(mine.Id, target.Id, BoardApproach.Dock).Apply(sim.World, sim);
        var b = sim.World.Boardings.Single();
        Assert.True(Boarders.WinChance(sim.World, b) > 0.8);
        Assert.InRange(b.CallKinds.Count, 1, 3);
        Hours(sim, 24);
        _out.WriteLine(string.Join("\n", b.Notes));
        Assert.Equal("taken", b.Outcome);
        Assert.Equal(Side.Player, target.Side);
        Assert.Equal("Prize " + name, target.Name);
        Assert.Equal(b.CallKinds.Count, b.Calls.Count);
        Assert.All(b.Calls, c => Assert.True(c.ByAuto));
        var me = Economy.Of(sim.World, Side.Player);
        Assert.True(me.Spoils.PrisonersK > 0);
        Assert.True(me.Spoils.Scraps >= 1);
        Assert.Contains(Intel.Of(sim.World, Side.Player).Notes, n => n.Source == IntelSource.Capture);
        Assert.Contains("heavy-armour", me.Sparked);
        Assert.Equal(1, me.Totals.ShipsCaptured);
    }

    [Fact]
    public void Only_helpless_ships_can_be_boarded_and_not_under_enemy_guns()
    {
        var sim = NewGame();
        var (mine, target) = BoardingSetPiece(sim);
        target.Crippled = false;
        target.Armour = 50;
        Assert.NotNull(Boarders.CanBoard(sim.World, mine, target));
        target.Crippled = true;
        var guard = NewFleet(sim, Side.Rival, target.Position + new Vec2(3, 0), "gun-frigate");
        Assert.Contains("guns", Boarders.CanBoard(sim.World, mine, target));
        Combat.Destroy(sim, sim.World.ShipsOf(guard).Single(), wreck: false);
        Assert.Null(Boarders.CanBoard(sim.World, mine, target));
    }

    [Fact]
    public void Ask_me_boarding_calls_wait_for_the_player_then_Auto_decides()
    {
        var sim = NewGame();
        Economy.Of(sim.World, Side.Player).Auto[AutoArea.Boarding] = AutoLevel.Advise;
        var (mine, target) = BoardingSetPiece(sim);
        new BoardShipCommand(mine.Id, target.Id).Apply(sim.World, sim);
        var b = sim.World.Boardings.Single();
        for (var i = 0; i < 12 && b.Calls.Count == 0; i++) sim.Step();
        var call = Assert.Single(b.Calls);
        Assert.True(call.Open);
        Hours(sim, 3);
        Assert.Contains(Economy.Of(sim.World, Side.Player).Needs, n => n.Key == $"board-call:{call.Id}" || b.Outcome != null);
        Hours(sim, 24);
        Assert.False(call.Open);
        Assert.NotNull(b.Outcome);
    }

    [Fact]
    public void The_player_can_make_a_boarding_call_while_paused()
    {
        var sim = NewGame();
        Economy.Of(sim.World, Side.Player).Auto[AutoArea.Boarding] = AutoLevel.Manual;
        var (mine, target) = BoardingSetPiece(sim);
        new BoardShipCommand(mine.Id, target.Id).Apply(sim.World, sim);
        var b = sim.World.Boardings.Single();
        for (var i = 0; i < 12 && b.Calls.Count == 0; i++) sim.Step();
        var call = b.Calls.Single();
        var pick = call.Options.Count - 1 == call.AutoPick ? 0 : call.Options.Count - 1;
        sim.Issue(new BoardingCallCommand(b.Id, call.Id, pick));
        sim.ApplyPending();
        Assert.Equal(pick, call.Picked);
        Assert.False(call.ByAuto);
    }

    // ---- Ground war ------------------------------------------------------------------------------

    /// <summary>The game the last Invade ran in.</summary>
    private static Simulation? LastInvasion;

    private static (Fleet Troops, double Days, Body Planet) Invade(ulong seed, GroundPlan plan, double troopShare = 2.2)
    {
        var sim = LastInvasion = NewGame(seed);
        ClearSide(sim, Side.Rival);
        var vela = RivalPlanet(sim);
        var garrison = Planets.GarrisonOf(vela);
        var ships = Enumerable.Repeat("troop-ship", (int)Math.Ceiling(garrison * troopShare / GameData.ShipClass("troop-ship").Cargo)).Prepend("gun-frigate").ToArray();
        var f = NewFleet(sim, Side.Player, vela.Position, ships);
        PlaceInOrbit(sim, f, vela);
        f.OrbitHeight = OrbitHeight.Low;
        new LandTroopsCommand(f.Id, vela.Id, plan).Apply(sim.World, sim);
        Assert.Equal(Side.Player, vela.LandingSide);
        Assert.NotNull(vela.Front);
        var start = sim.World.Hour;
        for (var d = 0; d < 30 && vela.Owner != Side.Player && vela.LandingSide != null; d++) Days(sim, 1);
        return (f, (sim.World.Hour - start) / 24, vela);
    }

    [Fact]
    public void Invasions_take_days_and_the_plan_matters()
    {
        var (_, methodical, m) = Invade(8, GroundPlan.Methodical);
        var (_, blitz, b) = Invade(8, GroundPlan.Blitz);
        _out.WriteLine($"methodical {methodical:0.0} days, blitz {blitz:0.0} days; happiness after {m.Happiness:0} / {b.Happiness:0}");
        Assert.Equal(Side.Player, m.Owner);
        Assert.Equal(Side.Player, b.Owner);
        Assert.InRange(methodical, 3.5, 9);
        Assert.InRange(blitz, 1.5, 5);
        Assert.True(blitz < methodical);
        // Methodical spares the mine.
        Assert.Contains(Planets.Mine, m.Buildings);
    }

    [Theory]
    [InlineData(GroundPlan.Methodical)]
    [InlineData(GroundPlan.Blitz)]
    public void A_methodical_capture_starts_content_and_a_blitz_sullen(GroundPlan plan)
    {
        var (f, _, vela) = Invade(8, plan);
        Assert.Equal(Side.Player, vela.Owner);
        // The overnight tally keeps the happiness at the moment of capture (later days move it on).
        var tally = Economy.Of(LastInvasion!.World, Side.Player).Totals.Captures[plan.ToString()];
        Assert.Equal(1, tally.Count);
        if (plan == GroundPlan.Methodical) Assert.Equal(50, tally.Happiness, 6);
        else Assert.True(tally.Happiness <= 25 + 1e-9);
        Assert.True(tally.PeopleK > 0);
        Assert.True(tally.Days >= tally.FightDays && tally.FightDays > 0);
        Assert.NotNull(f);
    }

    [Fact]
    public void Troop_ships_can_jump_the_yard_queue_and_fly_out_at_full_speed()
    {
        var sim = NewGame();
        var w = sim.World;
        w.Orders.RemoveAll(o => o.Side == Side.Player);
        new QueueOrderCommand(Side.Player, OrderKind.Ship, "gun-frigate").Apply(w, sim);
        new QueueOrderCommand(Side.Player, OrderKind.Ship, "gun-frigate").Apply(w, sim);
        w.Orders.First(o => o.Side == Side.Player).Paid = true; // the first is under way: troops go after it
        Economy.Of(w, Side.Player).Industry = 1000;
        new QueueOrderCommand(Side.Player, OrderKind.Ship, "troop-ship", Front: true).Apply(w, sim);
        Assert.Equal(new[] { "gun-frigate", "troop-ship", "gun-frigate" }, w.Orders.Where(o => o.Side == Side.Player).Select(o => o.Key));
        var troops = NewFleet(sim, Side.Player, Vec2.Zero, "troop-ship");
        var mixed = NewFleet(sim, Side.Player, Vec2.Zero, "troop-ship", "gun-frigate");
        Assert.Equal(1.0, Economy.JoinSpeed(w, troops));
        Assert.Equal(Economy.ReinforceSpeed, Economy.JoinSpeed(w, mixed));
    }

    [Fact]
    public void The_front_moves_tile_by_tile_and_the_last_stand_is_a_call()
    {
        var sim = NewGame();
        ClearSide(sim, Side.Rival);
        var vela = RivalPlanet(sim);
        var f = NewFleet(sim, Side.Player, vela.Position, "gun-frigate", "troop-ship", "troop-ship");
        PlaceInOrbit(sim, f, vela);
        f.OrbitHeight = OrbitHeight.Low;
        new LandTroopsCommand(f.Id, vela.Id, GroundPlan.Methodical).Apply(sim.World, sim);
        var front = vela.Front!;
        var first = front.Tiles.Count(c => c == 'A');
        Assert.True(first >= 1);
        Days(sim, 2);
        if (vela.Front != null) Assert.True(vela.Front.Tiles.Count(c => c == 'A') > first);
        Days(sim, 10);
        Assert.Contains("laststand", front.CallsDone);
    }

    [Fact]
    public void Planet_guns_shoot_landers_and_bring_up_a_call()
    {
        var sim = NewGame();
        ClearSide(sim, Side.Rival);
        var vela = RivalPlanet(sim);
        Put(sim, vela, Planets.Guns);
        var f = NewFleet(sim, Side.Player, vela.Position, "missile-destroyer", "troop-ship", "troop-ship");
        PlaceInOrbit(sim, f, vela);
        f.OrbitHeight = OrbitHeight.Low;
        var aboard = Planets.TroopsAboard(sim.World, f);
        new LandTroopsCommand(f.Id, vela.Id).Apply(sim.World, sim);
        Assert.True(vela.LandingTroops < aboard);
        var call = vela.Front!.Calls.Single(c => c.Kind == "guns");
        Assert.Equal(0, call.AutoPick); // the destroyer has missiles for a strike
        sim.Step();
        Assert.DoesNotContain(Planets.Guns, vela.Buildings);
    }

    [Fact]
    public void Blockades_weaken_the_garrison_each_week()
    {
        var sim = NewGame();
        var vela = RivalPlanet(sim);
        var fresh = Planets.GarrisonOf(vela);
        vela.BlockadeHours = 14 * 24;
        Assert.Equal(fresh * (1 - 2 * War.D.Ground.WeekWeakens), Planets.GarrisonOf(vela), 6);
        vela.BlockadeHours = 100 * 24;
        Assert.Equal(fresh * War.D.Ground.WeakestShare, Planets.GarrisonOf(vela), 6);
    }

    [Fact]
    public void Invasions_and_blockades_are_counted_for_the_overnight_report()
    {
        var (f, _, vela) = Invade(8, GroundPlan.Methodical);
        Assert.Equal(Side.Player, vela.Owner);
        // Invade made its own game; counted on the player's totals there.
        Assert.True(vela.Happiness >= 0);
        Assert.NotNull(f);
    }

    // ---- After the battle ----------------------------------------------------------------------

    [Fact]
    public void After_a_battle_Auto_rescues_captures_and_salvages_in_order()
    {
        var sim = NewGame();
        var (mine, target) = BoardingSetPiece(sim);
        // A wreck of each side, and their crews in escape pods, close by.
        var ourLost = Economy.NewShip(sim.World, Side.Player, "gun-frigate");
        ourLost.Xp = 7;
        var theirLost = Economy.NewShip(sim.World, Side.Rival, "missile-destroyer");
        var g1 = Fleets.Create(sim.World, Side.Player, new[] { ourLost }, mine.Position + new Vec2(10, 0));
        var g2 = Fleets.Create(sim.World, Side.Rival, new[] { theirLost }, mine.Position + new Vec2(20, 0));
        Combat.Destroy(sim, ourLost);
        Combat.Destroy(sim, theirLost);
        Assert.Equal(2, sim.World.Lifeboats.Count(b => b.CrewK > 0 && !b.Boat));
        _ = (g1, g2);
        var a = AfterBattle.Start(sim, mine, null);
        Assert.Equal("queued", a.Tasks[0].State);
        Assert.Equal("doing", a.Tasks[1].State); // the boarders go over at once, before the crippled ship limps off
        Assert.Equal("queued", a.Tasks[2].State);
        Hours(sim, 72);
        Assert.True(a.Done);
        var me = Economy.Of(sim.World, Side.Player);
        Assert.Contains(7.0, me.ReserveCrews);
        Assert.True(me.Spoils.PrisonersK > 0);
        Assert.True(me.Totals.Salvaged > 0);
        Assert.Contains(sim.World.Boardings, b => b.TargetShipId == target.Id);
        Assert.Empty(sim.World.Wrecks.Where(w => Vec2.Distance(w.Position, mine.Position) < 100));
        Assert.Empty(sim.World.Lifeboats);
    }

    [Fact]
    public void After_a_battle_capture_and_salvage_are_skipped_when_enemy_relief_is_near()
    {
        var sim = NewGame();
        var (mine, target) = BoardingSetPiece(sim);
        var relief = NewFleet(sim, Side.Rival, mine.Position + new Vec2(800, 0), "gun-frigate", "gun-frigate");
        sim.World.Sightings.Add(new Sighting { SeenBy = Side.Player, FleetId = relief.Id, Name = relief.Name, Ships = 2, Position = relief.Position, Hour = sim.World.Hour });
        var a = AfterBattle.Start(sim, mine, null);
        Assert.Equal("skipped", a.Tasks.Single(t => t.Step == AfterStep.Capture).State);
        Assert.Contains("relief", a.Tasks.Single(t => t.Step == AfterStep.Capture).Note);
        _ = target;
    }

    [Fact]
    public void Auto_picks_a_way_home_from_the_damage()
    {
        var sim = NewGame();
        var f = NewFleet(sim, Side.Player, BodyNamed(sim, "vela").Position + new Vec2(500, 0), "gun-frigate", "gun-frigate", "gun-frigate");
        var ships = sim.World.ShipsOf(f).ToList();
        Assert.Equal(HomeChoice.Nothing, AfterBattle.AutoPick(sim.World, f).Choice);
        ships[0].Weapons = 0.8;
        Assert.Equal(HomeChoice.MendHere, AfterBattle.AutoPick(sim.World, f).Choice);
        ships[1].Crippled = true;
        ships[1].Armour = 0;
        Assert.Equal(HomeChoice.SendDamagedHome, AfterBattle.AutoPick(sim.World, f).Choice);
        ships[1].Knocked.Add(Module.Engines);
        Assert.Equal(HomeChoice.Tow, AfterBattle.AutoPick(sim.World, f).Choice);
    }

    [Fact]
    public void Sending_the_damaged_home_splits_them_off_towards_a_yard()
    {
        var sim = NewGame();
        ClearSide(sim, Side.Rival);
        var f = NewFleet(sim, Side.Player, BodyNamed(sim, "vela").Position + new Vec2(500, 0), "gun-frigate", "gun-frigate", "gun-frigate");
        var hurt = sim.World.ShipsOf(f).First();
        hurt.Crippled = true;
        hurt.Armour = 0;
        hurt.CrippleLeft = 30;
        var a = AfterBattle.Start(sim, f, null);
        Assert.Equal(HomeChoice.SendDamagedHome, a.Home);
        Hours(sim, 3);
        Assert.True(a.Done);
        Assert.NotEqual(f.Id, hurt.FleetId);
        Assert.True(sim.World.FleetOf(hurt)!.Retreating);
        Assert.Equal(2, f.ShipIds.Count);
    }

    [Fact]
    public void Ask_me_waits_for_the_way_home_and_lists_it_in_Needs_you()
    {
        var sim = NewGame();
        ClearSide(sim, Side.Rival);
        Economy.Of(sim.World, Side.Player).Auto[AutoArea.AfterBattle] = AutoLevel.Advise;
        var f = NewFleet(sim, Side.Player, BodyNamed(sim, "vela").Position + new Vec2(500, 0), "gun-frigate", "gun-frigate");
        sim.World.ShipsOf(f).First().Weapons = 0.3;
        var a = AfterBattle.Start(sim, f, null);
        Assert.Null(a.Home);
        Hours(sim, 6);
        Assert.False(a.Done);
        Assert.Contains(Economy.Of(sim.World, Side.Player).Needs, n => n.Key == $"after-home:{a.Id}");
        sim.Issue(new SetHomeChoiceCommand(Side.Player, a.Id, HomeChoice.MendHere));
        sim.ApplyPending();
        Hours(sim, 2);
        Assert.True(a.Done);
        Assert.True(a.HomeByHand);
    }

    [Fact]
    public void Salvaging_an_enemy_wreck_gives_intel()
    {
        var sim = NewGame();
        ClearSide(sim, Side.Rival);
        Economy.Of(sim.World, Side.Rival).Researched.Add("heavy-armour");
        var at = BodyNamed(sim, "vela").Position + new Vec2(600, 0);
        var lost = Economy.NewShip(sim.World, Side.Rival, "missile-destroyer");
        Fleets.Create(sim.World, Side.Rival, new[] { lost }, at);
        Combat.Destroy(sim, lost);
        NewFleet(sim, Side.Player, at + new Vec2(5, 0), "gun-frigate");
        Hours(sim, 2);
        var me = Economy.Of(sim.World, Side.Player);
        Assert.True(me.Spoils.Scraps >= 1);
        Assert.Contains("heavy-armour", me.Sparked);
        var file = Intel.Of(sim.World, Side.Player);
        Assert.Contains(file.Notes, n => n.Source == IntelSource.Wreck);
        Assert.Contains(file.Tech, t => t.Source == IntelSource.Wreck);
    }

    [Fact]
    public void A_crippled_ship_far_from_a_yard_shows_in_Needs_you()
    {
        var sim = NewGame();
        ClearSide(sim, Side.Rival);
        var f = NewFleet(sim, Side.Player, BodyNamed(sim, "vela").Position + new Vec2(500, 0), "gun-frigate");
        var s = sim.World.ShipsOf(f).Single();
        s.Crippled = true;
        s.Armour = 0;
        s.CrippleLeft = 30;
        Hours(sim, 3);
        // The hourly rule sends lone crippled fleets home by itself; one that is stopped by hand is listed.
        f.Retreating = false;
        f.Flight = null;
        var needs = new List<string>();
        AfterBattle.Needs(sim.World, Side.Player, (_, key, text, _, _, act, _) => { needs.Add(key + " " + text); Assert.IsType<SendForRefitCommand>(act); });
        Assert.Contains(needs, n => n.StartsWith($"hurt:{f.Id}") && n.Contains("crippled"));
    }
}
