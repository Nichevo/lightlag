using Godot;

namespace Lightlag.Game;

/// <summary>
/// The last few hundred frames' timings, for the F8 report: frame time, the engine's process and physics time,
/// draw calls, and which screen was up. Kept in a ring, so it costs nothing to collect.
/// </summary>
public static class FrameTimes
{
    private const int Size = 600;
    private static readonly double[] Frame = new double[Size], Process = new double[Size], Hud = new double[Size], Draw = new double[Size], Screen = new double[Size],
        Gpu = new double[Size], RenderCpu = new double[Size], Gc = new double[Size];
    private static readonly int[] Nodes = new int[Size];
    private static Rid _viewport;
    private static double _gcPauseMs = -1;
    private static int _gen2 = -1, _gen2Count;
    private static double _drawMs;

    /// <summary>Time spent in our own _Draw code (the map, the battle timeline) since the last frame was noted.</summary>
    public static void AddDraw(double ms) => _drawMs += ms;
    private static readonly int[] Calls = new int[Size];
    private static int _at, _count;

    /// <summary>One frame: its length (seconds) and the time spent deciding what to redraw and refreshing panels (ms).</summary>
    public static void Note(double delta, double hudMs, double screenMs = 0)
    {
        Draw[_at] = _drawMs;
        _drawMs = 0;
        Screen[_at] = screenMs;
        Frame[_at] = delta * 1000;
        Process[_at] = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000;
        Calls[_at] = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        Hud[_at] = hudMs;
        // Where the rest of the frame goes: the renderer's own CPU and GPU time for the main view, and .NET garbage
        // collection pauses (a lag report once showed 90 % of the frame outside everything timed above).
        if (!_viewport.IsValid && Engine.GetMainLoop() is SceneTree tree)
        {
            _viewport = tree.Root.GetViewportRid();
            RenderingServer.ViewportSetMeasureRenderTime(_viewport, true);
        }
        Gpu[_at] = _viewport.IsValid ? RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport) : 0;
        RenderCpu[_at] = _viewport.IsValid ? RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport) : 0;
        var pause = GC.GetTotalPauseDuration().TotalMilliseconds;
        Gc[_at] = _gcPauseMs < 0 ? 0 : pause - _gcPauseMs;
        _gcPauseMs = pause;
        var gen2 = GC.CollectionCount(2);
        if (_gen2 >= 0 && gen2 > _gen2) _gen2Count += gen2 - _gen2;
        _gen2 = gen2;
        Nodes[_at] = (int)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
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
               + $"| Game screen update (ms) | {Avg(Screen):0.00} | {Pct(Screen, 0.95):0.00} | {Pct(Screen, 1):0.00} |\n"
               + $"| Map and timeline drawing (ms) | {Avg(Draw):0.00} | {Pct(Draw, 0.95):0.00} | {Pct(Draw, 1):0.00} |\n"
               + $"| Renderer CPU (ms) | {Avg(RenderCpu):0.00} | {Pct(RenderCpu, 0.95):0.00} | {Pct(RenderCpu, 1):0.00} |\n"
               + $"| GPU (ms) | {Avg(Gpu):0.00} | {Pct(Gpu, 0.95):0.00} | {Pct(Gpu, 1):0.00} |\n"
               + $"| .NET garbage collection pauses (ms) | {Avg(Gc):0.00} | {Pct(Gc, 0.95):0.00} | {Pct(Gc, 1):0.00} |\n"
               + $"| Draw calls | {Enumerable.Range(0, _count).Average(i => Calls[i]):0} | | {Enumerable.Range(0, _count).Max(i => Calls[i])} |\n"
               + $"| Nodes | {Enumerable.Range(0, _count).Average(i => Nodes[i]):0} | | {Enumerable.Range(0, _count).Max(i => Nodes[i])} |\n"
               + $"\nFull .NET collections since the game started: {_gen2Count}; .NET memory in use {GC.GetTotalMemory(false) / 1048576.0:0} MB.\n";
    }
}
