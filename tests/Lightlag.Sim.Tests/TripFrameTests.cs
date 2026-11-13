using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Lightlag.Sim.Tests;

/// <summary>
/// Trips start out moving with the planet the fleet leaves and end moving with the target, as a ship
/// leaving Earth for the Moon does: a trip to your own moon is short and cheap, not a chase after the
/// planet's orbit round the sun.
/// </summary>
public class TripFrameTests
{
    private readonly ITestOutputHelper _out;
    public TripFrameTests(ITestOutputHelper output) => _out = output;

    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);

    private static Fleet SplitFrigate(Simulation sim)
    {
        var w = sim.World;
        var home = w.Fleets.First(f => f.Side == Side.Player);
        var frigate = w.ShipsOf(home).First(s => s.ClassId == "gun-frigate");
        sim.Issue(new SplitFleetCommand(home.Id, new List<int> { frigate.Id }));
        sim.Step();
        return w.FleetOf(frigate)!;
    }

    private static FlightRequest To(World w, Body b) =>
        new(b.Id, Vec2.Zero, 1.0, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick);

    [Fact]
    public void A_trip_to_your_own_moon_is_short_and_stays_near_the_planet()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = SplitFrigate(sim);
        var verdant = BodyNamed(sim, "verdant");
        var pale = BodyNamed(sim, "pale");
        var perf = FleetPerformance.Of(w, f);
        var plan = FlightPlanner.Plan(w, f, To(w, pale));
        Assert.True(plan.Ok, plan.Problem);
        var flight = plan.Flight!;
        var hours = flight.ArriveHour - flight.DepartHour;
        // Burn half way and brake the rest, across the moon's distance: about 2 sqrt(d / a).
        var ideal = 2 * Math.Sqrt(pale.OrbitRadius / perf.Accel);
        _out.WriteLine($"trip {hours:0.0} h (ideal {ideal:0.0} h), dv {plan.DeltaV:0} Mm/h, accel {perf.Accel}");
        Assert.True(hours < ideal * 1.5, $"trip {hours:0.0} h, ideal {ideal:0.0} h");
        Assert.True(plan.DeltaV < perf.Accel * ideal * 1.6, $"dv {plan.DeltaV:0}");
        // The route never runs far off from the planet, chasing where it will be.
        for (var h = flight.DepartHour; h <= flight.ArriveHour; h += 0.5)
        {
            var at = FlightPlanner.FlightState(flight, h)!.Value.Position;
            Assert.True(Vec2.Distance(at, FlightPlanner.PredictBody(w, verdant, h)) < pale.OrbitRadius * 1.5);
        }

        sim.Issue(new PlanFlightCommand(f.Id, To(w, pale)));
        var end = w.Tick + 5 * GameTime.TicksPerDay;
        while (w.Tick < end && !(f.Flight == null && f.OrbitBodyId == pale.Id)) sim.Step();
        Assert.Equal(pale.Id, f.OrbitBodyId);
        Assert.Equal(HoldStyle.Orbit, f.Hold);
    }

    [Fact]
    public void A_moon_trip_starts_moving_with_the_planet_and_ends_moving_with_the_moon()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = SplitFrigate(sim);
        var verdant = BodyNamed(sim, "verdant");
        var pale = BodyNamed(sim, "pale");
        var plan = FlightPlanner.Plan(w, f, To(w, pale));
        Assert.True(plan.Ok, plan.Problem);
        var flight = plan.Flight!;
        var first = flight.Legs[0];
        var last = flight.Legs[^1];
        var leaving = FlightPlanner.BodyVelocity(w, verdant, flight.DepartHour);
        var arriving = FlightPlanner.BodyVelocity(w, pale, flight.ArriveHour);
        Assert.True(leaving.Length > 50, "a planet at 1 AU moves about 107 Mm an hour");
        Assert.True(Vec2.Distance(first.V0, leaving) < 0.5, $"{first.V0} vs {leaving}");
        Assert.True(Vec2.Distance(last.VelocityAt(last.End), arriving) < 0.5, $"{last.VelocityAt(last.End)} vs {arriving}");
        Assert.True(Vec2.Distance(flight.Target, FlightPlanner.PredictBody(w, pale, flight.ArriveHour)) < Fleets.OrbitRadiusFor(pale) * 1.01 + 0.1);
        // The burns are main burns; matching speed with the moon is a gentle push, not drawn as a burn.
        Assert.Equal(2, flight.Legs.Count(l => l.IsBurn));
    }

    [Fact]
    public void A_trip_to_another_planet_is_planned_at_rest_relative_to_the_sun()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = SplitFrigate(sim);
        var plan = FlightPlanner.Plan(w, f, To(w, BodyNamed(sim, "rust")));
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(Vec2.Zero, plan.Flight!.Legs[0].V0);
        Assert.All(plan.Flight.Legs, l => Assert.Equal(Vec2.Zero, l.Match));
        Assert.Equal(Vec2.Zero, plan.Flight.FrameVelocity);
    }

    [Fact]
    public void Moon_to_moon_trips_round_a_giant_are_quick()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = SplitFrigate(sim);
        var brim = BodyNamed(sim, "brim");
        var glass = BodyNamed(sim, "glass");
        // Put the frigate in orbit round Brim (a moon of the giant Tamber).
        f.Position = brim.Position + new Vec2(Fleets.OrbitRadiusFor(brim), 0);
        Fleets.EnterOrbit(f, brim);
        var perf = FleetPerformance.Of(w, f);
        var plan = FlightPlanner.Plan(w, f, To(w, glass));
        Assert.True(plan.Ok, plan.Problem);
        var hours = plan.Flight!.ArriveHour - plan.Flight.DepartHour;
        var farthest = brim.OrbitRadius + glass.OrbitRadius;
        var slowest = 2 * Math.Sqrt(farthest / perf.Accel) * 1.5;
        _out.WriteLine($"Brim to Glass: {hours:0.0} h (at most {slowest:0.0} h), dv {plan.DeltaV:0}");
        Assert.True(hours < slowest);
    }

    [Fact]
    public void A_chase_from_orbit_starts_with_the_planets_motion()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = SplitFrigate(sim);
        var verdant = BodyNamed(sim, "verdant");
        var v = FlightPlanner.TrueVelocity(w, f, w.Hour);
        Assert.True(Vec2.Distance(v, FlightPlanner.BodyVelocity(w, verdant, w.Hour)) < 1e-6);
        Assert.True(v.Length > 50, "a planet at 1 AU moves about 107 Mm an hour");
        Assert.Same(verdant, FlightPlanner.SystemOf(w, f));
        Assert.Same(verdant, FlightPlanner.SystemOf(w, BodyNamed(sim, "pale")));
        Assert.Null(FlightPlanner.SystemOf(w, BodyNamed(sim, "hesper")));
    }
}
