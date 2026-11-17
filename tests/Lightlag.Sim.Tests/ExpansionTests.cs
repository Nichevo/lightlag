using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Expanding has a cost: survey first, settle to own, settlers dear while home isn't crowded, upkeep with distance.</summary>
public class ExpansionTests
{
    private static Simulation NewGame()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: new GameOptions { Budget = false, Events = EventRate.Off });
        sim.World.Minds.Clear();
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        Planets.EnsureStarted(sim.World);
        return sim;
    }

    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);

    [Fact]
    public void A_world_must_be_surveyed_before_settlers_can_go()
    {
        var sim = NewGame();
        var kesh = BodyNamed(sim, "kesh");
        Assert.Contains("needs a survey", Planets.CanSettle(sim.World, Side.Player, kesh.Id));
        sim.Issue(new SettleCommand(Side.Player, kesh.Id));
        sim.ApplyPending();
        Assert.DoesNotContain(sim.World.Orders, o => o.SettleBodyId == kesh.Id);
        kesh.Surveyed[(int)Side.Player] = true;
        Assert.Null(Planets.CanSettle(sim.World, Side.Player, kesh.Id));
    }

    [Theory]
    [InlineData(0.3, 3.0)]
    [InlineData(0.5, 3.0)]
    [InlineData(0.85, 1.0)]
    [InlineData(1.0, 0.5)]
    public void Settlers_cost_more_while_home_is_not_crowded(double fill, double factor)
    {
        var sim = NewGame();
        var home = Economy.ShipyardOf(sim.World, Side.Player)!;
        home.PopK = Planets.Cap(home) * fill;
        Assert.Equal(factor, Planets.CrowdingFactor(home), 6);
        Assert.Equal(GameData.ShipClass(Planets.SettlerClass).Cost * factor, Planets.SettlerPrice(sim.World, Side.Player), 1);
    }

    [Fact]
    public void The_settler_price_is_fixed_when_ordered_and_says_why()
    {
        var sim = NewGame();
        var w = sim.World;
        w.SurveyAll();
        var home = Economy.ShipyardOf(w, Side.Player)!;
        home.PopK = Planets.Cap(home) * 0.5;
        Assert.Contains("50 % full", Planets.SettlerPriceWhy(w, Side.Player));
        sim.Issue(new SettleCommand(Side.Player, BodyNamed(sim, "kesh").Id));
        sim.ApplyPending();
        var order = Assert.Single(w.Orders, o => o.SettleBodyId != null);
        Assert.Equal(GameData.ShipClass(Planets.SettlerClass).Cost * 3, Economy.CostOf(order), 1);
        home.PopK = Planets.Cap(home);
        Assert.Equal(GameData.ShipClass(Planets.SettlerClass).Cost * 3, Economy.CostOf(order), 1);
    }

    [Fact]
    public void Settlers_to_a_small_world_carry_only_as_many_as_fit()
    {
        var sim = NewGame();
        var w = sim.World;
        w.SurveyAll();
        var kesh = BodyNamed(sim, "kesh");
        var src = Planets.SettleSource(w, Side.Player, kesh.Id)!;
        Assert.True(src.PeopleK <= Planets.Cap(kesh) + 1e-9);
    }

    [Fact]
    public void Far_worlds_cost_upkeep_and_a_starbase_or_home_nearby_cuts_it()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Economy.HomeOf(w, Side.Player)!;
        var moon = w.Bodies.First(m => m.ParentId == home.Id && m.Owner == Side.Player);
        Assert.Equal(0, Planets.UpkeepOf(w, home));
        Assert.Equal(0, Planets.UpkeepOf(w, moon)); // next to home: free
        var far = w.Bodies.Where(b => b.Owner == null && Planets.Settleable(b))
            .OrderByDescending(b => Vec2.Distance(b.Position, home.Position)).First();
        far.Owner = Side.Player;
        far.PopK = 1;
        var au = Vec2.Distance(far.Position, home.Position) / Physics.MmPerAu;
        Assert.True(au > 1);
        Assert.Equal((au - Planets.D.UpkeepFreeAu) * Planets.D.UpkeepPerAu, Planets.UpkeepOf(w, far), 6);
        Assert.Equal(Planets.UpkeepOf(w, far), Planets.UpkeepPerDay(w, Side.Player), 6);
        // A working starbase over it makes it a hub.
        w.Starbases.Add(new Starbase { Id = w.NextId++, Side = Side.Player, BodyId = far.Id, ClassId = "station-platform", ReadyHour = w.Hour });
        Assert.Equal(0, Planets.UpkeepOf(w, far));
    }

    [Fact]
    public void Upkeep_comes_off_the_industry_earned()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Economy.HomeOf(w, Side.Player)!;
        var far = w.Bodies.Where(b => b.Owner == null && Planets.Settleable(b))
            .OrderByDescending(b => Vec2.Distance(b.Position, home.Position)).First();
        far.Owner = Side.Player;
        far.PopK = 1;
        var before = Economy.Of(w, Side.Player).Totals.IndustryEarned;
        sim.Step();
        var upkeep = Planets.UpkeepPerDay(w, Side.Player);
        Assert.True(upkeep > 0.05);
        Assert.Equal((Economy.IncomePerDay(w, Side.Player) - upkeep) / 24, Economy.Of(w, Side.Player).Totals.IndustryEarned - before, 6);
    }

    [Fact]
    public void A_gas_giant_is_taken_by_building_a_skimmer_over_it_once_surveyed()
    {
        var sim = NewGame();
        var w = sim.World;
        var giant = w.Bodies.First(b => b.Owner == null && !Planets.Settleable(b) && b.Type == "gas");
        Economy.Of(w, Side.Player).Researched.Add("helium-3-skimming");
        Economy.Of(w, Side.Player).Industry = 1000;
        Assert.Contains("needs a survey", Economy.CanBuildStation(w, Side.Player, giant.Id, Works.Skimmer));
        giant.Surveyed[(int)Side.Player] = true;
        Assert.Null(Economy.CanBuildStation(w, Side.Player, giant.Id, Works.Skimmer));
        Assert.NotNull(Economy.CanBuildStation(w, Side.Player, giant.Id, "relay")); // only a skimmer takes it
        sim.Issue(new BuildStationCommand(Side.Player, giant.Id, Works.Skimmer));
        sim.StepMany(2);
        Assert.Equal(Side.Player, giant.Owner);
        Assert.Contains(w.Stations, st => st.BodyId == giant.Id && st.Kind == Works.Skimmer);
    }
}
