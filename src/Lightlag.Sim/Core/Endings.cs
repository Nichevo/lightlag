using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>Early endings numbers (economy.json, "endings"). See the comment there.</summary>
public sealed record EndingsData(
    double OfferAfterDays = 3, double OfferAgainDays = 20,
    double WillSurrenderExtra = 10, double WillBlockadeShare = 0.67, double WillAim = 0.9,
    double StalemateAfterDays = 60, double StalemateMove = 0.05, double StalemateEventFactor = 0.5);

/// <summary>Where the game's ending stands: terms on the table, and how long control has stood still (see Endings).</summary>
public sealed class EndingsState
{
    /// <summary>The side the losers have offered terms to, waiting for its answer (null: none on the table).</summary>
    public Side? TermsTo { get; set; }
    public double TermsHour { get; set; }

    /// <summary>The leader's control share when the terms were offered.</summary>
    public double TermsShare { get; set; }

    /// <summary>After "Play on", no new offer before this hour.</summary>
    public double DeclinedUntilHour { get; set; }
    public int Declined { get; set; }

    /// <summary>Each side's control share when it last moved by StalemateMove or more, and since when it has stood still.</summary>
    public double[] StillShares { get; set; } = Array.Empty<double>();
    public double StillSinceHour { get; set; }

    /// <summary>Control has hardly moved for StalemateAfterDays: events come more often until it moves again.</summary>
    public bool Stalled { get; set; }
}

/// <summary>
/// Early endings (a game-start choice, on by default, switchable any time in Options). When on: once a side has held
/// planets.tipShare of control for OfferAfterDays and every side behind it is run by the computer, the losers offer
/// terms (a computer-run leader takes them; you choose to accept and win now, or play on). While a side leads like
/// that, the sides behind it lose heart: their planets give up sooner and their crews aim a little worse. If control
/// hasn't moved much for a long time, events come more often. When off, none of this happens. Conceding is always
/// open, whatever the setting (ConcedeCommand).
/// </summary>
public static class Endings
{
    public static EndingsData D => GameData.Economy.Endings ?? new EndingsData();

    /// <summary>The side that has tipped the game (holds tipShare of control), if any.</summary>
    public static Side? Leader(World w)
    {
        foreach (var s in Sides.Standing(w))
            if (Planets.Tipped(w, s)) return s;
        return null;
    }

    public static bool Minded(World w, Side side) => w.Minds.Any(m => m.Side == side);

    /// <summary>This side is behind a side that has tipped the game, with early endings on: its will is breaking.</summary>
    public static bool WillBroken(World w, Side side) =>
        w.Rules.EarlyEndings && w.Loop && w.Winner == null && Leader(w) is Side l && l != side && !Sides.Out(w, side);

    /// <summary>Crews of a side whose will is breaking aim a little worse.</summary>
    public static double AimFactor(World w, Side side) => WillBroken(w, side) ? D.WillAim : 1;

    /// <summary>A blockaded planet of a side whose will is breaking gives up after this share of the usual wait.</summary>
    public static double BlockadeWaitFactor(World w, Side owner) => WillBroken(w, owner) ? D.WillBlockadeShare : 1;

    /// <summary>How much higher the surrender line is for planets blockaded by a leader the others have lost heart against.</summary>
    public static double SurrenderExtra(World w, Side to) => w.Rules.EarlyEndings && w.Loop && Planets.Tipped(w, to) ? D.WillSurrenderExtra : 0;

    /// <summary>Events come this much more often (a factor on the days between them) while the war has stalled.</summary>
    public static double EventFactor(World w) => w.Rules.EarlyEndings && w.Endings.Stalled ? D.StalemateEventFactor : 1;

