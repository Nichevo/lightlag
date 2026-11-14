using System.Text.Json.Serialization;
using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>The six budget pots. Every Industry spend is paid from one of them.</summary>
public enum Pot { Ships, Buildings, Stations, Refits, Transport, Reserve }

/// <summary>What happens to a cut-off place's share: rolls to Reserve (the default), is shared out to the rest of the pot, or is held for it.</summary>
public enum PlaceMode { Roll, ShareOut, Hold }

/// <summary>
/// economy.json "budget": starting shares (percent, in Pot order), how many days of its share fill a pot before
/// the rest overflows, the least move Auto bothers with, how far Auto leans to what is queued, and how many
/// points a threat moves towards ships and stations.
/// </summary>
public sealed record BudgetData(List<double>? Shares = null, double FullDays = 12, double MinMove = 3, double DemandWeight = 0.4, double ThreatShift = 10);

/// <summary>One pot: its name, share of the day's Industry, what it has saved, its lock and goal, and what it has spent.</summary>
public sealed class PotState
{
    public string Name { get; set; } = "";
    public double Share { get; set; }
    public double Saved { get; set; }

    /// <summary>Automation leaves a locked pot's share alone (a share set by hand locks it).</summary>
    public bool Locked { get; set; }

    /// <summary>A goal: the item (or project key) the pot saves for; nothing else may spend what it holds for it.</summary>
    public string? GoalKey { get; set; }
    public string? GoalName { get; set; }
    public double GoalCost { get; set; }
    public int? GoalBodyId { get; set; }

    /// <summary>Spent in all, today, and on each of the last 30 days (oldest first).</summary>
    public double Spent { get; set; }
    public double SpentToday { get; set; }
    public List<double> Days { get; set; } = new();
}

/// <summary>Industry held back in a pot for one cut-off place ("Hold it for ...").</summary>
public sealed class Earmark
{
    public Pot Pot { get; set; }
    public int BodyId { get; set; }
    public double Amount { get; set; }
}

/// <summary>A side's Industry budget (see Budget).</summary>
public sealed class BudgetState
{
    public List<PotState> Pots { get; set; } = new();
    public Pot OverflowTo { get; set; } = Pot.Reserve;

    /// <summary>The shares Auto leans back towards (race default, or the rival's personality).</summary>
    public List<double> BaseShares { get; set; } = new();

    /// <summary>Items in the player's order (keys; anything not listed follows in queue order), and items paused.</summary>
    public List<string> Ranked { get; set; } = new();
    public List<string> Paused { get; set; } = new();

    /// <summary>Cut-off places with a choice made for their share, and Industry held for them.</summary>
    public Dictionary<int, PlaceMode> Places { get; set; } = new();
    public List<Earmark> Earmarks { get; set; } = new();

    /// <summary>Spent at each place: in all, today, and a 30-day average a day.</summary>
    public Dictionary<int, double> SpentAt { get; set; } = new();
    public Dictionary<int, double> SpentAtToday { get; set; } = new();
    public Dictionary<int, double> SpentAtRate { get; set; } = new();

    /// <summary>Rolled into Reserve from cut-off places today, and a 30-day average a day.</summary>
    public double RolledToday { get; set; }
    public double RolledRate { get; set; }

    /// <summary>Overflow from full pots, and Reserve drawn for urgent defence, in all.</summary>
    public double Overflowed { get; set; }
    public double UrgentDrawn { get; set; }

    /// <summary>What the computer wanted to buy from each pot today but could not (one-off purchases that are not queued items).</summary>
    public List<double> Wanted { get; set; } = new();

    /// <summary>Auto's last share move, for the tab.</summary>
    public string? LastMove { get; set; }
    public double LastMoveHour { get; set; } = -1;
    public int Day { get; set; } = -1;

    [JsonIgnore] internal long CacheStamp = -1;
    [JsonIgnore] internal List<BudgetItem>? CacheItems;
}

/// <summary>
/// One thing drawing on a pot: where, what, its cost, whether it is next in its own line (the first unpaid ship
/// at its yard, the first queued building on its planet, a fleet at the refit yard), why it can't start for
/// reasons other than Industry, and whether it is paused or cut off.
/// </summary>
public sealed record BudgetItem(string Key, Pot Pot, int? BodyId, string What, double Cost, bool Next, string? Stall, bool Paused, bool Blocked,
    int? OrderId = null, int? FleetId = null)
{
    /// <summary>Holds its cost against the pot ahead of the items below it.</summary>
    public bool Holds => Next && !Paused && !Blocked && Stall == null;
}

/// <summary>
/// The Industry budget. Industry stays one pool per side (SideState.Industry); the pots are claims on it that
/// always add up to it. Each hour's income is split by share; a pot keeps what it does not spend and an order
/// draws only from its own pot. Inside a pot the items go in the player's order: the ones above hold their cost
/// first, and a paused item holds nothing. A goal holds the pot for one thing. A full pot overflows to the
/// nominated pot (Reserve by default). Urgent defence may draw on Reserve, and says so in the log. A place
/// that is cut off pauses its items and its share rolls to Reserve unless the player shares it out or holds it.
/// With the rule off (GameRules.Budget) everything spends the one pool as before.
/// </summary>
public static class Budget
{
    public const int Count = 6;
    public static readonly Pot[] All = Enum.GetValues<Pot>();
    private static readonly string[] Names = { "Ships", "Buildings", "Stations and defences", "Refits and repairs", "Transport and supply", "Reserve" };
    private static readonly double[] BuiltInShares = { 30, 25, 15, 10, 10, 10 };

    public static BudgetData D => GameData.Economy.Budget ?? new BudgetData();

    public static bool On(World w) => w.Loop && w.Rules.Budget;

    public static string DefaultName(Pot p) => Names[(int)p];

    public static string Short(Pot p) => p switch
    {
        Pot.Stations => "Stations",
        Pot.Refits => "Refits",
        Pot.Transport => "Transport",
        _ => Names[(int)p],
    };

    /// <summary>What a pot pays for, for tips.</summary>
    public static string What(Pot p) => p switch
    {
        Pot.Ships => "Warships, troop ships and courier drones",
        Pot.Buildings => "Planet buildings, and founding and training research teams",
        Pot.Stations => "Stations of the line, upgrades and bays, orbital stations, satellites, minefield upkeep",
        Pot.Refits => "Refits at the yard and module swaps",
        Pot.Transport => "Freighters, tankers, settler ships and lifting goods to orbit",
        _ => "Kept back for trouble: urgent defence and events may draw on it",
    };

