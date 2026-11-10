using System.Text.Json.Serialization;

namespace Lightlag.Sim.Core;

// ---- Settings and data --------------------------------------------------------------------------

public enum Difficulty { Easy, Normal }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Personality { Raider, Expander, Turtle }

/// <summary>A side run by the computer: how hard, and (for tests) which personality instead of a random one.</summary>
public sealed record AiSetup(Side Side, Difficulty Difficulty = Difficulty.Normal, Personality? Personality = null);

/// <summary>Choices made when a game starts that are not in the scenario file. Stored in the replay.</summary>
public sealed record GameOptions(List<AiSetup>? Ai = null)
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
    double ProbeEarly, double TakeHeldWeight, int ClaimGroups, List<string> Research);

public sealed record AiData(
    AiThinkHours ThinkHours, AiEasy Easy, AiRange AttackRatio, double NerveDrift, double NerveRange, AiRange FirstProbeDay,
    double ThreatRadiusAu, double DangerRadiusAu, double DangerRatio, double RefitSupplies, double RefitArmour,
    double EnemyShipValue, double EnemyBuildShare, int ClaimGroupShips, double ProbeDays, double ProbeDistanceAu,
    double ChainClaimAu, double GatherDays, double StrikeSuppliesLeft, double HomeShareSeen, double HomeShareUnseen,
    Dictionary<string, PersonalityData> Personalities);

// ---- State (saved with the world) ----------------------------------------------------------------

public enum JobKind { Guard, Claim, Break, Strike, Hunt, Probe, Refit }

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
}

public sealed record Thought(double Hour, string Text);

/// <summary>One computer-run side: its settings, its rolled temperament, its fleets' jobs and what it believes.</summary>
public sealed class MindState
{
    public Side Side { get; set; }
    public Difficulty Difficulty { get; set; }
    public Personality Personality { get; set; }
    public RandomState Random { get; set; } = new(0, 1);

    /// <summary>How much stronger it wants to be before attacking: rolled per game, and drifting a little day to day.</summary>
    public double BaseRatio { get; set; }
    public double Ratio { get; set; }
    public double FirstProbeDay { get; set; }

    public double NextThinkHour { get; set; }
    public double LastThinkHour { get; set; }
    public int LastNerveDay { get; set; }
    public double LastProbeEndHour { get; set; } = -1e9;

    /// <summary>Until this hour it is pulling fleets home to gather for a strike, so it starts no new claims.</summary>
    public double GatherUntilHour { get; set; } = -1e9;

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

