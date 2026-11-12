namespace Lightlag.Sim.Logging;

/// <summary>
/// Measures how long each part of a game step takes, for the headless "profile" run and batch summaries.
/// The simulation never reads the clock itself: whoever turns timing on hands in a clock (the headless
/// runner passes Stopwatch.GetTimestamp), and nothing the game decides depends on what is measured.
/// Sections can sit inside each other (the supply chain is asked from the rival's mind): each keeps its
/// total time and its own time (the total less the sections inside it).
/// </summary>
public sealed class SectionTimer(Func<long> clock, long ticksPerSecond)
{
    /// <summary>The timer the running game reports to (null: timing off, sections cost next to nothing).</summary>
    [ThreadStatic] public static SectionTimer? Active;

    public sealed class Row
    {
        public long Calls;
        public long Total;
        public long Self;
        public long Worst;
    }

    private readonly Dictionary<string, Row> _rows = new();
    private readonly Stack<(string Name, long Start, long Inner)> _open = new();

    public long TicksPerSecond { get; } = ticksPerSecond;
    public IReadOnlyDictionary<string, Row> Rows => _rows;

    /// <summary>Game hours measured (whole hours stepped, battle time included).</summary>
    public long Hours { get; set; }

    /// <summary>Starts a section on the active timer: `using var _ = SectionTimer.Time("intel");`.</summary>
    public static Section Time(string name) => Active is SectionTimer t ? t.Open(name) : default;

    private Section Open(string name)
    {
        _open.Push((name, clock(), 0));
        return new Section(this);
    }

    private void Close()
    {
        var (name, start, inner) = _open.Pop();
        var spent = clock() - start;
        if (!_rows.TryGetValue(name, out var row)) _rows[name] = row = new Row();
        row.Calls++;
        row.Total += spent;
        row.Self += spent - inner;
        row.Worst = Math.Max(row.Worst, spent);
        if (_open.Count > 0)
        {
            var parent = _open.Pop();
            _open.Push(parent with { Inner = parent.Inner + spent });
        }
    }

    public void Clear()
    {
        _rows.Clear();
        _open.Clear();
        Hours = 0;
    }

    /// <summary>Adds another timer's rows to this one (to total a batch of games).</summary>
    public void Add(SectionTimer other)
    {
        foreach (var (name, r) in other._rows)
        {
            if (!_rows.TryGetValue(name, out var row)) _rows[name] = row = new Row();
            row.Calls += r.Calls;
            row.Total += r.Total;
            row.Self += r.Self;
            row.Worst = Math.Max(row.Worst, r.Worst);
        }
        Hours += other.Hours;
    }

    public double Ms(long ticks) => ticks * 1000.0 / TicksPerSecond;

    /// <summary>A Markdown table, slowest own time first: total and own time, time per game hour, calls and the worst single call.</summary>
    public string Table(int top = 30)
    {
        var all = _rows.Values.Sum(r => r.Self);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("| Part | Own time (s) | Share | Total incl. inner (s) | Per game hour (ms) | Calls | Worst call (ms) |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var (name, r) in _rows.OrderByDescending(kv => kv.Value.Self).Take(top))
            sb.AppendLine($"| {name} | {Ms(r.Self) / 1000:0.00} | {(all > 0 ? 100.0 * r.Self / all : 0):0.0} % | {Ms(r.Total) / 1000:0.00} | "
                          + $"{(Hours > 0 ? Ms(r.Self) / Hours : 0):0.000} | {r.Calls} | {Ms(r.Worst):0.0} |");
        return sb.ToString();
    }

    public readonly struct Section : IDisposable
    {
        private readonly SectionTimer? _timer;
        internal Section(SectionTimer timer) => _timer = timer;
        public void Dispose() => _timer?.Close();
    }
}
