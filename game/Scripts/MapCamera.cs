using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// Where the map is looking. Works in world units (Mm) with double precision and a logarithmic zoom,
/// so it can go smoothly from the whole system (millions of Mm) down to a moon a few km across.
/// </summary>
public sealed class MapCamera
{
    public const double MinScale = 1e-4;   // pixels per Mm: the whole system and then some
    public const double MaxScale = 5000;   // pixels per Mm: a 10 km moon is 50 px across

    public Vec2 Center;                    // world point at the middle of the screen
    public Vector2 ScreenSize = new(1600, 900);
    private double _logScale = Math.Log(0.002);
    private double _targetLogScale = Math.Log(0.002);
    private Vec2? _anchorWorld;            // keep this world point under _anchorScreen while zooming
    private Vector2 _anchorScreen;

    public double Scale => Math.Exp(_logScale);
    public double TargetScale => Math.Exp(_targetLogScale);
    public Vector2 ScreenCenter => ScreenSize / 2;

    public Vector2 ToScreen(Vec2 world)
    {
        var s = Scale;
        var x = (world.X - Center.X) * s + ScreenCenter.X;
        var y = (world.Y - Center.Y) * s + ScreenCenter.Y;
        // Keep far-off points inside float range; they are off screen anyway.
        return new Vector2((float)Math.Clamp(x, -1e7, 1e7), (float)Math.Clamp(y, -1e7, 1e7));
    }

    public Vec2 ToWorld(Vector2 screen)
    {
        var s = Scale;
        return new Vec2(Center.X + (screen.X - ScreenCenter.X) / s, Center.Y + (screen.Y - ScreenCenter.Y) / s);
    }

    public bool OnScreen(Vector2 p, float margin) =>
        p.X > -margin && p.Y > -margin && p.X < ScreenSize.X + margin && p.Y < ScreenSize.Y + margin;

    /// <summary>Zoom in (steps &gt; 0) or out around a screen point.</summary>
    public void ZoomAt(Vector2 screen, double steps)
    {
        _targetLogScale = Math.Clamp(_targetLogScale + steps * 0.25, Math.Log(MinScale), Math.Log(MaxScale));
        _anchorWorld = ToWorld(screen);
        _anchorScreen = screen;
    }

    public void SetScale(double scale, bool instant = false)
    {
        _targetLogScale = Math.Log(Math.Clamp(scale, MinScale, MaxScale));
        _anchorWorld = null;
        if (instant) _logScale = _targetLogScale;
    }

    /// <summary>Scale so a circle of this radius (Mm) fits on screen.</summary>
    public void FitRadius(double radiusMm, bool instant = false) =>
        SetScale(Math.Min(ScreenSize.X, ScreenSize.Y) * 0.45 / Math.Max(radiusMm, 1e-6), instant);

    public void Pan(Vector2 screenDelta)
    {
        var s = Scale;
        Center = new Vec2(Center.X - screenDelta.X / s, Center.Y - screenDelta.Y / s);
        _anchorWorld = null;
    }

    /// <summary>Animate towards the target zoom. When following, the followed point stays centred.</summary>
    public void Update(double delta, Vec2? follow)
    {
        var t = Math.Min(1.0, delta * 12);
        _logScale += (_targetLogScale - _logScale) * t;
        if (Math.Abs(_targetLogScale - _logScale) < 1e-4) _logScale = _targetLogScale;

        if (follow is Vec2 f)
        {
            Center = f;
        }
        else if (_anchorWorld is Vec2 a)
        {
            // Move the centre so the anchor stays under the cursor at the new scale.
            var s = Scale;
            Center = new Vec2(a.X - (_anchorScreen.X - ScreenCenter.X) / s, a.Y - (_anchorScreen.Y - ScreenCenter.Y) / s);
            if (_logScale == _targetLogScale) _anchorWorld = null;
        }
    }

    /// <summary>A round distance that is about this many pixels long, with a label like "20,000 km" or "2 AU".</summary>
    public (double Mm, string Label) NiceDistance(double pixels)
    {
        var mm = pixels / Scale;
        var km = mm * 1000;
        if (mm >= Physics.MmPerAu * 0.1)
        {
            var au = Nice(mm / Physics.MmPerAu);
            return (au * Physics.MmPerAu, $"{au:0.#} AU");
        }
        var k = Nice(km);
        return (k / 1000, $"{k:#,0.##} km");
    }

    private static double Nice(double v)
    {
        var p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var m = v / p;
        return (m >= 5 ? 5 : m >= 2 ? 2 : 1) * p;
    }
}
