using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Draws the system: orbits, belt, star, planets, moons, fleets and their routes. Read-only: it never changes the
/// simulation. Between simulation ticks (one game hour) it blends from the previous tick to the
/// current one, so motion looks smooth at every speed.
/// </summary>
public partial class MapView : Node2D
{
    private const int BeltDots = 1200;

    public Simulation Sim = null!;
    public MapCamera Camera = null!;
    public int? SelectedId;
    public int? HoverId;

    /// <summary>The route the planner is showing, if any.</summary>
    public FlightPlan? Preview;

    /// <summary>0..1: how far between the previous tick and the current one to draw.</summary>
    public double Blend = 1;

    private readonly Dictionary<int, double> _prevAngle = new();
    private readonly Dictionary<int, Vec2> _prevShip = new();
    private readonly Dictionary<int, Vec2> _prevFleet = new();
    private readonly Dictionary<int, Vec2> _drawn = new();
    private Font _font = null!;

    public override void _Ready() => _font = ThemeDB.FallbackFont;

    /// <summary>Call just before each simulation step, to remember where things were.</summary>
    public void BeforeStep()
    {
        foreach (var b in Sim.World.Bodies) _prevAngle[b.Id] = b.Angle;
        foreach (var s in Sim.World.Ships) _prevShip[s.Id] = s.Position;
        foreach (var f in Sim.World.Fleets) _prevFleet[f.Id] = f.Position;
    }

    /// <summary>Forget the previous tick (after loading, so nothing blends from the old game).</summary>
    public void ResetBlend()
    {
        _prevAngle.Clear();
        _prevShip.Clear();
        _prevFleet.Clear();
        Blend = 1;
    }

    /// <summary>Where a body, ship or fleet is drawn this frame (blended), in world units.</summary>
    public Vec2? DrawnPosition(int id) => _drawn.TryGetValue(id, out var p) ? p : null;

    public double ScreenRadius(Body b) => Math.Max(MinRadius(b), b.Radius * Camera.Scale);

    private static float MinRadius(Body b) => b.Kind switch
    {
        BodyKind.Star => 7f,
        BodyKind.Moon => 2.5f,
        _ => b.Type is "gas" or "ice-giant" ? 5f : 4f,
    };

    /// <summary>A moon whose whole orbit is a few pixels across sits on its planet: hide it.</summary>
    public bool IsVisible(Body b) => b.Kind != BodyKind.Moon || b.OrbitRadius * Camera.Scale >= 7;

    /// <summary>Works out where everything is this frame. Call before drawing and before hit-testing.</summary>
    public void UpdatePositions()
    {
        _drawn.Clear();
        var w = Sim.World;
        var t = Blend;
        foreach (var b in w.Bodies)
        {
            if (b.ParentId is not int pid || !_drawn.TryGetValue(pid, out var parentPos))
            {
                _drawn[b.Id] = b.Position;
                continue;
            }
            var angle = b.Angle;
            if (_prevAngle.TryGetValue(b.Id, out var prev))
            {
                var d = b.Angle - prev;
                if (d < -Math.PI) d += Math.Tau;
                if (d > Math.PI) d -= Math.Tau;
                angle = prev + d * t;
            }
            _drawn[b.Id] = parentPos + new Vec2(Math.Cos(angle), Math.Sin(angle)) * b.OrbitRadius;
        }
        foreach (var s in w.Ships)
        {
            var p = s.Position;
            if (_prevShip.TryGetValue(s.Id, out var prev) && prev.IsFinite && p.IsFinite)
                p = prev + (p - prev) * t;
            _drawn[s.Id] = p;
        }
        foreach (var f in w.Fleets)
        {
            var p = f.Position;
            if (_prevFleet.TryGetValue(f.Id, out var prev) && prev.IsFinite && p.IsFinite && Vec2.Distance(prev, p) < 1e7)
                p = prev + (p - prev) * t;
            _drawn[f.Id] = p;
        }
    }

