using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>
/// Planets that hold out for relief: guns and defence stations keep a blockade from starting until they are knocked
/// out, and a planet that knows help is coming grows happier and its defenders hold on harder.
/// </summary>
public class PlanetDefenceTests
{
    private static Simulation NewGame()
    {
        var sim = Starts.Classic(Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory()));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        sim.World.Minds.Clear();
        return sim;
    }

    private static Body Named(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);

    private static void Put(Simulation sim, Body b, string kind)
    {
        Planets.EnsureStarted(sim.World);
        var data = Planets.BuildingKind(kind)!;
        var (x, y, turned) = Colonies.BestSpot(b, data)!.Value;
        Colonies.AddPiece(sim.World, b, data, x, y, turned, built: true);
        Colonies.SyncBuildings(b);
    }

    private static Fleet InOrbit(Simulation sim, Side side, Body b, params string[] classes)
    {
        var ships = classes.Select(c => Economy.NewShip(sim.World, side, c)).ToList();
        var f = Fleets.Create(sim.World, side, ships, b.Position);
        f.Flight = null;
        f.Position = b.Position + new Vec2(Fleets.OrbitRadiusFor(b), 0);
        Fleets.EnterOrbit(f, b);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
        return f;
    }

    private static Body RivalColony(Simulation sim)
    {
        var vela = Named(sim, "vela");
        vela.Owner = Side.Rival;
        vela.PopK = 30;
        vela.Happiness = 60;
        Put(sim, vela, Planets.Farm);
        Put(sim, vela, "habitat");
        return vela;
    }

    [Fact]
    public void Guns_keep_a_blockade_from_starting_until_they_are_knocked_out()
    {
        var sim = NewGame();
        var w = sim.World;
        var vela = RivalColony(sim);
        Put(sim, vela, Planets.Guns);
        InOrbit(sim, Side.Player, vela, "missile-destroyer", "missile-destroyer");
        sim.StepMany(48);
        Assert.True(Planets.Contested(w, vela));
        Assert.False(Economy.Blockaded(w, vela));
        Assert.Equal(0, vela.BlockadeHours);
        Colonies.RemoveOne(vela, p => p.Kind == Planets.Guns);
        sim.StepMany(48);
        Assert.False(Planets.Contested(w, vela));
        Assert.True(Economy.Blockaded(w, vela));
        Assert.True(vela.BlockadeHours >= 24);
    }

    [Fact]
    public void A_defence_station_fires_on_both_orbits_and_is_worn_down_first()
    {
        var sim = NewGame();
        var w = sim.World;
        var vela = RivalColony(sim);
        Economy.Of(w, Side.Rival).Industry = 1000;
        new BuildStationCommand(Side.Rival, vela.Id, Economy.DefenceStation).Apply(w, sim);
        var fort = w.Stations.Single(st => st.Kind == Economy.DefenceStation);
        fort.ReadyHour = w.Hour;
        var data = Economy.StationKind(Economy.DefenceStation)!;
        Assert.Equal(data.Armour, fort.Armour);
        var enemy = InOrbit(sim, Side.Player, vela, "missile-destroyer", "missile-destroyer", "gun-frigate");
        enemy.OrbitHeight = OrbitHeight.High;
        var armour = w.ShipsOf(enemy).Sum(s => s.Armour);
        sim.StepMany(12);
        Assert.True(Planets.Contested(w, vela));
        Assert.True(w.ShipsOf(enemy).Sum(s => s.Armour) < armour); // it reaches high orbit
        Assert.True(fort.Armour < data.Armour);
        // Worn right down, it is wrecked and the blockade can start.
        fort.Armour = 1;
        sim.StepMany(12);
        Assert.DoesNotContain(w.Stations, st => st.Kind == Economy.DefenceStation);
        Assert.Equal(1, Economy.Of(w, Side.Player).Totals.FortsWrecked);
        Assert.True(Economy.Blockaded(w, vela));
    }

    [Fact]
    public void Help_coming_lifts_happiness_and_slows_the_invasion()
    {
        var sim = NewGame();
        var w = sim.World;
        var vela = RivalColony(sim);
        InOrbit(sim, Side.Player, vela, "gun-frigate");
        sim.Step();
        Assert.True(Economy.Blockaded(w, vela));
        Assert.Null(Ground.HelpComing(w, vela));
        var without = Planets.TargetHappiness(w, vela).Item1;
        var holdWithout = Ground.HoldFactor(w, vela);
        // A rival warfleet sets out for the planet.
        var relief = w.Fleets.First(f => f.Side == Side.Rival && Combat.Armed(w, f));
        new PlanFlightCommand(relief.Id, new FlightRequest(vela.Id, vela.Position, 1, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick)).Apply(w, sim);
        Assert.Equal(vela.Id, relief.Flight?.TargetBodyId);
        var help = Ground.HelpComing(w, vela);
        Assert.NotNull(help);
        Assert.Equal(relief.Id, help!.Value.Fleet.Id);
        var (with, parts) = Planets.TargetHappiness(w, vela);
        Assert.Contains(parts, p => p.Item1 == "Help is coming");
        Assert.True(with > without);
        Assert.True(Ground.HoldFactor(w, vela) < holdWithout);
    }

    [Fact]
    public void Happier_defenders_lose_fewer()
    {
        var sim = NewGame();
        var vela = RivalColony(sim);
        vela.Happiness = 90;
        var happy = Ground.HoldFactor(sim.World, vela);
        vela.Happiness = 20;
        Assert.True(Ground.HoldFactor(sim.World, vela) > happy);
    }
}
