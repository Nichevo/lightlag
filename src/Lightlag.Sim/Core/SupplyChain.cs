using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

// ---- State -------------------------------------------------------------------------------------

/// <summary>A planet's own stock rule for one good (null parts: worked out by Auto, see Colonies.Target).</summary>
public sealed class StockRule
{
    /// <summary>Stock to keep here (replaces the siege-days target).</summary>
    public double? Keep { get; set; }

    /// <summary>Stock above this may be shipped away (default: above the keep level).</summary>
    public double? ExportAbove { get; set; }

    /// <summary>The planet asks for more below this (default: half the keep level).</summary>
    public double? ImportBelow { get; set; }

    public bool Empty => Keep == null && ExportAbove == null && ImportBelow == null;
}

/// <summary>The side's own settings for a run between two planets: an escort that goes with every load, a planet to go by.</summary>
public sealed class RouteSetting
{
    public int FromBodyId { get; set; }
    public int ToBodyId { get; set; }
    public int? EscortFleetId { get; set; }
    public int? ViaBodyId { get; set; }
}

/// <summary>A load delivered (for the chain view's amounts a day and the routes table).</summary>
public sealed record DeliveryNote(double Hour, int FromBodyId, int ToBodyId, string Good, double Amount, int FleetId, int? RunId);

/// <summary>A freighter lost with its load.</summary>
public sealed record CargoLossNote(double Hour, int FromBodyId, int ToBodyId, int Ships, double Amount);

/// <summary>What the supply chain screen remembers for a side: route settings, recent deliveries and losses.</summary>
public sealed class SupplyBook
{
    public List<RouteSetting> Routes { get; set; } = new();
    public List<DeliveryNote> Deliveries { get; set; } = new();
    public List<CargoLossNote> Losses { get; set; } = new();
}

// ---- What the screen shows ----------------------------------------------------------------------

public enum ChainNodeKind { Planet, Fleet }

public enum RouteRisk { Low, Medium, High }

public enum ProblemKind { Short, Risk, Soon, Stock }

/// <summary>A planet or fleet in the chain view, with a line on what it makes or where it is.</summary>
public sealed record ChainNode(ChainNodeKind Kind, int Id, string Name, string Sub);

/// <summary>
/// A good moving from one node to another: amount a day (over the last 30 days, loads under way
/// included), what the receiver needs a day, short (the receiver is under its ask line), at risk.
/// </summary>
public sealed record ChainFlow(ChainNodeKind FromKind, int FromId, ChainNodeKind ToKind, int ToId, string Good, double PerDay,
    double NeedPerDay, bool Short, RouteRisk Risk, string Label);

/// <summary>One row of the routes table.</summary>
public sealed record RouteRow(string Key, string Name, int FromBodyId, int? ToBodyId, int? ToFleetId, List<string> Goods, string Ships,
    string HowOften, RouteRisk Risk, string RiskWhy, bool Fixed, int? FixedRunId, int? EscortFleetId, int? ViaBodyId, double EveryDays,
    string? Status = null, bool AutoRun = false, string? Why = null);

/// <summary>A one-click fix: a command to give (with what it costs), or something for the screen to do (Ui).</summary>
public sealed record ChainFix(string Label, string Cost, GameCommand? Command = null, string? Ui = null);

/// <summary>A problem in the supply chain, ranked by Auto (Short, Risk, Soon, Stock, then how bad).</summary>
public sealed record ChainProblem(ProblemKind Kind, string Key, string Title, string Cause, List<ChainFix> Fixes, double Severity,
    int? BodyId = null, int? FleetId = null, string? Good = null);

/// <summary>Freighters and tankers: how many, how many busy, the idle ones and where; losses in the last year.</summary>
public sealed record HaulerUse(int Ships, int Busy, List<string> Idle, int LostShips, double LostCargo);

/// <summary>
/// The supply chain screen: where every good comes from and where it goes (the chain view), every run
/// that moves goods with its risk (routes), what is wrong and the one-click fixes (problems), and each
/// planet's stock rules (keep, export above, import below, siege mode). It only reads the world; every
/// change goes through the commands below, which all take effect at once, paused or not.
/// </summary>
public static class SupplyChain
{
    /// <summary>Days of deliveries the amounts a day are worked out over.</summary>
    public const double WindowDays = 30;

    /// <summary>Siege mode: exports stop, the siege-days target is at least this, and set keep levels rise by the factor.</summary>
    public const double SiegeModeDays = 90, SiegeKeepFactor = 1.5;

    /// <summary>Rival warships seen near a run within this many days count towards its risk.</summary>
    public const double SightingDays = 10;

    /// <summary>A fleet whose food runs out within this many days (or whose fuel or ammunition is low) is listed as Soon.</summary>
    public const double SoonDays = 20;

    public static SupplyBook Book(World w, Side side) => Economy.Of(w, side).Supply;

    public static StockRule? Rule(Body b, string good) => b.StockRules.TryGetValue(good, out var r) ? r : null;

    // ---- Hooks from Colonies --------------------------------------------------------------------

    /// <summary>A load set out: the side's route setting for the run sends its escort and goes by its planet.</summary>
    public static void OnStart(Simulation sim, Shipment s, Fleet hauler)
    {
        var w = sim.World;
        if (Book(w, s.Side).Routes.Find(r => r.FromBodyId == s.FromBodyId && r.ToBodyId == s.ToBodyId) is not RouteSetting route) return;
        if (route.ViaBodyId is int via && w.FindBody(via) is Body v && v.Owner == s.Side) s.ViaBodyId = via;
        if (route.EscortFleetId is int e && w.FindFleet(e) is Fleet escort && escort.Side == s.Side && escort.Id != hauler.Id && escort.ShipIds.Count > 0
            && escort.Refit == null && !escort.Retreating)
        {
            escort.Mission = Mission.Escort;
            escort.MissionFleetId = hauler.Id;
            escort.MissionBodyId = null;
        }
    }

    public static void NoteDelivery(World w, Shipment s)
    {
        var book = Book(w, s.Side);
        foreach (var (good, amount) in s.Cargo.OrderBy(c => c.Key, StringComparer.Ordinal))
            book.Deliveries.Add(new DeliveryNote(w.Hour, s.FromBodyId, s.ToBodyId, good, amount, s.FleetId, s.RunId));
        book.Deliveries.RemoveAll(d => w.Hour - d.Hour > WindowDays * 24 * 2);
    }

