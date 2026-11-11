using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>The areas of running an empire that can be handed to automation (see Stewards).</summary>
public enum AutoArea { Building, Workers, Transport, Settling, Research, Shipyard, Supply, Refits, Battle, Crews, Boarding, Ground, AfterBattle }

/// <summary>Auto: does it. Advise: suggests it in Needs you. Manual: leaves it alone.</summary>
public enum AutoLevel { Auto, Advise, Manual }

/// <summary>How soon a Needs you item wants a look.</summary>
public enum NeedLevel { Urgent, Soon, Advice, Info }

/// <summary>One line in the Empire screen's Needs you list: what, why, where, and the order that answers it (if any).</summary>
public sealed class NeedItem
{
    /// <summary>Stable key, so a dismissed item stays dismissed when the list is worked out again.</summary>
    public string Key { get; set; } = "";
    public NeedLevel Level { get; set; }
    public AutoArea? Area { get; set; }
    public string Text { get; set; } = "";
    public int? BodyId { get; set; }
    public int? FleetId { get; set; }

    /// <summary>The order the act button gives (null: only a jump).</summary>
    public GameCommand? Act { get; set; }
    public string? ActLabel { get; set; }
    public double Hour { get; set; }
}

/// <summary>A different automation level for one planet or fleet in one area.</summary>
public sealed record AutoOverride(AutoArea Area, int? BodyId, int? FleetId, AutoLevel Level);

/// <summary>Something automation did (shown under "Auto handled").</summary>
public sealed record AutoNote(double Hour, AutoArea Area, string Text);

/// <summary>How an area outside Stewards offers what it would do (see Stewards: Auto does it, Advise lists it).</summary>
public delegate bool OfferFn(AutoArea area, string key, string text, GameCommand act, string label, int? bodyId = null, int? fleetId = null, NeedLevel advice = NeedLevel.Advice);

/// <summary>An auto action to be judged later: was it a good one?</summary>
public sealed record AutoCheck(AutoArea Area, string Key, double JudgeHour, int? BodyId = null, int? PieceId = null, int? FleetId = null, int? TargetFleetId = null);

/// <summary>How an area's judged auto actions turned out.</summary>
public sealed class AutoScore
{
    public int Good { get; set; }
    public int Bad { get; set; }
}

/// <summary>A warship lost: never rebuilt by itself, but offered for rebuilding in Needs you.</summary>
public sealed record LostShip(string ClassId, string Name, double Hour, int? FleetId = null);

/// <summary>
/// Shared planning: what a planet should build next, which material is short. The rival's mind and the
/// player's automation both use these, so automation plays by the same judgement as the rival.
/// </summary>
public static class Planner
{
    /// <summary>The plant that makes each material good.</summary>
    private static readonly (string Good, string Plant)[] MaterialPlants =
        { ("alloys", "smelter"), ("electronics", "electronics-plant"), ("ammunition", "munitions-plant") };

    /// <summary>Reserve of each material the rival likes to keep across its linked planets.</summary>
    public static double MaterialReserve(string good) => good switch { "alloys" => 60, "electronics" => 20, _ => 40 };

    /// <summary>A material is short: something waits for it, or the side makes less than it uses and holds under its reserve.</summary>
    public static bool MaterialShort(World w, Side side, string good)
    {
        var linked = w.Bodies.Where(b => b.Owner == side && !Economy.Blockaded(w, b)).ToList();
        var held = linked.Sum(b => Colonies.Held(b, good));
        var wanted = linked.Sum(b => Colonies.Want(b, good));
        if (wanted > held) return true;
        if (w.Orders.Any(o => o.Side == side && o.Waiting != null && o.Waiting.Contains(Colonies.GoodName(good).ToLowerInvariant()))) return true;
        return held < MaterialReserve(good) && linked.Sum(b => Colonies.MadePerDay(b, good)) <= linked.Sum(b => Colonies.UsePerDay(b, good));
    }

    /// <summary>A free deposit of this mineral a mine fits on, richest first.</summary>
    public static (int X, int Y)? DepositFor(Body b, string mineral)
    {
        var data = Planets.BuildingKind(Planets.Mine)!;
        return b.Board?.Deposits.Where(d => d.Mineral == mineral && Colonies.FitProblem(b, data, d.X, d.Y, false) == null)
            .OrderByDescending(d => d.Rich).ThenBy(d => d.Y).ThenBy(d => d.X).Select(d => ((int, int)?)(d.X, d.Y)).FirstOrDefault();
    }

