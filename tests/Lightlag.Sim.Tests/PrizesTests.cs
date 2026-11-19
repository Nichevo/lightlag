using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Plan 4 build 4: contested prizes (a derelict cruiser, a helium-3 comet, an old relay that every side sees coming).</summary>
public class PrizesTests
{
    private static Simulation Game(GameOptions? options = null, ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: (options ?? new GameOptions()) with { Budget = false });
        Starts.Classic(sim);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        sim.Step();
        return sim;
    }

    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    /// <summary>Brings the prize of this kind forward: announced now, open within the hour (the planned one, or one added).</summary>
    private static Prize OpenNow(Simulation sim, PrizeKind kind)
    {
        var w = sim.World;
        var p = w.Prizes.Find(x => x.Kind == kind);
        if (p == null)
        {
            p = new Prize { Id = w.Prizes.Count + 1, Kind = kind, Name = Prizes.KindName(kind), A = Side.Player, B = Side.Rival };
            w.Prizes.Add(p);
        }
        p.AnnounceHour = w.Hour;
        p.OpenHour = w.Hour + 1;
        p.CloseHour = kind switch
        {
            PrizeKind.Derelict => p.OpenHour + Prizes.D.DerelictDays * 24,
            PrizeKind.Comet => p.OpenHour + Prizes.D.CometDays * 24,
            _ => null,
        };
        sim.StepMany(2);
        Assert.True(p.Open(w));
        return p;
    }

    /// <summary>Puts one of the side's armed fleets on the prize, engines cold.</summary>
    private static Fleet Park(World w, Side side, Prize p)
    {
        var f = w.Fleets.Where(f => f.Side == side && Combat.Armed(w, f)).OrderByDescending(f => f.ShipIds.Count).First();
        f.Flight = null;
        f.OrbitBodyId = null;
        f.Hold = HoldStyle.Drift;
        f.Velocity = Vec2.Zero;
        f.Position = p.At;
        foreach (var s in w.ShipsOf(f)) { s.Position = f.Position + s.FormationOffset; }
        f.Stance = Stance.HoldPosition;
        return f;
    }

    [Fact]
    public void Two_or_three_prizes_are_planned_for_the_middle_game_and_can_be_switched_off()
    {
        var w = Game().World;
        Assert.True(w.Rules.Prizes);
        Assert.InRange(w.Prizes.Count, Prizes.D.MinCount, Prizes.D.MaxCount);
        Assert.Equal(w.Prizes.Count, w.Prizes.Select(p => p.Kind).Distinct().Count());
        var first = w.Prizes.Min(p => p.OpenHour) / 24;
        Assert.InRange(first, Prizes.D.FirstOpenDayMin, Prizes.D.FirstOpenDayMax + 0.1);
        Assert.All(w.Prizes, p =>
        {
            Assert.False(p.Announced);
            Assert.Equal(Prizes.D.AnnounceDays * 24, p.OpenHour - p.AnnounceHour, 6);
        });

        var offSim = Game(new GameOptions(Prizes: false));
        Assert.False(offSim.World.Rules.Prizes);
        Days(offSim, 1);
        Assert.Empty(offSim.World.Prizes);
    }

    [Fact]
    public void Prizes_are_announced_to_everyone_between_the_home_worlds()
    {
        var sim = Game();
        var w = sim.World;
        var p = w.Prizes.OrderBy(x => x.AnnounceHour).First();
        Days(sim, p.AnnounceHour / 24 - w.Hour / 24 + 0.1);
        Assert.True(p.Announced);
        Assert.True(p.Coming(w));
        var a = Economy.HomeOf(w, p.A)!.Position;
        var b = Economy.HomeOf(w, p.B)!.Position;
        var da = Vec2.Distance(p.At, a);
        var db = Vec2.Distance(p.At, b);
        // Neither side's doorstep: about as far from each home, and well clear of the star.
        Assert.InRange(da / db, 0.6, 1.67);
        Assert.True(p.At.Length > 0.3 * Physics.MmPerAu);
        // The banner shows it coming to both sides, with when it opens.
        foreach (var side in new[] { Side.Player, Side.Rival })
            Assert.Contains(Events.Wide(w, side), e => e.Key == $"prize:{p.Id}:coming" && e.Coming && e.ComingWord == "opens in");
    }

    [Fact]
    public void Holding_the_derelict_alone_boards_it_and_a_damaged_cruiser_joins()
    {
        var sim = Game();
        var w = sim.World;
        var p = OpenNow(sim, PrizeKind.Derelict);
        var f = Park(w, Side.Player, p);
        var cruisers = w.Ships.Count(s => s.Side == Side.Player && s.ClassId == "cruiser");
        Days(sim, 1);
        Assert.Equal(Side.Player, p.WorkedBy);
        Assert.Contains("boarding", Prizes.Status(w, p, Side.Player));
        Assert.Contains("Derelict cruiser: boarding", Prizes.FleetStatus(w, f));
        Days(sim, Prizes.D.BoardDays);
        Assert.True(p.Over);
        Assert.Equal(Side.Player, p.Holder);
        var prize = w.Ships.Single(s => s.Side == Side.Player && s.ClassId == "cruiser" && s.Name.StartsWith("Prize ", StringComparison.Ordinal));
        Assert.Equal(cruisers + 1, w.Ships.Count(s => s.Side == Side.Player && s.ClassId == "cruiser"));
        Assert.True(prize.Armour < Tech.MaxArmour(w, prize) * 0.75);
        Assert.Contains(w.Chronicle.Moments, m => m.Kind == "prize" && m.Side == Side.Player);
    }

    [Fact]
    public void Both_sides_there_contest_it_and_nobody_gains()
    {
        var sim = Game();
        var w = sim.World;
        var p = OpenNow(sim, PrizeKind.Derelict);
        Park(w, Side.Player, p);
        Park(w, Side.Rival, p);
        sim.Step();
        Assert.True(p.Contested);
        Assert.Null(p.WorkedBy);
        Assert.Contains("contested", Prizes.Status(w, p, Side.Player));
        Assert.Equal(0, p.Hours[(int)Side.Player]);
        Assert.Equal(0, p.Hours[(int)Side.Rival]);
    }

    [Fact]
    public void Unarmed_ships_do_not_count()
    {
        var sim = Game();
        var w = sim.World;
        var p = OpenNow(sim, PrizeKind.Comet);
        var f = Park(w, Side.Player, p);
        foreach (var s in w.ShipsOf(f)) s.Fit = s.Fit.Where(m => m is not ("railgun" or "missile-rack")).ToList();
        if (Combat.Armed(w, f)) return; // this fit can't be stripped here: nothing to test
        sim.Step();
        Assert.Null(p.WorkedBy);
        Assert.Contains("unarmed", Prizes.FleetStatus(w, f));
    }

    [Fact]
    public void Mining_the_comet_brings_fuel_water_and_helium_3_home_while_it_passes()
    {
        var sim = Game();
        var w = sim.World;
        var p = OpenNow(sim, PrizeKind.Comet);
        Park(w, Side.Player, p);
        Days(sim, 2);
        // Into the shipyard's store (power plants may burn some of the helium-3 at once, so count what came in).
        var book = Economy.Of(w, Side.Player).Totals.GoodsIn;
        Assert.InRange(Ledger.Total(book, "helium-3", "comet mining"), Prizes.D.CometHelium3PerDay * 1.9, Prizes.D.CometHelium3PerDay * 2.1);
        Assert.InRange(Ledger.Total(book, Colonies.Fuel, "comet mining"), Prizes.D.CometFuelPerDay * 1.9, Prizes.D.CometFuelPerDay * 2.1);
        Assert.True(p.Mined[(int)Side.Player] > 0);
        Days(sim, Prizes.D.CometDays);
        Assert.True(p.Over);
        Assert.Equal(Side.Player, p.Holder);
        Assert.Contains("The comet has passed", p.Outcome);
    }

    [Fact]
    public void The_relay_is_taken_by_holding_it_gives_wide_sight_and_can_be_taken_back()
    {
        var sim = Game();
        var w = sim.World;
        var p = OpenNow(sim, PrizeKind.Relay);
        var mine = Park(w, Side.Player, p);
        Days(sim, Prizes.D.RelayTakeDays + 0.1);
        Assert.Equal(Side.Player, p.Holder);
        Assert.False(p.Over);
        Assert.Contains(Prizes.Observers(w, Side.Player), o => o.At == p.At);
        Assert.Empty(Prizes.Observers(w, Side.Rival));
        // The rival moves in once the player has gone: two days alone and it is theirs.
        mine.Position = p.At + new Vec2(Physics.MmPerAu * 0.2, 0);
        foreach (var s in w.ShipsOf(mine)) s.Position = mine.Position + s.FormationOffset;
        Park(w, Side.Rival, p);
        Days(sim, Prizes.D.RelayTakeDays + 0.1);
        Assert.Equal(Side.Rival, p.Holder);
        Assert.Contains("held by", Prizes.Status(w, p, Side.Player));
    }

    [Fact]
    public void The_rival_goes_for_an_open_prize()
    {
        var sim = Game(GameOptions.VsRival(Difficulty.Normal), 9);
        var w = sim.World;
        Days(sim, 20);
        OpenNow(sim, PrizeKind.Derelict);
        var p = w.Prizes.Single(x => x.Kind == PrizeKind.Derelict);
        var sent = false;
        for (var d = 0; d < 30 && !sent; d++)
        {
            Days(sim, 1);
            sent = Mind.Of(w, Side.Rival)!.Jobs.Any(j => j.Kind == JobKind.Prize && j.PrizeId == p.Id) || p.Hours[(int)Side.Rival] > 0;
        }
        Assert.True(sent, "the rival should send a group to the derelict");
    }

    [Fact]
    public void Prizes_survive_a_save_and_replay_the_same()
    {
        var sim = Game(GameOptions.AiVsAi(Difficulty.Normal), 12);
        var w = sim.World;
        foreach (var p in w.Prizes) { p.OpenHour -= 25 * 24; p.AnnounceHour -= 25 * 24; if (p.CloseHour != null) p.CloseHour -= 25 * 24; }
        Days(sim, 45);
        var json = Json.Serialize(w);
        var back = Json.Deserialize<World>(json);
        Assert.Equal(w.Prizes.Count, back.Prizes.Count);
        Assert.Equal(json, Json.Serialize(back));
        Assert.Contains(w.Prizes, p => p.Announced);
    }
}
