using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>
/// A variant: a saved fit for a class, with a name and a role. The class is the hull (size, slots, power,
/// heat); a variant is one way to fit it. Every class has the Standard variant (id 0, never stored), kept
/// current by the design manager. Auto variants are made and kept current by the design manager too;
/// your own variants are pinned: nothing changes them, but out-of-date parts are flagged.
/// </summary>
public sealed class Design
{
    /// <summary>The variant's id (from 1; 0 is the class's Standard). Old saves have 0 here until Designer.Update numbers them.</summary>
    public int Id { get; set; }
    public string ClassId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Fit { get; set; } = new();

    /// <summary>Made and kept current by the design manager (false: the player's own, pinned).</summary>
    public bool Auto { get; set; }

    /// <summary>The job it is for: "q-ship", "long-range" or "" (none).</summary>
    public string Role { get; set; } = "";
    public double SavedHour { get; set; }
}

/// <summary>Where a Q-ship gave itself away: raiders that side runs keep clear of supply convoys near here until the hour.</summary>
public sealed record QShipWarning(Vec2 At, double UntilHour, string ShipName);

/// <summary>A row of the models list: a fit of a class (its code), what it is now, and how many fly or are being built.</summary>
public sealed record ModelRow(string Code, List<string> Fit, string Status, string DesignName, int? DesignId, int InService, int Building, string Note);

/// <summary>One slot's pick against the best you could fit there now.</summary>
public sealed record SlotPick(int Slot, string Picked, string Best);

/// <summary>A line of a build bill: a part and the Industry and materials it takes (negative: a standard part left out).</summary>
public sealed record BillLine(string Part, double Industry, Dictionary<string, double> Materials);

/// <summary>
/// The design manager. It keeps each class's Standard up to date as research lands, adds Auto variants
/// when a new role makes sense (a Q-ship once Concealed mounts is in, a long-range fit for warships with
/// a free utility slot), scores picked against best for every slot, works out build bills, and lists a
/// class's models (each fit flying or being built, with its code): active, special, upgrade ready, old.
/// With Advise it offers each change in Needs you; with Manual the Standard stays as it is until you
/// update it. The rival's mind always runs it on Auto. Crewed or AI-run is chosen per class (AI cores).
/// </summary>
public static class Designer
{
    public const int StandardId = 0;
    public const string QShip = "q-ship";
    public const string LongRange = "long-range";
    public const string Lancer = "lancer";
    public const string ConcealedMounts = "concealed-mounts";

    /// <summary>Days a raider side stays wary of convoys near where a Q-ship opened fire.</summary>
    public const double WaryDays = 30;
    public const double WaryRadiusMm = 3000;

    /// <summary>A part fitted at build costs this share of its refit price in Industry.</summary>
    public const double BuildPartShare = 0.5;

    private static SideState S(World w, Side side) => Economy.Of(w, side);

    /// <summary>Does the design manager do its work by itself for this side (the rival always, the player on Auto)?</summary>
    public static bool OnAuto(World w, Side side) => Mind.Of(w, side) != null || Stewards.LevelFor(w, side, AutoArea.Designs) == AutoLevel.Auto;

    // ---- Variants --------------------------------------------------------------------------

    /// <summary>The class's stored variants (Auto and your own), oldest first. The Standard is not among them.</summary>
    public static List<Design> Of(World w, Side side, string classId) =>
        S(w, side).Designs.Where(d => d.ClassId == classId && d.Id > 0).OrderBy(d => d.Id).ToList();

    public static Design? Find(World w, Side side, int id) => id <= 0 ? null : S(w, side).Designs.Find(d => d.Id == id);

    public static string NameOf(World w, Side side, int id) => Find(w, side, id)?.Name ?? "Standard";

    /// <summary>The variant yards build for this class when no other is asked for.</summary>
    public static int DefaultFor(World w, Side side, string classId) =>
        S(w, side).DefaultDesign.TryGetValue(classId, out var id) && Find(w, side, id) is Design d && d.ClassId == classId ? id : StandardId;

    /// <summary>The Standard's fit now: the best fit live on Auto, else the last one kept (Advise or Manual freeze it).</summary>
    public static List<string> StandardFit(World w, Side side, ShipClass cls)
    {
        if (!OnAuto(w, side) && S(w, side).StandardFits.TryGetValue(cls.Id, out var kept) && Fitting.CanFit(w, side, cls, kept) == null) return kept.ToList();
        return Best(w, side, cls, Fitting.StandardFit(cls));
    }