    public static void NoteLoss(World w, Shipment s, int ships)
    {
        var book = Book(w, s.Side);
        book.Losses.Add(new CargoLossNote(w.Hour, s.FromBodyId, s.ToBodyId, Math.Max(1, ships), s.Loaded ? s.Cargo.Values.Sum() : 0));
        book.Losses.RemoveAll(l => w.Hour - l.Hour > 365 * 24);
    }

    // ---- Risk -----------------------------------------------------------------------------------

    private static IEnumerable<(Vec2 A, Vec2 B)> Legs(Vec2 from, Vec2 to, Vec2? via) =>
        via is Vec2 v ? new[] { (from, v), (v, to) } : new[] { (from, to) };

    /// <summary>
    /// How risky a run is, from what this side knows: cargo lost near it lately (+2), rival warships seen
    /// near it in the last ten days (+1 each, up to 2), less one for an escort and one for a guard zone on it.
    /// </summary>
    public static (RouteRisk Risk, string Why) RiskOf(World w, Side side, Vec2 from, Vec2 to, Vec2? via, bool escorted)
    {
        using var timed = SectionTimer.Time("supply-chain");
        var legs = Legs(from, to, via).ToList();
        var score = 0;
        var why = new List<string>();
        var losses = w.LossSpots.Count(l => l.Side == side && w.Hour - l.Hour < Lanes.LossDays * 24 && legs.Any(g => Lanes.DistToSegment(l.At, g.A, g.B) < Lanes.LossRadiusMm));
        if (losses > 0) { score += 2; why.Add(losses == 1 ? "cargo lost near it lately" : $"cargo lost near it {losses} times lately"); }
        var seen = Intel.FleetsNear(w, side, legs, SightingDays, Lanes.LossRadiusMm);
        if (seen > 0) { score += Math.Min(2, seen); why.Add(seen == 1 ? "a rival fleet seen near it in 10 days" : $"{seen} rival fleets seen near it in 10 days"); }
        if (score > 0 && escorted) { score--; why.Add("escorted"); }
        if (score > 0 && w.Zones.Any(z => z.Side == side && z.Kind == ZoneKind.Guard && w.Fleets.Any(f => f.ZoneId == z.Id && f.ShipIds.Count > 0)
                                          && legs.Any(g => Lanes.DistToSegment(z.Center, g.A, g.B) < z.RadiusMm + Lanes.LossRadiusMm)))
        {
            score--;
            why.Add("a guard zone covers it");
        }
        var risk = score >= 3 ? RouteRisk.High : score >= 1 ? RouteRisk.Medium : RouteRisk.Low;
        return (risk, why.Count == 0 ? "nothing seen near it" : string.Join(", ", why));
    }

    // ---- Routes ---------------------------------------------------------------------------------

    private static string Plural(int n, string one) => n == 1 ? $"1 {one}" : $"{n} {one}s";

    /// <summary>Every run moving goods: Auto runs (from recent and current loads), fixed runs, and supply runs to fleets.</summary>
    public static List<RouteRow> Routes(World w, Side side)
    {
        var rows = new List<RouteRow>();
        var book = Book(w, side);
        var window = Window(w);
        var recent = book.Deliveries.Where(d => w.Hour - d.Hour <= window * 24).ToList();
        var under = w.Shipments.Where(s => s.Side == side).OrderBy(s => s.Id).ToList();

        // Fixed runs first, each its own row.
        foreach (var run in w.FixedRuns.Where(r => r.Side == side).OrderBy(r => r.Id))
        {
            if (w.FindBody(run.FromBodyId) is not Body from || w.FindBody(run.ToBodyId) is not Body to) continue;
            var goods = run.Good == "" ? new List<string> { "all spare" } : new List<string> { run.Good };
            var fleets = recent.Where(d => d.RunId == run.Id).Select(d => d.FleetId).Concat(under.Where(s => s.RunId == run.Id).Select(s => s.FleetId)).Distinct().ToList();
            var setting = book.Routes.Find(r => r.FromBodyId == from.Id && r.ToBodyId == to.Id);
            var escort = run.EscortFleetId ?? setting?.EscortFleetId;
            var via = setting?.ViaBodyId is int v ? w.FindBody(v) : null;
            var (risk, why) = RiskOf(w, side, from.Position, to.Position, via?.Position, escort is int e && w.FindFleet(e) != null);
            rows.Add(new RouteRow($"run:{run.Id}", $"{from.Name} → {to.Name}", from.Id, to.Id, null, goods, ShipsText(w, fleets),
                $"every {run.EveryDays:0.#} d", risk, why, true, run.Id, escort, via?.Id, run.EveryDays, ShortRuns.Status(w, run), run.Auto, run.Why));
        }

        // Auto runs: one row per pair of planets, from loads delivered lately and loads under way.
        var pairs = recent.Where(d => d.RunId == null).Select(d => (d.FromBodyId, d.ToBodyId))
            .Concat(under.Where(s => s.RunId == null).Select(s => (s.FromBodyId, s.ToBodyId)))
            .Distinct().OrderBy(p => p.FromBodyId).ThenBy(p => p.ToBodyId).ToList();
        foreach (var (a, b) in pairs)
        {
            if (w.FindBody(a) is not Body from || w.FindBody(b) is not Body to) continue;
            var done = recent.Where(d => d.RunId == null && d.FromBodyId == a && d.ToBodyId == b).ToList();
            var now = under.Where(s => s.RunId == null && s.FromBodyId == a && s.ToBodyId == b).ToList();
            var goods = done.Select(d => d.Good).Concat(now.SelectMany(s => s.Cargo.Keys)).Distinct().OrderBy(g => g, StringComparer.Ordinal).ToList();
            var fleets = done.Select(d => d.FleetId).Concat(now.Select(s => s.FleetId)).Distinct().ToList();
            var loads = done.Select(d => d.Hour).Distinct().Count() + now.Count;
            var every = loads > 0 ? window / loads : window;
            var setting = book.Routes.Find(r => r.FromBodyId == a && r.ToBodyId == b);
            var via = setting?.ViaBodyId is int v ? w.FindBody(v) : null;
            var escorted = setting?.EscortFleetId is int e && w.FindFleet(e) != null;
            var (risk, why) = RiskOf(w, side, from.Position, to.Position, via?.Position, escorted);
            var often = loads <= 1 ? (done.Count == 0 ? "first load" : "once lately") : $"every {every:0.#} d";
            rows.Add(new RouteRow($"auto:{a}:{b}", $"{from.Name} → {to.Name}", a, b, null, goods, ShipsText(w, fleets), often, risk, why,
                false, null, setting?.EscortFleetId, via?.Id, Math.Max(1, Math.Round(every))));
        }

        // Supply runs to fleets.
        foreach (var f in w.Fleets.Where(f => f.Side == side && f.SupplyRunFor != null && f.ShipIds.Count > 0).OrderBy(f => f.Id))
        {
            if (w.FindFleet(f.SupplyRunFor!.Value) is not Fleet target) continue;
            var source = SourceOf(w, f);
            var goods = SupplyGoods(w, f);
            var escorted = w.Fleets.Any(o => o.Side == side && o.Mission == Mission.Escort && o.MissionFleetId == f.Id);
            var (risk, why) = source != null ? RiskOf(w, side, source.Position, target.Position, null, escorted) : (RouteRisk.Low, "");
            rows.Add(new RouteRow($"supply:{f.Id}", $"{source?.Name ?? "?"} → {target.Name}", source?.Id ?? -1, null, target.Id, goods, f.Name,
                "on call", risk, why, !f.AutoRun, null, null, null, 0));
        }
        return rows;
    }