    /// <summary>What this planet most needs next (and where, for a mine on a given mineral), if anything fits.</summary>
    public static (string Kind, int? X, int? Y, bool Urgent)? PlanetWant(World w, Side side, Body b, bool hungry, bool sparesShort, bool easy = false)
    {
        var fits = new Dictionary<string, bool>();
        bool Fits(string kind)
        {
            if (fits.TryGetValue(kind, out var ok)) return ok;
            return fits[kind] = Planets.BuildingKind(kind) is BuildingData d && (d.Needs == null || Tech.Has(w, side, d.Needs))
                                && Colonies.BestSpot(b, d) != null;
        }
        bool Staffed(string kind) => b.PopK >= Planets.StaffNeeded(b) + (Planets.BuildingKind(kind)?.StaffK ?? 0);
        bool Powered(string kind) => b.PowerMade - b.PowerWanted >= (Planets.BuildingKind(kind)?.Power ?? 0);
        string? Pick(params string[] kinds) => kinds.FirstOrDefault(k => Fits(k) && Powered(k));
        bool Short(string good) => Colonies.UsePerDay(b, good) > 0 && Colonies.MadePerDay(b, good) < Colonies.UsePerDay(b, good)
                                   && Colonies.Held(b, good) < 30 + 30 * Colonies.UsePerDay(b, good);
        if (hungry && Pick("fish-farm", Planets.TypeOf(b).Farm > 0 || b.Home ? Planets.Farm : "dome-farm") is string food) return (food, null, null, true);
        if (b.PowerWanted > b.PowerMade - 3)
        {
            var fuelled = Colonies.Held(b, "uranium") > 20 || Colonies.MadePerDay(b, "uranium") > 0.2 || DepositFor(b, "uranium") != null;
            return Fits("power-plant") && fuelled ? ("power-plant", null, null, true) : Fits("solar-field") ? ("solar-field", null, null, true) : null;
        }
        // The minerals that keep the lights on and the spare parts coming.
        foreach (var good in new[] { "uranium", "iron" })
            if (Short(good) && Powered(Planets.Mine) && DepositFor(b, good) is var (mx, my))
                return (Planets.Mine, mx, my, true);
        if (sparesShort && Staffed("workshop") && Pick("workshop") is string shop) return (shop, null, null, true);
        // Materials: ships, big buildings and later research wait for alloys, electronics and ammunition.
        // The plant for a short one goes up (at home first), then mines for the minerals it eats.
        foreach (var (good, plant) in MaterialPlants)
            if (MaterialShort(w, side, good) && Staffed(plant) && Pick(plant) is string make
                && !w.Bodies.Any(o => o.Owner == side && o.Pieces.Any(p => p.Kind == plant && !p.Built))) // one new plant at a time
                return (make, null, null, true);
        foreach (var good in new[] { "copper", "rare-earths", "uranium", "water" })
            if ((Short(good) || Colonies.Want(b, good) > Colonies.Held(b, good)) && Powered(Planets.Mine) && DepositFor(b, good) is var (gx, gy))
                return (Planets.Mine, gx, gy, true);
        if (b.Home && Colonies.Built(b).Count(p => p.Kind == "dry-dock") == 0 && Planets.Count(b, Planets.Mine) >= 3 && Staffed("dry-dock") && Pick("dry-dock") is string dock)
            return (dock, null, null, false);
        if (w.Rules.Happiness && b.Happiness < 45 && Planets.Count(b, "clinic") == 0 && Pick("clinic") is string clinic) return (clinic, null, null, false);
        if (Staffed(Planets.Mine) && Pick(Planets.Mine) is string dig) return (dig, null, null, false);
        if (b.PopK > Planets.Cap(b) * 0.85 && Pick("habitat") is string home) return (home, null, null, false);
        if (!Colonies.Built(b).Any(p => p.Kind == "storehouse") && b.Pieces.Count >= 3 && Pick("storehouse") is string store) return (store, null, null, false);
        if (b.Home && Planets.Count(b, Planets.Mine) >= 2)
        {
            if (Planets.Count(b, Planets.Guns) == 0 && Pick(Planets.Guns) is string guns) return (guns, null, null, false);
            if (Planets.Count(b, "pd-grid") == 0 && Pick("pd-grid") is string pd) return (pd, null, null, false);
            if (Planets.Count(b, "research-lab") < (easy ? 1 : 2) && Staffed("research-lab") && Pick("research-lab") is string lab) return (lab, null, null, false);
        }
        if (Colonies.Held(b, Colonies.Fuel) < 40 && b.Home && Pick("fuel-plant", "ice-mine") is string fuel) return (fuel, null, null, false);
        return null;
    }

    /// <summary>The side is short of food across its linked planets.</summary>
    public static bool Hungry(World w, Side side)
    {
        var linked = w.Bodies.Where(b => b.Owner == side && !Economy.Blockaded(w, b)).ToList();
        return linked.Sum(Planets.FoodMadePerDay) < linked.Sum(Planets.FoodNeedPerDay) * 1.15 + 0.1;
    }

    /// <summary>The side makes fewer spare parts than it uses.</summary>
    public static bool SparesShort(World w, Side side)
    {
        var linked = w.Bodies.Where(b => b.Owner == side && !Economy.Blockaded(w, b)).ToList();
        return linked.Sum(b => Colonies.MadePerDay(b, Colonies.Spares)) < linked.Sum(b => Colonies.UsePerDay(b, Colonies.Spares)) * 1.1;
    }

