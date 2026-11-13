using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Supply lanes and zones on the map. A lane is a soft band between its two bodies, as wide as its spread
/// (it widens the longer since the last sighting): the rival's lanes you have spotted in orange, your own
/// cargo runs in blue. A faint dashed line shows where the lane ran 20 days ago. Each lane is labelled
/// with how sure you are (seen once, seen twice, regular run) and how often ships pass; hovering one shows
/// its last five sightings with their dates. Zones are dashed ellipses along their lane (raid amber, guard
/// cyan, picket grey) with the dotted drift track of the fleets holding them, and slide with the lane.
/// </summary>
public partial class MapView
{
    private static readonly Color OwnLane = new("4aa8ff");
    private static readonly Color RaidColor = new("ffb347");

    private Vec2 DrawnAt(World w, int bodyId) => _drawn.TryGetValue(bodyId, out var p) ? p : w.FindBody(bodyId)?.Position ?? Vec2.Zero;

    private void DrawLanesAndZones()
    {
        var w = Sim!.World;
        var side = Side.Player;
        var mouse = GetLocalMousePosition();
        Lane? hovered = null;
        foreach (var l in w.Lanes.Where(l => l.SeenBy == side || RevealAll && l.SeenBy != side))
        {
            var a = Camera.ToScreen(DrawnAt(w, l.FromBodyId));
            var b = Camera.ToScreen(DrawnAt(w, l.ToBodyId));
            if (!SegmentOnScreen(a, b)) continue;
            var sure = (float)Lanes.Confidence(w, l);
            var width = Mathf.Clamp((float)(Lanes.WidthMm(w, l) * Camera.Scale), 10, 400);
            var tint = l.SeenBy == side ? Ui.Rival : Ui.Player; // the other side's cargo
            // Where it ran 20 days ago: a faint dashed line between where the two bodies were then.
            if (w.FindBody(l.FromBodyId) is Body fa && w.FindBody(l.ToBodyId) is Body fb && w.Hour > 20 * 24)
            {
                var then = w.Hour - 20 * 24;
                DrawDashedLine(Camera.ToScreen(FlightPlanner.PredictBody(w, fa, then)), Camera.ToScreen(FlightPlanner.PredictBody(w, fb, then)),
                    new Color(tint, 0.22f), 1, 10);
            }
            DrawBand(a, b, width, new Color(tint, 0.1f + 0.25f * sure));
            DrawLine(a, b, new Color(tint, 0.25f + 0.45f * sure), 1.2f, true);
            if (DistToSegment(mouse, a, b) <= width / 2 + 6) hovered = l;
            if (width > 20 || Camera.Scale > 0.0008)
                LaneLabel(a, b, $"{Name(w, l.FromBodyId)}–{Name(w, l.ToBodyId)} · {LaneLooks.Sureness(l)}{LaneLooks.Often(w, l)}", tint);
        }
        // Your own cargo runs: blue, narrow (you know where your ships are).
        foreach (var run in LaneLooks.OwnRuns(w))
        {
            var a = Camera.ToScreen(DrawnAt(w, run.From));
            var b = Camera.ToScreen(DrawnAt(w, run.To));
            if (!SegmentOnScreen(a, b)) continue;
            var width = Mathf.Clamp((float)(Lanes.BaseWidthMm * Camera.Scale), 8, 120);
            var risk = LaneLooks.Risk(w, run.From, run.To);
            var c = risk == "high" ? Ui.Danger : OwnLane;
            DrawBand(a, b, width, new Color(c, 0.16f));
            DrawLine(a, b, new Color(c, 0.5f), 1.2f, true);
            if (Camera.Scale > 0.0008)
                LaneLabel(a, b, $"your lane: {Name(w, run.From)}–{Name(w, run.To)} · {run.Ships} freighter{(run.Ships == 1 ? "" : "s")} on it · risk {risk}", c);
        }
        if (hovered != null) DrawLaneSightings(w, hovered);

        foreach (var z in w.Zones.Where(z => z.Side == side || RevealAll))
        {
            var onLane = z.FromBodyId is int fa2 && z.ToBodyId is int fb2;
            var center = onLane ? DrawnAt(w, z.FromBodyId!.Value) + (DrawnAt(w, z.ToBodyId!.Value) - DrawnAt(w, z.FromBodyId!.Value)) * z.T : z.Center;
            var at = Camera.ToScreen(center);
            var r = (float)(z.RadiusMm * Camera.Scale);
            if (!Camera.OnScreen(at, r + 50)) continue;
            var color = z.Side != side ? Ui.Rival : z.Kind switch
            {
                ZoneKind.Raid => RaidColor,
                ZoneKind.Guard => Ui.Player,
                _ => Ui.Dim,
            };
            r = Mathf.Max(r, 6);
            // Along its lane: an ellipse stretched along the run; otherwise a circle.
            var along = Vector2.Right;
            if (onLane)
            {
                var d = Camera.ToScreen(DrawnAt(w, z.ToBodyId!.Value)) - Camera.ToScreen(DrawnAt(w, z.FromBodyId!.Value));
                if (d.LengthSquared() > 1) along = d.Normalized();
            }
            var rx = r;
            var ry = onLane ? r * 0.62f : r;
            DrawEllipse(at, along, rx, ry, new Color(color, 0.05f), new Color(color, 0.7f));
            // The fleets holding it: their drift over the next day, dotted.
            foreach (var f in w.Fleets.Where(f => f.ZoneId == z.Id && f.ShipIds.Count > 0 && Shown(f)))
            {
                if (!_drawn.TryGetValue(f.Id, out var fp) || f.Velocity.Length < 1e-6) continue;
                var rel = f.Velocity - (onLane && w.FindBody(z.FromBodyId!.Value) is Body ab && w.FindBody(z.ToBodyId!.Value) is Body bb
                    ? (FlightPlanner.PredictBody(w, ab, w.Hour + 1) - ab.Position) * (1 - z.T) + (FlightPlanner.PredictBody(w, bb, w.Hour + 1) - bb.Position) * z.T
                    : Vec2.Zero);
                for (var i = 1; i <= 12; i++)
                {
                    var p = Camera.ToScreen(fp + rel * (2 * i));
                    if (p.DistanceTo(at) > Mathf.Max(rx, ry) * 1.3f) break;
                    DrawCircle(p, 1.4f, new Color(color, 0.7f - i * 0.04f));
                }
            }
            var holders = w.Fleets.Count(f => f.ZoneId == z.Id);
            var hold = w.Fleets.FirstOrDefault(f => f.ZoneId == z.Id) is Fleet h ? $" · {h.Name} {(z.Hold == ZoneHold.Drift ? "drifting" : "keeping station")}" : "";
            DrawString(_font, at + new Vector2(rx * 0.7f + 4, -ry * 0.7f - 4), $"{z.Name}{hold}{(holders > 1 ? $" (+{holders - 1})" : "")}"
                + (z.Catches > 0 ? $" · {z.Catches} caught" : ""), HorizontalAlignment.Left, -1, 11, new Color(color, 0.9f));
            if (onLane)
            {
                // It slides with the lane: small arrows along the run at both ends, and a word.
                foreach (var sgn in new[] { -1f, 1f })
                {
                    var tip = at + along * sgn * (rx + 10);
                    DrawLine(tip - along * sgn * 7, tip, new Color(color, 0.6f), 1.2f, true);
                    var back = -along * sgn * 4;
                    var side2 = new Vector2(-along.Y, along.X) * 3;
                    DrawLine(tip, tip + back + side2, new Color(color, 0.6f), 1.2f, true);
                    DrawLine(tip, tip + back - side2, new Color(color, 0.6f), 1.2f, true);
                }
                DrawString(_font, at + new Vector2(-40, ry + 16), "slides with the lane", HorizontalAlignment.Left, -1, 10, new Color(color, 0.55f));
            }
        }
    }