    private static string ShipsText(World w, List<int> fleetIds)
    {
        var fleets = fleetIds.Select(id => w.FindFleet(id)).Where(f => f != null).Cast<Fleet>().ToList();
        if (fleets.Count == 0) return fleetIds.Count == 0 ? "none yet" : "lost";
        if (fleets.Count == 1) return fleets[0].Name;
        return Plural(fleets.Sum(f => f.ShipIds.Count), "freighter");
    }

    private static List<string> SupplyGoods(World w, Fleet f)
    {
        var kinds = w.ShipsOf(f).Select(s => Fitting.Spec(s).CargoKind).Distinct().ToList();
        var goods = new List<string>();
        if (kinds.Contains("fuel")) { goods.Add("fuel"); goods.Add("ammunition"); }
        if (kinds.Contains("food")) goods.Add("food");
        return goods;
    }

    /// <summary>Where a supply fleet loads: the held planet it orbits, else the nearest held planet with ground.</summary>
    public static Body? SourceOf(World w, Fleet f) =>
        f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int at && w.FindBody(at) is Body b && b.Owner == f.Side ? b
            : w.Bodies.Where(x => x.Owner == f.Side && x.Board != null).OrderBy(x => Vec2.Distance(x.Position, f.Position)).ThenBy(x => x.Id).FirstOrDefault();

    private static double Window(World w) => Math.Clamp(w.Hour / 24, 1, WindowDays);

    // ---- Chain view -----------------------------------------------------------------------------

    /// <summary>The planets (with ground or a store) and the fleets supplied by a run.</summary>
    public static List<ChainNode> Nodes(World w, Side side)
    {
        var nodes = w.Bodies.Where(b => b.Owner == side && (b.Board != null || b.Stock.Count > 0)).OrderBy(b => b.Id)
            .Select(b => new ChainNode(ChainNodeKind.Planet, b.Id, b.Name, MakesLine(b))).ToList();
        foreach (var f in w.Fleets.Where(f => f.Side == side && f.SupplyRunFor != null).OrderBy(f => f.Id))
            if (w.FindFleet(f.SupplyRunFor!.Value) is Fleet t && nodes.All(n => n.Kind != ChainNodeKind.Fleet || n.Id != t.Id))
                nodes.Add(new ChainNode(ChainNodeKind.Fleet, t.Id, t.Name, $"{t.ShipIds.Count} ships"));
        return nodes;
    }

    /// <summary>A short line on what a planet makes: its biggest goods, the shipyard.</summary>
    public static string MakesLine(Body b)
    {
        var made = Colonies.Goods.Select(g => (g.Id, Per: Colonies.MadePerDay(b, g.Id))).Where(x => x.Per > 0.05)
            .OrderByDescending(x => x.Per).ThenBy(x => x.Id, StringComparer.Ordinal).Take(3).Select(x => Colonies.GoodName(x.Id).ToLowerInvariant()).ToList();
        if (b.Shipyard) made.Add("yard");
        if (made.Count == 0) return b.Board == null ? "outpost" : b.Home ? "home" : "makes nothing yet";
        return string.Join(", ", made);
    }

    /// <summary>Goods moving between planets and supplies to fleets.</summary>
    public static List<ChainFlow> Flows(World w, Side side)
    {
        var flows = new List<ChainFlow>();
        var book = Book(w, side);
        var window = Window(w);
        var amounts = new Dictionary<(int From, int To, string Good), double>();
        foreach (var d in book.Deliveries.Where(d => w.Hour - d.Hour <= window * 24))
            amounts[(d.FromBodyId, d.ToBodyId, d.Good)] = amounts.GetValueOrDefault((d.FromBodyId, d.ToBodyId, d.Good)) + d.Amount;
        foreach (var s in w.Shipments.Where(s => s.Side == side))
            foreach (var (good, amount) in s.Cargo)
                amounts[(s.FromBodyId, s.ToBodyId, good)] = amounts.GetValueOrDefault((s.FromBodyId, s.ToBodyId, good)) + amount;
        var routes = Routes(w, side);
        foreach (var ((a, b, good), amount) in amounts.OrderBy(x => x.Key.From).ThenBy(x => x.Key.To).ThenBy(x => x.Key.Good, StringComparer.Ordinal))
        {
            if (w.FindBody(a) is not Body from || w.FindBody(b) is not Body to || to.Owner != side) continue;
            var per = amount / window;
            var need = Math.Max(0, Colonies.UsePerDay(to, good) - Colonies.MadePerDay(to, good));
            var isShort = Colonies.Held(to, good) < AskLine(to, good) - 1e-6;
            var risk = routes.Where(r => r.FromBodyId == a && r.ToBodyId == b).Select(r => r.Risk).DefaultIfEmpty(RouteRisk.Low).Max();
            var name = Colonies.GoodName(good).ToLowerInvariant();
            var label = FormattableString.Invariant($"{name} {per:0.#}/d") + (isShort && need > per * 1.05 ? FormattableString.Invariant($" · needs {need:0.#}") : "")
                        + (risk != RouteRisk.Low ? " · at risk" : "");
            flows.Add(new ChainFlow(ChainNodeKind.Planet, a, ChainNodeKind.Planet, b, good, per, need, isShort, risk, label));
        }
        foreach (var r in routes.Where(r => r.ToFleetId != null))
        {
            if (w.FindFleet(r.ToFleetId!.Value) is not Fleet target || r.FromBodyId < 0) continue;
            var low = Combat.SupplyShare(w, target) < 1.0 / 3 || FoodDays(w, target) is double d && d < SoonDays;
            var per = target.ShipIds.Count * 0.5;
            var goods = string.Join(", ", r.Goods.Select(g => g == "ammunition" ? "ammo" : g));
            flows.Add(new ChainFlow(ChainNodeKind.Planet, r.FromBodyId, ChainNodeKind.Fleet, target.Id, r.Goods.FirstOrDefault() ?? "fuel", per, per, low, r.Risk,
                $"{goods} ({r.Ships})" + (r.Risk != RouteRisk.Low ? " · at risk" : "")));
        }
        return flows;
    }

