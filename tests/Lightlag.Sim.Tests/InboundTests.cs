using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Early warning: enemy fleets heading for your planets, and the sensor numbers behind it.</summary>
public class InboundTests
{
    /// <summary>A rival fleet coasting at the player's home planet, 30 hours out, known to the player.</summary>
    private static (Simulation Sim, Body Home, Fleet Theirs) Coming()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory());
        var w = sim.World;
        w.Minds.Clear();
        w.Light.On = false; // command's picture is then the fleets' own
        var home = w.Bodies.First(b => b.Owner == Side.Player && b.Shipyard);
        var theirs = w.Fleets.First(f => f.Side == Side.Rival);
        theirs.Flight = null;
        theirs.Chase = null;
        Fleets.EnterDrift(theirs);
        theirs.Position = home.Position + new Vec2(3000, 0);
        theirs.Velocity = (FlightPlanner.PredictBody(w, home, w.Hour + 30) - theirs.Position) * (1.0 / 30);
        foreach (var sh in w.ShipsOf(theirs)) sh.Position = theirs.Position + sh.FormationOffset;
        Sensors.Reveal(w, Side.Player, theirs);
        return (sim, home, theirs);
    }

    [Fact]
    public void A_known_fleet_on_course_for_a_planet_is_listed_with_its_arrival()
    {
        var (sim, home, theirs) = Coming();
        var t = Assert.Single(Inbound.Planets(sim.World, Side.Player), x => x.FleetId == theirs.Id);
        Assert.Equal(home.Id, t.BodyId);
        Assert.InRange(t.Hours, 25, 35);
        Assert.Empty(Inbound.Planets(sim.World, Side.Rival).Where(x => x.FleetId == theirs.Id)); // never your own fleets
    }

    [Fact]
    public void An_out_of_sight_track_still_warns_until_it_goes_stale()
    {
        var (sim, home, theirs) = Coming();
        var w = sim.World;
        var s = Sensors.Find(w, Side.Player, theirs.Id)!;
        s.Visible = false;
        s.Hour = w.Hour - 5 * 24; // last seen 5 days ago, far back along the same course
        s.Position -= s.Velocity * (5 * 24);
        var t = Assert.Single(Inbound.Planets(w, Side.Player), x => x.FleetId == theirs.Id);
        Assert.False(t.InSight);
        Assert.InRange(t.SeenHoursAgo, 119, 121);
        Assert.Equal(home.Id, t.BodyId);

        s.Hour = w.Hour - Inbound.PlanetTrackHours - 1;
        Assert.DoesNotContain(Inbound.Planets(w, Side.Player), x => x.FleetId == theirs.Id);
    }

    [Fact]
    public void Fleet_warnings_use_tracks_up_to_two_days_old()
    {
        var (sim, _, theirs) = Coming();
        var w = sim.World;
        var s = Sensors.Find(w, Side.Player, theirs.Id)!;
        s.Visible = false;
        s.Hour = w.Hour - 24;
        Assert.True(Inbound.FreshForFleets(w, s));
        s.Hour = w.Hour - Inbound.FleetTrackHours - 1;
        Assert.False(Inbound.FreshForFleets(w, s));
    }

    [Fact]
    public void A_home_planet_sees_a_frigate_burn_across_the_inner_system_but_cold_ships_only_close_in()
    {
        var sensors = GameData.Sensors;
        var frigate = GameData.ShipClass("gun-frigate");
        Assert.True(Sensors.Range(sensors.PlanetSensorMm, frigate.SignatureSize * sensors.Burn) >= 1.5 * Physics.MmPerAu);
        // Ambushes still work: a cold frigate is only seen inside a light-second or so of the planet.
        Assert.True(Sensors.Range(sensors.PlanetSensorMm, frigate.SignatureSize * sensors.Cold) <= 500);
        // A frigate sees another's burn well beyond any battle.
        Assert.True(Sensors.Range(frigate.SensorMm, frigate.SignatureSize * sensors.Burn) >= 0.15 * Physics.MmPerAu);
    }
}