    /// <summary>The fit a variant builds and refits to now (an unknown or unusable variant: the Standard).</summary>
    public static List<string> FitFor(World w, Side side, string classId, int designId)
    {
        var cls = GameData.ShipClass(classId);
        if (Find(w, side, designId) is not Design d || d.ClassId != classId) return StandardFit(w, side, cls);
        var fit = d.Auto && OnAuto(w, side) ? Best(w, side, cls, d.Fit) : d.Fit.ToList();
        return Fitting.CanFit(w, side, cls, fit) == null ? fit : StandardFit(w, side, cls);
    }

    /// <summary>The variant a new ship is built to: the one asked for, else the class default (the rival mixes in a Q-ship now and then).</summary>
    public static int ForBuild(World w, Side side, string classId, int? asked)
    {
        if (asked is int id && (id == StandardId || Find(w, side, id)?.ClassId == classId)) return id;
        if (Mind.Of(w, side) != null && Of(w, side, classId).Find(d => d.Role == QShip) is Design q)
        {
            var mine = w.Ships.Where(s => s.Side == side && s.ClassId == classId).ToList();
            if (mine.Count >= 2 && mine.Count(s => s.DesignId == q.Id) * 4 < mine.Count) return q.Id;
        }
        return DefaultFor(w, side, classId);
    }

    // ---- Best fit ----------------------------------------------------------------------------

    /// <summary>What a part is for, so it is only ever swapped for a better one of the same kind.</summary>
    public static string Family(ModuleData m)
    {
        if (m.Slot == "E") return "engine";
        var kind = m.Lances > 0 ? "lance" : m.Railguns > 0 ? "railgun" : m.Missiles > 0 ? "missile" : m.PointDefence > 0 ? "pd" : m.Shield > 0 ? "shield" : m.Armour > 0 ? "armour"
            : m.SensorMm > 0 ? "sensor" : m.Supplies > 0 ? "tank" : m.HeatSinks > 0 ? "sinks" : m.Id;
        return (m.Hidden ? "hidden-" : "") + kind;
    }

    /// <summary>How good a part is at its job (higher is better).</summary>
    public static double Score(ModuleData m) => Family(m) switch
    {
        "engine" => m.AccelFactor,
        var f when f.EndsWith("railgun", StringComparison.Ordinal) => m.Railguns,
        var f when f.EndsWith("missile", StringComparison.Ordinal) => m.Missiles,
        "pd" => m.PointDefence,
        "lance" => m.Lances,
        "shield" => m.Shield,
        "armour" => m.Armour,
        "sensor" => m.SensorMm,
        "tank" => m.Supplies,
        "sinks" => m.HeatSinks + m.HeatCapacity / 100,
        _ => 0,
    };

    /// <summary>The best part of the same kind this side can fit in a slot of this type (the part itself if nothing beats it).</summary>
    public static string Upgrade(World w, Side side, string slotType, string moduleId)
    {
        if (GameData.Module(moduleId) is not ModuleData m) return moduleId;
        if (m.Slot == "E") return Fitting.BestEngine(w, side) is { Length: > 0 } e ? e : moduleId;
        var family = Family(m);
        var best = GameData.Modules.Values
            .Where(x => x.Slot == slotType && Family(x) == family && (x.Needs.Length == 0 || Tech.Has(w, side, x.Needs)))
            .OrderByDescending(Score).ThenBy(x => x.Power).ThenBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault();
        return best != null && Score(best) > Score(m) + 1e-9 ? best.Id : moduleId;
    }

    /// <summary>A fit with every part swapped for the best of its kind, keeping inside the power and heat budgets (slots that would break them stay).</summary>
    public static List<string> Best(World w, Side side, ShipClass cls, IReadOnlyList<string> from)
    {
        var fit = from.Count == cls.SlotList.Count ? from.ToList() : Fitting.StandardFit(cls);
        // Parts this side can't fit any more (a lost research can't happen, but old data can): back to the standard part.
        for (var i = 0; i < fit.Count; i++)
            if (Fitting.CanUse(w, side, cls, i, fit[i]) != null) fit[i] = cls.SlotList[i].Fit;
        var best = fit.ToList();
        for (var i = 0; i < fit.Count; i++) best[i] = Upgrade(w, side, cls.SlotList[i].Type, fit[i]);
        for (var i = best.Count - 1; i >= 0 && Fitting.CanFit(w, side, cls, best) != null; i--)
            if (cls.SlotList[i].Type != "E") best[i] = fit[i];
        return Fitting.CanFit(w, side, cls, best) == null ? best : Fitting.CanFit(w, side, cls, fit) == null ? fit : Fitting.StandardFit(cls);
    }

    /// <summary>For each slot: the part picked and the best of its kind you could fit now.</summary>
    public static List<SlotPick> PickedVsBest(World w, Side side, ShipClass cls, IReadOnlyList<string> fit)
    {
        var best = Best(w, side, cls, fit);
        return Enumerable.Range(0, Math.Min(fit.Count, cls.SlotList.Count)).Select(i => new SlotPick(i, fit[i], best[i])).ToList();
    }

