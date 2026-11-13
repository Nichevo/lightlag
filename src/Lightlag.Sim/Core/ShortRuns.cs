namespace Lightlag.Sim.Core;

/// <summary>
/// Standing runs for lasting shortages: a planet that burns a good faster than it makes it gets a run
/// from a planet with a lasting spare, set up by transport automation (Auto: at once; Advise: offered
/// in Needs you) and stopped by it when the shortage ends. Free freighters take the runs that are due,
/// most overdue first, so one freighter serves several runs in turn until more are built. One-off loads
/// (Colonies.AutoTransport) still answer sudden asks; these runs answer the steady drain.
/// </summary>
public static class ShortRuns
{
    /// <summary>A good drained slower than this a day is not worth a run.</summary>
    public const double MinNetPerDay = 0.05;

    /// <summary>A run is set up once the stock lasts under this many days at the planet's drain (or the planet asks).</summary>
    public const double ShortDays = 2 * Colonies.RunOutDays;

    public const double MinEveryDays = 7, MaxEveryDays = 30;

    /// <summary>Days an Auto run stopped by hand stays stopped before automation may set it up again.</summary>
    public const double StoppedDays = 60;

    /// <summary>Why a due load did not go, when no freighter was free.</summary>
    public const string NoFreighter = "no free freighter";

    /// <summary>A run worth setting up: what, where from, how often, and why.</summary>
    public sealed record Plan(Body From, Body To, string Good, double NetPerDay, double EveryDays, RouteRisk Risk, bool Asks)
    {
        public string Why => $"{To.Name} uses {NetPerDay:0.##} {Colonies.GoodName(Good).ToLowerInvariant()} a day more than it makes";
    }

    /// <summary>A good burnt here a day beyond what is made here (below 0: made faster than used).</summary>
    public static double NetUse(Body b, string good) => Colonies.UsePerDay(b, good) - Colonies.MadePerDay(b, good);

    /// <summary>A lasting shortage: the planet drains the good and its stock lasts under ShortDays (or it asks already).</summary>
    public static bool Short(Body to, string good)
    {
        if (to.Board == null) return false;
        var net = NetUse(to, good);
        if (net < MinNetPerDay) return false;
        return Colonies.Asks(to, good, out _) || Colonies.Held(to, good) / net < ShortDays;
    }

    /// <summary>A run (Auto or by hand) already brings the good to this planet.</summary>
    public static bool Covered(World w, Side side, int toId, string good) =>
        w.FixedRuns.Any(r => r.Side == side && r.ToBodyId == toId && (r.Good == good || r.Good == ""));

    /// <summary>The hold a run is planned on: the smallest freighter the side has, else a plain freighter.</summary>
    public static double PlanHold(World w, Side side)
    {
        var holds = w.Ships.Where(s => s.Side == side && SupplyLines.IsFreighter(s)).Select(s => Fitting.Spec(s).Cargo).Where(c => c > 0).ToList();
        if (holds.Count > 0) return holds.Min();
        return GameData.ShipClasses.TryGetValue("freighter", out var fr) ? fr.Cargo : 200;
    }

    /// <summary>How often to run: a load about every time half a hold has been used, within 7 to 30 days.</summary>
    public static double EveryDaysFor(double netPerDay, double hold) =>
        Math.Clamp(Math.Ceiling(hold * 0.5 / Math.Max(netPerDay, 1e-6)), MinEveryDays, MaxEveryDays);

    /// <summary>Where an Auto run fills the planet to: its usual fill line, or two runs' worth of its drain if more.</summary>
    public static double FillLine(Body to, string good, double everyDays)
    {
        Colonies.Asks(to, good, out var upTo);
        return Math.Max(upTo, Math.Max(0, NetUse(to, good)) * everyDays * 2);
    }

    /// <summary>How much an Auto run's load may bring: up to the fill line, less what is already on the way.</summary>
    public static double Room(World w, Side side, Body to, string good, double everyDays)
    {
        var coming = w.Shipments.Where(s => s.Side == side && s.ToBodyId == to.Id).Sum(s => s.Cargo.TryGetValue(good, out var v) ? v : 0);
        return Math.Max(0, FillLine(to, good, everyDays) - Colonies.Held(to, good) - coming);
    }

