using System.Globalization;
using System.Text.Json.Serialization;

namespace Lightlag.Sim.Core;

// ---- What a finished game leaves behind -----------------------------------------------------------

/// <summary>How one battle went for a computer-run side: the odds it had going in, and won / lost / even.</summary>
public sealed record BattleLesson(double Odds, string Outcome);

/// <summary>
/// One side of a finished game: its race, and for a computer-run side the style it started with (personality and
/// the leaning bands, see <see cref="Leanings.Style"/>), how the game went (Score 1 won, 0 lost; a game that ran out
/// of days scores the control leader 0.6 and the rest 0.4) and its battles.
/// </summary>
public sealed record SideLesson(Side Side, bool Computer, string Race, string? Personality, Dictionary<string, string>? Style,
    double Score, double Control, List<BattleLesson> Battles);

/// <summary>
/// How the person played a game, for the rival to counter next time: the day of their first battle (null: none),
/// battles fought, the share of warships they built that carry missiles, supply ships they caught, and colonies
/// they took (and the day of the first).
/// </summary>
public sealed record PlayerHabits(double? FirstBattleDay, int Battles, double MissileShare, int Warships, int Raids, int Takes, double? FirstTakeDay);

/// <summary>
/// One finished game in the rival's memory. Build is the game version and Balance a fingerprint of the data
/// files (<see cref="GameData.BalanceStamp"/>): lessons from another balance count for less. Id only keeps a
/// game from being stored twice; nothing about the map, the seed or the starts is kept, so nothing here can
/// tell the rival where anyone starts.
/// </summary>
public sealed record GameLesson(string Id, string Build, string Balance, string Kind, double Days, string? Winner, string Reason,
    List<SideLesson> Sides, PlayerHabits? Player)
{
    public const string Overnight = "overnight", Played = "played";
}

/// <summary>The rival's memory: the latest finished games, oldest first.</summary>
public sealed class RivalMemory
{
    public const string FileName = "rival-memory.json";

    /// <summary>The most games kept; the oldest go first.</summary>
    public const int MaxGames = 400;

    public int Version { get; set; } = 1;
    public List<GameLesson> Games { get; set; } = new();

    /// <summary>Adds a game (once: the same Id again is ignored). True if it was added.</summary>
    public bool Add(GameLesson g)
    {
        if (Games.Any(x => x.Id == g.Id)) return false;
        Games.Add(g);
        if (Games.Count > MaxGames) Games.RemoveRange(0, Games.Count - MaxGames);
        return true;
    }

