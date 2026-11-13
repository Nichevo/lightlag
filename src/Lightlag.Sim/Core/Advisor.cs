using System.Text;

namespace Lightlag.Sim.Core;

/// <summary>
/// The optional advisor for the computer rival (off by default). Now and then the game asks a local language model
/// (Ollama, on the player's own machine) for one strategy nudge. The model is shown only what the rival itself
/// knows (its own worlds and fleets, the enemy fleets it has seen and the worlds anyone can see the enemy holds), and
/// may answer with one of a few focuses. The answer reaches the game as an <see cref="AdviseCommand"/>, so it is
/// in the replay and saves like any order, and the simulation stays deterministic. A nudge only leans the rival's
/// profile halfway towards the focus for a few weeks: it never gives orders, and the rival's own rules still decide.
/// This file holds the parts the simulation owns: the brief, the reply parser and the nudge; the asking lives in
/// the game (game/Scripts/Advisor.cs) and the headless tools.
/// </summary>
public static class Advisor
{
    /// <summary>The focuses the advisor may pick, each with what it leans the rival towards.</summary>
    public static readonly string[] Focuses = { "take", "raid", "hunt", "expand", "defend", "research" };

    /// <summary>How long a nudge lasts, and the least time from one nudge to the next (game days).</summary>
    public const double NudgeDays = 20, GapDays = 25;

    /// <summary>How far a nudge moves the profile towards the focus (0: none, 1: all the way).</summary>
    public const double Weight = 0.5;

    public static string Describe(string focus) => focus switch
    {
        "take" => "go after enemy colonies that are badly guarded",
        "raid" => "lie in wait for enemy supply ships on the lanes",
        "hunt" => "hunt small enemy groups out on their own",
        "expand" => "settle and claim empty worlds",
        "defend" => "guard your own worlds and fight only when the odds are good",
        "research" => "put more into research before the next ship",
        _ => focus,
    };

    /// <summary>True when the side may be asked now: a computer-run side, no nudge running, and the gap passed.</summary>
    public static bool Due(World w, Side side) =>
        w.Loop && w.Winner == null && Mind.Of(w, side) is MindState m && w.Hour >= m.LastAdviceHour + GapDays * 24;

    /// <summary>The profile with a running nudge folded in (unchanged when there is none).</summary>
    public static PersonalityProfile Apply(PersonalityProfile p, MindState m, double hour)
    {
        if (m.Advice == "" || hour >= m.AdviceUntilHour) return p;
        var target = m.Advice switch
        {
            "take" => p with { Aggression = 0.9, Risk = 0.7 },
            "raid" => p with { Raiding = 0.9 },
            "hunt" => p with { Aggression = 0.8, Risk = 0.8 },
            "expand" => p with { Expansion = 0.9 },
            "defend" => p with { Defence = 0.9, Aggression = 0.2, Risk = 0.3 },
            "research" => p with { Research = 0.9 },
            _ => p,
        };
        static double Mix(double a, double b) => Math.Round(a + (b - a) * Weight, 3);
        return new PersonalityProfile(Mix(p.Aggression, target.Aggression), Mix(p.Expansion, target.Expansion), Mix(p.Defence, target.Defence),
            Mix(p.Risk, target.Risk), Mix(p.Raiding, target.Raiding), Mix(p.Research, target.Research));
    }

