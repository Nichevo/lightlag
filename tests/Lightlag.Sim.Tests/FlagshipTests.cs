using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class FlagshipTests
{
    private static Simulation LagGame()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: new GameOptions(Light: new LightSetup(true, 1.0)));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    [Fact]
    public void Only_the_flagship_is_protected_by_default()
    {
        var sim = LagGame();
        var w = sim.World;
        var flag = Light.Flagship(w, Side.Player)!;
        foreach (var s in w.Ships.Where(s => s.Side == Side.Player))
            Assert.Equal(s.Id == flag.Id, Flagships.IsProtected(w, s));
    }

    [Fact]
    public void Screen_cover_turns_most_shots_at_the_flagship_onto_other_ships()
    {
        var sim = LagGame();
        var w = sim.World;
        var flag = Light.Flagship(w, Side.Player)!;
        var fleet = w.FleetOf(flag)!;
        Assert.Equal(flag.Id, Flagships.Screened(sim, flag).Id); // loose: no screen
        fleet.Cover = CoverStyle.Close;
        var hits = Enumerable.Range(0, 400).Count(_ => Flagships.Screened(sim, flag).Id == flag.Id);
        Assert.InRange(hits, 20, 100); // about 15 % get through
    }

    [Fact]
    public void Watching_the_flagship_fleet_makes_it_suspected_and_a_close_look_confirms_it()
    {
        var sim = LagGame();
        var w = sim.World;
        var rival = w.Fleets.First(f => f.Side == Side.Rival);
        var s = new Sighting { SeenBy = Side.Player, FleetId = rival.Id, Name = rival.Name, Ships = rival.ShipIds.Count, Position = rival.Position, Visible = true, Hour = w.Hour };
        w.Sightings.Add(s);
        Flagships.Update(sim);
        Assert.Equal(FlagKnown.Unknown, s.Flag);
        for (var i = 0; i < 6; i++) { s.Visible = true; Flagships.Update(sim); }
        Assert.Equal(FlagKnown.Suspected, s.Flag);
        s.Classes = new List<string> { "gun-frigate" };
        s.ClassesHour = w.Hour;
        Flagships.Update(sim);
        Assert.Equal(FlagKnown.Confirmed, s.Flag);
        Assert.Equal(Light.Flagship(w, Side.Rival)!.Id, s.FlagShipId);
    }

    [Fact]
    public void Radio_silence_hides_the_signal_and_holds_orders_until_it_ends()
    {
        var sim = LagGame();
        var w = sim.World;
        var rival = w.Fleets.First(f => f.Side == Side.Rival);
        var s = new Sighting { SeenBy = Side.Player, FleetId = rival.Id, Name = rival.Name, Visible = true, Hour = w.Hour };
        w.Sightings.Add(s);
        Economy.Of(w, Side.Rival).RadioSilence = true;
        for (var i = 0; i < 10; i++) { s.Visible = true; Flagships.Update(sim); }
        Assert.Equal(0, s.FlagScore);

        // A far player fleet: its order waits while the player keeps silent.
        var home = w.Fleets.First(f => f.Side == Side.Player);
        var frigate = w.ShipsOf(home).First(x => x.ClassId == "gun-frigate");
        sim.Issue(new SplitFleetCommand(home.Id, new List<int> { frigate.Id }));
        sim.Step();
        var lone = w.FleetOf(frigate)!;
        lone.Position += new Vec2(Physics.MmPerAu, 0); // far out of command range
        sim.Issue(new SetRadioSilenceCommand(Side.Player, true));
        sim.Issue(new SetHoldCommand(lone.Id, HoldStyle.Drift));
        sim.Step();
        Assert.Single(w.Light.Held);
        Assert.NotEqual(HoldStyle.Drift, lone.Hold);
        sim.Issue(new SetRadioSilenceCommand(Side.Player, false));
        sim.Step();
        Assert.Empty(w.Light.Held);
        for (var i = 0; i < 3; i++) sim.Step();
        Assert.Equal(HoldStyle.Drift, lone.Hold);
    }
}