    /// <summary>
    /// The next idea for a team: a sparked idea first, then one in a field the team keeps, then the lowest
    /// tier and cheapest. Only ideas that can be queued now (seen, needs done, not a later build).
    /// </summary>
    public static string? NextIdea(World w, Side side, ResearchTeam? team)
    {
        var state = Economy.Of(w, side);
        return GameData.Economy.Research
            .Where(r => Economy.CanQueue(w, side, OrderKind.Research, r.Id) == null && (w.Light.On || !Tech.LagOnly(r.Id)))
            .OrderByDescending(r => HelpsMaterials(w, side, r.Id))
            .ThenByDescending(r => state.Sparked.Contains(r.Id))
            .ThenByDescending(r => team != null && team.Kept.Contains(r.Field))
            .ThenBy(r => r.Tier).ThenBy(r => r.Cost).ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => r.Id).FirstOrDefault();
    }

    /// <summary>Research that opens the way to a plant for a material the side is short of (electronics: assays, then fabs).</summary>
    public static bool HelpsMaterials(World w, Side side, string id) =>
        id is "electronics-fabs" && MaterialShort(w, side, "electronics");

    /// <summary>Only supply ships (tankers), not freighter-only fleets.</summary>
    public static bool SupplyFleet(World w, Fleet f) =>
        f.ShipIds.Count > 0 && w.ShipsOf(f).All(SupplyLines.IsSupplyShip) && !w.ShipsOf(f).All(s => s.ClassId == "freighter" || s.ClassId == "bulk-freighter");

    /// <summary>Share of missiles left across the fleet's racks (1 with none fitted).</summary>
    public static double MissileShare(World w, Fleet f)
    {
        double have = 0, full = 0;
        foreach (var s in w.ShipsOf(f)) { have += s.Missiles; full += Fitting.Spec(s).Missiles; }
        return full > 0 ? have / full : 1;
    }

    /// <summary>A fleet that fights (has an armed ship that is not a supply or cargo ship).</summary>
    public static bool Warfleet(World w, Fleet f) => f.ShipIds.Count > 0 && Combat.Armed(w, f) && !w.ShipsOf(f).All(SupplyLines.IsSupplyShip);

    /// <summary>A warship class: armed and carries no cargo (settler, troop and supply ships are not warships).</summary>
    public static bool WarshipClass(string classId) =>
        GameData.ShipClasses.TryGetValue(classId, out var c) && (c.Railguns > 0 || c.Missiles > 0) && c.Cargo <= 0;

    /// <summary>A warship (for the lost-ship list): armed and not a cargo ship.</summary>
    public static bool Warship(Ship s) => !SupplyLines.IsSupplyShip(s) && Fitting.Spec(s) is { } spec && (spec.Railguns > 0 || spec.Missiles > 0) && spec.Cargo <= 0;
}

/// <summary>
/// Automation for a side without an AI mind (the player): nine areas, each Auto (does it), Advise
/// (suggests it in Needs you) or Manual (leaves it). A planet or fleet can have its own level. Automation
/// only fills gaps: it never cancels or changes an order given by hand (a hand order pins its item).
/// Also works out the Needs you list: urgent troubles, things due soon, advice and news.
/// Runs every few hours, inside the step (so replays run it again and need no record).
/// </summary>
public static class Stewards
{
    public const int EveryTicks = 3;
    public const double DismissDays = 3;
    public const double LostShipDays = 20;
    public const int KeepNotes = 40;

    public static readonly AutoArea[] Areas = Enum.GetValues<AutoArea>();

    /// <summary>An area's starting level: from the data file, else the built-in one.</summary>
    public static AutoLevel Default(AutoArea area) =>
        GameData.Ai.Automation?.Defaults is { } d && d.TryGetValue(area.ToString().ToLowerInvariant(), out var text)
        && Enum.TryParse<AutoLevel>(text, true, out var level) ? level : BuiltInDefault(area);

    private static AutomationData L => GameData.Ai.Automation ?? new AutomationData(new());

    public static AutoLevel BuiltInDefault(AutoArea area) => area switch
    {
        AutoArea.Building or AutoArea.Settling or AutoArea.Research => AutoLevel.Advise,
        AutoArea.Shipyard => AutoLevel.Manual,
        _ => AutoLevel.Auto,
    };

    public static string AreaName(AutoArea area) => area switch
    {
        AutoArea.Building => "Planet building",
        AutoArea.Workers => "Workers and spares",
        AutoArea.Transport => "Transport",
        AutoArea.Settling => "Settling",
        AutoArea.Research => "Research queues",
        AutoArea.Shipyard => "Shipyard queue",
        AutoArea.Supply => "Fleet supply",
        AutoArea.Refits => "Refits",
        AutoArea.Battle => "Battle rules",
        AutoArea.Crews => "Crews",
        AutoArea.Boarding => "Boarding calls",
        AutoArea.Ground => "Ground war calls",
        AutoArea.AfterBattle => "After battle",
        _ => area.ToString(),
    };

    public static string AreaWhat(AutoArea area) => area switch
    {
        AutoArea.Building => "What each planet builds next: food, power, mines, material plants",
        AutoArea.Workers => "Topping up planets short of workers; spare parts where they run low",
        AutoArea.Transport => "Free freighters carry goods to planets that ask for them",
        AutoArea.Settling => "Sending settlers to empty worlds worth having",
        AutoArea.Research => "The next idea for each team when its queue runs dry",
        AutoArea.Shipyard => "Support ships only (tankers, freighters): warships are always your call",
        AutoArea.Supply => "Tankers sent to fleets low on fuel, food or ammunition",
        AutoArea.Refits => "Idle fleets sent to refit to the current designs when a bay is free",
        AutoArea.Battle => "Pull-back rules chosen by each fleet's job, place and odds",
        AutoArea.Crews => "Veteran crews spread over new ships in port; the best crew on the flagship",
        AutoArea.Boarding => "The calls during a boarding (Advise or Manual: ask me, Auto decides if I don't answer in time)",
        AutoArea.Ground => "The plan and the calls during an invasion (Advise or Manual: ask me)",
        AutoArea.AfterBattle => "Rescue, capture, salvage, then mend or go home (Advise or Manual: I pick how to get home)",
        _ => "",
    };

