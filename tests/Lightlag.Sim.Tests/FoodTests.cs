using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Food per world: each world eats from its own store, and worlds without farms live on food shipped in.</summary>
public class FoodTests
{
    private static Simulation NewGame()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 3, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        Planets.EnsureStarted(sim.World);
        return sim;
    }

    private static (Body Home, Body Moon) HomeAndMoon(World w)
    {
        var home = Economy.HomeOf(w, Side.Player)!;
        var moon = w.Bodies.First(m => m.ParentId == home.Id && m.Kind == BodyKind.Moon && m.Owner == Side.Player);
        return (home, moon);
    }

    [Fact]
    public void A_world_without_farms_does_not_eat_from_home()
    {
        var sim = NewGame();
        var (home, moon) = HomeAndMoon(sim.World);
        Assert.True(Planets.FoodMadePerDay(moon) <= 0, "the start moon is a mining outpost with no farm");
        moon.FoodStock = 0;
        home.FoodStock = 500;
        sim.Step();
        Assert.True(moon.HungryHours > 0, "the moon goes hungry at once with an empty store");
        Assert.True(home.HungryHours == 0);
        Assert.True(home.FoodStock > 0);
    }

    [Fact]
    public void Freighters_carry_food_to_a_world_without_farms()
    {
        var sim = NewGame();
        var w = sim.World;
        var (home, moon) = HomeAndMoon(w);
        moon.FoodStock = 0.05;
        home.FoodStock = Colonies.StoreCap(home, Colonies.Food);
        var before = SupplyChain.Book(w, Side.Player).Deliveries.Count(d => d.Good == Colonies.Food && d.ToBodyId == moon.Id);
        sim.StepMany(20 * GameTime.TicksPerDay);
        Assert.True(SupplyChain.Book(w, Side.Player).Deliveries.Count(d => d.Good == Colonies.Food && d.ToBodyId == moon.Id) > before,
            "food was shipped to the moon");
        Assert.True(moon.FoodStock > 0.05);
    }

    [Fact]
    public void Running_out_of_food_shows_in_needs_you_and_turns_urgent_in_the_last_days()
    {
        var sim = NewGame();
        var w = sim.World;
        var (home, moon) = HomeAndMoon(w);
        sim.Issue(new SetAutoTransportCommand(Side.Player, false));
        sim.ApplyPending();
        moon.FoodStock = Planets.FoodNeedPerDay(moon) * 2; // two days left
        Stewards.Update(sim);
        var need = Assert.Single(Economy.Of(w, Side.Player).Needs, n => n.Key == $"chain:short:{moon.Id}:food");
        Assert.Equal(NeedLevel.Urgent, need.Level);
        Assert.Contains("Food runs out at", need.Text);
    }

    [Fact]
    public void Settlers_land_with_food_for_their_first_weeks()
    {
        var sim = NewGame();
        var w = sim.World;
        var kesh = w.Bodies.Single(b => b.Key == "kesh");
        var fleet = w.Fleets.First(f => f.Side == Side.Player);
        var ship = w.ShipsOf(fleet).First();
        ship.CargoPeople = 5;
        typeof(Planets).GetMethod("Unload", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { sim, fleet, kesh, new List<Ship> { ship }, 5.0 });
        Assert.Equal(Side.Player, kesh.Owner);
        var days = kesh.FoodStock / Planets.FoodNeedPerDay(kesh);
        Assert.InRange(days, Planets.D.SettlerFoodDays - 0.01, Planets.D.SettlerFoodDays + 0.01);
    }

    [Fact]
    public void A_flight_with_nothing_to_fly_ends_so_a_freighter_at_home_is_free_again()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Economy.HomeOf(w, Side.Player)!;
        var freighter = Colonies.FreeHaulers(w, Side.Player).First();
        // Planned while already where it would stop: no legs (this once left the freighter "flying" for ever).
        freighter.Flight = new Flight { TargetBodyId = home.Id, Target = home.Position, Arrival = ArrivalStyle.Brake, HoldAfter = HoldStyle.Orbit, DepartHour = w.Hour, ArriveHour = w.Hour };
        Assert.DoesNotContain(Colonies.FreeHaulers(w, Side.Player), f => f.Id == freighter.Id);
        sim.Step();
        Assert.Null(freighter.Flight);
        Assert.Contains(Colonies.FreeHaulers(w, Side.Player), f => f.Id == freighter.Id);
    }

    [Fact]
    public void A_food_run_waits_while_the_world_has_a_good_store_and_goes_first_when_it_runs_low()
    {
        var sim = NewGame();
        var w = sim.World;
        var (home, moon) = HomeAndMoon(w);
        sim.Issue(new FixedRunCommand(Side.Player, home.Id, moon.Id, Colonies.Food, 10, Auto: true, Why: "test"));
        sim.ApplyPending();
        var run = w.FixedRuns.Single(r => r.Good == Colonies.Food && r.ToBodyId == moon.Id);
        moon.FoodStock = Planets.FoodNeedPerDay(moon) * 50;
        Assert.False(ShortRuns.FoodLow(w, run));
        Assert.StartsWith("waits:", ShortRuns.Status(w, run));
        moon.FoodStock = Planets.FoodNeedPerDay(moon) * 3;
        Assert.True(ShortRuns.FoodUrgent(w, run));
    }

    [Fact]
    public void An_old_save_keeps_each_worlds_food()
    {
        var sim = NewGame();
        var (home, _) = HomeAndMoon(sim.World);
        home.FoodStock = 37;
        var json = sim.ToSave().ToJson();
        Assert.Contains("\"food\": 37", json);
        // Before food was a good, a world's food was its own "foodStock" field and not in its stock.
        var old = json.Replace("\"food\": 37", "\"oldfood\": 0").Replace($"\"key\": \"{home.Key}\",", $"\"key\": \"{home.Key}\",\n      \"foodStock\": 37,");
        var loaded = Simulation.FromSave(SaveGame.FromJson(old), GameLog.Memory());
        var again = loaded.World.Bodies.Single(b => b.Id == home.Id);
        Assert.Equal(37, again.FoodStock, 6);
        Assert.Null(again.SavedFoodStock);
    }
}