    /// <summary>Sets up the computer-run sides at the start of a game.</summary>
    public static void Setup(World w, GameOptions? options, ulong seed)
    {
        if (options?.Ai == null || !w.Loop) return;
        foreach (var setup in options.Ai.GroupBy(a => a.Side).Select(g => g.First()))
        {
            var rng = new SimRandom(Mix(seed) ^ Mix((ulong)setup.Side + 0xC0FFEEUL), stream: 11 + (ulong)setup.Side);
            var personality = setup.Personality ?? (Personality)rng.Range(0, 3);
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
            mind.Random = rng.Save();
            w.Minds.Add(mind);
        }
    }

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
                    Hunt();
                    Probe();
                    if (_w.Hour >= _m.GatherUntilHour) Expand();
                }
            }
            Spend();
        }

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
        }

        /// <summary>The enemy holds most planets and is counting down to a win: time to take risks.</summary>
        private bool Desperate() => Economy.Of(_w, _enemy).MajoritySinceHour != null;

        /// <summary>The attack ratio it uses now: lower when desperate.</summary>
        private double Want => Desperate() ? Math.Min(_m.Ratio, 1.0) : _m.Ratio;

        private void Believe()
        {
            // Sightings it has (what its sensors saw). Recent ones only for planning.
            _known = _w.Sightings.Where(s => s.SeenBy == _side && _w.Hour - s.Hour <= 20 * 24).ToList();
            // What the enemy has earned, from the planets anyone can see it holds.
            var hours = Math.Max(0, _w.Hour - _m.LastThinkHour);
            var enemyIncome = Economy.Claimable(_w).Where(b => b.Owner == _enemy).Sum(b => b.Industry);
            _m.EnemyEarnedGuess += enemyIncome * hours / 24;
            var killed = Economy.Of(_w, _side).Totals.ShipsKilled;
            var fromEconomy = _m.EnemyStartPower + (_m.EnemyEarnedGuess + GameData.Economy.StartIndustry) * A.EnemyBuildShare
                              - killed * A.EnemyShipValue;
            var seen = _known.Sum(SightingPower);
            _m.EnemyPowerGuess = Math.Round(Math.Max(seen, fromEconomy), 1);
            _m.OwnPower = Math.Round(Stats.Power(_w, _side), 1);
        }

        private double SightingPower(Sighting s) => s.Ships * A.EnemyShipValue;

        /// <summary>Known enemy power near a point (recently seen there, or heading there as last seen).</summary>
        private double EnemyNear(Vec2 at, double radius, double maxAgeHours = 3 * 24) =>
            _known.Where(s => _w.Hour - s.Hour <= maxAgeHours && Vec2.Distance(s.Guess(_w.Hour), at) <= radius).Sum(SightingPower);

        // ---- Fleets ---------------------------------------------------------------------------

        private IEnumerable<Fleet> Mine() => _w.Fleets.Where(f => f.Side == _side && f.ShipIds.Count > 0);

        private bool AtHome(Fleet f) => _home != null && Economy.InOrbitAt(f, _home) && f.Flight == null;

        private bool Busy(Fleet f) => f.Maneuvering || f.Retreating || f.JoinFleetId != null;

        private static double ShareOf(IEnumerable<(double Have, double Full)> parts)
        {
            double have = 0, full = 0;
            foreach (var (h, fl) in parts) { have += h; full += fl; }
            return full > 0 ? have / full : 1;
        }

        private double Supplies(Fleet f) =>
            _w.ShipsOf(f).Select(s => Math.Clamp(s.Supplies / GameData.ShipClass(s.ClassId).Supplies, 0, 1)).DefaultIfEmpty(1).Min();

        private double Missiles(Fleet f) =>
            ShareOf(_w.ShipsOf(f).Where(s => GameData.ShipClass(s.ClassId).Missiles > 0)
                .Select(s => ((double)s.Missiles, (double)GameData.ShipClass(s.ClassId).Missiles)));

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
                    JobKind.Hunt => job.EnemyFleetId is not int e || Sensors.Find(_w, _side, e) is not Sighting s || _w.Hour - s.Hour > 48,
                    JobKind.Probe => f.Flight == null && _w.Hour - job.Since > A.ProbeDays * 24,
                    JobKind.Refit => AtHome(f) && Ready(f),
                    _ => false,
                };
                // A flight that ended somewhere else (an order it could not finish): think again.
                if (!done && f.Flight == null && !Busy(f) && job.Kind is JobKind.Claim or JobKind.Break or JobKind.Strike
                    && body != null && !Economy.InOrbitAt(f, body))
                    done = true;
                if (!done) continue;
                _m.Jobs.Remove(job);
                if (job.Kind == JobKind.Probe) _m.LastProbeEndHour = _w.Hour;
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
            var mine = Stats.Power(_w, f);
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
                if (mine >= theirs * A.DangerRatio)
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
            // Pull back from a fight it expects to lose.
            // A scout only runs from what comes close to it; others also from what was just seen where they are going.
            var near = job.Kind == JobKind.Probe
                ? EnemyNear(f.Position, 0.5 * A.DangerRadiusAu * _au, 12)
                : Math.Max(EnemyNear(f.Position, A.DangerRadiusAu * _au), EnemyNear(job.Point, A.DangerRadiusAu * _au, 24));
            var limit = job.Kind == JobKind.Strike ? A.DangerRatio * 1.3 : A.DangerRatio;
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
            var guard = Mine().Where(f => AtHome(f)).Sum(f => Stats.Power(_w, f));
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
                guard += Stats.Power(_w, f);
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
                var need = there * (ours ? Math.Max(1.1, A.DangerRatio * 1.05) : Math.Max(Want, A.DangerRatio * 1.05));
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
            var poolPower = pool.Sum(f => Stats.Power(_w, f));
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
            var speed = SpeedLeaving(pool.OrderByDescending(f => Stats.Power(_w, f)).First(), _enemyYard, A.StrikeSuppliesLeft);
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
        /// Not enough at home to strike, but enough if the claim groups came back: call home the ones that have
        /// not started claiming yet, and start no new claims for a while.
        /// </summary>
        private bool Gather(double poolPower, double keep, double want, double guess)
        {
            if (_w.Hour < _m.GatherUntilHour) { SetGoal("Gather", $"pulling fleets home to strike {_enemyYard!.Name}"); return true; }
            if (Easy) return false; // Easy does not plan that far ahead
            bool Enough(List<Fleet> fleets)
            {
                var total = (poolPower + fleets.Sum(f => Stats.Power(_w, f))) * (1 - keep);
                return fleets.Count > 0 && total >= want && total >= 3 * A.EnemyShipValue;
            }
            var claiming = Mine().Where(f => !AtHome(f) && !Busy(f) && _m.JobOf(f.Id) is { Kind: JobKind.Claim } j
                                             && _w.FindBody(j.BodyId ?? -1) is Body b && b.ClaimSide != _side && !NeedsRefit(f)).ToList();
            // Planning the flights home is costly, so first check that even all of them would be enough.
            if (!Enough(claiming)) return false;
            var away = claiming.Where(f => TravelDays(f, _home!, 0.6) <= A.GatherDays).ToList();
            if (!Enough(away)) return false;
            var awayPower = away.Sum(f => Stats.Power(_w, f));
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
            foreach (var s in _known.Where(s => s.Visible || _w.Hour - s.Hour <= maxAgeHours).OrderBy(s => s.Ships).ToList())
            {
                if (_m.Jobs.Any(j => j.EnemyFleetId == s.FleetId)) continue;
                var at = s.Guess(_w.Hour);
                if (!mineBodies.Any(p => Vec2.Distance(p, at) <= 0.5 * _au)) continue;
                var need = SightingPower(s) * Math.Max(Want / _p.HuntWeight, A.DangerRatio * 1.05);
                if (Pool().Where(Ready).Sum(f => Stats.Power(_w, f)) * (1 - _p.GuardShare) < need) continue;
                var group = TakeFromPool(need, $"hunt {s.Name}");
                if (group == null) continue;
                // Burn hard and brake to gun range when there is fuel to spare (chasing it if it moves); else a plain attack run.
                var request = new FlightRequest(null, at, Burn(group), ArrivalStyle.Brake, HoldStyle.Drift, _w.Tick, TargetFleetId: s.FleetId,
                    Chase: ChaseMode.GunRange);
                if (!Fly(group, request) && !Fly(group, request with { Chase = ChaseMode.None, SpeedFraction = 0.8 }))
                { GoHome(group, "could not reach the enemy fleet"); continue; }
                Orders(group, Core.Stance.Engage);
                Assign(group, JobKind.Hunt, null, at, $"{s.Name} ({s.Ships} ships) seen near our planets", s.FleetId);
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
            Orders(scout, Core.Stance.Hold);
            Assign(scout, JobKind.Probe, null, point, $"scout {A.ProbeDistanceAu} AU short of {_enemyYard.Name} and watch for {A.ProbeDays:0} days");
            Note($"Sends {scout.Name} to look at {_enemyYard.Name}");
        }

        /// <summary>Claim planets: the best open ones first, and enemy-held ones when they look lightly held.</summary>
        private void Expand()
        {
            var totalPower = Mine().Sum(f => Stats.Power(_w, f));
            for (var tries = 0; tries < 3; tries++)
            {
                if (Slip()) return;
                var pool = Pool().Where(Ready).ToList();
                var free = pool.Sum(f => Stats.Power(_w, f)) - totalPower * _p.GuardShare;
                if (free < A.EnemyShipValue * 0.6 || pool.Sum(f => f.ShipIds.Count) < 2) return;
                if (_m.Jobs.Count(j => j.Kind == JobKind.Claim) >= _p.ClaimGroups) return;
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
            var held = b.Owner == _enemy ? _p.TakeHeldWeight * (Desperate() ? 3 : 0.7) : 1;
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
            var power = Stats.Power(_w, f);
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

        private void Spend()
        {
            var orders = _w.Orders.Where(o => o.Side == _side).ToList();
            if (orders.Count(o => o.Kind == OrderKind.Ship) < 2)
            {
                var frigateShare = Easy ? 0.6 : _p.FrigateShare;
                var cls = _rng.Chance(frigateShare) ? "gun-frigate" : "missile-destroyer";
                if (Economy.CanQueue(_w, _side, OrderKind.Ship, cls) == null)
                    Do(new QueueOrderCommand(_side, OrderKind.Ship, cls));
            }
            if (!orders.Any(o => o.Kind == OrderKind.Research))
            {
                var open = _p.Research.Where(r => GameData.ResearchById.ContainsKey(r) && Economy.CanQueue(_w, _side, OrderKind.Research, r) == null).ToList();
                if (open.Count > 0)
                {
                    var pick = Easy ? open[_rng.Range(0, open.Count)] : open[0];
                    // Research waits behind ships when Industry is short, so not to stall the shipyard: a ship
                    // waiting to start is paid for first.
                    var shipWaiting = orders.FirstOrDefault(o => o.Kind == OrderKind.Ship && !o.Paid) is WorkOrder next ? Economy.CostOf(next) : 0;
                    if (Economy.Of(_w, _side).Industry >= shipWaiting + GameData.Research(pick).Cost * 0.5 || orders.Count(o => o.Kind == OrderKind.Ship) == 0)
                        Do(new QueueOrderCommand(_side, OrderKind.Research, pick));
                }
            }
        }

        // ---- Orders -----------------------------------------------------------------------------

        /// <summary>Takes ships from the home fleet(s) worth at least this much power (or this many ships) as a new fleet.</summary>
        private Fleet? TakeFromPool(double power, string what, int minShips = 1)
        {
            var pool = Pool().Where(Ready).OrderByDescending(f => Stats.Power(_w, f)).ToList();
            var from = pool.FirstOrDefault();
            if (from == null) return null;
            var ships = _w.ShipsOf(from).OrderByDescending(s => Stats.ShipPower(_w, s)).ToList();
            var picked = new List<Ship>();
            double sum = 0;
            foreach (var s in ships)
            {
                if (sum >= power && picked.Count >= minShips) break;
                picked.Add(s);
                sum += Stats.ShipPower(_w, s);
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
            Orders(f, stance);
            Assign(f, kind, target.Id, target.Position, why);
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
            if (!FlightPlanner.Plan(_w, f, request).Ok) return false;
            Do(new PlanFlightCommand(f.Id, request));
            return f.Flight != null;
        }

        private void Orders(Fleet f, Stance stance)
        {
            if (f.Stance != stance || !f.RetreatWhenDamaged) Do(new SetStanceCommand(f.Id, stance, true));
        }

        private void Assign(Fleet f, JobKind kind, int? bodyId, Vec2 point, string why, int? enemyFleetId = null)
        {
            _m.Jobs.RemoveAll(j => j.FleetId == f.Id);
            _m.Jobs.Add(new FleetJob { FleetId = f.Id, Kind = kind, BodyId = bodyId, EnemyFleetId = enemyFleetId, Point = point, Why = why, Since = _w.Hour });
        }

        private void Do(GameCommand command)
        {
            // Applied now, not queued: the mind runs inside the step, so a replay runs it again and needs no record.
            (command with { Source = $"ai:{_side.ToString().ToLowerInvariant()}" }).Apply(_w, _sim);
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
