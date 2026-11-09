using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;

namespace Lightlag.Headless;

/// <summary>Searches .jsonl game logs by level, area, text, seed and game day.</summary>
public static class LogSearch
{
    public static int Run(Options opts)
    {
        var target = opts.Positional.FirstOrDefault() ?? Path.Combine(Cli.DataFolder(opts).Logs);
        var files = Directory.Exists(target)
            ? Directory.GetFiles(target, "*.jsonl", SearchOption.AllDirectories).OrderBy(f => f).ToArray()
            : new[] { target };
        var minLevel = opts.Get("level") is string lv ? Enum.Parse<LogLevel>(lv, ignoreCase: true) : LogLevel.Debug;
        var area = opts.Get("area");
        var text = opts.Get("text");
        var seed = opts.GetULong("seed");
        var max = opts.GetLong("max") ?? 200;
        (int From, int To)? days = null;
        if (opts.Get("days") is string d)
        {
            var parts = d.Split('-');
            days = (int.Parse(parts[0]), int.Parse(parts.Length > 1 ? parts[1] : parts[0]));
        }

        var shown = 0;
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file))
            {
                var e = GameLog.Parse(line);
                if (e == null) continue;
                if (e.Level < minLevel) continue;
                if (area != null && !e.Area.Equals(area, StringComparison.OrdinalIgnoreCase)) continue;
                if (seed != null && e.Seed != seed) continue;
                var day = GameTime.DayOf(e.Tick);
                if (days != null && (day < days.Value.From || day > days.Value.To)) continue;
                if (text != null && !line.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine($"{e.Time:HH:mm:ss} {e.Level.ToString().ToUpperInvariant(),-5} seed {e.Seed} day {day,4} [{e.Area}] {e.Message}");
                if (++shown >= max)
                {
                    Console.WriteLine($"(stopped at {max} lines; use --max to see more)");
                    return 0;
                }
            }
        }
        Console.WriteLine($"{shown} matching lines in {files.Length} file(s).");
        return 0;
    }
}