    private static string Name(World w, int id) => w.FindBody(id)?.Name ?? "?";

    private bool SegmentOnScreen(Vector2 a, Vector2 b)
    {
        var box = new Rect2(Vector2.Zero, Camera.ScreenSize).Grow(200);
        return box.HasPoint(a) || box.HasPoint(b) || box.HasPoint((a + b) / 2);
    }

    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = ab.LengthSquared() > 1e-6 ? Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1) : 0;
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>A soft band: brightest along the middle, fading to nothing at its edges.</summary>
    private void DrawBand(Vector2 a, Vector2 b, float width, Color color)
    {
        var d = b - a;
        if (d.LengthSquared() < 1) return;
        var n = new Vector2(-d.Y, d.X).Normalized() * (width / 2);
        var clear = new Color(color, 0);
        DrawPolygon(new[] { a + n, b + n, b, a }, new[] { clear, clear, color, color });
        DrawPolygon(new[] { a, b, b - n, a - n }, new[] { color, color, clear, clear });
    }

    private void LaneLabel(Vector2 a, Vector2 b, string text, Color color)
    {
        // A third of the way along, clear of a zone in the middle.
        var mid = a.Lerp(b, 0.3f);
        var box = new Rect2(Vector2.Zero, Camera.ScreenSize);
        if (!box.HasPoint(mid))
        {
            // Long lanes: put the label at the visible part's middle.
            var pts = Enumerable.Range(0, 21).Select(i => a.Lerp(b, i / 20f)).Where(box.HasPoint).ToList();
            if (pts.Count == 0) return;
            mid = pts[pts.Count / 2];
        }
        DrawString(_font, mid + new Vector2(6, -8), text, HorizontalAlignment.Left, -1, 11, new Color(color, 0.8f));
    }

    /// <summary>The last sightings of a lane: dots where along the lane each ship was (as the lane lies now), with dates.</summary>
    private void DrawLaneSightings(World w, Lane l)
    {
        if (w.FindBody(l.FromBodyId) is not Body fa || w.FindBody(l.ToBodyId) is not Body fb) return;
        var a = DrawnAt(w, fa.Id);
        var b = DrawnAt(w, fb.Id);
        var color = l.SeenBy == Side.Player ? Ui.Rival : Ui.Player;
        if (l.Recent.Count == 0)
        {
            var mid = Camera.ToScreen((a + b) * 0.5);
            DrawString(_font, mid + new Vector2(6, 10), $"seen {l.Seen} time{(l.Seen == 1 ? "" : "s")}, last {GameTime.Format((long)l.LastHour)}", HorizontalAlignment.Left, -1, 11, color);
            return;
        }
        foreach (var s in l.Recent)
        {
            // Where along the lane it was then, put on the lane as it lies now.
            var ta = FlightPlanner.PredictBody(w, fa, s.Hour);
            var tb = FlightPlanner.PredictBody(w, fb, s.Hour);
            var ab = tb - ta;
            var len2 = ab.X * ab.X + ab.Y * ab.Y;
            var t = len2 > 1e-9 ? Math.Clamp(Vec2.Dot(s.Position - ta, ab) / len2, 0, 1) : 0.5;
            var off = len2 > 1e-9 ? ((s.Position - ta).X * -ab.Y + (s.Position - ta).Y * ab.X) / Math.Sqrt(len2) : 0;
            var now = b - a;
            var nl = now.Length;
            var perp = nl > 1e-9 ? new Vec2(-now.Y / nl, now.X / nl) : Vec2.Zero;
            var p = Camera.ToScreen(a + now * t + perp * off);
            DrawCircle(p, 3.5f, new Color(color, 0.9f));
            DrawArc(p, 5.5f, 0, Mathf.Tau, 16, new Color(color, 0.5f), 1, true);
            DrawString(_font, p + new Vector2(7, 4), GameTime.Format((long)s.Hour), HorizontalAlignment.Left, -1, 10, new Color(Ui.Text, 0.8f));
        }
    }

    /// <summary>A dashed ellipse with a faint fill, its long axis along <paramref name="along"/>.</summary>
    private void DrawEllipse(Vector2 at, Vector2 along, float rx, float ry, Color fill, Color line)
    {
        var across = new Vector2(-along.Y, along.X);
        var n = Math.Clamp((int)((rx + ry) * 0.5f * Mathf.Tau / 10) / 2 * 2, 32, 720);
        var pts = new Vector2[n];
        for (var i = 0; i < n; i++)
        {
            var a = Mathf.Tau * i / n;
            pts[i] = at + along * (Mathf.Cos(a) * rx) + across * (Mathf.Sin(a) * ry);
        }
        DrawColoredPolygon(pts, fill);
        for (var i = 0; i < n; i += 2) DrawLine(pts[i], pts[(i + 1) % n], line, 1.2f, true);
    }
}