    public static void Update(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop || w.Winner != null) return;
        var e = w.Endings;
        if (!w.Rules.EarlyEndings)
        {
            e.TermsTo = null;
            e.Stalled = false;
            e.StillShares = Array.Empty<double>();
            return;
        }
        Terms(sim, e);
        if (w.Winner == null && w.Tick % GameTime.TicksPerDay == 0) Stalemate(sim, e);
    }

    private static void Terms(Simulation sim, EndingsState e)
    {
        var w = sim.World;
        var leader = Leader(w);
        if (e.TermsTo is Side open && open != leader)
        {
            e.TermsTo = null;
            sim.Log.Info("game", "Terms withdrawn: the lead slipped", new { side = open.ToString() });
        }
        if (leader is not Side l || e.TermsTo != null || w.Hour < e.DeclinedUntilHour - 1e-9) return;
        if (Economy.Of(w, l).MajoritySinceHour is not double since || w.Hour - since < D.OfferAfterDays * 24 - 1e-9) return;
        // Only a computer-run side offers terms: you concede yourself, or play on.
        if (!Sides.Standing(w).Where(s => s != l).All(s => Minded(w, s))) return;
        var share = Planets.Control(w, l).Share;
        if (Minded(w, l))
        {
            Economy.Win(sim, l, $"took {Losers(w, l)} surrender at {share:P0} of the system");
            return;
        }
        e.TermsTo = l;
        e.TermsHour = w.Hour;
        e.TermsShare = share;
        sim.Log.Info("game", "Terms offered", new { to = l.ToString(), share, day = w.Day });
    }

    /// <summary>"the rival's", or "the Hive's and the Lattice's" with two rivals.</summary>
    public static string Losers(World w, Side leader)
    {
        var names = Sides.Standing(w).Where(s => s != leader).Select(s => s == Side.Player ? "your" : $"{Sides.Them(w, s)}'s").ToList();
        return names.Count == 0 ? "the rival's" : string.Join(" and ", names);
    }

    private static void Stalemate(Simulation sim, EndingsState e)
    {
        var w = sim.World;
        var sides = Sides.InPlay(w).ToList();
        var shares = new double[Sides.Max];
        foreach (var s in sides) shares[(int)s] = Sides.Out(w, s) ? 0 : Planets.Control(w, s).Share;
        var moved = e.StillShares.Length != shares.Length || sides.Any(s => Math.Abs(shares[(int)s] - e.StillShares[(int)s]) >= D.StalemateMove - 1e-9);
        if (moved)
        {
            e.StillShares = shares;
            e.StillSinceHour = w.Hour;
            if (e.Stalled) sim.Log.Info("game", "Control is moving again", new { day = w.Day });
            e.Stalled = false;
            return;
        }
        if (e.Stalled || w.Hour - e.StillSinceHour < D.StalemateAfterDays * 24 - 1e-9) return;
        e.Stalled = true;
        // Events already on their way come sooner too.
        foreach (var s in sides)
        {
            var state = Economy.Of(w, s);
            if (state.NextEventHour > w.Hour) state.NextEventHour = w.Hour + (state.NextEventHour - w.Hour) * D.StalemateEventFactor;
        }
        sim.Log.Info("game", "The war has stalled: events come more often", new { day = w.Day, days = D.StalemateAfterDays });
    }

    /// <summary>The Needs you line for terms on the table: accept (win now) as the act, play on beside it.</summary>
    public static NeedItem? Need(World w, Side side)
    {
        var e = w.Endings;
        if (e.TermsTo != side || w.Winner != null) return null;
        return new NeedItem
        {
            Key = "terms", Level = NeedLevel.Urgent, Hour = e.TermsHour,
            Text = $"{Capital(Losers(w, side))} side asks for terms: you hold {e.TermsShare:P0} of the system. Accept to win now, or play on",
            Act = new AnswerTermsCommand(side, true), ActLabel = "Accept: win now",
            Alt = new AnswerTermsCommand(side, false), AltLabel = "Play on",
        };
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>One line on where the ending stands, for the progress and empire screens (null: nothing to say).</summary>
    public static string? Status(World w, Side side)
    {
        if (w.Winner != null) return null;
        if (!w.Rules.EarlyEndings) return "Early endings are off: games end only by the control or shipyard rules.";
        var e = w.Endings;
        var parts = new List<string>();
        if (e.TermsTo == side) parts.Add($"Terms are on the table: you hold {e.TermsShare:P0}");
        else if (Leader(w) is Side l)
        {
            if (l == side) parts.Add($"You lead: {Losers(w, l)} side has lost heart (planets give up sooner, crews aim worse)"
                                      + (Sides.Standing(w).Where(s => s != l).All(s => Minded(w, s)) ? "; terms come if you hold on" : ""));
            else parts.Add($"{Capital(Sides.Them(w, l))} leads: your people are losing heart (planets give up sooner, crews aim worse). You can concede in the game menu");
        }
        if (e.Stalled) parts.Add("The war has stalled: events come more often until control moves");
        return parts.Count == 0 ? null : string.Join(". ", parts) + ".";
    }
}

/// <summary>Answer terms the losing side offered: accept and win now, or play on (no new offer for a while).</summary>
public sealed record AnswerTermsCommand(Side Side, bool Accept) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        var e = world.Endings;
        if (world.Winner != null || e.TermsTo != Side)
        {
            sim.Log.Warn("game", "No terms on the table", new { side = Side.ToString(), Accept, Source });
            return;
        }
        e.TermsTo = null;
        if (Accept)
        {
            Economy.Win(sim, Side, $"accepted {Endings.Losers(world, Side)} surrender at {e.TermsShare:P0} of the system");
            return;
        }
        e.Declined++;
        e.DeclinedUntilHour = world.Hour + Endings.D.OfferAgainDays * 24;
        sim.Log.Info("game", "Terms turned down: playing on", new { side = Side.ToString(), againDay = e.DeclinedUntilHour / 24 });
    }
}

/// <summary>Give the game up. Always open, whatever the Early endings setting.</summary>
public sealed record ConcedeCommand(Side Side) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.Winner != null || !Sides.InGame(world, Side) || Sides.Out(world, Side))
        {
            sim.Log.Warn("game", "Can't concede now", new { side = Side.ToString(), Source });
            return;
        }
        sim.Log.Info("game", "Conceded", new { side = Side.ToString(), day = world.Day });
        Economy.Lose(sim, Side, Side == Side.Player ? "won when you conceded" : $"won when {Sides.Them(world, Side)} conceded");
    }
}

/// <summary>Switch Early endings on or off mid-game (Options).</summary>
public sealed record SetEarlyEndingsCommand(bool On) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.Rules.EarlyEndings == On) return;
        world.Rules.EarlyEndings = On;
        if (!On)
        {
            world.Endings.TermsTo = null;
            world.Endings.Stalled = false;
            world.Endings.StillShares = Array.Empty<double>();
        }
        sim.Log.Info("game", On ? "Early endings on" : "Early endings off", new { on = On, Source });
    }
}
