using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// The look of the system on Medium and High: the shader sun, orbits that fade behind their body (so you
/// see which way it moves), gravity wells as rings, the belt as thousands of drifting specks, moons darkening
/// in their planet's shadow, city lights on settled worlds, and a comet on a long orbit. All of it only
/// draws; none of it touches the simulation. Low keeps the original drawing.
/// </summary>
public partial class MapView
{
    private SunSprite? _sun;
    private readonly Dictionary<string, BeltSpecks> _belts = new();

    /// <summary>The star's shader sprite, kept in step each frame (hidden on Low).</summary>
    private void SyncSun(Body? star)
    {
        if (star == null) return;
        if (_sun == null)
        {
            _sun = SunSprite.For(star);
            AddChild(_sun);
            MoveChild(_sun, 0); // behind the planets
        }
        var at = Camera.ToScreen(_drawn[star.Id]);
        var r = (float)ScreenRadius(star);
        _sun.Visible = Graphics.Fancy && Camera.OnScreen(at, Mathf.Max(r * 5, r + 260));
        if (_sun.Visible) _sun.Set(at, r);
    }

    /// <summary>A moon's share in its planet's shadow (0..1): behind the planet as seen from the star, and inside its width.</summary>
    private double EclipseOf(Body b, Vec2 starAt)
    {
        if (b.Kind != BodyKind.Moon || b.ParentId is not int pid || Sim.World.FindBody(pid) is not Body parent) return 0;
        var p = _drawn[pid];
        var dir = p - starAt;
        if (dir.Length < 1e-9) return 0;
        dir = dir.Normalized();
        var rel = _drawn[b.Id] - p;
        var along = Vec2.Dot(rel, dir);
        if (along <= 0) return 0;
        var off = (rel - dir * along).Length;
        var lo = parent.Radius - b.Radius;
        var hi = parent.Radius + b.Radius;
        return off <= lo ? 1 : off >= hi ? 0 : 1 - (off - lo) / Math.Max(1e-9, hi - lo);
    }

    /// <summary>City lights for a settled world: brighter with more people.</summary>
    private static float CityOf(Body b) =>
        b.Owner != null && b.PopK > 0 ? Mathf.Clamp((float)Math.Log10(1 + b.PopK) / 3f, 0.25f, 1f) : 0f;

    /// <summary>An orbit that fades away behind its body: brightest just behind it, faintest just ahead, so it shows the way the body moves.</summary>
    private void DrawOrbitFading(Vec2 parentWorld, double radiusMm, float alpha, Vec2 bodyWorld, double gapPx)
    {
        var scale = Camera.Scale;
        var rPx = radiusMm * scale;
        if (rPx < 3) return;
        var rel = bodyWorld - parentWorld;
        var bodyAngle = Math.Atan2(rel.Y, rel.X);
        // Orbits run the way the angle grows, so "behind" is the way it shrinks.
        float Fade(double behind) => alpha * (0.2f + 1.9f * Mathf.Pow(1f - (float)Math.Clamp(behind / Math.Tau, 0, 1), 2.4f));
        var diag = Camera.ScreenSize.Length();
        if (rPx < 20000)
        {
            var c = Camera.ToScreen(parentWorld);
            if (!Camera.OnScreen(c, (float)rPx + 10)) return;
            var segments = (int)Math.Clamp(rPx / 2, 48, 720);
            var gap = Math.Min(Math.PI, gapPx / rPx);
            if (gap * rPx < 2) gap = 0;
            var span = Math.Tau - 2 * gap;
            var pts = new Vector2[segments + 1];
            var cols = new Color[segments + 1];
            for (var i = 0; i <= segments; i++)
            {
                var behind = gap + span * i / segments;
                var a = bodyAngle - behind;
                pts[i] = c + new Vector2((float)(Math.Cos(a) * rPx), (float)(Math.Sin(a) * rPx));
                cols[i] = new Color(Ui.Orbit, Mathf.Min(1f, Fade(behind)));
            }
            DrawPolylineColors(pts, cols, 1, true);
            return;
        }
        var toView = Camera.Center - parentWorld;
        if (Math.Abs(toView.Length - radiusMm) * scale > diag) return;
        var mid = Math.Atan2(toView.Y, toView.X);
        var half = Math.Min(Math.PI, diag * 1.2 / rPx);
        const int n = 96;
        var bodyAt = Camera.ToScreen(bodyWorld);
        var run = new List<Vector2>();
        var runCols = new List<Color>();
        void Flush()
        {
            if (run.Count >= 2) DrawPolylineColors(run.ToArray(), runCols.ToArray(), 1, true);
            run.Clear();
            runCols.Clear();
        }
        for (var i = 0; i <= n; i++)
        {
            var a = mid - half + 2 * half * i / n;
            var p = Camera.ToScreen(parentWorld + new Vec2(Math.Cos(a), Math.Sin(a)) * radiusMm);
            if (p.DistanceTo(bodyAt) < gapPx) { Flush(); continue; }
            var behind = ((bodyAngle - a) % Math.Tau + Math.Tau) % Math.Tau;
            run.Add(p);
            runCols.Add(new Color(Ui.Orbit, Mathf.Min(1f, Fade(behind))));
        }
        Flush();
    }