    /// <summary>The chain filters: all goods, the alloys chain (what alloys are made from and go into), fuel and ammunition to fleets, at risk, short.</summary>
    public static readonly string[] Filters = { "all", "alloys", "fuel-ammo", "risk", "short" };

    public static string FilterName(string f) => f switch
    {
        "all" => "All goods", "alloys" => "Alloys chain", "fuel-ammo" => "Fuel and ammo to fleets", "risk" => "At risk only", "short" => "Short only",
        _ => f.StartsWith("good:", StringComparison.Ordinal) ? $"{Colonies.GoodName(f[5..])} chain" : f,
    };

    public static bool Shows(string filter, ChainFlow flow) => filter switch
    {
        "all" => true,
        "alloys" => ChainOf("alloys").Contains(flow.Good),
        "fuel-ammo" => flow.ToKind == ChainNodeKind.Fleet || flow.Good is "fuel" or "ammunition",
        "risk" => flow.Risk != RouteRisk.Low,
        "short" => flow.Short,
        _ => !filter.StartsWith("good:", StringComparison.Ordinal) || ChainOf(filter[5..]).Contains(flow.Good),
    };

    /// <summary>A good, what it is made from (all the way back) and what is made from it.</summary>
    public static HashSet<string> ChainOf(string good)
    {
        var kinds = Colonies.Kinds.ToList();
        var set = new HashSet<string> { good };
        var todo = new Queue<string>(new[] { good });
        while (todo.Count > 0)
        {
            var g = todo.Dequeue();
            foreach (var k in kinds.Where(k => k.Makes != null && k.Makes.ContainsKey(g) && k.Uses != null))
                foreach (var input in k.Uses!.Keys.OrderBy(x => x, StringComparer.Ordinal))
                    if (set.Add(input)) todo.Enqueue(input);
        }
        foreach (var k in kinds.Where(k => k.Uses != null && k.Uses.ContainsKey(good) && k.Makes != null))
            foreach (var made in k.Makes!.Keys) set.Add(made);
        return set;
    }

    // ---- Stock rules ----------------------------------------------------------------------------

    /// <summary>Below this the planet asks for more: its import-below rule, else half its keep level.</summary>
    public static double AskLine(Body b, string good) =>
        Rule(b, good)?.ImportBelow ?? Colonies.Target(b, good) * Colonies.C.AskBelow;

    /// <summary>Above this stock may be shipped away: its export-above rule (not under what is waited for), else its keep level.</summary>
    public static double ExportLine(Body b, string good) =>
        Rule(b, good)?.ExportAbove is double above ? Math.Max(above, Colonies.Want(b, good)) : Colonies.Target(b, good);

    /// <summary>The stock targets row text for a planet and good: "keep 80", "import &lt;20", "export &gt;30".</summary>
    public static string RuleText(Body b, string good)
    {
        var r = Rule(b, good);
        var parts = new List<string>();
        var keep = Colonies.Target(b, good);
        if (r?.Keep != null || keep >= 0.5) parts.Add(FormattableString.Invariant($"keep {keep:0}"));
        if (r?.ImportBelow is double imp) parts.Add(FormattableString.Invariant($"import <{imp:0}"));
        if (r?.ExportAbove is double exp) parts.Add(FormattableString.Invariant($"export >{exp:0}"));
        if (b.SiegeMode && parts.Count > 0) parts.Add("(siege)");
        return parts.Count == 0 ? "—" : string.Join(" ", parts);
    }

    // ---- Problems -------------------------------------------------------------------------------

    /// <summary>Days of food the fleet's crews have left (the hungriest ship), null with no crews.</summary>
    public static double? FoodDays(World w, Fleet f)
    {
        var crewed = w.ShipsOf(f).Where(s => SupplyLines.MaxFood(s) > 0).ToList();
        return crewed.Count == 0 ? null : crewed.Min(s => s.Food);
    }

    /// <summary>Everything wrong in the side's supply chain with the fixes for it, ranked: Short, Risk, Soon, Stock, then how bad.</summary>
    public static List<ChainProblem> Problems(World w, Side side, int max = 12)
    {
        using var timed = SectionTimer.Time("supply-chain");
        var list = new List<ChainProblem>();
        var state = Economy.Of(w, side);
        Shorts(w, side, list);
        Risks(w, side, list);
        Soon(w, side, list);
        Stocks(w, side, list);
        return list.Where(p => !state.Dismissed.ContainsKey(p.Key))
            .OrderBy(p => p.Kind).ThenByDescending(p => p.Severity).ThenBy(p => p.Key, StringComparer.Ordinal).Take(max).ToList();
    }

    private static string Industry(double cost) => FormattableString.Invariant($"{cost:0} Industry");

