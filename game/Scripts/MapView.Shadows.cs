using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Sight shadows: the grey areas behind planets, moons and the star that none of your ships or planets
/// can see into. A point is shaded when it is inside the reach of at least one of your sensors (for a
/// ship burning its engines, the farthest anything is seen) but every sensor that reaches it has a body in the way.
/// Worked out along screen rows, cut wherever a row crosses a sensor's reach or a shadow's edge, and only
/// when the view or the world has changed; each piece uses the same straight-line test as the simulation.
/// </summary>
public partial class MapView
{
    /// <summary>What the map shows of your sensors (B cycles): shadows only, full coverage, or nothing.</summary>
    public SightView Sight { get; set; } = SightView.Shadows;

    public enum SightView { Shadows, Coverage, Off }

    private const int ShadowCell = 4;          // margin round a shadow cone's box, in pixels
    private const int ShadowRow = 4;           // height of each screen row the shadows are worked out on
    private const int CoverageRow = 3;         // the coverage tint is soft, so taller rows will do
    private const ulong ShadowRefreshMs = 120; // how often the shadows are worked out again while time runs
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
    private (double Scale, double X, double Y, Vector2 Size, SightView Sight) _shadowView;
    private (double Hour, int Fleets, int Ships) _shadowWorld;

    /// <summary>One sensor: how far it sees a warm ship (Reach), a cold one and a burning one.</summary>
    private readonly record struct Observer(Vec2 At, double Reach, double ColdReach, double BurnReach, int? Body, List<int> Blockers);