    /// <summary>
    /// Gravity wells as rings (no shading, which would clash with the other map layers): a ring round each
    /// planet where its pull outweighs the star's (its sphere of influence), and fainter rings inside it,
    /// closer together towards the middle.
    /// </summary>
    private void DrawWells()
    {
        var w = Sim.World;
        foreach (var b in w.Bodies)
        {
            if (b.Kind != BodyKind.Planet || b.ParentId is not int pid || w.FindBody(pid) is not Body parent || parent.Gm <= 0 || b.Gm <= 0) continue;
            var soi = b.OrbitRadius * Math.Pow(b.Gm / parent.Gm, 0.4);
            var rPx = (float)(soi * Camera.Scale);
            if (rPx < 30 || rPx > 12000) continue;
            var at = Camera.ToScreen(_drawn[b.Id]);
            if (!Camera.OnScreen(at, rPx)) continue;
            var tint = b.Atmosphere is string a ? new Color(a) : new Color(b.Color).Lightened(0.3f);
            // Fade in as it grows past a few dozen pixels, so zoomed out the map stays clean.
            var fade = Mathf.Clamp((rPx - 30) / 120f, 0, 1);
            // The edge ring is the clearest; inner rings at half and a quarter of the way (the pull 4x and 16x
            // stronger) are fainter, so the well reads as deepening towards the planet.
            foreach (var (k, alpha, width) in new[] { (1f, 0.22f, 1.5f), (0.5f, 0.12f, 1f), (0.25f, 0.09f, 1f) })
            {
                var rr = rPx * k;
                if (rr < 12) continue;
                DrawArc(at, rr, 0, Mathf.Tau, Math.Clamp((int)(rr / 4), 32, 256), new Color(tint, alpha * fade), width, true);
            }
        }
    }

    /// <summary>The belt as one MultiMesh of specks (Medium and High); hides the specks on Low.</summary>
    private void SyncBelts()
    {
        var w = Sim.World;
        foreach (var belt in w.Belts.Where(b => b.Kind != BeltKind.Radiation))
        {
            if (!_belts.TryGetValue(belt.Key, out var specks))
            {
                specks = BeltSpecks.For(belt, Sim.World.Seed);
                _belts[belt.Key] = specks;
                AddChild(specks);
            }
            var parent = w.FindBody(belt.ParentId);
            specks.Visible = Graphics.Fancy && parent != null;
            if (specks.Visible) specks.Place(Camera, _drawn[parent!.Id], parent.Gm, DrawHour);
        }
    }

    // ---- The comet ----------------------------------------------------------------------------