    private static void Shorts(World w, Side side, List<ChainProblem> list)
    {
        var state = Economy.Of(w, side);
        var free = Colonies.FreeHaulers(w, side).Any();
        var planets = w.Bodies.Where(b => b.Owner == side && b.Board != null).OrderBy(b => b.Id).ToList();
        foreach (var to in planets)
            foreach (var good in Colonies.Goods.Select(g => g.Id))
            {
                var held = Colonies.Held(to, good);
                var line = AskLine(to, good);
                var waiting = held < Colonies.Want(to, good) - 1e-6;
                if (!(held < line - 1e-6 && (Colonies.Target(to, good) > 0.5 || Rule(to, good)?.ImportBelow != null)) && !waiting) continue;
                if (w.Shipments.Any(s => s.Side == side && s.ToBodyId == to.Id && s.Cargo.ContainsKey(good))) continue; // on its way
                var name = Colonies.GoodName(good).ToLowerInvariant();
                var use = Colonies.UsePerDay(to, good);
                var made = Colonies.MadePerDay(to, good);
                var title = use > made + 0.05
                    ? FormattableString.Invariant($"{Colonies.GoodName(good)}: {to.Name} makes {made:0.#} a day but uses {use:0.#}")
                    : FormattableString.Invariant($"{Colonies.GoodName(good)}: {to.Name} holds {held:0} of the {line:0} it asks for");
                var fixes = new List<ChainFix>();
                string cause;
                var source = w.Bodies.Where(b => b.Owner == side && b.Id != to.Id && (b.Board != null || b.Stock.Count > 0) && !Economy.Blockaded(w, b)
                                                 && Colonies.SpareOf(b, good) >= 1)
                    .OrderByDescending(b => Colonies.SpareOf(b, good)).ThenBy(b => b.Id).FirstOrDefault();
                if (source != null)
                {
                    var spare = Colonies.SpareOf(source, good);
                    if (!state.AutoTransport)
                    {
                        cause = FormattableString.Invariant($"{source.Name} has {spare:0} spare but Auto transport is off.");
                        fixes.Add(new ChainFix("Auto transport on", "free", new SetAutoTransportCommand(side, true)));
                    }
                    else if (!free) cause = Haulers(w, side).Ships == 0
                        ? FormattableString.Invariant($"{source.Name} has {spare:0} spare but you have no freighters.")
                        : FormattableString.Invariant($"{source.Name} has {spare:0} spare but every freighter is busy.");
                    else cause = FormattableString.Invariant($"{source.Name} has {spare:0} spare: Auto ships it on its next round.");
                    if (free)
                        fixes.Add(new ChainFix($"Ship from {source.Name}", "a free freighter",
                            new ShipGoodsCommand(side, source.Id, to.Id, good, Math.Max(20, Colonies.Target(to, good) * 1.5) - held)));
                    else if (FreighterFix(w, side) is ChainFix build) fixes.Add(build);
                }
                else
                {
                    var all = w.Bodies.Where(b => b.Owner == side).ToList();
                    var empireMade = all.Sum(b => Colonies.MadePerDay(b, good));
                    var empireUse = all.Sum(b => Colonies.UsePerDay(b, good));
                    cause = FormattableString.Invariant($"No planet has {name} to spare (made {empireMade:0.#} a day, used {empireUse:0.#}).");
                    if (BuildMakerFix(w, side, good, to) is ChainFix build) fixes.Add(build);
                    else cause += " Nothing that makes it can be built yet.";
                }
                fixes.Add(new ChainFix("Show chain", "", Ui: $"chain:{good}"));
                var severity = ShortSeverity(to, good);
                list.Add(new ChainProblem(ProblemKind.Short, $"chain:short:{to.Id}:{good}", title, cause, fixes, severity, to.Id, null, good));
            }
    }

    /// <summary>
    /// How bad a shortage is, for the Supply tab, Needs you and Auto transport alike: how far under its ask
    /// line the planet is, more if something waits for it (a building, a ship order, a repair), and more the
    /// faster it is used.
    /// </summary>
    public static double ShortSeverity(Body to, string good)
    {
        var line = AskLine(to, good);
        var held = Colonies.Held(to, good);
        return (line > 0 ? Math.Max(0, 1 - held / line) : 1) + (Colonies.Want(to, good) > held ? 0.5 : 0) + Colonies.UsePerDay(to, good) * 0.1;
    }

    /// <summary>A freighter from the shipyard (bulk once researched), if one can be queued.</summary>
    private static ChainFix? FreighterFix(World w, Side side)
    {
        var cls = SupplyLines.HaulerClass(w, side);
        if (Economy.CanQueue(w, side, OrderKind.Ship, cls) != null) return null;
        var data = GameData.ShipClass(cls);
        return new ChainFix($"Build {data.Name.ToLowerInvariant()}", Industry(data.Cost), new QueueOrderCommand(side, OrderKind.Ship, cls));
    }

    /// <summary>The building that makes a good, on the planet that wants it if it fits (else the best other one), as a fix.</summary>
    private static ChainFix? BuildMakerFix(World w, Side side, string good, Body want)
    {
        var maker = Colonies.Kinds.Where(k => k.Makes != null && k.Makes.ContainsKey(good)).OrderBy(k => k.Cost).ThenBy(k => k.Id, StringComparer.Ordinal).FirstOrDefault();
        var places = w.Bodies.Where(b => b.Owner == side && b.Board != null).OrderBy(b => b.Id == want.Id ? 0 : 1)
            .ThenByDescending(b => maker?.Uses?.Keys.Sum(g => Colonies.Held(b, g)) ?? 0).ThenBy(b => b.Id).ToList();
        if (maker != null)
        {
            foreach (var b in places)
                if (Colonies.CanPlace(w, side, b.Id, maker.Id, null, null, false) == null)
                    return new ChainFix($"Build {maker.Name.ToLowerInvariant()} on {b.Name}", Industry(maker.Cost), new BuildOnPlanetCommand(side, b.Id, maker.Id));
            return null;
        }
        // A mineral: a mine on a free deposit of it.
        var mine = Planets.BuildingKind("mine");
        if (mine == null) return null;
        foreach (var b in places)
            foreach (var d in b.Board!.Deposits.Where(d => d.Mineral == good).OrderByDescending(d => d.Rich).ThenBy(d => d.Y).ThenBy(d => d.X))
                if (Colonies.CanPlace(w, side, b.Id, mine.Id, d.X, d.Y, false) == null)
                    return new ChainFix($"Build mine on {b.Name}", Industry(mine.Cost), new BuildOnPlanetCommand(side, b.Id, mine.Id, d.X, d.Y));
        return null;
    }

