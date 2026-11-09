namespace Lightlag.Sim.Core;

/// <summary>
/// Seeded random numbers for the simulation (PCG32). All game randomness must come from here,
/// never from System.Random or the clock, so a seed plus the player's commands replays exactly.
/// The state is two numbers and is saved with the world.
/// </summary>
public sealed class SimRandom
{
    private const ulong Multiplier = 6364136223846793005UL;

    public SimRandom(ulong seed, ulong stream = 54)
    {
        Inc = (stream << 1) | 1UL;
        State = 0;
        NextUInt();
        State += seed;
        NextUInt();
    }

    // For loading from a save.
    public SimRandom(RandomState saved)
    {
        State = saved.State;
        Inc = saved.Inc;
    }

    public ulong State { get; private set; }
    public ulong Inc { get; private set; }

    public RandomState Save() => new(State, Inc);

    public uint NextUInt()
    {
        var old = State;
        State = unchecked(old * Multiplier + Inc);
        var xorShifted = (uint)(((old >> 18) ^ old) >> 27);
        var rot = (int)(old >> 59);
        return (xorShifted >> rot) | (xorShifted << ((-rot) & 31));
    }

    /// <summary>A number in [0, 1).</summary>
    public double NextDouble() => NextUInt() / 4294967296.0;

    public double Range(double min, double max) => min + (max - min) * NextDouble();

    /// <summary>An integer in [min, max).</summary>
    public int Range(int min, int max)
    {
        if (max <= min) return min;
        return min + (int)(NextUInt() % (uint)(max - min));
    }

    public bool Chance(double p) => NextDouble() < p;
}

public sealed record RandomState(ulong State, ulong Inc);
