using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Plan 4 build 3: reading the rival's intentions (plans overheard in its order traffic, late and partial).</summary>
public class IntentionsTests
{
    private static Simulation AiGame(ulong seed = 200, LightSetup? light = null)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: GameOptions.AiVsAi(Difficulty.Normal) with { Light = light });
        Starts.Classic(sim);
        return sim;
    }

    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    private static List<Intercept> All(World w) => Sides.InPlay(w).SelectMany(s => Intel.Files(w, s)).SelectMany(f => f.Intercepts).ToList();

    [Fact]
    public void Cover_is_full_near_an_ear_and_none_far_away()
    {
        var ears = new List<Vec2> { Vec2.Zero };
        Assert.Equal(1, Intentions.Cover(ears, new Vec2(0.2 * Physics.MmPerAu, 0)).Cover);
        Assert.Equal(0, Intentions.Cover(ears, new Vec2(2 * Physics.MmPerAu, 0)).Cover);
        var mid = Intentions.Cover(ears, new Vec2(1.0 * Physics.MmPerAu, 0)).Cover;
        Assert.InRange(mid, 0.4, 0.6);
        Assert.Equal(0, Intentions.Cover(new List<Vec2>(), Vec2.Zero).Cover);
    }

    [Fact]
    public void Plans_are_overheard_late_and_partial_and_only_shown_once_they_arrive()
    {
        var sim = AiGame();
        var w = sim.World;
        Days(sim, 80);
        var all = All(w);
        Assert.NotEmpty(all);
        Assert.All(all, i =>
        {
            Assert.True(i.HeardHour >= i.SentHour + 1, "decoding takes hours");
            Assert.InRange(i.Sure, 0.1, 0.95);
            Assert.False(i.False); // nobody has False order traffic
            if (i.EtaLow is double low) Assert.True(i.EtaHigh >= low);
            if (i.BodyId != null) Assert.Equal(i.TrueBodyId, i.BodyId);
        });
        // Partial: some catches miss the target or the timing.
        Assert.Contains(all, i => i.BodyId == null || i.EtaLow == null || i.Ships == 0);
        // What is still on its way is not shown.
        foreach (var side in Sides.InPlay(w))
            Assert.All(Intentions.Heard(w, side, old: true), i => Assert.True(i.HeardHour <= w.Hour));
    }

    [Fact]
    public void Slow_light_makes_the_news_later()
    {
        var sim = AiGame(201, new LightSetup(true, 0.002));
        var w = sim.World;
        Days(sim, 80);
        var all = All(w);
        Assert.NotEmpty(all);
        // At a five-hundredth of light speed, a tenth of an AU alone is over 20 hours.
        Assert.Contains(all, i => i.HeardHour - i.SentHour > Intentions.DecodeBaseHours + 2);
    }

    [Fact]
    public void False_order_traffic_plants_attacks_that_never_come()
    {
        var sim = AiGame(202);
        var w = sim.World;
        w.Sides[(int)Side.Rival].Researched.Add(Intentions.FalseTraffic);
        Days(sim, 120);
        var fakes = Intel.Of(w, Side.Player, Side.Rival).Intercepts.Where(i => i.False).ToList();
        Assert.NotEmpty(fakes);
        Assert.All(fakes, i =>
        {
            Assert.NotNull(i.BodyId);
            Assert.Equal(Side.Player, w.FindBody(i.BodyId!.Value)!.Owner);
            Assert.NotNull(i.EtaLow);
        });
        Assert.Contains(fakes, i => i.Outcome == PlanOutcome.Spoofed);
        // The side without the research plants nothing.
        Assert.DoesNotContain(Intel.Of(w, Side.Rival, Side.Player).Intercepts, i => i.False);
    }

    [Fact]
    public void Plans_heard_are_the_same_in_every_run()
    {
        string Run()
        {
            var sim = AiGame(203);
            Days(sim, 60);
            return string.Join("\n", All(sim.World).Select(i => $"{i.Id}|{i.Key}|{i.HeardHour}|{i.BodyId}|{i.EtaLow}|{i.Sure}|{i.Times}"));
        }
        var a = Run();
        Assert.NotEmpty(a);
        Assert.Equal(a, Run());
    }

    [Fact]
    public void Rival_guards_home_when_an_attack_on_it_is_overheard()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: GameOptions.VsRival(Difficulty.Normal));
        Starts.Classic(sim);
        var w = sim.World;
        Days(sim, 2);
        var home = Economy.ShipyardOf(w, Side.Rival)!;
        var mine = w.Fleets.First(f => f.Side == Side.Player);
        var file = Intel.Of(w, Side.Rival, Side.Player);
        file.Intercepts.Add(new Intercept
        {
            Id = ++file.LastInterceptId, Kind = PlanKind.Attack, Key = "test", FleetId = mine.Id, FleetName = mine.Name, Ships = 4,
            BodyId = home.Id, BodyName = home.Name, TrueBodyId = home.Id, EtaLow = w.Hour + 30, EtaHigh = w.Hour + 70,
            Sure = 0.8, SentHour = w.Hour - 1, HeardHour = w.Hour, FirstHeardHour = w.Hour,
        });
        Assert.Single(Intentions.Against(w, Side.Rival, home.Id, 96, 0.5));
        Days(sim, 1);
        var mind = Mind.Of(w, Side.Rival)!;
        Assert.Contains(mind.Thoughts, t => t.Text.Contains("overheard plans of an attack on " + home.Name));
    }

    [Fact]
    public void Words_for_the_screens()
    {
        var w = AiGame().World;
        var i = new Intercept { Kind = PlanKind.Attack, BodyName = "Verdant", EtaLow = w.Hour + 3 * 24, EtaHigh = w.Hour + 6 * 24, Sure = 0.6, HeardHour = w.Hour };
        Assert.Equal("Attack on Verdant", Intentions.What(i));
        Assert.Equal("3 to 6 days", Intentions.When(w, i));
        Assert.Equal("Attack (target not caught)", Intentions.What(new Intercept { Kind = PlanKind.Attack }));
        Assert.Equal("timing not caught", Intentions.When(w, new Intercept()));
        Assert.StartsWith("any time now", Intentions.When(w, new Intercept { EtaLow = w.Hour, EtaHigh = w.Hour + 30 }));
        Assert.Contains("60%", Intentions.Line(w, i).Replace(" %", "%"));
    }
}