    public override void _Draw()
    {
        if (Sim == null) return;
        var w = Sim.World;
        var star = w.Bodies.FirstOrDefault(b => b.ParentId == null);

        foreach (var belt in w.Belts) DrawBelt(belt);

        foreach (var b in w.Bodies)
        {
            if (b.ParentId is int pid && IsVisible(b))
                DrawOrbit(_drawn[pid], b.OrbitRadius, b.Kind == BodyKind.Moon ? 0.16f : 0.22f);
        }

        foreach (var b in w.Bodies)
        {
            if (!IsVisible(b)) continue;
            var at = Camera.ToScreen(_drawn[b.Id]);
            var r = (float)ScreenRadius(b);
            if (!Camera.OnScreen(at, r + 200)) continue;
            if (b.Kind == BodyKind.Star) DrawStar(b, at, r);
            else DrawPlanet(b, at, r, star != null ? _drawn[star.Id] : Vec2.Zero);
        }

        foreach (var b in w.Bodies)
        {
            if (!IsVisible(b) || !ShowLabel(b)) continue;
            var at = Camera.ToScreen(_drawn[b.Id]);
            if (!Camera.OnScreen(at, 100)) continue;
            var r = (float)ScreenRadius(b);
            var color = b.Id == SelectedId ? Ui.Text : b.Id == HoverId ? new Color(Ui.Text, 0.85f) : Ui.Dim;
            DrawString(_font, at + new Vector2(r + 5, 5), b.Name, HorizontalAlignment.Left, -1, b.Kind == BodyKind.Moon ? 12 : 14, color);
            if (b.Id == SelectedId) DrawArc(at, r + 5, 0, Mathf.Tau, 48, new Color(Ui.Text, 0.8f), 1.2f, true);
        }

        foreach (var f in w.Fleets) DrawRoute(f, f.Flight, false);
        foreach (var (fleet, flight) in PendingFlights()) DrawRoute(fleet, flight, false);
        if (Preview?.Flight is Flight preview && SelectedId is int sel && w.FindFleet(sel) is Fleet pf)
            DrawRoute(pf, preview, true, Preview.Ok);
        foreach (var f in w.Fleets) DrawFleet(f);
    }

    // ---- Pieces ---------------------------------------------------------------------------

    private bool ShowLabel(Body b) => b.Kind switch
    {
        BodyKind.Star => true,
        BodyKind.Moon => b.OrbitRadius * Camera.Scale >= 30 || b.Id == SelectedId || b.Id == HoverId,
        // Inner planets bunch up around the star when zoomed out: label them once there is room.
        _ => b.OrbitRadius * Camera.Scale >= 40 || b.Id == SelectedId || b.Id == HoverId,
    };

    /// <summary>
    /// An orbit circle. Small ones are drawn whole; huge ones (zoomed in close) only near the view,
    /// worked out in double precision so they stay smooth and in the right place.
    /// </summary>
    private void DrawOrbit(Vec2 parentWorld, double radiusMm, float alpha)
    {
        var scale = Camera.Scale;
        var rPx = radiusMm * scale;
        if (rPx < 3) return;
        var color = new Color(Ui.Orbit, alpha);
        var diag = Camera.ScreenSize.Length();
        if (rPx < 20000)
        {
            var c = Camera.ToScreen(parentWorld);
            if (!Camera.OnScreen(c, (float)rPx + 10)) return;
            var segments = (int)Math.Clamp(rPx / 2, 48, 720);
            DrawArc(c, (float)rPx, 0, Mathf.Tau, segments, color, 1, true);
            return;
        }
        var toView = Camera.Center - parentWorld;
        if (Math.Abs(toView.Length - radiusMm) * scale > diag) return;
        var mid = Math.Atan2(toView.Y, toView.X);
        var half = Math.Min(Math.PI, diag * 1.2 / rPx);
        const int n = 96;
        var pts = new Vector2[n + 1];
        for (var i = 0; i <= n; i++)
        {
            var a = mid - half + 2 * half * i / n;
            pts[i] = Camera.ToScreen(parentWorld + new Vec2(Math.Cos(a), Math.Sin(a)) * radiusMm);
        }
        DrawPolyline(pts, color, 1, true);
    }