    /// <summary>The best source for a run: a planet with a lasting spare (makes more than it uses, or holds a month's worth to spare); safest, then most to give.</summary>
    public static (Body Body, RouteRisk Risk)? Source(World w, Side side, Body to, string good, double netPerDay)
    {
        var best = w.Bodies.Where(b => b.Owner == side && b.Id != to.Id && (b.Board != null || b.Stock.Count > 0) && !Economy.Blockaded(w, b))
            .Where(b => Colonies.SpareOf(b, good) >= 1 && (NetUse(b, good) < -MinNetPerDay || Colonies.SpareOf(b, good) >= Math.Max(20, netPerDay * 30)))
            .Select(b => (Body: b, Risk: SupplyChain.RiskOf(w, side, b.Position, to.Position, null, false).Risk))
            .OrderBy(x => x.Risk).ThenBy(x => NetUse(x.Body, good)).ThenByDescending(x => Colonies.SpareOf(x.Body, good))
            .ThenBy(x => Vec2.Distance(x.Body.Position, to.Position)).ThenBy(x => x.Body.Id).FirstOrDefault();
        return best.Body == null ? null : best;
    }

    /// <summary>The runs worth setting up now: lasting shortages no run covers yet, with a source, worst first.</summary>
    public static List<Plan> Wanted(World w, Side side)
    {
        var plans = new List<Plan>();
        var hold = PlanHold(w, side);
        foreach (var to in w.Bodies.Where(b => b.Owner == side && b.Board != null && !Economy.Blockaded(w, b)).OrderBy(b => b.Id))
            foreach (var good in Colonies.Goods.Select(g => g.Id))
            {
                if (!Short(to, good) || Covered(w, side, to.Id, good) || Economy.Of(w, side).Dismissed.ContainsKey($"run:{to.Id}:{good}")) continue;
                var net = NetUse(to, good);
                if (Source(w, side, to, good, net) is not var (from, risk)) continue;
                plans.Add(new Plan(from, to, good, net, EveryDaysFor(net, hold), risk, Colonies.Asks(to, good, out _)));
            }
        return plans.OrderByDescending(p => SupplyChain.ShortSeverity(p.To, p.Good)).ThenBy(p => p.To.Id).ThenBy(p => p.Good, StringComparer.Ordinal).ToList();
    }

    /// <summary>Why an Auto run is no longer needed (null: still needed): the planet makes enough now, or the source has run dry.</summary>
    public static string? RetireWhy(World w, FixedRun run)
    {
        if (!run.Auto || run.Good == "" || w.FindBody(run.FromBodyId) is not Body from || w.FindBody(run.ToBodyId) is not Body to) return null;
        var good = Colonies.GoodName(run.Good).ToLowerInvariant();
        if (NetUse(to, run.Good) < MinNetPerDay / 2 && Colonies.Held(to, run.Good) >= Colonies.Target(to, run.Good))
            return $"{to.Name} now makes enough {good}";
        if (run.DrySkips >= 2 && NetUse(from, run.Good) >= 0)
            return $"{from.Name} has had no {good} to spare twice running";
        return null;
    }

    /// <summary>
    /// Where the run stands, for the planet screen and the Supply tab: the freighter on it and its stage, or when
    /// the next load goes, or why it is stuck.
    /// </summary>
    public static string Status(World w, FixedRun run)
    {
        if (w.Shipments.Find(s => s.RunId == run.Id) is Shipment s)
        {
            var name = w.FindFleet(s.FleetId)?.Name ?? "a freighter";
            var at = w.FindBody(s.Loaded ? s.ToBodyId : s.FromBodyId)?.Name ?? "?";
            return s.Loaded ? $"{name} carrying it to {at}" : $"{name} on the way to load at {at}";
        }
        if (w.Hour >= run.NextHour)
        {
            if (Colonies.FreeHaulers(w, run.Side).Any()) return "due now";
            var haulers = w.Fleets.Where(f => f.Side == run.Side).Sum(f => w.ShipsOf(f).Count(SupplyLines.IsFreighter));
            var waiting = w.FixedRuns.Count(r => r.Side == run.Side && w.Hour >= r.NextHour && !w.Shipments.Any(x => x.RunId == r.Id));
            if (haulers == 0) return "WAITING: you have no freighter; build one at the shipyard";
            return $"WAITING: no freighter free ({haulers} busy{(waiting > 1 ? $", {waiting} runs in line" : "")}); it goes when one is back";
        }
        var days = (run.NextHour - w.Hour) / 24;
        var next = days < 1 ? "next load within a day" : $"next load in {days:0} days";
        return run.LastSkip is string skip && skip != NoFreighter ? $"{next} (last time: {skip})" : next;
    }
}
