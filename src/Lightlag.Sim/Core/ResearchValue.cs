namespace Lightlag.Sim.Core;

/// <summary>
/// What a research idea is worth to a side right now (the research review). Each idea lists its aims in
/// economy.json: broad ones (economy, expansion, war, defence, supply, intel, people) that a personality
/// weighs, and narrow ones (anti-missile, food, siege, crew...) that the situation weighs: what the side has
/// seen the enemy field, what it is short of, and what is happening to its planets and fleets. The race
/// leans on some aims (races.json researchLean: a Lattice side has no crews or mouths to feed). A computer
/// side also has a taste per broad aim rolled each game, and a direction it turns to for a while when
/// something happens (see Mind). Value is shared out over the time the idea takes, and an idea also gets
/// part of the value of what it opens. Only what the side itself can know is read.
/// </summary>
public static class ResearchValue
{
    public static readonly string[] Broad = { "economy", "expansion", "war", "defence", "supply", "intel", "people" };

    /// <summary>Narrow aims, weighed by the situation (and race), not the personality.</summary>
    public static readonly string[] Narrow =
    {
        "anti-missile", "anti-rail", "anti-pd", "anti-shield", "anti-armour", "heat", "siege", "hazard", "ground",
        "food", "happiness", "growth", "crowded", "crew", "power", "materials", "research", "fuel", "range", "stealth",
    };

    public static bool KnownAim(string aim) => Array.IndexOf(Broad, aim) >= 0 || Array.IndexOf(Narrow, aim) >= 0;

    public static int BroadIndex(string aim) => Array.IndexOf(Broad, aim);

    /// <summary>Share of an idea's value it gets from the best idea it opens.</summary>
    public const double ChainShare = 0.3;

    /// <summary>Days of work at which value is cut by about 30 % (sqrt(1 + days / this)).</summary>
    public const double DaysScale = 60;

    /// <summary>A narrow aim the situation weighs, how much, and why (for the thinking log).</summary>
    public sealed record Need(string Aim, double Factor, string Why);

    /// <summary>How much each broad aim matters to a personality (null: the player's automation, all even).</summary>
    public static double[] Weights(PersonalityProfile? p)
    {
        if (p == null) return Broad.Select(_ => 1.0).ToArray();
        return new[]
        {
            0.7 + 0.3 * p.Research + 0.2 * p.Expansion, // economy: everyone wants it
            0.4 + 0.8 * p.Expansion,                     // expansion
            0.4 + 0.8 * p.Aggression,                    // war
            0.4 + 0.8 * p.Defence,                       // defence
            0.5 + 0.3 * p.Expansion + 0.3 * p.Raiding,   // supply: far colonies and far raids
            0.4 + 0.5 * p.Raiding + 0.3 * p.Risk,        // intel
            0.4 + 0.5 * p.Expansion + 0.3 * p.Defence,   // people
        };
    }

    /// <summary>Which broad aim a narrow one serves, for choosing a direction.</summary>
    public static string BroadOf(string aim) => aim switch
    {
        "anti-missile" or "anti-rail" or "heat" or "siege" or "hazard" => "defence",
        "anti-pd" or "anti-shield" or "anti-armour" or "ground" => "war",
        "food" or "happiness" or "growth" or "crowded" or "crew" => "people",
        "power" or "materials" or "research" => "economy",
        "fuel" or "range" => "supply",
        "stealth" => "intel",
        _ => BroadIndex(aim) >= 0 ? aim : "economy",
    };