    // ---- State ------------------------------------------------------------------------------------

    /// <summary>The side's budget, set up on first use; pots that no longer add up to the side's Industry are put right.</summary>
    public static BudgetState Of(World w, Side side)
    {
        var state = Economy.Of(w, side);
        if (state.Budget is not { Pots.Count: Count } bs)
        {
            bs = state.Budget = New(StartShares(w, side));
            foreach (var p in All) bs.Pots[(int)p].Saved = Math.Max(0, state.Industry) * bs.Pots[(int)p].Share / 100;
        }
        Sync(state, bs);
        return bs;
    }

    private static BudgetState New(double[] shares)
    {
        var bs = new BudgetState { BaseShares = shares.ToList() };
        foreach (var p in All) bs.Pots.Add(new PotState { Name = Names[(int)p], Share = shares[(int)p] });
        return bs;
    }

    /// <summary>Starting shares: the rival personality's (ai.json), else economy.json's, else the built-in ones.</summary>
    public static double[] StartShares(World w, Side side)
    {
        if (Mind.Of(w, side) is MindState m && Mind.DataFor(m.Personality).Budget is { Count: Count } ai) return Normal(ai.ToArray());
        return D.Shares is { Count: Count } s ? Normal(s.ToArray()) : (double[])BuiltInShares.Clone();
    }

    /// <summary>Back to the starting shares (a new rival mind), keeping what is saved.</summary>
    public static void Reset(World w, Side side)
    {
        var bs = Of(w, side);
        var shares = StartShares(w, side);
        bs.BaseShares = shares.ToList();
        foreach (var p in All) bs.Pots[(int)p].Share = shares[(int)p];
    }

    /// <summary>Industry changed outside the budget (a test, an old save): more is shared out by share, less comes from Reserve first.</summary>
    private static void Sync(SideState state, BudgetState bs)
    {
        var total = 0.0;
        foreach (var p in bs.Pots) total += p.Saved;
        var delta = Math.Max(0, state.Industry) - total;
        if (Math.Abs(delta) < 1e-9) return;
        if (delta > 0)
        {
            foreach (var p in bs.Pots) p.Saved += delta * p.Share / 100;
            return;
        }
        Take(bs, -delta, Pot.Reserve);
    }

    /// <summary>Takes this much out of the pots: from the first pot, then Reserve, then the fullest. Returns what was found.</summary>
    private static double Take(BudgetState bs, double amount, Pot first)
    {
        var left = amount;
        void From(PotState p)
        {
            var take = Math.Min(left, Math.Max(0, p.Saved));
            p.Saved -= take;
            left -= take;
        }
        From(bs.Pots[(int)first]);
        if (left > 1e-12) From(bs.Pots[(int)Pot.Reserve]);
        foreach (var p in bs.Pots.OrderByDescending(p => p.Saved).ToList())
            if (left > 1e-12) From(p);
        foreach (var p in bs.Pots) if (p.Saved < 0 && p.Saved > -1e-9) p.Saved = 0;
        return amount - left;
    }

    private static double[] Normal(double[] shares)
    {
        var sum = shares.Sum();
        return sum <= 0 ? (double[])BuiltInShares.Clone() : shares.Select(s => Math.Max(0, s) * 100 / sum).ToArray();
    }

    // ---- Which pot pays ---------------------------------------------------------------------------

    /// <summary>The computer wanted this from a pot and could not pay: Auto and its Reserve lean that way tomorrow.</summary>
    public static void Want(World w, Side side, Pot pot, double amount)
    {
        if (!On(w) || amount <= 0) return;
        var bs = Of(w, side);
        while (bs.Wanted.Count < Count) bs.Wanted.Add(0);
        bs.Wanted[(int)pot] = Math.Max(bs.Wanted[(int)pot], amount);
    }

    /// <summary>Freighters, tankers and settler ships come out of Transport; other hulls out of Ships.</summary>
    public static Pot PotOf(WorkOrder o) => o.Kind != OrderKind.Ship ? Pot.Buildings
        : o.SettleBodyId != null || GameData.ShipClasses.TryGetValue(o.Key, out var cls) && cls.Cargo > 0 && o.Key != "troop-ship" ? Pot.Transport : Pot.Ships;

    // ---- Items ------------------------------------------------------------------------------------