    /// <summary>
    /// The prompt: what the side knows, in plain words, and the answer wanted. Only the side's own knowledge goes in:
    /// its worlds, fleets and Industry, enemy fleets its sensors have reported, and the worlds the enemy openly holds.
    /// </summary>
    public static string Brief(World w, Side side)
    {
        var enemy = Sides.MainEnemy(w, side);
        var mine = Economy.Claimable(w).Where(b => b.Owner == side).ToList();
        var theirs = Economy.Claimable(w).Where(b => b.Owner == enemy).ToList();
        var empty = Economy.Claimable(w).Count(b => b.Owner == null);
        var fleets = w.Fleets.Where(f => f.Side == side).ToList();
        var seen = Sensors.Known(w, seat: true).Where(s => s.SeenBy == side && !s.Own).ToList();
        var m = Mind.Of(w, side);
        var sb = new StringBuilder();
        sb.AppendLine("You advise the commander of one side in a slow, real-time space war inside one star system. Ships are scarce; "
                      + "a side wins by controlling most of the system's people, Industry, worlds and fleets.");
        sb.AppendLine($"Day {w.Day}.");
        sb.AppendLine($"Our worlds ({mine.Count}): {string.Join(", ", mine.Select(b => $"{b.Name} ({b.PopK:0.#}k people)"))}.");
        sb.AppendLine($"Our fleets ({fleets.Count}, {fleets.Sum(f => f.ShipIds.Count)} ships): "
                      + string.Join(", ", fleets.Take(8).Select(f => $"{f.Name} {f.ShipIds.Count} ships" + (m?.JobOf(f.Id) is FleetJob j ? $" ({j.Kind})" : ""))) + ".");
        sb.AppendLine($"Worlds the enemy holds ({theirs.Count}): {string.Join(", ", theirs.Select(b => b.Name))}. Empty worlds left to claim: {empty}.");
        sb.AppendLine(seen.Count == 0 ? "We have seen no enemy fleets lately."
            : $"Enemy fleets we have seen: {string.Join(", ", seen.Take(8).Select(s => $"{s.Ships} ships, last seen {Math.Max(0, w.Hour - s.Hour) / 24:0.#} days ago"))}.");
        if (m != null)
        {
            sb.AppendLine($"Our temperament: {m.Personality}. Current goal: {m.Goal}.");
            var lost = Stats.Of(w, side);
            sb.AppendLine($"Battles fought so far: {lost.Battles}.");
        }
        sb.AppendLine();
        sb.AppendLine("Pick ONE focus for the next few weeks from: " + string.Join(", ", Focuses.Select(f => $"{f} ({Describe(f)})")) + ".");
        sb.AppendLine("Answer on one line exactly as: FOCUS: <focus> | WHY: <one short sentence>");
        return sb.ToString();
    }

    /// <summary>
    /// Reads the model's answer: the first known focus after "FOCUS:" (or, failing that, the first focus word in the
    /// text), and a short reason. Null when no focus can be found. Thinking blocks (&lt;think&gt;...&lt;/think&gt;) are skipped.
    /// </summary>
    public static (string Focus, string Why)? ParseReply(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var text = reply;
        var end = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (end >= 0) text = text[(end + 8)..];
        var lower = text.ToLowerInvariant();
        string? focus = null;
        var at = lower.IndexOf("focus:", StringComparison.Ordinal);
        if (at >= 0)
        {
            var after = lower[(at + 6)..].TrimStart(' ', '*', '"', '\'');
            focus = Focuses.FirstOrDefault(f => after.StartsWith(f, StringComparison.Ordinal));
        }
        focus ??= Focuses.Select(f => (f, i: IndexOfWord(lower, f))).Where(x => x.i >= 0).OrderBy(x => x.i).Select(x => x.f).FirstOrDefault();
        if (focus == null) return null;
        var why = "";
        var w = lower.IndexOf("why:", StringComparison.Ordinal);
        if (w >= 0) why = text[(w + 4)..];
        why = Clean(why);
        return (focus, why);
    }

    private static int IndexOfWord(string text, string word)
    {
        for (var i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
        {
            var before = i == 0 || !char.IsLetter(text[i - 1]);
            var after = i + word.Length >= text.Length || !char.IsLetter(text[i + word.Length]);
            if (before && after) return i;
        }
        return -1;
    }

    /// <summary>One line, plain characters, at most 160 long.</summary>
    public static string Clean(string? text)
    {
        var line = (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        var sb = new StringBuilder();
        foreach (var c in line)
            if (!char.IsControl(c) && c != '*' && c != '`') sb.Append(c);
        var s = sb.ToString().Trim();
        return s.Length > 160 ? s[..160] : s;
    }
}

/// <summary>
/// A nudge from the rival's advisor (see Advisor): lean towards one focus for a few weeks. Turned down when the
/// side is not run by the computer, the focus is unknown, or the last nudge was too recent.
/// </summary>
public sealed record AdviseCommand(Side Side, string Focus, string Why) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        var focus = (Focus ?? "").Trim().ToLowerInvariant();
        if (Mind.Of(world, Side) is not MindState m)
        {
            sim.Log.Warn("ai", "Advice for a side the computer does not run", new { side = Side.ToString(), Source });
            return;
        }
        if (!Advisor.Focuses.Contains(focus))
        {
            sim.Log.Warn("ai", "Advice with an unknown focus", new { side = Side.ToString(), focus, Source });
            return;
        }
        if (!Advisor.Due(world, Side))
        {
            sim.Log.Info("ai", "Advice too soon after the last; ignored", new { side = Side.ToString(), focus, Source });
            return;
        }
        var why = Advisor.Clean(Why);
        m.Advice = focus;
        m.AdviceWhy = why;
        m.AdviceUntilHour = world.Hour + Advisor.NudgeDays * 24;
        m.LastAdviceHour = world.Hour;
        m.AdviceCount++;
        var text = $"Advisor: {focus} ({Advisor.Describe(focus)})" + (why != "" ? $": {why}" : "");
        m.Thoughts.Add(new Thought(world.Hour, text));
        if (m.Thoughts.Count > 40) m.Thoughts.RemoveAt(0);
        Choices.Note(world, Side, "advice", focus);
        sim.Log.Info("ai", "Advisor nudge", new { side = Side.ToString(), focus, why, until = m.AdviceUntilHour, Source });
    }
}
