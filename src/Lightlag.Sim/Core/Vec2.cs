namespace Lightlag.Sim.Core;

/// <summary>A point or direction on the game plane. Distances are in megametres (Mm, 1000 km).</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static readonly Vec2 Zero = new(0, 0);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    public Vec2 Normalized()
    {
        var len = Length;
        return len > 1e-12 ? new Vec2(X / len, Y / len) : Zero;
    }

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);

    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;
    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}
