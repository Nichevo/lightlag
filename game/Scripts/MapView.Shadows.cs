using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Sight shadows: the grey areas behind planets, moons and the star that none of your ships or planets
/// can see into. A point is shaded when it is inside the reach of at least one of your sensors (for a
/// warm ship) but every sensor that reaches it has a body in the way. Worked out on a coarse screen grid
/// a few times a second, using the same straight-line test as the simulation.
/// </summary>
public partial class MapView
{
    /// <summary>What the map shows of your sensors (B cycles): shadows only, full coverage, or nothing.</summary>
    public SightView Sight { get; set; } = SightView.Shadows;

    public enum SightView { Shadows, Coverage, Off }

    private const int ShadowCell = 4;          // grid step in pixels
    private const ulong ShadowRefreshMs = 120; // how often the grid is worked out again
    private static readonly Color ShadowColor = new(0.62f, 0.64f, 0.72f, 0.2f);
    private static readonly Color SeesCold = new(Ui.Player, 0.11f);
    private static readonly Color SeesWarm = new(Ui.Player, 0.04f);
    private static readonly Color ShadowEdge = new(0.62f, 0.64f, 0.72f, 0.28f);

    private readonly List<(Rect2 Rect, Color Color)> _shadowRuns = new();
    private readonly List<(Vector2 A, Vector2 B)> _shadowEdges = new();
    private ulong _shadowAt;
    private (double Scale, double X, double Y, Vector2 Size) _shadowView;