    /// <summary>
    /// A comet on a long ellipse (from the game's seed, purely for looks): a bright head, a blue gas tail
    /// pointing straight away from the star and a curved, yellowish dust tail. Its closest pass to the star
    /// falls somewhere in the first year of the game.
    /// </summary>
    private void DrawComet(Body star)
    {
        if (star.Gm <= 0) return;
        var seed = Sim.World.Seed;
        double R(int n)
        {
            unchecked
            {
                var x = seed * 0x9E3779B97F4A7C15UL + (ulong)n * 0xD1B54A32D192ED03UL;
                x = (x ^ (x >> 31)) * 0xBF58476D1CE4E5B9UL;
                x ^= x >> 29;
                return (x >> 11) / (double)(1UL << 53);
            }
        }
        var au = Physics.MmPerAu;
        var rp = (0.45 + 0.4 * R(1)) * au;
        var ra = (9 + 6 * R(2)) * au;
        var a = (rp + ra) / 2;
        var e = (ra - rp) / (ra + rp);
        var n = Math.Sqrt(star.Gm / (a * a * a)); // radians per hour
        var periHour = (60 + 300 * R(3)) * 24;
        var omega = R(4) * Math.Tau;
        var dir = R(5) < 0.5 ? 1 : -1;
        var meanAnomaly = n * (DrawHour - periHour);
        meanAnomaly = Math.IEEERemainder(meanAnomaly, Math.Tau);
        var E = meanAnomaly + 0.85 * e * Math.Sign(Math.Sin(meanAnomaly));
        for (var i = 0; i < 30; i++)
        {
            var f = E - e * Math.Sin(E) - meanAnomaly;
            E -= f / (1 - e * Math.Cos(E));
        }
        var nu = 2 * Math.Atan2(Math.Sqrt(1 + e) * Math.Sin(E / 2), Math.Sqrt(1 - e) * Math.Cos(E / 2));
        var r = a * (1 - e * Math.Cos(E));
        var ang = omega + dir * nu;
        var sun = _drawn[star.Id];
        var pos = sun + new Vec2(Math.Cos(ang), Math.Sin(ang)) * r;
        var head = Camera.ToScreen(pos);
        // Which way it is moving (for the dust tail's curve).
        var ang2 = omega + dir * (nu + 0.001);
        var vel = (new Vec2(Math.Cos(ang2), Math.Sin(ang2)) - new Vec2(Math.Cos(ang), Math.Sin(ang))).Normalized();

        var rAu = r / au;
        var tailMm = Math.Min(0.6, 0.22 / (rAu * rAu)) * au;
        var tailPx = (float)(tailMm * Camera.Scale);
        var away = (pos - sun).Normalized();
        var awayS = new Vector2((float)away.X, (float)away.Y);
        var velS = new Vector2((float)vel.X, (float)vel.Y);
        if (!Camera.OnScreen(head, Mathf.Max(tailPx, 20) + 20)) return;
        var activity = Mathf.Clamp((float)(3.5 / rAu - 0.6), 0.12f, 1f);
        if (tailPx > 4)
        {
            // Dust tail: wider, curving back along the path.
            DrawTail(head, s => awayS * s - velS * s * s * 0.35f, tailPx * 0.8f, tailPx * 0.16f, new Color(1f, 0.92f, 0.75f, 0.32f * activity));
            // Gas tail: straight and thin, faint blue.
            DrawTail(head, s => awayS * s, tailPx, tailPx * 0.05f, new Color(0.6f, 0.8f, 1f, 0.38f * activity));
        }
        var coma = Mathf.Clamp((float)(0.012 * au * Camera.Scale) * activity, 5f, 60f);
        DrawTextureRect(Graphics.SoftDot, new Rect2(head - Vector2.One * coma, Vector2.One * coma * 2), false, new Color(0.8f, 0.92f, 1f, 0.55f * activity + 0.2f));
        DrawCircle(head, 1.6f, Graphics.Hot(new Color(1f, 1f, 1f), 2.5f));
        if (Camera.Scale * au > 300)
            DrawString(_font, head + new Vector2(8, -8), "comet", HorizontalAlignment.Left, -1, 11, new Color(Ui.Dim, 0.7f));
    }