    private void DrawBelt(Belt belt)
    {
        var parent = Sim.World.FindBody(belt.ParentId);
        if (parent == null) return;
        var scale = Camera.Scale;
        if ((belt.OuterRadius - belt.InnerRadius) * scale < 3) return;
        var center = _drawn[parent.Id];
        var hours = Sim.World.Tick + Blend - 1;
        var color = new Color(belt.Color);
        var size = (float)Math.Clamp(0.4 + scale * 2, 0.8, 2.0);
        for (var i = 0; i < BeltDots; i++)
        {
            // Fixed pseudo-random spread per dot; each dot orbits at its own Kepler speed.
            var h1 = Hash(i * 2 + 1);
            var h2 = Hash(i * 2 + 2);
            var r = belt.InnerRadius + (belt.OuterRadius - belt.InnerRadius) * (0.5 + 0.5 * Math.Sin((h1 - 0.5) * Math.PI));
            var period = Physics.OrbitPeriodHours(r, parent.Gm);
            var a = h2 * Math.Tau + Math.Tau * hours / period;
            var p = Camera.ToScreen(center + new Vec2(Math.Cos(a), Math.Sin(a)) * r);
            if (!Camera.OnScreen(p, 4)) continue;
            DrawCircle(p, size, new Color(color, 0.25f + 0.35f * (float)Hash(i + 7777)));
        }
    }

    private static double Hash(int n)
    {
        unchecked
        {
            var x = (uint)n * 0x9E3779B1u;
            x ^= x >> 15; x *= 0x85EBCA77u; x ^= x >> 13; x *= 0xC2B2AE3Du; x ^= x >> 16;
            return x / 4294967296.0;
        }
    }

    private void DrawStar(Body b, Vector2 at, float r)
    {
        var core = new Color(b.Color);
        for (var i = 5; i >= 1; i--)
            DrawCircle(at, r * (1 + i * 0.6f), new Color(core, 0.05f));
        DrawCircle(at, r, core);
        DrawCircle(at, r * 0.7f, core.Lightened(0.35f));
    }

    private void DrawPlanet(Body b, Vector2 at, float r, Vec2 starWorld)
    {
        var baseColor = new Color(b.Color);
        DrawCircle(at, r, baseColor);
        if (r >= 3)
        {
            // Night side: the half facing away from the star.
            var toStar = starWorld - _drawn[b.Id];
            var light = (float)Math.Atan2(toStar.Y, toStar.X);
            const int n = 28;
            var pts = new Vector2[n + 1];
            for (var i = 0; i <= n; i++)
            {
                var a = light + Mathf.Pi / 2 + Mathf.Pi * i / n;
                pts[i] = at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            }
            DrawColoredPolygon(pts, new Color(0, 0, 0, 0.55f));
            if (r >= 6 && b.Type is "gas" or "ice-giant")
            {
                // A few faint bands on the giants.
                for (var k = -2; k <= 2; k++)
                {
                    var y = k * r * 0.3f;
                    var half = Mathf.Sqrt(Mathf.Max(0, r * r - y * y));
                    DrawLine(at + new Vector2(-half, y), at + new Vector2(half, y), new Color(baseColor.Darkened(0.2f), 0.5f), Math.Max(1, r * 0.08f));
                }
            }
        }
        if (b.Atmosphere is string atm && r >= 4)
            DrawArc(at, r + Math.Max(0.8f, r * 0.04f), 0, Mathf.Tau, 64, new Color(new Color(atm), 0.55f), Math.Max(1, r * 0.06f), true);
    }

    /// <summary>Is the formation spread out enough on screen to draw each ship on its own?</summary>
    private bool ShipsApart => Fleets.FormationSpacing * Camera.Scale >= 22;

