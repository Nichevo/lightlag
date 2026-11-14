using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The operations map on the fleets screen (K): a flat chart of the system showing what a list of fleets can't:
/// your zones, patrol loops and cargo runs, where enemy fleets were last seen and how long ago, your fleets as
/// counters, and rings round places nobody covers (red, dashed) or that are thinly covered (amber). The picked
/// fleet is ringed with how far it gets in five days at full burn on the fuel it has. Wheel zooms, right-drag
/// pans, a click picks the nearest fleet or place. Only draws: orders go through the screen beside it.
/// </summary>
public partial class OpsMap : Control
{
    /// <summary>The screen this map belongs to: the world, the picked fleet, the judged places, and what to do on a click.</summary>
    public Func<World?> GetWorld { get; set; } = () => null;
    public Func<int?> Picked { get; set; } = () => null;
    public Func<string?> PickedPlace { get; set; } = () => null;
    public Func<IReadOnlyList<CoverJudgement>> Judged { get; set; } = () => Array.Empty<CoverJudgement>();
    public Func<IReadOnlyCollection<int>> Ticked { get; set; } = () => Array.Empty<int>();
    public Action<int> OnPickFleet { get; set; } = _ => { };
    public Action<string> OnPickPlace { get; set; } = _ => { };
    public HashSet<string> Layers { get; set; } = new();

    private float _zoom = 1;
    private Vector2 _pan;
    private bool _dragging;

    /// <summary>Last frame's chart: world centre, pixels per Mm, and the screen spots of fleets and places (for clicks).</summary>
    private Vec2 _centre;
    private double _scale = 1;
    private readonly List<(Vector2 At, int FleetId)> _fleetSpots = new();
    private readonly List<(Vector2 At, string Key)> _placeSpots = new();

    private static readonly Color Warn = new("ffb347");
    private static readonly Color Good = new("6fdc8c");
    private static readonly Color Grid = new(0.45f, 0.55f, 0.7f, 0.18f);

    public OpsMap()
    {
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(420, 360);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        TooltipText = "Wheel to zoom, right-drag to pan, click a fleet or a place to pick it, double-click to reset the view";
    }

    public override void _Process(double delta)
    {
        if (IsVisibleInTree()) QueueRedraw();
    }