    /// <summary>Everything waiting on Industry, in the player's order (one list across the pots).</summary>
    public static List<BudgetItem> Items(World w, Side side)
    {
        var bs = Of(w, side);
        var stamp = w.Tick * 1_000_003L + w.NextId * 31L + w.Orders.Count;
        if (bs.CacheStamp == stamp && bs.CacheItems != null) return bs.CacheItems;
        var raw = new List<BudgetItem>();
        var main = Economy.ShipyardOf(w, side);
        foreach (var o in w.Orders)
        {
            if (o.Side != side || o.Kind != OrderKind.Ship || o.Paid) continue;
            var yard = o.YardBodyId is int y ? w.FindBody(y) : main;
            var stall = o.Waiting != null && !o.Waiting.Contains("Industry", StringComparison.Ordinal) ? o.Waiting : null;
            raw.Add(new BudgetItem("order:" + o.Id, PotOf(o), yard?.Id, Economy.NameOf(o), Economy.CostOf(o), false, stall, IsPaused(bs, "order:" + o.Id),
                o.Blockaded || yard != null && Economy.Blockaded(w, yard), OrderId: o.Id));
        }
        foreach (var b in w.Bodies)
        {
            if (b.Owner != side) continue;
            var underWay = b.Pieces.Any(p => !p.Built && !p.Waiting);
            foreach (var p in b.Pieces)
            {
                if (!p.Waiting || Planets.BuildingKind(p.Kind) is not BuildingData data) continue;
                var key = PieceKey(b, p);
                raw.Add(new BudgetItem(key, Pot.Buildings, b.Id, data.Name, data.Cost, false, underWay ? "behind the building under way" : null,
                    IsPaused(bs, key), Economy.Blockaded(w, b)));
            }
        }
        foreach (var f in w.Fleets)
        {
            if (f.Side != side || f.Refit is not RefitPlan plan) continue;
            var next = w.ShipsOf(f).Where(s => s.RefitHoursLeft <= 0 && Refits.Outdated(w, s)).OrderBy(s => s.Id).FirstOrDefault();
            if (next == null || w.FindBody(plan.YardBodyId) is not Body yard) continue;
            var key = "refit:" + f.Id;
            var stall = !Economy.InOrbitAt(f, yard) ? "flying to the yard" : plan.Waiting != null && !plan.Waiting.Contains("Industry", StringComparison.Ordinal) ? plan.Waiting : null;
            raw.Add(new BudgetItem(key, Pot.Refits, yard.Id, $"{f.Name} refit", Refits.CostOf(next), false, stall, IsPaused(bs, key), Economy.Blockaded(w, yard), FleetId: f.Id));
        }
        var ordered = raw.Select((it, i) => (it, r: bs.Ranked.IndexOf(it.Key), i))
            .OrderBy(x => x.r < 0 ? int.MaxValue : x.r).ThenBy(x => x.i).Select(x => x.it).ToList();
        // Next in its own line: the first live ship order at each yard, the first live building on each planet, every refit.
        var lines = new HashSet<string>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var it = ordered[i];
            if (it.Paused || it.Blocked) continue;
            var line = it.Key.StartsWith("order:", StringComparison.Ordinal) ? $"yard:{it.BodyId}" : it.Key.StartsWith("piece:", StringComparison.Ordinal) ? $"planet:{it.BodyId}" : it.Key;
            if (lines.Add(line)) ordered[i] = it with { Next = true };
        }
        bs.CacheStamp = stamp;
        bs.CacheItems = ordered;
        return ordered;
    }

    public static string PieceKey(Body b, Piece p) => $"piece:{b.Id}:{p.Id}";

    private static bool IsPaused(BudgetState bs, string key) => bs.Paused.Contains(key);

    public static bool Paused(World w, Side side, string key) => On(w) && Of(w, side).Paused.Contains(key);

    private static void Touch(BudgetState bs) => bs.CacheStamp = -1;

    /// <summary>The ship order a yard takes up next: one already under way, else the first unpaused one in budget order.</summary>
    public static WorkOrder? NextOrder(World w, Side side, IEnumerable<WorkOrder> candidates, Body? yard = null)
    {
        var list = candidates.ToList();
        if (list.Find(o => o.Paid) is WorkOrder going) return going;
        if (!On(w)) return list.FirstOrDefault();
        var bs = Of(w, side);
        var ordered = list.Where(o => !bs.Paused.Contains("order:" + o.Id)).Select((o, i) => (o, r: bs.Ranked.IndexOf("order:" + o.Id), i))
            .OrderBy(x => x.r < 0 ? int.MaxValue : x.r).ThenBy(x => x.i).Select(x => x.o).ToList();
        if (yard == null || ordered.Count < 2) return ordered.FirstOrDefault();
        // The yard takes the first order that can start now: one waiting for materials, or for its own pot, keeps its
        // place and lets the next go ahead (a settler ship is not held up by a warship short of alloys).
        foreach (var o in ordered)
        {
            var mats = Races.Materials(w, side, Economy.MaterialsOf(o));
            var why = Economy.MaterialShort(yard, mats) ?? Races.GrowStatus(w, side, yard, Races.BiomassFor(w, side, Economy.CostOf(o)), started: false)
                      ?? (UrgentCover(w, side, PotOf(o), Economy.CostOf(o), "order:" + o.Id) ? null : ShortOf(w, side, PotOf(o), Economy.CostOf(o), "order:" + o.Id));
            if (why == null) return o;
            o.Waiting = why;
        }
        return ordered[0];
    }

    /// <summary>The queued building a planet starts next: the first unpaused one in budget order.</summary>
    public static Piece? NextPiece(World w, Body b)
    {
        var waiting = b.Pieces.Where(p => p.Waiting).ToList();
        if (!On(w) || b.Owner is not Side side) return waiting.FirstOrDefault();
        var bs = Of(w, side);
        return waiting.Where(p => !bs.Paused.Contains(PieceKey(b, p))).Select((p, i) => (p, r: bs.Ranked.IndexOf(PieceKey(b, p)), i))
            .OrderBy(x => x.r < 0 ? int.MaxValue : x.r).ThenBy(x => x.i).Select(x => x.p).FirstOrDefault();
    }

    // ---- What may be spent ------------------------------------------------------------------------

    /// <summary>
    /// What this item (or, with no key, a one-off purchase) may draw from the pot now: what it has saved, less a
    /// goal it is not, less Industry held for cut-off places, less the cost of each live item above it.
    /// </summary>
    public static double Free(World w, Side side, Pot pot, string? key = null)
    {
        var state = Economy.Of(w, side);
        if (!On(w)) return state.Industry;
        var bs = Of(w, side);
        var ps = bs.Pots[(int)pot];
        var free = ps.Saved;
        if (ps.GoalKey != null && ps.GoalKey != key) free -= Math.Min(ps.Saved, ps.GoalCost);
        var at = key != null ? Items(w, side).Find(i => i.Key == key)?.BodyId : null;
        foreach (var e in bs.Earmarks) if (e.Pot == pot && e.BodyId != at) free -= e.Amount;
        if (ps.GoalKey == null || ps.GoalKey != key)
            foreach (var it in Items(w, side))
            {
                if (it.Key == key) break;
                if (it.Pot == pot && it.Holds) free -= it.Cost;
            }
        return Math.Max(0, free);
    }

    /// <summary>Whether a one-off purchase from this pot can be paid now.</summary>
    public static bool Afford(World w, Side side, Pot pot, double cost, string? key = null) => Free(w, side, pot, key) >= cost - 1e-9;

    /// <summary>Why this can't be paid now, in words (which pot, and about how many days), or null if it can.</summary>
    public static string? ShortOf(World w, Side side, Pot pot, double cost, string? key = null)
    {
        var state = Economy.Of(w, side);
        if (!On(w)) return state.Industry < cost - 1e-9 ? $"needs {cost - state.Industry:0} more Industry" : null;
        if (key != null && Paused(w, side, key)) return "paused in the Industry budget";
        var free = Free(w, side, pot, key);
        if (free >= cost - 1e-9) return null;
        var bs = Of(w, side);
        var ps = bs.Pots[(int)pot];
        if (ps.GoalKey != null && ps.GoalKey != key && ps.Saved >= cost) return $"{ps.Name} is saving its Industry for {ps.GoalName}";
        if (ps.Saved >= cost && key != null && Items(w, side).TakeWhile(i => i.Key != key).FirstOrDefault(i => i.Pot == pot && i.Holds) is BudgetItem above)
            return $"Industry held for {above.What} first ({ps.Name})";
        var need = cost - free;
        return $"{ps.Name} needs {need:0} more Industry ({Days(w, side, pot, need)})";
    }

    /// <summary>
    /// Urgent defence: while the side's planets are blockaded, a warship or station short in its own pot may take the
    /// rest from Reserve (unless Reserve is saving for a goal). True when that covers it.
    /// </summary>
    public static bool UrgentCover(World w, Side side, Pot pot, double cost, string? key = null)
    {
        if (!On(w) || pot is not (Pot.Ships or Pot.Stations) || Threat(w, side) <= 0) return false;
        if (key != null && Paused(w, side, key)) return false;
        return Free(w, side, pot, key) + Free(w, side, Pot.Reserve) >= cost - 1e-9;
    }

    /// <summary>ShortOf as a sentence for a "why not" check ("Ships needs 40 more Industry (about 2 d)"), or null.</summary>
    public static string? Problem(World w, Side side, Pot pot, double cost, string? key = null) =>
        ShortOf(w, side, pot, cost, key) is string s ? char.ToUpperInvariant(s[0]) + s[1..] : null;

    /// <summary>"about 4 d", "under a day" or "no share", for a pot to gather this much.</summary>
    public static string Days(World w, Side side, Pot pot, double need)
    {
        var perDay = PerDay(w, side, pot);
        if (need <= 0) return "now";
        if (perDay <= 1e-9) return "it gets no share";
        var d = need / perDay;
        return d < 1 ? "under a day" : $"about {Math.Ceiling(d):0} d";
    }

    /// <summary>Industry a day this pot gets from its share.</summary>
    public static double PerDay(World w, Side side, Pot pot) => Economy.IncomePerDay(w, side) * Of(w, side).Pots[(int)pot].Share / 100;

    // ---- Paying -----------------------------------------------------------------------------------

    /// <summary>
    /// Pays from the pot (and the side's Industry). Callers check first; anything the pot can't cover comes from
    /// Reserve (logged when urgent) and then from the fullest pot, so the pots always add up to Industry.
    /// </summary>
    public static void Pay(World w, Side side, Pot pot, double cost, string? key = null, int? bodyId = null, bool urgent = false, GameLog? log = null)
    {
        if (cost <= 0) return;
        var state = Economy.Of(w, side);
        if (!On(w))
        {
            state.Industry -= cost;
            return;
        }
        var bs = Of(w, side);
        var ps = bs.Pots[(int)pot];
        var own = Math.Min(cost, Math.Max(0, ps.Saved));
        ps.Saved -= own;
        var rest = cost - own;
        if (urgent && pot == Pot.Reserve && own > 0)
        {
            bs.UrgentDrawn += own;
            log?.Info("budget", "Urgent need draws on Reserve", new { side = side.ToString(), amount = Math.Round(own, 1), key });
        }
        if (rest > 1e-9)
        {
            var reserve = bs.Pots[(int)Pot.Reserve];
            var fromReserve = Math.Min(rest, Math.Max(0, reserve.Saved));
            reserve.Saved -= fromReserve;
            rest -= fromReserve;
            if (urgent && fromReserve > 0)
            {
                bs.UrgentDrawn += fromReserve;
                log?.Info("budget", "Urgent defence draws on Reserve", new { side = side.ToString(), pot = pot.ToString(), amount = Math.Round(fromReserve, 1), key });
            }
            if (rest > 1e-9) rest -= Take(bs, rest, Pot.Reserve);
        }
        state.Industry -= cost;
        Spent(bs, ps, cost, bodyId);
        if (key != null && ps.GoalKey == key)
        {
            log?.Info("budget", "Goal reached and paid", new { side = side.ToString(), pot = pot.ToString(), goal = ps.GoalName, cost });
            ClearGoal(ps);
        }
        if (state.Industry < 0 && state.Industry > -1e-6) state.Industry = 0;
        Touch(bs);
    }

    /// <summary>Running costs (upkeep, lift, minefields): what the pot (then Reserve) has, never below zero. Returns what was paid.</summary>
    public static double Drain(World w, Side side, Pot? pot, double amount, int? bodyId = null)
    {
        var state = Economy.Of(w, side);
        var paid = Math.Min(amount, Math.Max(0, state.Industry));
        if (paid <= 0) return 0;
        if (!On(w))
        {
            state.Industry -= paid;
            return paid;
        }
        var bs = Of(w, side);
        if (pot is Pot p)
        {
            var have = bs.Pots[(int)p].Saved + bs.Pots[(int)Pot.Reserve].Saved;
            paid = Math.Min(paid, Math.Max(0, have));
            Take(bs, paid, p);
            Spent(bs, bs.Pots[(int)p], paid, bodyId);
        }
        else
        {
            // Upkeep comes off the top, like a cut in income: from each pot by its share, any pot that is empty passing its part on.
            var left = paid;
            foreach (var x in bs.Pots)
            {
                var part = Math.Min(Math.Max(0, x.Saved), paid * x.Share / 100);
                x.Saved -= part;
                left -= part;
            }
            if (left > 1e-12) Take(bs, left, Pot.Reserve);
            foreach (var x in bs.Pots)
            {
                var part = paid * x.Share / 100;
                x.SpentToday += part;
                x.Spent += part;
            }
        }
        state.Industry -= paid;
        if (state.Industry < 0) state.Industry = 0;
        foreach (var x in bs.Pots) if (x.Saved < 0) x.Saved = 0;
        return paid;
    }

    private static void Spent(BudgetState bs, PotState ps, double amount, int? bodyId)
    {
        ps.Spent += amount;
        ps.SpentToday += amount;
        if (bodyId is int b)
        {
            bs.SpentAt[b] = bs.SpentAt.GetValueOrDefault(b) + amount;
            bs.SpentAtToday[b] = bs.SpentAtToday.GetValueOrDefault(b) + amount;
        }
    }

    /// <summary>An order called off after it was paid: its Industry goes back to its pot.</summary>
    public static void Refund(World w, Side side, Pot pot, double amount)
    {
        var state = Economy.Of(w, side);
        if (amount <= 0) return;
        if (!On(w))
        {
            state.Industry += amount;
            return;
        }
        var bs = Of(w, side);
        state.Industry += amount;
        var ps = bs.Pots[(int)pot];
        ps.Saved += amount;
        ps.Spent -= amount;
        Touch(bs);
    }

    // ---- Income and the hour ----------------------------------------------------------------------

    /// <summary>The hour's income, split by share. A cut-off place's part of a pot rolls to Reserve or is held for it.</summary>
    public static void Earn(World w, Side side, double earned)
    {
        var state = Economy.Of(w, side);
        if (!On(w))
        {
            state.Industry += earned;
            return;
        }
        var bs = Of(w, side);
        var items = Items(w, side);
        state.Industry += earned; // after the last Of, so the new income is not shared out twice
        foreach (var p in All)
        {
            var ps = bs.Pots[(int)p];
            var part = earned * ps.Share / 100;
            if (p != Pot.Reserve && part > 0)
            {
                var live = items.Where(i => i.Pot == p && !i.Paused && i.BodyId != null).ToList();
                var total = live.Sum(i => i.Cost);
                if (total > 0)
                    foreach (var g in live.Where(i => i.Blocked).GroupBy(i => i.BodyId!.Value).OrderBy(g => g.Key))
                    {
                        var mode = bs.Places.GetValueOrDefault(g.Key);
                        if (mode == PlaceMode.ShareOut) continue;
                        var cut = part * g.Sum(i => i.Cost) / total;
                        if (mode == PlaceMode.Hold)
                        {
                            if (bs.Earmarks.Find(e => e.Pot == p && e.BodyId == g.Key) is not Earmark e)
                                bs.Earmarks.Add(e = new Earmark { Pot = p, BodyId = g.Key });
                            e.Amount += cut;
                            continue;
                        }
                        part -= cut;
                        bs.Pots[(int)Pot.Reserve].Saved += cut;
                        bs.RolledToday += cut;
                    }
            }
            ps.Saved += part;
        }
    }

    /// <summary>Each hour after spending: earmarks for places no longer cut off are let go, full pots overflow, and once a day the books turn over.</summary>
    public static void Update(Simulation sim, Side side)
    {
        var w = sim.World;
        if (!On(w)) return;
        var bs = Of(w, side);
        var items = Items(w, side);
        foreach (var e in bs.Earmarks.ToList())
            if (w.FindBody(e.BodyId) is not Body b || b.Owner != side || !Economy.Blockaded(w, b) || bs.Places.GetValueOrDefault(e.BodyId) != PlaceMode.Hold)
                bs.Earmarks.Remove(e);
        foreach (var e in bs.Earmarks) e.Amount = Math.Min(e.Amount, bs.Pots[(int)e.Pot].Saved);
        Overflow(w, side, bs, items, sim.Log);
        // The rival works its Reserve and idle pots every six hours, as a player would with "Send".
        if (Mind.Of(w, side) != null && w.Tick % 6 == 0) Sweep(w, side, bs, sim.Log);
        if (w.Day == bs.Day) return;
        bs.Day = w.Day;
        foreach (var ps in bs.Pots)
        {
            ps.Days.Add(ps.SpentToday);
            if (ps.Days.Count > 30) ps.Days.RemoveAt(0);
            ps.SpentToday = 0;
        }
        foreach (var id in bs.SpentAtRate.Keys.Concat(bs.SpentAtToday.Keys).Distinct().OrderBy(k => k).ToList())
            bs.SpentAtRate[id] = bs.SpentAtRate.GetValueOrDefault(id) * 29 / 30 + bs.SpentAtToday.GetValueOrDefault(id) / 30;
        bs.SpentAtToday.Clear();
        bs.RolledRate = bs.RolledRate * 29 / 30 + bs.RolledToday / 30;
        bs.RolledToday = 0;
        // Forget items and places that are gone.
        var keys = items.Select(i => i.Key).ToHashSet();
        bs.Ranked.RemoveAll(k => !keys.Contains(k));
        bs.Paused.RemoveAll(k => !keys.Contains(k));
        foreach (var id in bs.Places.Keys.Where(id => w.FindBody(id)?.Owner != side).OrderBy(k => k).ToList()) bs.Places.Remove(id);
        foreach (var ps in bs.Pots)
            if (ps.GoalKey != null && ps.GoalKey.StartsWith("order:", StringComparison.Ordinal) | ps.GoalKey.StartsWith("piece:", StringComparison.Ordinal)
                                   | ps.GoalKey.StartsWith("refit:", StringComparison.Ordinal) && !keys.Contains(ps.GoalKey))
            {
                sim.Log.Info("budget", "Goal dropped: its item is gone", new { side = side.ToString(), goal = ps.GoalName });
                ClearGoal(ps);
            }
        // The rival runs its budget on Auto: shares follow its queue, its wants and the war.
        if (Mind.Of(w, side) != null && Propose(w, side) is double[] shares)
        {
            Apply(bs, shares);
            bs.LastMove = Describe(w, side, shares);
            bs.LastMoveHour = w.Hour;
            sim.Log.Info("budget", "Shares moved", new { side = side.ToString(), shares = shares.Select(s => Math.Round(s)).ToArray() });
        }
        bs.Wanted.Clear();
    }

    /// <summary>
    /// The rival's Reserve: it keeps KeepDays of its whole income (more while under threat) and sends the rest to
    /// the pots whose live items are short, the shortest first, as a player would with "Send".
    /// </summary>
    internal static void Sweep(World w, Side side, BudgetState bs, GameLog log)
    {
        var reserve = bs.Pots[(int)Pot.Reserve];
        var items = Items(w, side);
        double Wanted(Pot p) => bs.Wanted.Count == Count ? bs.Wanted[(int)p] : 0;
        // A pot with nothing waiting on it and nothing wanted gives back what it holds above a few days of its share.
        foreach (var p in All)
        {
            if (p == Pot.Reserve || bs.Pots[(int)p].GoalKey != null || bs.Pots[(int)p].Locked || Wanted(p) > 0 || items.Any(i => i.Pot == p && !i.Paused)) continue;
            var idle = bs.Pots[(int)p].Saved - PerDay(w, side, p) * IdleDays;
            if (idle <= 1) continue;
            bs.Pots[(int)p].Saved -= idle;
            reserve.Saved += idle;
        }
        var keep = Economy.IncomePerDay(w, side) * (Threat(w, side) > 0 ? 2 * KeepDays : KeepDays);
        var spare = reserve.Saved - keep;
        if (spare <= 1) return;
        var needs = All.Where(p => p != Pot.Reserve)
            .Select(p => (Pot: p, Need: items.Where(i => i.Pot == p && i.Holds).Sum(i => i.Cost) + (bs.Wanted.Count == Count ? bs.Wanted[(int)p] : 0) - bs.Pots[(int)p].Saved))
            .Where(x => x.Need > 0).OrderByDescending(x => x.Need).ThenBy(x => x.Pot).ToList();
        foreach (var (pot, need) in needs)
        {
            var send = Math.Min(spare, need);
            if (send <= 0) break;
            reserve.Saved -= send;
            bs.Pots[(int)pot].Saved += send;
            spare -= send;
            log.Info("budget", "Reserve sent to a pot", new { side = side.ToString(), pot = pot.ToString(), amount = Math.Round(send, 1) });
        }
        // The rest goes to the pots with room below full (buildings, ships, then stations), wanted ones first: money in hand wins nothing.
        foreach (var p in new[] { Pot.Buildings, Pot.Ships, Pot.Stations }.OrderByDescending(Wanted).ThenBy(p => Array.IndexOf(new[] { Pot.Buildings, Pot.Ships, Pot.Stations }, p)))
        {
            if (spare <= 1) break;
            var room = FullAt(w, side, p) - bs.Pots[(int)p].Saved;
            if (room <= 1) continue;
            var send = Math.Min(spare, room);
            reserve.Saved -= send;
            bs.Pots[(int)p].Saved += send;
            spare -= send;
        }
    }

    /// <summary>Days of the side's whole income the rival keeps in Reserve before sending the rest on.</summary>
    public const double KeepDays = 5;

    /// <summary>Days of its share an idle pot of the rival's keeps before the rest goes back to Reserve.</summary>
    public const double IdleDays = 3;

    /// <summary>The most a pot holds before the rest overflows: its share for FullDays, or its goal, or the items it is paying for next, if more.</summary>
    public static double FullAt(World w, Side side, Pot pot)
    {
        var bs = Of(w, side);
        var ps = bs.Pots[(int)pot];
        var items = Items(w, side).Where(i => i.Pot == pot && !i.Paused).Sum(i => i.Cost);
        return Math.Max(Math.Max(PerDay(w, side, pot) * D.FullDays, ps.GoalCost), items) + bs.Earmarks.Where(e => e.Pot == pot).Sum(e => e.Amount);
    }

    private static void Overflow(World w, Side side, BudgetState bs, List<BudgetItem> items, GameLog log)
    {
        foreach (var p in All)
        {
            if (p == Pot.Reserve || p == bs.OverflowTo) continue;
            var ps = bs.Pots[(int)p];
            var full = FullAt(w, side, p);
            if (full <= 0 || ps.Saved <= full + 1e-9) continue;
            var extra = ps.Saved - full;
            ps.Saved = full;
            bs.Pots[(int)bs.OverflowTo].Saved += extra;
            bs.Overflowed += extra;
        }
    }

    private static void ClearGoal(PotState ps)
    {
        ps.GoalKey = null;
        ps.GoalName = null;
        ps.GoalCost = 0;
        ps.GoalBodyId = null;
    }

    // ---- Automation -------------------------------------------------------------------------------

    /// <summary>
    /// The shares automation wants: leaning from the starting shares towards what is queued in each pot (and goals
    /// still short), and towards ships and stations while the side is under threat. Locked pots keep theirs.
    /// Null when no pot would move by MinMove or more.
    /// </summary>
    public static double[]? Propose(World w, Side side)
    {
        var bs = Of(w, side);
        var now = bs.Pots.Select(p => p.Share).ToArray();
        var target = bs.BaseShares.Count == Count ? bs.BaseShares.ToArray() : StartShares(w, side);
        var items = Items(w, side);
        var demand = new double[Count];
        foreach (var it in items) if (!it.Paused && !it.Blocked && it.Pot != Pot.Reserve) demand[(int)it.Pot] += it.Cost;
        for (var i = 0; i < Math.Min(Count, bs.Wanted.Count); i++) if (i != (int)Pot.Reserve) demand[i] += bs.Wanted[i];
        foreach (var p in All)
        {
            var ps = bs.Pots[(int)p];
            if (ps.GoalKey != null && !items.Any(i => i.Key == ps.GoalKey)) demand[(int)p] += ps.GoalCost;
            demand[(int)p] = Math.Max(0, demand[(int)p] - ps.Saved);
        }
        var total = demand.Sum();
        if (total > 0)
        {
            var spendable = 100 - target[(int)Pot.Reserve];
            for (var i = 0; i < Count; i++)
                if (i != (int)Pot.Reserve) target[i] = (1 - D.DemandWeight) * target[i] + D.DemandWeight * spendable * demand[i] / total;
        }
        var threat = Threat(w, side);
        if (threat > 0)
        {
            var shift = Math.Min(D.ThreatShift * threat, target[(int)Pot.Buildings] + target[(int)Pot.Transport]);
            var fromB = shift * target[(int)Pot.Buildings] / Math.Max(1e-9, target[(int)Pot.Buildings] + target[(int)Pot.Transport]);
            target[(int)Pot.Buildings] -= fromB;
            target[(int)Pot.Transport] -= shift - fromB;
            target[(int)Pot.Ships] += shift / 2;
            target[(int)Pot.Stations] += shift / 2;
        }
        // Locked pots keep their share; the others fill the rest in proportion.
        var locked = bs.Pots.Select(p => p.Locked).ToArray();
        var lockedSum = Enumerable.Range(0, Count).Where(i => locked[i]).Sum(i => now[i]);
        var freeSum = Enumerable.Range(0, Count).Where(i => !locked[i]).Sum(i => target[i]);
        if (freeSum <= 0) return null;
        for (var i = 0; i < Count; i++) target[i] = locked[i] ? now[i] : target[i] * (100 - lockedSum) / freeSum;
        var rounded = Round(target, locked);
        return Enumerable.Range(0, Count).Any(i => Math.Abs(rounded[i] - now[i]) >= D.MinMove - 1e-9) ? rounded : null;
    }

    /// <summary>0 to 1: how pressed the side is (its planets blockaded or under claim).</summary>
    public static double Threat(World w, Side side)
    {
        var held = w.Bodies.Where(b => b.Owner == side && b.PopK > 0).ToList();
        if (held.Count == 0) return 0;
        var pressed = held.Count(b => Economy.Blockaded(w, b) || b.ClaimSide is Side c && c != side);
        return Math.Min(1, pressed * 2.0 / held.Count);
    }

    /// <summary>Whole percents adding up to 100 (locked ones as they are).</summary>
    private static double[] Round(double[] shares, bool[] locked)
    {
        var r = shares.Select((s, i) => locked[i] ? s : Math.Floor(s)).ToArray();
        var left = (int)Math.Round(100 - r.Sum());
        foreach (var i in Enumerable.Range(0, Count).Where(i => !locked[i]).OrderByDescending(i => shares[i] - Math.Floor(shares[i])).ThenBy(i => i))
        {
            if (left <= 0) break;
            r[i] += 1;
            left--;
        }
        return r;
    }

    internal static void Apply(BudgetState bs, double[] shares)
    {
        for (var i = 0; i < Count; i++) bs.Pots[i].Share = shares[i];
        Touch(bs);
    }

    /// <summary>"Ships 30 → 35 %, Buildings 25 → 20 %".</summary>
    public static string Describe(World w, Side side, double[] shares)
    {
        var bs = Of(w, side);
        return string.Join(", ", All.Where(p => Math.Abs(shares[(int)p] - bs.Pots[(int)p].Share) >= 0.5)
            .Select(p => $"{bs.Pots[(int)p].Name} {bs.Pots[(int)p].Share:0} → {shares[(int)p]:0} %"));
    }

    /// <summary>The player's budget area: Auto moves the shares and notes it, Advise offers them in Needs you, Manual leaves them.</summary>
    internal static void Steward(Simulation sim, Side side, Func<AutoArea, string, string, GameCommand, string, int?, int?, NeedLevel, bool> offer)
    {
        var w = sim.World;
        if (!On(w) || w.Hour < Of(w, side).LastMoveHour + 24 || Propose(w, side) is not double[] shares) return;
        var text = "Budget shares: " + Describe(w, side, shares);
        if (offer(AutoArea.Budget, "budget:shares", text, new SetBudgetSharesCommand(side, shares.ToList()), "Use these shares", null, null, NeedLevel.Advice))
        {
            var bs = Of(w, side);
            bs.LastMove = text;
            bs.LastMoveHour = w.Hour;
        }
    }

    /// <summary>Sets one pot's share; the other unlocked pots make up the rest in proportion.</summary>
    internal static double[] WithShare(BudgetState bs, Pot pot, double share)
    {
        var now = bs.Pots.Select(p => p.Share).ToArray();
        var locked = bs.Pots.Select((p, i) => p.Locked && i != (int)pot).ToArray();
        share = Math.Clamp(share, 0, 100 - Enumerable.Range(0, Count).Where(i => locked[i]).Sum(i => now[i]));
        var others = Enumerable.Range(0, Count).Where(i => i != (int)pot && !locked[i]).ToList();
        var rest = 100 - share - Enumerable.Range(0, Count).Where(i => locked[i]).Sum(i => now[i]);
        var otherSum = others.Sum(i => now[i]);
        var r = (double[])now.Clone();
        r[(int)pot] = share;
        foreach (var i in others) r[i] = otherSum > 0 ? now[i] * rest / otherSum : rest / others.Count;
        if (others.Count == 0) r[(int)pot] = 100 - Enumerable.Range(0, Count).Where(i => locked[i]).Sum(i => now[i]);
        return Round(r, locked.Select((l, i) => l || i == (int)pot).ToArray());
    }

    // ---- Views ------------------------------------------------------------------------------------

    /// <summary>When an item should be paid, from where it stands in its pot and the pot's share.</summary>
    public static string When(World w, Side side, BudgetItem it)
    {
        if (it.Paused) return "not while paused";
        if (it.Blocked) return "cut off";
        if (it.Stall != null) return it.Stall;
        var free = Free(w, side, it.Pot, it.Key);
        if (free >= it.Cost) return "ready now";
        if (!it.Next) return "when the rest are done";
        var d = Days(w, side, it.Pot, it.Cost - free);
        return d == "under a day" ? "ready within a day" : $"ready in {d.Replace("about ", "")}";
    }

    /// <summary>What a goal still needs and when it lands at the pot's share.</summary>
    public static string GoalLine(World w, Side side, Pot pot)
    {
        var ps = Of(w, side).Pots[(int)pot];
        if (ps.GoalKey == null) return "";
        var left = ps.GoalCost - ps.Saved;
        return left <= 0 ? "Ready: it pays as soon as it can start" : $"Ready in {Days(w, side, pot, left).Replace("about ", "")} at {PerDay(w, side, pot):0.#} a day";
    }

    /// <summary>Things worth saving for that are not queued yet: the next station of the line over each held body.</summary>
    public static List<(Pot Pot, string Key, string Name, double Cost, int? BodyId)> Projects(World w, Side side)
    {
        var list = new List<(Pot, string, string, double, int?)>();
        foreach (var it in Items(w, side).Where(i => !i.Paused)) list.Add((it.Pot, it.Key, it.What + (it.BodyId is int b && w.FindBody(b) is Body body ? $" at {body.Name}" : ""), it.Cost, it.BodyId));
        foreach (var b in w.Bodies.Where(b => b.Owner == side && b.PopK > 0).OrderBy(b => b.Id))
        {
            if (Starbases.At(w, b.Id) is Starbase st)
            {
                if (st.Side != side || Starbases.NextTier(st) is not StationTier up || !GameData.ShipClasses.TryGetValue(up.ClassId, out var cls)) continue;
                list.Add((Pot.Stations, $"upgrade:{st.Id}:{up.ClassId}", $"{cls.Name} at {b.Name}", Starbases.UpgradeCost(st, up.ClassId), b.Id));
            }
            else if (Starbases.TierAt(1) is StationTier first && GameData.ShipClasses.TryGetValue(first.ClassId, out var cls))
                list.Add((Pot.Stations, $"starbase:{b.Id}:{first.ClassId}", $"{cls.Name} at {b.Name}", cls.Cost, b.Id));
        }
        return list;
    }

    /// <summary>For each place: what it earns a day, what has been spent there a day (30-day average), and on what.</summary>
    public static List<(Body Body, double Earns, double Spends, string On)> Places(World w, Side side)
    {
        var bs = Of(w, side);
        var items = Items(w, side);
        var factor = Tech.IndustryFactor(w, side);
        return w.Bodies.Where(b => b.Owner == side && (Planets.IndustryPerDay(w, b) > 0 || bs.SpentAtRate.ContainsKey(b.Id) || items.Any(i => i.BodyId == b.Id)))
            .OrderByDescending(b => Planets.IndustryPerDay(w, b)).ThenBy(b => b.Id)
            .Select(b => (b, Planets.IndustryPerDay(w, b) * (double.IsFinite(factor) ? factor : 1), bs.SpentAtRate.GetValueOrDefault(b.Id),
                Economy.Blockaded(w, b) ? "blockaded" : string.Join(", ", items.Where(i => i.BodyId == b.Id).Select(i => i.What.ToLowerInvariant()).Distinct().Take(3)) is { Length: > 0 } s ? s : "nothing queued"))
            .ToList();
    }
}