    private void DrawFleet(Fleet f)
    {
        if (!_drawn.TryGetValue(f.Id, out var world) || !world.IsFinite) return;
        var at = Camera.ToScreen(world);
        if (!Camera.OnScreen(at, 400)) return;
        var color = f.Side == Side.Player ? Ui.Player : Ui.Rival;
        var heading = Heading(f);
        var apart = ShipsApart;
        var ships = Sim.World.ShipsOf(f).ToList();
        var drawnShips = apart ? ships : ships.Take(1).ToList();
        for (var i = 0; i < drawnShips.Count; i++)
        {
            var s = drawnShips[i];
            var p = apart ? Camera.ToScreen(_drawn.TryGetValue(s.Id, out var sp) ? sp : world) : at;
            DrawShipIcon(p, s, color, heading);
            if (apart && (Camera.Scale > 0.05 || f.Id == SelectedId))
                DrawString(_font, p + new Vector2(12, -8), s.Name, HorizontalAlignment.Left, -1, 12, color);
        }
        if (!apart)
        {
            // Zoomed out: the fleet is one icon with a count.
            var label = ships.Count > 1 ? $"{f.Name} ({ships.Count})" : $"{f.Name}: {ships.FirstOrDefault()?.Name}";
            if (ships.Count > 1) DrawString(_font, at + new Vector2(-4, 16), ships.Count.ToString(), HorizontalAlignment.Left, -1, 11, color);
            if (f.Id == SelectedId || f.Id == HoverId || Camera.Scale > 0.01)
                DrawString(_font, at + new Vector2(12, -8), label, HorizontalAlignment.Left, -1, 12, color);
        }
        if (f.Id == SelectedId)
        {
            var radius = apart ? (float)(Fleets.FormationSpacing * 1.6 * Camera.Scale) + 14 : 14f;
            DrawArc(at, radius, 0, Mathf.Tau, 48, color, 1.5f, true);
        }
    }

    private static float Heading(Fleet f)
    {
        if (f.Velocity.Length > 1e-6) return (float)Math.Atan2(f.Velocity.Y, f.Velocity.X);
        if (f.Flight is Flight fl) { var d = fl.Target - f.Position; return (float)Math.Atan2(d.Y, d.X); }
        return 0;
    }

    private void DrawShipIcon(Vector2 at, Ship s, Color color, float heading)
    {
        var shape = ShipIcons.For(GameData.ShipClasses.TryGetValue(s.ClassId, out var cls) ? cls.Icon : "frigate");
        var pts = new Vector2[shape.Length];
        for (var i = 0; i < shape.Length; i++) pts[i] = at + shape[i].Rotated(heading);
        DrawColoredPolygon(pts, color);
        var outline = new Vector2[pts.Length + 1];
        pts.CopyTo(outline, 0);
        outline[^1] = pts[0];
        DrawPolyline(outline, color.Darkened(0.5f), 1, true);
    }

