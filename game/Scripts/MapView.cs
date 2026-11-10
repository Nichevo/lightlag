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

    /// <summary>Fleets picked together (box or Shift+click), drawn with a ring each.</summary>
    public IReadOnlyCollection<int> GroupIds = Array.Empty<int>();

    /// <summary>Waypoints of the route being planned (drawn as numbered diamonds you can drag).</summary>
    public IReadOnlyList<WaypointDraft>? PlannerWaypoints;

    /// <summary>Debug (F7): draw every fleet, seen or not, and enemy routes.</summary>
    public bool RevealAll;

    /// <summary>The box being dragged out to pick fleets, in screen pixels.</summary>
    public Rect2? SelectionBox;

    /// <summary>Which way each fleet's ships are drawn pointing; turns towards the fleet's facing over time.</summary>
    private readonly Dictionary<int, double> _shownFacing = new();

    /// <summary>How fast ship icons swing round to a new facing, in radians per real second.</summary>
    private const double TurnRate = 5;

    /// <summary>The game hour being drawn (between the last step and the current one).</summary>
    public double DrawHour;

    /// <summary>The route the planner is showing, if any.</summary>
    public FlightPlan? Preview;

    /// <summary>0..1: how far between the previous tick and the current one to draw.</summary>
    public double Blend = 1;

    private readonly Dictionary<int, double> _prevAngle = new();
    private readonly Dictionary<int, Vec2> _prevShip = new();
    private readonly Dictionary<int, Vec2> _prevFleet = new();
    private readonly Dictionary<int, Vec2> _prevMissile = new();
    private readonly Dictionary<int, Vec2> _drawn = new();
    private Font _font = null!;

    public override void _Ready() => _font = ThemeDB.FallbackFont;

    /// <summary>Call just before each simulation step, to remember where things were.</summary>
    public void BeforeStep()
    {
        foreach (var b in Sim.World.Bodies) _prevAngle[b.Id] = b.Angle;
        foreach (var s in Sim.World.Ships) _prevShip[s.Id] = s.Position;
        foreach (var f in Sim.World.Fleets) _prevFleet[f.Id] = f.Position;
        _prevMissile.Clear();
        foreach (var m in Sim.World.Missiles) _prevMissile[m.Id] = m.Position;
    }

    /// <summary>Swing each fleet's drawn facing towards its real one, so a flip to brake is seen as a turn.</summary>
    public void TurnShips(double delta)
    {
        foreach (var f in Sim.World.Fleets)
        {
            if (!_shownFacing.TryGetValue(f.Id, out var shown)) { _shownFacing[f.Id] = f.Facing; continue; }
            var diff = Math.IEEERemainder(f.Facing - shown, Math.Tau);
            var step = TurnRate * delta;
            _shownFacing[f.Id] = Math.Abs(diff) <= step ? f.Facing : shown + Math.Sign(diff) * step;
        }
    }

    /// <summary>Forget the previous tick (after loading, so nothing blends from the old game).</summary>
    public void ResetBlend()
    {
        _prevAngle.Clear();
        _prevShip.Clear();
        _prevFleet.Clear();
        _shownFacing.Clear();
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
            // Held planets take their side's colour; a shipyard is marked; a claim shows as a filling ring.
            var side = b.Owner is Side o ? (o == Side.Player ? Ui.Player : Ui.Rival) : (Color?)null;
            var color = b.Id == SelectedId ? Ui.Text : b.Id == HoverId ? new Color(Ui.Text, 0.85f) : side is Color sc ? new Color(sc, 0.85f) : Ui.Dim;
            var name = b.Name + (b.Shipyard ? " · shipyard" : "");
            if (b.Shipyard && b.LastBombardedHour is double hit && w.Hour - hit <= 24) name += " · under attack";
            if (b.ClaimSide is Side claimer)
            {
                var share = (float)(b.ClaimHours / (GameData.Economy.ClaimDays * 24));
                var cc = claimer == Side.Player ? Ui.Player : Ui.Rival;
                DrawArc(at, r + 9, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * share, 40, cc, 2.5f, true);
                DrawArc(at, r + 9, 0, Mathf.Tau, 40, new Color(cc, 0.2f), 1, true);
                name += $" · claiming {share * 100:0} %";
            }
            var gap = b.ClaimSide != null ? 14 : 5; // clear of the claim ring
            DrawString(_font, at + new Vector2(r + gap, 5), name, HorizontalAlignment.Left, -1, b.Kind == BodyKind.Moon ? 12 : 14, color);
            if (b.Id == SelectedId) DrawArc(at, r + 5, 0, Mathf.Tau, 48, new Color(Ui.Text, 0.8f), 1.2f, true);
        }

        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player || RevealAll)) DrawRoute(f, f.Flight, false);
        foreach (var (fleet, flight) in PendingFlights()) DrawRoute(fleet, flight, false);
        if (Preview?.Flight is Flight preview && SelectedId is int sel && w.FindFleet(sel) is Fleet pf)
            DrawRoute(pf, preview, true, Preview.Ok);
        DrawPlannerWaypoints();
        if (SelectedId is int selected && w.FindFleet(selected) is Fleet sf && Shown(sf)) DrawRanges(sf);
        DrawLastSeen();
        foreach (var f in w.Fleets.Where(Shown)) DrawFleet(f);
        DrawMissiles();
        if (SelectionBox is Rect2 box)
        {
            DrawRect(box, new Color(Ui.Player, 0.08f));
            DrawRect(box, new Color(Ui.Player, 0.7f), false, 1);
        }
    }

    /// <summary>Weapon reach of the selected fleet: railguns, missiles and contact.</summary>
    private void DrawRanges(Fleet f)
    {
        if (!_drawn.TryGetValue(f.Id, out var p) || !p.IsFinite) return;
        var at = Camera.ToScreen(p);
        var color = f.Side == Side.Player ? Ui.Player : Ui.Rival;
        var c = GameData.Combat;
        var ships = Sim.World.ShipsOf(f).ToList();
        void Ring(double mm, float alpha, string label)
        {
            var r = (float)(mm * Camera.Scale);
            if (r < 12 || r > 5000) return;
            DrawArc(at, r, 0, Mathf.Tau, 96, new Color(color, alpha), 1, true);
            DrawString(_font, at + new Vector2(6, -r - 4), label, HorizontalAlignment.Left, -1, 13, new Color(color, alpha + 0.45f));
        }
        string Km(double mm) => $"{mm * 1000:#,0} km";
        Ring(c.ContactRangeMm, 0.12f, $"Contact {Km(c.ContactRangeMm)}: battle time starts");
        if (ships.Any(s => s.Missiles > 0)) Ring(c.Missile.LaunchRangeMm, 0.2f, $"Missile launch {Km(c.Missile.LaunchRangeMm)}");
        if (ships.Any(s => GameData.ShipClass(s.ClassId).Railguns > 0)) Ring(c.Railgun.RangeMm, 0.28f, $"Railguns {Km(c.Railgun.RangeMm)}");
        if (f.Side == Side.Player)
        {
            // How far this fleet's sensors pick up a single cold frigate.
            var cold = GameData.ShipClasses.Values.Min(k => k.SignatureSize) * GameData.Sensors.Cold;
            var sees = Sensors.Range(Sensors.SensorOf(Sim.World, f), cold);
            var r = (float)(sees * Camera.Scale);
            if (r >= 12 && r <= 5000)
            {
                DrawArc(at, r, 0, Mathf.Tau, 96, new Color(Ui.Text, 0.12f), 1, true);
                DrawString(_font, at + new Vector2(6, r + 14), $"Sees a cold frigate {Km(sees)}", HorizontalAlignment.Left, -1, 13, new Color(Ui.Text, 0.45f));
            }
        }
    }

    private void DrawMissiles()
    {
        foreach (var m in Sim.World.Missiles)
        {
            var p = m.Position;
            if (_prevMissile.TryGetValue(m.Id, out var prev)) p = prev + (p - prev) * Blend;
            var at = Camera.ToScreen(p);
            if (!Camera.OnScreen(at, 20)) continue;
            var color = m.Side == Side.Player ? Ui.Player : Ui.Rival;
            // A short tail pointing back along its path relative to the target, so salvos read as streaks.
            var tail = Camera.ToScreen(p - m.Velocity.Normalized() * (12 / Camera.Scale));
            DrawLine(tail, at, new Color(color, 0.5f), 1.2f, true);
            DrawCircle(at, 2f, color.Lightened(0.4f));
        }
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
        var hours = DrawHour;
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
        var heading = (float)(_shownFacing.TryGetValue(f.Id, out var shown) ? shown : f.Facing);
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
        if (apart && SpeedTag(f) is { Length: > 0 } tag)
            DrawString(_font, at + new Vector2(12, 22), $"{f.Name}{tag}", HorizontalAlignment.Left, -1, 12, color);
        if (!apart)
        {
            // Zoomed out: the fleet is one icon with a count.
            var label = (ships.Count > 1 ? $"{f.Name} ({ships.Count})" : $"{f.Name}: {ships.FirstOrDefault()?.Name}") + SpeedTag(f);
            if (ships.Count > 1) DrawString(_font, at + new Vector2(-4, 16), ships.Count.ToString(), HorizontalAlignment.Left, -1, 11, color);
            if (f.Id == SelectedId || f.Id == HoverId || Camera.Scale > 0.01)
                DrawString(_font, at + new Vector2(12, -8), label, HorizontalAlignment.Left, -1, 12, color);
        }
        if (f.Id == SelectedId || GroupIds.Contains(f.Id))
        {
            var radius = apart ? (float)(Fleets.FormationSpacing * 1.6 * Camera.Scale) + 14 : 14f;
            DrawArc(at, radius, 0, Mathf.Tau, 48, color, 1.5f, true);
        }
    }

    private void DrawPlannerWaypoints()
    {
        if (PlannerWaypoints == null) return;
        for (var i = 0; i < PlannerWaypoints.Count; i++)
        {
            var wp = PlannerWaypoints[i];
            var at = Camera.ToScreen(wp.Point);
            DrawDiamond(at, 8, Ui.Player, wp.Stop);
            var label = $"{i + 1}" + (wp.Stop ? " stop" + (wp.WaitHours > 0 ? $", wait {wp.WaitHours:0} h" : "") : "")
                        + (wp.Speed is double sp ? $" · {sp:P0} speed" : "");
            DrawString(_font, at + new Vector2(12, -8), label, HorizontalAlignment.Left, -1, 13, Ui.Player);
        }
    }

    private void DrawDiamond(Vector2 at, float r, Color color, bool filled)
    {
        var pts = new[] { at + new Vector2(0, -r), at + new Vector2(r, 0), at + new Vector2(0, r), at + new Vector2(-r, 0), at + new Vector2(0, -r) };
        if (filled) DrawColoredPolygon(pts[..4], new Color(color, 0.6f));
        DrawPolyline(pts, color, 1.5f, true);
    }

    /// <summary>The planner waypoint under a screen point, if any.</summary>
    public int? HitTestWaypoint(Vector2 screen)
    {
        if (PlannerWaypoints == null) return null;
        for (var i = PlannerWaypoints.Count - 1; i >= 0; i--)
            if (Camera.ToScreen(PlannerWaypoints[i].Point).DistanceTo(screen) <= 12) return i;
        return null;
    }

    public static readonly Color SpeedUp = new("6be38a");
    public static readonly Color Braking = new("c48bff");

    /// <summary>Green where a burn is speeding the fleet up, violet where it is braking.</summary>
    private static Color BurnColor(FlightLeg leg, double hour) =>
        Vec2.Dot(leg.A, leg.VelocityAt(hour)) >= 0 ? SpeedUp : Braking;

    /// <summary>Waypoints of a flight not reached yet (older saves only have the points).</summary>
    private List<Vec2> AheadPoints(Flight flight) => flight.WaypointPlan.Count > 0
        ? flight.WaypointsAhead(DrawHour).Select(w => w.Point).ToList()
        : flight.Waypoints;

    /// <summary>A not-yet-reached waypoint of the selected fleet's current route under a screen point.</summary>
    public int? HitTestFlightWaypoint(Vector2 screen)
    {
        if (SelectedId is not int id || Sim.World.FindFleet(id) is not { Side: Side.Player, Flight: Flight flight }) return null;
        if (flight.WaypointPlan.Count == 0) return null;
        var ahead = flight.WaypointsAhead(Sim.World.Hour).Select(w => w.Point).ToList();
        for (var i = ahead.Count - 1; i >= 0; i--)
            if (Camera.ToScreen(ahead[i]).DistanceTo(screen) <= 12) return i;
        return null;
    }

    /// <summary>Is this fleet drawn: yours, an enemy your side can see, or everything in debug view.</summary>
    public bool Shown(Fleet f) => f.Side == Side.Player || RevealAll || Sensors.Sees(Sim.World, Side.Player, f);

    /// <summary>Enemy fleets out of sight: a faint mark where they were last seen, and a dashed guess of where they went.</summary>
    private void DrawLastSeen()
    {
        var w = Sim.World;
        foreach (var s in w.Sightings)
        {
            if (s.SeenBy != Side.Player || s.Visible) continue;
            if (RevealAll && w.FindFleet(s.FleetId) != null) continue;
            var at = Camera.ToScreen(s.Position);
            var guess = Camera.ToScreen(s.Guess(DrawHour));
            if (!Camera.OnScreen(at, 300) && !Camera.OnScreen(guess, 300)) continue;
            var color = new Color(Ui.Rival, 0.45f);
            if (at.DistanceTo(guess) > 4) DrawDashedLine(at, guess, new Color(Ui.Rival, 0.3f), 1, 5);
            DrawArc(at, 9, 0, Mathf.Tau, 20, color, 1.2f, true);
            DrawLine(at + new Vector2(-5, -5), at + new Vector2(5, 5), color, 1.2f);
            DrawLine(at + new Vector2(-5, 5), at + new Vector2(5, -5), color, 1.2f);
            var ago = Format.Hours(Math.Max(0, DrawHour - s.Hour));
            if (s.FleetId == SelectedId || s.FleetId == HoverId || Camera.Scale > 0.01)
                DrawString(_font, at + new Vector2(12, -8), $"{s.Name} ({s.Ships}): last seen {ago} ago", HorizontalAlignment.Left, -1, 12, color);
            if (s.FleetId == SelectedId) DrawArc(at, 15, 0, Mathf.Tau, 32, Ui.Rival, 1.5f, true);
        }
    }

    /// <summary>" · 12 km/s" for a moving fleet in contact with an enemy (or selected), else "".</summary>
    private string SpeedTag(Fleet f)
    {
        var speed = f.Velocity.Length;
        if (speed < 1e-6) return f.Id == SelectedId || InContact(f) ? " · stopped" : "";
        return f.Id == SelectedId || InContact(f) || f.InFlight ? $" · {Format.Speed(speed)}" : "";
    }

    private bool InContact(Fleet f) =>
        Sim.World.Fleets.Any(o => o.Side != f.Side && Vec2.Distance(o.Position, f.Position) <= GameData.Combat.ContactRangeMm);

    private void DrawShipIcon(Vector2 at, Ship s, Color color, float heading)
    {
        // Damaged ships darken; a ship with a part knocked out gets a red mark.
        var share = (float)(s.Armour / GameData.ShipClass(s.ClassId).Armour);
        color = color.Lerp(new Color(0.35f, 0.35f, 0.4f), Math.Clamp(1 - share, 0, 1) * 0.7f);
        if (s.Disabled != null) DrawCircle(at + new Vector2(0, -10), 2.5f, Ui.Danger);
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
        var now = DrawHour;
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
            if (!leg.IsBurn)
            {
                DrawPolyline(pts, new Color(baseColor, alpha * 0.55f), 1.2f, true);
                continue;
            }
            // Your own routes show what the engines do: speeding up or braking (a turn brakes, then speeds up).
            var colored = ok && f.Side == Side.Player;
            var colors = new Color[n];
            for (var i = 0; i < n; i++)
                colors[i] = colored ? BurnColor(leg, from + (leg.End - from) * (i + 0.5) / n) : baseColor;
            var startAt = 0;
            for (var i = 1; i <= n; i++)
            {
                if (i < n && colors[i] == colors[startAt]) continue;
                DrawPolyline(pts[startAt..(i + 1)], new Color(colors[startAt], alpha), 2.5f, true);
                startAt = i;
            }
            // Burns are short next to the coasts: zoomed out, mark each with a dot so it can still be seen.
            if (pts[0].DistanceTo(pts[n]) < 10) DrawCircle(pts[n / 2], 3.5f, new Color(colors[n / 2], Math.Min(1f, alpha + 0.2f)));
        }
        var end = Camera.ToScreen(flight.Target);
        DrawArc(end, 5, 0, Mathf.Tau, 16, new Color(baseColor, alpha), 1.2f, true);
        if (!preview)
        {
            var ahead = AheadPoints(flight);
            var stops = flight.WaypointPlan.Count > 0 ? flight.WaypointsAhead(DrawHour).Select(w => w.Stop).ToList() : null;
            var mine = f.Id == SelectedId && f.Side == Side.Player && PlannerWaypoints == null;
            for (var i = 0; i < ahead.Count; i++)
            {
                var at = Camera.ToScreen(ahead[i]);
                DrawDiamond(at, mine ? 7 : 5, new Color(baseColor, mine ? 0.9f : alpha), stops?[i] == true);
                if (mine) DrawString(_font, at + new Vector2(10, -6), $"{i + 1}", HorizontalAlignment.Left, -1, 12, new Color(baseColor, 0.9f));
            }
            if (mine && ahead.Count > 0)
                DrawString(_font, Camera.ToScreen(ahead[^1]) + new Vector2(10, 10), "drag to change", HorizontalAlignment.Left, -1, 11, new Color(baseColor, 0.6f));
        }
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
            var (fleetId, request) = c switch
            {
                PlanFlightCommand pf => (pf.FleetId, pf.Request),
                SendAheadCommand sa => (sa.FleetId, sa.Request),
                _ => (0, (FlightRequest?)null),
            };
            if (request != null && Sim.World.FindFleet(fleetId) is Fleet f
                && FlightPlanner.Plan(Sim.World, f, request) is { Ok: true, Flight: Flight flight })
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
            if (Sim.World.FleetOf(s) is not Fleet owner || !Shown(owner)) continue;
            if (!_drawn.TryGetValue(s.FleetId, out var fp) || !fp.IsFinite) continue;
            var p = ShipsApart && _drawn.TryGetValue(s.Id, out var sp) ? sp : fp;
            var d = Camera.ToScreen(p).DistanceTo(screen);
            if (d < bestD) { bestD = d; best = s.FleetId; }
        }
        foreach (var sight in Sim.World.Sightings)
        {
            if (sight.SeenBy != Side.Player || sight.Visible) continue;
            var d = Camera.ToScreen(sight.Position).DistanceTo(screen);
            if (d < bestD) { bestD = d; best = sight.FleetId; }
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
