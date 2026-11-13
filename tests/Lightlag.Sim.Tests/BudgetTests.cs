using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>
/// The Industry budget: income split by share into six pots that add up to the side's Industry; orders draw only
/// from their own pot; items in a pot go in the player's order and can be paused; goals hold a pot; full pots
/// overflow; Reserve can be sent across; cut-off places roll their share to Reserve; the rival runs the same
/// budget from its personality's leanings, and it all replays exactly.
/// </summary>
public class BudgetTests
{
    private static Simulation NewGame(ulong seed = 8, GameOptions? options = null)
    {
        var sim = Starts.Classic(Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: options ?? new GameOptions { Events = EventRate.Off }));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);
    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));
    private static BudgetState Book(Simulation sim, Side side = Side.Player) => Budget.Of(sim.World, side);

    /// <summary>Sets every pot's savings (test setup): the side's Industry becomes their sum.</summary>
    private static void Fill(Simulation sim, params (Pot Pot, double Amount)[] pots)
    {
        var bs = Book(sim);
        foreach (var p in bs.Pots) p.Saved = 0;
        foreach (var (pot, amount) in pots) bs.Pots[(int)pot].Saved = amount;
        Economy.Of(sim.World, Side.Player).Industry = bs.Pots.Sum(p => p.Saved);
    }

    private static void Manual(Simulation sim) => Array.ForEach(sim.World.Sides.ToArray(), s => s.Auto[AutoArea.Budget] = AutoLevel.Manual);

    [Fact]
    public void Income_is_split_by_share_and_the_pots_add_up_to_the_Industry()
    {
        var sim = NewGame();
        Manual(sim);
        var bs = Book(sim);
        Assert.Equal(new double[] { 30, 25, 15, 10, 10, 10 }, bs.Pots.Select(p => p.Share));
        Assert.Equal("Stations and defences", bs.Pots[(int)Pot.Stations].Name);
        Fill(sim);
        Budget.Earn(sim.World, Side.Player, 100);
        Assert.Equal(new double[] { 30, 25, 15, 10, 10, 10 }, bs.Pots.Select(p => Math.Round(p.Saved, 6)));
        sim.StepMany(GameTime.TicksPerDay);
        var me = Economy.Of(sim.World, Side.Player);
        Assert.Equal(me.Industry, bs.Pots.Sum(p => p.Saved), 6);
        Assert.True(bs.Pots[(int)Pot.Ships].Saved > 30);
        Assert.Empty(sim.CheckRules());
    }

    [Fact]
    public void A_ship_waits_on_its_own_pot_and_says_which_and_how_long()
    {
        var sim = NewGame();
        Manual(sim);
        var w = sim.World;
        var cost = GameData.ShipClass("gun-frigate").Cost;
        Fill(sim, (Pot.Ships, cost / 2), (Pot.Buildings, 20));
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, "gun-frigate"));
        sim.StepMany(2);
        var order = w.Orders.Single(o => o.Side == Side.Player && o.Kind == OrderKind.Ship);
        Assert.False(order.Paid); // a destroyer can never eat the farm money
        Assert.Contains("Ships needs", order.Waiting);
        Assert.Contains(" d)", order.Waiting);
        Assert.True(Book(sim).Pots[(int)Pot.Buildings].Saved > 19); // under its full mark, so nothing overflows
        // Send it Reserve: one click.
        Book(sim).Pots[(int)Pot.Reserve].Saved = cost;
        Economy.Of(w, Side.Player).Industry += cost;
        sim.Issue(new MoveIndustryCommand(Side.Player, Pot.Reserve, Pot.Ships, -1));
        sim.StepMany(2);
        Assert.True(order.Paid);
        Assert.True(Book(sim).Pots[(int)Pot.Reserve].Saved + Book(sim).Pots[(int)Pot.Ships].Saved < cost / 2 + 1); // all of Reserve went in; what Ships has left over its full mark flows back
        Assert.Equal(cost, Book(sim).Pots[(int)Pot.Ships].Spent, 0); // plus its part of an hour's upkeep
        // Called off: the Industry goes back to its pot.
        var ships = Book(sim).Pots[(int)Pot.Ships].Saved;
        sim.Issue(new CancelOrderCommand(Side.Player, order.Id));
        sim.ApplyPending();
        Assert.Equal(ships + cost, Book(sim).Pots[(int)Pot.Ships].Saved, 6);
        Assert.Empty(sim.CheckRules());
    }

    [Fact]
    public void Settler_ships_and_freighters_come_out_of_Transport()
    {
        Assert.Equal(Pot.Transport, Budget.PotOf(new WorkOrder { Kind = OrderKind.Ship, Key = "freighter" }));
        Assert.Equal(Pot.Transport, Budget.PotOf(new WorkOrder { Kind = OrderKind.Ship, Key = "colony-ship", SettleBodyId = 3 }));
        Assert.Equal(Pot.Ships, Budget.PotOf(new WorkOrder { Kind = OrderKind.Ship, Key = "troop-ship" }));
        Assert.Equal(Pot.Ships, Budget.PotOf(new WorkOrder { Kind = OrderKind.Ship, Key = "missile-destroyer" }));
    }

    [Fact]
    public void Items_in_a_pot_go_in_the_players_order_and_a_paused_one_passes_its_turn_down()
    {
        var sim = NewGame();
        Manual(sim);
        var w = sim.World;
        var verdant = BodyNamed(sim, "verdant");
        var mine = Planets.BuildingKind(Planets.Mine)!;
        var farm = Planets.BuildingKind(Planets.Farm)!;
        Fill(sim);
        sim.Issue(new BuildOnPlanetCommand(Side.Player, verdant.Id, Planets.Mine, Queue: true));
        sim.Issue(new BuildOnPlanetCommand(Side.Player, verdant.Id, Planets.Farm, Queue: true));
        sim.ApplyPending();
        var first = verdant.Pieces.Single(p => p.Waiting && p.Kind == Planets.Mine);
        var second = verdant.Pieces.Single(p => p.Waiting && p.Kind == Planets.Farm);
        var items = Budget.Items(w, Side.Player).Where(i => i.Pot == Pot.Buildings).ToList();
        Assert.Equal(new[] { Budget.PieceKey(verdant, first), Budget.PieceKey(verdant, second) }, items.Select(i => i.Key));
        // Move the farm to the top: it is the one started.
        sim.Issue(new RankItemCommand(Side.Player, Budget.PieceKey(verdant, second), -1));
        sim.ApplyPending();
        Assert.Equal(second, Budget.NextPiece(w, verdant));
        // Pause it: it keeps its place, and the mine goes first.
        sim.Issue(new PauseItemCommand(Side.Player, Budget.PieceKey(verdant, second), true));
        sim.ApplyPending();
        Assert.Equal(first, Budget.NextPiece(w, verdant));
        Assert.Equal(Budget.PieceKey(verdant, second), Budget.Items(w, Side.Player).First(i => i.Pot == Pot.Buildings).Key);
        Assert.Equal("paused in the Industry budget", Colonies.QueuedWhy(w, verdant, second));
        Fill(sim, (Pot.Buildings, mine.Cost + farm.Cost + 5));
        sim.StepMany((int)Math.Ceiling(2 / GameTime.HoursPerTick));
        Assert.False(first.Waiting);
        Assert.True(second.Waiting);
        Assert.Empty(sim.CheckRules());
    }

    [Fact]
    public void An_item_above_holds_its_cost_and_a_one_off_purchase_waits_behind_it()
    {
        var sim = NewGame();
        Manual(sim);
        var w = sim.World;
        var verdant = BodyNamed(sim, "verdant");
        var farm = Planets.BuildingKind(Planets.Farm)!;
        Fill(sim);
        sim.Issue(new BuildOnPlanetCommand(Side.Player, verdant.Id, Planets.Farm, Queue: true));
        sim.ApplyPending();
        Fill(sim, (Pot.Buildings, farm.Cost - 1));
        Assert.Equal(0, Budget.Free(w, Side.Player, Pot.Buildings), 6); // all of it is held for the farm
        Assert.Contains("Buildings needs", Budget.ShortOf(w, Side.Player, Pot.Buildings, 10));
    }

    [Fact]
    public void A_goal_holds_its_pot_gives_a_date_and_clears_when_paid()
    {
        var sim = NewGame();
        Manual(sim);
        var w = sim.World;
        var home = BodyNamed(sim, "verdant");
        var platform = GameData.ShipClass("station-platform").Cost;
        Fill(sim, (Pot.Stations, platform / 2), (Pot.Reserve, platform));
        var key = $"starbase:{home.Id}:station-platform";
        sim.Issue(new SetGoalCommand(Side.Player, Pot.Stations, key, "Platform at Verdant", platform, home.Id));
        sim.ApplyPending();
        // Nothing else may spend it while it saves.
        Assert.Equal(0, Budget.Free(w, Side.Player, Pot.Stations), 6);
        Assert.StartsWith("Ready in", Budget.GoalLine(w, Side.Player, Pot.Stations));
        Assert.NotNull(Starbases.CanBuild(w, Side.Player, home.Id, "station-platform"));
        // Add from Reserve: it is ready now, and building it pays the goal off.
        sim.Issue(new MoveIndustryCommand(Side.Player, Pot.Reserve, Pot.Stations, platform / 2));
        sim.ApplyPending();
        Assert.Null(Starbases.CanBuild(w, Side.Player, home.Id, "station-platform"));
        sim.Issue(new BuildStarbaseCommand(Side.Player, home.Id, "station-platform"));
        sim.ApplyPending();
        Assert.Single(w.Starbases, s => s.Side == Side.Player);
        Assert.Null(Book(sim).Pots[(int)Pot.Stations].GoalKey);
        Assert.Empty(sim.CheckRules());
    }

    [Fact]
    public void A_full_pot_overflows_into_the_nominated_pot()
    {
        var sim = NewGame();
        Manual(sim);
        var w = sim.World;
        Fill(sim, (Pot.Refits, 5000));
        sim.StepMany((int)Math.Ceiling(1 / GameTime.HoursPerTick));
        var full = Budget.FullAt(w, Side.Player, Pot.Refits);
        Assert.Equal(full, Book(sim).Pots[(int)Pot.Refits].Saved, 3);
        Assert.True(Book(sim).Pots[(int)Pot.Reserve].Saved > 5000 - full - 1);
        sim.Issue(new SetOverflowCommand(Side.Player, Pot.Ships));
        Fill(sim, (Pot.Refits, 5000));
        sim.StepMany((int)Math.Ceiling(1 / GameTime.HoursPerTick));
        Assert.True(Book(sim).Pots[(int)Pot.Ships].Saved > 5000 - full - 1);
    }

    [Fact]
    public void A_blockaded_place_pauses_and_its_share_rolls_to_Reserve_unless_held()
    {
        var sim = NewGame();
        Manual(sim);
        var w = sim.World;
        var verdant = BodyNamed(sim, "verdant");
        Fill(sim);
        sim.Issue(new BuildOnPlanetCommand(Side.Player, verdant.Id, Planets.Farm, Queue: true));
        sim.ApplyPending();
        // An enemy fleet over Verdant.
        var enemy = w.Fleets.First(f => f.Side == Side.Rival && f.ShipIds.Count > 0);
        enemy.Flight = null;
        enemy.Position = verdant.Position + new Vec2(Fleets.OrbitRadiusFor(verdant), 0);
        Fleets.EnterOrbit(enemy, verdant);
        foreach (var s in w.ShipsOf(enemy)) s.Position = enemy.Position + s.FormationOffset;
        Assert.True(Economy.Blockaded(w, verdant));
        var item = Budget.Items(w, Side.Player).Single(i => i.Pot == Pot.Buildings);
        Assert.True(item.Blocked);
        var reserve = Book(sim).Pots[(int)Pot.Reserve].Saved;
        var buildings = Book(sim).Pots[(int)Pot.Buildings].Saved;
        Budget.Earn(w, Side.Player, 24);
        Assert.Equal(0, Book(sim).Pots[(int)Pot.Buildings].Saved - buildings, 6); // its whole share went to the cut-off farm
        Assert.Equal(24 * 0.35, Book(sim).Pots[(int)Pot.Reserve].Saved - reserve, 6);
        // Hold it for Verdant: it stays in the pot, kept for the place.
        sim.Issue(new SetPlaceModeCommand(Side.Player, verdant.Id, PlaceMode.Hold));
        sim.ApplyPending();
        Budget.Earn(w, Side.Player, 24);
        Assert.Equal(24 * 0.25, Book(sim).Pots[(int)Pot.Buildings].Saved - buildings, 6);
        Assert.Equal(24 * 0.25, Book(sim).Earmarks.Single().Amount, 6);
    }

    [Fact]
    public void A_share_set_by_hand_locks_the_pot_and_the_others_make_up_the_rest()
    {
        var sim = NewGame();
        sim.Issue(new SetBudgetShareCommand(Side.Player, Pot.Stations, 30));
        sim.ApplyPending();
        var bs = Book(sim);
        Assert.Equal(30, bs.Pots[(int)Pot.Stations].Share);
        Assert.True(bs.Pots[(int)Pot.Stations].Locked);
        Assert.Equal(100, bs.Pots.Sum(p => p.Share), 6);
        Assert.All(bs.Pots, p => Assert.Equal(Math.Round(p.Share), p.Share));
        sim.Issue(new RenamePotCommand(Side.Player, Pot.Stations, "Forts"));
        sim.ApplyPending();
        Assert.Equal("Forts", bs.Pots[(int)Pot.Stations].Name);
    }

    [Fact]
    public void Auto_moves_shares_towards_what_is_queued_and_Advise_offers_them()
    {
        var sim = NewGame();
        var w = sim.World;
        for (var i = 0; i < 4; i++) sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, "missile-destroyer"));
        sim.ApplyPending();
        var proposed = Budget.Propose(w, Side.Player);
        Assert.NotNull(proposed);
        Assert.True(proposed![(int)Pot.Ships] > 30);
        Assert.Equal(100, proposed.Sum(), 6);
        // Locked pots are left alone.
        Book(sim).Pots[(int)Pot.Ships].Locked = true;
        var again = Budget.Propose(w, Side.Player);
        Assert.True(again == null || again[(int)Pot.Ships] == 30);
        Book(sim).Pots[(int)Pot.Ships].Locked = false;
        w.Sides.Single(s => s.Side == Side.Player).Auto[AutoArea.Budget] = AutoLevel.Advise;
        Days(sim, 1);
        Assert.Contains(Economy.Of(w, Side.Player).Needs, n => n.Key == "budget:shares" && n.Act is SetBudgetSharesCommand);
        w.Sides.Single(s => s.Side == Side.Player).Auto[AutoArea.Budget] = AutoLevel.Auto;
        Days(sim, 2);
        Assert.True(Book(sim).Pots[(int)Pot.Ships].Share > 30);
        Assert.NotNull(Book(sim).LastMove);
    }

    [Fact]
    public void The_rival_starts_from_its_personalitys_shares()
    {
        var sim = NewGame(options: new GameOptions(new List<AiSetup> { new(Side.Rival, Difficulty.Normal, Personality.Turtle) }));
        Assert.Equal(new double[] { 20, 20, 30, 10, 5, 15 }, Book(sim, Side.Rival).Pots.Select(p => p.Share));
    }

    [Fact]
    public void With_the_rule_off_everything_spends_the_one_pool()
    {
        var sim = NewGame(options: new GameOptions { Events = EventRate.Off, Budget = false });
        var w = sim.World;
        Economy.Of(w, Side.Player).Industry = 1000;
        Assert.Equal(1000, Budget.Free(w, Side.Player, Pot.Stations));
        Assert.Null(Economy.Of(w, Side.Player).Budget);
    }

    [Fact]
    public void Computer_games_with_the_budget_build_and_replay_exactly()
    {
        var options = GameOptions.AiVsAi() with { Events = EventRate.Off };
        var a = Simulation.NewGame(Scenarios.TestSystem, 5, GameLog.Memory(), options: options);
        var b = Simulation.NewGame(Scenarios.TestSystem, 5, GameLog.Memory(), options: options);
        a.RuleCheckEveryTicks = b.RuleCheckEveryTicks = 24;
        a.StrictRules = b.StrictRules = true;
        a.StepMany(GameTime.TicksPerDay * 60);
        b.StepMany(GameTime.TicksPerDay * 60);
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            Assert.True(Economy.Of(a.World, side).Totals.ShipsBuilt > 0);
            Assert.True(Budget.Of(a.World, side).Pots.Sum(p => p.Spent) > 0);
            Assert.Equal(Economy.Of(a.World, side).Industry, Budget.Of(a.World, side).Pots.Sum(p => p.Saved), 3);
        }
        static string World(Simulation s) => System.Text.RegularExpressions.Regex.Replace(s.ToSave().ToJson(), "\"savedAtUtc\": \"[^\"]*\"", "");
        Assert.Equal(World(a), World(b));
    }
}
