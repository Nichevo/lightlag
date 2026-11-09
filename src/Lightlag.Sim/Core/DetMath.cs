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

    /// <summary>Angle of the point (x, y), in (-pi, pi], like Math.Atan2.</summary>
    public static double Atan2(double y, double x)
    {
        if (x == 0 && y == 0) return 0;
        var ax = Math.Abs(x);
        var ay = Math.Abs(y);
        var a = ay <= ax ? Atan01(ay / ax) : HalfPi - Atan01(ax / ay);
        if (x < 0) a = Pi - a;
        return y < 0 || (y == 0 && double.IsNegative(y)) ? -a : a;
    }

    /// <summary>Cube root (Newton steps, + - * / only).</summary>
    public static double Cbrt(double v)
    {
        if (v == 0 || !double.IsFinite(v)) return v;
        var neg = v < 0;
        v = Math.Abs(v);
        var x = v > 1 ? Math.Sqrt(v) : 1.0; // above the root, so Newton comes down to it steadily
        for (var i = 0; i < 200; i++)
        {
            var next = x - (x * x * x - v) / (3 * x * x);
            if (next >= x) break;
            x = next;
        }
        return neg ? -x : x;
    }

    private const double Sqrt3 = 1.7320508075688772;
    private const double SixthPi = 0.5235987755982988;
    private const double TanTwelfthPi = 0.2679491924311227;

    // atan for 0 <= x <= 1.
    private static double Atan01(double x)
    {
        if (x <= TanTwelfthPi) return AtanSeries(x);
        // atan(x) = pi/6 + atan((x*sqrt3 - 1) / (x + sqrt3)) brings x down below tan(pi/12).
        return SixthPi + AtanSeries((x * Sqrt3 - 1) / (x + Sqrt3));
    }

    // Series to x^35: for |x| <= tan(pi/12) the error is below 1e-20.
    private static double AtanSeries(double x)
    {
        var x2 = x * x;
        var power = x;
        var sum = x;
        for (var n = 1; n <= 17; n++)
        {
            power *= -x2;
            sum += power / (2 * n + 1);
        }
        return sum;
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