    /// <summary>Parts of this fit that are behind the best of their kind (for "3 of 5 slots could be better").</summary>
    public static int Behind(World w, Side side, ShipClass cls, IReadOnlyList<string> fit) => PickedVsBest(w, side, cls, fit).Count(p => p.Picked != p.Best);

    // ---- Costs -------------------------------------------------------------------------------

    /// <summary>Electronics an AI core takes for a ship of this class.</summary>
    public static double CoreElectronics(ShipClass cls) => Math.Ceiling(cls.Cost / 100 * War.D.Crews.AiCoreElectronicsPer100);

    /// <summary>
    /// The bill for building this fit: the hull with its standard fit (the class's cost and materials),
    /// each part that differs (what it adds less the standard part it replaces; parts at half their refit
    /// price in Industry), and the AI core for an AI-run ship.
    /// </summary>
    public static List<BillLine> Bill(ShipClass cls, IReadOnlyList<string> fit, bool aiRun)
    {
        var lines = new List<BillLine> { new("Hull and standard fit", cls.Cost, new Dictionary<string, double>(cls.Materials ?? new())) };
        for (var i = 0; i < Math.Min(fit.Count, cls.SlotList.Count); i++)
        {
            var slot = cls.SlotList[i];
            if (fit[i] == slot.Fit) continue;
            var add = GameData.Module(fit[i]);
            var std = GameData.Module(slot.Fit);
            var mats = new Dictionary<string, double>();
            foreach (var (g, v) in add?.Materials ?? new()) mats[g] = mats.GetValueOrDefault(g) + v * slot.Mounts;
            foreach (var (g, v) in std?.Materials ?? new()) mats[g] = mats.GetValueOrDefault(g) - v * slot.Mounts;
            var industry = ((add?.Cost ?? 0) - (std?.Cost ?? 0)) * BuildPartShare;
            var name = add == null ? $"No {std?.Name.ToLowerInvariant() ?? "part"}" : add.Name + (slot.Pair ? " pair" : "");
            lines.Add(new BillLine(name, industry, mats.Where(kv => Math.Abs(kv.Value) > 1e-9).ToDictionary(kv => kv.Key, kv => kv.Value)));
        }
        if (aiRun) lines.Add(new BillLine("AI core", 0, new Dictionary<string, double> { ["electronics"] = CoreElectronics(cls) }));
        return lines;
    }

    public static double Industry(ShipClass cls, IReadOnlyList<string> fit, bool aiRun) => Math.Max(cls.Cost * 0.5, Bill(cls, fit, aiRun).Sum(l => l.Industry));

