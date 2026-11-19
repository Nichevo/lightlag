using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

// ---- Data (economy.json, "researchRoll") ------------------------------------------------------

/// <summary>
/// Research that is different each game. At the start each side rolls Leads (LeadCount ideas, LeadCut quicker
/// and in view from day one) and Hard problems (HardCount ideas, HardExtra slower), and hides LostCount ideas
/// from LostPool until a spark, a survey find, a capture or a trade turns them up.
/// Leads come from the side's founding trait seeds (TraitSeeds, by trait id), the map (MapSeeds) and random draws
/// from tiers LeadTiers; hard problems from the map and random draws from tiers HardTiers.
/// SurveyReveal: the chance that a survey find turns up a lost idea (when the side still has one hidden).
/// </summary>
public sealed record ResearchRollData(
    int[]? LeadCount = null,
    int[]? HardCount = null,
    int LostCount = 10,
    double LeadCut = 0.3,
    double HardExtra = 0.5,
    int[]? LeadTiers = null,
    int[]? HardTiers = null,
    int MaxMapLeads = 2,
    double SurveyReveal = 0.4,
    double LeadValue = 1.15,
    List<string>? LostPool = null,
    Dictionary<string, TraitSeed>? TraitSeeds = null,
    List<MapSeed>? MapSeeds = null);

/// <summary>A founding trait's research seeds: the ideas it makes leads (and, rarely, hard problems).</summary>
public sealed record TraitSeed(List<string>? Leads = null, List<string>? Hard = null);

/// <summary>
/// A map fact that shapes research: When is one of helium-3, rocks, volcanic, rare-earths, uranium, dim-star,
/// bright-star, flares, ice; Leads and Hard name the ideas it pushes; Why is the line the player sees.
/// </summary>
public sealed record MapSeed(string When, string Why, List<string>? Leads = null, List<string>? Hard = null);

// ---- State --------------------------------------------------------------------------------

/// <summary>One side's roll for this game: its leads and hard problems (with why each), and its lost ideas.</summary>
public sealed class ResearchRoll
{
    public List<string> Leads { get; set; } = new();
    public List<string> Hard { get; set; } = new();

    /// <summary>Why each lead or hard problem was rolled ("Founding trait: Belters", "Map: helium-3 in the system", "Chance").</summary>
    public Dictionary<string, string> Why { get; set; } = new();

    /// <summary>Ideas hidden this game. A lost idea is found once it is sparked (see Sparks), studied from the rival or researched.</summary>
    public List<string> Lost { get; set; } = new();

    /// <summary>Lost ideas found so far, and how.</summary>
    public Dictionary<string, string> Found { get; set; } = new();
}

// ---- Rules ----------------------------------------------------------------------------------

public static class ResearchRolls
{
    public static ResearchRollData D => GameData.Economy.ResearchRoll ?? new ResearchRollData();

    private static readonly int[] DefaultLeads = { 4, 5 }, DefaultHard = { 2, 3 }, DefaultLeadTiers = { 1, 3 }, DefaultHardTiers = { 2, 4 };

    public static ResearchRoll Of(World w, Side side) => Economy.Of(w, side).Roll;

    public static bool Lead(World w, Side side, string id) => Of(w, side).Leads.Contains(id);
    public static bool Hard(World w, Side side, string id) => Of(w, side).Hard.Contains(id);

    /// <summary>Is this idea hidden from the side (a lost idea not yet found)?</summary>
    public static bool Hidden(World w, Side side, string id)
    {
        var roll = Of(w, side);
        if (roll.Lost.Count == 0 || !roll.Lost.Contains(id)) return false;
        var state = Economy.Of(w, side);
        return !state.Researched.Contains(id) && !state.Sparked.Contains(id) && !roll.Found.ContainsKey(id)
               && RivalTech.StageOf(w, side, id) < RivalStage.Studied;
    }

    /// <summary>A lost idea that has turned up this game (it shows a "found" mark).</summary>
    public static bool FoundLost(World w, Side side, string id) => Of(w, side).Lost.Contains(id) && !Hidden(w, side, id);

    /// <summary>How the side found a lost idea (null: not found, or not lost).</summary>
    public static string? HowFound(World w, Side side, string id)
    {
        if (!FoundLost(w, side, id)) return null;
        if (Of(w, side).Found.TryGetValue(id, out var how)) return how;
        return Economy.Of(w, side).SparkNotes.LastOrDefault(n => n.Idea == id)?.Why ?? "turned up during the game";
    }

    /// <summary>Time factor from the roll: leads are quicker, hard problems slower (1 when neither).</summary>
    public static double TimeFactor(World w, Side side, string id)
    {
        var roll = Of(w, side);
        if (roll.Leads.Contains(id)) return 1 - D.LeadCut;
        if (roll.Hard.Contains(id)) return 1 + D.HardExtra;
        return 1;
    }

    /// <summary>
    /// The side's founding traits. Founding traits are a later build: until then sides have none and the roll
    /// reads only the map and chance. The trait build fills SideState.Traits and the seeds plug in by trait id.
    /// </summary>
    public static IEnumerable<string> TraitsOf(World w, Side side) => Economy.Of(w, side).Traits;