/// <summary>Words and numbers about lanes shared by the map and the zone panel.</summary>
public static class LaneLooks
{
    public static string Sureness(Lane l) => l.Seen switch
    {
        1 => "seen once",
        2 => "seen twice",
        _ => "regular run",
    };

    public static string Often(World w, Lane l) =>
        Lanes.EveryDays(l) is double d ? $" · about every {d:0.#} days" : Lanes.WidthMm(w, l) > 150 ? " · wide" : "";

    /// <summary>Your cargo under way, grouped by run: from, to and how many freighters.</summary>
    public static List<(int From, int To, int Ships)> OwnRuns(World w) => w.Shipments
        .Where(s => s.Side == Side.Player && w.FindFleet(s.FleetId) is { InFlight: true })
        .GroupBy(s => (Math.Min(s.FromBodyId, s.ToBodyId), Math.Max(s.FromBodyId, s.ToBodyId)))
        .Select(g => (g.Key.Item1, g.Key.Item2, g.Sum(s => w.FindFleet(s.FleetId)?.ShipIds.Count ?? 0)))
        .OrderBy(r => r.Item1).ThenBy(r => r.Item2).ToList();

    /// <summary>Risk on one of your runs: high after a loss near it lately, medium with armed enemies seen near it, else low.</summary>
    public static string Risk(World w, int from, int to)
    {
        if (w.FindBody(from) is not Body a || w.FindBody(to) is not Body b) return "low";
        if (w.LossSpots.Any(l => l.Side == Side.Player && Dist(l.At, a.Position, b.Position) < Lanes.LossRadiusMm)) return "high";
        if (Lanes.RaidersNearRuns(w, Side.Player).Any(r => (r.From.Id == from && r.To.Id == to) || (r.From.Id == to && r.To.Id == from))) return "medium";
        return "low";
    }