    public static Dictionary<string, double> Materials(ShipClass cls, IReadOnlyList<string> fit, bool aiRun)
    {
        var total = new Dictionary<string, double>();
        foreach (var line in Bill(cls, fit, aiRun))
            foreach (var (g, v) in line.Materials) total[g] = total.GetValueOrDefault(g) + v;
        return total.Where(kv => kv.Value > 1e-9).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // ---- Crewed or AI-run --------------------------------------------------------------------

    /// <summary>Are new ships of this class AI-run for this side?</summary>
    public static bool AiRun(World w, Side side, string classId) =>
        Races.Of(w, side).AiOnly
        || S(w, side).AiRun.Contains(classId) && GameData.ShipClasses.TryGetValue(classId, out var c) && c.AiRunAllowed && Tech.Has(w, side, "ai-cores");

    /// <summary>Why ships of this class can't be AI-run for this side, or null.</summary>
    public static string? CanRunOnAi(World w, Side side, string classId)
    {
        if (!GameData.ShipClasses.TryGetValue(classId, out var cls)) return $"Unknown class '{classId}'";
        if (Races.Of(w, side).AiOnly) return null;
        if (!cls.AiRunAllowed) return $"{cls.Name}s carry people: they always have a crew";
        if (!Tech.Has(w, side, "ai-cores")) return $"Needs {GameData.Research("ai-cores").Name}";
        return null;
    }

    // ---- Models ------------------------------------------------------------------------------

    /// <summary>The code prefix (type, hull mark, variant letter) a fit has or would get; "new" letter while no ship has been built to it.</summary>
    public static string CodeFor(World w, Side side, ShipClass cls, IReadOnlyList<string> fit)
    {
        var letter = ShipNames.VariantLetter(w, side, cls, fit);
        return $"{cls.Short}{ShipNames.HullMark(w, side)}{(letter is char c ? c.ToString() : "·")}";
    }

    /// <summary>
    /// Every model of a class: each variant's current fit (Active for the default and Auto variants,
    /// Special for your own, Upgrade ready when ships of that variant fly but none on the new fit yet),
    /// then the older fits still flying (Old).
    /// </summary>
    public static List<ModelRow> Models(World w, Side side, string classId)
    {
        var cls = GameData.ShipClass(classId);
        var ships = w.Ships.Where(s => s.Side == side && s.ClassId == classId).ToList();
        var orders = w.Orders.Where(o => o.Side == side && o.Kind == OrderKind.Ship && o.Key == classId).ToList();
        var rows = new List<ModelRow>();
        var current = new HashSet<string>();
        var def = DefaultFor(w, side, classId);
        foreach (var id in new[] { StandardId }.Concat(Of(w, side, classId).Select(d => d.Id)))
        {
            var d = Find(w, side, id);
            var fit = FitFor(w, side, classId, id);
            var key = string.Join(",", fit);
            current.Add(key);
            var flying = ships.Count(s => s.DesignId == id && string.Join(",", Fitting.FitOf(s)) == key);
            var building = orders.Count(o => ForOrder(w, o) == id);
            var behind = ships.Count(s => s.DesignId == id && string.Join(",", Fitting.FitOf(s)) != key);
            var status = d is { Auto: false } ? "Special" : "Active";
            var note = d == null ? (OnAuto(w, side) ? "Kept current by Auto" : "Kept as it is until you update it")
                : d.Auto ? $"Auto variant ({RoleName(d.Role)})" : "Your design: pinned, Auto only flags old parts";
            if (flying == 0 && building == 0 && behind > 0) { status = "Upgrade ready"; note = $"{behind} ship{(behind == 1 ? "" : "s")} can refit to it"; }
            if (id == def) note += " · yards build it";
            rows.Add(new ModelRow(CodeFor(w, side, cls, fit), fit, status, d?.Name ?? "Standard", id, flying, building, note));
        }
        foreach (var g in ships.Where(s => !current.Contains(string.Join(",", Fitting.FitOf(s)))).GroupBy(s => string.Join(",", Fitting.FitOf(s))).OrderBy(g => g.Min(s => s.Id)))
        {
            var first = g.OrderBy(s => s.Id).First();
            var code = first.Code.Contains('-') ? first.Code[..first.Code.IndexOf('-')] : first.Code;
            var outdated = g.Count(s => Refits.Outdated(w, s));
            rows.Add(new ModelRow(code, Fitting.FitOf(first).ToList(), "Old", NameOf(w, side, first.DesignId), first.DesignId, g.Count(), 0,
                outdated > 0 ? $"Refit offered for {outdated}" : "Still flying"));
        }
        return rows;
    }

    /// <summary>The variant a queued order will be built to.</summary>
    public static int ForOrder(World w, WorkOrder o) => o.DesignId is int id && (id == StandardId || Find(w, o.Side, id)?.ClassId == o.Key) ? id : DefaultFor(w, o.Side, o.Key);

    public static string RoleName(string role) => role switch
    {
        QShip => "Q-ship",
        LongRange => "long range",
        Lancer => "lancer",
        GunFort => "gun fort",
        MissileFort => "missile fort",
        FleetBase => "fleet base",
        Watchtower => "watchtower",
        YardGuard => "yard guard",
        "" => "own fit",
        _ => role,
    };

    /// <summary>What an Auto role variant of this class would be, if the role makes sense for it now (null: it doesn't).</summary>
    public static (string Name, string Role, List<string> Fit)? RoleVariant(World w, Side side, ShipClass cls, string role)
    {
        var fit = Fitting.StandardFit(cls);
        var utility = Enumerable.Range(0, fit.Count).Where(i => cls.SlotList[i].Type == "U").ToList();
        switch (role)
        {
            case QShip:
                if (cls.CargoKind != "food" || utility.Count == 0 || !Tech.Has(w, side, ConcealedMounts)) return null;
                fit[utility[0]] = "hidden-gun";
                break;
            case LongRange:
                if (!Planner.WarshipClass(cls.Id) || utility.Count == 0 || utility.Any(i => fit[i].Length > 0)) return null;
                fit[utility[0]] = "fuel-tank";
                break;
            case Lancer:
                // Pulse lances in place of the railgun pair: no dodging them, but they run hot.
                var guns = Enumerable.Range(0, fit.Count).Where(i => cls.SlotList[i].Type == "W" && fit[i] == "railgun").ToList();
                if (!Planner.WarshipClass(cls.Id) || guns.Count == 0 || !Tech.Has(w, side, Beams.LanceIdea)) return null;
                foreach (var i in guns) fit[i] = Beams.LanceModule;
                break;
            default:
                return null;
        }
        fit = Best(w, side, cls, fit);
        return Fitting.CanFit(w, side, cls, fit) == null ? (role switch { QShip => "Q-ship", Lancer => "Lancer", _ => "Long range" }, role, fit) : null;
    }

    public static readonly string[] Roles = { QShip, LongRange, Lancer };

    // ---- Station loadouts ----------------------------------------------------------------------

    public const string GunFort = "gun-fort";
    public const string MissileFort = "missile-fort";
    public const string FleetBase = "fleet-base";
    public const string Watchtower = "watchtower";
    public const string YardGuard = "yard-guard";

    /// <summary>The loadouts every station tier comes with (variants of the tier, kept current like Auto variants).</summary>
    public static readonly string[] StationRoles = { GunFort, MissileFort, FleetBase, Watchtower, YardGuard };

    /// <summary>
    /// A station loadout for this tier: Gun fort (railguns, a magazine), Missile fort (missile racks, magazine,
    /// depot, watch), Fleet base (repairs, magazine, slipway, depot; few guns), Watchtower (watch and relay bays,
    /// point defence) or Yard guard (slipway or dock, repairs, railguns). Null when the tier can't carry it or it
    /// would be the Standard.
    /// </summary>
    public static (string Name, string Role, List<string> Fit)? StationVariant(World w, Side side, ShipClass cls, string role)
    {
        if (!cls.Station) return null;
        var std = Fitting.StandardFit(cls);
        var fit = std.ToList();
        var slots = cls.SlotList;
        string BayOf(int i) => GameData.Module(fit[i])?.Bay ?? "";
        void KeepBays(params string[] keep)
        {
            for (var i = 0; i < fit.Count; i++)
                if (slots[i].Type == "B" && !keep.Contains(BayOf(i))) fit[i] = "";
        }
        void PutBay(string module)
        {
            var bay = GameData.Module(module)!.Bay;
            if (Enumerable.Range(0, fit.Count).Any(i => slots[i].Type == "B" && BayOf(i) == bay)) return;
            var free = Enumerable.Range(0, fit.Count).FirstOrDefault(i => slots[i].Type == "B" && fit[i].Length == 0, -1);
            if (free >= 0 && Fitting.CanUse(w, side, cls, free, module) == null) fit[free] = module;
        }
        var weapons = Enumerable.Range(0, fit.Count).Where(i => slots[i].Type == "W").ToList();
        switch (role)
        {
            case GunFort:
                if (weapons.Count == 0) return null;
                foreach (var i in weapons) fit[i] = "railgun";
                KeepBays("magazine");
                break;
            case MissileFort:
                if (weapons.Count == 0) return null;
                foreach (var i in weapons) fit[i] = "missile-rack";
                KeepBays("magazine", "fuel-depot", "watch");
                break;
            case FleetBase:
                foreach (var i in weapons.Skip(1)) fit[i] = "";
                KeepBays("hangar", "dock", "magazine", "slipway", "fuel-depot");
                if (!fit.Any(m => GameData.Module(m)?.Bay is "hangar" or "dock")) PutBay("dock-bay");
                break;
            case Watchtower:
                foreach (var i in weapons) fit[i] = "";
                KeepBays();
                PutBay("watch-bay");
                PutBay("relay-bay");
                for (var i = 0; i < fit.Count; i++)
                    if (slots[i].Type == "D" && GameData.Module(fit[i])?.PointDefence is not > 0) fit[i] = "";
                break;
            case YardGuard:
                foreach (var i in weapons) fit[i] = "railgun";
                KeepBays("hangar", "slipway", "dock");
                if (!fit.Any(m => GameData.Module(m)?.Bay is "slipway" or "dock")) PutBay("dock-bay");
                break;
            default:
                return null;
        }
        fit = Best(w, side, cls, fit);
        if (fit.SequenceEqual(Best(w, side, cls, std)) || Fitting.CanFit(w, side, cls, fit) != null) return null;
        return (RoleName(role) is var n ? char.ToUpperInvariant(n[0]) + n[1..] : role, role, fit);
    }

    // ---- Each day ----------------------------------------------------------------------------

    /// <summary>
    /// Hourly in full games: number old saves' designs, keep the frozen Standards for Advise and Manual,
    /// and (on Auto) add role variants and make the rival's choices. Q-ship warnings run out.
    /// </summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop) return;
        foreach (var side in Sides.InPlay(w))
        {
            var state = S(w, side);
            foreach (var d in state.Designs.Where(d => d.Id <= 0).ToList())
            {
                // A design from an older save: the fit new ships of the class got. It becomes a pinned variant and the default.
                d.Id = state.NextDesignId++;
                d.Name = "Saved fit";
                state.DefaultDesign[d.ClassId] = d.Id;
            }
            state.QShipWarnings.RemoveAll(q => w.Hour >= q.UntilHour);
            if (w.Tick % GameTime.TicksPerDay != 0) continue;
            var auto = OnAuto(w, side);
            foreach (var cls in GameData.ShipClasses.Values.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                // On Auto the Standard is live; keep a copy so switching to Manual freezes it where it was.
                if (auto || !state.StandardFits.ContainsKey(cls.Id)) state.StandardFits[cls.Id] = Best(w, side, cls, Fitting.StandardFit(cls));
                if (Mind.Of(w, side) == null || (cls.Needs.Length > 0 && !Tech.Has(w, side, cls.Needs))) continue;
                foreach (var role in Roles)
                    if (!Of(w, side, cls.Id).Any(d => d.Role == role) && RoleVariant(w, side, cls, role) is { } v)
                        Add(sim, side, cls.Id, v.Name, v.Role, v.Fit, auto: true, "auto");
            }
            // Stations: each tier's Standard, and its loadouts once the tier is researched (for every side).
            foreach (var cls in GameData.StationClasses.Values.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                if (auto || !state.StandardFits.ContainsKey(cls.Id)) state.StandardFits[cls.Id] = Best(w, side, cls, Fitting.StandardFit(cls));
                if (Starbases.Researched(w, side, cls.Id) != null || !w.Starbases.Any(st => st.Side == side) && Starbases.TopTier(w, side) < 2) continue;
                foreach (var role in StationRoles)
                    if (!Of(w, side, cls.Id).Any(d => d.Role == role) && StationVariant(w, side, cls, role) is { } v)
                        Add(sim, side, cls.Id, v.Name, v.Role, v.Fit, auto: true, "stations");
            }
            if (Mind.Of(w, side) != null) RivalCrewing(sim, side);
        }
    }