    /// <summary>Is this map fact true of the world? (see MapSeed)</summary>
    public static bool MapHas(World w, string when)
    {
        IEnumerable<Body> worlds = w.Bodies.Where(b => b.Kind != BodyKind.Star && b.Kind != BodyKind.Station);
        bool Deposit(string mineral) => worlds.Any(b => b.Board?.Deposits.Any(d => d.Mineral == mineral) == true);
        return when switch
        {
            "helium-3" => worlds.Any(b => b.Type is "gas" or "ice-giant") || Deposit("helium-3"),
            "rocks" => worlds.Count(b => b.Kind == BodyKind.Asteroid) >= 4,
            "volcanic" => worlds.Any(b => b.Type == "volcanic"),
            "rare-earths" => Deposit("rare-earths"),
            "uranium" => Deposit("uranium"),
            "ice" => worlds.Any(b => b.Type is "ice" or "ice-giant"),
            "dim-star" => w.Map.StarLight < 0.5,
            "bright-star" => w.Map.StarLight >= 0.99,
            "flares" => w.Map.FlareFactor > 1,
            _ => false,
        };
    }

    /// <summary>Can this idea be in a roll at all in this game (in the data, not a later build, of use with the light rules)?</summary>
    private static bool Rollable(World w, string id) =>
        GameData.ResearchById.TryGetValue(id, out var r) && !r.Later && (w.Light.On || !Tech.LagOnly(id));

    /// <summary>
    /// Roll every side's leads, hard problems and lost ideas for a new game, from the game seed (its own stream,
    /// so the rest of the game's randomness is untouched). Does nothing when the option is off.
    /// </summary>
    public static void Setup(World w, ulong seed)
    {
        if (!w.Rules.ResearchVariety) return;
        foreach (var side in Sides.InPlay(w))
        {
            var rng = new SimRandom(seed, stream: 61 + (ulong)side);
            Economy.Of(w, side).Roll = Roll(w, side, rng);
        }
    }

    /// <summary>One side's roll (see Setup). Public for tests.</summary>
    public static ResearchRoll Roll(World w, Side side, SimRandom rng)
    {
        var d = D;
        var roll = new ResearchRoll();
        var leadCount = Pick(rng, d.LeadCount ?? DefaultLeads);
        var hardCount = Pick(rng, d.HardCount ?? DefaultHard);
        var taken = new HashSet<string>(Economy.Of(w, side).Researched);

        void AddLead(string id, string why)
        {
            if (roll.Leads.Count >= leadCount || !Rollable(w, id) || !taken.Add(id)) return;
            roll.Leads.Add(id);
            roll.Why[id] = why;
        }
        void AddHard(string id, string why)
        {
            if (roll.Hard.Count >= hardCount || !Rollable(w, id) || !taken.Add(id)) return;
            roll.Hard.Add(id);
            roll.Why[id] = why;
        }

        // 1. Founding trait seeds (in the order the traits were taken).
        foreach (var trait in TraitsOf(w, side))
            if (d.TraitSeeds?.GetValueOrDefault(trait) is TraitSeed seed)
            {
                foreach (var id in seed.Leads ?? new()) AddLead(id, $"Founding trait: {Name(trait)}");
                foreach (var id in seed.Hard ?? new()) AddHard(id, $"Founding trait: {Name(trait)}");
            }

        // 2. The map: every fact that holds offers its ideas; up to MaxMapLeads leads come from them, picked at random.
        var mapLeads = new List<(string Id, string Why)>();
        foreach (var m in d.MapSeeds ?? new())
        {
            if (!MapHas(w, m.When)) continue;
            foreach (var id in m.Leads ?? new()) mapLeads.Add((id, $"Map: {m.Why}"));
            foreach (var id in m.Hard ?? new()) AddHard(id, $"Map: {m.Why}");
        }
        var fromMap = 0;
        while (mapLeads.Count > 0 && fromMap < d.MaxMapLeads && roll.Leads.Count < leadCount)
        {
            var i = rng.Range(0, mapLeads.Count);
            var (id, why) = mapLeads[i];
            mapLeads.RemoveAt(i);
            var before = roll.Leads.Count;
            AddLead(id, why);
            if (roll.Leads.Count > before) fromMap++;
        }

        // 3. Chance fills the rest. The pool is in data order, so the same seed always rolls the same.
        var lost = new HashSet<string>(d.LostPool ?? new());
        var lt = d.LeadTiers ?? DefaultLeadTiers;
        var ht = d.HardTiers ?? DefaultHardTiers;
        Fill(rng, roll.Leads.Count, leadCount, r => r.Tier >= lt[0] && r.Tier <= lt[^1] && !lost.Contains(r.Id) && !taken.Contains(r.Id) && Rollable(w, r.Id),
            id => AddLead(id, "Chance"));
        Fill(rng, roll.Hard.Count, hardCount, r => r.Tier >= ht[0] && r.Tier <= ht[^1] && !lost.Contains(r.Id) && !taken.Contains(r.Id) && Rollable(w, r.Id),
            id => AddHard(id, "Chance"));

        // 4. Lost ideas: from the pool (minor ideas no other idea, class or station needs), never a lead or hard problem.
        var pool = (d.LostPool ?? new()).Where(id => Rollable(w, id) && !taken.Contains(id)).ToList();
        while (pool.Count > 0 && roll.Lost.Count < d.LostCount)
        {
            var i = rng.Range(0, pool.Count);
            roll.Lost.Add(pool[i]);
            pool.RemoveAt(i);
        }
        return roll;
    }

