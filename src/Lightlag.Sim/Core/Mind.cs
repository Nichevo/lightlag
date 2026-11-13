using System.Text.Json.Serialization;

namespace Lightlag.Sim.Core;

// ---- Settings and data --------------------------------------------------------------------------

public enum Difficulty { Easy, Normal }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Personality { Raider, Expander, Turtle, Trader, Hunter, Opportunist }

/// <summary>A side run by the computer: how hard, and (for tests) which personality instead of a random one.</summary>
public sealed record AiSetup(Side Side, Difficulty Difficulty = Difficulty.Normal, Personality? Personality = null);

/// <summary>Choices made when a game starts that are not in the scenario file. Stored in the replay.</summary>
/// <summary>
/// Game-start choices. Happiness, SupplyLines, StartPopulation and RandomStarts are the optional systems
/// (null: the default, see GameRules).
/// </summary>
public sealed record GameOptions(List<AiSetup>? Ai = null, LightSetup? Light = null, AutonomyLevel? Autonomy = null, bool? Decapitation = null,
    bool? Happiness = null, bool? SupplyLines = null, bool? StartPopulation = null, bool? RandomStarts = null, double? ChoiceSpread = null,
    bool? FuelTypes = null, bool? Hazards = null, EventRate? Events = null, string? PlayerRace = null, string? RivalRace = null)
{
    public static GameOptions VsRival(Difficulty difficulty) => new(new List<AiSetup> { new(Side.Rival, difficulty) });

    public static GameOptions AiVsAi(Difficulty difficulty = Difficulty.Normal) =>
        new(new List<AiSetup> { new(Side.Player, difficulty), new(Side.Rival, difficulty) });
}

public sealed record AiRange(double Min, double Max);
public sealed record AiThinkHours(double Easy, double Normal);
public sealed record AiEasy(double ExtraRatio, double SlipChance);

public sealed record PersonalityData(
    string Name, double FrigateShare, double Ratio, double ClaimWeight, double HuntWeight, double GuardShare,
    double ProbeEarly, double TakeHeldWeight, int ClaimGroups, List<string> Research, double FlagshipBoldness = 0.3,
    PersonalityProfile? Profile = null, string PullBack = "standard", string FireAt = "split", string Missiles = "mid",
    double Weight = 1, int ExtraFreighters = 0, double BuildEveryDays = 12);

/// <summary>
/// A personality on six axes, 0 to 1: aggression (how readily the main fleet takes the fight to the enemy),
/// expansion (settling), defence (guns on colonies), risk (how close to even odds it still goes in, and how
/// late it turns back), raiding lanes (lying in wait for supply ships) and research (Industry it puts into ideas
/// before the next ship).
/// </summary>
public sealed record PersonalityProfile(double Aggression = 0.5, double Expansion = 0.5, double Defence = 0.5, double Risk = 0.5,
    double Raiding = 0.3, double Research = 0.5)
{
    public IEnumerable<(string Axis, double Value)> Axes() => new[]
    {
        ("aggression", Aggression), ("expansion", Expansion), ("defence", Defence), ("risk", Risk), ("raiding lanes", Raiding), ("research", Research),
    };
}

public sealed record AiData(
    AiThinkHours ThinkHours, AiEasy Easy, AiRange AttackRatio, double NerveDrift, double NerveRange, AiRange FirstProbeDay,
    double ThreatRadiusAu, double DangerRadiusAu, double DangerRatio, double RefitSupplies, double RefitArmour,
    double EnemyShipValue, double EnemyBuildShare, int ClaimGroupShips, double ProbeDays, double ProbeDistanceAu,
    double ChainClaimAu, double GatherDays, double StrikeSuppliesLeft, double HomeShareSeen, double HomeShareUnseen,
    Dictionary<string, PersonalityData> Personalities, AutomationData? Automation = null, double FightRatio = 0.9,
    double DefendOwnRatio = 0.8, double HuntOdds = 1.2, double CampaignAfterDays = 20, int CampaignMinShips = 3,
    double CampaignRelief = 0.5, double CampaignMaxDays = 60, double GunValue = 60, double MirrorShare = 1.15, double FocusDays = 20,
    double RestlessAfterDays = 45, double RestlessDays = 120, double RestlessMax = 0.35);

/// <summary>
/// The player's automation (see Stewards): each area's starting level, and how it learns: after
/// habitChanges hand changes the same way, or acceptsToAuto suggestions taken (dismissesToManual turned
/// down) with none the other way, it suggests a new default; auto actions are judged judgeDays later,
/// and an area whose judged actions go badly more than badShare of the time (at least minJudged) is flagged.
/// </summary>
public sealed record AutomationData(Dictionary<string, string> Defaults, int HabitChanges = 3, int AcceptsToAuto = 5, int DismissesToManual = 5,
    double JudgeDays = 5, double BadShare = 0.5, int MinJudged = 4);

// ---- State (saved with the world) ----------------------------------------------------------------

public enum JobKind { Guard, Claim, Break, Strike, Hunt, Probe, Refit, Ambush, Escort }

/// <summary>What the computer has told one of its fleets to do, and why (shown in the debug view).</summary>
public sealed class FleetJob
{
    public int FleetId { get; set; }
    public JobKind Kind { get; set; }
    public int? BodyId { get; set; }
    public int? EnemyFleetId { get; set; }
    public Vec2 Point { get; set; }
    public string Why { get; set; } = "";
    public double Since { get; set; }

    /// <summary>A claim job that is the main fleet's campaign against an enemy planet (not a small claim group).</summary>
    public bool Campaign { get; set; }

    /// <summary>The supply lane an ambush party lies on; the run an escort guards starts here (BodyId is where it goes).</summary>
    public int? LaneId { get; set; }
    public int? FromBodyId { get; set; }
}

public sealed record Thought(double Hour, string Text);

/// <summary>
/// Leanings a computer side rolls at the start of each game on top of its personality (and that drift a
/// little day to day): missiles against railguns, the research field it favours, escorts and big ships
/// against frigates, and the odds it wants before it fights. The choice log records them, so overnight
/// runs show which ones win.
/// </summary>
public sealed class Leanings
{
    /// <summary>Added to the missile-destroyer share of new hulls (-0.2..0.2).</summary>
    public double Missiles { get; set; }
    public string Field { get; set; } = "";
    /// <summary>Multipliers on the chance of an escort and of a cruiser among new hulls (0.4..1.6).</summary>
    public double Escorts { get; set; } = 1;
    public double BigShips { get; set; } = 1;
    /// <summary>Multiplier on the odds it wants before fighting (0.85 bold .. 1.15 careful).</summary>
    public double Odds { get; set; } = 1;

    public static Leanings Roll(SimRandom rng) => new()
    {
        Missiles = Math.Round(rng.Range(-0.2, 0.2), 3),
        Field = Research.Fields[rng.Range(0, Research.Fields.Length)],
        Escorts = Math.Round(rng.Range(0.4, 1.6), 3),
        BigShips = Math.Round(rng.Range(0.4, 1.6), 3),
        Odds = Math.Round(rng.Range(0.85, 1.15), 3),
    };

    /// <summary>A small daily drift, kept in range.</summary>
    public void Drift(SimRandom rng)
    {
        Missiles = Math.Round(Math.Clamp(Missiles + rng.Range(-0.01, 0.01), -0.2, 0.2), 3);
        Escorts = Math.Round(Math.Clamp(Escorts + rng.Range(-0.02, 0.02), 0.4, 1.6), 3);
        BigShips = Math.Round(Math.Clamp(BigShips + rng.Range(-0.02, 0.02), 0.4, 1.6), 3);
        Odds = Math.Round(Math.Clamp(Odds + rng.Range(-0.005, 0.005), 0.85, 1.15), 3);
    }

    /// <summary>The leanings as choice-log entries, in bands (so games can be grouped).</summary>
    public IEnumerable<string> Bands(PullPreset pull)
    {
        yield return Missiles > 0.07 ? "more missiles" : Missiles < -0.07 ? "more railguns" : "even guns";
        yield return $"{Field} research first";
        yield return Escorts > 1.2 ? "more escorts" : Escorts < 0.8 ? "fewer escorts" : "usual escorts";
        yield return BigShips > 1.2 ? "more big ships" : BigShips < 0.8 ? "fewer big ships" : "usual big ships";
        yield return Odds < 0.95 ? "bold odds" : Odds > 1.05 ? "careful odds" : "usual odds";
        yield return $"pull back {pull}";
    }
}

/// <summary>One computer-run side: its settings, its rolled temperament, its fleets' jobs and what it believes.</summary>
public sealed class MindState
{
    public Side Side { get; set; }
    public Difficulty Difficulty { get; set; }
    public Personality Personality { get; set; }
    public RandomState Random { get; set; } = new(0, 1);

    /// <summary>How much stronger it wants to be before attacking: rolled per game, and drifting a little day to day.</summary>
    public double BaseRatio { get; set; }

    /// <summary>The pull-back preset its fleets use away from home (home guards fight to the last).</summary>
    public PullPreset PullLeaning { get; set; } = PullPreset.Standard;

    /// <summary>This game's leanings (see Leanings).</summary>
    public Leanings Lean { get; set; } = new();

    /// <summary>How far below the best score an option may be and still be picked (0.1: within 10 %).</summary>
    public double Spread { get; set; } = Mind.NormalSpread;
    public double Ratio { get; set; }
    public double FirstProbeDay { get; set; }

    public double NextThinkHour { get; set; }
    public double LastThinkHour { get; set; }
    public int LastNerveDay { get; set; }
    public double LastProbeEndHour { get; set; } = -1e9;

    /// <summary>Until this hour it is pulling fleets home to gather for a strike, so it starts no new claims.</summary>
    public double GatherUntilHour { get; set; } = -1e9;

    /// <summary>When the planets may next have a building ahead of the next ship (see Mind.Build).</summary>
    public double NextBuildHour { get; set; }

    /// <summary>An opportunist's current focus (take, raid, hunt or expand) and when it looks again.</summary>
    public string Focus { get; set; } = "";
    public double FocusUntilHour { get; set; }

    /// <summary>Battles fought so far, and the day of the last one (a long quiet makes the side restless).</summary>
    public int BattlesSeen { get; set; }
    public double LastBattleDay { get; set; }

    /// <summary>What it is mostly trying to do now, and why.</summary>
    public string Goal { get; set; } = "Getting started";
    public List<FleetJob> Jobs { get; set; } = new();
    public List<Thought> Thoughts { get; set; } = new();

    /// <summary>Its guess of the enemy: Industry earned (from the planets anyone can see it holds), and fighting power.</summary>
    public double EnemyStartPower { get; set; }
    public double EnemyEarnedGuess { get; set; }
    public double EnemyPowerGuess { get; set; }
    public double OwnPower { get; set; }

    public FleetJob? JobOf(int fleetId) => Jobs.Find(j => j.FleetId == fleetId);
}

// ---- The thinking -------------------------------------------------------------------------------

/// <summary>
/// The computer rival. It is part of the simulation (so games with it replay and save exactly), and it plays
/// by the same rules as the player: it orders its fleets with the same commands and only uses what its side
/// can see. Every few hours it thinks in three layers: what matters most now (defend, expand, probe, strike),
/// which fleet does which job, and when to pull a fleet back from a fight it expects to lose. Fights
/// themselves are run by the combat code with each fleet's standing orders, as for the player.
/// </summary>
public static class Mind
{
    private static AiData A => GameData.Ai;

    public static PersonalityData DataFor(Personality p) =>
        A.Personalities.TryGetValue(p.ToString().ToLowerInvariant(), out var d)
            ? d : throw new InvalidDataException($"ai.json: no personality '{p}'");

    public static MindState? Of(World w, Side side) => w.Minds.Find(m => m.Side == side);

    /// <summary>Choice spread in normal games (headless runs use a wider one, see GameOptions.ChoiceSpread).</summary>
    public const double NormalSpread = 0.04;