    /// <summary>The rival runs its small support hulls on AI once it has AI cores (crews are precious), warships crewed.</summary>
    private static void RivalCrewing(Simulation sim, Side side)
    {
        var w = sim.World;
        if (!Tech.Has(w, side, "ai-cores")) return;
        foreach (var id in new[] { "escort", "picket", "tanker", "freighter", "bulk-freighter", "supply-tender" })
            if (!S(w, side).AiRun.Contains(id) && CanRunOnAi(w, side, id) == null)
            {
                S(w, side).AiRun.Add(id);
                sim.Log.Info("designs", $"{side} runs new ships of a class on AI", new { side = side.ToString(), classId = id });
            }
    }

    internal static Design Add(Simulation sim, Side side, string classId, string name, string role, List<string> fit, bool auto, string source)
    {
        var state = S(sim.World, side);
        var d = new Design { Id = state.NextDesignId++, ClassId = classId, Name = name, Role = role, Fit = fit.ToList(), Auto = auto, SavedHour = sim.World.Hour };
        state.Designs.Add(d);
        sim.Log.Info("designs", $"{side} adds a variant of the {GameData.ShipClass(classId).Name.ToLowerInvariant()}", new { side = side.ToString(), classId, d.Id, name, role, auto, fit = string.Join(",", fit), source });
        return d;
    }