    private static int Pick(SimRandom rng, int[] range) => range.Length == 0 ? 0 : rng.Range(range[0], range[^1] + 1);

    private static void Fill(SimRandom rng, int have, int want, Func<ResearchData, bool> fits, Action<string> add)
    {
        var pool = GameData.Economy.Research.Where(fits).Select(r => r.Id).ToList();
        for (var n = have; n < want && pool.Count > 0; n++)
        {
            var i = rng.Range(0, pool.Count);
            add(pool[i]);
            pool.RemoveAt(i);
        }
    }

    /// <summary>"torch-born" → "Torch-born".</summary>
    public static string Name(string trait) => trait.Length == 0 ? trait : char.ToUpperInvariant(trait[0]) + trait[1..];

    /// <summary>A lost idea turns up for a side: it comes into view (as a spark does) and the news goes on the spark list.</summary>
    public static void Reveal(Simulation sim, Side side, string id, string how)
    {
        var w = sim.World;
        if (!Hidden(w, side, id)) return;
        var state = Economy.Of(w, side);
        Of(w, side).Found[id] = how;
        if (!state.Sparked.Contains(id)) state.Sparked.Add(id);
        state.SparkNotes.Add(new SparkNote(id, $"Lost idea found: {how}", w.Hour, false));
        if (state.SparkNotes.Count > 60) state.SparkNotes.RemoveAt(0);
        sim.Log.Info("research", "Lost idea found", new { side = side.ToString(), idea = id, how });
    }

    /// <summary>Turn up one of the side's hidden lost ideas at random (none left: nothing happens). Returns the idea.</summary>
    public static string? RevealOne(Simulation sim, Side side, string how)
    {
        var hidden = Of(sim.World, side).Lost.Where(id => Hidden(sim.World, side, id)).ToList();
        if (hidden.Count == 0) return null;
        var id = hidden[sim.Rng.Range(0, hidden.Count)];
        Reveal(sim, side, id, how);
        return id;
    }

    /// <summary>Has this side's intel on the rival got far enough to know its leads and hard problems?</summary>
    public static bool KnowsRivalRoll(World w, Side side, Side rival) => Intel.Level(Intel.Of(w, side, rival)) >= KnownAtLevel;

    /// <summary>Intel level at which a rival's leads and hard problems are known (Understood: its research fields).</summary>
    public const int KnownAtLevel = 3;

    /// <summary>"Leads: Engine Mk II, Fusion power · Hard problems: Solar sails", or "" when the roll is empty.</summary>
    public static string Describe(World w, Side side)
    {
        var roll = Of(w, side);
        if (roll.Leads.Count == 0 && roll.Hard.Count == 0) return "";
        string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(id => GameData.Research(id).Name));
        return $"Leads: {Names(roll.Leads)}" + (roll.Hard.Count > 0 ? $" · Hard problems: {Names(roll.Hard)}" : "");
    }

    /// <summary>Check the data: every id named is a real idea, and no lost-pool idea is needed by another idea.</summary>
    public static string? Problem(EconomyData e)
    {
        if (e.ResearchRoll is not { } d) return null;
        var ids = e.Research.Select(r => r.Id).ToHashSet();
        var needed = e.Research.SelectMany(r => r.Needs ?? new()).ToHashSet();
        foreach (var id in d.LostPool ?? new())
        {
            if (!ids.Contains(id)) return $"researchRoll.lostPool names unknown idea '{id}'";
            if (needed.Contains(id)) return $"researchRoll.lostPool: '{id}' is needed by another idea, so it can't be lost";
        }
        foreach (var (trait, seed) in d.TraitSeeds ?? new())
            foreach (var id in (seed.Leads ?? new()).Concat(seed.Hard ?? new()))
                if (!ids.Contains(id)) return $"researchRoll.traitSeeds.{trait} names unknown idea '{id}'";
        foreach (var m in d.MapSeeds ?? new())
            foreach (var id in (m.Leads ?? new()).Concat(m.Hard ?? new()))
                if (!ids.Contains(id)) return $"researchRoll.mapSeeds ({m.When}) names unknown idea '{id}'";
        if (!(d.LeadCut is >= 0 and < 1 && d.HardExtra >= 0 && d.LostCount >= 0 && d.SurveyReveal is >= 0 and <= 1))
            return "researchRoll needs leadCut in [0, 1), hardExtra and lostCount of 0 or more, surveyReveal in [0, 1]";
        return null;
    }
}
