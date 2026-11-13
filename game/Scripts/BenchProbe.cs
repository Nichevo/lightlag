using System.Collections.Generic;
using System.Diagnostics;
namespace Lightlag.Game;
/// <summary>
/// Lap timer for the --panel-bench developer check: Hud.Refresh marks a lap after each part it refreshes.
/// Off (and nearly free) unless the bench is running.
/// </summary>
public static class BenchProbe
{
    public static bool On;
    public static readonly Dictionary<string, double> Ms = new();
    private static readonly Stopwatch Sw = new();
    public static void Start() { if (On) Sw.Restart(); }
    public static void Lap(string name)
    {
        if (!On) return;
        var t = Sw.Elapsed.TotalMilliseconds;
        Ms[name] = (Ms.TryGetValue(name, out var v) ? v : 0) + t;
        Sw.Restart();
    }
}
