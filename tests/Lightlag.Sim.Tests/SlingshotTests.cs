using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Lightlag.Sim.Tests;

public class SlingshotTests(ITestOutputHelper output)
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet Home(Simulation sim) => sim.World.Fleets.First(f => f.Side == Side.Player);
    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);

    private static FlightRequest To(Simulation sim, Body b, double speed, RouteKind route = RouteKind.Direct, int? via = null) =>
        new(b.Id, b.Position, speed, ArrivalStyle.Brake, HoldStyle.Orbit, sim.World.Tick, route, FlybyBodyId: via);

    [Fact]
    public void Only_heavy_planets_are_offered_for_a_flyby()
    {
        var sim = NewGame();
        var names = FlightPlanner.FlybyCandidates(sim.World, To(sim, BodyNamed(sim, "azure"), 0.5)).Select(b => b.Key).ToList();
        Assert.Contains("tamber", names);
        Assert.DoesNotContain("cinder", names);
        Assert.DoesNotContain("azure", names);
    }

    [Fact]
    public void A_flyby_of_tamber_bends_the_course_for_free()
    {
        var sim = NewGame();
        var f = Home(sim);
        foreach (var s in sim.World.ShipsOf(f)) s.Supplies = Fitting.MaxSupplies(sim.World, s);
        var tamber = BodyNamed(sim, "tamber");
        var plan = FlightPlanner.Plan(sim.World, f, To(sim, BodyNamed(sim, "coldwell"), 0.3, RouteKind.Slingshot, tamber.Id));
        output.WriteLine($"{plan.Ok} {plan.Problem} gain {plan.Flight?.FlybyGain:0.0} arrive {plan.Flight?.ArriveHour / 24:0.0} d dv {plan.DeltaV:0}");
        Assert.NotNull(plan.Flight);
        Assert.Equal(tamber.Id, plan.Flight!.FlybyBodyId);
        var direct = FlightPlanner.Plan(sim.World, f, To(sim, BodyNamed(sim, "coldwell"), 0.3));
        output.WriteLine($"direct {direct.Ok} arrive {direct.Flight?.ArriveHour / 24:0.0} d dv {direct.DeltaV:0}");
    }

    [Fact]
    public void A_slingshot_is_only_offered_when_it_beats_the_direct_route()
    {
        var sim = NewGame();
        var f = Home(sim);
        foreach (var target in new[] { "azure", "rust", "coldwell" })
        {
            var req = To(sim, BodyNamed(sim, target), 0.3);
            var direct = FlightPlanner.Plan(sim.World, f, req);
            var (best, via) = FlightPlanner.BestSlingshot(sim.World, f, req with { Route = RouteKind.Slingshot });
            output.WriteLine($"{target}: {best.Ok} {best.Problem} via {via?.Key} {best.Flight?.ArriveHour / 24:0} d vs {direct.Flight?.ArriveHour / 24:0} d");
            if (best.Flight != null) Assert.True(FlightPlanner.Beats(best, direct));
        }
        // Going out to Azure, a pass behind Tamber saves months for about the same fuel.
        var azure = FlightPlanner.BestSlingshot(sim.World, f, To(sim, BodyNamed(sim, "azure"), 0.3, RouteKind.Slingshot));
        Assert.NotNull(azure.Plan.Flight);
        Assert.Equal("tamber", azure.Via!.Key);
        var straight = FlightPlanner.Plan(sim.World, f, To(sim, BodyNamed(sim, "azure"), 0.3));
        Assert.True(azure.Plan.Flight!.ArriveHour < straight.Flight!.ArriveHour - 60 * 24);
        Assert.True(azure.Plan.DeltaV < straight.DeltaV * 1.3);
    }

    [Fact]
    public void The_swing_burn_is_paid_for_when_the_fleet_passes()
    {
        var sim = NewGame();
        var f = Home(sim);
        var (plan, _) = FlightPlanner.BestSlingshot(sim.World, f, To(sim, BodyNamed(sim, "azure"), 0.3, RouteKind.Slingshot));
        Assert.NotNull(plan.Flight);
        var full = sim.World.ShipsOf(f).Sum(s => s.Supplies);
        f.Flight = plan.Flight;
        f.Hold = HoldStyle.Drift;
        sim.StepMany((int)(plan.Flight!.FlybyHour - sim.World.Hour + 2));
        var used = full - sim.World.ShipsOf(f).Sum(s => s.Supplies);
        var expected = sim.World.ShipsOf(f).Sum(s => (FlightPlanner.DeltaVBetween(plan.Flight, 0, plan.Flight.FlybyHour + 2) + plan.Flight.FlybyBurn) * Tech.SuppliesPerDeltaV(sim.World, s));
        Assert.InRange(used, expected * 0.9, expected * 1.1 + 1);
    }

    [Fact]
    public void The_best_slingshot_is_found_and_flown_without_hitting_anything()
    {
        var sim = NewGame();
        var f = Home(sim);
        var req = To(sim, BodyNamed(sim, "azure"), 0.3, RouteKind.Slingshot);
        var (plan, via) = FlightPlanner.BestSlingshot(sim.World, f, req);
        output.WriteLine($"{plan.Ok} {plan.Problem} via {via?.Name} arrive {plan.Flight?.ArriveHour / 24:0.0} d");
        Assert.NotNull(plan.Flight);
        f.Flight = plan.Flight;
        f.Hold = HoldStyle.Drift;
        var arrive = plan.Flight.ArriveHour;
        sim.StepMany((int)(plan.Flight.FlybyHour - sim.World.Hour + 24));
        Assert.True(sim.World.Hour > plan.Flight.FlybyHour);
        Assert.NotNull(f.Flight);
    }
}
