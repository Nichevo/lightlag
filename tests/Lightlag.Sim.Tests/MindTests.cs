using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

public class MindTests
{
    private static Simulation NewGame(ulong seed, GameOptions options)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: options);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    [Fact]
    public void A_new_game_has_a_computer_rival_with_a_rolled_temperament()
    {
        var sim = NewGame(5, GameOptions.VsRival(Difficulty.Normal));
        var mind = Assert.Single(sim.World.Minds);
        Assert.Equal(Side.Rival, mind.Side);
        var a = GameData.Ai;
        // Each personality scales the rolled day by its probeEarly.
        var early = a.Personalities.Values.Select(p => p.ProbeEarly).ToList();
        Assert.InRange(mind.FirstProbeDay, a.FirstProbeDay.Min * early.Min() - 0.1, a.FirstProbeDay.Max * early.Max() + 0.1);
        Assert.True(mind.BaseRatio >= 1.0);
        Assert.Null(Mind.Of(sim.World, Side.Player));
    }

    [Fact]
    public void Personalities_vary_between_games()
    {
        var seen = Enumerable.Range(1, 30)
            .Select(i => NewGame((ulong)i, GameOptions.VsRival(Difficulty.Normal)).World.Minds[0].Personality).ToHashSet();
        Assert.Equal(Enum.GetValues<Personality>().Length, seen.Count);
    }

    [Fact]
    public void The_rival_builds_claims_and_gives_its_fleets_jobs()
    {
        var sim = NewGame(7, GameOptions.VsRival(Difficulty.Normal));
        // Ships cost more since M8 and fly on gentler engines, so the first claims leave a little later.
        var claimed = false;
        for (var d = 0; d < 90 && !(d >= 60 && claimed); d++)
        {
            Days(sim, 1);
            claimed |= sim.World.Minds[0].Jobs.Any(j => j.Kind == JobKind.Claim);
        }
        var rival = Economy.Of(sim.World, Side.Rival);
        Assert.True(rival.Totals.ShipsBuilt > 0);
        Assert.True(claimed);
        Assert.NotEmpty(sim.World.Minds[0].Thoughts);
        Assert.True(Economy.Claimable(sim.World).Count(b => b.Owner == Side.Rival) + rival.Totals.PlanetsClaimed > 1);
    }

    [Fact]
    public void Easy_thinks_less_often_than_normal()
    {
        Assert.True(Mind.ThinkHours(Difficulty.Easy) > Mind.ThinkHours(Difficulty.Normal));
        var sim = NewGame(7, GameOptions.VsRival(Difficulty.Easy));
        Days(sim, 3);
        var m = sim.World.Minds[0];
        Assert.Equal(Difficulty.Easy, m.Difficulty);
        Assert.True(m.NextThinkHour - m.LastThinkHour >= Mind.ThinkHours(Difficulty.Easy) - 1e-9);
    }

    [Fact]
    public void A_computer_vs_computer_game_replays_exactly()
    {
        var sim = NewGame(31, GameOptions.AiVsAi());
        Days(sim, 150);
        Assert.Equal(2, sim.World.Minds.Count);
        var result = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(result.Exact, result.Message);
    }

    [Fact]
    public void A_saved_game_keeps_the_rival_mind_and_continues_the_same()
    {
        var straight = NewGame(12, GameOptions.VsRival(Difficulty.Normal));
        Days(straight, 60);

        var first = NewGame(12, GameOptions.VsRival(Difficulty.Normal));
        Days(first, 30);
        var loaded = Simulation.FromSave(SaveGame.FromJson(first.ToSave().ToJson()), GameLog.Memory());
        Assert.Equal(first.World.Minds[0].Personality, loaded.World.Minds[0].Personality);
        Days(loaded, 30);
        Assert.Equal(straight.StateHash(), loaded.StateHash());
    }

    [Fact]
    public void Daily_stats_are_recorded_for_both_sides()
    {
        var sim = NewGame(3, GameOptions.VsRival(Difficulty.Normal));
        Days(sim, 10);
        Assert.Equal(11 * 2, sim.World.History.Count); // day 0 to day 10
        var csv = Stats.ToCsv(sim.World).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Stats.CsvHeader, csv[0]);
        Assert.Equal(23, csv.Length);
        Assert.All(sim.World.History, r => Assert.True(r.Ships > 0));
        Assert.Contains("Rival:", Stats.Summary(sim.World));
    }

    [Fact]
    public void A_fleet_that_was_closing_in_brakes_to_a_stop_once_the_fight_is_gone()
    {
        var sim = NewGame(4, new GameOptions(null));
        var f = sim.World.Fleets.First(x => x.Side == Side.Player);
        f.Flight = null;
        Fleets.EnterDrift(f);
        f.Velocity = new Vec2(30, 0);
        f.Maneuvering = true;
        Days(sim, 3);
        Assert.False(f.Maneuvering);
        Assert.True(f.Velocity.Length < 1e-6);
    }

    [Fact]
    public void Ai_data_loads_with_every_personality()
    {
        foreach (var p in Enum.GetValues<Personality>())
            Assert.NotEmpty(Mind.DataFor(p).Research);
    }
}

public class LowFuelTests
{
    [Fact]
    public void Stop_short_of_fuel_brakes_as_much_as_it_can_and_drifts_on_slower()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 4, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        var f = sim.World.Fleets.First(x => x.Side == Side.Player);
        f.Flight = null;
        Fleets.EnterDrift(f);
        f.Velocity = new Vec2(300, 0);
        // The same share in every ship (so sharing changes nothing), and fuel for at least 100 Mm/h in every ship.
        var ships = sim.World.ShipsOf(f).ToList();
        var share = ships.Max(s => 100 * Tech.SuppliesPerDeltaV(sim.World, s) / GameData.ShipClass(s.ClassId).Supplies);
        foreach (var s in ships) s.Supplies = share * GameData.ShipClass(s.ClassId).Supplies;
        sim.Issue(new StopFleetCommand(f.Id));
        sim.StepMany(24);
        Assert.Null(f.Flight);
        Assert.InRange(f.Velocity.Length, 150, 205); // shared fuel can brake a little more than 100
        Assert.True(f.Velocity.Length > 100);
    }
}

public class SupplySharingTests
{
    [Fact]
    public void Ships_in_a_fleet_share_supplies_and_keep_the_total()
    {
        var sim = Starts.Classic(Simulation.NewGame(Scenarios.TestSystem, 4, GameLog.Memory()));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        var f = sim.World.Fleets.First(x => x.Side == Side.Player && x.ShipIds.Count >= 2);
        var ships = sim.World.ShipsOf(f).ToList();
        f.Flight = null;
        Fleets.EnterDrift(f);
        ships[0].Supplies = 0;
        var total = ships.Sum(s => s.Supplies);
        sim.StepMany(12);
        Assert.Equal(total, ships.Sum(s => s.Supplies), 6);
        var shares = ships.Select(s => s.Supplies / GameData.ShipClass(s.ClassId).Supplies).ToList();
        Assert.True(shares.Max() - shares.Min() < 0.01);
    }
}