    /// <summary>What is happening to the side, as factors on narrow aims (1 when nothing is), with why.</summary>
    public static List<Need> Read(World w, Side side)
    {
        var needs = new List<Need>();
        void Add(string aim, double factor, string why) => needs.Add(new Need(aim, factor, why));

        // What it has seen the enemy field (battles, salvage, close looks).
        foreach (var enemy in Sides.Enemies(w, side))
            foreach (var t in Intel.Of(w, side, enemy).Tech)
            {
                switch (t.Part)
                {
                    case "missiles" when t.Level >= 2: Add("anti-missile", 1.3 + 0.1 * t.Level, $"the enemy fields missile brain {t.Level}"); break;
                    case "railguns" when t.Level >= 2: Add("anti-rail", 1.2 + 0.1 * t.Level, $"the enemy fields railguns Mk {t.Level}"); break;
                    case "point-defence" when t.Level >= 2: Add("anti-pd", 1.3 + 0.1 * t.Level, $"the enemy fields point defence Mk {t.Level}"); break;
                    case "shields" when t.Level >= 1: Add("anti-shield", 1.3 + 0.1 * t.Level, "the enemy fields shields"); break;
                    case "armour" when t.Level >= 2: Add("anti-armour", 1.2 + 0.1 * t.Level, $"the enemy fields armour Mk {t.Level}"); break;
                }
            }

        var held = w.Bodies.Where(b => b.Owner == side).ToList();
        var peopled = held.Where(b => b.PopK > 0).ToList();
        if (peopled.Count > 0)
        {
            var made = peopled.Sum(Planets.FoodMadePerDay);
            var need = Races.FoodWanted(w, side, peopled);
            if (need > 0 && made < need) Add("food", 2.0, "its people go hungry");
            else if (need > 0 && made < need * 1.15) Add("food", 1.4, "food is tight");
            if (w.Rules.Happiness)
            {
                var low = peopled.Min(b => b.Happiness);
                if (low < 35) Add("happiness", 2.0, $"{peopled.First(b => b.Happiness == low).Name} is close to giving up");
                else if (peopled.Average(b => b.Happiness) < 50) Add("happiness", 1.4, "its people are unhappy");
            }
            if (peopled.Any(b => b.PowerWanted > b.PowerMade + 1)) Add("power", 1.6, "planets are short of power");
            if (peopled.Any(b => Planets.Cap(b) > 0 && b.PopK > Planets.Cap(b) * 0.85)) Add("crowded", 1.5, "planets are filling up");
            if (peopled.Count(b => !b.Home && b.PopK < 5) >= 2) Add("growth", 1.4, "new colonies are small");
            if (peopled.Any(b => Economy.Blockaded(w, b))) Add("siege", 1.8, "a planet is blockaded");
            else if (peopled.Any(b => b.LandingSide != null && b.LandingSide != side)) Add("siege", 1.6, "troops are landing on a planet");
        }
        if (Planner.MaterialShort(w, side, "alloys") || Planner.MaterialShort(w, side, "electronics")) Add("materials", 1.5, "materials are short");
        if (held.Sum(b => Colonies.Held(b, Colonies.Fuel)) < 60) Add("fuel", 1.4, "fuel stores are low");
        var home = held.FirstOrDefault(b => b.Home) ?? held.FirstOrDefault();
        if (home != null && held.Any(b => Vec2.Distance(b.Position, home.Position) > 2 * Physics.MmPerAu)) Add("range", 1.3, "colonies are far from home");
        if (w.Bodies.Any(b => b.LandingSide == side)) Add("ground", 1.6, "its troops are fighting on the ground");
        else if (w.Fleets.Any(f => f.Side == side && w.ShipsOf(f).Any(s => s.CargoTroops > 0))) Add("ground", 1.3, "it has troops aboard");
        var stats = Stats.Of(w, side);
        if (stats.RockHits >= 3 || stats.HazardHull > 50) Add("hazard", 1.6, "rocks and flares have been hurting its ships");
        if (w.Ships.Any(s => s.Side == side && s.Heat > Heat.Capacity(s) * 0.8)) Add("heat", 1.4, "ships run hot");
        var lastSeen = w.Sightings.Where(s => s.SeenBy == side && !s.Own).Select(s => s.Hour).DefaultIfEmpty(-1e9).Max();
        if (w.Day > 60 && w.Hour - lastSeen > 30 * 24) Add("stealth", 1.3, "it has lost sight of the enemy");
        var battles = w.Choices.Where(c => c.Side == side && c.Kind == "battle").TakeLast(5).ToList();
        if (battles.Count >= 3 && battles.Count(c => c.What == "lost") > battles.Count(c => c.What == "won"))
        {
            Add("defence", 1.3, "it has been losing battles");
            Add("war", 1.2, "it has been losing battles");
        }
        return needs;
    }

    /// <summary>The situation as one factor per aim (the biggest reason for each).</summary>
    public static Dictionary<string, double> Factors(List<Need> needs) =>
        needs.GroupBy(n => n.Aim).ToDictionary(g => g.Key, g => g.Max(n => n.Factor));

    /// <summary>How a side values ideas now. Taste and Focus are the computer's (null for the player).</summary>
    public sealed record Outlook(double[] Weights, Dictionary<string, double> Situation, RaceData Race,
        double[]? Taste = null, string? Focus = null, ISet<string>? Favourites = null, string? LeanField = null,
        double ResearchMinded = 1, ResearchTeam? Team = null);

