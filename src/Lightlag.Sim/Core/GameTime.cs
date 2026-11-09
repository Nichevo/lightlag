namespace Lightlag.Sim.Core;

/// <summary>
/// The simulation runs in fixed steps ("ticks") of one game hour, whatever speed the screen runs at.
/// That fixed step is what makes replays exact.
/// </summary>
public static class GameTime
{
    public const int TicksPerDay = 24;
    public const double HoursPerTick = 1.0;

    /// <summary>Battle-time steps per hour: 360 steps of 10 seconds.</summary>
    public const int SubSteps = 360;
    public const double SubStepHours = 1.0 / SubSteps;

    public static int DayOf(long tick) => (int)(tick / TicksPerDay);

    /// <summary>"Day 12, 07:00".</summary>
    public static string Format(long tick) => $"Day {DayOf(tick)}, {tick % TicksPerDay:00}:00";

    /// <summary>"Day 12, 07:23" from game hours, with minutes.</summary>
    public static string Format(double hour)
    {
        var tick = (long)Math.Floor(hour);
        var minutes = (int)Math.Floor((hour - tick) * 60);
        return $"Day {DayOf(tick)}, {tick % TicksPerDay:00}:{minutes:00}";
    }
}