// ---- Commands -----------------------------------------------------------------------------------

/// <summary>Set one pot's share by hand (the others make it up); it locks the pot so automation leaves it.</summary>
public sealed record SetBudgetShareCommand(Side Side, Pot Pot, double Share) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (!Budget.On(world)) return;
        var bs = Budget.Of(world, Side);
        Budget.Apply(bs, Budget.WithShare(bs, Pot, Share));
        if (Source != "auto") bs.Pots[(int)Pot].Locked = true;
        sim.Log.Info("budget", "Share set", new { side = Side.ToString(), pot = Pot.ToString(), share = bs.Pots[(int)Pot].Share, Source });
    }
}

/// <summary>Set all six shares at once (Auto's move, or the player accepting Advise's).</summary>
public sealed record SetBudgetSharesCommand(Side Side, List<double> Shares) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (!Budget.On(world) || Shares.Count != Budget.Count || Shares.Any(s => !(s >= 0)) || Math.Abs(Shares.Sum() - 100) > 0.5)
        {
            sim.Log.Warn("budget", "Shares must be six numbers adding up to 100", new { side = Side.ToString(), Shares, Source });
            return;
        }
        var bs = Budget.Of(world, Side);
        var shares = Shares.ToArray();
        for (var i = 0; i < Budget.Count; i++) if (bs.Pots[i].Locked) shares[i] = bs.Pots[i].Share;
        if (Math.Abs(shares.Sum() - 100) > 0.5) return;
        Budget.Apply(bs, shares);
        sim.Log.Info("budget", "Shares set", new { side = Side.ToString(), shares, Source });
    }
}

