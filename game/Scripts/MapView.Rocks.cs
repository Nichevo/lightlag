using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// Close up on an asteroid belt or ring: a few dozen odd-shaped rocks tumble along with it, drifting
/// against each other, and now and then two knock together in a puff of dust. Purely for looks: it uses
/// Godot's own random numbers and the real clock, never the simulation's, and changes nothing in the game.
/// </summary>
public partial class MapView
{
    private sealed class Rock
    {
        public string Belt = "";
        public double Radius, Phase;   // where in the belt it rides (Mm, and its angle at hour 0)
        public Vec2 Offset;            // its own drift away from that, in Mm
        public Vector2 Drift;          // pixels a second
        public float Size, Spin, Turn, Fade;
        public Vector2[] Outline = Array.Empty<Vector2>();
        public Color Color;
        public Vector2 Screen;
    }

    private sealed class Puff
    {
        public Vector2 At;
        public Vector2[] Bits = Array.Empty<Vector2>();
        public float Age;
        public Color Color;
    }

    private const int RocksShown = 45;
    private readonly List<Rock> _rocks = new();
    private readonly List<Puff> _puffs = new();
    private readonly RandomNumberGenerator _rockRng = new();
    private ulong _rockClock;

    /// <summary>Draws the tumbling rocks of any belt or ring that is big on screen (Medium and High only).</summary>
    private void DrawRocks()
    {
        var now = Time.GetTicksMsec();
        var dt = _rockClock == 0 ? 0f : Math.Min(0.1f, (now - _rockClock) / 1000f);
        _rockClock = now;
        var w = Sim!.World;
        var scale = Camera.Scale;
        var any = false;
        foreach (var belt in w.Belts.Where(b => b.Kind != BeltKind.Radiation))
        {
            if (w.FindBody(belt.ParentId) is not Body parent || !_drawn.TryGetValue(parent.Id, out var c)) continue;
            // Only when the belt is wide on screen and some of it is in view.
            var width = (belt.OuterRadius - belt.InnerRadius) * scale;
            var centre = Camera.ToScreen(c);
            var far = Camera.ScreenCenter.DistanceTo(centre) / scale;
            if (width < 120 || far > belt.OuterRadius + Camera.ScreenSize.Length() / scale || far < belt.InnerRadius - Camera.ScreenSize.Length() / scale) continue;
            any = true;
            var mine = _rocks.Where(r => r.Belt == belt.Key).ToList();
            for (var i = mine.Count; i < RocksShown; i++)
                if (NewRock(belt, c, parent.Gm) is Rock fresh) { _rocks.Add(fresh); mine.Add(fresh); }
            foreach (var r in mine)
            {
                var period = Physics.OrbitPeriodHours(r.Radius, parent.Gm);
                var a = r.Phase + Math.Tau * (DrawHour % period) / period;
                r.Offset += new Vec2(r.Drift.X, r.Drift.Y) * (dt / scale);
                r.Screen = Camera.ToScreen(c + new Vec2(Math.Cos(a), Math.Sin(a)) * r.Radius + r.Offset);
                r.Spin += r.Turn * dt;
                r.Fade = Math.Min(1, r.Fade + dt * 1.5f);
            }
            // Knocks: two rocks that touch bounce apart and throw up dust.
            for (var i = 0; i < mine.Count; i++)
            for (var j = i + 1; j < mine.Count; j++)
            {
                var (p, q) = (mine[i], mine[j]);
                var d = p.Screen.DistanceTo(q.Screen);
                if (d >= p.Size + q.Size || d < 0.01f) continue;
                var n = (q.Screen - p.Screen) / d;
                var closing = (p.Drift - q.Drift).Dot(n);
                if (closing <= 0) continue;
                p.Drift -= n * closing;
                q.Drift += n * closing;
                p.Turn = -p.Turn;
                q.Turn = -q.Turn;
                Knock((p.Screen + q.Screen) / 2, p.Color.Lerp(q.Color, 0.5f));
            }
            // Rocks that drift out of view are dropped; new ones fade in where the camera looks.
            _rocks.RemoveAll(r => r.Belt == belt.Key && (!Camera.OnScreen(r.Screen, 60) || r.Offset.Length > (belt.OuterRadius - belt.InnerRadius) * 0.3));
            foreach (var r in mine.Where(_rocks.Contains)) DrawRock(r);
        }
        if (!any) _rocks.Clear();
        DrawPuffs(dt);
    }