    public static string PathIn(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>The memory in a data folder (empty if there is none, or it can't be read).</summary>
    public static RivalMemory Load(string dataDir)
    {
        var path = PathIn(dataDir);
        try { return File.Exists(path) ? Json.Deserialize<RivalMemory>(File.ReadAllText(path)) : new RivalMemory(); }
        catch (Exception) { return new RivalMemory(); }
    }

    public void Save(string dataDir)
    {
        var path = PathIn(dataDir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Json.Serialize(this, indented: true));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Forget everything: deletes the memory file.</summary>
    public static void Forget(string dataDir)
    {
        var path = PathIn(dataDir);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Adds one finished game to the memory in a data folder and saves it. True if it was new.</summary>
    public static bool Remember(string dataDir, GameLesson g)
    {
        var mem = Load(dataDir);
        if (!mem.Add(g)) return false;
        mem.Save(dataDir);
        return true;
    }
}

// ---- What the rival takes into a new game ----------------------------------------------------------

/// <summary>Off; learn only from overnight computer-vs-computer runs; or from those and the person's own games.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LearnMode { Off, Overnight, All }

/// <summary>
/// Shifts to the rival's profile and leanings that counter how the person has played (see Lessons.Learn).
/// Profile shifts are added to the personality's axes (kept within 0..1), Escorts to the escort leaning (kept
/// within its rolled range), and ResearchAim (a broad aim) is given the top research taste a roll can give.
/// </summary>
public sealed record Counters(double Defence = 0, double Expansion = 0, double Aggression = 0, double Escorts = 0, string? ResearchAim = null);

/// <summary>
/// What the rival learned from past games, worked out before a game starts and handed in with the game options
/// (so it is in the replay and the save, and the game stays deterministic). Personalities: race ("*" for any)
/// to a weight multiplier per personality. Style: a preferred band per leaning (keys as in Leanings.Style).
/// Odds: the odds leaning to take. Counters: answers to the person's habits. Notes and CounterNotes: the same in words.
/// </summary>
public sealed record Learned(int Games, int PlayedGames, Dictionary<string, Dictionary<string, double>>? Personalities = null,
    Dictionary<string, string>? Style = null, double? Odds = null, Counters? Counters = null, List<string>? Notes = null,
    List<string>? CounterNotes = null);

/// <summary>
/// The rival learning from past games. When a game ends, <see cref="Of"/> writes down what happened (game side and
/// headless runs store it in the data folder's rival memory). When a new one starts, <see cref="Learn"/> turns the
/// memory into a <see cref="Learned"/> that Mind.Setup leans the rival's start by. It plays fair: the memory holds
/// only general tendencies (no map, seed or start), it is read once at the start and frozen into the game, and every
/// lean stays inside what an ordinary random roll could give: the rival chooses better, it never gets a bonus.
/// </summary>
public static class Lessons
{
    /// <summary>The share of leanings still rolled at random, so the rival keeps trying new things.</summary>
    public const double Explore = 1.0 / 3;

    /// <summary>Weight of a game from another balance; once FreshEnough games are on this balance, older ones count for nothing.</summary>
    public const double OldBalanceWeight = 0.25;
    public const int FreshEnough = 50;

    /// <summary>Games against the person count this much more than overnight games for style.</summary>
    public const double PlayedWeight = 2;

    /// <summary>Weighted samples needed before a band, personality or odds lesson is used.</summary>
    public const double MinSamples = 3, MinBattles = 8;

    /// <summary>Each older game against the person counts this much less for their habits.</summary>
    public const double HabitFade = 0.85;

    public static readonly string[] StyleKeys = { "missiles", "field", "escorts", "bigShips", "odds", "pull" };

    // ---- Recording ---------------------------------------------------------------------------------

    /// <summary>What happened in a finished (or stopped) game.</summary>
    public static GameLesson Of(Simulation sim, string kind, string build)
    {
        var w = sim.World;
        var leader = Sides.InPlay(w).OrderByDescending(s => Planets.Control(w, s).Share).ThenBy(s => (int)s).First();
        var sides = new List<SideLesson>();
        foreach (var side in Sides.InPlay(w))
        {
            var m = Mind.Of(w, side);
            var score = w.Winner is Side won ? (won == side ? 1 : 0) : side == leader ? 0.6 : 0.4;
            var battles = w.Battles.Where(b => b.EndHour != null && b.For(side).ShipsIn > 0 && b.Foes(side).ShipsIn > 0)
                .Select(b =>
                {
                    var mine = b.For(side);
                    var theirs = b.Foes(side);
                    var odds = theirs.PowerIn > 0 ? mine.PowerIn / theirs.PowerIn : 9.99;
                    return new BattleLesson(Math.Round(Math.Min(odds, 9.99), 2), Choices.Outcome(mine, theirs));
                }).ToList();
            sides.Add(new SideLesson(side, m != null, Economy.Of(w, side).Race, m?.Personality.ToString(),
                m is { Style.Count: > 0 } ? new Dictionary<string, string>(m.Style) : null,
                score, Math.Round(Planets.Control(w, side).Share, 3), m != null ? battles : new List<BattleLesson>()));
        }
        var human = Sides.InPlay(w).Where(s => Mind.Of(w, s) == null).Cast<Side?>().FirstOrDefault();
        return new GameLesson($"{w.Tick}-{sim.StateHash()}", build, GameData.BalanceStamp, kind, Math.Round(w.Hour / 24, 1),
            w.Winner?.ToString(), w.WinReason ?? "", sides, human is Side h ? HabitsOf(w, h) : null);
    }

    /// <summary>How one side played (see PlayerHabits).</summary>
    public static PlayerHabits HabitsOf(World w, Side side)
    {
        var fought = w.Battles.Where(b => b.For(side).ShipsIn > 0 && b.Foes(side).ShipsIn > 0).ToList();
        int warships = 0, missiles = 0;
        foreach (var c in w.Choices.Where(c => c.Side == side && c.Kind == "ship"))
        {
            if (!GameData.ShipClasses.TryGetValue(c.What, out var cls)) continue;
            var withMissiles = cls.Missiles > 0 || c.Detail.Contains("missile", StringComparison.Ordinal);
            if (cls.Railguns <= 0 && !withMissiles) continue;
            warships++;
            if (withMissiles) missiles++;
        }
        var takes = w.Choices.Where(c => c.Side == side && c.Kind is "invasion-won" or "blockade-won").ToList();
        return new PlayerHabits(fought.Count > 0 ? Math.Round(fought.Min(b => b.StartHour) / 24, 1) : null, fought.Count,
            warships > 0 ? Math.Round((double)missiles / warships, 3) : 0, warships,
            w.Choices.Count(c => c.Side == side && c.Kind == "raid"), takes.Count,
            takes.Count > 0 ? Math.Round(takes.Min(c => c.Hour) / 24, 1) : null);
    }

    // ---- Learning -----------------------------------------------------------------------------------

    /// <summary>What the rival takes into a new game from its memory (null: nothing to go on, or learning off).</summary>
    public static Learned? Learn(RivalMemory memory, LearnMode mode, string balance)
    {
        if (mode == LearnMode.Off) return null;
        var games = memory.Games.Where(g => mode == LearnMode.All || g.Kind == GameLesson.Overnight).ToList();
        var fresh = games.Count(g => g.Balance == balance);
        double Weight(GameLesson g) => g.Balance == balance ? 1 : fresh >= FreshEnough ? 0 : OldBalanceWeight;
        games = games.Where(g => Weight(g) > 0).ToList();
        if (games.Count == 0) return null;
        var notes = new List<string>();

        var samples = games.SelectMany(g => g.Sides.Where(s => s.Computer)
            .Select(s => (Side: s, W: Weight(g) * (g.Kind == GameLesson.Played ? PlayedWeight : 1)))).ToList();

        // Personalities: by race, and over every race ("*").
        var personalities = new Dictionary<string, Dictionary<string, double>>();
        foreach (var race in samples.Select(s => s.Side.Race).Distinct().OrderBy(r => r, StringComparer.Ordinal).Prepend("*"))
        {
            var mine = samples.Where(s => race == "*" || s.Side.Race == race).ToList();
            var weights = new Dictionary<string, double>();
            foreach (var g in mine.Where(s => s.Side.Personality != null).GroupBy(s => s.Side.Personality!).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var (rate, n) = Rate(g.Select(s => (s.Side.Score, s.W)));
                if (n >= MinSamples) weights[g.Key] = Math.Round(Math.Clamp(0.5 + rate, 0.6, 1.5), 3);
            }
            if (weights.Count > 0) personalities[race] = weights;
        }
        if (personalities.TryGetValue("*", out var any) && any.Count > 0)
        {
            var best = any.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First();
            if (best.Value > 1.05) notes.Add($"leans towards playing as a {best.Key}, the personality that has done best");
        }

        // Style: the band of each leaning that has done clearly better than the leaning as a whole.
        var style = new Dictionary<string, string>();
        foreach (var key in StyleKeys)
        {
            var withKey = samples.Where(s => s.Side.Style?.ContainsKey(key) == true).ToList();
            var (all, _) = Rate(withKey.Select(s => (s.Side.Score, s.W)));
            var bands = withKey.GroupBy(s => s.Side.Style![key])
                .Select(g => (Band: g.Key, Rate: Rate(g.Select(s => (s.Side.Score, s.W)))))
                .Where(b => b.Rate.N >= MinSamples)
                .OrderByDescending(b => b.Rate.Rate).ThenBy(b => b.Band, StringComparer.Ordinal).ToList();
            if (bands.Count > 0 && bands[0].Rate.Rate >= all + 0.05)
            {
                style[key] = bands[0].Band;
                notes.Add($"prefers {bands[0].Band}");
            }
        }

        // Odds: how its fights at fair-to-good odds went. Losing those too often makes it more careful; winning
        // close fights makes it bolder.
        double? odds = null;
        var fights = samples.SelectMany(s => s.Side.Battles.Select(b => (b, s.W))).ToList();
        var fair = Rate(fights.Where(f => f.b.Odds >= 1 && f.b.Odds < 1.5).Select(f => (Score(f.b.Outcome), f.W)));
        var close = Rate(fights.Where(f => f.b.Odds >= 0.7 && f.b.Odds < 1).Select(f => (Score(f.b.Outcome), f.W)));
        if (fair.N >= MinBattles)
        {
            var lean = Math.Clamp(1 + 0.4 * (0.6 - fair.Rate), 0.85, 1.15);
            if (close.N >= MinBattles / 2 && close.Rate > 0.55) lean = Math.Min(lean, 0.92);
            odds = Math.Round(lean, 3);
            if (odds > 1.03) notes.Add($"wants better odds before it fights (it won {fair.Rate:P0} of fights at 1 to 1.5 odds)");
            else if (odds < 0.97) notes.Add("fights at closer odds (close fights have gone its way)");
        }

        var played = games.Where(g => g.Kind == GameLesson.Played && g.Player != null).ToList();
        var counterNotes = new List<string>();
        var counters = played.Count > 0 ? CountersFor(played, Weight, counterNotes) : null;
        return new Learned(games.Count, played.Count, personalities.Count > 0 ? personalities : null, style.Count > 0 ? style : null, odds, counters,
            notes, counters != null ? counterNotes : null);
    }

    /// <summary>Answers to the person's habits, newer games counting more.</summary>
    private static Counters? CountersFor(List<GameLesson> played, Func<GameLesson, double> balanceWeight, List<string> notes)
    {
        var weighted = played.Select((g, i) => (H: g.Player!, W: balanceWeight(g) * Fade(played.Count - 1 - i), Days: Math.Max(1, g.Days))).ToList();
        var total = weighted.Sum(x => x.W);
        if (total < 1.5) return null; // two games at the least
        double Avg(Func<(PlayerHabits H, double W, double Days), double> f) => weighted.Sum(x => f(x) * x.W) / total;
        var withBattles = weighted.Where(x => x.H.FirstBattleDay != null).ToList();
        var firstBattle = withBattles.Count > 0 ? withBattles.Sum(x => x.H.FirstBattleDay!.Value * x.W) / withBattles.Sum(x => x.W) : (double?)null;
        var withShips = weighted.Where(x => x.H.Warships > 0).ToList();
        var missiles = withShips.Count > 0 ? withShips.Sum(x => x.H.MissileShare * x.W) / withShips.Sum(x => x.W) : (double?)null;
        var raids = Avg(x => x.H.Raids);
        var takes = Avg(x => x.H.Takes);
        var battlesPer100 = Avg(x => x.H.Battles * 100 / x.Days);

        double defence = 0, expansion = 0, aggression = 0, escorts = 0;
        string? aim = null;
        if (firstBattle < 40)
        {
            defence += 0.15;
            notes.Add($"guards its colonies sooner: you tend to fight early (first battle around day {firstBattle:0})");
        }
        if (takes >= 1)
        {
            defence += 0.1;
            notes.Add($"puts more guns on its colonies: you take about {takes:0.#} of its colonies a game");
        }
        if (missiles >= 0.5)
        {
            escorts += 0.3;
            aim = "defence";
            notes.Add($"builds more escorts and point defence: {missiles:P0} of your warships carry missiles");
        }
        else if (missiles <= 0.15)
        {
            escorts -= 0.2;
            notes.Add("builds fewer escorts: you rarely use missiles");
        }
        if (raids >= 2)
        {
            escorts += 0.2;
            aim ??= "supply";
            notes.Add($"guards its supply ships better: you catch about {raids:0} a game");
        }
        if (battlesPer100 < 1 && (firstBattle == null || firstBattle > 100))
        {
            expansion += 0.1;
            aggression += 0.1;
            notes.Add("expands and pushes harder: you tend to play quietly");
        }
        defence = Math.Min(defence, 0.2);
        if (defence == 0 && expansion == 0 && aggression == 0 && escorts == 0 && aim == null) return null;
        return new Counters(Math.Round(defence, 3), Math.Round(expansion, 3), Math.Round(aggression, 3), Math.Round(escorts, 3), aim);
    }

    private static double Fade(int age)
    {
        var f = 1.0;
        for (var i = 0; i < age; i++) f *= HabitFade;
        return f;
    }

    private static double Score(string outcome) => outcome switch { "won" => 1, "even" => 0.5, _ => 0 };

    /// <summary>A weighted success rate pulled towards a half (one win and one loss to start), and the weight behind it.</summary>
    private static (double Rate, double N) Rate(IEnumerable<(double Score, double W)> xs)
    {
        double n = 0, s = 0;
        foreach (var (score, w) in xs) { n += w; s += score * w; }
        return ((s + 1) / (n + 2), n);
    }

    // ---- Applying ---------------------------------------------------------------------------------

    /// <summary>A leaning's value for a band (with a little jitter so games still differ), as Leanings.Bands names them.</summary>
    internal static void ApplyStyle(Leanings lean, ref PullPreset pull, string key, string band, SimRandom rng)
    {
        var jitter = rng.Range(-1.0, 1.0);
        static double Level(string band, string more, string fewer) => band.StartsWith(more, StringComparison.Ordinal) ? 1 : band.StartsWith(fewer, StringComparison.Ordinal) ? -1 : 0;
        switch (key)
        {
            case "missiles":
                lean.Missiles = Math.Round(Math.Clamp(0.13 * (band == "more missiles" ? 1 : band == "more railguns" ? -1 : 0) + 0.05 * jitter, -0.2, 0.2), 3);
                break;
            case "field":
                var field = band.Replace(" research first", "", StringComparison.Ordinal);
                if (Array.IndexOf(Research.Fields, field) >= 0) lean.Field = field;
                break;
            case "escorts":
                lean.Escorts = Math.Round(Math.Clamp(1 + 0.4 * Level(band, "more", "fewer") + 0.15 * jitter, 0.4, 1.6), 3);
                break;
            case "bigShips":
                lean.BigShips = Math.Round(Math.Clamp(1 + 0.4 * Level(band, "more", "fewer") + 0.15 * jitter, 0.4, 1.6), 3);
                break;
            case "odds":
                lean.Odds = Math.Round(Math.Clamp(1 + 0.1 * (band == "careful odds" ? 1 : band == "bold odds" ? -1 : 0) + 0.03 * jitter, 0.85, 1.15), 3);
                break;
            case "pull":
                var name = band.Replace("pull back ", "", StringComparison.Ordinal);
                if (Enum.TryParse<PullPreset>(name, out var p) && p is PullPreset.Cautious or PullPreset.Standard or PullPreset.FightOn) pull = p;
                break;
        }
    }

    /// <summary>A profile with the counters' shifts added (each axis kept within 0..1).</summary>
    public static PersonalityProfile Shift(PersonalityProfile p, Counters? c) => c == null ? p : p with
    {
        Defence = Math.Clamp(p.Defence + c.Defence, 0, 1),
        Expansion = Math.Clamp(p.Expansion + c.Expansion, 0, 1),
        Aggression = Math.Clamp(p.Aggression + c.Aggression, 0, 1),
    };

    /// <summary>One line for the after-game screen: what the rival brought into this game.</summary>
    public static string Summary(MindState m)
    {
        if (m.LearnedGames <= 0) return "";
        var from = string.Create(CultureInfo.InvariantCulture, $"It drew on {m.LearnedGames} past game{(m.LearnedGames == 1 ? "" : "s")}");
        return m.LearnedNotes.Count == 0 ? from + ", but had nothing clear to learn yet." : from + ": it " + string.Join("; ", m.LearnedNotes) + ".";
    }
}