    /// <summary>
    /// The player's design manager (from Stewards): Advise offers each Standard update and each new role
    /// variant; Auto adds role variants by itself. Your own variants with parts behind the best are always
    /// only flagged.
    /// </summary>
    public static void Steward(Simulation sim, Side side, OfferFn offer, NeedFn need)
    {
        var w = sim.World;
        var level = Stewards.LevelFor(w, side, AutoArea.Designs);
        foreach (var cls in GameData.ShipClasses.Values.Where(c => c.Needs.Length == 0 || Tech.Has(w, side, c.Needs)).OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            var flying = w.Ships.Any(s => s.Side == side && s.ClassId == cls.Id);
            if (level == AutoLevel.Advise && S(w, side).StandardFits.TryGetValue(cls.Id, out var kept))
            {
                var best = Best(w, side, cls, Fitting.StandardFit(cls));
                if (!best.SequenceEqual(kept) && flying)
                    offer(AutoArea.Designs, $"design-std:{cls.Id}:{string.Join(",", best)}", $"Update the {cls.Name} Standard: {Changes(kept, best)}",
                        new UpdateStandardCommand(side, cls.Id), "Update it");
            }
            if (level != AutoLevel.Manual && flying)
                foreach (var role in Roles)
                    if (!Of(w, side, cls.Id).Any(d => d.Role == role) && RoleVariant(w, side, cls, role) is { } v)
                        offer(AutoArea.Designs, $"design-role:{cls.Id}:{role}", $"Add a {v.Name} variant of the {cls.Name}: {Changes(Fitting.StandardFit(cls), v.Fit)}",
                            new SaveVariantCommand(side, cls.Id, null, v.Name, v.Fit, v.Role, Auto: true), "Add it");
            foreach (var d in Of(w, side, cls.Id).Where(d => !d.Auto))
                if (Behind(w, side, cls, d.Fit) is var n and > 0 && w.Ships.Any(s => s.Side == side && s.DesignId == d.Id))
                    need(NeedLevel.Info, $"design-old:{d.Id}:{string.Join(",", Best(w, side, cls, d.Fit))}",
                        $"Your {cls.Name} {d.Name} variant has {n} part{(n == 1 ? "" : "s")} behind the best you have: {Changes(d.Fit, Best(w, side, cls, d.Fit))}",
                        null, null, new SaveVariantCommand(side, cls.Id, d.Id, d.Name, Best(w, side, cls, d.Fit), d.Role), "Update its parts");
        }
    }