/// <summary>Lock or unlock a pot's share against automation.</summary>
public sealed record LockPotCommand(Side Side, Pot Pot, bool Locked) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (!Budget.On(world)) return;
        Budget.Of(world, Side).Pots[(int)Pot].Locked = Locked;
    }
}

/// <summary>Rename a pot (the six stay six).</summary>
public sealed record RenamePotCommand(Side Side, Pot Pot, string Name) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (!Budget.On(world)) return;
        var name = (Name ?? "").Trim();
        Budget.Of(world, Side).Pots[(int)Pot].Name = name.Length == 0 ? Budget.DefaultName(Pot) : name.Length > 32 ? name[..32] : name;
    }
}

/// <summary>Move saved Industry from one pot to another (Send from Reserve, or give a pot's savings back). Amount below 0: all of it.</summary>
public sealed record MoveIndustryCommand(Side Side, Pot From, Pot To, double Amount) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (!Budget.On(world) || From == To) return;
        var bs = Budget.Of(world, Side);
        var from = bs.Pots[(int)From];
        var amount = Math.Min(Amount < 0 ? from.Saved : Amount, Math.Max(0, from.Saved));
        if (amount <= 0) return;
        from.Saved -= amount;
        bs.Pots[(int)To].Saved += amount;
        sim.Log.Info("budget", "Industry moved between pots", new { side = Side.ToString(), from = From.ToString(), to = To.ToString(), amount = Math.Round(amount, 1), Source });
    }
}