    private static void Risks(World w, Side side, List<ChainProblem> list)
    {
        foreach (var r in Routes(w, side).Where(r => r.Risk != RouteRisk.Low))
        {
            var fixes = new List<ChainFix>();
            var goods = string.Join(", ", r.Goods.Select(g => g == "all spare" ? g : Colonies.GoodName(g).ToLowerInvariant()));
            var title = $"{r.Name} {goods} run at {(r.Risk == RouteRisk.High ? "high" : "some")} risk";
            var cause = char.ToUpperInvariant(r.RiskWhy[0]) + r.RiskWhy[1..] + ".";
            var hauler = r.ToFleetId != null ? w.FindFleet(int.Parse(r.Key[7..])) : null;
            var near = hauler?.Position ?? w.FindBody(r.FromBodyId)?.Position ?? Vec2.Zero;
            var escort = FreeEscort(w, side, near);
            if (escort != null && r.EscortFleetId == null)
            {
                GameCommand cmd = r.ToFleetId != null && hauler != null
                    ? new SetMissionCommand(escort.Id, Mission.Escort, null, hauler.Id)
                    : new SetRouteCommand(side, r.FromBodyId, r.ToBodyId!.Value, escort.Id, r.ViaBodyId);
                fixes.Add(new ChainFix($"Escort with {escort.Name}", $"{escort.Name} leaves its post", cmd));
            }
            if (r.ToBodyId is int to && w.FindBody(r.FromBodyId) is Body from && w.FindBody(to) is Body dest && Detour(w, side, from, dest, r) is var (via, extraDays))
                fixes.Add(new ChainFix($"Go by {via.Name}", FormattableString.Invariant($"+{extraDays:0.#} days a load"),
                    new SetRouteCommand(side, from.Id, dest.Id, r.EscortFleetId, via.Id)));
            if (r.FixedRunId is int runId && w.FixedRuns.Find(x => x.Id == runId) is FixedRun run && run.EveryDays < 60)
                fixes.Add(new ChainFix("Run half as often", FormattableString.Invariant($"every {run.EveryDays * 2:0} d"),
                    new SetFixedRunEveryCommand(side, runId, run.EveryDays * 2)));
            if (hauler != null) fixes.Add(new ChainFix("Show", "", Ui: $"fleet:{hauler.Id}"));
            list.Add(new ChainProblem(ProblemKind.Risk, $"chain:risk:{r.Key}", title, cause, fixes, (int)r.Risk, r.ToBodyId, hauler?.Id));
        }
    }

    /// <summary>The nearest idle warfleet (no mission, not in a fight, not refitting).</summary>
    private static Fleet? FreeEscort(World w, Side side, Vec2 near) =>
        w.Fleets.Where(f => f.Side == side && Planner.Warfleet(w, f) && f.Mission == Mission.None && f.Flight == null && f.Chase == null
                            && !f.Retreating && f.Refit == null && f.ZoneId == null && !Heat.InBattle(w, f)
                            && !Book(w, side).Routes.Any(r => r.EscortFleetId == f.Id))
            .OrderBy(f => Vec2.Distance(f.Position, near)).ThenBy(f => f.Id).FirstOrDefault();

    /// <summary>The held planet to go by that makes the run safest for the least extra distance (null: none safer).</summary>
    private static (Body Via, double ExtraDays)? Detour(World w, Side side, Body from, Body to, RouteRow r)
    {
        var direct = Vec2.Distance(from.Position, to.Position);
        var speed = Colonies.FreeHaulers(w, side).Concat(w.Fleets.Where(f => f.Side == side && w.ShipsOf(f).Any(Colonies.IsHauler)))
            .Select(f => FleetPerformance.Of(w, f).MaxSpeed).DefaultIfEmpty(0).Max() * Economy.ReinforceSpeed;
        (Body, double)? best = null;
        var bestScore = double.MaxValue;
        foreach (var b in w.Bodies.Where(b => b.Owner == side && b.Id != from.Id && b.Id != to.Id && b.Id != r.ViaBodyId).OrderBy(b => b.Id))
        {
            var (risk, _) = RiskOf(w, side, from.Position, to.Position, b.Position, r.EscortFleetId != null);
            if (risk >= r.Risk) continue;
            var extra = Vec2.Distance(from.Position, b.Position) + Vec2.Distance(b.Position, to.Position) - direct;
            var score = (int)risk * 1e9 + extra;
            if (score >= bestScore) continue;
            bestScore = score;
            best = (b, speed > 0 ? extra / speed / 24 : 0);
        }
        return best;
    }