    /// <summary>A new rock somewhere in the belt that is on screen, or null if none of the belt is.</summary>
    private Rock? NewRock(Belt belt, Vec2 centre, double gm)
    {
        for (var tries = 0; tries < 12; tries++)
        {
            var screen = new Vector2(_rockRng.Randf() * Camera.ScreenSize.X, _rockRng.Randf() * Camera.ScreenSize.Y);
            var at = Camera.ToWorld(screen) - centre;
            var radius = at.Length;
            if (radius < belt.InnerRadius || radius > belt.OuterRadius) continue;
            var period = Physics.OrbitPeriodHours(radius, gm);
            var angle = Math.Atan2(at.Y, at.X) - Math.Tau * (DrawHour % period) / period;
            var size = 2f + 6f * Mathf.Pow(_rockRng.Randf(), 2.2f);
            var points = 7 + _rockRng.RandiRange(0, 4);
            var outline = new Vector2[points];
            var stretch = 0.6f + 0.4f * _rockRng.Randf();
            for (var k = 0; k < points; k++)
            {
                var t = Mathf.Tau * (k + 0.3f * _rockRng.Randf()) / points;
                var r = 0.65f + 0.35f * _rockRng.Randf();
                outline[k] = new Vector2(Mathf.Cos(t) * r, Mathf.Sin(t) * r * stretch);
            }
            var tint = new Color(belt.Color);
            var shade = 0.55f + 0.45f * _rockRng.Randf();
            return new Rock
            {
                Belt = belt.Key, Radius = radius, Phase = angle, Size = size, Outline = outline,
                Drift = new Vector2(_rockRng.RandfRange(-1, 1), _rockRng.RandfRange(-1, 1)) * (4 + 10 * _rockRng.Randf()),
                Spin = _rockRng.Randf() * Mathf.Tau, Turn = _rockRng.RandfRange(-1.2f, 1.2f),
                Color = new Color(tint.R * shade, tint.G * shade, tint.B * shade), Screen = screen,
            };
        }
        return null;
    }

    private void DrawRock(Rock r)
    {
        var (c, s) = (Mathf.Cos(r.Spin), Mathf.Sin(r.Spin));
        var points = new Vector2[r.Outline.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var o = r.Outline[i] * r.Size;
            points[i] = r.Screen + new Vector2(o.X * c - o.Y * s, o.X * s + o.Y * c);
        }
        var light = new Vector2(c, s).Dot(Vector2.Right) * 0.15f;
        DrawColoredPolygon(points, new Color(r.Color.Lightened(light), 0.9f * r.Fade));
        var rim = new Vector2[points.Length + 1];
        points.CopyTo(rim, 0);
        rim[^1] = points[0];
        DrawPolyline(rim, new Color(r.Color.Darkened(0.45f), 0.8f * r.Fade), 1f, true);
    }

    private void Knock(Vector2 at, Color color)
    {
        if (_puffs.Count > 20) return;
        var bits = new Vector2[10];
        for (var i = 0; i < bits.Length; i++)
            bits[i] = new Vector2(Mathf.Cos(Mathf.Tau * i / bits.Length + _rockRng.Randf()), Mathf.Sin(Mathf.Tau * i / bits.Length + _rockRng.Randf())) * (8 + 18 * _rockRng.Randf());
        _puffs.Add(new Puff { At = at, Bits = bits, Color = color.Lightened(0.3f) });
    }

    private void DrawPuffs(float dt)
    {
        foreach (var p in _puffs)
        {
            p.Age += dt;
            var t = p.Age / 1.6f;
            var alpha = Mathf.Clamp(1 - t, 0, 1);
            DrawCircle(p.At, 3 + 10 * t, new Color(p.Color, 0.25f * alpha));
            foreach (var b in p.Bits) DrawCircle(p.At + b * Mathf.Sqrt(t), 1.3f, new Color(p.Color, 0.8f * alpha));
        }
        _puffs.RemoveAll(p => p.Age > 1.6f);
    }
}