    /// <summary>The level for an area, for one planet or fleet if it has its own.</summary>
    public static AutoLevel LevelFor(World w, Side side, AutoArea area, int? bodyId = null, int? fleetId = null)
    {
        var state = Economy.Of(w, side);
        if (fleetId != null && state.AutoOverrides.Find(o => o.Area == area && o.FleetId == fleetId) is { } fo) return fo.Level;
        if (bodyId != null && state.AutoOverrides.Find(o => o.Area == area && o.BodyId == bodyId) is { } bo) return bo.Level;
        return state.Auto.TryGetValue(area, out var level) ? level : Default(area);
    }

    private static bool Minded(World w, Side side) => Mind.Of(w, side) != null;

    public static void Update(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop || w.Winner != null || w.Tick % EveryTicks != 0) return;
        // The player's side (the rival's mind does its own planning with the same Planner).
        if (!Minded(w, Side.Player) && Economy.ShipyardOf(w, Side.Player) != null) new Steward(sim, Side.Player).Run();
    }

    /// <summary>A warship was lost: remember it so Needs you can offer to rebuild it.</summary>
    public static void OnLost(World w, Ship s)
    {
        if (!w.Loop || !Planner.Warship(s)) return;
        var lost = Economy.Of(w, s.Side).Lost;
        lost.Add(new LostShip(s.ClassId, s.Name, w.Hour, s.FleetId));
        if (lost.Count > 20) lost.RemoveAt(0);
    }

    private sealed class Steward
    {
        private readonly Simulation _sim;
        private readonly World _w;
        private readonly Side _side;
        private readonly SideState _state;
        private readonly Body _home;
        private readonly List<NeedItem> _needs = new();

        public Steward(Simulation sim, Side side)
        {
            _sim = sim;
            _w = sim.World;
            _side = side;
            _state = Economy.Of(_w, side);
            _home = Economy.ShipyardOf(_w, side)!;
        }

        public void Run()
        {
            if (_w.Day != _state.AutoDay) { _state.AutoDay = _w.Day; _state.AutoHandledToday = 0; }
            foreach (var key in _state.Dismissed.Where(d => d.Value <= _w.Hour).Select(d => d.Key).ToList()) _state.Dismissed.Remove(key);
            _state.AutoTransport = LevelFor(_w, _side, AutoArea.Transport) == AutoLevel.Auto;
            Alerts();
            Learn();
            Building();
            Workers();
            Transport();
            Settling();
            ResearchQueues();
            Supply();
            Shipyard();
            Refits.Steward(_sim, _side, Offer);
            BattleRules.Steward(_sim, _side, Offer);
            Crews.Steward(_sim, _side, Offer);
            AfterBattle.Needs(_w, _side, (level, key, text, bodyId, fleetId, act, label) => Need(level, key, text, bodyId, fleetId, act, label, AutoArea.AfterBattle));
            _state.Needs = _needs.Where(n => !_state.Dismissed.ContainsKey(n.Key))
                .OrderBy(n => n.Level).ThenBy(n => n.Hour).ThenBy(n => n.Key, StringComparer.Ordinal).ToList();
        }

        private void Need(NeedLevel level, string key, string text, int? bodyId = null, int? fleetId = null, GameCommand? act = null, string? label = null, AutoArea? area = null)
        {
            if (_needs.Any(n => n.Key == key)) return;
            var old = _state.Needs.Find(n => n.Key == key);
            _needs.Add(new NeedItem
            {
                Key = key, Level = level, Area = area, Text = text, BodyId = bodyId, FleetId = fleetId,
                Act = act, ActLabel = act == null ? null : label ?? "Do it", Hour = old?.Hour ?? _w.Hour,
            });
        }

        /// <summary>
        /// One thing an area would do. Auto: done now (counted under Auto handled). Advise: offered in
        /// Needs you. Manual: nothing. Returns true when it was done.
        /// </summary>
        private bool Offer(AutoArea area, string key, string text, GameCommand act, string label, int? bodyId = null, int? fleetId = null, NeedLevel advice = NeedLevel.Advice)
        {
            switch (LevelFor(_w, _side, area, bodyId, fleetId))
            {
                case AutoLevel.Auto:
                    (act with { Source = "auto" }).Apply(_w, _sim);
                    Track(area, key, act);
                    _state.AutoHandledToday++;
                    _state.AutoHandled++;
                    _state.AutoNotes.Add(new AutoNote(_w.Hour, area, text));
                    if (_state.AutoNotes.Count > KeepNotes) _state.AutoNotes.RemoveAt(0);
                    _sim.Log.Info("auto", $"Auto: {text}", new { side = _side.ToString(), area = area.ToString(), key });
                    return true;
                case AutoLevel.Advise:
                    Need(advice, key, text, bodyId, fleetId, act, label, area);
                    return false;
                default:
                    return false;
            }
        }

        // ---- Learning: judging auto actions, and noticing habits ----------------------------------

        /// <summary>Remember an auto action to judge it later (the kinds that can go well or badly).</summary>
        private void Track(AutoArea area, string key, GameCommand act)
        {
            var at = _w.Hour + L.JudgeDays * 24;
            AutoCheck? check = act switch
            {
                BuildOnPlanetCommand b when _w.FindBody(b.BodyId) is Body body && body.Pieces.Where(p => p.Kind == b.Kind).MaxBy(p => p.Id) is Piece p
                    => new AutoCheck(area, key, at + (Planets.BuildingKind(b.Kind)?.Days ?? 0) * 24, b.BodyId, PieceId: p.Id),
                SetSupplyRunCommand s => new AutoCheck(area, key, at, FleetId: s.FleetId, TargetFleetId: s.TargetFleetId),
                SettleCommand s => new AutoCheck(area, key, at + 30 * 24, s.BodyId),
                SetAutoTopUpCommand t => new AutoCheck(area, key, at + 20 * 24, t.BodyId),
                _ => null,
            };
            if (check != null) _state.AutoChecks.Add(check);
        }

        /// <summary>Was it good? A building that works, a fleet that got its supplies, a world settled, a planet staffed.</summary>
        private bool? Judge(AutoCheck c)
        {
            var body = c.BodyId is int id ? _w.FindBody(id) : null;
            switch (c.Area)
            {
                case AutoArea.Building:
                    if (body == null || body.Owner != _side) return null; // lost the planet: not the building's fault
                    if (body.Pieces.Find(p => p.Id == c.PieceId) is not Piece piece || !piece.Built) return false;
                    return Planets.BuildingKind(piece.Kind) is BuildingData d && Colonies.Output(_w, body, piece, d) >= 0.5;
                case AutoArea.Supply:
                    if (c.TargetFleetId is not int t || _w.FindFleet(t) is not Fleet target || target.ShipIds.Count == 0) return null;
                    return _w.FindFleet(c.FleetId ?? -1) is { ShipIds.Count: > 0 } && Combat.SupplyShare(_w, target) >= 0.5;
                case AutoArea.Settling:
                    return body?.Owner == _side && body.PopK > 0;
                case AutoArea.Workers:
                    if (body == null || body.Owner != _side) return null;
                    return body.PopK >= Planets.StaffNeeded(body) * 0.8 || body.PopK >= Planets.Cap(body) * 0.8;
                default:
                    return null;
            }
        }

        private void Learn()
        {
            foreach (var c in _state.AutoChecks.Where(c => _w.Hour >= c.JudgeHour).ToList())
            {
                _state.AutoChecks.Remove(c);
                if (Judge(c) is not bool good) continue;
                var score = Score(c.Area);
                if (good) score.Good++;
                else score.Bad++;
                _sim.Log.Info("auto", good ? "Auto action worked out" : "Auto action went badly", new { side = _side.ToString(), area = c.Area.ToString(), c.Key });
            }
            // Pull-back habits: the same hand-picked preset for the same situation, again and again.
            foreach (var (key, n) in _state.Habits.Where(h => h.Key.StartsWith("pull:", StringComparison.Ordinal)).OrderBy(h => h.Key, StringComparer.Ordinal))
            {
                var parts = key.Split(':');
                if (n < L.HabitChanges || parts.Length != 3 || !Enum.TryParse<PullPreset>(parts[2], out var preset) || preset == PullPreset.Custom
                    || BattleRules.AutoPick(_w, _side, parts[1]) == preset) continue;
                Need(NeedLevel.Advice, $"learn-pull:{parts[1]}:{preset}",
                    $"You often set {BattleRules.SituationName(parts[1])} to pull back {PullRule.Name(preset)}: make that Auto's choice?",
                    act: new SetPullDefaultCommand(_side, parts[1], preset), label: "Make it default", area: AutoArea.Battle);
            }
            foreach (var area in Areas)
            {
                var level = LevelFor(_w, _side, area);
                var name = AreaName(area);
                // Results: an area whose auto actions keep going badly.
                if (level == AutoLevel.Auto && _state.AutoScores.TryGetValue(area, out var sc) && sc.Good + sc.Bad >= L.MinJudged
                    && sc.Bad > (sc.Good + sc.Bad) * L.BadShare)
                    Need(NeedLevel.Advice, $"learn-bad:{area}", $"Auto {name.ToLowerInvariant()} has gone badly {sc.Bad} times of {sc.Good + sc.Bad}: switch it to Advise?",
                        act: new SetAutoLevelCommand(_side, area, AutoLevel.Advise), label: "Advise", area: area);
                // Habits: the same hand change again and again.
                foreach (var to in Enum.GetValues<AutoLevel>())
                    if (to != level && Habit($"set:{area}:{to}") >= L.HabitChanges)
                        Need(NeedLevel.Advice, $"learn-habit:{area}:{to}", $"You often set {name.ToLowerInvariant()} to {to} for planets or fleets: make {to} the default?",
                            act: new SetAutoLevelCommand(_side, area, to), label: $"Make {to} default", area: area);
                // Suggestions always taken, or always turned down.
                if (level == AutoLevel.Advise)
                {
                    var took = Habit($"accept:{area}");
                    var dropped = Habit($"dismiss:{area}");
                    if (took >= L.AcceptsToAuto && dropped == 0)
                        Need(NeedLevel.Advice, $"learn-auto:{area}", $"You've taken every {name.ToLowerInvariant()} suggestion ({took}): let Auto do it?",
                            act: new SetAutoLevelCommand(_side, area, AutoLevel.Auto), label: "Auto", area: area);
                    else if (dropped >= L.DismissesToManual && took == 0)
                        Need(NeedLevel.Advice, $"learn-manual:{area}", $"You've turned down every {name.ToLowerInvariant()} suggestion ({dropped}): stop suggesting?",
                            act: new SetAutoLevelCommand(_side, area, AutoLevel.Manual), label: "Manual", area: area);
                }
            }
        }

        private AutoScore Score(AutoArea area) =>
            _state.AutoScores.TryGetValue(area, out var s) ? s : _state.AutoScores[area] = new AutoScore();

        private int Habit(string key) => _state.Habits.TryGetValue(key, out var n) ? n : 0;

        // ---- Alerts: always listed ------------------------------------------------------------

        private void Alerts()
        {
            var mine = _w.Bodies.Where(b => b.Owner == _side).OrderBy(b => b.Id).ToList();
            foreach (var b in mine)
            {
                if (b.Hungry && b.PopK > 0) Need(NeedLevel.Urgent, $"hungry:{b.Id}", $"{b.Name} is going hungry", b.Id);
                if (Economy.Blockaded(_w, b)) Need(NeedLevel.Urgent, $"blockaded:{b.Id}", $"{b.Name} is blockaded", b.Id);
            }
            foreach (var s in _w.Sightings.Where(s => s.SeenBy == _side && !s.Own && _w.Hour - s.Hour < 6).OrderBy(s => s.FleetId))
            {
                var near = mine.Where(b => b.PopK > 0 && Vec2.Distance(s.Position, b.Position) < 0.15 * Physics.MmPerAu)
                    .OrderBy(b => Vec2.Distance(s.Position, b.Position)).ThenBy(b => b.Id).FirstOrDefault();
                if (near != null) Need(NeedLevel.Urgent, $"enemy-near:{near.Id}", $"Enemy fleet ({s.Ships} ships) near {near.Name}", near.Id);
            }
            foreach (var (enemy, from, to) in Lanes.RaidersNearRuns(_w, _side))
                Need(NeedLevel.Urgent, $"raiders:{from.Id}:{to.Id}", $"Raiders ({enemy.Ships} ships) near the {from.Name}–{to.Name} run", to.Id);
            foreach (var o in _w.Orders.Where(o => o.Side == _side && o.Waiting != null && !o.Waiting.Contains("Industry")).OrderBy(o => o.Id))
                Need(o.Kind == OrderKind.Ship ? NeedLevel.Urgent : NeedLevel.Soon, $"waiting:{o.Id}",
                    $"{Economy.NameOf(o)} waits: {o.Waiting}", o.YardBodyId ?? _home.Id);
            foreach (var b in mine)
                foreach (var p in b.Pieces.Where(p => p.MaterialsLeft is { Count: > 0 } && !p.Built))
                    if (Colonies.MaterialShort(b, p) is string why)
                        Need(NeedLevel.Soon, $"piece-waiting:{p.Id}", $"{Planets.BuildingKind(p.Kind)?.Name ?? p.Kind} at {b.Name} waits: {why}", b.Id);
            if (LevelFor(_w, _side, AutoArea.Research) == AutoLevel.Manual)
                foreach (var t in IdleTeams())
                    Need(NeedLevel.Soon, $"idle-team:{t.Id}", $"{t.Name} has nothing to research");
            foreach (var n in _state.SparkNotes.Where(n => _w.Hour - n.Hour < 48))
                Need(NeedLevel.Info, $"spark:{n.Idea}",
                    $"{n.Why}: {GameData.Research(n.Idea).Name} {(n.SpedUp ? "sped up" : "comes into view")}");
            foreach (var lost in _state.Lost.Where(l => _w.Hour - l.Hour < LostShipDays * 24))
                if (Economy.CanQueue(_w, _side, OrderKind.Ship, lost.ClassId) == null)
                    Need(NeedLevel.Advice, $"lost:{lost.Name}", $"{lost.Name} ({GameData.ShipClass(lost.ClassId).Name}) was lost: build a new one?", _home.Id,
                        act: new QueueOrderCommand(_side, OrderKind.Ship, lost.ClassId,
                            lost.FleetId is int fid && _w.FindFleet(fid) is { ShipIds.Count: > 0 } ? fid : null), label: "Build it", area: AutoArea.Shipyard);
        }

        private IEnumerable<ResearchTeam> IdleTeams() => _state.Teams.Where(t => Research.Ready(_w, t)
            && !_w.Orders.Any(o => o.Side == _side && o.Kind == OrderKind.Research && o.TeamId == t.Id)).OrderBy(t => t.Id);

        // ---- Areas ----------------------------------------------------------------------------

        /// <summary>Planet building: the shared planner's pick for each planet with nothing under way.</summary>
        private void Building()
        {
            var hungry = Planner.Hungry(_w, _side);
            var spares = Planner.SparesShort(_w, _side);
            var reserve = _w.Orders.FirstOrDefault(o => o.Side == _side && o.Kind == OrderKind.Ship && !o.Paid) is WorkOrder next ? Economy.CostOf(next) : 0;
            foreach (var b in _w.Bodies.Where(b => b.Owner == _side && b.PopK > 0 && b.Board != null && b.Building == null)
                         .OrderByDescending(b => b.Home).ThenByDescending(b => b.PopK).ThenBy(b => b.Id))
            {
                if (Planner.PlanetWant(_w, _side, b, hungry, spares) is not var (kind, x, y, urgent) || Planets.BuildingKind(kind) is not BuildingData data) continue;
                if (Planets.CanBuild(_w, _side, b.Id, kind, x, y) != null) continue;
                var auto = LevelFor(_w, _side, AutoArea.Building, b.Id) == AutoLevel.Auto;
                // Auto spends only what is spare after the next ship (urgent needs go first), and not too often.
                if (auto && (_state.Industry < data.Cost + (urgent ? 0 : reserve) || !urgent && _w.Hour < _state.NextAutoBuildHour)) continue;
                var mats = data.Materials is { Count: > 0 } m ? " (" + string.Join(", ", m.Select(kv => $"{kv.Value:0} {Colonies.GoodName(kv.Key).ToLowerInvariant()}")) + ")" : "";
                if (Offer(AutoArea.Building, $"build:{b.Id}:{kind}", $"Build a {data.Name.ToLowerInvariant()} at {b.Name} for {data.Cost:0} Industry{mats}",
                        new BuildOnPlanetCommand(_side, b.Id, kind, x, y), "Build it", b.Id, advice: urgent ? NeedLevel.Soon : NeedLevel.Advice)
                    && !urgent)
                    _state.NextAutoBuildHour = _w.Hour + 4 * 24;
            }
        }

        /// <summary>Workers: planets well short of the staff their buildings need get settlers topping them up.</summary>
        private void Workers()
        {
            foreach (var b in _w.Bodies.Where(b => b.Owner == _side && !b.Home && b.Board != null && b.PopK > 0 && !b.AutoTopUp).OrderBy(b => b.Id))
            {
                var need = Planets.StaffNeeded(b);
                if (need <= 0 || b.PopK >= need * 0.8 || b.PopK >= Planets.Cap(b) * 0.9) continue;
                Offer(AutoArea.Workers, $"topup:{b.Id}", $"Top up {b.Name} with settlers: {b.PopK:0.#}k people for {need:0.#}k jobs",
                    new SetAutoTopUpCommand(_side, b.Id, true), "Top up", b.Id);
            }
        }

        /// <summary>Transport on Advise: the shipments Auto would send, offered one by one.</summary>
        private void Transport()
        {
            if (LevelFor(_w, _side, AutoArea.Transport) != AutoLevel.Advise) return;
            foreach (var (from, to, good, amount) in Colonies.TransportWanted(_w, _side).Take(6))
                Need(NeedLevel.Advice, $"ship:{to.Id}:{good}", $"Send {amount:0} {Colonies.GoodName(good).ToLowerInvariant()} from {from.Name} to {to.Name}",
                    to.Id, act: new ShipGoodsCommand(_side, from.Id, to.Id, good, amount), label: "Send", area: AutoArea.Transport);
        }

        /// <summary>Settling: one empty world at a time, nearest and richest first, away from seen enemies.</summary>
        private void Settling()
        {
            var bound = _w.Orders.Count(o => o.Side == _side && o.SettleBodyId != null) + _w.Fleets.Count(f => f.Side == _side && f.PeopleRun != null);
            if (bound >= 1) return;
            var to = _w.Bodies.Where(b => b.Owner == null && b.PopK <= 0 && Planets.Settleable(b) && b.Industry > 0 && !Planets.SettlersBound(_w, _side, b.Id)
                                          && !_w.Sightings.Any(s => s.SeenBy == _side && !s.Own && _w.Hour - s.Hour < 240 && Vec2.Distance(s.Position, b.Position) < 0.1 * Physics.MmPerAu)
                                          && Planets.CanSettle(_w, _side, b.Id) == null)
                .OrderBy(b => Vec2.Distance(b.Position, _home.Position) / (1 + b.Industry)).ThenBy(b => b.Id).FirstOrDefault();
            if (to == null) return;
            Offer(AutoArea.Settling, $"settle:{to.Id}", $"Send settlers to {to.Name}", new SettleCommand(_side, to.Id), "Settle", to.Id);
        }

        /// <summary>Research: an idle team gets the planner's next idea.</summary>
        private void ResearchQueues()
        {
            foreach (var t in IdleTeams().ToList())
            {
                if (Planner.NextIdea(_w, _side, t) is not string idea) continue;
                var r = GameData.Research(idea);
                Offer(AutoArea.Research, $"research:{t.Id}", $"{t.Name}: research {r.Name} ({r.Cost:0} Industry)",
                    new QueueOrderCommand(_side, OrderKind.Research, idea, TeamId: t.Id), "Queue it");
            }
        }

        private readonly List<Fleet> _unserved = new();

        /// <summary>
        /// Fleet supply: a fighting fleet below a third of its supplies or missiles, away from home, with no
        /// tanker on its way gets a free tanker (one not on a run given by hand). With none free, the
        /// shipyard is asked for one.
        /// </summary>
        private void Supply()
        {
            var free = _w.Fleets.Where(f => f.Side == _side && Planner.SupplyFleet(_w, f) && !f.Retreating
                                            && (f.SupplyRunFor == null || f.AutoRun && _w.FindFleet(f.SupplyRunFor.Value) is not { ShipIds.Count: > 0 }))
                .OrderBy(f => f.Id).ToList();
            foreach (var f in _w.Fleets.Where(f => f.Side == _side && Planner.Warfleet(_w, f)).OrderBy(f => f.Id).ToList())
            {
                var low = Combat.SupplyShare(_w, f) < 1.0 / 3 || Planner.MissileShare(_w, f) < 1.0 / 3;
                if (!low || Economy.InOrbitAt(f, _home) || _w.Fleets.Any(o => o.SupplyRunFor == f.Id)) continue;
                var tanker = free.OrderBy(o => Vec2.Distance(o.Position, f.Position)).ThenBy(o => o.Id).FirstOrDefault();
                if (tanker == null)
                {
                    _unserved.Add(f);
                    Need(NeedLevel.Soon, $"low:{f.Id}", $"{f.Name} is low on supplies and no tanker is free", fleetId: f.Id);
                    continue;
                }
                if (Offer(AutoArea.Supply, $"supply:{f.Id}", $"Send {tanker.Name} on a supply run to {f.Name}",
                        new SetSupplyRunCommand(_side, tanker.Id, f.Id), "Send", fleetId: f.Id, advice: NeedLevel.Soon))
                    free.Remove(tanker);
            }
        }

        /// <summary>
        /// Shipyard on Auto queues support ships only: a tanker when a fleet waits for supplies and none is
        /// free, a freighter when planets wait for goods and no freighter is free. Never warships.
        /// </summary>
        private void Shipyard()
        {
            var queued = _w.Orders.Where(o => o.Side == _side && o.Kind == OrderKind.Ship).ToList();
            if (queued.Count >= 3) return;
            if (_unserved.Count > 0 && !queued.Any(o => o.Key == "tanker") && Economy.CanQueue(_w, _side, OrderKind.Ship, "tanker") == null)
                Offer(AutoArea.Shipyard, "yard:tanker", $"Build a tanker: {_unserved[0].Name} waits for supplies", new QueueOrderCommand(_side, OrderKind.Ship, "tanker"), "Build it");
            var waiting = Colonies.TransportWanted(_w, _side).Any() && !Colonies.FreeHaulers(_w, _side).Any();
            var haulers = _w.Fleets.Where(f => f.Side == _side).Sum(f => _w.ShipsOf(f).Count(s => s.ClassId is "freighter" or "bulk-freighter"));
            var planets = _w.Bodies.Count(b => b.Owner == _side && b.Board != null);
            var cls = Economy.CanQueue(_w, _side, OrderKind.Ship, "bulk-freighter") == null ? "bulk-freighter" : "freighter";
            if (waiting && haulers < planets && !queued.Any(o => o.Key is "freighter" or "bulk-freighter") && Economy.CanQueue(_w, _side, OrderKind.Ship, cls) == null)
                Offer(AutoArea.Shipyard, "yard:freighter", $"Build a {GameData.ShipClass(cls).Name.ToLowerInvariant()}: planets wait for goods and no freighter is free",
                    new QueueOrderCommand(_side, OrderKind.Ship, cls), "Build it");
        }
    }
}

