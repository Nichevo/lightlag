namespace Lightlag.Sim.Core;

/// <summary>
/// The simulation runs in fixed steps ("ticks") of one game hour, whatever speed the screen runs at.
/// That fixed step is what makes replays exact.
/// </summary>
public static class GameTime
{
    public const int TicksPerDay = 24;
    public const double HoursPerTick = 1.0;

    public static int DayOf(long tick) => (int)(tick / TicksPerDay);

    /// <summary>"Day 12, 07:00".</summary>
    public static string Format(long tick) => $"Day {DayOf(tick)}, {tick % TicksPerDay:00}:00";
}
