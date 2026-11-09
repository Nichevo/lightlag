using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lightlag.Sim.Logging;

public enum LogLevel { Debug, Info, Warn, Error, Fatal }

/// <summary>One log line. Written as one JSON object per line (JSON Lines) so it can be searched and filtered.</summary>
public sealed record LogEntry(
    DateTime Time,
    LogLevel Level,
    string Area,
    string Message,
    string Build,
    ulong Seed,
    long Tick,
    JsonNode? Data);

/// <summary>
/// The game's log. Every line carries the build, the game seed and the game time, so any line can be
/// tied back to the exact game and moment it came from. Lines go to a .jsonl file (flushed at once,
/// so nothing is lost on a crash) and to a short in-memory tail that crash and F8 reports copy.
///
/// Areas are short lowercase words: "game", "sim", "rule", "crash", "report", "replay", "save", "ui", "ai".
/// </summary>
public sealed class GameLog : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<string> _tail = new();
    private readonly int _tailSize;
    private StreamWriter? _file;
    private readonly int[] _counts = new int[5];

    public GameLog(BuildInfo build, string? filePath = null, LogLevel minLevel = LogLevel.Info, int tailSize = 500)
    {
        Build = build;
        MinLevel = minLevel;
        _tailSize = tailSize;
        if (filePath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            _file = new StreamWriter(new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            FilePath = Path.GetFullPath(filePath);
        }
    }

    /// <summary>A log that keeps only its in-memory tail. Handy for tests.</summary>
    public static GameLog Memory(BuildInfo? build = null) => new(build ?? BuildInfo.Dev, null, LogLevel.Debug);

    public BuildInfo Build { get; }
    public LogLevel MinLevel { get; set; }
    public string? FilePath { get; }

    /// <summary>Supplies the current seed and tick. The simulation sets this when a game starts.</summary>
    public Func<(ulong Seed, long Tick)>? GameContext { get; set; }

    /// <summary>Also echo lines here (the console in headless runs, Godot's output in the editor).</summary>
    public Action<LogEntry, string>? Echo { get; set; }

    public event Action<LogEntry>? Written;

    public int Count(LogLevel level) { lock (_lock) return _counts[(int)level]; }

    public void Debug(string area, string message, object? data = null) => Write(LogLevel.Debug, area, message, data);
    public void Info(string area, string message, object? data = null) => Write(LogLevel.Info, area, message, data);
    public void Warn(string area, string message, object? data = null) => Write(LogLevel.Warn, area, message, data);
    public void Error(string area, string message, object? data = null) => Write(LogLevel.Error, area, message, data);
    public void Fatal(string area, string message, object? data = null) => Write(LogLevel.Fatal, area, message, data);

    public void Write(LogLevel level, string area, string message, object? data = null)
    {
        if (level < MinLevel) return;
        var (seed, tick) = SafeContext();
        JsonNode? node = null;
        if (data != null)
        {
            try { node = JsonSerializer.SerializeToNode(data, data.GetType(), Json.Options); }
            catch (Exception ex) { node = JsonValue.Create($"<could not write data: {ex.Message}>"); }
        }
        var entry = new LogEntry(DateTime.UtcNow, level, area, message, Build.Version, seed, tick, node);
        var line = ToJsonLine(entry);
        lock (_lock)
        {
            _counts[(int)level]++;
            _tail.Enqueue(line);
            while (_tail.Count > _tailSize) _tail.Dequeue();
            try { _file?.WriteLine(line); }
            catch (Exception) { /* a full disk must not take the game down */ }
        }
        Echo?.Invoke(entry, line);
        Written?.Invoke(entry);
    }

    /// <summary>The most recent lines, oldest first.</summary>
    public IReadOnlyList<string> Tail() { lock (_lock) return _tail.ToArray(); }

    public static string ToJsonLine(LogEntry e)
    {
        var obj = new JsonObject
        {
            ["time"] = e.Time.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["lvl"] = e.Level.ToString().ToLowerInvariant(),
            ["build"] = e.Build,
            ["seed"] = e.Seed,
            ["day"] = Core.GameTime.DayOf(e.Tick),
            ["tick"] = e.Tick,
            ["area"] = e.Area,
            ["msg"] = e.Message,
        };
        if (e.Data != null) obj["data"] = e.Data.DeepClone();
        return obj.ToJsonString();
    }

    /// <summary>Reads a line back. Returns null for a line that is not valid log JSON.</summary>
    public static LogEntry? Parse(string line)
    {
        try
        {
            var o = JsonNode.Parse(line)?.AsObject();
            if (o == null) return null;
            return new LogEntry(
                DateTime.Parse(o["time"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind),
                Enum.Parse<LogLevel>(o["lvl"]!.GetValue<string>(), ignoreCase: true),
                o["area"]?.GetValue<string>() ?? "",
                o["msg"]?.GetValue<string>() ?? "",
                o["build"]?.GetValue<string>() ?? "",
                o["seed"]?.GetValue<ulong>() ?? 0,
                o["tick"]?.GetValue<long>() ?? 0,
                o["data"]?.DeepClone());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private (ulong, long) SafeContext()
    {
        try { return GameContext?.Invoke() ?? (0, 0); }
        catch (Exception) { return (0, 0); }
    }

    /// <summary>Deletes old log files so the folder does not grow forever.</summary>
    public static void PruneOldFiles(string folder, int keep)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            var files = new DirectoryInfo(folder).GetFiles("*.jsonl").OrderByDescending(f => f.Name).Skip(keep);
            foreach (var f in files) f.Delete();
        }
        catch (Exception) { /* not worth failing over */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}