    /// <summary>
    /// A flight's path: bright where the engines burn, faint where it coasts, a ring where it ends and
    /// (for a planet) a faint ghost of where the planet will be by then.
    /// </summary>
    private void DrawRoute(Fleet f, Flight? flight, bool preview, bool ok = true)
    {
        if (flight == null || flight.Legs.Count == 0) return;
        var baseColor = !ok ? Ui.Danger : f.Side == Side.Player ? Ui.Player : Ui.Rival;
        var alpha = preview ? 0.9f : f.Id == SelectedId ? 0.6f : 0.35f;
        var now = Sim.World.Tick + Blend - 1;
        if (!flight.Started && _drawn.TryGetValue(f.Id, out var here))
        {
            // Still waiting to leave: a dotted line from here to the departure point.
            DrawDashedLine(Camera.ToScreen(here), Camera.ToScreen(flight.Legs[0].P0), new Color(baseColor, alpha * 0.6f), 1, 3);
        }
        foreach (var leg in flight.Legs)
        {
            if (leg.End <= now) continue;
            var from = Math.Max(leg.Start, now);
            const int n = 24;
            var pts = new Vector2[n + 1];
            for (var i = 0; i <= n; i++) pts[i] = Camera.ToScreen(leg.PositionAt(from + (leg.End - from) * i / n));
            DrawPolyline(pts, new Color(baseColor, leg.IsBurn ? alpha : alpha * 0.55f), leg.IsBurn ? 2.5f : 1.2f, true);
        }
        var end = Camera.ToScreen(flight.Target);
        DrawArc(end, 5, 0, Mathf.Tau, 16, new Color(baseColor, alpha), 1.2f, true);
        if (flight.Arrival == ArrivalStyle.FastPass)
        {
            // Carries on past the target.
            var last = flight.Legs[^1];
            var v = last.VelocityAt(last.End);
            if (v.Length > 1e-9)
                DrawDashedLine(end, Camera.ToScreen(flight.Target + v * 24 * 3), new Color(baseColor, alpha * 0.4f), 1, 6);
        }
        if (flight.TargetBodyId is int bodyId && Sim.World.FindBody(bodyId) is Body body)
        {
            var ghost = Camera.ToScreen(FlightPlanner.PredictBody(Sim.World, body, flight.ArriveHour));
            DrawArc(ghost, (float)ScreenRadius(body) + 3, 0, Mathf.Tau, 24, new Color(baseColor, alpha * 0.5f), 1, true);
        }
        if (preview || f.Id == SelectedId)
            DrawString(_font, end + new Vector2(8, 16), GameTime.Format((long)flight.ArriveHour), HorizontalAlignment.Left, -1, 12, new Color(baseColor, alpha));
    }

    /// <summary>Orders given while paused only reach the simulation on its next tick; show them now anyway.</summary>
    private IEnumerable<(Fleet, Flight)> PendingFlights()
    {
        foreach (var c in Sim.PendingCommands)
        {
            if (c is PlanFlightCommand pf && Sim.World.FindFleet(pf.FleetId) is Fleet f
                && FlightPlanner.Plan(Sim.World, f, pf.Request) is { Ok: true, Flight: Flight flight })
                yield return (f, flight);
        }
    }

    /// <summary>The fleet or body under a screen point, fleets first. Clicking any ship picks its fleet.</summary>
    public int? HitTest(Vector2 screen)
    {
        int? best = null;
        var bestD = 14f;
        foreach (var s in Sim.World.Ships)
        {
            if (!_drawn.TryGetValue(s.FleetId, out var fp) || !fp.IsFinite) continue;
            var p = ShipsApart && _drawn.TryGetValue(s.Id, out var sp) ? sp : fp;
            var d = Camera.ToScreen(p).DistanceTo(screen);
            if (d < bestD) { bestD = d; best = s.FleetId; }
        }
        return best ?? HitTestBody(screen);
    }

    /// <summary>The body under a screen point (the smallest, so a moon wins over its planet).</summary>
    public int? HitTestBody(Vector2 screen)
    {
        int? best = null;
        var bestBody = double.MaxValue;
        foreach (var b in Sim.World.Bodies)
        {
            if (!IsVisible(b)) continue;
            var d = Camera.ToScreen(_drawn[b.Id]).DistanceTo(screen);
            var r = ScreenRadius(b) + 6;
            if (d <= r && r < bestBody) { bestBody = r; best = b.Id; }
        }
        return best;
    }
}

/// <summary>Ship icon shapes, pointing right (+x), about 20 px long.</summary>
public static class ShipIcons
{
    private static readonly Vector2[] Frigate = { new(9, 0), new(-6, 6), new(-3, 0), new(-6, -6) };
    private static readonly Vector2[] Destroyer = { new(11, 0), new(3, 4.5f), new(-9, 4.5f), new(-6, 0), new(-9, -4.5f), new(3, -4.5f) };

    public static Vector2[] For(string icon) => icon switch
    {
        "destroyer" => Destroyer,
        _ => Frigate,
    };
}