    private static void Soon(World w, Side side, List<ChainProblem> list)
    {
        var yard = Economy.ShipyardOf(w, side);
        foreach (var f in w.Fleets.Where(f => f.Side == side && Planner.Warfleet(w, f)).OrderBy(f => f.Id))
        {
            if (f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int at && w.FindBody(at)?.Owner == side) continue; // restocks where it is
            var food = FoodDays(w, f);
            var fuel = Combat.SupplyShare(w, f);
            var ammo = AmmoShare(w, f);
            string? what = food is double d && d < SoonDays ? d < 0.5 ? $"{f.Name} is out of food: its crews are hungry" : FormattableString.Invariant($"{f.Name} food runs out in {d:0} days")
                : fuel < 0.4 ? FormattableString.Invariant($"{f.Name} is down to {fuel:P0} fuel")
                : ammo < 0.3 ? FormattableString.Invariant($"{f.Name} is down to {ammo:P0} ammunition") : null;
            if (what == null) continue;
            var fixes = new List<ChainFix>();
            string cause;
            if (w.Fleets.Find(o => o.Side == side && o.SupplyRunFor == f.Id && o.ShipIds.Count > 0) is Fleet run)
            {
                var due = run.Flight is { } fl && (fl.TargetFleetId == f.Id || fl.TargetBodyId == null) ? fl.ArriveHour - w.Hour : (double?)null;
                cause = due is double h ? $"{run.Name} is on its way (about {Format(h)}). OK unless the run is caught." : $"{run.Name} supplies it on call.";
                fixes.Add(new ChainFix("Show", "", Ui: $"fleet:{run.Id}"));
            }
            else
            {
                var tanker = w.Fleets.Where(o => o.Side == side && Planner.SupplyFleet(w, o) && o.SupplyRunFor == null && !o.Retreating)
                    .OrderBy(o => Vec2.Distance(o.Position, f.Position)).ThenBy(o => o.Id).FirstOrDefault();
                var needsFood = food is double fd && fd < SoonDays;
                var freighter = needsFood ? w.Fleets.Where(o => o.Side == side && o.ShipIds.Count > 0 && w.ShipsOf(o).All(s => Fitting.Spec(s).CargoKind == "food")
                                                               && Colonies.FreeHaulers(w, side).Contains(o))
                    .OrderBy(o => Vec2.Distance(o.Position, f.Position)).ThenBy(o => o.Id).FirstOrDefault() : null;
                var send = needsFood ? freighter ?? tanker : tanker ?? freighter;
                cause = "No supply run serves it.";
                if (send != null) fixes.Add(new ChainFix($"Send {send.Name}", "on call", new SetSupplyRunCommand(side, send.Id, f.Id)));
                else if (Economy.CanQueue(w, side, OrderKind.Ship, needsFood ? "freighter" : "tanker") == null)
                {
                    var cls = GameData.ShipClass(needsFood ? "freighter" : "tanker");
                    fixes.Add(new ChainFix($"Build {cls.Name.ToLowerInvariant()}", Industry(cls.Cost), new QueueOrderCommand(side, OrderKind.Ship, cls.Id)));
                }
                if (yard != null) fixes.Add(new ChainFix("Show", "", Ui: $"fleet:{f.Id}"));
            }
            fixes.Add(new ChainFix("Dismiss", $"{Stewards.DismissDays:0} days", new DismissNeedCommand(side, $"chain:soon:{f.Id}")));
            var severity = 1 - Math.Min(food is double x ? x / SoonDays : 1, Math.Min(fuel / 0.4, ammo / 0.3));
            list.Add(new ChainProblem(ProblemKind.Soon, $"chain:soon:{f.Id}", what, cause, fixes, severity, null, f.Id));
        }
        foreach (var b in w.Bodies.Where(b => b.Owner == side && b.Board != null && Economy.Blockaded(w, b)).OrderBy(b => b.Id))
        {
            if (Colonies.HoldsOutDays(b) is not double days || days > 60) continue;
            var fixes = new List<ChainFix>();
            if (!b.SiegeMode) fixes.Add(new ChainFix("Siege mode", "exports stop", new SetSiegeModeCommand(side, b.Id, true)));
            fixes.Add(new ChainFix("Show", "", Ui: $"planet:{b.Id}"));
            fixes.Add(new ChainFix("Dismiss", $"{Stewards.DismissDays:0} days", new DismissNeedCommand(side, $"chain:siege:{b.Id}")));
            list.Add(new ChainProblem(ProblemKind.Soon, $"chain:siege:{b.Id}", FormattableString.Invariant($"{b.Name} is blockaded: holds out {days:0} days"),
                "No freighter gets in or out while the enemy holds its orbit.", fixes, 1 - days / 60, b.Id));
        }
    }

    private static double AmmoShare(World w, Fleet f)
    {
        double have = 0, full = 0;
        foreach (var s in w.ShipsOf(f)) { have += s.Ammo; full += SupplyLines.MaxAmmo(s); }
        return full > 0 ? have / full : 1;
    }

    private static void Stocks(World w, Side side, List<ChainProblem> list)
    {
        var mine = w.Bodies.Where(b => b.Owner == side && (b.Board != null || b.Stock.Count > 0)).OrderBy(b => b.Id).ToList();
        foreach (var b in mine)
            foreach (var good in Colonies.Goods.Select(g => g.Id))
            {
                var held = Colonies.Held(b, good);
                var cap = b.Board != null ? Colonies.StoreCap(b, good) : double.MaxValue;
                var name = Colonies.GoodName(good).ToLowerInvariant();
                if (b.Board != null && held >= cap * 0.98 && cap > 1 && Colonies.MadePerDay(b, good) > 0.05)
                {
                    var fixes = new List<ChainFix>();
                    var store = Planets.BuildingKind("storehouse");
                    if (store != null && Colonies.CanPlace(w, side, b.Id, store.Id, null, null, false) == null)
                        fixes.Add(new ChainFix("Build storehouse", Industry(store.Cost), new BuildOnPlanetCommand(side, b.Id, store.Id)));
                    fixes.Add(new ChainFix("Export all spare", "keeps none", new SetStockRuleCommand(side, b.Id, good, Rule(b, good)?.Keep, 0, Rule(b, good)?.ImportBelow)));
                    fixes.Add(new ChainFix("Dismiss", $"{Stewards.DismissDays:0} days", new DismissNeedCommand(side, $"chain:full:{b.Id}:{good}")));
                    list.Add(new ChainProblem(ProblemKind.Stock, $"chain:full:{b.Id}:{good}", FormattableString.Invariant($"{b.Name}'s {name} store is full ({held:0})"),
                        "What it makes now goes to waste.", fixes, 1, b.Id, null, good));
                    continue;
                }
                // Fuel and ammunition are drawn by fleets in orbit, so a planet that "doesn't use" them still does.
                if (held < 20 || good is Colonies.Fuel or "ammunition" || Colonies.UsePerDay(b, good) > 0.01 || Colonies.Want(b, good) > 0) continue;
                var spare = Colonies.SpareOf(b, good);
                var user = mine.Where(o => o.Id != b.Id && o.Board != null && Colonies.UsePerDay(o, good) > 0.01)
                    .OrderByDescending(o => Colonies.UsePerDay(o, good)).ThenBy(o => o.Id).FirstOrDefault();
                var stockFixes = new List<ChainFix>();
                string cause;
                if (b.SiegeMode) cause = "Siege mode keeps it here.";
                else if (user == null) cause = "No planet of yours uses it yet.";
                else if (spare < 1) cause = FormattableString.Invariant($"Its stock rule keeps it here ({RuleText(b, good)}); {user.Name} uses it.");
                else cause = Economy.Of(w, side).AutoTransport && Colonies.TransportWanted(w, side).Any(t => t.From.Id == b.Id && t.Good == good)
                    ? $"Auto ships it to {user.Name} on its next round."
                    : FormattableString.Invariant($"{user.Name} uses {Colonies.UsePerDay(user, good):0.#} a day and has enough for now.");
                if (user != null && spare >= 1 && Colonies.FreeHaulers(w, side).Any())
                    stockFixes.Add(new ChainFix($"Ship to {user.Name}", "a free freighter", new ShipGoodsCommand(side, b.Id, user.Id, good, spare)));
                stockFixes.Add(new ChainFix("Change stock", "", Ui: $"stock:{b.Id}:{good}"));
                stockFixes.Add(new ChainFix("OK", $"{Stewards.DismissDays:0} days", new DismissNeedCommand(side, $"chain:idle:{b.Id}:{good}")));
                list.Add(new ChainProblem(ProblemKind.Stock, $"chain:idle:{b.Id}:{good}", FormattableString.Invariant($"{b.Name} holds {held:0} {name} it cannot use"),
                    cause, stockFixes, held / 100, b.Id, null, good));
            }
    }

