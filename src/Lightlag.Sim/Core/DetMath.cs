namespace Lightlag.Sim.Core;

/// <summary>
/// Maths that gives bit-for-bit the same answer on every machine. System.Math.Sin and Cos call the
/// operating system's maths library, which differs in the last digit between Windows and Linux;
/// that was enough to make a replay recorded on Windows drift when played back on Linux.
/// These use only + - * / (which are exact and identical everywhere), so use them in the simulation.
/// Sqrt is fine to use from System.Math: it is exact by the IEEE standard.
/// </summary>
public static class DetMath
{
    private const double TwoPi = 6.283185307179586;
    private const double Pi = 3.141592653589793;
    private const double HalfPi = 1.5707963267948966;

    public static double Sin(double x)
    {
        x = Wrap(x);                       // now in [-pi, pi)
        if (x > HalfPi) x = Pi - x;        // sin(pi - x) = sin(x)
        else if (x < -HalfPi) x = -Pi - x;
        return SinSeries(x);               // |x| <= pi/2
    }

    public static double Cos(double x)
    {
        x = Math.Abs(Wrap(x));             // cos is even; now in [0, pi]
        return x > HalfPi ? -CosSeries(Pi - x) : CosSeries(x);
    }

    /// <summary>Angle wrapped into [-pi, pi).</summary>
    public static double Wrap(double x)
    {
        if (x >= -Pi && x < Pi) return x;
        x -= TwoPi * Math.Floor((x + Pi) / TwoPi);
        return x >= Pi ? x - TwoPi : x;
    }

    // Taylor series to x^22: for |x| <= pi/2 the error is below 1e-16.
    private static double CosSeries(double x)
    {
        var x2 = x * x;
        var term = 1.0;
        var sum = 1.0;
        for (var n = 1; n <= 11; n++)
        {
            term *= -x2 / ((2 * n - 1) * (2 * n));
            sum += term;
        }
        return sum;
    }

    // Taylor series to x^23: for |x| <= pi/2 the error is below 1e-16.
    private static double SinSeries(double x)
    {
        var x2 = x * x;
        var term = x;
        var sum = x;
        for (var n = 1; n <= 11; n++)
        {
            term *= -x2 / ((2 * n) * (2 * n + 1));
            sum += term;
        }
        return sum;
    }
}
