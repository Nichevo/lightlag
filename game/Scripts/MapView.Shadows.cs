using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Sight shadows: the grey areas behind planets, moons and the star that none of your ships or planets
/// can see into. A point is shaded when it is inside the reach of at least one of your sensors (for a
/// ship burning its engines, the farthest anything is seen) but every sensor that reaches it has a body in the way. Worked out on a coarse screen grid
/// a few times a second, using the same straight-line test as the simulation.
/// </summary>
public partial class MapView
{
    /// <summary>What the map shows of your sensors (B cycles): shadows only, full coverage, or nothing.</summary>
    public SightView Sight { get; set; } = SightView.Shadows;

    public enum SightView { Shadows, Coverage, Off }

    private const int ShadowCell = 4;          // grid step in pixels
    private const int CoverageCell = 6;        // the coverage tint is soft, so a coarser grid will do
    private const ulong ShadowRefreshMs = 120; // how often the grid is worked out again
    private static readonly Color ShadowColor = new(0.62f, 0.64f, 0.72f, 0.2f);
    private static readonly Color SeesCold = new(Ui.Player, 0.11f);
    private static readonly Color SeesWarm = new(Ui.Player, 0.05f);
    private static readonly Color SeesBurn = new(Ui.Player, 0.02f);
    private static readonly Color BurnEdge = new(Ui.Player, 0.35f);
    private static readonly Color ShadowEdge = new(0.62f, 0.64f, 0.72f, 0.28f);

    private readonly List<(Rect2 Rect, Color Color)> _shadowRuns = new();
    private readonly List<(Vector2 A, Vector2 B)> _shadowEdges = new();
    private readonly List<(Vector2 At, float Radius)> _burnRings = new();
    private ulong _shadowAt;
    private (double Scale, double X, double Y, Vector2 Size) _shadowView;

    /// <summary>One sensor: how far it sees a warm ship (Reach), a cold one and a burning one.</summary>
    private readonly record struct Observer(Vec2 At, double Reach, double ColdReach, double BurnReach, int? Body, List<int> Blockers);

    private void DrawShadows()
    {
        if (Sight == SightView.Off) return;
        var now = Time.GetTicksMsec();
        var view = (Camera.Scale, Camera.Center.X, Camera.Center.Y, Camera.ScreenSize);
        if (view != _shadowView || now - _shadowAt >= ShadowRefreshMs)
        {
            _shadowView = view;
            _shadowAt = now;
            WorkOutShadows();
        }
        foreach (var (r, c) in _shadowRuns) DrawRect(r, c);
        foreach (var (a, b) in _shadowEdges) DrawDashedLine(a, b, ShadowEdge, 1, 6);
        foreach (var (at, r) in _burnRings) DrawDashedCircle(at, r);
        if (Sight == SightView.Coverage)
            DrawString(_font, new Vector2(16, 64),
                "Sensor coverage (B): bright = a ship coasting cold is spotted · fainter = a warm ship · faintest, to the dashed edge = a ship burning its engines · grey = hidden behind a body",
                HorizontalAlignment.Left, -1, 13, new Color(Ui.Text, 0.75f));
    }

    /// <summary>A dashed circle, skipped when it is too small to read or wholly off screen.</summary>
    private void DrawDashedCircle(Vector2 at, float r) => DrawDashedCircle(at, r, BurnEdge, null);

    /// <summary>A dashed circle in a colour, with an optional label at its top.</summary>
    private void DrawDashedCircle(Vector2 at, float r, Color color, string? label)
    {
        var size = Camera.ScreenSize;
        if (r < 8 || at.X + r < 0 || at.Y + r < 0 || at.X - r > size.X || at.Y - r > size.Y) return;
        var dashes = Math.Clamp((int)(r * Mathf.Tau / 12), 24, 720);
        for (var i = 0; i < dashes; i += 2)
        {
            var a0 = Mathf.Tau * i / dashes;
            var a1 = Mathf.Tau * (i + 1) / dashes;
            DrawLine(at + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, at + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, color, 1);
        }
        if (label != null) DrawString(_font, at + new Vector2(-40, r + 16), label, HorizontalAlignment.Left, -1, 11, color);
    }