    public static Outlook For(World w, Side side, PersonalityProfile? profile = null, double[]? taste = null, string? focus = null,
        ISet<string>? favourites = null, string? leanField = null, ResearchTeam? team = null, List<Need>? needs = null) =>
        new(Weights(profile), Factors(needs ?? Read(w, side)), Races.Of(w, side), taste, focus, favourites, leanField,
            profile == null ? 1 : 0.7 + 0.6 * profile.Research, team);

    /// <summary>An idea's worth before its time and what it opens.</summary>
    public static double Base(World w, Side side, ResearchData r, Outlook o)
    {
        var aims = r.Aims ?? new List<string> { "economy" };
        double broad = 0;
        var nBroad = 0;
        double narrow = 1;
        foreach (var aim in aims)
        {
            var i = BroadIndex(aim);
            if (i >= 0)
            {
                var weight = o.Weights[i] * (o.Taste is { } t && i < t.Length ? t[i] : 1) * (o.Focus == aim ? 1.4 : 1);
                broad += weight * (o.Situation.TryGetValue(aim, out var f) ? f : 1);
                nBroad++;
            }
            else narrow *= o.Situation.TryGetValue(aim, out var f) ? f : aim == "research" ? o.ResearchMinded : 1;
            narrow *= o.Race.Lean(aim);
        }
        // Never quite worthless: even an idea the race has little use for keeps a fifth of its value.
        var value = r.Worth * (nBroad > 0 ? broad / nBroad : 0.8) * Math.Clamp(narrow, 0.2, 3);
        if (o.Focus != null && nBroad == 0 && aims.Any(a => BroadOf(a) == o.Focus)) value *= 1.4;
        if (o.Favourites?.Contains(r.Id) == true) value *= 1.25;
        if (o.LeanField != null && r.Field == o.LeanField) value *= 1.1;
        if (o.Team != null && o.Team.Kept.Contains(r.Field)) value *= 1.2;
        return value;
    }

    /// <summary>
    /// Score of every idea the side can queue now, best first: value (plus part of the best idea it opens)
    /// over the time it would take its quickest team, with counters to what the enemy fields, sparks and
    /// ideas for short materials raised.
    /// </summary>
    public static List<(string Id, double Score)> Rank(World w, Side side, Outlook o, IEnumerable<string>? only = null)
    {
        var state = Economy.Of(w, side);
        var open = (only ?? GameData.Economy.Research.Select(r => r.Id))
            .Where(id => GameData.ResearchById.ContainsKey(id) && Economy.CanQueue(w, side, OrderKind.Research, id) == null && (w.Light.On || !Tech.LagOnly(id)))
            .Distinct().ToList();
        if (open.Count == 0) return new();
        var counters = Intel.Counters(w, side).Select(c => c.Idea).ToHashSet();
        var baseOf = new Dictionary<string, double>();
        double BaseOf(ResearchData r) => baseOf.TryGetValue(r.Id, out var v) ? v : baseOf[r.Id] = Base(w, side, r, o);
        var teams = state.Teams.Count > 0 ? state.Teams : null;
        var ranked = new List<(string, double)>();
        foreach (var id in open)
        {
            var r = GameData.Research(id);
            var value = BaseOf(r);
            // Stepping stones: part of the best idea this one opens (not yet researched).
            var opens = GameData.Economy.Research.Where(x => !x.Later && x.Needs?.Contains(id) == true && !state.Researched.Contains(x.Id) && (w.Light.On || !Tech.LagOnly(x.Id)));
            var best = opens.Select(BaseOf).DefaultIfEmpty(0).Max();
            value += ChainShare * best;
            if (counters.Contains(id)) value *= 1.5;
            if (state.Sparked.Contains(id)) value *= 1.15;
            if (Planner.HelpsMaterials(w, side, id)) value *= 3;
            var speed = o.Team != null ? Research.Speed(o.Team, r.Field)
                : teams?.Max(t => Research.Speed(t, r.Field)) ?? 1;
            var days = Research.BaseHours(w, side, id) / 24 / Math.Max(0.05, speed);
            ranked.Add((id, value / Math.Sqrt(1 + days / DaysScale)));
        }
        return ranked.OrderByDescending(x => x.Item2).ThenBy(x => x.Item1, StringComparer.Ordinal).ToList();
    }

    /// <summary>The broad aim the situation pushes hardest now (null: nothing stands out), and why.</summary>
    public static (string Aim, string Why)? Pressing(List<Need> needs) =>
        needs.Where(n => n.Factor >= 1.5).OrderByDescending(n => n.Factor).ThenBy(n => n.Aim, StringComparer.Ordinal)
            .Select(n => ((string, string)?)(BroadOf(n.Aim), n.Why)).FirstOrDefault();
}