    // ---- Freighters -----------------------------------------------------------------------------

    public static HaulerUse Haulers(World w, Side side)
    {
        var fleets = w.Fleets.Where(f => f.Side == side && f.ShipIds.Count > 0 && w.ShipsOf(f).Any(Colonies.IsHauler)).OrderBy(f => f.Id).ToList();
        var free = Colonies.FreeHaulers(w, side).Select(f => f.Id).ToHashSet();
        var ships = fleets.Sum(f => w.ShipsOf(f).Count(Colonies.IsHauler));
        var idleFleets = fleets.Where(f => free.Contains(f.Id) || f.SupplyRunFor == null && !w.Shipments.Any(s => s.FleetId == f.Id) && f.Flight == null && f.PeopleRun == null).ToList();
        var idle = idleFleets.Select(f => f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int at && w.FindBody(at) is Body b ? $"{f.Name} at {b.Name}" : $"{f.Name} in space").ToList();
        var busy = ships - idleFleets.Sum(f => w.ShipsOf(f).Count(Colonies.IsHauler));
        var lost = Book(w, side).Losses.Where(l => w.Hour - l.Hour <= 365 * 24).ToList();
        return new HaulerUse(ships, busy, idle, lost.Sum(l => l.Ships), lost.Sum(l => l.Amount));
    }

    private static string Format(double hours) => hours < 48 ? FormattableString.Invariant($"{hours:0} hours") : FormattableString.Invariant($"{hours / 24:0.#} days");
}

// ---- Commands (all at once, paused or not) ------------------------------------------------------

/// <summary>Set a planet's stock rule for a good: keep, export above, import below (null: Auto works it out).</summary>
public sealed record SetStockRuleCommand(Side Side, int BodyId, string Good, double? Keep, double? ExportAbove, double? ImportBelow) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        static bool Bad(double? v) => v is double x && (double.IsNaN(x) || x < 0 || x > 100_000);
        if (world.FindBody(BodyId) is not Body b || b.Owner != Side || Colonies.Goods.All(g => g.Id != Good) || Bad(Keep) || Bad(ExportAbove) || Bad(ImportBelow))
        {
            sim.Log.Warn("supply", "Bad stock rule", new { side = Side.ToString(), BodyId, Good, Keep, ExportAbove, ImportBelow, Source });
            return;
        }
        var rule = new StockRule { Keep = Keep, ExportAbove = ExportAbove, ImportBelow = ImportBelow };
        Choices.Note(world, Side, "stock", rule.Empty ? "rule cleared" : rule.ExportAbove != null ? "export above" : rule.ImportBelow != null ? "import below" : "keep", b.Name, $"{Good}; {Source}");
        if (rule.Empty) b.StockRules.Remove(Good);
        else b.StockRules[Good] = rule;
        sim.Log.Info("supply", "Stock rule set", new { side = Side.ToString(), body = b.Name, Good, Keep, ExportAbove, ImportBelow, Source });
    }
}

/// <summary>Siege mode on a planet: its exports stop and its keep levels rise.</summary>
public sealed record SetSiegeModeCommand(Side Side, int BodyId, bool On) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.FindBody(BodyId) is not Body b || b.Owner != Side)
        {
            sim.Log.Warn("supply", "Bad siege mode order", new { side = Side.ToString(), BodyId, On, Source });
            return;
        }
        if (b.SiegeMode != On) Choices.Note(world, Side, "siege", On ? "siege mode on" : "siege mode off", b.Name, Source);
        b.SiegeMode = On;
        sim.Log.Info("supply", On ? "Siege mode on" : "Siege mode off", new { side = Side.ToString(), body = b.Name, Source });
    }
}

/// <summary>Set a run's escort (goes with every load) and the planet loads go by (both null: back to plain Auto).</summary>
public sealed record SetRouteCommand(Side Side, int FromBodyId, int ToBodyId, int? EscortFleetId, int? ViaBodyId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.FindBody(FromBodyId) is not Body from || world.FindBody(ToBodyId) is not Body to || from.Id == to.Id
            || EscortFleetId is int e && world.FindFleet(e)?.Side != Side
            || ViaBodyId is int v && (world.FindBody(v) is not Body via || via.Owner != Side || v == FromBodyId || v == ToBodyId))
        {
            sim.Log.Warn("supply", "Bad route order", new { side = Side.ToString(), FromBodyId, ToBodyId, EscortFleetId, ViaBodyId, Source });
            return;
        }
        var routes = SupplyChain.Book(world, Side).Routes;
        Choices.Note(world, Side, "escort", EscortFleetId != null ? "route escorted" : ViaBodyId != null ? "route via a planet" : "route back to Auto", $"{from.Name} to {to.Name}", Source);
        routes.RemoveAll(r => r.FromBodyId == FromBodyId && r.ToBodyId == ToBodyId);
        if (EscortFleetId != null || ViaBodyId != null)
            routes.Add(new RouteSetting { FromBodyId = FromBodyId, ToBodyId = ToBodyId, EscortFleetId = EscortFleetId, ViaBodyId = ViaBodyId });
        sim.Log.Info("supply", "Route set", new { side = Side.ToString(), from = from.Name, to = to.Name, EscortFleetId, ViaBodyId, Source });
    }
}

/// <summary>Change how often a fixed run goes.</summary>
public sealed record SetFixedRunEveryCommand(Side Side, int RunId, double EveryDays) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.FixedRuns.Find(r => r.Id == RunId && r.Side == Side) is not FixedRun run || EveryDays is < 1 or > 365 || double.IsNaN(EveryDays))
        {
            sim.Log.Warn("supply", "Bad fixed run change", new { side = Side.ToString(), RunId, EveryDays, Source });
            return;
        }
        run.NextHour += (EveryDays - run.EveryDays) * 24;
        run.EveryDays = EveryDays;
    }
}