    /// <summary>"Engine Mk I → Engine Mk II, + Hidden gun pair": what differs between two fits.</summary>
    public static string Changes(IReadOnlyList<string> from, IReadOnlyList<string> to)
    {
        var bits = new List<string>();
        for (var i = 0; i < Math.Min(from.Count, to.Count); i++)
        {
            if (from[i] == to[i]) continue;
            var a = GameData.Module(from[i])?.Name;
            var b = GameData.Module(to[i])?.Name;
            bits.Add(a == null ? $"+ {b}" : b == null ? $"- {a}" : $"{a} → {b}");
        }
        return bits.Count == 0 ? "no change" : string.Join(", ", bits);
    }

    // ---- Q-ships -----------------------------------------------------------------------------

    /// <summary>Does this ship carry a concealed mount?</summary>
    public static bool Hidden(Ship s)
    {
        var fit = Fitting.FitOf(s);
        return fit.Any(m => GameData.Module(m) is { Hidden: true });
    }

    /// <summary>A Q-ship opens fire: the other side's raiders learn there is one about and stay wary near here.</summary>
    public static void Revealed(Simulation sim, Ship s, Side seenBy)
    {
        var w = sim.World;
        if (!w.Loop || w.Hour - s.RevealedHour < 24) return;
        s.RevealedHour = w.Hour;
        S(w, seenBy).QShipWarnings.Add(new QShipWarning(s.Position, w.Hour + WaryDays * 24, s.Name));
        sim.Log.Info("designs", $"{s.Name} opens its hidden mounts", new { shipId = s.Id, side = s.Side.ToString(), seenBy = seenBy.ToString() });
    }

    /// <summary>Is this side wary of convoys near this point (a Q-ship fired near here lately)?</summary>
    public static bool Wary(World w, Side side, Vec2 at) => S(w, side).QShipWarnings.Any(q => Vec2.Distance(q.At, at) <= WaryRadiusMm);
}

// ---- Commands -----------------------------------------------------------------------------------

/// <summary>Save a variant of a class: a new one (DesignId null), or changes to one of yours. Your own variants are pinned.</summary>
public sealed record SaveVariantCommand(Side Side, string ClassId, int? DesignId, string Name, List<string> Fit, string Role = "", bool Auto = false) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        var name = (Name ?? "").Trim();
        string? problem = !GameData.TryClass(ClassId, out var cls) ? $"Unknown class '{ClassId}'"
            : Fitting.CanFit(world, Side, cls, Fit) is string p ? p
            : name.Length == 0 ? "A variant needs a name" : name.Length > 24 ? "At most 24 letters"
            : DesignId is int id && Designer.Find(world, Side, id)?.ClassId != ClassId ? "No such variant"
            : Designer.Of(world, Side, ClassId).Any(d => d.Id != DesignId && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) ? "Another variant has that name"
            : null;
        if (problem != null)
        {
            sim.Log.Warn("designs", $"Can't save that variant: {problem}", new { side = Side.ToString(), ClassId, DesignId, Name, fit = string.Join(",", Fit ?? new()), Source });
            return;
        }
        if (DesignId is int existing && Designer.Find(world, Side, existing) is Design d)
        {
            d.Fit = Fit.ToList();
            d.Name = name;
            d.Role = Role ?? "";
            d.Auto = Auto && d.Auto;
            d.SavedHour = world.Hour;
            sim.Log.Info("designs", $"{Side} updates a variant", new { side = Side.ToString(), ClassId, d.Id, name, fit = string.Join(",", Fit), Source });
            return;
        }
        Designer.Add(sim, Side, ClassId, name, Role ?? "", Fit, Auto, Source);
    }
}

