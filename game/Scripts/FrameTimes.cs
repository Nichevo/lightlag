using Godot;

namespace Lightlag.Game;

/// <summary>
/// The last few hundred frames' timings, for the F8 report: frame time, the engine's process and physics time,
/// draw calls, and which screen was up. Kept in a ring, so it costs nothing to collect.
/// </summary>
public static class FrameTimes
{
    private const int Size = 600;
    private static readonly double[] Frame = new double[Size], Process = new double[Size], Hud = new double[Size];
    private static readonly int[] Calls = new int[Size];
    private static int _at, _count;

    /// <summary>One frame: its length (seconds) and the time spent deciding what to redraw and refreshing panels (ms).</summary>
    public static void Note(double delta, double hudMs)
    {
        Frame[_at] = delta * 1000;
        Process[_at] = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000;
        Calls[_at] = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        Hud[_at] = hudMs;
        _at = (_at + 1) % Size;
        _count = Math.Min(Size, _count + 1);
    }

    /// <summary>A Markdown section for the report.</summary>
    public static string Describe(string screen)
    {
        if (_count == 0) return "";
        double Avg(double[] a) => Enumerable.Range(0, _count).Average(i => a[i]);
        double Pct(double[] a, double p)
        {
            var sorted = Enumerable.Range(0, _count).Select(i => a[i]).OrderBy(x => x).ToList();
            return sorted[Math.Clamp((int)(p * (sorted.Count - 1)), 0, sorted.Count - 1)];
        }
        var fps = Avg(Frame) > 0 ? 1000 / Avg(Frame) : 0;
        return "## Frame timings\n\n"
               + $"Last {_count} frames on {screen}, {DisplayServer.WindowGetSize()} window, {RenderingServer.GetVideoAdapterName()}:\n\n"
               + "| | average | 95th percentile | worst |\n|---|---|---|---|\n"
               + $"| Frame (ms) | {Avg(Frame):0.0} ({fps:0} fps) | {Pct(Frame, 0.95):0.0} | {Pct(Frame, 1):0.0} |\n"
               + $"| Process (ms) | {Avg(Process):0.0} | {Pct(Process, 0.95):0.0} | {Pct(Process, 1):0.0} |\n"
               + $"| Panels (ms) | {Avg(Hud):0.00} | {Pct(Hud, 0.95):0.00} | {Pct(Hud, 1):0.00} |\n"
               + $"| Draw calls | {Enumerable.Range(0, _count).Average(i => Calls[i]):0} | | {Enumerable.Range(0, _count).Max(i => Calls[i])} |\n";
    }
}