    private Vector2 ToScreen(Vec2 p) => Size / 2 + _pan + new Vector2((float)((p.X - _centre.X) * _scale), (float)(-(p.Y - _centre.Y) * _scale));

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } up:
                ZoomAt(up.Position, 1.2f);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } down:
                ZoomAt(down.Position, 1 / 1.2f);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } r:
                _dragging = r.Pressed;
                AcceptEvent();
                break;
            case InputEventMouseMotion m when _dragging:
                _pan += m.Relative;
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click:
                AcceptEvent();
                if (click.DoubleClick) { _zoom = 1; _pan = Vector2.Zero; break; }
                Main.Instance.RunGuarded("ops map click", () => Click(click.Position));
                break;
        }
    }

    private void ZoomAt(Vector2 at, float factor)
    {
        var z = Math.Clamp(_zoom * factor, 0.25f, 40f);
        factor = z / _zoom;
        // Keep the point under the mouse where it is.
        var fromCentre = at - Size / 2 - _pan;
        _pan -= fromCentre * (factor - 1);
        _zoom = z;
    }

    private void Click(Vector2 at)
    {
        // Fleets first (they sit on top of places), then the nearest place.
        var fleet = _fleetSpots.Where(s => s.At.DistanceTo(at) < 16).OrderBy(s => s.At.DistanceTo(at)).Select(s => (int?)s.FleetId).FirstOrDefault();
        var place = _placeSpots.Where(s => s.At.DistanceTo(at) < 20).OrderBy(s => s.At.DistanceTo(at)).Select(s => s.Key).FirstOrDefault();
        if (fleet is int id) OnPickFleet(id);
        else if (place != null) OnPickPlace(place);
    }

    public override void _Draw()
    {
        if (GetWorld() is not World w) return;
        var font = ThemeDB.FallbackFont;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.015f, 0.02f, 0.035f));
        _fleetSpots.Clear();
        _placeSpots.Clear();

        // Fit what matters: our planets and fleets, and enemies seen near them lately.
        var pts = new List<Vec2>();
        pts.AddRange(w.Bodies.Where(b => b.Owner == Side.Player).Select(b => b.Position));
        pts.AddRange(w.Fleets.Where(f => f.Side == Side.Player && f.ShipIds.Count > 0).Select(f => f.Position));
        pts.AddRange(Seen(w).Select(s => s.Position));
        if (pts.Count == 0) pts.Add(Vec2.Zero);
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        var span = Math.Max(Math.Max(maxX - minX, maxY - minY), 0.5 * Physics.MmPerAu);
        _centre = new Vec2((minX + maxX) / 2, (minY + maxY) / 2);
        _scale = Math.Min(Size.X, Size.Y) / (span * 1.25) * _zoom;

        // Orbits of the planets round the star, for bearings.
        var star = w.Bodies.FirstOrDefault(b => b.Kind == BodyKind.Star);
        if (star != null)
        {
            var sc = ToScreen(star.Position);
            foreach (var p in w.Bodies.Where(b => b.Kind == BodyKind.Planet && b.ParentId == star.Id))
                DrawArc(sc, (float)(Vec2.Distance(p.Position, star.Position) * _scale), 0, Mathf.Tau, 128, Grid, 1);
            DrawCircle(sc, 5, new Color("ffd27a"));
        }

        // Cargo runs.
        if (Layers.Contains("runs"))
        {
            foreach (var j in Judged().Where(j => j.Place.Kind == PlaceKind.Run))
                if (w.FindBody(j.Place.FromBodyId ?? -1) is Body a && w.FindBody(j.Place.ToBodyId ?? -1) is Body b)
                {
                    var col = StateColor(j.State);
                    DrawDashedLine(ToScreen(a.Position), ToScreen(b.Position), new Color(col, 0.7f), 2, 8);
                    var mid = ToScreen((a.Position + b.Position) * 0.5);
                    _placeSpots.Add((mid, j.Place.Key));
                    DrawString(font, mid + new Vector2(6, -6), $"{j.Place.Name} · {Coverage.StateName(j.State)}", HorizontalAlignment.Left, -1, 11, new Color(col, 0.9f));
                }
        }

        // Zones.
        if (Layers.Contains("zones"))
            foreach (var z in w.Zones.Where(z => z.Side == Side.Player))
            {
                var c = ToScreen(Lanes.CenterOf(w, z));
                var r = Math.Max(4f, (float)(z.RadiusMm * _scale));
                DrawCircle(c, r, new Color(Ui.Player, 0.07f));
                DrawArc(c, r, 0, Mathf.Tau, 64, new Color(Ui.Player, 0.5f), 1);
                DrawString(font, c + new Vector2(r + 3, 4), z.Name, HorizontalAlignment.Left, -1, 11, new Color(Ui.Player, 0.8f));
            }

        // Bodies: planets and moons we, the rival or nobody hold.
        foreach (var b in w.Bodies.Where(b => b.Kind != BodyKind.Star && b.Kind != BodyKind.Station))
        {
            var at = ToScreen(b.Position);
            if (at.X < -50 || at.Y < -50 || at.X > Size.X + 50 || at.Y > Size.Y + 50) continue;
            var moon = b.Kind == BodyKind.Moon;
            var col = b.Owner is Side s ? Ui.SideColor(s) : new Color(b.Color);
            DrawCircle(at, moon ? 2.5f : 4.5f, col);
            if (!moon || _zoom > 3 || b.Owner == Side.Player)
                DrawString(font, at + new Vector2(6, moon ? 12 : 4), b.Name, HorizontalAlignment.Left, -1, moon ? 10 : 12, new Color(col, moon ? 0.7f : 0.95f));
        }

        // Cover rings: red dashed for open, amber for thin, a faint green for covered.
        if (Layers.Contains("cover"))
            foreach (var j in Judged().Where(j => j.Place.Kind != PlaceKind.Run))
            {
                var at = ToScreen(Coverage.Where(w, j.Place));
                _placeSpots.Add((at, j.Place.Key));
                var picked = PickedPlace() == j.Place.Key;
                var col = StateColor(j.State);
                if (j.State == CoverState.Open) DashedRing(at, 16, col, picked ? 3 : 2);
                else DrawArc(at, 14, 0, Mathf.Tau, 48, new Color(col, j.State == CoverState.Covered ? 0.35f : 0.8f), picked ? 3 : 1.5f);
                if (j.State != CoverState.Covered)
                    DrawString(font, at + new Vector2(18, -10), Coverage.StateName(j.State).ToUpperInvariant(), HorizontalAlignment.Left, -1, 11, col);
            }

        // Patrol loops.
        if (Layers.Contains("loops"))
            foreach (var f in w.Fleets.Where(f => f.Side == Side.Player && f.Loop.Count > 0 && f.ShipIds.Count > 0))
            {
                var stops = f.Loop.Select(s => Operations.StopAt(w, s)).OfType<Vec2>().Select(ToScreen).ToList();
                var col = new Color("9fd6ff");
                for (var i = 0; i < stops.Count && stops.Count > 1; i++)
                    DrawDashedLine(stops[i], stops[(i + 1) % stops.Count], new Color(col, 0.6f), 1.5f, 5);
                for (var i = 0; i < stops.Count; i++)
                {
                    DrawCircle(stops[i], 6, new Color(0.02f, 0.03f, 0.05f));
                    DrawArc(stops[i], 6, 0, Mathf.Tau, 24, col, 1.5f);
                    DrawString(font, stops[i] + new Vector2(-3, 4), $"{i + 1}", HorizontalAlignment.Left, -1, 9, col);
                }
            }

        // Enemy last seen, fading with age.
        if (Layers.Contains("enemies"))
            foreach (var s in Seen(w))
            {
                var age = w.Hour - s.Hour;
                var fade = (float)Math.Clamp(1 - age / (Coverage.ThreatDays * 24), 0.25, 1);
                var at = ToScreen(s.Position);
                var col = new Color(Ui.SideColor(s.Of), fade);
                DrawColoredPolygon(new[] { at + new Vector2(0, -7), at + new Vector2(6, 5), at + new Vector2(-6, 5) }, col);
                DrawString(font, at + new Vector2(9, 4), $"{s.Name} ({s.Ships}) · {(s.Visible ? "now" : Coverage.Age(age) + " ago")}",
                    HorizontalAlignment.Left, -1, 11, col);
            }

        // The picked fleet's reach: how far five days at full burn takes it on its fuel.
        var pickedId = Picked();
        if (Layers.Contains("reach") && pickedId is int pid && w.FindFleet(pid) is Fleet pf && pf.ShipIds.Count > 0)
        {
            var reach = ReachMm(w, pf, 5 * 24);
            if (reach > 0)
            {
                var at = ToScreen(pf.Position);
                var r = (float)(reach * _scale);
                DrawArc(at, r, 0, Mathf.Tau, 96, new Color(Ui.Player, 0.35f), 1);
                DrawString(font, at + new Vector2(r * 0.71f + 4, -r * 0.71f), "5 days at full burn", HorizontalAlignment.Left, -1, 10, new Color(Ui.Player, 0.6f));
            }
        }

        // Our fleets: counters with their ship count.
        var ticked = Ticked();
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player && f.ShipIds.Count > 0).OrderBy(f => f.Id))
        {
            var at = ToScreen(f.Position);
            _fleetSpots.Add((at, f.Id));
            var picked = f.Id == pickedId;
            var col = f.Retreating ? Ui.Danger : Ui.Player;
            var rect = new Rect2(at - new Vector2(9, 7), new Vector2(18, 14));
            DrawRect(rect, new Color(0.02f, 0.05f, 0.09f));
            DrawRect(rect, col, false, picked ? 2.5f : 1.2f);
            if (ticked.Contains(f.Id)) DrawRect(rect.Grow(3), Warn, false, 1.2f);
            DrawString(font, at + new Vector2(-8, 5), $"{f.ShipIds.Count}", HorizontalAlignment.Center, 16, 11, col);
            if (f.Flight is { } fl)
                DrawDashedLine(at, ToScreen(fl.Target), new Color(col, 0.45f), 1, 4);
            if (picked || _zoom > 1.5f)
                DrawString(font, at + new Vector2(12, -8), f.Name, HorizontalAlignment.Left, -1, 11, new Color(col, picked ? 1 : 0.75f));
        }

        // Scale bar.
        var bar = (float)(Physics.MmPerAu * 0.5 * _scale);
        var label = "0.5 AU";
        if (bar > Size.X * 0.4f) { bar /= 5; label = "0.1 AU"; }
        var origin = new Vector2(14, Size.Y - 14);
        DrawLine(origin, origin + new Vector2(bar, 0), Ui.Dim, 1);
        DrawString(font, origin + new Vector2(0, -4), label, HorizontalAlignment.Left, -1, 10, Ui.Dim);
    }

    private void DashedRing(Vector2 at, float r, Color col, float width)
    {
        const int n = 16;
        for (var i = 0; i < n; i++)
        {
            var a0 = Mathf.Tau * i / n;
            DrawArc(at, r, a0, a0 + Mathf.Tau / n * 0.6f, 6, col, width);
        }
    }

    private static Color StateColor(CoverState s) => s switch { CoverState.Covered => Good, CoverState.Thin => Warn, _ => Ui.Danger };

    /// <summary>Enemy fleets seen lately (warships and raiders, not cargo ships), newest first.</summary>
    private static IEnumerable<Sighting> Seen(World w) =>
        w.Sightings.Where(s => s.SeenBy == Side.Player && !s.Own && !Lanes.CargoSighting(s) && w.Hour - s.Hour <= Coverage.ThreatDays * 24)
            .OrderBy(s => s.FleetId);

    /// <summary>How far the fleet gets in this many hours at full burn (or the burn its fuel allows).</summary>
    private static double ReachMm(World w, Fleet f, double hours)
    {
        var perf = FleetPerformance.Of(w, f);
        if (perf.MaxSpeed <= 0 || perf.Accel <= 0) return 0;
        var v = Math.Min(perf.MaxSpeed, perf.DeltaVLeft / 2);
        if (v <= 0) return 0;
        var a = perf.Accel;
        var tAcc = v / a;
        if (hours <= 2 * tAcc) return a * (hours / 2) * (hours / 2);
        return v * v / a + v * (hours - 2 * tAcc);
    }
}