/// <summary>Set an automation level: for an area, or for one planet or fleet in it (null level: back to the area's).</summary>
public sealed record SetAutoLevelCommand(Side Side, AutoArea Area, AutoLevel? Level, int? BodyId = null, int? FleetId = null) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        var state = Economy.Of(world, Side);
        if (Source == "player")
        {
            // Habits (see Stewards.Learn): a hand change the same way again and again; a new default resets them.
            if (BodyId == null && FleetId == null)
                foreach (var key in state.Habits.Keys.Where(k => k.StartsWith($"set:{Area}:", StringComparison.Ordinal) || k.EndsWith($":{Area}", StringComparison.Ordinal)).ToList())
                    state.Habits.Remove(key);
            else if (Level is AutoLevel to)
                state.Habits[$"set:{Area}:{to}"] = state.Habits.GetValueOrDefault($"set:{Area}:{to}") + 1;
            if (BodyId == null && FleetId == null) state.AutoScores.Remove(Area);
        }
        if (BodyId == null && FleetId == null)
        {
            state.Auto[Area] = Level ?? Stewards.Default(Area);
            if (Area == AutoArea.Transport) state.AutoTransport = state.Auto[Area] == AutoLevel.Auto;
        }
        else
        {
            state.AutoOverrides.RemoveAll(o => o.Area == Area && o.BodyId == BodyId && o.FleetId == FleetId);
            if (Level is AutoLevel l) state.AutoOverrides.Add(new AutoOverride(Area, BodyId, FleetId, l));
        }
        sim.Log.Info("auto", $"{Stewards.AreaName(Area)} set to {Level?.ToString() ?? "the area's level"}", new { side = Side.ToString(), Area, Level, BodyId, FleetId, Source });
    }
}