    /// <summary>A tail: a strip from the head out along a curve, widening and fading.</summary>
    private void DrawTail(Vector2 head, Func<float, Vector2> along, float length, float width, Color color)
    {
        const int n = 16;
        var left = new Vector2[n + 1];
        var right = new Vector2[n + 1];
        for (var i = 0; i <= n; i++)
        {
            var s = (float)i / n;
            var p = head + along(s) * length;
            var ahead = head + along(Mathf.Min(1, s + 0.02f)) * length - (head + along(Mathf.Max(0, s - 0.02f)) * length);
            var across = new Vector2(-ahead.Y, ahead.X).Normalized();
            var wdt = width * (0.15f + 0.85f * Mathf.Sqrt(s));
            left[i] = p + across * wdt;
            right[i] = p - across * wdt;
        }
        for (var i = 0; i < n; i++)
        {
            var a0 = color.A * (1 - (float)i / n);
            var a1 = color.A * (1 - (float)(i + 1) / n);
            var c0 = new Color(color, a0);
            var c1 = new Color(color, a1);
            DrawPolygon(new[] { left[i], left[i + 1], right[i + 1], right[i] }, new[] { c0, c1, c1, c0 });
        }
    }
}

/// <summary>
/// One belt as thousands of tiny specks in a single MultiMesh, each on its own orbit at its own speed.
/// Positions are worked out each frame on the CPU (in double precision, so close up they stay put).
/// </summary>
public partial class BeltSpecks : MultiMeshInstance2D
{
    private const int Count = 5000;
    private double[] _radius = Array.Empty<double>();
    private double[] _phase = Array.Empty<double>();
    private float[] _size = Array.Empty<float>();
    private Color[] _color = Array.Empty<Color>();
    private double _gm = -1;
    private double[] _period = Array.Empty<double>();

    public static BeltSpecks For(Belt belt, ulong seed)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            Mesh = new QuadMesh { Size = Vector2.One },
            InstanceCount = Count,
            VisibleInstanceCount = 0,
        };
        var s = new BeltSpecks { Multimesh = mm, Texture = Graphics.SoftDot, ShowBehindParent = true, Name = $"Belt-{belt.Key}" };
        var rng = new RandomNumberGenerator { Seed = seed ^ 0xBE17UL };
        s._radius = new double[Count];
        s._phase = new double[Count];
        s._size = new float[Count];
        s._color = new Color[Count];
        var tint = new Color(belt.Color);
        for (var i = 0; i < Count; i++)
        {
            // Thicker in the middle of the belt, thinning to the edges.
            var u = (rng.Randf() + rng.Randf() + rng.Randf()) / 3.0;
            s._radius[i] = belt.InnerRadius + (belt.OuterRadius - belt.InnerRadius) * u;
            s._phase[i] = rng.Randf() * Math.Tau;
            s._size[i] = 1.6f + 3.2f * Mathf.Pow(rng.Randf(), 3f);
            var shade = 0.7f + 0.5f * rng.Randf();
            s._color[i] = new Color(tint.R * shade, tint.G * shade, tint.B * shade, 0.35f + 0.45f * rng.Randf());
        }
        return s;
    }

    public void Place(MapCamera camera, Vec2 center, double gm, double hours)
    {
        if (gm != _gm)
        {
            _gm = gm;
            _period = new double[Count];
            for (var i = 0; i < Count; i++) _period[i] = Physics.OrbitPeriodHours(_radius[i], gm);
        }
        var mm = Multimesh;
        // Specks grow a little as you zoom in, but stay specks.
        var grow = (float)Math.Clamp(0.7 + camera.Scale * 600, 0.7, 2.2);
        var shown = 0;
        for (var i = 0; i < Count; i++)
        {
            var a = _phase[i] + Math.Tau * (hours % _period[i]) / _period[i];
            var p = camera.ToScreen(center + new Vec2(Math.Cos(a), Math.Sin(a)) * _radius[i]);
            if (!camera.OnScreen(p, 4)) continue;
            var size = _size[i] * grow;
            mm.SetInstanceTransform2D(shown, new Transform2D(0, new Vector2(size, size), 0, p));
            mm.SetInstanceColor(shown, _color[i]);
            shown++;
        }
        mm.VisibleInstanceCount = shown;
    }
}
