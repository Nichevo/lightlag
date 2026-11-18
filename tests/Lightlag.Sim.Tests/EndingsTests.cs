using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Plan 4 build 1: early endings (terms, losing heart, stalled wars) and conceding.</summary>
public class EndingsTests
{
    private static Simulation NewGame(GameOptions options)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: options with { Budget = false });
        Starts.Classic(sim);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    /// <summary>The player holds every free world and the rival's fleet is gone: well past tipShare, short of winShare.</summary>
    private static void TipFor(Simulation sim, Side side)
    {
        var w = sim.World;
        var other = side == Side.Player ? Side.Rival : Side.Player;
        foreach (var b in Economy.Claimable(w).Where(b => b.Owner == null))
        {
            b.Owner = side;
            if (Planets.Settleable(b)) b.PopK = Math.Min(5, Planets.Cap(b));
        }
        foreach (var b in w.Bodies.Where(b => b.Owner == other && b.Shipyard)) b.PopK = 40;
        foreach (var ship in w.Ships.Where(s => s.Side == other).ToList()) Combat.Destroy(sim, ship);
        Economy.Of(w, other).FlagshipId = null;
        var share = Planets.Control(w, side).Share;
        Assert.InRange(share, Planets.D.TipShare, Planets.D.WinShare);
        sim.Step();
        Assert.True(Planets.Tipped(w, side));
    }

    [Fact]
    public void Early_endings_are_on_by_default_and_can_be_switched_off_at_the_start()
    {
        Assert.True(NewGame(GameOptions.VsRival(Difficulty.Normal)).World.Rules.EarlyEndings);
        Assert.False(NewGame(GameOptions.VsRival(Difficulty.Normal) with { EarlyEndings = false }).World.Rules.EarlyEndings);
    }

    [Fact]
    public void The_losing_rival_offers_terms_and_accepting_wins_now()
    {
        var sim = NewGame(GameOptions.VsRival(Difficulty.Normal));
        var w = sim.World;
        TipFor(sim, Side.Player);
        Days(sim, Endings.D.OfferAfterDays - 0.5);
        Assert.Null(w.Endings.TermsTo);
        Days(sim, 1);
        Assert.Equal(Side.Player, w.Endings.TermsTo);
        Assert.Null(w.Winner);
        // It shows in Needs you, with Accept as the act and Play on beside it.
        sim.StepMany(Stewards.EveryTicks);
        var need = Assert.Single(Economy.Of(w, Side.Player).Needs, n => n.Key == "terms");
        Assert.Equal(new AnswerTermsCommand(Side.Player, true), need.Act);
        Assert.Contains(NeedKinds.Choices(w, Side.Player, need), c => c.Command == new AnswerTermsCommand(Side.Player, false));

        sim.Issue(new AnswerTermsCommand(Side.Player, true));
        sim.Step();
        Assert.Equal(Side.Player, w.Winner);
        Assert.Contains("surrender", w.WinReason);
    }

    [Fact]
    public void Playing_on_withdraws_the_offer_until_later()
    {
        var sim = NewGame(GameOptions.VsRival(Difficulty.Normal));
        var w = sim.World;
        TipFor(sim, Side.Player);
        Days(sim, Endings.D.OfferAfterDays + 0.5);
        Assert.Equal(Side.Player, w.Endings.TermsTo);
        sim.Issue(new AnswerTermsCommand(Side.Player, false));
        sim.Step();
        Assert.Null(w.Endings.TermsTo);
        Assert.Null(w.Winner);
        Assert.Equal(w.Hour - 1 + Endings.D.OfferAgainDays * 24, w.Endings.DeclinedUntilHour, 6);
        // (The rival rebuilds, so the test shortens the wait rather than hold the lead for weeks.)
        w.Endings.DeclinedUntilHour = w.Hour + 24;
        Days(sim, 0.5);
        Assert.Null(w.Endings.TermsTo);
        Days(sim, 1);
        Assert.Equal(Side.Player, w.Endings.TermsTo);
    }

    [Fact]
    public void With_early_endings_off_there_are_no_terms_and_the_old_rules_still_win()
    {
        // No computer rival here, so it can't win the lead back while the thirty days run.
        var sim = NewGame(new GameOptions(EarlyEndings: false));
        var w = sim.World;
        TipFor(sim, Side.Player);
        Assert.False(Endings.WillBroken(w, Side.Rival));
        Assert.Equal(1, Endings.AimFactor(w, Side.Rival));
        Assert.Equal(Planets.D.SurrenderBelowTipped, Planets.SurrenderLine(w, Side.Player));
        Days(sim, Planets.D.WinAfterDays - 1);
        Assert.Null(w.Endings.TermsTo);
        Assert.Null(w.Winner);
        Days(sim, 1.1);
        Assert.Equal(Side.Player, w.Winner);
    }

    [Fact]
    public void The_side_behind_a_leader_loses_heart()
    {
        var sim = NewGame(GameOptions.VsRival(Difficulty.Normal));
        var w = sim.World;
        TipFor(sim, Side.Player);
        Assert.True(Endings.WillBroken(w, Side.Rival));
        Assert.False(Endings.WillBroken(w, Side.Player));
        Assert.Equal(Endings.D.WillAim, Endings.AimFactor(w, Side.Rival));
        Assert.Equal(1, Endings.AimFactor(w, Side.Player));
        Assert.Equal(Planets.D.SurrenderBelowTipped + Endings.D.WillSurrenderExtra, Planets.SurrenderLine(w, Side.Player));
        Assert.Equal(Endings.D.WillBlockadeShare, Endings.BlockadeWaitFactor(w, Side.Rival));
        // Switched off mid-game: back to the old numbers at once.
        sim.Issue(new SetEarlyEndingsCommand(false));
        sim.Step();
        Assert.False(w.Rules.EarlyEndings);
        Assert.Equal(1, Endings.AimFactor(w, Side.Rival));
        Assert.Equal(Planets.D.SurrenderBelowTipped, Planets.SurrenderLine(w, Side.Player));
    }

    [Fact]
    public void Switching_off_takes_terms_off_the_table()
    {
        var sim = NewGame(GameOptions.VsRival(Difficulty.Normal));
        var w = sim.World;
        TipFor(sim, Side.Player);
        Days(sim, Endings.D.OfferAfterDays + 0.5);
        Assert.Equal(Side.Player, w.Endings.TermsTo);
        sim.Issue(new SetEarlyEndingsCommand(false));
        sim.Step();
        Assert.Null(w.Endings.TermsTo);
        Days(sim, 5);
        Assert.Null(w.Endings.TermsTo);
        Assert.Null(w.Winner);
    }

    [Fact]
    public void A_computer_leader_takes_the_terms()
    {
        var sim = NewGame(GameOptions.AiVsAi());
        var w = sim.World;
        TipFor(sim, Side.Player);
        Days(sim, Endings.D.OfferAfterDays + 0.5);
        Assert.Equal(Side.Player, w.Winner);
        Assert.Contains("surrender", w.WinReason);
    }

    [Fact]
    public void No_terms_are_offered_to_a_computer_leader_by_you()
    {
        // The rival leads you: you get no offer (you concede yourself, or fight on), and it does not win early.
        var sim = NewGame(GameOptions.VsRival(Difficulty.Normal));
        var w = sim.World;
        TipFor(sim, Side.Rival);
        Days(sim, Endings.D.OfferAfterDays + 1);
        Assert.Null(w.Endings.TermsTo);
        Assert.Null(w.Winner);
        Assert.True(Endings.WillBroken(w, Side.Player));
        Assert.Contains("concede", Endings.Status(w, Side.Player));
    }

    [Fact]
    public void Conceding_ends_the_game_whatever_the_setting()
    {
        foreach (var on in new[] { true, false })
        {
            var sim = NewGame(GameOptions.VsRival(Difficulty.Normal) with { EarlyEndings = on });
            sim.Issue(new ConcedeCommand(Side.Player));
            sim.Step();
            Assert.Equal(Side.Rival, sim.World.Winner);
            Assert.Equal("won when you conceded", sim.World.WinReason);
        }
    }

    [Fact]
    public void Conceding_in_a_three_race_game_hands_it_to_the_strongest_rival()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(),
            options: GameOptions.VsRival(Difficulty.Normal) with { ThirdRace = Races.Hive, Ai = new List<AiSetup> { new(Side.Rival), new(Side.Third) } });
        sim.Issue(new ConcedeCommand(Side.Player));
        sim.Step();
        Assert.NotNull(sim.World.Winner);
        Assert.NotEqual(Side.Player, sim.World.Winner);
    }

    [Fact]
    public void A_stalled_war_brings_events_sooner()
    {
        var sim = NewGame(GameOptions.VsRival(Difficulty.Normal));
        var w = sim.World;
        Days(sim, 2);
        Assert.False(w.Endings.Stalled);
        Assert.Equal(1, Endings.EventFactor(w));
        // Pretend control has not moved for the stalemate time.
        w.Endings.StillSinceHour = w.Hour - Endings.D.StalemateAfterDays * 24;
        var nextBefore = Economy.Of(w, Side.Player).NextEventHour;
        Days(sim, 1);
        Assert.True(w.Endings.Stalled);
        Assert.Equal(Endings.D.StalemateEventFactor, Endings.EventFactor(w));
        Assert.True(Economy.Of(w, Side.Player).NextEventHour < nextBefore || nextBefore <= w.Hour);
        Assert.Contains("stalled", Endings.Status(w, Side.Player));
        // Off: no stalemate.
        sim.Issue(new SetEarlyEndingsCommand(false));
        sim.Step();
        Assert.False(w.Endings.Stalled);
        Assert.Equal(1, Endings.EventFactor(w));
    }

    [Fact]
    public void Ending_commands_replay_exactly()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 11, GameLog.Memory(), options: GameOptions.VsRival(Difficulty.Normal));
        Days(sim, 1);
        sim.Issue(new SetEarlyEndingsCommand(false));
        Days(sim, 1);
        sim.Issue(new SetEarlyEndingsCommand(true));
        sim.Issue(new AnswerTermsCommand(Side.Player, true)); // nothing on the table: refused, and replays the same
        Days(sim, 1);
        sim.Issue(new ConcedeCommand(Side.Player));
        Days(sim, 1);
        Assert.Equal(Side.Rival, sim.World.Winner);
        var replay = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(replay.Exact, replay.Message);
    }
}
