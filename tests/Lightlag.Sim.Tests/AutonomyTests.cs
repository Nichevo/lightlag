using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

public class AutonomyTests
{
    private static Simulation NewGame(bool lag = false, ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(),
            options: lag ? new GameOptions(Light: new LightSetup(true, 1.0)) : null);
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

    private static void StepDays(Simulation sim, double days, Func<bool>? until = null)
    {
        var end = sim.World.Tick + (long)(days * GameTime.TicksPerDay);
        while (sim.World.Tick < end && !(until?.Invoke() ?? false)) sim.Step();
    }

    [Fact]
    public void A_patrol_visits_the_planet_and_its_moon_in_turn()
    {
        var sim = NewGame();
        var f = SplitFrigate(sim);
        var verdant = BodyNamed(sim, "verdant");
        var pale = BodyNamed(sim, "pale");
        Assert.Equal(new[] { verdant.Id, pale.Id }, Autonomy.PatrolStops(sim.World, new Fleet { MissionBodyId = verdant.Id }).Select(b => b.Id));
        sim.Issue(new SetMissionCommand(f.Id, Mission.Patrol, verdant.Id));
        var sawPale = false;
        StepDays(sim, 30, () => (sawPale |= Economy.InOrbitAt(f, pale)) && Economy.InOrbitAt(f, verdant));
        Assert.True(sawPale);
        Assert.True(Economy.InOrbitAt(f, verdant));
        Assert.Equal(Mission.Patrol, f.Mission);
    }

    [Fact]
    public void A_hold_mission_goes_to_its_body_and_stays()
    {
        var sim = NewGame();
        var f = SplitFrigate(sim);
        var pale = BodyNamed(sim, "pale");
        sim.Issue(new SetMissionCommand(f.Id, Mission.Hold, pale.Id));
        sim.Step();
        Assert.True(f.InFlight);
        StepDays(sim, 20);
        Assert.True(Economy.InOrbitAt(f, pale));
    }

    [Fact]
    public void An_escort_follows_its_fleet()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = SplitFrigate(sim);
        var lead = w.Fleets.First(x => x.Side == Side.Player && x.Id != f.Id);
        var pale = BodyNamed(sim, "pale");
        sim.Issue(new SetMissionCommand(f.Id, Mission.Escort, EscortFleetId: lead.Id));
        sim.Issue(new PlanFlightCommand(lead.Id, new FlightRequest(pale.Id, pale.Position, 0.6, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick)));
        sim.Step();
        StepDays(sim, 2);
        Assert.Equal(pale.Id, f.Flight?.TargetBodyId ?? f.OrbitBodyId);
        StepDays(sim, 20, () => Economy.InOrbitAt(f, pale) && Economy.InOrbitAt(lead, pale));
        Assert.True(Economy.InOrbitAt(f, pale));
    }

    [Fact]
    public void A_direct_order_ends_the_mission()
    {
        var sim = NewGame();
        var f = SplitFrigate(sim);
        var pale = BodyNamed(sim, "pale");
        sim.Issue(new SetMissionCommand(f.Id, Mission.Patrol, BodyNamed(sim, "verdant").Id));
        sim.Step();
        sim.Issue(new PlanFlightCommand(f.Id, new FlightRequest(pale.Id, pale.Position, 0.5, ArrivalStyle.Brake, HoldStyle.Orbit, sim.World.Tick)));
        sim.Step();
        Assert.Equal(Mission.None, f.Mission);
    }

    /// <summary>Sets the frigate at Tamber (cut off by lag) and the rival's fleet burning straight at it from 400 Mm.</summary>
    private static (Simulation Sim, Fleet Frigate, Fleet Rival) Threatened(AutonomyLevel level)
    {
        var sim = NewGame(lag: true);
        var w = sim.World;
        var f = SplitFrigate(sim);
        var tamber = BodyNamed(sim, "tamber");
        sim.Issue(new PlanFlightCommand(f.Id, new FlightRequest(tamber.Id, tamber.Position, 1, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick)));
        sim.Issue(new SetAutonomyCommand(f.Id, level));
        sim.Step();
        StepDays(sim, 600, () => f.Flight == null && w.Light.InTransit.Count == 0);
        StepDays(sim, 1); // past the time it waits after an order
        StepDays(sim, 5, () => Light.SignalClear(w, Side.Player, f.Position)); // in plain view of the flagship
        Assert.True(Autonomy.CutOff(w, f, out var delay) && delay < 2);
        var rival = w.Fleets.First(x => x.Side == Side.Rival);
        Fleets.EnterDrift(rival);
        rival.Position = f.Position + new Vec2(400, 0);
        var tamberVelocity = FlightPlanner.PredictBody(w, tamber, w.Hour + 1) - FlightPlanner.PredictBody(w, tamber, w.Hour);
        rival.Velocity = tamberVelocity + new Vec2(-40, 0); // 40 Mm/h closing, in Tamber's frame
        rival.LastBurnHour = w.Hour;
        rival.Stance = Stance.Hold;
        return (sim, f, rival);
    }

    [Fact]
    public void A_free_fleet_cut_off_runs_from_a_stronger_enemy_at_once()
    {
        var (sim, f, _) = Threatened(AutonomyLevel.Free);
        StepDays(sim, 1.0 / 24 * 2);
        Assert.True(f.Retreating);
        var note = Assert.Single(sim.World.Light.Notes);
        Assert.True(note.ArriveHour > note.Hour + 0.5); // the news takes its light time to get home
    }

    [Fact]
    public void A_sensible_fleet_waits_for_orders_while_there_is_time_then_runs()
    {
        var (sim, f, _) = Threatened(AutonomyLevel.Sensible);
        StepDays(sim, 1.0 / 24 * 2);
        Assert.False(f.Retreating, string.Join(";", sim.World.Light.Notes.Select(n => n.Text)));
        StepDays(sim, 0.5, () => f.Retreating);
        Assert.True(f.Retreating);
    }

    [Fact]
    public void A_strict_fleet_only_follows_its_standing_orders()
    {
        var (sim, f, rival) = Threatened(AutonomyLevel.Strict);
        StepDays(sim, 0.5, () => Vec2.Distance(f.Position, rival.Position) < 50);
        Assert.Empty(sim.World.Light.Notes);
    }

    [Fact]
    public void Missions_and_notes_survive_a_save_and_replay_the_same()
    {
        var (sim, f, _) = Threatened(AutonomyLevel.Free);
        StepDays(sim, 1.0 / 24 * 2);
        var loaded = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        Assert.Equal(AutonomyLevel.Free, loaded.World.FindFleet(f.Id)!.Autonomy);
        Assert.Single(loaded.World.Light.Notes);
        for (var i = 0; i < 24; i++) { sim.Step(); loaded.Step(); }
        Assert.Equal(sim.StateHash(), loaded.StateHash());
    }
}