    /// <summary>Sets up the computer-run sides at the start of a game.</summary>
    public static void Setup(World w, GameOptions? options, ulong seed)
    {
        if (options?.Ai == null || !w.Loop) return;
        foreach (var setup in options.Ai.GroupBy(a => a.Side).Select(g => g.First()))
        {
            var rng = new SimRandom(Mix(seed) ^ Mix((ulong)setup.Side + 0xC0FFEEUL), stream: 11 + (ulong)setup.Side);
            var personality = setup.Personality ?? Roll(rng, Races.Of(w, setup.Side));
            var pd = DataFor(personality);
            var ratio = rng.Range(A.AttackRatio.Min, A.AttackRatio.Max) * pd.Ratio
                        + (setup.Difficulty == Difficulty.Easy ? A.Easy.ExtraRatio : 0);
            var mind = new MindState
            {
                Side = setup.Side,
                Difficulty = setup.Difficulty,
                Personality = personality,
                BaseRatio = Math.Round(ratio, 3),
                Ratio = Math.Round(ratio, 3),
                FirstProbeDay = Math.Round(rng.Range(A.FirstProbeDay.Min, A.FirstProbeDay.Max) * pd.ProbeEarly, 1),
                NextThinkHour = rng.Range(0, (int)ThinkHours(setup.Difficulty)),
                // Both sides start alike, so its first guess of the enemy is its own start.
                EnemyStartPower = Stats.Power(w, setup.Side),
            };
            mind.EnemyPowerGuess = mind.EnemyStartPower;
            // Pull-back leaning for this game (a choice the overnight runs compare): mostly Standard. Only home
            // guards fight to the last; fleets out on a job always keep a way out.
            // Usually the personality's own preset; now and then another, so the overnight runs can compare them.
            var lean = rng.NextDouble();
            mind.PullLeaning = lean < 0.7 ? PresetOf(pd.PullBack) : lean < 0.8 ? PullPreset.Cautious : lean < 0.9 ? PullPreset.Standard : PullPreset.FightOn;
            mind.Lean = Leanings.Roll(rng);
            mind.Spread = setup.Difficulty == Difficulty.Easy ? 0 : options.ChoiceSpread ?? NormalSpread;
            Choices.Note(w, setup.Side, "lean", $"personality {personality}");
            Choices.Note(w, setup.Side, "lean", $"race {Races.Of(w, setup.Side).Id}");
            foreach (var band in mind.Lean.Bands(mind.PullLeaning)) Choices.Note(w, setup.Side, "lean", band);
            mind.Random = rng.Save();
            w.Minds.Add(mind);
        }
    }

    /// <summary>Rolls a personality, each as likely as its weight in ai.json.</summary>
    private static Personality Roll(SimRandom rng, RaceData race)
    {
        var all = Enum.GetValues<Personality>();
        // Each race leans to some personalities (a trading Hive is rare: see races.json).
        double Weight(Personality p) => DataFor(p).Weight * race.PersonalityWeight(p);
        var total = all.Sum(Weight);
        var roll = rng.NextDouble() * total;
        foreach (var p in all)
        {
            roll -= Weight(p);
            if (roll < 0) return p;
        }
        return all[^1];
    }

    public static PullPreset PresetOf(string name) => name.ToLowerInvariant() switch
    {
        "cautious" => PullPreset.Cautious,
        "fighton" or "fight on" or "fight-on" => PullPreset.FightOn,
        _ => PullPreset.Standard,
    };

    /// <summary>The personality's profile (defaults when ai.json gives none).</summary>
    public static PersonalityProfile ProfileOf(Personality p) => DataFor(p).Profile ?? new PersonalityProfile();