    private void DrawShadows()
    {
        if (Sight == SightView.Off || !PlayerInfo.Has(MapLayer.Shadows)) return;
        var now = Time.GetTicksMsec();
        var view = (Camera.Scale, Camera.Center.X, Camera.Center.Y, Camera.ScreenSize, Sight);
        // While paused nothing moves, so the shadows stay as they are until the view changes.
        var world = (DrawHour, Sim.World.Fleets.Count, Sim.World.Ships.Count);
        if (view != _shadowView || (world != _shadowWorld && now - _shadowAt >= ShadowRefreshMs))
        {
            _shadowView = view;
            _shadowWorld = world;
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
            WorkOutCoverage(sensors, Clear, centres, bodies);
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

        // Only points inside some sensor's shadow cone (from a body big enough to show) can be hidden.
        var shown = new List<(Observer O, int Body)>();
        var boxes = new List<Rect2>();
        foreach (var (o, i, outline, box, edgeA, edgeB, tiny) in cones)
        {
            if (tiny) continue;
            // The two edges of the cone, from the body's limb out to the sensor's reach.
            _shadowEdges.Add((outline[0], edgeA));
            _shadowEdges.Add((outline[^1], edgeB));
            shown.Add((o, i));
            boxes.Add(box);
        }
        if (shown.Count > 0)
        {
            bool Hidden(Vec2 at)
            {
                var inCone = false;
                foreach (var (o, i) in shown)
                    if (Within(at, o.At, o.BurnReach) && Blocks(o, i, at)) { inCone = true; break; }
                if (!inCone) return false;
                foreach (var other in sensors)
                    if (Within(at, other.At, other.BurnReach) && Clear(other, at)) return false;
                return true;
            }
            var edges = ShadowEdgesOf(sensors, centres, bodies, coverage: false);
            ScanRows(ShadowRow, edges, at => Hidden(at) ? 1 : 0, _ => ShadowColor, boxes);
        }
        // Edges are only worth drawing where the shadow itself shows.
        if (_shadowRuns.Count == 0) _shadowEdges.Clear();
    }

    /// <summary>
    /// Every circle and straight line on which "seen" or "hidden" can change: each sensor's reach, and for
    /// each body that could block it, the two sides of its shadow cone, the line across the sensor where the
    /// body goes from in front to behind, and the circle through the sensor and the body's centre (inside
    /// it, a point is nearer than the body). Between two crossings of a screen row, the answer is the same.
    /// </summary>
    private static (List<(Vec2 At, double R)> Circles, List<(Vec2 At, Vec2 Dir)> Lines) ShadowEdgesOf(
        List<Observer> sensors, Vec2[] centres, List<Body> bodies, bool coverage)
    {
        var circles = new List<(Vec2, double)>();
        var lines = new List<(Vec2, Vec2)>();
        foreach (var o in sensors)
        {
            circles.Add((o.At, o.BurnReach));
            if (coverage)
            {
                circles.Add((o.At, o.Reach));
                circles.Add((o.At, o.ColdReach));
            }
            foreach (var i in o.Blockers)
            {
                var gap = centres[i] - o.At;
                var d = gap.Length;
                if (d <= 0) continue;
                var half = Math.Asin(Math.Min(1, bodies[i].Radius / d));
                var dir = Math.Atan2(gap.Y, gap.X);
                lines.Add((o.At, new Vec2(Math.Cos(dir - half), Math.Sin(dir - half))));
                lines.Add((o.At, new Vec2(Math.Cos(dir + half), Math.Sin(dir + half))));
                lines.Add((o.At, new Vec2(-gap.Y, gap.X)));
                circles.Add(((o.At + centres[i]) * 0.5, d / 2));
            }
        }
        return (circles, lines);
    }

    /// <summary>
    /// Fills _shadowRuns one screen row at a time: cuts the row where it crosses any of the edges, asks
    /// stateOf once in the middle of each piece (0 = nothing to draw), and joins neighbours in the same
    /// state into one strip. The work depends on how many edges cross the screen, not on how big the
    /// shadows are, so a shadow filling the screen (zoomed right in) costs no more than a small one.
    /// </summary>
    private void ScanRows(int rowPx, (List<(Vec2 At, double R)> Circles, List<(Vec2 At, Vec2 Dir)> Lines) edges,
        Func<Vec2, int> stateOf, Func<int, Color> colorOf, List<Rect2>? only = null)
    {
        var size = Camera.ScreenSize;
        var scale = Camera.Scale;
        var centre = Camera.Center;
        var half = Camera.ScreenCenter;
        var rows = (int)Math.Ceiling(size.Y / rowPx);
        var cuts = new List<double>();
        for (var row = 0; row < rows; row++)
        {
            // Where on this row anything can show: the whole row, or only across the given boxes.
            double lo = 0, hi = size.X;
            if (only != null)
            {
                var mid = (row + 0.5f) * rowPx;
                lo = double.MaxValue;
                hi = double.MinValue;
                foreach (var box in only)
                    if (mid >= box.Position.Y && mid <= box.End.Y)
                    {
                        lo = Math.Min(lo, box.Position.X);
                        hi = Math.Max(hi, box.End.X);
                    }
                if (lo >= hi) continue;
            }
            var y = centre.Y + ((row + 0.5) * rowPx - half.Y) / scale; // the row, as a line across the world
            cuts.Clear();
            cuts.Add(lo);
            cuts.Add(hi);
            void Cut(double worldX)
            {
                var x = (worldX - centre.X) * scale + half.X;
                if (x > lo && x < hi) cuts.Add(x);
            }
            foreach (var (at, r) in edges.Circles)
            {
                var dy = y - at.Y;
                if (Math.Abs(dy) >= r) continue;
                var dx = Math.Sqrt(r * r - dy * dy);
                Cut(at.X - dx);
                Cut(at.X + dx);
            }
            foreach (var (at, dir) in edges.Lines)
                if (Math.Abs(dir.Y) > 1e-12) Cut(at.X + (y - at.Y) * dir.X / dir.Y);
            cuts.Sort();

            var start = lo;
            var last = 0;
            for (var k = 0; k + 1 < cuts.Count; k++)
            {
                var x0 = cuts[k];
                var x1 = cuts[k + 1];
                if (x1 - x0 < 1e-6) continue;
                var state = stateOf(new Vec2(centre.X + ((x0 + x1) / 2 - half.X) / scale, y));
                if (state == last) continue;
                if (last != 0) _shadowRuns.Add((new Rect2((float)start, row * rowPx, (float)(x0 - start), rowPx), colorOf(last)));
                start = x0;
                last = state;
            }
            if (last != 0) _shadowRuns.Add((new Rect2((float)start, row * rowPx, (float)(hi - start), rowPx), colorOf(last)));
        }
    }

    /// <summary>
    /// Every point on screen: seen well enough to spot a ship coasting cold, a warm ship, or only a ship
    /// burning its engines; hidden behind a body from every sensor that reaches it; or out of reach (dark).
    /// </summary>
    private void WorkOutCoverage(List<Observer> sensors, Func<Observer, Vec2, bool> clear, Vec2[] centres, List<Body> bodies)
    {
        var colors = new[] { default, SeesBurn, SeesWarm, SeesCold, ShadowColor };
        int StateOf(Vec2 at)
        {
            var state = 0;
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
            return state == 0 && reached ? 4 : state;
        }
        ScanRows(CoverageRow, ShadowEdgesOf(sensors, centres, bodies, coverage: true), StateOf, state => colors[state]);
    }
}