    private void WorkOutShadows()
    {
        _shadowRuns.Clear();
        _shadowEdges.Clear();
        _burnRings.Clear();
        var w = Sim.World;
        var bodies = w.Bodies.Where(b => b.Radius > 0 && _drawn.ContainsKey(b.Id)).ToList();
        if (bodies.Count == 0) return;

        // Every sensor of yours, with how far it would see a warm ship, and which bodies could block it.
        var warm = GameData.Sensors.Warm;
        var sensors = new List<Observer>();
        void Add(Vec2 at, double sensorMm, int? body)
        {
            if (!at.IsFinite || sensorMm <= 0) return;
            var reach = Sensors.Range(sensorMm, warm);
            var burnReach = Sensors.Range(sensorMm, GameData.Sensors.Burn);
            // A body can hide even a burning ship, out to the far edge where one is seen, so look that far.
            var blockReach = burnReach;
            var blockers = new List<int>();
            for (var i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b.Id == body) continue;
                var d = Vec2.Distance(at, _drawn[b.Id]);
                if (d > b.Radius && d - b.Radius < blockReach) blockers.Add(i);
            }
            sensors.Add(new Observer(at, reach, Sensors.Range(sensorMm, GameData.Sensors.Cold), burnReach, body, blockers));
        }
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player))
            if (_drawn.TryGetValue(f.Id, out var p)) Add(p, Sensors.SensorOf(w, f), null);
        foreach (var b in w.Bodies.Where(b => b.Owner == Side.Player && _drawn.ContainsKey(b.Id)))
            Add(_drawn[b.Id], Sensors.PlanetSensorOf(w, Side.Player, b), b.Id);
        if (sensors.Count == 0) return;

        // Body centres and radii looked up once, not per grid cell.
        var centres = new Vec2[bodies.Count];
        var radii2 = new double[bodies.Count];
        for (var i = 0; i < bodies.Count; i++)
        {
            centres[i] = _drawn[bodies[i].Id];
            radii2[i] = bodies[i].Radius * bodies[i].Radius;
        }

        // Does body i stand between the sensor and this point?
        bool Blocks(Observer o, int i, Vec2 to)
        {
            var d = to - o.At;
            var len2 = Vec2.Dot(d, d);
            if (len2 < 1e-18) return false;
            var t = Vec2.Dot(centres[i] - o.At, d) / len2;
            if (t <= 0 || t >= 1) return false;
            var off = o.At + d * t - centres[i];
            return Vec2.Dot(off, off) < radii2[i];
        }

        bool Clear(Observer o, Vec2 to)
        {
            foreach (var i in o.Blockers)
                if (Blocks(o, i, to)) return false;
            return true;
        }

        static bool Within(Vec2 a, Vec2 b, double r)
        {
            var d = a - b;
            return Vec2.Dot(d, d) <= r * r;
        }

        // Every body's shadow cone from every sensor, as a convex outline on screen: the two tangent
        // points, then out along the far edge of the sensor's reach (an arc, as a few straight pieces).
        // Cones that miss the screen are dropped, so the per-cell tests below only try bodies that matter.
        var size = Camera.ScreenSize;
        var screen = new Rect2(Vector2.Zero, size);
        var scale = Camera.Scale;
        var cones = new List<(Observer O, int Body, List<Vector2> Outline, Rect2 Box, Vector2 EdgeA, Vector2 EdgeB, bool Tiny)>();
        for (var s = 0; s < sensors.Count; s++)
        {
            var o = sensors[s];
            var onScreen = new List<int>();
            foreach (var i in o.Blockers)
            {
                var b = bodies[i];
                var gap = centres[i] - o.At;
                var d = gap.Length;
                var half = Math.Asin(Math.Min(1, b.Radius / d));
                var dir = Math.Atan2(gap.Y, gap.X);
                Vec2 Ray(double angle, double length) => o.At + new Vec2(Math.Cos(angle), Math.Sin(angle)) * length;
                var near = Math.Sqrt(Math.Max(0, d * d - b.Radius * b.Radius)); // to the tangent points
                var arcSteps = Math.Max(2, (int)Math.Ceiling(2 * half / 0.15));
                var outline = new List<Vector2> { Camera.ToScreen(Ray(dir - half, near)) };
                for (var a = 0; a <= arcSteps; a++)
                    outline.Add(Camera.ToScreen(Ray(dir - half + 2 * half * a / arcSteps, o.BurnReach / Math.Cos(half / arcSteps))));
                outline.Add(Camera.ToScreen(Ray(dir + half, near)));
                var box = new Rect2(outline[0], Vector2.Zero);
                foreach (var p in outline) box = box.Expand(p);
                box = box.Grow(ShadowCell).Intersection(screen);
                if (box.Size.X <= 0 || box.Size.Y <= 0) continue;
                onScreen.Add(i);
                var tiny = b.Radius * scale < 0.5 && d * scale < 2; // too small to show as a shadow
                cones.Add((o, i, outline, box, Camera.ToScreen(Ray(dir - half, o.BurnReach)), Camera.ToScreen(Ray(dir + half, o.BurnReach)), tiny));
            }
            sensors[s] = o with { Blockers = onScreen };
        }

        if (Sight == SightView.Coverage)
        {
            WorkOutCoverage(sensors, Clear);
            // The edge of where a burning ship is seen: the outer line of the union of all sensors' reach.
            for (var i = 0; i < sensors.Count; i++)
            {
                var o = sensors[i];
                var inside = false;
                for (var j = 0; j < sensors.Count && !inside; j++)
                    inside = j != i && Vec2.Distance(sensors[j].At, o.At) + o.BurnReach < sensors[j].BurnReach;
                if (!inside) _burnRings.Add((Camera.ToScreen(o.At), (float)(o.BurnReach * Camera.Scale)));
            }
            return;
        }

        var cols = (int)Math.Ceiling(size.X / ShadowCell);
        var rows = (int)Math.Ceiling(size.Y / ShadowCell);
        var hidden = new bool[cols * rows];
        var tested = new bool[cols * rows];

        // Only cells inside some sensor's shadow cone need testing: walk each cone's rows on screen.
        foreach (var (o, i, outline, box, edgeA, edgeB, tiny) in cones)
        {
            if (tiny) continue;
            // The two edges of the cone, from the body's limb out to the sensor's reach.
            _shadowEdges.Add((outline[0], edgeA));
            _shadowEdges.Add((outline[^1], edgeB));

            var r0 = (int)(box.Position.Y / ShadowCell);
            var r1 = Math.Min(rows - 1, (int)(box.End.Y / ShadowCell));
            for (var row = r0; row <= r1; row++)
            {
                // Walk only the cells where this row crosses the cone, not the cone's whole box.
                if (!SpanAt(outline, (row + 0.5) * ShadowCell, out var x0, out var x1)) continue;
                x0 = Math.Max(x0, -ShadowCell);
                x1 = Math.Min(x1, size.X + ShadowCell);
                var c0 = Math.Max(0, (int)Math.Floor(x0 / ShadowCell) - 1);
                var c1 = Math.Min(cols - 1, (int)Math.Floor(x1 / ShadowCell) + 1);
                for (var col = c0; col <= c1; col++)
                {
                    var k = row * cols + col;
                    if (tested[k]) continue;
                    var at = Camera.ToWorld(new Vector2((col + 0.5f) * ShadowCell, (row + 0.5f) * ShadowCell));
                    // Only this cone's own body matters here; other bodies have their own cones.
                    if (!Within(at, o.At, o.BurnReach) || !Blocks(o, i, at)) continue;
                    tested[k] = true;
                    var seen = false;
                    foreach (var other in sensors)
                        if (Within(at, other.At, other.BurnReach) && Clear(other, at)) { seen = true; break; }
                    hidden[k] = !seen;
                }
            }
        }

        // Join each row's hidden cells into strips, so a big shadow is a handful of rectangles.
        for (var row = 0; row < rows; row++)
        {
            var start = -1;
            for (var col = 0; col <= cols; col++)
            {
                var on = col < cols && hidden[row * cols + col];
                if (on && start < 0) start = col;
                else if (!on && start >= 0)
                {
                    _shadowRuns.Add((new Rect2(start * ShadowCell, row * ShadowCell, (col - start) * ShadowCell, ShadowCell), ShadowColor));
                    start = -1;
                }
            }
        }
        // Edges are only worth drawing where the shadow itself shows.
        if (_shadowRuns.Count == 0) _shadowEdges.Clear();
    }

    /// <summary>Where a horizontal line crosses a convex outline: its left and right ends, if it does.</summary>
    private static bool SpanAt(List<Vector2> outline, double y, out double x0, out double x1)
    {
        x0 = double.MaxValue;
        x1 = double.MinValue;
        for (var i = 0; i < outline.Count; i++)
        {
            var a = outline[i];
            var b = outline[(i + 1) % outline.Count];
            if ((a.Y > y) == (b.Y > y)) continue;
            var x = a.X + (y - a.Y) / ((double)b.Y - a.Y) * ((double)b.X - a.X);
            x0 = Math.Min(x0, x);
            x1 = Math.Max(x1, x);
        }
        return x0 <= x1;
    }

    /// <summary>
    /// Every cell on screen: seen well enough to spot a ship coasting cold, a warm ship, or only a ship
    /// burning its engines; hidden behind a body from every sensor that reaches it; or out of reach (dark).
    /// </summary>
    private void WorkOutCoverage(List<Observer> sensors, Func<Observer, Vec2, bool> clear)
    {
        const int cell = CoverageCell;
        var size = Camera.ScreenSize;
        var cols = (int)Math.Ceiling(size.X / cell);
        var rows = (int)Math.Ceiling(size.Y / cell);
        var colors = new[] { (Color?)null, SeesBurn, SeesWarm, SeesCold, ShadowColor };
        for (var row = 0; row < rows; row++)
        {
            var start = 0;
            var last = -1;
            for (var col = 0; col <= cols; col++)
            {
                var state = 0;
                if (col < cols)
                {
                    var at = Camera.ToWorld(new Vector2((col + 0.5f) * cell, (row + 0.5f) * cell));
                    var reached = false;
                    foreach (var o in sensors)
                    {
                        var gap = at - o.At;
                        var d2 = Vec2.Dot(gap, gap);
                        if (d2 > o.BurnReach * o.BurnReach) continue;
                        reached = true;
                        var could = d2 <= o.ColdReach * o.ColdReach ? 3 : d2 <= o.Reach * o.Reach ? 2 : 1;
                        if (could <= state || !clear(o, at)) continue; // can't improve on what's already seen
                        state = could;
                        if (state == 3) break;
                    }
                    if (state == 0 && reached) state = 4;
                }
                else state = -1;
                if (state == last) continue;
                if (last > 0 && colors[last] is Color c)
                    _shadowRuns.Add((new Rect2(start * cell, row * cell, (col - start) * cell, cell), c));
                start = col;
                last = state;
            }
        }
    }
}