    /// <summary>Scrambles a seed (SplitMix64), so neighbouring seeds roll unrelated personalities.</summary>
    private static ulong Mix(ulong z)
    {
        z = unchecked(z + 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }

    /// <summary>A plain-text page on each computer-run side: temperament, beliefs, fleet jobs and recent thoughts.</summary>
    public static string Describe(World w)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in w.Minds)
        {
            sb.AppendLine($"# {m.Side}: {m.Personality}, {m.Difficulty}");
            sb.AppendLine();
            sb.AppendLine($"- Profile: {string.Join(", ", ProfileOf(m.Personality).Axes().Select(a => $"{a.Axis} {a.Value:0.00}"))}");
            if (m.Focus != "") sb.AppendLine($"- Focus now: {m.Focus}");
            sb.AppendLine($"- Attack ratio now {m.Ratio:0.00} (rolled {m.BaseRatio:0.00}); first probe from day {m.FirstProbeDay:0}");
            sb.AppendLine($"- Own power {m.OwnPower:0}; guesses the enemy at {m.EnemyPowerGuess:0}");
            sb.AppendLine($"- Goal: {m.Goal}");
            sb.AppendLine();
            sb.AppendLine("## Fleet jobs");
            sb.AppendLine();
            foreach (var j in m.Jobs)
                sb.AppendLine($"- {w.FindFleet(j.FleetId)?.Name ?? $"fleet {j.FleetId}"}: {j.Kind}"
                              + (j.BodyId is int b ? $" {w.FindBody(b)?.Name}" : "") + $" since {GameTime.Format(j.Since)}: {j.Why}");
            sb.AppendLine();
            sb.AppendLine("## Recent thoughts");
            sb.AppendLine();
            foreach (var t in m.Thoughts) sb.AppendLine($"- {GameTime.Format(t.Hour)}: {t.Text}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>A thought noted from outside the mind's own turn (mines, counters): kept for the F7 overlay.</summary>
    public static void Think(World w, MindState m, string text)
    {
        if (m.Thoughts.Any(t => t.Text == text && w.Hour - t.Hour < 48)) return;
        m.Thoughts.Add(new Thought(w.Hour, text));
        if (m.Thoughts.Count > 40) m.Thoughts.RemoveAt(0);
    }

    public static double ThinkHours(Difficulty d) => d == Difficulty.Easy ? A.ThinkHours.Easy : A.ThinkHours.Normal;

    /// <summary>Called at each whole hour.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop || w.Winner != null) return;
        foreach (var mind in w.Minds)
        {
            if (w.Hour < mind.NextThinkHour) continue;
            mind.NextThinkHour = w.Hour + ThinkHours(mind.Difficulty);
            var rng = new SimRandom(mind.Random);
            new Thinker(sim, mind, rng).Run();
            mind.Random = rng.Save();
            mind.LastThinkHour = w.Hour;
        }
    }

    /// <summary>One round of thinking for one side.</summary>
    private sealed class Thinker
    {
        private readonly Simulation _sim;
        private readonly World _w;
        private readonly MindState _m;
        private readonly SimRandom _rng;
        private readonly Side _side, _enemy;
        private readonly PersonalityData _p;
        private PersonalityProfile _profile;
        private readonly Body? _home, _enemyYard;
        private readonly double _au = Physics.MmPerAu;
        private List<Sighting> _known = new();

        public Thinker(Simulation sim, MindState mind, SimRandom rng)
        {
            _sim = sim;
            _w = sim.World;
            _m = mind;
            _rng = rng;
            _side = mind.Side;
            _enemy = Combat.Other(mind.Side);
            _p = DataFor(mind.Personality);
            _profile = ProfileOf(mind.Personality);
            _home = Economy.ShipyardOf(_w, _side);
            _enemyYard = Economy.ShipyardOf(_w, _enemy);
        }

        private bool Easy => _m.Difficulty == Difficulty.Easy;

        /// <summary>Easy rivals now and then skip a check, as a distracted player would.</summary>
        private bool Slip() => Easy && _rng.Chance(A.Easy.SlipChance);

        public void Run()
        {
            if (_home == null) return; // no shipyard: nothing to plan from (the game is about to end)
            Nerve();
            Believe();
            Quiet();
            Focus();
            CleanJobs();
            foreach (var f in Mine().ToList()) CheckFleet(f);
            MergeAtHome();
            var threatened = DefendHome(out var covered);
            if (threatened && covered)
            {
                // Home is safe with what is there: go after the intruders with what can be spared,
                // rather than sitting on a growing pile of ships.
                DefendClaims();
                Hunt(ThreatAgeHours);
            }
            if (!threatened)
            {
                DefendClaims();
                if (!Strike())
                {
                    Campaign();
                    Hunt();
                    Ambush();
                    SupplyCare();
                    Probe();
                    if (_w.Hour >= _m.GatherUntilHour) Expand();
                }
            }
            SupplyRuns();
            Routes();
            Settle();
            Siege();
            Spend();
            FireControlAll();
        }

        /// <summary>Its firing rule and missile timing by personality (raiders finish off and hold missiles close,
        /// turtles go for the largest and launch far, expanders split fire); fleets running from a chaser drop missiles behind them.</summary>
        private void FireControlAll()
        {
            var rule = FireRule(_p.FireAt, null);
            var launch = _p.Missiles.ToLowerInvariant() switch { "close" => MissileLaunch.Close, "far" => MissileLaunch.Far, _ => MissileLaunch.Mid };
            foreach (var f in Mine().ToList())
            {
                if (f.MissileLaunch != launch) Do(new SetFireControlCommand(f.Id, f.LongShots, launch));
                if (f.Retreating && _w.ShipsOf(f).Any(x => x.Missiles > 0 && !x.Out(Module.Missiles))
                    && _w.Fleets.Any(e => e.Side != _side && Vec2.Distance(e.Position, f.Position) <= GameData.Combat.ContactRangeMm * 1.5 && Sensors.Sees(_w, _side, e))
                    && !_w.Dropped.Any(d => d.Side == _side && Vec2.Distance(d.Position, f.Position) <= GameData.Heat.DropSeekMm * 2))
                {
                    Do(new DropMissilesCommand(f.Id, 0.25));
                    Note($"{f.Name} drops missiles behind it as it runs");
                }
            }
            if (_w.Light.On) FlagshipWar(rule);
            else
                foreach (var f in Mine().ToList())
                {
                    var own = FireRule(_p.FireAt, f);
                    if (f.FireAt != own) Do(new SetCoverCommand(f.Id, f.Cover, own));
                }
        }

        /// <summary>
        /// A firing rule from ai.json (weakest, largest, split, engines, flagship, or roll: one of the others
        /// for each battle, worked out from the battle and fleet so it needs no saved state).
        /// </summary>
        private FireAt FireRule(string name, Fleet? f) => name.ToLowerInvariant() switch
        {
            "weakest" => FireAt.Weakest,
            "largest" => FireAt.Largest,
            "engines" => FireAt.Engines,
            "flagship" => FireAt.Flagship,
            "roll" => f != null && _w.ActiveBattle is Battle b && f.FightPower > 0
                ? new[] { FireAt.Nearest, FireAt.Largest, FireAt.Weakest, FireAt.Split, FireAt.Engines }[(int)((ulong)(b.Id * 7 + f.Id) % 5)]
                : f?.FireAt ?? FireAt.Nearest,
            _ => FireAt.Split,
        };

        /// <summary>
        /// Under light lag: the fleet with its flagship screens it (Turtles keep close), and fleets near a
        /// fleet it takes to carry the enemy flagship go for it when they clearly outgun that fleet.
        /// </summary>
        private void FlagshipWar(FireAt rule)
        {
            var flag = Light.Flagship(_w, _side);
            foreach (var f in Mine().ToList())
            {
                // Orders stuck behind a planet for six hours go by drone.
                if (Drones.Blocked(_w, f.Id).Any(t => t.Side == _side && _w.Hour - t.ArriveHour >= 6)
                    && Drones.CanSend(_w, _side, f.Id) == null)
                {
                    Do(new SendDroneCommand(_side, f.Id));
                    Note($"Sends a drone to {f.Name}: a planet blocks its orders");
                }
                var carries = flag != null && flag.FleetId == f.Id;
                var cover = !carries ? CoverStyle.Loose : _m.Personality == Personality.Turtle ? CoverStyle.Close : CoverStyle.Screen;
                var fireAt = rule;
                var near = _known.Where(s => s.Flag is FlagKnown.Suspected or FlagKnown.Confirmed
                        && Vec2.Distance(s.Guess(_w.Hour), f.Position) <= GameData.Combat.ContactRangeMm * 2)
                    .OrderByDescending(s => s.FlagScore).FirstOrDefault();
                if (near != null && War(f) >= SightingPower(near) * (_m.Personality == Personality.Raider ? 1.0 : 1.3))
                    fireAt = FireAt.Flagship;
                if (f.Cover != cover || f.FireAt != fireAt)
                {
                    Do(new SetCoverCommand(f.Id, cover, fireAt));
                    // (A personality that always fires at the flagship has no sighting to name here.)
                    if (fireAt == FireAt.Flagship && f.FireAt != FireAt.Flagship && near != null) Note($"{f.Name} goes for the suspected flagship in {near.Name}");
                }
            }
        }

        // ---- Personality ------------------------------------------------------------------------

        /// <summary>The profile in force: the personality's, with an opportunist's current focus raised.</summary>
        private PersonalityProfile P => _profile;

        /// <summary>
        /// An opportunist looks at the enemy every few weeks and goes for what it neglects: colonies with
        /// nothing guarding them (take), supply ships out on the lanes (raid), small groups out on their own
        /// (hunt), or else empty worlds (expand).
        /// </summary>
        private void Focus()
        {
            if (_m.Personality != Personality.Opportunist) return;
            if (_w.Hour >= _m.FocusUntilHour || _m.Focus == "")
            {
                var colonies = Economy.Claimable(_w).Where(b => b.Owner == _enemy && !b.Shipyard).ToList();
                var bare = colonies.Count(b => EnemyNear(b.Position, A.DangerRadiusAu * _au, 10 * 24) <= 0);
                var convoys = _known.Count(SupplyConvoy);
                var loners = _known.Count(s => !SupplyConvoy(s) && s.Ships <= 2);
                var options = new List<(string Focus, double Score)>
                {
                    ("take", bare * 1.0), ("raid", convoys * 1.5), ("hunt", loners * 1.0), ("expand", SettleTargets().Count * 0.5),
                };
                var pick = Choices.Band(options, _rng, 0.15);
                if (pick != _m.Focus) Note($"Focus now {pick}: the enemy looks weakest there");
                _m.Focus = pick;
                _m.FocusUntilHour = _w.Hour + A.FocusDays * 24;
            }
            var b0 = ProfileOf(_m.Personality);
            _profile = _m.Focus switch
            {
                "take" => b0 with { Aggression = 0.9, Risk = 0.7 },
                "raid" => b0 with { Raiding = 0.9 },
                "hunt" => b0 with { Aggression = 0.8, Risk = 0.8 },
                _ => b0 with { Expansion = 0.9 },
            };
        }

        /// <summary>The odds the main fleet wants before a campaign: bolder with aggression.</summary>
        private double CampaignOdds => (1.25 - 0.5 * P.Aggression) * (1 - Restless);

        /// <summary>
        /// After a long quiet (no battle for RestlessAfterDays) a side settles for worse odds, up to
        /// RestlessMax less, over the next RestlessDays: sitting on a growing fleet wins nothing.
        /// </summary>
        private double Restless => Math.Clamp((_w.Day - _m.LastBattleDay - A.RestlessAfterDays) / A.RestlessDays, 0, 1) * A.RestlessMax * (0.5 + P.Aggression);

        private void Quiet()
        {
            var battles = Stats.Of(_w, _side).Battles;
            if (battles != _m.BattlesSeen || _m.LastBattleDay <= 0) { _m.BattlesSeen = battles; _m.LastBattleDay = Math.Max(_w.Day, _m.FirstProbeDay); }
        }

        /// <summary>How much stronger nearby enemies must be before a fleet out on a job turns back: later with risk.</summary>
        private double TurnBackRatio => A.DangerRatio * (0.8 + 0.4 * P.Risk);

        // ---- What it believes ---------------------------------------------------------------

        private void Nerve()
        {
            if (_w.Day <= _m.LastNerveDay) return;
            _m.LastNerveDay = _w.Day;
            // A random walk around the rolled value, slowly getting bolder as a long game drags on.
            var impatience = Math.Max(0, _w.Day - 90) * 0.002;
            var centre = _m.BaseRatio - impatience;
            var drifted = _m.Ratio + _rng.Range(-A.NerveDrift, A.NerveDrift);
            _m.Ratio = Math.Round(Math.Clamp(drifted, Math.Max(1.0, centre - A.NerveRange), Math.Max(1.0, centre + A.NerveRange)), 3);
            _m.Lean.Drift(_rng);
        }

        /// <summary>The odds it wants before it fights: about even, leaned bolder or more careful this game.</summary>
        private double Fight => A.FightRatio * _m.Lean.Odds;

        /// <summary>The enemy holds most planets and is counting down to a win: time to take risks.</summary>
        private bool Desperate() => Economy.Of(_w, _enemy).MajoritySinceHour != null;

        /// <summary>Ahead on control by a clear margin: time to take the enemy's planets, not just empty ones.</summary>
        private bool Leading() => Planets.Control(_w, _side).Share >= 0.6;

        /// <summary>The attack ratio it uses now: lower when desperate.</summary>
        private double Want => Desperate() ? Math.Min(_m.Ratio, 1.0) : _m.Ratio;

        private void Believe()
        {
            // Sightings it has (what its sensors saw). Recent ones only for planning.
            _known = Sensors.Known(_w, seat: true).Where(s => s.SeenBy == _side && !s.Own && _w.Hour - s.Hour <= 20 * 24).ToList();
            // What the enemy has earned, from the planets anyone can see it holds.
            var hours = Math.Max(0, _w.Hour - _m.LastThinkHour);
            var enemyIncome = Economy.Claimable(_w).Where(b => b.Owner == _enemy).Sum(b => b.Industry);
            _m.EnemyEarnedGuess += enemyIncome * hours / 24;
            var killed = Economy.Of(_w, _side).Totals.ShipsKilled;
            var fromEconomy = _m.EnemyStartPower + (_m.EnemyEarnedGuess + GameData.Economy.StartIndustry) * A.EnemyBuildShare
                              - killed * A.EnemyShipValue;
            // Ships are limited by alloys and electronics more than by Industry, so it also reasons that the
            // enemy has built about what it has itself, scaled by how their holdings compare.
            var ownIncome = Math.Max(1, Economy.Claimable(_w).Where(b => b.Owner == _side).Sum(b => b.Industry));
            var mirror = War(_side) * Math.Clamp(enemyIncome / ownIncome, 0.6, 1.6) * A.MirrorShare;
            var seen = _known.Sum(SightingPower);
            _m.EnemyPowerGuess = Math.Round(Math.Max(seen, Math.Min(fromEconomy, mirror)), 1);
            _m.OwnPower = Math.Round(War(_side), 1);
        }

        /// <summary>Fighting power: armed ships only (freighters, tankers, settlers and troop ships add nothing).</summary>
        private double WarShip(Ship s) => Planner.Warship(s) ? Stats.ShipPower(_w, s) : 0;

        private double War(Fleet f) => _w.ShipsOf(f).Sum(WarShip);

        private double War(Side side) => _w.Ships.Where(s => s.Side == side).Sum(WarShip);

        private double SightingPower(Sighting s) => (s.Ships - UnarmedSeen(s) * 0.8) * A.EnemyShipValue;

        /// <summary>Tankers and freighters identified in a sighting (they carry no guns).</summary>
        private static int UnarmedSeen(Sighting s) =>
            Math.Min(s.Ships, s.Classes.Count(c => GameData.ShipClasses.TryGetValue(c, out var cls) && cls.Cargo > 0));

        /// <summary>A sighting identified as supply ships only: a soft target worth raiding.</summary>
        private bool SupplyConvoy(Sighting s) => s.Ships > 0 && s.Classes.Count >= s.Ships && UnarmedSeen(s) >= s.Ships && !Designer.Wary(_w, _side, s.Guess(_w.Hour));

        /// <summary>Known enemy power near a point (recently seen there, or heading there as last seen).</summary>
        private double EnemyNear(Vec2 at, double radius, double maxAgeHours = 3 * 24) =>
            _known.Where(s => _w.Hour - s.Hour <= maxAgeHours && Vec2.Distance(s.Guess(_w.Hour), at) <= radius).Sum(SightingPower);

        // ---- Fleets ---------------------------------------------------------------------------

        private IEnumerable<Fleet> Mine() => _w.Fleets.Where(f => f.Side == _side && f.ShipIds.Count > 0 && !SupplyOnly(f));

        /// <summary>A fleet of only tankers, freighters and colony ships: it runs supplies or people and is left out of the fighting.</summary>
        private bool SupplyOnly(Fleet f) => f.ShipIds.Count > 0 && _w.ShipsOf(f).All(s => SupplyLines.IsSupplyShip(s) || Planets.IsColonyShip(s));

        /// <summary>
        /// Tankers launched into the home fleet split off and run supplies (Auto) to the largest fleet out on a
        /// job, or to the home fleet when nothing is out; a run whose fleet is gone is pointed at another.
        /// </summary>
        private void SupplyRuns()
        {
            if (Easy) return;
            foreach (var f in Mine().Where(f => AtHome(f) && !Busy(f)).ToList())
            {
                var supply = _w.ShipsOf(f).Where(SupplyLines.IsSupplyShip).ToList();
                if (supply.Count == 0 || supply.Count == f.ShipIds.Count) continue;
                if (SplitOff(f, supply) is Fleet run) Note($"{run.Name} splits off to run supplies");
            }
            var served = Mine().Where(f => _m.JobOf(f.Id) is { Kind: not JobKind.Refit } && !AtHome(f))
                .OrderByDescending(f => f.ShipIds.Count).ThenBy(f => f.Id).FirstOrDefault()
                ?? Mine().Where(AtHome).OrderByDescending(f => f.ShipIds.Count).ThenBy(f => f.Id).FirstOrDefault();
            foreach (var run in _w.Fleets.Where(f => f.Side == _side && f.ShipIds.Count > 0 && _w.ShipsOf(f).All(SupplyLines.IsSupplyShip)
                                                     && !_w.ShipsOf(f).All(SupplyLines.IsFreighter)).ToList())
            {
                var current = run.SupplyRunFor is int id ? _w.FindFleet(id) : null;
                if (current is { ShipIds.Count: > 0 } && current.Side == _side && !SupplyOnly(current)
                    && (served == null || current.Id == served.Id || run.Flight != null)) continue;
                if (served == null || served.Id == current?.Id) continue;
                Do(new SetSupplyRunCommand(_side, run.Id, served.Id));
                Note($"{run.Name} runs supplies to {served.Name}");
            }
        }

        /// <summary>
        /// Freighters are left to Auto transport between planets. Every few days a colony that digs a mineral
        /// with some to spare gets a fixed run carrying it home, so rich deposits feed the home workshops.
        /// </summary>
        private void Routes()
        {
            if (Easy || _home == null) return;
            foreach (var b in _w.Bodies.Where(b => b.Owner == _side && b.Id != _home.Id && b.Board != null).OrderBy(b => b.Id))
            {
                if (_w.FixedRuns.Any(r => r.Side == _side && r.FromBodyId == b.Id)) continue;
                var good = Colonies.Goods.Select(g => g.Id).Where(g => g != "spares" && Colonies.MadePerDay(b, g) > 0.2)
                    .OrderByDescending(g => Colonies.MadePerDay(b, g)).FirstOrDefault();
                if (good == null) continue;
                Do(new FixedRunCommand(_side, b.Id, _home.Id, good, 20));
                Note($"Sets a run carrying {good} from {b.Name} home");
            }
        }

        private bool AtHome(Fleet f) => _home != null && Economy.InOrbitAt(f, _home) && f.Flight == null;

        private bool Busy(Fleet f) => f.Maneuvering || f.Retreating || f.Resupplying || f.JoinFleetId != null || f.Refit != null || OrderOnItsWay(f);

        /// <summary>Under light lag: an order to this fleet has been sent and not yet arrived.</summary>
        private bool OrderOnItsWay(Fleet f) => _w.Light.InTransit.Any(t => t.Command is IFleetCommand fc && fc.FleetId == f.Id);

        private static double ShareOf(IEnumerable<(double Have, double Full)> parts)
        {
            double have = 0, full = 0;
            foreach (var (h, fl) in parts) { have += h; full += fl; }
            return full > 0 ? have / full : 1;
        }

        private double Supplies(Fleet f) =>
            _w.ShipsOf(f).Select(s => Math.Clamp(s.Supplies / Fitting.MaxSupplies(_w, s), 0, 1)).DefaultIfEmpty(1).Min();

        private double Missiles(Fleet f) =>
            ShareOf(_w.ShipsOf(f).Where(s => Fitting.Spec(s).Missiles > 0)
                .Select(s => ((double)s.Missiles, (double)Fitting.Spec(s).Missiles)));

        private bool Ready(Fleet f) => Combat.ArmourShare(_w, f) >= 0.9 && Supplies(f) >= 0.7 && Missiles(f) >= 0.7;

        private bool NeedsRefit(Fleet f) =>
            Supplies(f) < A.RefitSupplies || Combat.ArmourShare(_w, f) < A.RefitArmour || Missiles(f) < 0.3;

        private void CleanJobs()
        {
            _m.Jobs.RemoveAll(j => _w.FindFleet(j.FleetId) is not Fleet f || f.Side != _side);
            foreach (var job in _m.Jobs.ToList())
            {
                var f = _w.FindFleet(job.FleetId)!;
                var body = job.BodyId is int b ? _w.FindBody(b) : null;
                var done = job.Kind switch
                {
                    JobKind.Claim => body == null || body.Owner == _side || (body.Owner == _enemy && body.Shipyard),
                    JobKind.Break => body == null || body.ClaimSide != _enemy,
                    JobKind.Strike => _enemyYard == null,
                    JobKind.Hunt => job.EnemyFleetId is not int e || Sensors.Find(_w, _side, e, seat: true) is not Sighting s || _w.Hour - s.Hour > 48,
                    JobKind.Probe => f.Flight == null && _w.Hour - job.Since > A.ProbeDays * 24,
                    JobKind.Refit => AtHome(f) && Ready(f),
                    JobKind.Ambush => f.ZoneId == null || _w.Hour - job.Since > AmbushDays * 24 || Supplies(f) < 0.4,
                    JobKind.Escort => body == null || body.Owner != _side || _w.Hour - job.Since > EscortDays * 24 || Supplies(f) < 0.4,
                    _ => false,
                };
                // A flight that ended somewhere else (an order it could not finish): think again.
                if (!done && f.Flight == null && !Busy(f) && job.Kind is JobKind.Claim or JobKind.Break or JobKind.Strike
                    && body != null && !Economy.InOrbitAt(f, body))
                    done = true;
                if (!done) continue;
                _m.Jobs.Remove(job);
                if (job.Kind == JobKind.Probe) _m.LastProbeEndHour = _w.Hour;
                if (job.Kind == JobKind.Escort)
                {
                    if (job.FromBodyId is int from && job.BodyId is int to) Do(new SetRouteCommand(_side, from, to, null, null));
                    Do(new SetMissionCommand(f.Id, Mission.None));
                }
                // A claim group that took its planet moves on to the next one nearby, if it is fit for it.
                if (job.Kind == JobKind.Claim && body?.Owner == _side && ChainClaim(f, body)) continue;
                if (job.Kind != JobKind.Refit && !AtHome(f)) GoHome(f, $"{Describe(job)} is over");
            }
        }

        private void CheckFleet(Fleet f)
        {
            if (Busy(f)) return;
            var job = _m.JobOf(f.Id);
            if (job?.Kind == JobKind.Refit) return;
            if (NeedsRefit(f) && !AtHome(f))
            {
                GoHome(f, $"low on supplies, armour or missiles ({Supplies(f):P0} supplies, {Combat.ArmourShare(_w, f):P0} armour)");
                return;
            }
            if (job == null || job.Kind == JobKind.Guard || Slip()) return;
            var mine = War(f);
            // Being chased: run hard from a stronger chaser, turn and fight a weaker one (scouts always run).
            if (ChaserOf(f) is Sighting chaser && f.Chase?.TargetFleetId != chaser.FleetId)
            {
                var theirs = SightingPower(chaser);
                if (theirs > mine * A.DangerRatio || job.Kind == JobKind.Probe || !Combat.HasWorking(_w, f, Module.Railguns))
                {
                    Note($"{f.Name} runs from {chaser.Name}, which is closing on it (about {theirs:0} against its {mine:0})");
                    _m.Jobs.Remove(job);
                    if (job.Kind == JobKind.Probe) _m.LastProbeEndHour = _w.Hour;
                    GoHome(f, "running from a chaser", hard: true);
                    return;
                }
                if (mine >= theirs * Fight)
                {
                    var turn = new FlightRequest(null, chaser.Guess(_w.Hour), Burn(f), ArrivalStyle.Brake, HoldStyle.Drift, _w.Tick,
                        TargetFleetId: chaser.FleetId, Chase: ChaseMode.GunRange);
                    if (Fly(f, turn))
                    {
                        Orders(f, Core.Stance.Engage);
                        Note($"{f.Name} turns to fight {chaser.Name}, which is chasing it ({mine:0} against about {theirs:0})");
                        return;
                    }
                }
            }
            // An enemy in contact at about even odds or better: fight it (scouts don't).
            if (job.Kind != JobKind.Probe && f.Stance != Core.Stance.Engage && Combat.HasWorking(_w, f, Module.Railguns)
                && Combat.NearestEnemyFleet(_w, f, GameData.Combat.ContactRangeMm) is Fleet e && _known.Find(s => s.FleetId == e.Id) is Sighting seen
                && mine >= SightingPower(seen) * Fight)
            {
                Orders(f, Core.Stance.Engage);
                Note($"{f.Name} engages {seen.Name} ({mine:0} against about {SightingPower(seen):0})");
                return;
            }
            // Pull back from a fight it expects to lose.
            // A scout only runs from what comes close to it; others also from what was just seen where they are going.
            var near = job.Kind == JobKind.Probe
                ? EnemyNear(f.Position, 0.5 * A.DangerRadiusAu * _au, 12)
                : Math.Max(EnemyNear(f.Position, A.DangerRadiusAu * _au), EnemyNear(job.Point, A.DangerRadiusAu * _au, 24));
            var limit = job.Kind == JobKind.Strike ? TurnBackRatio * 1.3 : TurnBackRatio;
            if (job.Kind == JobKind.Strike && Desperate()) limit *= 1.5;
            if (near > mine * limit)
            {
                Note($"{f.Name} pulls back from {Describe(job)}: enemy power about {near:0} nearby against its {mine:0}");
                _m.Jobs.Remove(job);
                if (job.Kind == JobKind.Probe) _m.LastProbeEndHour = _w.Hour;
                GoHome(f, "outgunned");
            }
        }

        /// <summary>Fleets sitting at home with no job join into one home fleet.</summary>
        private void MergeAtHome()
        {
            var idle = Mine().Where(f => AtHome(f) && !Busy(f) && _m.JobOf(f.Id) is null or { Kind: JobKind.Guard or JobKind.Refit })
                .OrderByDescending(f => f.ShipIds.Count).ThenBy(f => f.Id).ToList();
            if (idle.Count < 2) return;
            foreach (var other in idle.Skip(1))
                if (Fleets.CanMerge(idle[0], other) == null)
                {
                    Do(new MergeFleetsCommand(idle[0].Id, other.Id));
                    _m.Jobs.RemoveAll(j => j.FleetId == other.Id);
                }
        }

        /// <summary>The home fleet(s): at the shipyard, no outside job, ready to go.</summary>
        private List<Fleet> Pool() => Mine().Where(f => AtHome(f) && !Busy(f) && _m.JobOf(f.Id) is null or { Kind: JobKind.Guard }).ToList();

        // ---- Defence --------------------------------------------------------------------------

        /// <summary>How old a sighting near home still counts as a threat (and as a fleet worth hunting).</summary>
        private const double ThreatAgeHours = 2 * 24;

        /// <summary>Is the enemy near home? covered: what is at home already outguns it.</summary>
        private bool DefendHome(out bool covered)
        {
            covered = false;
            if (Slip()) return false;
            var threat = EnemyNear(_home!.Position, A.ThreatRadiusAu * _au, ThreatAgeHours);
            if (threat <= 0) return false;
            var guard = Mine().Where(f => AtHome(f)).Sum(f => War(f));
            SetGoal("Defend home", $"enemy power about {threat:0} within {A.ThreatRadiusAu} AU of {_home.Name}; {guard:0} at home");
            if (guard >= threat * 1.1) { covered = true; return true; }
            // Call back fleets that are not far away.
            foreach (var f in Mine().Where(f => !AtHome(f) && !Busy(f)))
            {
                var job = _m.JobOf(f.Id);
                if (job?.Kind is JobKind.Refit or JobKind.Strike) continue;
                if (Vec2.Distance(f.Position, _home.Position) > 1.5 * A.ThreatRadiusAu * _au) continue;
                if (job != null) _m.Jobs.Remove(job);
                GoHome(f, $"called home to defend {_home.Name}");
                guard += War(f);
                if (guard >= threat * 1.1) break;
            }
            return true;
        }

        /// <summary>The enemy is claiming one of its planets: send enough to break the claim, if it has it.</summary>
        private void DefendClaims()
        {
            foreach (var b in Economy.Claimable(_w).Where(b => b.ClaimSide == _enemy).OrderByDescending(b => b.Industry).ToList())
            {
                if (_m.Jobs.Any(j => j.Kind == JobKind.Break && j.BodyId == b.Id)) continue;
                var ours = b.Owner == _side;
                if (!ours && (Easy || _rng.NextDouble() > _p.HuntWeight * 0.5)) continue; // a neutral planet: only some care
                if (Slip()) continue;
                var there = Math.Max(EnemyNear(b.Position, 0.05 * _au, 10 * 24), 2 * A.EnemyShipValue);
                // Its own planets are always worth a fight, even at somewhat worse odds; a neutral one needs better.
                var need = there * (ours ? A.DefendOwnRatio * _m.Lean.Odds : Math.Max(Want, 1.3));
                var group = TakeFromPool(need, $"break the enemy claim on {b.Name}");
                if (group == null)
                {
                    Note($"Can't spare enough to stop the enemy claiming {b.Name} (needs about {need:0})");
                    continue;
                }
                Send(group, b, JobKind.Break, 0.8, Core.Stance.Engage,
                    $"enemy is claiming {(ours ? "our" : "neutral")} {b.Name}; about {there:0} enemy power there");
            }
        }

        // ---- Offence and expansion ------------------------------------------------------------

        /// <summary>Go for the enemy shipyard when it is clearly stronger than what it thinks guards it.</summary>
        private bool Strike()
        {
            if (_enemyYard == null || _w.Day < _m.FirstProbeDay + 5) return false;
            if (_m.Jobs.Any(j => j.Kind == JobKind.Strike))
            {
                SetGoal("Strike", $"a fleet is on its way to {_enemyYard.Name}");
                return false; // the rest of the empire carries on
            }
            if (Slip()) return false;
            var pool = Pool().Where(Ready).ToList();
            var poolPower = pool.Sum(f => War(f));
            var seenNearYard = EnemyNear(_enemyYard.Position, A.ThreatRadiusAu * _au, 10 * 24);
            var seenElsewhere = _known.Where(s => _w.Hour - s.Hour <= 5 * 24
                                                  && Vec2.Distance(s.Guess(_w.Hour), _enemyYard.Position) > A.ThreatRadiusAu * _au)
                .Sum(SightingPower);
            // Fresh eyes on the enemy home (a scout) are trusted; otherwise ships it has not seen are probably at home.
            var freshLook = _known.Any(s => _w.Hour - s.Hour <= 5 * 24 && Vec2.Distance(s.Position, _enemyYard.Position) <= A.ThreatRadiusAu * _au)
                            || Mine().Any(f => Vec2.Distance(f.Position, _enemyYard.Position) <= 0.5 * A.ThreatRadiusAu * _au);
            var unseen = Math.Max(0, _m.EnemyPowerGuess - seenElsewhere - seenNearYard);
            var guess = seenNearYard + unseen * (freshLook ? A.HomeShareSeen : A.HomeShareUnseen);
            var allIn = _m.Personality == Personality.Turtle;
            var keep = allIn ? 0.15 : _p.GuardShare;
            var strikePower = poolPower * (1 - keep);
            var want = guess * Want;
            if (strikePower < want || strikePower < 3 * A.EnemyShipValue)
            {
                if (Gather(poolPower, keep, want, guess)) return false;
                if (_w.Day % 5 == 0 && _w.Hour % 24 < ThinkHours(_m.Difficulty))
                    Note($"Not ready to strike {_enemyYard.Name}: {strikePower:0} power free, wants {want:0} ({Want:0.00} x guessed {guess:0})");
                return false;
            }
            var speed = SpeedLeaving(pool.OrderByDescending(f => War(f)).First(), _enemyYard, A.StrikeSuppliesLeft);
            if (speed == null)
            {
                Note($"Can't reach {_enemyYard.Name} with enough supplies left to fight");
                return false;
            }
            var group = TakeFromPool(strikePower, "strike");
            if (group == null) return false;
            SetGoal("Strike", $"{strikePower:0} power against about {guess:0} guessed at {_enemyYard.Name} (wants {Want:0.00}x)");
            Send(group, _enemyYard, JobKind.Strike, speed.Value, Core.Stance.Engage,
                $"{strikePower:0} power against about {guess:0} guessed there (wants {Want:0.00}x)" + (Desperate() ? "; the enemy is close to winning" : ""));
            return true;
        }

        /// <summary>
        /// Takes the fight to the enemy: when the home fleet could take on what guards an enemy planet plus
        /// what might come to its rescue, at about even odds, most of it goes to blockade and take that planet.
        /// The enemy has to come and fight for it, or lose it (one campaign at a time).
        /// </summary>
        private bool Campaign()
        {
            if (_w.Day < _m.FirstProbeDay + A.CampaignAfterDays * (1.5 - P.Aggression) || Slip()) return false;
            if (_m.Jobs.Any(j => j.Campaign))
            {
                if (_m.Jobs.Find(j => j.Campaign) is { BodyId: int id } && _w.FindBody(id) is Body at)
                    SetGoal("Campaign", $"the main fleet is taking {at.Name}");
                return false;
            }
            var pool = Pool().Where(Ready).ToList();
            if (pool.Count == 0) return false;
            var poolPower = pool.Sum(f => War(f));
            var free = poolPower * (1 - _p.GuardShare);
            if (free < A.CampaignMinShips * A.EnemyShipValue || pool.Sum(f => f.ShipIds.Count) < A.CampaignMinShips) return false;
            var seenAnywhere = _known.Where(s => _w.Hour - s.Hour <= 5 * 24).Sum(SightingPower);
            var unseen = Math.Max(0, _m.EnemyPowerGuess - seenAnywhere);
            var from = pool.OrderByDescending(f => War(f)).First();
            Body? best = null;
            double bestScore = 0, bestGuess = 0;
            foreach (var b in Economy.Claimable(_w))
            {
                if (b.Owner != _enemy || b.Shipyard || _m.Jobs.Any(j => j.BodyId == b.Id)) continue;
                var local = EnemyNear(b.Position, A.DangerRadiusAu * _au, 10 * 24);
                var guess = local + Planets.Count(b, Planets.Guns) * A.GunValue + unseen * A.CampaignRelief;
                if (free < guess * Fight * CampaignOdds) continue;
                var days = TravelDays(from, b, ClaimSpeed);
                if (double.IsInfinity(days) || days > A.CampaignMaxDays) continue;
                var score = (b.Industry + 0.05 * b.PopK + 1) * _p.TakeHeldWeight / (1 + days / 10) * _rng.Range(0.85, 1.15);
                if (score > bestScore) { best = b; bestScore = score; bestGuess = guess; }
            }
            if (best == null)
            {
                if (_w.Day % 10 == 0 && _w.Hour % 24 < ThinkHours(_m.Difficulty))
                    Note($"No campaign yet: {free:0} power free at home, about {unseen * A.CampaignRelief:0} enemy power could come to help any of its planets");
                return false;
            }
            var group = TakeFromPool(free, $"campaign against {best.Name}", A.CampaignMinShips);
            if (group == null) return false;
            var power = War(group);
            SetGoal("Campaign", $"{power:0} power to take {best.Name}, against about {bestGuess:0} there or able to come");
            Send(group, best, JobKind.Claim, ClaimSpeed, Core.Stance.Engage,
                $"campaign: take enemy-held {best.Name} ({best.Industry:0} Industry a day); about {bestGuess:0} enemy power there or able to come");
            if (_m.JobOf(group.Id) is FleetJob job) job.Campaign = true;
            return group.Flight != null;
        }

        /// <summary>
        /// Not enough at home to strike, but enough if the claim groups came back: call home the ones that have
        /// not started claiming yet, and start no new claims for a while.
        /// </summary>
        private bool Gather(double poolPower, double keep, double want, double guess)
        {
            if (_w.Hour < _m.GatherUntilHour) { SetGoal("Gather", $"pulling fleets home to strike {_enemyYard!.Name}"); return true; }
            if (Easy) return false; // Easy does not plan that far ahead
            bool Enough(List<Fleet> fleets)
            {
                var total = (poolPower + fleets.Sum(f => War(f))) * (1 - keep);
                return fleets.Count > 0 && total >= want && total >= 3 * A.EnemyShipValue;
            }
            var claiming = Mine().Where(f => !AtHome(f) && !Busy(f) && _m.JobOf(f.Id) is { Kind: JobKind.Claim, Campaign: false } j
                                             && _w.FindBody(j.BodyId ?? -1) is Body b && b.ClaimSide != _side && !NeedsRefit(f)).ToList();
            // Planning the flights home is costly, so first check that even all of them would be enough.
            if (!Enough(claiming)) return false;
            var away = claiming.Where(f => TravelDays(f, _home!, 0.6) <= A.GatherDays).ToList();
            if (!Enough(away)) return false;
            var awayPower = away.Sum(f => War(f));
            _m.GatherUntilHour = _w.Hour + A.GatherDays * 24;
            SetGoal("Gather", $"{poolPower:0} at home and {awayPower:0} out claiming could strike about {guess:0} at {_enemyYard!.Name}");
            foreach (var f in away) GoHome(f, "called home to gather for a strike");
            return true;
        }

        /// <summary>Attack an enemy fleet in sight near its planets when it clearly outguns it.</summary>
        private void Hunt(double maxAgeHours = 6)
        {
            if (Slip()) return;
            var mineBodies = Economy.Claimable(_w).Where(b => b.Owner == _side).Select(b => b.Position).ToList();
            foreach (var s in _known.Where(s => s.Visible || _w.Hour - s.Hour <= maxAgeHours)
                         .OrderBy(s => s.Flag is FlagKnown.Suspected or FlagKnown.Confirmed ? 0 : 1).ThenBy(s => s.Ships).ToList())
            {
                if (_m.Jobs.Any(j => j.EnemyFleetId == s.FleetId)) continue;
                var at = s.Guess(_w.Hour);
                // Raiders go further out for an identified supply convoy: cutting supply lines starves a fleet.
                var reach = SupplyConvoy(s) ? 0.5 + P.Raiding : 0.5;
                if (!mineBodies.Append(_home!.Position).Any(p => Vec2.Distance(p, at) <= reach * _au)) continue;
                // About even odds or a little better (raiders hunt keener); a supply convoy needs only an escort's worth.
                var there = Math.Max(SightingPower(s), EnemyNear(at, A.DangerRadiusAu * _au, 24));
                var need = there * Math.Max(Want / _p.HuntWeight, Fight * A.HuntOdds);
                if (Pool().Where(Ready).Sum(f => War(f)) * (1 - _p.GuardShare) < need) continue;
                var group = TakeFromPool(need, $"hunt {s.Name}", small: SupplyConvoy(s) || P.Raiding >= 0.7);
                if (group == null) continue;
                // Burn hard and brake to gun range when there is fuel to spare (chasing it if it moves); else a plain attack run.
                var request = new FlightRequest(null, at, Burn(group), ArrivalStyle.Brake, HoldStyle.Drift, _w.Tick, TargetFleetId: s.FleetId,
                    Chase: ChaseMode.GunRange);
                if (!Fly(group, request) && !Fly(group, request with { Chase = ChaseMode.None, SpeedFraction = 0.8 }))
                { GoHome(group, "could not reach the enemy fleet"); continue; }
                Assign(group, JobKind.Hunt, null, at, $"{s.Name} ({s.Ships} ships) seen near our planets", s.FleetId);
                Orders(group, Core.Stance.Engage);
            }
        }

        /// <summary>How long a raiding party waits on a lane before coming home.</summary>
        private const double AmbushDays = 20;

        /// <summary>
        /// Raiders lie in wait on the enemy's best-known supply lanes: small groups (a ship or two, the fastest
        /// first) drift cold in a raiding zone on each and go after the cargo that comes by. Three lone raiders
        /// cover three lanes where one big group covers one. The keener the raider, the more parties at once.
        /// </summary>
        private void Ambush()
        {
            if (P.Raiding < 0.5 || Easy || Slip()) return;
            var parties = P.Raiding >= 0.9 ? 3 : P.Raiding >= 0.7 ? 2 : 1;
            if (_m.Jobs.Count(j => j.Kind == JobKind.Ambush) >= parties) return;
            var taken = _m.Jobs.Where(j => j.Kind == JobKind.Ambush).Select(j => j.LaneId).ToHashSet();
            var lane = _w.Lanes.Where(l => l.SeenBy == _side && !taken.Contains(l.Id) && Lanes.Confidence(_w, l) >= 0.5)
                .OrderByDescending(l => Lanes.Confidence(_w, l)).ThenBy(l => l.Id).FirstOrDefault();
            if (lane == null || _w.FindBody(lane.FromBodyId) is not Body a || _w.FindBody(lane.ToBodyId) is not Body b) return;
            var mid = (a.Position + b.Position) * 0.5;
            var guard = EnemyNear(mid, A.DangerRadiusAu * _au, 48);
            var need = Math.Max(guard * A.DangerRatio, A.EnemyShipValue);
            if (Pool().Where(Ready).Sum(f => War(f)) * (1 - _p.GuardShare) < need) return;
            var group = TakeFromPool(need, $"ambush on the {a.Name}–{b.Name} lane", small: true);
            if (group == null) return;
            Do(new HuntCommand(group.Id, a.Id, b.Id));
            Assign(group, JobKind.Ambush, null, mid, $"lie in wait on the {a.Name}–{b.Name} supply lane");
            if (_m.JobOf(group.Id) is FleetJob job) job.LaneId = lane.Id;
            Orders(group, Core.Stance.Engage);
            Note($"{group.Name} ({group.ShipIds.Count} ships) lies in wait on the {a.Name}–{b.Name} supply lane");
        }

        /// <summary>How long an escort stays on a risky run before it comes home.</summary>
        private const double EscortDays = 40;

        /// <summary>
        /// The supply chain, judged as the player's Supply tab judges it (SupplyChain.Problems and RiskOf):
        /// a run at high risk gets a small escort from the home pool (one, or two for a defensive side), and a
        /// planet off home that is blockaded or has enemies near goes into siege mode until they have been
        /// gone for a while.
        /// </summary>
        private void SupplyCare()
        {
            if (Easy || _w.Hour % 24 >= ThinkHours(_m.Difficulty) || Slip()) return;
            var escorts = _m.Jobs.Count(j => j.Kind == JobKind.Escort);
            var most = P.Defence >= 0.5 ? 2 : 1;
            foreach (var p in SupplyChain.Problems(_w, _side, 12).Where(p => p.Kind == ProblemKind.Risk && p.Severity >= (int)RouteRisk.High))
            {
                if (escorts >= most) break;
                if (p.Fixes.Select(f => f.Command).OfType<SetRouteCommand>().FirstOrDefault() is not SetRouteCommand route
                    || _m.Jobs.Any(j => j.Kind == JobKind.Escort && j.FromBodyId == route.FromBodyId && j.BodyId == route.ToBodyId)
                    || _w.FindBody(route.FromBodyId) is not Body a || _w.FindBody(route.ToBodyId) is not Body b) continue;
                var need = Math.Max(A.EnemyShipValue, EnemyNear((a.Position + b.Position) * 0.5, A.DangerRadiusAu * _au, 10 * 24) * Fight);
                if (Pool().Where(Ready).Sum(f => War(f)) * (1 - _p.GuardShare) < need) continue;
                var group = TakeFromPool(need, $"escort on the {a.Name}–{b.Name} run", small: true);
                if (group == null) continue;
                Do(new SetRouteCommand(_side, a.Id, b.Id, group.Id, route.ViaBodyId));
                Assign(group, JobKind.Escort, b.Id, b.Position, $"escort loads on the {a.Name}–{b.Name} run ({p.Cause.TrimEnd('.')})");
                if (_m.JobOf(group.Id) is FleetJob job) job.FromBodyId = a.Id;
                Orders(group, Core.Stance.Engage);
                Note($"{group.Name} escorts the {a.Name}–{b.Name} run: {p.Cause}");
                escorts++;
            }
            foreach (var b in _w.Bodies.Where(b => b.Owner == _side && b.Board != null && !b.Home && !b.Shipyard).OrderBy(b => b.Id))
            {
                var radius = A.DangerRadiusAu * _au;
                var threatened = Economy.Blockaded(_w, b) || EnemyNear(b.Position, radius, 48) > 0;
                if (threatened && !b.SiegeMode)
                {
                    Do(new SetSiegeModeCommand(_side, b.Id, true));
                    Note($"{b.Name} goes into siege mode: enemies near");
                }
                else if (!threatened && b.SiegeMode && EnemyNear(b.Position, radius, 20 * 24) <= 0)
                    Do(new SetSiegeModeCommand(_side, b.Id, false));
            }
        }

        /// <summary>Send a small, quiet scout towards the enemy home to see what it has.</summary>
        private void Probe()
        {
            if (_enemyYard == null || _w.Day < _m.FirstProbeDay || _m.Jobs.Any(j => j.Kind == JobKind.Probe)) return;
            if (_w.Hour - _m.LastProbeEndHour < 10 * 24 || Slip()) return;
            var pool = Pool().Where(Ready).ToList();
            var from = pool.Where(f => f.ShipIds.Count >= 2).OrderByDescending(f => f.ShipIds.Count).FirstOrDefault();
            if (from == null) return;
            var scout = SplitOff(from, 1, preferFast: true);
            if (scout == null) return;
            var toHome = _home!.Position - _enemyYard.Position;
            var point = _enemyYard.Position + toHome * (A.ProbeDistanceAu * _au / Math.Max(toHome.Length, 1e-9));
            var request = new FlightRequest(null, point, 0.5, ArrivalStyle.Brake, HoldStyle.Drift, _w.Tick);
            if (!Fly(scout, request)) { Do(new MergeFleetsCommand(from.Id, scout.Id)); return; }
            Assign(scout, JobKind.Probe, null, point, $"scout {A.ProbeDistanceAu} AU short of {_enemyYard.Name} and watch for {A.ProbeDays:0} days");
            Orders(scout, Core.Stance.Hold);
            Note($"Sends {scout.Name} to look at {_enemyYard.Name}");
        }

        /// <summary>Claim planets: the best open ones first, and enemy-held ones when they look lightly held.</summary>
        private void Expand()
        {
            var totalPower = Mine().Sum(f => War(f));
            for (var tries = 0; tries < 3; tries++)
            {
                if (Slip()) return;
                var pool = Pool().Where(Ready).ToList();
                var free = pool.Sum(f => War(f)) - totalPower * _p.GuardShare;
                if (free < A.EnemyShipValue * 0.6 || pool.Sum(f => f.ShipIds.Count) < 2) return;
                if (_m.Jobs.Count(j => j.Kind == JobKind.Claim && !j.Campaign) >= _p.ClaimGroups) return;
                Body? best = null;
                double bestScore = 0, bestDanger = 0;
                foreach (var b in Economy.Claimable(_w))
                {
                    var score = ClaimScore(b, pool[0], free, out var danger);
                    if (score > bestScore) { best = b; bestScore = score; bestDanger = danger; }
                }
                if (best == null) return;
                var want = Math.Max(bestDanger * Math.Max(Want, A.DangerRatio * 1.05), A.EnemyShipValue * 0.6);
                var group = TakeFromPool(want, $"claim {best.Name}", A.ClaimGroupShips);
                if (group == null) return;
                SetGoal("Expand", $"claiming the best open planets; next {best.Name}");
                Send(group, best, JobKind.Claim, ClaimSpeed, Core.Stance.Engage,
                    $"{(best.Owner == _enemy ? "take enemy-held" : "claim")} {best.Name}: {best.Industry:0} Industry a day"
                    + (bestDanger > 0 ? $", about {bestDanger:0} enemy power seen near" : ""));
            }
        }

        /// <summary>Score of claiming a planet, from where a fleet would set out (higher is better); 0 if it should not try.</summary>
        private double ClaimScore(Body b, Fleet from, double free, out double danger)
        {
            danger = EnemyNear(b.Position, 0.1 * _au, 10 * 24);
            if (b.Owner == _side || b.Shipyard || b.ClaimSide == _side) return 0;
            if (_m.Jobs.Any(j => j.BodyId == b.Id)) return 0;
            if (b.Owner == _enemy) danger = Math.Max(danger, A.EnemyShipValue);
            if (danger * Math.Max(Want, A.DangerRatio * 1.05) > free) return 0;
            var days = TravelDays(from, b, ClaimSpeed);
            if (double.IsInfinity(days)) return 0;
            // Enemy planets are worth more once the game is close (it is near winning, or we are ahead and can press on).
            var held = b.Owner == _enemy ? _p.TakeHeldWeight * (Desperate() || Leading() ? 3 : 0.7) * (b.PopK > 0 ? 1.5 : 1) : 1;
            return b.Industry * _p.ClaimWeight * held / (1 + days / 10) * _rng.Range(0.85, 1.15);
        }

        private const double ClaimSpeed = 0.6;

        /// <summary>The fastest of a few speeds at which the fleet arrives with at least this share of its supplies; null if none.</summary>
        private double? SpeedLeaving(Fleet f, Body b, double suppliesLeft)
        {
            var now = FleetPerformance.Of(_w, f).DeltaVLeft;
            if (now <= 0) return null;
            foreach (var speed in new[] { 0.85, 0.7, 0.55, 0.4 })
            {
                var plan = FlightPlanner.Plan(_w, f, new FlightRequest(b.Id, b.Position, speed, ArrivalStyle.Brake, HoldStyle.Orbit, _w.Tick));
                if (plan.Ok && Supplies(f) * plan.DeltaVLeft / now >= suppliesLeft) return speed;
            }
            return null;
        }

        /// <summary>Days a fleet would take to fly to a body (infinite if it can't).</summary>
        private double TravelDays(Fleet f, Body b, double speed)
        {
            var plan = FlightPlanner.Plan(_w, f, new FlightRequest(b.Id, b.Position, speed, ArrivalStyle.Brake, HoldStyle.Orbit, _w.Tick));
            return plan.Ok && plan.Flight != null ? (plan.Flight.ArriveHour - _w.Hour) / 24 : double.PositiveInfinity;
        }

        /// <summary>Sends a claim group that just took a planet on to the best planet near it. False if there is none.</summary>
        private bool ChainClaim(Fleet f, Body from)
        {
            if (Easy || Busy(f) || NeedsRefit(f) || Supplies(f) < 0.5 || _w.Hour < _m.GatherUntilHour) return false;
            var power = War(f);
            Body? best = null;
            double bestScore = 0;
            foreach (var b in Economy.Claimable(_w))
            {
                if (Vec2.Distance(b.Position, from.Position) > A.ChainClaimAu * _au) continue;
                var score = ClaimScore(b, f, power, out _);
                if (score > bestScore) { best = b; bestScore = score; }
            }
            if (best == null) return false;
            Send(f, best, JobKind.Claim, ClaimSpeed, Core.Stance.Engage, $"moves on from {from.Name} to claim nearby {best.Name}: {best.Industry:0} Industry a day");
            return f.Flight != null;
        }

        // ---- Economy ----------------------------------------------------------------------------

        /// <summary>
        /// What to build next: one tanker once there are a few warships and none to top them up, otherwise
        /// frigates and destroyers in the personality's mix, now and then a cruiser or an escort once researched.
        /// </summary>
        private string PickHull(List<WorkOrder> orders)
        {
            var mine = _w.Ships.Where(s => s.Side == _side).ToList();
            var tankers = mine.Count(s => s.ClassId == "tanker") + orders.Count(o => o.Kind == OrderKind.Ship && o.Key == "tanker");
            var warships = mine.Count(s => !SupplyLines.IsSupplyShip(s) && Fitting.Spec(s).Railguns + Fitting.Spec(s).Missiles > 0);
            if (!Easy && tankers == 0 && warships >= 4 && Economy.CanQueue(_w, _side, OrderKind.Ship, "tanker") == null) return "tanker";
            var freighters = mine.Count(SupplyLines.IsFreighter) + orders.Count(o => o.Kind == OrderKind.Ship && o.Key is "freighter" or "bulk-freighter" or "sail-freighter");
            var colonies = _w.Bodies.Count(b => b.Owner == _side && b.Board != null && !b.Home);
            var hauler = SupplyLines.HaulerClass(_w, _side) is var h && h == "bulk-freighter" ? "freighter" : h;
            if (!Easy && colonies >= 2 && freighters < 1 + colonies / 2 + _p.ExtraFreighters && warships >= 4 && Economy.CanQueue(_w, _side, OrderKind.Ship, hauler) == null) return hauler;
            if (!Easy && _rng.Chance(0.25 * _m.Lean.BigShips) && Economy.CanQueue(_w, _side, OrderKind.Ship, "cruiser") == null) return "cruiser";
            if (!Easy && _rng.Chance(0.15 * _m.Lean.Escorts) && Economy.CanQueue(_w, _side, OrderKind.Ship, "escort") == null) return "escort";
            var frigateShare = Easy ? 0.6 : Math.Clamp(_p.FrigateShare - _m.Lean.Missiles, 0.1, 0.9);
            return _rng.Chance(frigateShare) ? "gun-frigate" : "missile-destroyer";
        }

        private void Spend()
        {
            var orders = _w.Orders.Where(o => o.Side == _side).ToList();
            if (orders.Count(o => o.Kind == OrderKind.Ship) < 2)
            {
                var cls = PickHull(orders);
                if (Economy.CanQueue(_w, _side, OrderKind.Ship, cls) == null)
                    Do(new QueueOrderCommand(_side, OrderKind.Ship, cls));
            }
            // Keep every ready team busy: one idea queued per team.
            var teams = Economy.Of(_w, _side).Teams;
            if (orders.Count(o => o.Kind == OrderKind.Research) < Math.Max(1, teams.Count(t => Research.Ready(_w, t))))
            {
                var open = _p.Research.Where(r => GameData.ResearchById.ContainsKey(r) && Economy.CanQueue(_w, _side, OrderKind.Research, r) == null
                                                  && (_w.Light.On || !Tech.LagOnly(r))).ToList();
                // Materials first: with electronics short, the way to an electronics plant jumps the queue.
                if (Planner.NextIdea(_w, _side, null) is string urgent && Planner.HelpsMaterials(_w, _side, urgent)) open.Insert(0, urgent);
                if (open.Count == 0 && Planner.NextIdea(_w, _side, null) is string idea) open.Add(idea);
                if (open.Count > 0)
                {
                    // Near the top of its list, leaning to its favoured field: one of those close to the best.
                    var pick = Easy ? open[_rng.Range(0, open.Count)]
                        : Planner.HelpsMaterials(_w, _side, open[0]) ? open[0]
                        : Choices.Band(open.Take(6).Select((r, i) => (r, Score: 1 - 0.03 * i + (GameData.Research(r).Field == _m.Lean.Field ? 0.04 : 0))).ToList(), _rng, _m.Spread);
                    // Research costs no Industry (only scientist time), so it never holds up the shipyard.
                    Do(new QueueOrderCommand(_side, OrderKind.Research, pick));
                }
            }
            if (!Easy) Stations(orders);
            if (!Easy) RefitAtHome(orders);
            // Munitions plants: eager rivals tool up for the latest at once, the rest when the enemy pulls ahead.
            if (!Easy) Munitions.Steward(_sim, _side, (area, key, text, act, label, bodyId, fleetId, advice) =>
            {
                if (!Do(act)) return false;
                Note(text);
                return true;
            }, Munitions.Eager(_p));
            if (!Easy) Teams(orders);
            Build(orders);
        }

        /// <summary>Fleets idle at home refit to the current designs when a bay is free and Industry spare after the next ship.</summary>
        private void RefitAtHome(List<WorkOrder> orders)
        {
            var reserve = orders.FirstOrDefault(o => o.Kind == OrderKind.Ship && !o.Paid) is WorkOrder next ? Economy.CostOf(next) : 0;
            foreach (var f in Mine().Where(f => AtHome(f) && !Busy(f)).ToList())
                if (Refits.WorthIt(_w, f, reserve))
                {
                    Do(new SendForRefitCommand(_side, f.Id));
                    Note($"{f.Name} refits to the current designs at {_home!.Name}");
                    return;
                }
        }

        // ---- Planets and people -----------------------------------------------------------------

        /// <summary>Industry left after the next ship waiting to start.</summary>
        private double Spare(List<WorkOrder> orders) =>
            Economy.Of(_w, _side).Industry - (orders.FirstOrDefault(o => o.Kind == OrderKind.Ship && !o.Paid) is WorkOrder next ? Economy.CostOf(next) : 0);

        /// <summary>
        /// Planet buildings, one under way per planet: food when it runs short, power before anything that
        /// needs it, a workshop when spare parts run short, then mines on free deposits, habitats when a
        /// planet fills up, a storehouse, guns and a point defence grid at home, and a research lab or two,
        /// with Industry to spare after the next ship. Each goes to its best spot on the board.
        /// </summary>
        private void Build(List<WorkOrder> orders)
        {
            var mine = _w.Bodies.Where(b => b.Owner == _side && b.PopK > 0 && b.Board != null && b.Building == null)
                .OrderByDescending(b => b.Home).ThenByDescending(b => b.PopK).ThenBy(b => b.Id).ToList();
            if (mine.Count == 0) return;
            var linked = _w.Bodies.Where(b => b.Owner == _side && !Economy.Blockaded(_w, b)).ToList();
            var hungry = linked.Sum(Planets.FoodMadePerDay) < Races.FoodWanted(_w, _side, linked);
            var sparesShort = linked.Sum(b => Colonies.MadePerDay(b, Colonies.Spares)) < linked.Sum(b => Colonies.UsePerDay(b, Colonies.Spares)) * 1.1;
            var industry = Economy.Of(_w, _side).Industry;
            if (industry < Colonies.Kinds.Min(k => k.Cost)) return; // nothing is affordable: skip the board searches
            // Urgent buildings (food, power, the minerals that keep power and spare parts coming) go ahead of
            // the next ship; others wait for Industry to spare after it, or for their turn every so often.
            var turn = _w.Hour >= _m.NextBuildHour;
            foreach (var b in mine)
            {
                var want = Planner.PlanetWant(_w, _side, b, hungry, sparesShort, Easy) ?? Fortify(b);
                if (want is not var (kind, x, y, urgent) || Planets.BuildingKind(kind) is not BuildingData data) continue;
                var budget = urgent || turn ? industry : Spare(orders) - (Easy ? 120 : 60);
                if (budget < data.Cost || Planets.CanBuild(_w, _side, b.Id, kind, x, y) != null) continue;
                Do(new BuildOnPlanetCommand(_side, b.Id, kind, x, y));
                Note($"Builds a {data.Name.ToLowerInvariant()} at {b.Name}");
                if (!urgent) _m.NextBuildHour = _w.Hour + (Easy ? 30 : _p.BuildEveryDays) * 24;
                return;
            }
        }

        /// <summary>A defensive personality (defence 0.7 or more) puts a rail battery on each colony with people, then a point defence grid.</summary>
        private (string, int?, int?, bool)? Fortify(Body b)
        {
            if (P.Defence < 0.7 || b.Home || b.PopK < 10) return null;
            foreach (var kind in new[] { Planets.Guns, "pd-grid", StrikeCraft.Base })
                if (Planets.Count(b, kind) == 0 && Planets.BuildingKind(kind) is BuildingData d && Colonies.BestSpot(b, d) != null
                    && (d.Needs == null || Tech.Has(_w, _side, d.Needs))
                    && b.PowerMade - b.PowerWanted >= d.Power)
                    return (kind, null, null, false);
            return null;
        }

        /// <summary>
        /// Fleets holding orbit over an enemy planet with people knock out its farms (that starves it into
        /// surrender sooner, without the grudge that bombing its people would leave).
        /// </summary>
        private void Siege()
        {
            foreach (var f in Mine().Where(f => _m.JobOf(f.Id) is { Kind: JobKind.Claim or JobKind.Strike }).ToList())
            {
                if (f.OrbitBodyId is not int id || _w.FindBody(id) is not Body b || b.Owner != _enemy || !Economy.InOrbitAt(f, b)) continue;
                var want = b.PopK > 0 && Planets.Count(b, Planets.Farm) > 0 ? BombTarget.Farms
                    : Planets.Count(b, Planets.Guns) > 0 ? BombTarget.Guns : BombTarget.None;
                if (f.Bombing != want && !OrderOnItsWay(f)) Do(new SetBombingCommand(f.Id, want));
            }
        }

        /// <summary>Empty worlds worth settling, safe from known enemies and not already being settled.</summary>
        private List<Body> SettleTargets() => _w.Bodies
            .Where(b => b.Owner == null && b.PopK <= 0 && Planets.Settleable(b) && b.Industry > 0
                        && !Planets.SettlersBound(_w, _side, b.Id)
                        && EnemyNear(b.Position, 0.1 * _au, 10 * 24) <= 0)
            .ToList();

        /// <summary>
        /// Settling: one or two settle orders at a time while there are empty worlds to settle, nearest
        /// and richest first, or a thin colony of its own to top up.
        /// </summary>
        private void Settle()
        {
            var orders = _w.Orders.Where(o => o.Side == _side && o.Kind == OrderKind.Ship).ToList();
            if (_home != null)
            {
                var targets = SettleTargets();
                var thin = _w.Bodies.Where(b => b.Owner == _side && !b.Home && Planets.Settleable(b) && b.PopK < Planets.Cap(b) * 0.5
                                                && !Planets.SettlersBound(_w, _side, b.Id)).ToList();
                var bound = orders.Count(o => o.SettleBodyId != null) + _w.Fleets.Count(f => f.Side == _side && f.PeopleRun != null);
                var to = targets.OrderBy(b => Vec2.Distance(b.Position, _home.Position) / (1 + b.Industry)).ThenBy(b => b.Id).FirstOrDefault()
                         ?? thin.OrderBy(b => Vec2.Distance(b.Position, _home.Position)).ThenBy(b => b.Id).FirstOrDefault();
                if (to != null && bound < (Easy ? 1 : 1 + (int)Math.Round(P.Expansion * 2)) && orders.Count < 2 && Planets.CanSettle(_w, _side, to.Id) == null)
                {
                    Do(new SettleCommand(_side, to.Id));
                    Note($"Sends settlers to {to.Name}");
                }
            }
            Troops(orders);
        }

        /// <summary>
        /// A claim group blockading an enemy planet that won't give up soon (it is content, or bears a grudge)
        /// gets a troop ship sent out to it; once enough troops are aboard they land.
        /// </summary>
        private void Troops(List<WorkOrder> orders)
        {
            if (Easy) return;
            foreach (var f in Mine().Where(f => _m.JobOf(f.Id) is { Kind: JobKind.Claim } && !f.Retreating && !f.Resupplying
                                                && f.JoinFleetId == null && f.Refit == null && !OrderOnItsWay(f)).ToList())
            {
                // In orbit at the planet, or still on the way to it: troops are ordered as the fleet sets out, so they
                // arrive near the start of the blockade (overnight runs showed troops ordered once in orbit landing after
                // the surrender, or never).
                var inOrbit = !f.Maneuvering && f.Flight == null && f.OrbitBodyId is int id && _w.FindBody(id) is Body there && Economy.InOrbitAt(f, there);
                var targetId = inOrbit ? f.OrbitBodyId : f.Flight != null && !f.Maneuvering ? _m.JobOf(f.Id)!.BodyId : null;
                if (targetId is not int tid || _w.FindBody(tid) is not Body b || b.Owner != _enemy || b.PopK <= 0) continue;
                // How long the blockade will take on its own: happiness falls BlockadedHappinessPerDay a day to the
                // surrender line (never, with a grudge). Troops are sent when that is longer than a week or two.
                var raider = _m.Personality == Personality.Raider;
                var daysLeft = b.Grudge == _side ? double.PositiveInfinity
                    : Math.Max(0, b.Happiness - Planets.SurrenderLine(_w, _side)) / Math.Max(0.1, Planets.D.BlockadedHappinessPerDay);
                var stubborn = daysLeft > (raider ? 7 : 14);
                var tired = b.BlockadeHours > (raider ? 7 : 21) * 24 && daysLeft > 3;
                if (!stubborn && !tired) continue;
                var troops = Planets.TroopsAboard(_w, f);
                var garrison = Planets.GarrisonOf(b);
                if (inOrbit)
                {
                    // A raider goes in hard and early; the others land with a safe margin on Auto's plan.
                    if (troops >= garrison * (raider ? 1.25 : 1.5) && Planets.CanLand(_w, f, b) == null)
                    {
                        var plan = raider ? GroundPlan.Blitz : Ground.AutoPlan(_w, _side, b, troops);
                        Do(new LandTroopsCommand(f.Id, b.Id, plan));
                        Note($"{f.Name} lands troops on {b.Name} ({Ground.PlanName(plan)})");
                        continue;
                    }
                    if (f.OrbitHeight != OrbitHeight.Low && troops > 0) Do(new SetOrbitHeightCommand(f.Id, OrbitHeight.Low));
                }
                var coming = orders.Count(o => o.Key == "troop-ship" && o.JoinFleetId == f.Id)
                             + _w.Fleets.Count(o => o.Side == _side && o.JoinFleetId == f.Id && _w.ShipsOf(o).Any(Planets.IsTroopShip));
                var aboardSoon = troops + coming * GameData.ShipClass("troop-ship").Cargo;
                // Troops go to the front of the yard queue: a siege won't wait behind the next warship.
                if (aboardSoon < garrison * 1.5 && orders.Count(o => o.Key == "troop-ship") < 2
                    && Economy.CanQueue(_w, _side, OrderKind.Ship, "troop-ship") == null)
                {
                    Do(new QueueOrderCommand(_side, OrderKind.Ship, "troop-ship", f.Id, Front: true));
                    Note($"Builds a troop ship for {f.Name} at {b.Name}");
                    return;
                }
            }
        }

        /// <summary>
        /// With Industry to spare after the next ship, a second team is founded in the field the
        /// personality's research list leans on most; it keeps that field.
        /// </summary>
        private void Teams(List<WorkOrder> orders)
        {
            var state = Economy.Of(_w, _side);
            if (_w.Day < 15 || state.Teams.Count >= 2) return;
            var shipWaiting = orders.FirstOrDefault(o => o.Kind == OrderKind.Ship && !o.Paid) is WorkOrder next ? Economy.CostOf(next) : 0;
            if (state.Industry < Research.T.FoundCost + shipWaiting + 60) return;
            var field = _p.Research.Where(GameData.ResearchById.ContainsKey).GroupBy(r => GameData.Research(r).Field)
                .OrderByDescending(g => g.Count()).ThenBy(g => Research.FieldIndex(g.Key)).Select(g => g.Key).FirstOrDefault() ?? "weapons";
            if (Research.CanFound(_w, _side, field) == null) Do(new FoundTeamCommand(_side, field));
        }

        /// <summary>
        /// With Industry to spare after the next ship: a sensor station on the held planet nearest the
        /// enemy that lacks one; under light lag, a relay on the held body farthest from home.
        /// </summary>
        private void Stations(List<WorkOrder> orders)
        {
            var shipWaiting = orders.FirstOrDefault(o => o.Kind == OrderKind.Ship && !o.Paid) is WorkOrder next ? Economy.CostOf(next) : 0;
            var spare = Economy.Of(_w, _side).Industry - shipWaiting;
            var held = _w.Bodies.Where(b => b.Owner == _side && !b.Shipyard).ToList();
            var toward = _enemyYard?.Position ?? Vec2.Zero;
            var sensor = Economy.StationKind(Economy.SensorStation);
            if (sensor != null && spare >= sensor.Cost * 2
                && held.Where(b => b.Industry >= 2 && !Economy.HasStation(_w, _side, b.Id, Economy.SensorStation))
                    .OrderBy(b => Vec2.Distance(b.Position, toward)).ThenBy(b => b.Id).FirstOrDefault() is Body watch)
            {
                Do(new BuildStationCommand(_side, watch.Id, Economy.SensorStation));
                Note($"Builds a sensor station at {watch.Name}");
                return;
            }
            // Helium-3: a skimmer over a gas giant it holds, once its engines or reactors want helium-3.
            var skimmer = Economy.StationKind(Works.Skimmer);
            if (skimmer != null && spare >= skimmer.Cost * 1.5 && (skimmer.Needs == null || Tech.Has(_w, _side, skimmer.Needs))
                && (Planner.FuelsWanted(_w, _side).Contains("helium-3") || Tech.Has(_w, _side, "helium-3-reactors"))
                && !_w.Stations.Any(st => st.Side == _side && st.Kind == Works.Skimmer)
                && _w.Bodies.Where(b => b.Owner == _side && skimmer.OnTypes?.Contains(b.Type) == true).OrderBy(b => b.Id).FirstOrDefault() is Body giant)
            {
                Do(new BuildStationCommand(_side, giant.Id, Works.Skimmer));
                Note($"Builds a helium-3 skimmer at {giant.Name}");
                return;
            }
            if (_home != null && Transport(spare)) return;
            var relay = Economy.StationKind(Economy.Relay);
            if (!_w.Light.On || relay == null || spare < relay.Cost * 2 || _home == null) return;
            if (_w.Stations.Any(st => st.Side == _side && st.Kind == Economy.Relay)) return;
            if (held.OrderByDescending(b => Vec2.Distance(b.Position, _home.Position)).ThenBy(b => b.Id).FirstOrDefault() is Body far
                && Vec2.Distance(far.Position, _home.Position) > 0.5 * _au)
            {
                Do(new BuildStationCommand(_side, far.Id, Economy.Relay));
                Note($"Builds a relay at {far.Name}");
            }
        }

        /// <summary>
        /// Transport and orbit, with Industry to spare: the best lift-to-orbit station it has researched over
        /// home; a laser station at home once it has sail ships; and up to three sensor satellites (home's
        /// leading and trailing points, then over the enemy's yard). One thing a think. True when it did one.
        /// </summary>
        private bool Transport(double spare)
        {
            var home = _home!;
            foreach (var kind in new[] { "space-elevator", "skyhook" })
            {
                if (Economy.StationKind(kind) is not StationData lift || spare < lift.Cost * 1.5) continue;
                if (Economy.HasStation(_w, _side, home.Id, kind)) break;
                if (Economy.CanBuildStation(_w, _side, home.Id, kind) != null) continue;
                Do(new BuildStationCommand(_side, home.Id, kind));
                Note($"Builds a {lift.Name.ToLowerInvariant()} at {home.Name}: cheaper lift to orbit");
                return true;
            }
            if (Economy.StationKind(Sails.LaserStation) is StationData laser && spare >= laser.Cost * 1.5
                && _w.Ships.Any(s => s.Side == _side && Sails.Is(s)) && !_w.Stations.Any(st => st.Side == _side && st.Kind == Sails.LaserStation)
                && Economy.CanBuildStation(_w, _side, home.Id, Sails.LaserStation) == null)
            {
                Do(new BuildStationCommand(_side, home.Id, Sails.LaserStation));
                Note($"Builds a laser station at {home.Name} for its sail ships");
                return true;
            }
            const string sensor = "sensor-sat";
            if (Satellites.KindOf(sensor) is not SatelliteKind sat || spare < sat.Cost * 3) return false;
            // Losses teach it: after two it stops spying over the enemy's yard, after six it stops launching.
            var lost = Economy.Of(_w, _side).Totals.SatellitesLost;
            if (lost >= 6) return false;
            var spots = new List<(int Body, SatSpot Spot)> { (home.Id, SatSpot.Leading), (home.Id, SatSpot.Trailing) };
            if (_enemyYard != null && lost < 2) spots.Add((_enemyYard.Id, SatSpot.Orbit));
            foreach (var (body, spot) in spots)
            {
                // A spot where one was lately shot down waits until the loss is old news.
                if (_w.Satellites.Any(x => x.Side == _side && x.BodyId == body && x.Spot == spot) || Satellites.CanLaunch(_w, _side, sensor, body, spot) != null) continue;
                Do(new LaunchSatelliteCommand(_side, sensor, body, spot));
                Note($"Launches a sensor satellite to {Satellites.SpotName(_w.FindBody(body)!.Name, spot)}");
                return true;
            }
            return false;
        }

        // ---- Orders -----------------------------------------------------------------------------

        /// <summary>Takes ships from the home fleet(s) worth at least this much power (or this many ships) as a new fleet.</summary>
        /// <param name="small">Raiding parties: the smallest, fastest warships first, so the group stays small and quiet.</param>
        private Fleet? TakeFromPool(double power, string what, int minShips = 1, bool small = false)
        {
            var pool = Pool().Where(Ready).OrderByDescending(f => War(f)).ToList();
            var from = pool.FirstOrDefault();
            if (from == null) return null;
            // Under light lag the flagship stays at home unless every ship is needed, or the personality
            // is bold enough to lead from the front this time (its flag bridge makes the group fight better).
            var flag = _w.Light.On && !_rng.Chance(_p.FlagshipBoldness) ? Economy.Of(_w, _side).FlagshipId : null;
            // Under decapitation a spare destroyer goes out with groups, so one blow can't take both.
            var spareFirst = _w.Light.Decapitation && Economy.Of(_w, _side).FlagshipId is int f0 && _w.FindShip(f0)?.FleetId == from.Id
                             && _w.ShipsOf(from).Count(Light.CommandCapable) >= 2;
            var ships = _w.ShipsOf(from).OrderBy(s => s.Id == flag ? 1 : 0)
                .ThenBy(s => spareFirst && Light.CommandCapable(s) && s.Id != Economy.Of(_w, _side).FlagshipId ? 0 : 1)
                .ThenBy(s => small ? (Planner.Warship(s) ? 0 : 1) : 0)
                .ThenBy(s => small ? WarShip(s) : -WarShip(s)).ThenByDescending(s => small ? s.MaxSpeed : 0).ToList();
            var picked = new List<Ship>();
            double sum = 0;
            foreach (var s in ships)
            {
                if (sum >= power && picked.Count >= minShips) break;
                picked.Add(s);
                sum += WarShip(s);
            }
            if (sum < power * 0.999 || picked.Count < minShips) return null;
            if (picked.Count == ships.Count)
            {
                _m.Jobs.RemoveAll(j => j.FleetId == from.Id);
                return from;
            }
            return SplitOff(from, picked);
        }

        private Fleet? SplitOff(Fleet from, int count, bool preferFast)
        {
            var ships = _w.ShipsOf(from).OrderByDescending(s => preferFast ? s.MaxSpeed : 0).ThenBy(s => s.Id).Take(count).ToList();
            return ships.Count == count ? SplitOff(from, ships) : null;
        }

        private Fleet? SplitOff(Fleet from, List<Ship> ships)
        {
            var before = _w.Fleets.Count;
            Do(new SplitFleetCommand(from.Id, ships.Select(s => s.Id).ToList()));
            return _w.Fleets.Count > before ? _w.Fleets[^1] : null;
        }

        private void Send(Fleet f, Body target, JobKind kind, double speed, Stance stance, string why)
        {
            var request = new FlightRequest(target.Id, target.Position, speed, ArrivalStyle.Brake, HoldStyle.Orbit, _w.Tick);
            if (!Fly(f, request))
            {
                Note($"{f.Name} cannot fly to {target.Name}; stays home");
                return;
            }
            Assign(f, kind, target.Id, target.Position, why);
            Orders(f, stance);
            Note($"{f.Name} ({f.ShipIds.Count} ships): {kind} {target.Name}: {why}");
        }

        /// <summary>An enemy fleet in sight whose track is closing on this fleet, if any (the nearest).</summary>
        private Sighting? ChaserOf(Fleet f) =>
            _known.Where(s => s.Visible && Sensors.HeadingFor(s, f, _w.Hour) is double eta && eta <= 48)
                .OrderBy(s => Vec2.Distance(s.Guess(_w.Hour), f.Position)).FirstOrDefault();

        /// <summary>How hard to burn towards a fight: flat out with fuel to spare, gentler when supplies are low.</summary>
        private double Burn(Fleet f) => Supplies(f) >= 0.7 ? 1.0 : Supplies(f) >= 0.45 ? 0.7 : 0.5;

        private void GoHome(Fleet f, string why, bool hard = false)
        {
            if (_home == null || Busy(f)) return;
            if (f.ZoneId is int zone)
            {
                Do(new SetZoneFleetCommand(f.Id, null));
                if (!_w.Fleets.Any(o => o.ZoneId == zone)) Do(new DeleteZoneCommand(_side, zone));
            }
            _m.Jobs.RemoveAll(j => j.FleetId == f.Id);
            if (AtHome(f))
            {
                Assign(f, JobKind.Refit, _home.Id, _home.Position, why);
                return;
            }
            var request = new FlightRequest(_home.Id, _home.Position, hard ? 1.0 : 0.6, ArrivalStyle.Brake, HoldStyle.Orbit, _w.Tick);
            if (!Fly(f, request)) request = request with { SpeedFraction = FlightPlanner.MinSpeedFraction };
            if (f.Flight?.TargetBodyId != _home.Id && !Fly(f, request))
            {
                Note($"{f.Name} cannot get home ({why})");
                return;
            }
            Assign(f, JobKind.Refit, _home.Id, _home.Position, why);
        }

        private bool Fly(Fleet f, FlightRequest request)
        {
            // A swing past a big planet, when one beats the direct route (the planner only offers those).
            if (request is { TargetBodyId: not null, TargetFleetId: null, Waypoints: null, Route: RouteKind.Direct }
                && FlightPlanner.BestSlingshot(_w, f, request with { Route = RouteKind.Slingshot }) is ({ Ok: true, Flight: not null } sling, Body via))
            {
                Note($"{f.Name} swings past {via.Name}");
                return Do(new PlanFlightCommand(f.Id, request with { Route = RouteKind.Slingshot, FlybyBodyId = via.Id, DepartTick = _w.Tick })) || f.Flight != null;
            }
            if (!FlightPlanner.Plan(_w, f, request).Ok) return false;
            request = SlowThroughRocks(f, request);
            return Do(new PlanFlightCommand(f.Id, request)) || f.Flight != null;
        }

        /// <summary>
        /// Rocks on the way (belts, rings, battle debris): if the worst-hit ship would lose more than
        /// SlowAbove of its hull, fly slower (hits hurt less at lower speed), down to a third of the asked speed.
        /// The same estimate the player's route planner shows.
        /// </summary>
        private FlightRequest SlowThroughRocks(Fleet f, FlightRequest request)
        {
            if (!Hazards.On(_w)) return request;
            var best = request;
            for (var i = 0; i < 4; i++)
            {
                var plan = FlightPlanner.Plan(_w, f, best);
                if (plan is not { Ok: true, Flight: Flight flight } || Hazards.Estimate(_w, f, flight).HullShare <= Hazards.D.SlowAbove) break;
                var slower = best with { SpeedFraction = Math.Max(FlightPlanner.MinSpeedFraction, best.SpeedFraction * 0.7) };
                if (slower.SpeedFraction < request.SpeedFraction / 3 - 1e-9 || slower.SpeedFraction >= best.SpeedFraction - 1e-9 || !FlightPlanner.Plan(_w, f, slower).Ok) break;
                best = slower;
            }
            if (best.SpeedFraction < request.SpeedFraction - 1e-9) Note($"{f.Name} slows to {best.SpeedFraction:P0} through rocks");
            return best;
        }

        private void Orders(Fleet f, Stance stance)
        {
            if (f.Stance != stance || !f.RetreatWhenDamaged) Do(new SetStanceCommand(f.Id, stance, true));
            // By job, not by where the fleet is now (a group just sent out is still in orbit at home):
            // home guards fight to the last, a group breaking a claim on its own planet fights on, the rest by leaning.
            var job = _m.JobOf(f.Id);
            var preset = job is null or { Kind: JobKind.Guard } && AtHome(f) ? PullPreset.ToTheLast
                : job is { Kind: JobKind.Break } && job.BodyId is int b && _w.FindBody(b)?.Owner == _side && _m.PullLeaning != PullPreset.FightOn ? PullPreset.FightOn
                : _m.PullLeaning;
            if (f.Rule.Preset != preset) Do(new SetPullRuleCommand(f.Id, preset));
        }

        private void Assign(Fleet f, JobKind kind, int? bodyId, Vec2 point, string why, int? enemyFleetId = null)
        {
            _m.Jobs.RemoveAll(j => j.FleetId == f.Id);
            _m.Jobs.Add(new FleetJob { FleetId = f.Id, Kind = kind, BodyId = bodyId, EnemyFleetId = enemyFleetId, Point = point, Why = why, Since = _w.Hour });
        }

        /// <summary>
        /// Gives an order. The mind runs inside the step, so a replay runs it again and needs no record.
        /// Under light lag an order to somewhere far from the flagship goes out at light speed, like the
        /// player's (returns true: on its way); splitting and merging fleets is done on the spot. The same
        /// kind of order to the same fleet is not sent twice while one is on its way.
        /// </summary>
        private bool Do(GameCommand command)
        {
            var c = command with { Source = $"ai:{_side.ToString().ToLowerInvariant()}" };
            if (c is not SplitFleetCommand and not MergeFleetsCommand && _w.Light.On
                && c.Destination(_w) is (Side, Vec2 at) && Light.DelayHours(_w, _side, at) > 0)
            {
                var fleet = (c as IFleetCommand)?.FleetId;
                if (!_w.Light.InTransit.Any(t => t.Side == _side && t.Command.GetType() == c.GetType() && (t.Command as IFleetCommand)?.FleetId == fleet))
                    Light.Send(_sim, c);
                return true;
            }
            c.Apply(_w, _sim);
            return false;
        }

        private string Describe(FleetJob job)
        {
            var body = job.BodyId is int b ? _w.FindBody(b)?.Name : null;
            return body != null ? $"{job.Kind} {body}" : job.Kind.ToString();
        }

        private void SetGoal(string goal, string why)
        {
            var text = $"{goal}: {why}";
            if (_m.Goal == text) return;
            if (!_m.Goal.StartsWith(goal + ":", StringComparison.Ordinal)) Note($"Goal now {goal}: {why}");
            _m.Goal = text;
        }

        private void Note(string text)
        {
            // The same thought again within two days adds nothing (it would flood the log and the overlay).
            if (_m.Thoughts.Any(t => t.Text == text && _w.Hour - t.Hour < 48)) return;
            _m.Thoughts.Add(new Thought(_w.Hour, text));
            if (_m.Thoughts.Count > 40) _m.Thoughts.RemoveAt(0);
            _sim.Log.Info("ai", text, new { side = _side.ToString(), personality = _m.Personality.ToString(), ratio = _m.Ratio });
        }
    }
}
