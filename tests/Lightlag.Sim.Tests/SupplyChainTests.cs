using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>The supply chain screen's sim side: stock rules, siege mode, routes, risk, flows and problems with fixes.</summary>
public class SupplyChainTests
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
    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    private static Body Colony(Simulation sim, string key, double popK)
    {
        var b = BodyNamed(sim, key);
        b.Owner = Side.Player;
        b.PopK = popK;
        b.OwnShare = 1;
        return b;
    }

    private static Fleet FreighterAt(Simulation sim, Body at)
    {
        var ship = Economy.NewShip(sim.World, Side.Player, "freighter");
        var f = Fleets.Create(sim.World, Side.Player, new List<Ship> { ship }, at.Position + new Vec2(Fleets.OrbitRadiusFor(at), 0));
        Fleets.EnterOrbit(f, at);
        ship.Position = f.Position + ship.FormationOffset;
        return f;
    }

    private static void Apply(Simulation sim, GameCommand c)
    {
        sim.Issue(c);
        sim.ApplyPending();
    }

    [Fact]
    public void Stock_rules_set_keep_export_and_import_lines()
    {
        var sim = NewGame();
        var kesh = Colony(sim, "kesh", 4);
        kesh.Stock["iron"] = 50;
        var auto = Colonies.Target(kesh, "iron");
        Apply(sim, new SetStockRuleCommand(Side.Player, kesh.Id, "iron", 30, 40, 10));
        Assert.Equal(30 + Colonies.Want(kesh, "iron"), Colonies.Target(kesh, "iron"), 6);
        Assert.Equal(10, Colonies.Held(kesh, "iron") - Colonies.SpareOf(kesh, "iron") - 30, 6); // export above 40: 10 spare of 50
        Assert.Equal(10, SupplyChain.AskLine(kesh, "iron"), 6);
        Assert.Contains("import <10", SupplyChain.RuleText(kesh, "iron"));
        Apply(sim, new SetStockRuleCommand(Side.Player, kesh.Id, "iron", null, null, null));
        Assert.Empty(kesh.StockRules);
        Assert.Equal(auto, Colonies.Target(kesh, "iron"), 6);
    }

    [Fact]
    public void Siege_mode_locks_exports_and_raises_keep_levels()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        verdant.Stock["spares"] = 500;
        var before = Colonies.Target(verdant, "spares");
        Assert.True(Colonies.SpareOf(verdant, "spares") > 0);
        Apply(sim, new SetSiegeModeCommand(Side.Player, verdant.Id, true));
        Assert.True(verdant.SiegeMode);
        Assert.Equal(0, Colonies.SpareOf(verdant, "spares"));
        Assert.True(Colonies.Target(verdant, "spares") > before);
    }

    [Fact]
    public void An_import_below_rule_brings_goods_the_planet_does_not_use()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        var kesh = Colony(sim, "kesh", 4);
        verdant.Stock["copper"] = 300;
        FreighterAt(sim, verdant);
        Apply(sim, new SetStockRuleCommand(Side.Player, kesh.Id, "copper", null, null, 25));
        for (var day = 0; day < 120 && Colonies.Held(kesh, "copper") < 1; day++) Days(sim, 1);
        Assert.True(Colonies.Held(kesh, "copper") >= 25);
        var flow = Assert.Single(SupplyChain.Flows(sim.World, Side.Player), f => f.Good == "copper");
        Assert.Equal(verdant.Id, flow.FromId);
        Assert.True(flow.PerDay > 0);
        Assert.Contains(SupplyChain.Routes(sim.World, Side.Player), r => r.Key == $"auto:{verdant.Id}:{kesh.Id}" && !r.Fixed);
    }

    [Fact]
    public void Siege_mode_on_the_source_stops_the_load()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        var kesh = Colony(sim, "kesh", 4);
        verdant.Stock["copper"] = 300;
        FreighterAt(sim, verdant);
        Apply(sim, new SetSiegeModeCommand(Side.Player, verdant.Id, true));
        Apply(sim, new SetStockRuleCommand(Side.Player, kesh.Id, "copper", null, null, 25));
        Days(sim, 5);
        Assert.DoesNotContain(sim.World.Shipments, s => s.Cargo.ContainsKey("copper"));
        Assert.Contains(SupplyChain.Problems(sim.World, Side.Player), p => p.Kind == ProblemKind.Short && p.Good == "copper" && p.BodyId == kesh.Id);
    }

    [Fact]
    public void A_route_setting_sends_loads_by_another_planet()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        var kesh = Colony(sim, "kesh", 4);
        var pale = Colony(sim, "pale", 2);
        kesh.Stock["rare-earths"] = 40;
        FreighterAt(sim, kesh);
        Apply(sim, new SetRouteCommand(Side.Player, kesh.Id, verdant.Id, null, pale.Id));
        Apply(sim, new FixedRunCommand(Side.Player, kesh.Id, verdant.Id, "rare-earths", 20));
        Days(sim, 0.5);
        var s = Assert.Single(sim.World.Shipments);
        Assert.Equal(pale.Id, s.ViaBodyId);
        var passed = false;
        for (var hour = 0; hour < 24 * 150 && Colonies.Held(verdant, "rare-earths") < 1; hour++)
        {
            sim.StepMany(GameTime.TicksPerDay / 24);
            passed |= sim.World.Shipments.Any(x => x.ViaDone);
        }
        Assert.True(passed);
        Assert.True(Colonies.Held(verdant, "rare-earths") >= 1);
        var row = Assert.Single(SupplyChain.Routes(sim.World, Side.Player), r => r.Fixed && r.FixedRunId != null && !r.AutoRun);
        Assert.Equal(pale.Id, row.ViaBodyId);
        Assert.Contains(SupplyChain.Book(sim.World, Side.Player).Deliveries, d => d.Good == "rare-earths" && d.RunId == row.FixedRunId);
    }

    [Fact]
    public void Rival_warships_near_a_run_and_losses_raise_its_risk()
    {
        var sim = NewGame();
        sim.Step();
        var w = sim.World;
        var verdant = BodyNamed(sim, "verdant");
        var kesh = Colony(sim, "kesh", 4);
        Assert.Equal(RouteRisk.Low, SupplyChain.RiskOf(w, Side.Player, kesh.Position, verdant.Position, null, false).Risk);
        var mid = kesh.Position + (verdant.Position - kesh.Position) * 0.5;
        w.Sightings.Add(new Sighting { SeenBy = Side.Player, FleetId = 9999, Name = "R-1", Ships = 2, Position = mid, Hour = w.Hour, Visible = true, Classes = new List<string> { "gun-frigate", "gun-frigate" } });
        Intel.Update(sim); // route risk reads the intel file, which takes in sightings each hour
        Assert.Equal(RouteRisk.Medium, SupplyChain.RiskOf(w, Side.Player, kesh.Position, verdant.Position, null, false).Risk);
        w.LossSpots.Add(new LossSpot(Side.Player, mid, w.Hour));
        var (risk, why) = SupplyChain.RiskOf(w, Side.Player, kesh.Position, verdant.Position, null, false);
        Assert.Equal(RouteRisk.High, risk);
        Assert.Contains("cargo lost", why);
        Assert.Equal(RouteRisk.Medium, SupplyChain.RiskOf(w, Side.Player, kesh.Position, verdant.Position, null, true).Risk);

        // A fixed run on it is listed under Risk, with fixes.
        Apply(sim, new FixedRunCommand(Side.Player, kesh.Id, verdant.Id, "iron", 10));
        var problem = Assert.Single(SupplyChain.Problems(w, Side.Player), p => p.Kind == ProblemKind.Risk);
        Assert.Contains(problem.Fixes, f => f.Command is SetFixedRunEveryCommand);
        var slower = problem.Fixes.First(f => f.Command is SetFixedRunEveryCommand).Command!;
        Apply(sim, slower);
        Assert.Equal(20, w.FixedRuns.Single().EveryDays);
    }

    [Fact]
    public void A_short_good_nobody_makes_offers_a_building_that_makes_it()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        foreach (var b in sim.World.Bodies) b.Stock.Remove("alloys");
        Economy.Of(sim.World, Side.Player).Industry = 1000;
        Apply(sim, new SetStockRuleCommand(Side.Player, verdant.Id, "alloys", null, null, 30));
        var problem = Assert.Single(SupplyChain.Problems(sim.World, Side.Player), p => p.Kind == ProblemKind.Short && p.Good == "alloys");
        Assert.Contains("No planet has alloys to spare", problem.Cause);
        var fix = Assert.Single(problem.Fixes, f => f.Command is BuildOnPlanetCommand);
        Assert.Equal("smelter", ((BuildOnPlanetCommand)fix.Command!).Kind);
        Assert.Contains("Industry", fix.Cost);
        var build = (BuildOnPlanetCommand)fix.Command!;
        Assert.Null(Planets.CanBuild(sim.World, Side.Player, build.BodyId, build.Kind));
        var at = sim.World.FindBody(build.BodyId)!;
        var pieces = at.Pieces.Count(p => p.Kind == "smelter");
        Apply(sim, build);
        Assert.Equal(pieces + 1, at.Pieces.Count(p => p.Kind == "smelter"));
        Assert.Contains(problem.Fixes, f => f.Ui == "chain:alloys");
    }

    [Fact]
    public void A_lost_freighter_counts_in_the_years_losses()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        var kesh = Colony(sim, "kesh", 4);
        kesh.Stock["rare-earths"] = 40;
        var f = FreighterAt(sim, kesh);
        Apply(sim, new FixedRunCommand(Side.Player, kesh.Id, verdant.Id, "rare-earths", 20));
        for (var hour = 0; hour < 24 * 20 && !sim.World.Shipments.Any(s => s.Loaded); hour++) sim.StepMany(GameTime.TicksPerDay / 24);
        Assert.Equal(1, SupplyChain.Haulers(sim.World, Side.Player).Busy);
        Combat.Destroy(sim, sim.World.ShipsOf(f).Single());
        sim.StepMany(GameTime.TicksPerDay / 24);
        var use = SupplyChain.Haulers(sim.World, Side.Player);
        Assert.Equal(1, use.LostShips);
        Assert.True(use.LostCargo > 0);
    }

    [Fact]
    public void Chain_filters_follow_recipes()
    {
        var alloys = SupplyChain.ChainOf("alloys");
        Assert.Contains("iron", alloys);
        Assert.Contains("copper", alloys);
        Assert.Contains("ammunition", alloys);
        Assert.DoesNotContain("water", alloys);
    }

    [Fact]
    public void Supply_commands_round_trip_and_work_while_paused()
    {
        var cmds = new GameCommand[]
        {
            new SetStockRuleCommand(Side.Player, 3, "iron", 10, null, 5),
            new SetSiegeModeCommand(Side.Player, 3, true),
            new SetRouteCommand(Side.Player, 3, 4, 7, 5),
            new SetFixedRunEveryCommand(Side.Player, 9, 12),
        };
        foreach (var c in cmds)
        {
            var json = System.Text.Json.JsonSerializer.Serialize<GameCommand>(c);
            Assert.Equal(c, System.Text.Json.JsonSerializer.Deserialize<GameCommand>(json));
            Assert.Null(c.Destination(NewGame().World));
        }
    }

    [Fact]
    public void Saved_games_keep_stock_rules_and_routes()
    {
        var sim = NewGame();
        var verdant = BodyNamed(sim, "verdant");
        var kesh = Colony(sim, "kesh", 4);
        Apply(sim, new SetStockRuleCommand(Side.Player, kesh.Id, "iron", 12, 30, null));
        Apply(sim, new SetSiegeModeCommand(Side.Player, kesh.Id, true));
        Apply(sim, new SetRouteCommand(Side.Player, kesh.Id, verdant.Id, null, Colony(sim, "pale", 2).Id));
        var json = System.Text.Json.JsonSerializer.Serialize(sim.World, Json.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<World>(json, Json.Options)!;
        var k = back.Bodies.Single(b => b.Id == kesh.Id);
        Assert.True(k.SiegeMode);
        Assert.Equal(12, k.StockRules["iron"].Keep);
        Assert.Single(Economy.Of(back, Side.Player).Supply.Routes);
    }
}