    private static double Dist(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var len = ab.X * ab.X + ab.Y * ab.Y;
        var t = len > 1e-9 ? Math.Clamp(Vec2.Dot(p - a, ab) / len, 0, 1) : 0;
        return Vec2.Distance(p, a + ab * t);
    }

    /// <summary>
    /// A fleet's rough chance of catching the next ship on a lane from a zone on it (as the zone panel works
    /// it out): how much of the lane's spread the zone covers, whether the fleet is fast enough, and how sure the lane is.
    /// </summary>
    public static double CatchChance(World w, Fleet f, Lane l)
    {
        if (w.FindBody(l.FromBodyId) is not Body a || w.FindBody(l.ToBodyId) is not Body b) return 0;
        var radius = Math.Clamp(Vec2.Distance(a.Position, b.Position) * 0.25, 100, 1500);
        var cover = Math.Min(1, 2 * radius / Math.Max(1, Lanes.WidthMm(w, l)));
        var fast = FleetPerformance.Of(w, f).MaxSpeed >= l.Speed ? 1 : 0.5;
        return Math.Round(cover * fast * Lanes.Confidence(w, l), 2);
    }

    /// <summary>
    /// How long a lane stays worth holding: days until the run between its bodies passes close to the star
    /// (then cargo has to go round) or stretches to twice its length now. Null: good for the next 60 days.
    /// </summary>
    public static (int Days, string Why)? Window(World w, int from, int to)
    {
        if (w.FindBody(from) is not Body a || w.FindBody(to) is not Body b) return null;
        var star = w.Bodies.FirstOrDefault(x => x.ParentId == null);
        var now = Vec2.Distance(a.Position, b.Position);
        for (var day = 1; day <= 60; day++)
        {
            var h = w.Hour + day * 24;
            var pa = FlightPlanner.PredictBody(w, a, h);
            var pb = FlightPlanner.PredictBody(w, b, h);
            if (star != null && Dist(star.Position, pa, pb) < 0.25 * Physics.MmPerAu && Dist(star.Position, a.Position, b.Position) >= 0.25 * Physics.MmPerAu)
                return (day, "then the route swings past the sun");
            if (Vec2.Distance(pa, pb) > now * 2) return (day, "then the run stretches to twice as long");
        }
        return null;
    }
}