/// <summary>Which pot a full pot's extra goes to.</summary>
public sealed record SetOverflowCommand(Side Side, Pot To) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (Budget.On(world)) Budget.Of(world, Side).OverflowTo = To;
    }
}

/// <summary>Set a pot's goal (an item's key or a project's), or drop it (Key null).</summary>
public sealed record SetGoalCommand(Side Side, Pot Pot, string? Key, string Name = "", double Cost = 0, int? BodyId = null) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (!Budget.On(world)) return;
        var ps = Budget.Of(world, Side).Pots[(int)Pot];
        if (Key == null || Cost <= 0)
        {
            ps.GoalKey = null;
            ps.GoalName = null;
            ps.GoalCost = 0;
            ps.GoalBodyId = null;
            sim.Log.Info("budget", "Goal dropped", new { side = Side.ToString(), pot = Pot.ToString(), Source });
            return;
        }
        ps.GoalKey = Key;
        ps.GoalName = Name;
        ps.GoalCost = Cost;
        ps.GoalBodyId = BodyId;
        sim.Log.Info("budget", "Goal set", new { side = Side.ToString(), pot = Pot.ToString(), Key, Name, Cost, Source });
    }
}

/// <summary>Move an item up (Delta below 0) or down its pot's list.</summary>
public sealed record RankItemCommand(Side Side, string Key, int Delta) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (!Budget.On(world)) return;
        var bs = Budget.Of(world, Side);
        var items = Budget.Items(world, Side);
        var it = items.Find(i => i.Key == Key);
        if (it == null) return;
        var keys = items.Select(i => i.Key).ToList();
        var mates = items.Where(i => i.Pot == it.Pot).Select(i => i.Key).ToList();
        var at = mates.IndexOf(Key);
        var to = Math.Clamp(at + Delta, 0, mates.Count - 1);
        if (to == at) return;
        var other = mates[to];
        var (a, b) = (keys.IndexOf(Key), keys.IndexOf(other));
        (keys[a], keys[b]) = (keys[b], keys[a]);
        bs.Ranked = keys;
        bs.CacheStamp = -1;
        sim.Log.Info("budget", "Item moved in its pot", new { side = Side.ToString(), Key, Delta, Source });
    }
}

/// <summary>Pause an item (it keeps its place; its share goes to the next one down) or start it again.</summary>
public sealed record PauseItemCommand(Side Side, string Key, bool Paused) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (!Budget.On(world)) return;
        var bs = Budget.Of(world, Side);
        if (Paused && !bs.Paused.Contains(Key)) bs.Paused.Add(Key);
        if (!Paused) bs.Paused.Remove(Key);
        bs.CacheStamp = -1;
        sim.Log.Info("budget", Paused ? "Item paused" : "Item started again", new { side = Side.ToString(), Key, Source });
    }
}

/// <summary>A cut-off place's share: roll it to Reserve, share it out to the rest of the pot, or hold it for the place.</summary>
public sealed record SetPlaceModeCommand(Side Side, int BodyId, PlaceMode Mode) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (!Budget.On(world) || world.FindBody(BodyId)?.Owner != Side) return;
        var bs = Budget.Of(world, Side);
        if (Mode == PlaceMode.Roll) bs.Places.Remove(BodyId);
        else bs.Places[BodyId] = Mode;
        sim.Log.Info("budget", "Place share set", new { side = Side.ToString(), BodyId, mode = Mode.ToString(), Source });
    }
}