/// <summary>Press a Needs you item's act button: gives its order.</summary>
public sealed record AcceptNeedCommand(Side Side, string Key) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        var state = Economy.Of(world, Side);
        if (state.Needs.Find(n => n.Key == Key) is not { Act: GameCommand act } need) return;
        state.Needs.Remove(need);
        if (need.Area is AutoArea area && !Key.StartsWith("learn-", StringComparison.Ordinal))
            state.Habits[$"accept:{area}"] = state.Habits.GetValueOrDefault($"accept:{area}") + 1;
        if (need.Key.StartsWith("lost:", StringComparison.Ordinal)) state.Lost.RemoveAll(l => "lost:" + l.Name == need.Key);
        var c = act with { Source = Source };
        // An order for a far fleet still goes out at light speed.
        if (world.Light.On && c.Destination(world) is (_, Vec2 at) && Light.DelayHours(world, Side, at) > 0) Light.Send(sim, c);
        else c.Apply(world, sim);
    }
}

/// <summary>Dismiss a Needs you item for a few days.</summary>
public sealed record DismissNeedCommand(Side Side, string Key) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        var state = Economy.Of(world, Side);
        state.Dismissed[Key] = world.Hour + (Key.StartsWith("learn-", StringComparison.Ordinal) ? 30 : Stewards.DismissDays) * 24;
        if (state.Needs.Find(n => n.Key == Key) is { Area: AutoArea area, Act: not null } && !Key.StartsWith("learn-", StringComparison.Ordinal))
            state.Habits[$"dismiss:{area}"] = state.Habits.GetValueOrDefault($"dismiss:{area}") + 1;
        state.Needs.RemoveAll(n => n.Key == Key);
        if (Key.StartsWith("lost:", StringComparison.Ordinal)) state.Lost.RemoveAll(l => "lost:" + l.Name == Key);
    }
}