    private readonly record struct Observer(Vec2 At, double Reach, double ColdReach, int? Body, List<int> Blockers);

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
        if (Sight == SightView.Coverage)
            DrawString(_font, new Vector2(16, 64),
                "Sensor coverage (B): bright = a ship coasting cold is spotted · faint = only a warm ship · grey = hidden behind a body",
                HorizontalAlignment.Left, -1, 13, new Color(Ui.Text, 0.75f));
    }

    private void WorkOutShadows()
    {
        _shadowRuns.Clear();
        _shadowEdges.Clear();
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
            var blockers = new List<int>();
            for (var i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b.Id == body) continue;
                var d = Vec2.Distance(at, _drawn[b.Id]);
                if (d > b.Radius && d - b.Radius < reach) blockers.Add(i);
            }
            sensors.Add(new Observer(at, reach, Sensors.Range(sensorMm, GameData.Sensors.Cold), body, blockers));
        }
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player))
            if (_drawn.TryGetValue(f.Id, out var p)) Add(p, Sensors.SensorOf(w, f), null);
        var planetSensor = GameData.Sensors.PlanetSensorMm * Tech.Factor(w, Side.Player, Tech.LongSensors);
        foreach (var b in w.Bodies.Where(b => b.Owner == Side.Player && _drawn.ContainsKey(b.Id)))
            Add(_drawn[b.Id], planetSensor, b.Id);
        if (sensors.Count == 0) return;

        bool Clear(Observer o, Vec2 to)
        {
            var d = to - o.At;
            var len2 = Vec2.Dot(d, d);
            if (len2 < 1e-18) return true;
            foreach (var i in o.Blockers)
            {
                var b = bodies[i];
                var c = _drawn[b.Id];
                var t = Vec2.Dot(c - o.At, d) / len2;
                if (t <= 0 || t >= 1) continue;
                if (Vec2.Distance(o.At + d * t, c) < b.Radius) return false;
            }
            return true;
        }

        if (Sight == SightView.Coverage)
        {
            WorkOutCoverage(sensors, Clear);
            return;
        }

        var size = Camera.ScreenSize;
        var cols = (int)Math.Ceiling(size.X / ShadowCell);
        var rows = (int)Math.Ceiling(size.Y / ShadowCell);
        var hidden = new bool[cols * rows];
        var tested = new bool[cols * rows];
        var screen = new Rect2(Vector2.Zero, size);
        var scale = Camera.Scale;

        // Only cells inside some sensor's shadow cone need testing: walk each cone's box on screen.
        foreach (var o in sensors)
        {
            foreach (var i in o.Blockers)
            {
                var b = bodies[i];
                var c = _drawn[b.Id];
                var gap = c - o.At;
                var d = gap.Length;
                if (b.Radius * scale < 0.5 && d * scale < 2) continue; // too small to show
                var half = Math.Asin(Math.Min(1, b.Radius / d));
                var dir = Math.Atan2(gap.Y, gap.X);
                Vec2 Ray(double angle, double length) => o.At + new Vec2(Math.Cos(angle), Math.Sin(angle)) * length;
                var near = Math.Sqrt(Math.Max(0, d * d - b.Radius * b.Radius)); // to the tangent points
                var corners = new[]
                {
                    Ray(dir - half, near), Ray(dir + half, near),
                    Ray(dir - half, o.Reach), Ray(dir + half, o.Reach), Ray(dir, o.Reach),
                };
                var pts = corners.Select(Camera.ToScreen).ToArray();
                var box = new Rect2(pts[0], Vector2.Zero);
                foreach (var p in pts) box = box.Expand(p);
                box = box.Grow(ShadowCell).Intersection(screen);
                if (box.Size.X <= 0 || box.Size.Y <= 0) continue;

                // The two edges of the cone, from the body's limb out to the sensor's reach.
                _shadowEdges.Add((pts[0], pts[2]));
                _shadowEdges.Add((pts[1], pts[3]));

                var c0 = (int)(box.Position.X / ShadowCell);
                var c1 = Math.Min(cols - 1, (int)(box.End.X / ShadowCell));
                var r0 = (int)(box.Position.Y / ShadowCell);
                var r1 = Math.Min(rows - 1, (int)(box.End.Y / ShadowCell));
                for (var row = r0; row <= r1; row++)
                for (var col = c0; col <= c1; col++)
                {
                    var k = row * cols + col;
                    if (tested[k]) continue;
                    var at = Camera.ToWorld(new Vector2((col + 0.5f) * ShadowCell, (row + 0.5f) * ShadowCell));
                    if (Vec2.Distance(at, o.At) > o.Reach || Clear(o, at)) continue;
                    tested[k] = true;
                    var seen = false;
                    foreach (var other in sensors)
                        if (Vec2.Distance(at, other.At) <= other.Reach && Clear(other, at)) { seen = true; break; }
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

    /// <summary>
    /// Every cell on screen: seen well enough to spot a ship coasting cold, seen only for a warm ship,
    /// hidden behind a body from every sensor that reaches it, or out of reach (left dark).
    /// </summary>
    private void WorkOutCoverage(List<Observer> sensors, Func<Observer, Vec2, bool> clear)
    {
        var size = Camera.ScreenSize;
        var cols = (int)Math.Ceiling(size.X / ShadowCell);
        var rows = (int)Math.Ceiling(size.Y / ShadowCell);
        var colors = new[] { (Color?)null, SeesWarm, SeesCold, ShadowColor };
        for (var row = 0; row < rows; row++)
        {
            var start = 0;
            var last = -1;
            for (var col = 0; col <= cols; col++)
            {
                var state = 0;
                if (col < cols)
                {
                    var at = Camera.ToWorld(new Vector2((col + 0.5f) * ShadowCell, (row + 0.5f) * ShadowCell));
                    var reached = false;
                    foreach (var o in sensors)
                    {
                        var d = Vec2.Distance(at, o.At);
                        if (d > o.Reach) continue;
                        reached = true;
                        if (!clear(o, at)) continue;
                        state = Math.Max(state, d <= o.ColdReach ? 2 : 1);
                        if (state == 2) break;
                    }
                    if (state == 0 && reached) state = 3;
                }
                else state = -1;
                if (state == last) continue;
                if (last > 0 && colors[last] is Color c)
                    _shadowRuns.Add((new Rect2(start * ShadowCell, row * ShadowCell, (col - start) * ShadowCell, ShadowCell), c));
                start = col;
                last = state;
            }
        }
    }
}
