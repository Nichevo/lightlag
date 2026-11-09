using System.Text.RegularExpressions;
using Lightlag.Sim.Core;
using Xunit;

namespace Lightlag.Sim.Tests;

public class DeterminismTests
{
    [Fact]
    public void DetMath_matches_System_Math_closely()
    {
        var rng = new SimRandom(3);
        for (var i = 0; i < 100_000; i++)
        {
            var x = rng.Range(-50.0, 50.0);
            Assert.InRange(DetMath.Sin(x) - Math.Sin(x), -1e-14, 1e-14);
            Assert.InRange(DetMath.Cos(x) - Math.Cos(x), -1e-14, 1e-14);
        }
        Assert.Equal(0.0, DetMath.Sin(0));
        Assert.Equal(1.0, DetMath.Cos(0));
    }

    [Fact]
    public void DetMath_values_are_pinned()
    {
        // Fixed results: if these change, saves and replays from older builds stop matching.
        Assert.Equal(0.8414709848078965, DetMath.Sin(1.0));
        Assert.Equal(0.5403023058681397, DetMath.Cos(1.0));
        Assert.Equal(-0.9589242746631386, DetMath.Sin(5.0));
    }

    /// <summary>
    /// The operating system's trig functions differ between Windows and Linux in the last digit,
    /// which breaks replays across machines. The simulation must use DetMath instead.
    /// </summary>
    [Fact]
    public void Simulation_uses_no_platform_dependent_maths()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Lightlag.sln"))) root = Path.GetDirectoryName(root)!;
        var banned = new Regex(@"\bMath\.(Sin|Cos|Tan|Asin|Acos|Atan2?|Sinh|Cosh|Tanh|Pow|Exp|Log|Log10|Log2|Cbrt)\s*\(");
        var offenders = Directory.GetFiles(Path.Combine(root, "src", "Lightlag.Sim"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.EndsWith("DetMath.cs"))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => banned.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0, "Use DetMath in the simulation:\n" + string.Join("\n", offenders));
    }
}
