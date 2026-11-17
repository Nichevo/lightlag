using Lightlag.Sim.Core;

namespace Lightlag.Sim.Tests;

/// <summary>Settling needs a survey first; tests about settling start with the side's surveys done.</summary>
public static class SurveyHelp
{
    public static void SurveyAll(this World w, Side side = Side.Player)
    {
        foreach (var b in w.Bodies) b.Surveyed[(int)side] = true;
    }
}