/// <summary>Delete a variant ("Reset to Auto"): its ships follow the Standard from now on.</summary>
public sealed record DeleteVariantCommand(Side Side, int DesignId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (Designer.Find(world, Side, DesignId) is not Design d)
        {
            sim.Log.Warn("designs", "No such variant to delete", new { side = Side.ToString(), DesignId, Source });
            return;
        }
        var state = Economy.Of(world, Side);
        state.Designs.Remove(d);
        if (state.DefaultDesign.TryGetValue(d.ClassId, out var def) && def == d.Id) state.DefaultDesign.Remove(d.ClassId);
        foreach (var s in world.Ships.Where(s => s.Side == Side && s.DesignId == d.Id)) s.DesignId = Designer.StandardId;
        foreach (var o in world.Orders.Where(o => o.Side == Side && o.DesignId == d.Id)) o.DesignId = null;
        sim.Log.Info("designs", $"{Side} deletes a variant", new { side = Side.ToString(), d.ClassId, DesignId, d.Name, Source });
    }
}

/// <summary>Hand a class back to Auto: your own variants of it are deleted and the Standard is built again.</summary>
public sealed record HandToAutoCommand(Side Side, string ClassId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        foreach (var d in Designer.Of(world, Side, ClassId).Where(d => !d.Auto).ToList())
            new DeleteVariantCommand(Side, d.Id) { Source = Source }.Apply(world, sim);
        Economy.Of(world, Side).DefaultDesign.Remove(ClassId);
        sim.Log.Info("designs", $"{Side} hands a class to Auto", new { side = Side.ToString(), ClassId, Source });
    }
}

/// <summary>Which variant yards build for a class when no other is asked for (0: the Standard).</summary>
public sealed record SetDefaultVariantCommand(Side Side, string ClassId, int DesignId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (DesignId != Designer.StandardId && Designer.Find(world, Side, DesignId)?.ClassId != ClassId)
        {
            sim.Log.Warn("designs", "No such variant of that class", new { side = Side.ToString(), ClassId, DesignId, Source });
            return;
        }
        if (DesignId == Designer.StandardId) Economy.Of(world, Side).DefaultDesign.Remove(ClassId);
        else Economy.Of(world, Side).DefaultDesign[ClassId] = DesignId;
        sim.Log.Info("designs", $"{Side} builds a variant by default", new { side = Side.ToString(), ClassId, DesignId, Source });
    }
}

/// <summary>Keep the class's Standard at the best fit you have now (with Advise or Manual it stays there until the next update).</summary>
public sealed record UpdateStandardCommand(Side Side, string ClassId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (!GameData.TryClass(ClassId, out var cls))
        {
            sim.Log.Warn("designs", "No such class", new { side = Side.ToString(), ClassId, Source });
            return;
        }
        var best = Designer.Best(world, Side, cls, Fitting.StandardFit(cls));
        Economy.Of(world, Side).StandardFits[ClassId] = best;
        foreach (var d in Designer.Of(world, Side, ClassId).Where(d => d.Auto)) d.Fit = Designer.Best(world, Side, cls, d.Fit);
        sim.Log.Info("designs", $"{Side} updates a Standard", new { side = Side.ToString(), ClassId, fit = string.Join(",", best), Source });
    }
}

/// <summary>Crewed or AI-run for new ships of a class (AI cores). Ships flying change over at their next refit.</summary>
public sealed record SetCrewingCommand(Side Side, string ClassId, bool AiRun) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (AiRun && Designer.CanRunOnAi(world, Side, ClassId) is string problem)
        {
            sim.Log.Warn("designs", $"Can't run that class on AI: {problem}", new { side = Side.ToString(), ClassId, Source });
            return;
        }
        var list = Economy.Of(world, Side).AiRun;
        list.Remove(ClassId);
        if (AiRun) list.Add(ClassId);
        sim.Log.Info("designs", $"{Side} sets a class {(AiRun ? "AI-run" : "crewed")}", new { side = Side.ToString(), ClassId, AiRun, Source });
    }
}

/// <summary>Move a ship to another variant of its class: it refits to that variant at its next yard stop.</summary>
public sealed record SetShipVariantCommand(Side Side, int ShipId, int DesignId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.FindShip(ShipId) is not Ship s || s.Side != Side
            || DesignId != Designer.StandardId && Designer.Find(world, Side, DesignId)?.ClassId != s.ClassId)
        {
            sim.Log.Warn("designs", "Bad ship variant order", new { side = Side.ToString(), ShipId, DesignId, Source });
            return;
        }
        s.DesignId = DesignId;
        sim.Log.Info("designs", $"{s.Name} will refit to another variant", new { ShipId, DesignId, Source });
    }
}
