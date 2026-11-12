using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Engines and battle effects on Medium and High: engine plumes whose length follows the burn (Gentle,
/// Cruise, Hard), a red glow on hot ships, running lights close up, glowing railgun tracers with their
/// slugs, missile smoke trails, point-defence flak bursts, a flash and drifting debris on each hit, and a
/// shimmer where a shield takes it. Everything here is drawing only, timed by the game clock, so pausing
/// freezes it.
/// </summary>
public partial class MapView
{
    private FxQuads? _plumes;

    /// <summary>Bits of debris thrown off by hits: where and when, how they drift and spin.</summary>
    private readonly List<Debris> _debris = new();
    private readonly HashSet<CombatEffect> _seenEffects = new();
    private readonly Dictionary<int, List<(Vec2 At, double Hour)>> _smoke = new();
    private double _lastFxHour = double.NaN;

    private sealed record Debris(Vec2 At, Vec2 Drift, Vector2 Fling, double Hour, float Spin, float Size, Color Color);

    private const double EffectLife = 1.0 / 60;     // a game minute
    private const double DebrisLife = 8.0 / 60;     // eight game minutes
    private const double SmokeLife = 3.0 / 60;      // three game minutes
    private const double SmokeEvery = 4.0 / 3600;   // a puff every four game seconds

    private void EnsurePlumes()
    {
        if (_plumes != null) return;
        _plumes = FxQuads.For("res://Shaders/plume.gdshader");
        AddChild(_plumes);
    }

    /// <summary>Start of a frame's drawing: clear the plume batch.</summary>
    private void BeginFx()
    {
        EnsurePlumes();
        _plumes!.Clear();
        // A load or a replay jumping back: forget effects from the future.
        if (!double.IsNaN(_lastFxHour) && DrawHour < _lastFxHour - 1e-6)
        {
            _debris.Clear();
            _smoke.Clear();
            _seenEffects.Clear();
        }
        _lastFxHour = DrawHour;
    }

    private void EndFx()
    {
        _plumes!.Visible = Graphics.Fancy;
        _plumes.Flush(Graphics.Bloom ? 1.8f : 1.0f);
    }

    /// <summary>How hard a fleet's engines burn: the flight's speed setting (Gentle 0.4, Cruise 0.7, Hard 1).</summary>
    private static float BurnLevel(Fleet f) => (float)Math.Clamp(f.Flight?.SpeedFraction is double s && s > 0 ? s : 1, 0.2, 1);

    /// <summary>A plume out of the back of a ship at a screen point.</summary>
    private void AddPlume(Vector2 at, float heading, float length, float width, float burn, int seed)
    {
        if (!Graphics.Fancy || _plumes == null) return;
        var back = new Vector2(-Mathf.Cos(heading), -Mathf.Sin(heading));
        _plumes.Add(at, back, length, width, new Color(0.55f + 0.45f * burn, (seed * 0.618034f) % 1f, burn > 0.9f ? 1f : 0f, 1f));
    }

    /// <summary>Icon-sized plume behind a ship, its length following the burn.</summary>
    private void IconPlume(Vector2 at, Ship s, float heading, Fleet f)
    {
        var burn = BurnLevel(f);
        var length = 8f + 26f * burn;
        AddPlume(at - new Vector2(Mathf.Cos(heading), Mathf.Sin(heading)) * 5f, heading, length, 6f + 4f * burn, burn, s.Id);
    }

    /// <summary>A soft red glow on a ship running hot (from three quarters of its heat capacity), brightest when overheated.</summary>
    private void DrawHeatGlow(Vector2 at, Ship s, float radius)
    {
        if (!Graphics.Fancy) return;
        var cap = Heat.Capacity(s);
        var share = s.Overheated ? 1.2f : cap > 0 ? (float)(s.Heat / cap) : 0f;
        if (share < 0.6f) return;
        var k = Mathf.Clamp((share - 0.6f) / 0.5f, 0, 1);
        var pulse = 0.85f + 0.15f * Mathf.Sin((float)Time.GetTicksMsec() / 260f + s.Id);
        var r = radius * (1.4f + 0.4f * k);
        DrawTextureRect(Graphics.SoftDot, new Rect2(at - Vector2.One * r, Vector2.One * r * 2), false, Graphics.Hot(new Color(1f, 0.25f, 0.1f, 0.55f * k * pulse), 1.6f));
    }

    /// <summary>Running lights on a ship drawing: red to port, green to starboard, a white strobe at the stern.</summary>
    private void DrawRunningLights(float hullPx, float height, int seed)
    {
        if (!Graphics.Fancy) return;
        var ms = Time.GetTicksMsec() + (ulong)(seed * 137);
        var blink = ms % 1600 < 900;
        var strobe = ms % 2300 < 90;
        var r = Mathf.Clamp(hullPx * 0.012f, 1.2f, 3.5f);
        var side = height * 0.36f;
        if (blink)
        {
            DrawCircle(new Vector2(hullPx * 0.05f, -side), r, Graphics.Hot(new Color(1f, 0.2f, 0.2f), 2f));
            DrawCircle(new Vector2(hullPx * 0.05f, side), r, Graphics.Hot(new Color(0.2f, 1f, 0.4f), 2f));
        }
        if (strobe) DrawCircle(new Vector2(-hullPx * 0.5f, 0), r * 1.3f, Graphics.Hot(Colors.White, 3f));
        DrawCircle(new Vector2(hullPx * 0.48f, 0), r * 0.7f, new Color(1f, 1f, 1f, 0.6f));
    }

    // ---- Battle effects ---------------------------------------------------------------------------

    private static double H(int n, int k)
    {
        unchecked
        {
            var x = (uint)(n * 73856093 ^ k * 19349663) * 0x9E3779B1u;
            x ^= x >> 15; x *= 0x85EBCA77u; x ^= x >> 13;
            return x / 4294967296.0;
        }
    }

    /// <summary>Notice new hits: throw off debris (once per hit).</summary>
    private void SpawnFromEffects()
    {
        _seenEffects.RemoveWhere(e => DrawHour - e.Hour > EffectLife * 4);
        foreach (var e in Sim.Effects)
        {
            if (e.Kind is not (EffectKind.RailHit or EffectKind.MissileHit)) continue;
            if (DrawHour < e.Hour || DrawHour - e.Hour > EffectLife || !_seenEffects.Add(e)) continue;
            var drift = NearestShip(e.To) is Ship hit && Sim.World.FleetOf(hit) is Fleet f ? f.Velocity : Vec2.Zero;
            var key = e.GetHashCode();
            var pieces = e.Kind == EffectKind.MissileHit ? 10 : 6;
            for (var i = 0; i < pieces; i++)
            {
                var a = (float)(H(key, i) * Math.Tau);
                var speed = 8f + 26f * (float)H(key, i + 50);
                var grey = 0.45f + 0.4f * (float)H(key, i + 90);
                var hot = H(key, i + 70) < 0.3;
                _debris.Add(new Debris(e.To, drift, new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * speed, e.Hour,
                    (float)(H(key, i + 20) - 0.5) * 8f, 1.2f + 2.2f * (float)H(key, i + 30),
                    hot ? new Color(1f, 0.6f, 0.25f) : new Color(grey, grey, grey * 1.05f)));
            }
        }
        _debris.RemoveAll(d => DrawHour - d.Hour > DebrisLife || DrawHour < d.Hour);
        if (_debris.Count > 1500) _debris.RemoveRange(0, _debris.Count - 1500);
    }

    /// <summary>The ship nearest a world point (within a few contact ranges), for whose hull or shield took a hit.</summary>
    private Ship? NearestShip(Vec2 at)
    {
        Ship? best = null;
        var bestD = 50.0; // Mm
        foreach (var s in Sim.World.Ships)
        {
            var p = _drawn.TryGetValue(s.Id, out var dp) ? dp : s.Position;
            var d = Vec2.Distance(p, at);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    /// <summary>The fancy battle effects (replaces DrawEffects on Medium and High).</summary>
    private void DrawEffectsFancy()
    {
        SpawnFromEffects();
        var white = Graphics.Hot(new Color(1f, 0.97f, 0.9f), 2.5f);
        foreach (var e in Sim.Effects)
        {
            var age = DrawHour - e.Hour;
            if (age < -1e-9 || age > EffectLife) continue;
            var t = (float)(age / EffectLife);
            var fade = 1 - t;
            var mine = e.Side == Side.Player ? Ui.Player : Ui.Rival;
            var from = Camera.ToScreen(e.From);
            var to = Camera.ToScreen(e.To);
            if (!Camera.OnScreen(to, 200) && !Camera.OnScreen(from, 200)) continue;
            var key = e.GetHashCode();
            switch (e.Kind)
            {
                case EffectKind.RailHit:
                case EffectKind.RailMiss:
                {
                    var hit = e.Kind == EffectKind.RailHit;
                    // The tracer: a wide soft glow, a narrower one and a bright core, all fading.
                    var k = hit ? 1f : 0.5f;
                    DrawLine(from, to, new Color(mine, 0.07f * fade * k), 7f, true);
                    DrawLine(from, to, new Color(mine, 0.22f * fade * k), 3f, true);
                    DrawLine(from, to, Graphics.Hot(new Color(mine.Lightened(0.5f), 0.85f * fade * k), 1.8f), 1.1f, true);
                    // The slug itself: a bright streak racing down the line in the first third.
                    var p = Mathf.Clamp(t / 0.3f, 0, 1);
                    if (p < 1)
                    {
                        var dir = (to - from).Normalized();
                        var head = from.Lerp(to, p);
                        DrawLine(head - dir * 16, head, white, 2f, true);
                    }
                    else if (hit)
                    {
                        // The hit: a flash that blooms and shrinks.
                        var f2 = Mathf.Clamp((t - 0.3f) / 0.7f, 0, 1);
                        var r = 4 + 16 * Mathf.Sqrt(f2);
                        DrawTextureRect(Graphics.SoftDot, new Rect2(to - Vector2.One * r, Vector2.One * r * 2), false,
                            Graphics.Hot(new Color(1f, 0.85f, 0.55f, 0.95f * (1 - f2)), 2.2f));
                        DrawCircle(to, 2.5f * (1 - f2) + 0.5f, white);
                        DrawShieldShimmer(e, (float)(1 - f2), key);
                    }
                    break;
                }
                case EffectKind.PointDefenceKill:
                {
                    // Tracer dashes racing out, then a burst of flak: puffs and sparks round the kill.
                    var dir = (to - from).Normalized();
                    var len = from.DistanceTo(to);
                    for (var i = 0; i < 4; i++)
                    {
                        var d = ((t * 2.2f + i * 0.25f) % 1f) * len;
                        DrawLine(from + dir * d, from + dir * Mathf.Min(len, d + 7), new Color(mine.Lightened(0.4f), 0.75f * fade), 1.2f, true);
                    }
                    for (var i = 0; i < 7; i++)
                    {
                        var a = (float)(H(key, i) * Math.Tau);
                        var dist = 3 + 11 * (float)H(key, i + 9);
                        var at = to + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * dist * (0.4f + 0.6f * t);
                        var r = 2 + 5 * t;
                        DrawTextureRect(Graphics.SoftDot, new Rect2(at - Vector2.One * r, Vector2.One * r * 2), false, new Color(0.85f, 0.85f, 0.8f, 0.5f * fade));
                        if (t < 0.4f) DrawCircle(at, 1.1f, Graphics.Hot(new Color(1f, 0.85f, 0.5f, 1 - t / 0.4f), 2f));
                    }
                    DrawArc(to, 3 + 9 * t, 0, Mathf.Tau, 14, new Color(1, 1, 1, 0.7f * fade), 1.2f, true);
                    break;
                }
                case EffectKind.Volley:
                {
                    var dir = (to - from).Normalized();
                    var across = new Vector2(-dir.Y, dir.X);
                    var half = Mathf.Max((float)(e.Width * Camera.Scale) / 2, e.Width > 1 ? 6f : 1.5f);
                    var tip = to + dir * half * 0.5f;
                    var c = e.Side != Side.Player ? new Color(1f, 0.4f, 0.35f) : mine;
                    DrawColoredPolygon(new[] { from, tip + across * half, tip - across * half }, new Color(c, 0.1f * fade));
                    DrawLine(from, tip + across * half, new Color(c, 0.35f * fade), 1, true);
                    DrawLine(from, tip - across * half, new Color(c, 0.35f * fade), 1, true);
                    break;
                }
                case EffectKind.MissileHit:
                {
                    // A fireball, a shock ring racing out, and a shield shimmer if one took the blast.
                    var r = 6 + 26 * Mathf.Sqrt(t);
                    DrawTextureRect(Graphics.SoftDot, new Rect2(to - Vector2.One * r, Vector2.One * r * 2), false,
                        Graphics.Hot(new Color(1f, 0.55f, 0.2f, 0.9f * fade), 2.4f));
                    var core = 3 + 8 * (1 - t);
                    DrawTextureRect(Graphics.SoftDot, new Rect2(to - Vector2.One * core, Vector2.One * core * 2), false, Graphics.Hot(new Color(1f, 0.95f, 0.8f, fade), 3f));
                    DrawArc(to, 4 + 40 * t, 0, Mathf.Tau, 32, new Color(1f, 0.85f, 0.6f, 0.5f * fade), 1.5f, true);
                    DrawShieldShimmer(e, fade, key);
                    break;
                }
                default:
                    DrawNewEffect(e, from, to, fade);
                    break;
            }
        }
        DrawDebris();
    }

    /// <summary>A flicker of cyan arcs on the side of a shielded ship facing the shot.</summary>
    private void DrawShieldShimmer(CombatEffect e, float strength, int key)
    {
        if (NearestShip(e.To) is not Ship s || s.Shield <= 0) return;
        var at = Camera.ToScreen(_drawn.TryGetValue(s.Id, out var p) ? p : s.Position);
        var facing = (e.From - (_drawn.TryGetValue(s.Id, out var q) ? q : s.Position));
        var mid = (float)Math.Atan2(facing.Y, facing.X);
        var r = CloseUp && GameData.ShipClasses.TryGetValue(s.ClassId, out var cls)
            ? Mathf.Clamp((float)(Fleets.FormationSpacing * Camera.Scale) * 0.75f * (float)(cls.LengthM / 260.0), 10f, 900f) * 0.62f
            : 13f;
        var ms = Time.GetTicksMsec();
        for (var i = 0; i < 3; i++)
        {
            var flick = 0.6f + 0.4f * Mathf.Sin(ms / 40f + i * 2.1f + key);
            var rr = r * (1 + 0.06f * i);
            var span = 0.9f - 0.2f * i;
            DrawArc(at, rr, mid - span, mid + span, 24, Graphics.Hot(new Color(0.37f, 0.88f, 0.9f, 0.55f * strength * flick), 1.6f), 1.6f - 0.4f * i, true);
        }
        // A few hexagon cells lit where it struck.
        for (var i = 0; i < 4; i++)
        {
            var a = mid + (float)(H(key, i + 200) - 0.5) * 1.2f;
            var c = at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            var size = Mathf.Max(2.5f, r * 0.12f);
            var hex = new Vector2[7];
            for (var k = 0; k < 7; k++) hex[k] = c + new Vector2(Mathf.Cos(k * Mathf.Pi / 3), Mathf.Sin(k * Mathf.Pi / 3)) * size;
            DrawPolyline(hex, new Color(0.6f, 0.95f, 1f, 0.5f * strength), 1, true);
        }
    }

    private void DrawDebris()
    {
        foreach (var d in _debris)
        {
            var age = DrawHour - d.Hour;
            var t = (float)(age / DebrisLife);
            // Flung out fast, then coasting; drifts on with the ship it came off.
            var fling = d.Fling * (1 - Mathf.Exp(-t * 6f)) * 1.4f;
            var at = Camera.ToScreen(d.At + d.Drift * age) + fling;
            if (!Camera.OnScreen(at, 10)) continue;
            var fade = 1 - t * t;
            var c = d.Color.R > 0.9f && d.Color.G < 0.7f
                ? Graphics.Hot(new Color(d.Color, fade * Mathf.Max(0, 1 - t * 3)), 1.8f) // hot sparks cool off quickly
                : new Color(d.Color, 0.85f * fade);
            if (c.A <= 0.01f) continue;
            var a = d.Spin * t;
            var u = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * d.Size;
            var v = new Vector2(-u.Y, u.X) * 0.5f;
            DrawColoredPolygon(new[] { at + u, at + v, at - u * 0.7f, at - v * 1.3f }, c);
        }
    }

    /// <summary>Smoke behind missiles: puffs left in space every few game seconds, spreading and fading.</summary>
    private void DrawMissileSmoke()
    {
        foreach (var m in Sim.World.Missiles)
        {
            if (!_smoke.TryGetValue(m.Id, out var list)) _smoke[m.Id] = list = new();
            if (list.Count == 0 || DrawHour - list[^1].Hour >= SmokeEvery)
            {
                var p = m.Position;
                if (_prevMissile.TryGetValue(m.Id, out var prev)) p = prev + (p - prev) * Blend;
                list.Add((p, DrawHour));
            }
        }
        foreach (var (id, list) in _smoke.ToList())
        {
            list.RemoveAll(x => DrawHour - x.Hour > SmokeLife || x.Hour > DrawHour + 1e-9);
            if (list.Count == 0) { _smoke.Remove(id); continue; }
            Vector2? last = null;
            foreach (var (at, hour) in list)
            {
                var s = Camera.ToScreen(at);
                var t = (float)((DrawHour - hour) / SmokeLife);
                if (Camera.OnScreen(s, 20))
                {
                    var r = 1.5f + 6f * t;
                    DrawTextureRect(Graphics.SoftDot, new Rect2(s - Vector2.One * r, Vector2.One * r * 2), false, new Color(0.8f, 0.82f, 0.86f, 0.35f * (1 - t)));
                    if (last is Vector2 l && l.DistanceTo(s) < 80) DrawLine(l, s, new Color(0.85f, 0.85f, 0.9f, 0.25f * (1 - t)), 1, true);
                }
                last = s;
            }
        }
    }
}
