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

    /// <summary>Just the scenery (the title screen's backdrop): no labels, routes, bars, marks or panels.</summary>
    public bool Quiet;

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

    private readonly Dictionary<int, Vec2> _prevShip = new();
    private readonly Dictionary<int, Vec2> _prevFleet = new();
    private readonly Dictionary<int, Vec2> _prevMissile = new();
    private readonly Dictionary<int, Vec2> _drawn = new();
    private Font _font = null!;

    public override void _Ready() => _font = ThemeDB.FallbackFont;

    /// <summary>Call just before each simulation step, to remember where things were.</summary>
    public void BeforeStep()
    {
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
            // Bodies move once per game hour in the simulation, but in battle time an hour takes many
            // steps: draw them where they are at the drawn hour, so they glide instead of jumping.
            var angle = b.Angle;
            if (b.OrbitPeriodDays > 0) angle += Math.Tau * (DrawHour - w.Tick) / (b.OrbitPeriodDays * 24);
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
        ApplyLag();
        SyncPlanets();
    }

    private readonly Dictionary<int, PlanetSprite> _planets = new();

    /// <summary>Move each planet and moon sprite to where its body is drawn this frame.</summary>
    private void SyncPlanets()
    {
        var w = Sim.World;
        var star = w.Bodies.FirstOrDefault(b => b.ParentId == null);
        var starAt = star != null ? _drawn[star.Id] : Vec2.Zero;
        foreach (var gone in _planets.Keys.Where(id => w.FindBody(id) == null).ToList())
        {
            _planets[gone].QueueFree();
            _planets.Remove(gone);
        }
        SyncSun(star);
        SyncBelts();
        var spin = (float)(Math.Tau * DrawHour / (24 * 30));
        var cloudTime = (float)(DrawHour % 100000);
        foreach (var b in w.Bodies)
        {
            if (b.Kind == BodyKind.Star) continue;
            if (!_planets.TryGetValue(b.Id, out var sprite))
            {
                sprite = PlanetSprite.For(b);
                _planets[b.Id] = sprite;
                AddChild(sprite);
            }
            var at = Camera.ToScreen(_drawn[b.Id]);
            var r = (float)ScreenRadius(b);
            sprite.Visible = IsVisible(b) && Camera.OnScreen(at, r * 1.2f + 4);
            if (!sprite.Visible) continue;
            var toStar = starAt - _drawn[b.Id];
            sprite.Set(at, r, new Vector2((float)toStar.X, (float)toStar.Y), spin + (float)(b.Id * 1.3), cloudTime);
            if (Graphics.Fancy) sprite.SetLights((float)EclipseOf(b, starAt), CityOf(b));
        }
    }

    public override void _Draw()
    {
        if (Sim == null) return;
        var w = Sim.World;
        var star = w.Bodies.FirstOrDefault(b => b.ParentId == null);

        var fancy = Graphics.Fancy;
        BeginFx();
        if (!fancy) foreach (var belt in w.Belts) DrawBelt(belt);
        if (fancy) DrawWells();

        foreach (var b in w.Bodies)
        {
            if (b.ParentId is not int pid || !IsVisible(b)) continue;
            if (fancy) DrawOrbitFading(_drawn[pid], b.OrbitRadius, b.Kind == BodyKind.Moon ? 0.16f : 0.22f, _drawn[b.Id], ScreenRadius(b) + 2);
            else DrawOrbit(_drawn[pid], b.OrbitRadius, b.Kind == BodyKind.Moon ? 0.16f : 0.22f, _drawn[b.Id], ScreenRadius(b) + 2);
        }

        if (!Quiet) DrawShadows();
        if (fancy && star != null) DrawComet(star);

        foreach (var b in w.Bodies)
        {
            if (!IsVisible(b)) continue;
            var at = Camera.ToScreen(_drawn[b.Id]);
            var r = (float)ScreenRadius(b);
            if (!Camera.OnScreen(at, r + 200)) continue;
            if (b.Kind == BodyKind.Star && !fancy) DrawStar(b, at, r); // planets and moons are PlanetSprite children; on Medium and High so is the star
        }

        foreach (var b in w.Bodies)
        {
            if (Quiet || !IsVisible(b) || !ShowLabel(b)) continue;
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
                var share = (float)(b.ClaimHours / Economy.ClaimHoursFor(b));
                var cc = claimer == Side.Player ? Ui.Player : Ui.Rival;
                DrawArc(at, r + 9, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * share, 40, cc, 2.5f, true);
                DrawArc(at, r + 9, 0, Mathf.Tau, 40, new Color(cc, 0.2f), 1, true);
                name += $" · claiming {share * 100:0} %";
            }
            var gap = b.ClaimSide != null ? 14 : 5; // clear of the claim ring
            DrawString(_font, at + new Vector2(r + gap, 5), name, HorizontalAlignment.Left, -1, b.Kind == BodyKind.Moon ? 12 : 14, color);
            if (b.Id == SelectedId) DrawArc(at, r + 5, 0, Mathf.Tau, 48, new Color(Ui.Text, 0.8f), 1.2f, true);
        }

        if (!Quiet)
        {
            foreach (var f in w.Fleets.Where(f => f.Side == Side.Player || RevealAll)) DrawRoute(f, f.Flight, false);
            foreach (var (fleet, flight) in PendingFlights()) DrawRoute(fleet, flight, false);
            if (Preview?.Flight is Flight preview && SelectedId is int sel && w.FindFleet(sel) is Fleet pf)
                DrawRoute(pf, preview, true, Preview.Ok);
            DrawLanesAndZones();
            DrawPlannerWaypoints();
            if (Engagement == null && SelectedId is int selected && w.FindFleet(selected) is Fleet sf && Shown(sf)) DrawRanges(sf);
            RecordTracks();
            DrawTracks();
            DrawEngagement();
            DrawLastSeen();
            if (RevealAll) DrawMindJobs();
        }
        DrawWrecks();
        foreach (var f in w.Fleets.Where(Shown)) DrawFleet(f);
        if (!Quiet) DrawLag();
        if (fancy) DrawMissileSmoke();
        DrawMissiles();
        if (fancy) DrawEffectsFancy();
        else DrawEffects();
        EndFx();
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
        if (ships.Any(s => Fitting.Spec(s).Railguns > 0)) Ring(c.Railgun.RangeMm, 0.28f, $"Railguns {Km(c.Railgun.RangeMm)}");
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

    /// <summary>Recent shots: railgun tracers (bright on a hit), point defence killing missiles, missile strikes. Each fades over a game minute.</summary>
    private void DrawEffects()
    {
        const double life = 1.0 / 60; // one game minute
        foreach (var e in Sim.Effects)
        {
            var age = DrawHour - e.Hour;
            if (age < -1e-9 || age > life) continue;
            var fade = (float)(1 - age / life);
            var mine = e.Side == Side.Player ? Ui.Player : Ui.Rival;
            var from = Camera.ToScreen(e.From);
            var to = Camera.ToScreen(e.To);
            if (!Camera.OnScreen(to, 200) && !Camera.OnScreen(from, 200)) continue;
            switch (e.Kind)
            {
                case EffectKind.RailHit:
                    DrawLine(from, to, new Color(mine, 0.8f * fade), 1.5f);
                    DrawCircle(to, 3 + 4 * fade, new Color(1, 0.9f, 0.6f, 0.9f * fade));
                    break;
                case EffectKind.RailMiss:
                    DrawLine(from, to, new Color(mine, 0.3f * fade), 1);
                    break;
                case EffectKind.PointDefenceKill:
                    DrawDashedLine(from, to, new Color(mine, 0.7f * fade), 1, 3);
                    DrawArc(to, 3 + 6 * (1 - fade), 0, Mathf.Tau, 12, new Color(1, 1, 1, 0.9f * fade), 1.5f, true);
                    break;
                case EffectKind.Volley:
                {
                    // The fan of slugs: from the guns out to the target, as wide as the spread there.
                    var dir = (to - from).Normalized();
                    var across = new Vector2(-dir.Y, dir.X);
                    var half = Mathf.Max((float)(e.Width * Camera.Scale) / 2, e.Width > 1 ? 6f : 1.5f);
                    var tip = to + dir * half * 0.5f;
                    var incoming = e.Side != Side.Player;
                    var c = incoming ? new Color(1f, 0.4f, 0.35f) : mine;
                    DrawColoredPolygon(new[] { from, tip + across * half, tip - across * half }, new Color(c, 0.12f * fade));
                    DrawLine(from, tip + across * half, new Color(c, 0.4f * fade), 1);
                    DrawLine(from, tip - across * half, new Color(c, 0.4f * fade), 1);
                    break;
                }
                case EffectKind.MissileHit:
                    DrawCircle(to, 4 + 10 * (1 - fade), new Color(1, 0.6f, 0.2f, 0.8f * fade));
                    break;
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
        // Our own dropped missiles drifting dark (the enemy's are hidden): a dim ring, with the time left to wake.
        var w = Sim.World;
        var shown = 0;
        foreach (var m in w.Dropped)
        {
            if (m.Side != Side.Player && !RevealAll) continue;
            var at = Camera.ToScreen(m.Position);
            if (!Camera.OnScreen(at, 20)) continue;
            var color = m.Side == Side.Player ? Ui.Player : Ui.Rival;
            DrawArc(at, 3, 0, Mathf.Tau, 10, new Color(color, 0.6f), 1);
            if (shown++ == 0 || Camera.Scale > 2)
                DrawString(_font, at + new Vector2(6, 4), m.WakeHour > w.Hour ? $"wakes in {Format.Hours(m.WakeHour - w.Hour)}" : "armed",
                    HorizontalAlignment.Left, -1, 10, new Color(color, 0.7f));
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
    /// <summary>An orbit line, with a gap where its own body sits (the planet is drawn underneath).</summary>
    private void DrawOrbit(Vec2 parentWorld, double radiusMm, float alpha, Vec2 bodyWorld, double gapPx)
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
            var at = bodyWorld - parentWorld;
            var gap = Math.Min(Math.PI, gapPx / rPx);
            var start = (float)(Math.Atan2(at.Y, at.X) + gap);
            if (gap * rPx < 2) DrawArc(c, (float)rPx, 0, Mathf.Tau, segments, color, 1, true);
            else DrawArc(c, (float)rPx, start, start + (float)(Math.Tau - 2 * gap), segments, color, 1, true);
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
        // Leave a gap where the body sits.
        var bodyAt = Camera.ToScreen(bodyWorld);
        var run = new List<Vector2>();
        foreach (var p in pts)
        {
            if (p.DistanceTo(bodyAt) < gapPx)
            {
                if (run.Count >= 2) DrawPolyline(run.ToArray(), color, 1, true);
                run.Clear();
                continue;
            }
            run.Add(p);
        }
        if (run.Count >= 2) DrawPolyline(run.ToArray(), color, 1, true);
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
        // Enemy ships show as plain blips until a close look tells their class.
        var known = f.Side == Side.Player || RevealAll || Sensors.Find(Sim.World, Side.Player, f.Id, seat: true) is { Classes.Count: > 0 };
        if (apart)
        {
            foreach (var s in ships)
            {
                var p = Camera.ToScreen(_drawn.TryGetValue(s.Id, out var sp) ? sp : world);
                DrawShipOrBlip(p, s, color, heading, known);
                if (Quiet) continue;
                if (known && !CloseUp) DrawDamageStrip(p + new Vector2(-8, -16), new[] { s });
                if (Camera.Scale > 0.05 || f.Id == SelectedId)
                    DrawString(_font, p + new Vector2(12, -8), known ? s.Name : "unknown ship", HorizontalAlignment.Left, -1, 12, color);
            }
        }
        else
        {
            // Zoomed out: a tight cluster with one mark per ship (up to eight), so the count shows at a glance.
            var count = Math.Min(ships.Count, 8);
            for (var i = count - 1; i >= 0; i--)
                DrawShipOrBlip(at + ClusterOffset(i, heading), ships[i], color, heading, known);
            if (Quiet) return;
            // One strip for the fleet: the worst of each bar.
            if (known && ships.Count > 0) DrawDamageStrip(at + new Vector2(-8, -18), ships);
            if (ships.Count > count)
                DrawString(_font, at + new Vector2(-4, 26), $"+{ships.Count - count}", HorizontalAlignment.Left, -1, 11, color);
        }
        if (Quiet) return;
        if (apart && SpeedTag(f) is { Length: > 0 } tag)
            DrawString(_font, at + new Vector2(12, 22), $"{f.Name}{tag}", HorizontalAlignment.Left, -1, 12, color);
        if (!apart)
        {
            // Zoomed out: the fleet is one icon with a count.
            var label = (ships.Count > 1 ? $"{f.Name} ({ships.Count} ships)" : $"{f.Name}: {(known ? ships.FirstOrDefault()?.Name : "1 ship")}") + SpeedTag(f);
            if (f.Id == SelectedId || f.Id == HoverId || Camera.Scale > 0.01)
                DrawString(_font, at + new Vector2(12, -8), label, HorizontalAlignment.Left, -1, 12, color);
        }
        if (f.Side == Side.Player) DrawHeat(f, ships, at);
        if (f.Id == SelectedId || GroupIds.Contains(f.Id))
        {
            var radius = apart ? (float)(Fleets.FormationSpacing * 1.6 * Camera.Scale) + 14 : 14f;
            DrawArc(at, radius, 0, Mathf.Tau, 48, color, 1.5f, true);
        }
    }

    /// <summary>
    /// Stat bars under your ships: armour (green), supplies (yellow), shield (cyan) and heat (blue, orange
    /// when hot, red when overheated). Zoomed in, one set under each ship; zoomed out, one set for the
    /// fleet (average armour and shield, lowest supplies, hottest ship). Plus a word when radiators are in.
    /// </summary>
    private void DrawHeat(Fleet f, List<Ship> ships, Vector2 at)
    {
        if (ships.Count == 0) return;
        var w = Sim.World;
        if (ShipsApart)
            foreach (var s in ships)
                DrawBars(Camera.ToScreen(_drawn.TryGetValue(s.Id, out var sp) ? sp : f.Position) + new Vector2(-10, 12), new[] { s });
        else DrawBars(at + new Vector2(-10, 14), ships);
        var overheated = ships.Any(s => s.Overheated);
        var radiatorsIn = ships.Any(s => !Heat.RadiatorsOut(w, f, s));
        var tag = overheated ? "OVERHEATED" : f.Radiators == RadiatorMode.Cold ? (Heat.ColdClockHours(w, f) is double c ? $"running cold, {Format.Hours(c)} left" : "running cold")
            : radiatorsIn ? "radiators in" : null;
        if (tag != null && (f.Id == SelectedId || overheated || Camera.Scale > 0.01))
            DrawString(_font, at + new Vector2(12, 36), tag, HorizontalAlignment.Left, -1, 10, overheated ? Ui.Danger : new Color(Ui.Rival, 0.85f));
    }

    private void DrawBars(Vector2 origin, IReadOnlyList<Ship> ships)
    {
        var w = Sim.World;
        double Avg(Func<Ship, double> f) => ships.Average(f);
        var armour = Avg(s => Math.Max(0, s.Armour) / Math.Max(1, Tech.MaxArmour(w, s)));
        var supplies = ships.Min(s => s.Supplies / Math.Max(1e-9, Fitting.MaxSupplies(w, s)));
        var shieldMax = ships.Sum(s => Heat.MaxShield(w, s));
        var shield = shieldMax > 0 ? ships.Sum(s => s.Shield) / shieldMax : -1;
        var heat = ships.Max(s => Heat.Capacity(s) > 0 ? s.Heat / Heat.Capacity(s) : 0);
        var hot = ships.Any(s => s.Overheated);
        var bars = new List<(double Value, Color Color)>
        {
            (armour, armour < 0.5 ? Ui.Danger : new Color("6bd16b")),
            (supplies, supplies < 0.25 ? Ui.Danger : new Color("e8d35a")),
        };
        if (shield >= 0) bars.Add((shield, new Color("5ee0e6")));
        bars.Add((hot ? 1 : heat, hot ? Ui.Danger : heat >= 0.75 ? Ui.Rival : new Color("6b8cff")));
        const float width = 20, height = 2, gap = 1;
        for (var i = 0; i < bars.Count; i++)
        {
            var y = origin + new Vector2(0, i * (height + gap));
            DrawRect(new Rect2(y, new Vector2(width, height)), new Color(0.12f, 0.12f, 0.16f, 0.85f));
            DrawRect(new Rect2(y, new Vector2(width * (float)Math.Clamp(bars[i].Value, 0, 1), height)), bars[i].Color);
        }
    }

    /// <summary>Debug (F7): each computer-run fleet's job, as a faint line to its target and a tag.</summary>
    private void DrawMindJobs()
    {
        var w = Sim.World;
        foreach (var m in w.Minds)
        {
            var color = m.Side == Side.Player ? Ui.Player : Ui.Rival;
            foreach (var job in m.Jobs)
            {
                if (w.FindFleet(job.FleetId) is not Fleet f || !_drawn.TryGetValue(f.Id, out var p) || !p.IsFinite) continue;
                var target = job.BodyId is int b && w.FindBody(b) is Body body ? body.Position
                    : job.EnemyFleetId is int e && w.FindFleet(e) is Fleet enemy ? enemy.Position : job.Point;
                var at = Camera.ToScreen(p);
                var to = Camera.ToScreen(target);
                if (at.DistanceTo(to) > 6) DrawDashedLine(at, to, new Color(color, 0.35f), 1, 8);
                DrawString(_font, at + new Vector2(12, 36), $"[{job.Kind}]", HorizontalAlignment.Left, -1, 11, new Color(Ui.Text, 0.75f));
            }
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
        ? flight.ChosenWaypointsAhead(DrawHour).Select(w => w.Point).ToList()
        : flight.Waypoints;

    /// <summary>Is the end of the route being planned, or else of the selected fleet's current route, under a screen point?</summary>
    public bool HitTestRouteEnd(Vector2 screen)
    {
        Vec2? end = Preview?.Flight?.Target;
        if (end == null && PlannerWaypoints == null && SelectedId is int id && Sim.World.FindFleet(id) is { Side: Side.Player, Flight: Flight f })
            end = f.Target;
        return end is Vec2 e && Camera.ToScreen(e).DistanceTo(screen) <= 12;
    }

    /// <summary>A not-yet-reached waypoint of the selected fleet's current route under a screen point.</summary>
    public int? HitTestFlightWaypoint(Vector2 screen)
    {
        if (SelectedId is not int id || Sim.World.FindFleet(id) is not { Side: Side.Player, Flight: Flight flight }) return null;
        if (flight.WaypointPlan.Count == 0) return null;
        var ahead = flight.ChosenWaypointsAhead(Sim.World.Hour).Select(w => w.Point).ToList();
        for (var i = ahead.Count - 1; i >= 0; i--)
            if (Camera.ToScreen(ahead[i]).DistanceTo(screen) <= 12) return i;
        return null;
    }

    /// <summary>Is this fleet drawn: yours, an enemy your side can see, or everything in debug view.</summary>
    public bool Shown(Fleet f) => RevealAll || (Lagging ? ShownLagging(f) : f.Side == Side.Player || Sensors.Sees(Sim.World, Side.Player, f));

    /// <summary>The engagement screen's overlay (null when it is closed).</summary>
    public EngagementInfo? Engagement { get; set; }

    /// <summary>
    /// Engagement screen: weapon reach around your fleet (solid) and around the enemy (dashed, what it
    /// could carry), both fleets' courses ahead if nothing changes, and where they pass closest.
    /// </summary>
    private void DrawEngagement()
    {
        if (Engagement is not EngagementInfo e) return;
        var w = Sim.World;
        var c = GameData.Combat;
        if (!_drawn.TryGetValue(e.Mine.Id, out var mineWorld)) mineWorld = e.Mine.Position;
        var me = Camera.ToScreen(mineWorld);
        var enemyWorld = _drawn.TryGetValue(e.EnemyId, out var ew) && Shown(w.FindFleet(e.EnemyId)!) ? ew : e.EnemyAt;
        var them = Camera.ToScreen(enemyWorld);
        var scale = (float)Camera.Scale;

        var view = new Rect2(Vector2.Zero, GetViewportRect().Size);
        // Labels go in the open middle of the screen, clear of the engagement panels.
        var open = new Rect2(350, 130, Math.Max(100, view.Size.X - 350 - 430), Math.Max(100, view.Size.Y - 130 - 280));
        foreach (var ring in EngagementRings.For(e))
        {
            var at = ring.AroundMine ? me : them;
            var r = (float)ring.Mm * scale;
            if (EngagementRings.Status(at, r, view) != RingStatus.Shown) continue;
            if (ring.Dashed)
            {
                var n = Math.Clamp((int)(r * Mathf.Tau / 12) / 2 * 2, 48, 4000); // dashes about 12 px long
                for (var i = 0; i < n; i += 2)
                {
                    var a0 = Mathf.Tau * i / n;
                    var a1 = Mathf.Tau * (i + 1) / n;
                    DrawLine(at + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, at + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, new Color(ring.Color, 0.75f), ring.Width);
                }
            }
            else
            {
                DrawArc(at, r, 0, Mathf.Tau, Math.Clamp((int)(r / 3), 96, 1024), new Color(ring.Color, 0.85f), ring.Width, true);
                if (ring.Color == EngagementRings.Railguns) DrawCircle(at, r, new Color(ring.Color, 0.06f)); // where your guns bite
            }
            if (EngagementRings.LabelPoint(at, r, open) is Vector2 lp)
            {
                var text = $"{ring.Label} {Format.Distance(ring.Mm)}";
                var width = _font.GetStringSize(text, HorizontalAlignment.Left, -1, 12).X;
                var x = Math.Min(lp.X + 4, open.End.X + 20 - width); // keep it clear of the right-hand panel
                DrawString(_font, new Vector2(x, lp.Y - 4), text, HorizontalAlignment.Left, -1, 12, ring.Color.Lightened(0.15f));
            }
        }

        // Courses ahead: both fleets, as dots, out to the look-ahead.
        const int steps = 40;
        for (var i = 1; i <= steps; i++)
        {
            var t = w.Hour + e.LookAheadHours * i / steps;
            var a = Camera.ToScreen(Combat.PredictFleet(w, e.Mine, t));
            DrawCircle(a, 1.6f, new Color(Ui.Player, 0.6f));
            if (Sensors.Predict(w, Side.Player, e.EnemyId, t) is Vec2 b) DrawCircle(Camera.ToScreen(b), 1.6f, new Color(Ui.Rival, 0.6f));
        }
        if (e.ClosestInHours > 1e-3)
        {
            var t = w.Hour + e.ClosestInHours;
            var a = Camera.ToScreen(Combat.PredictFleet(w, e.Mine, t));
            var b = Sensors.Predict(w, Side.Player, e.EnemyId, t) is Vec2 bw ? Camera.ToScreen(bw) : a;
            var gold = new Color(1f, 0.83f, 0.47f);
            DrawLine(a, b, gold, 2);
            DrawArc(a, 5, 0, Mathf.Tau, 16, gold, 1.5f, true);
            var when = e.ClosestInHours < 1 / 60.0 ? $"{e.ClosestInHours * 3600:0} s" : e.ClosestInHours < 2 ? $"{e.ClosestInHours * 60:0} min" : $"{e.ClosestInHours:0.#} h";
            DrawString(_font, a + new Vector2(10, 18), $"Closest pass in {when} · {Format.Distance(e.ClosestGap)}", HorizontalAlignment.Left, -1, 12, gold);
        }
        if (!e.InSight)
            DrawString(_font, them + new Vector2(12, 24), "predicted position", HorizontalAlignment.Left, -1, 11, new Color(Ui.Rival, 0.7f));
    }

    // Recent track of each enemy fleet you have seen: where it was each hour, and whether it was burning.
    private readonly Dictionary<int, List<(Vec2 At, bool Burn, long Tick)>> _tracks = new();
    private long _trackTick = -1;
    private const int TrackHours = 72;

    /// <summary>Once a game hour: note where each enemy fleet in sight is, and whether it is burning.</summary>
    private void RecordTracks()
    {
        var w = Sim.World;
        if (w.Tick == _trackTick) return;
        if (w.Tick < _trackTick) _tracks.Clear(); // a load went back in time
        _trackTick = w.Tick;
        foreach (var f in w.Fleets)
        {
            if (f.Side == Side.Player || !Sensors.Sees(w, Side.Player, f, seat: Lagging) || !f.Position.IsFinite) continue;
            if (!_tracks.TryGetValue(f.Id, out var list)) _tracks[f.Id] = list = new();
            list.Add((Lagging && News(f) is Sighting news ? news.Position : f.Position, f.Thrusting || f.Warm && w.Hour - f.LastBurnHour < 1.01, w.Tick));
        }
        foreach (var (id, list) in _tracks.ToList())
        {
            list.RemoveAll(p => w.Tick - p.Tick > TrackHours);
            if (list.Count == 0 || w.FindFleet(id) == null && Sensors.Find(w, Side.Player, id, seat: Lagging) == null) _tracks.Remove(id);
        }
    }

    /// <summary>
    /// Enemy tracks: where seen fleets have been over the last few days (bright and thick where they burned,
    /// faint where they coasted), a flame behind a fleet burning now, and its course ahead if it keeps going.
    /// </summary>
    private void DrawTracks()
    {
        var w = Sim.World;
        foreach (var (id, list) in _tracks)
        {
            if (RevealAll && w.FindFleet(id) is { Side: Side.Player }) continue;
            for (var i = 1; i < list.Count; i++)
            {
                if (list[i].Tick - list[i - 1].Tick > 1) continue; // out of sight in between
                var a = Camera.ToScreen(list[i - 1].At);
                var b = Camera.ToScreen(list[i].At);
                if (!Camera.OnScreen(a, 200) && !Camera.OnScreen(b, 200)) continue;
                var age = (float)(w.Tick - list[i].Tick) / TrackHours;
                var burn = list[i].Burn;
                DrawLine(a, b, burn ? new Color(1f, 0.6f, 0.2f, 0.95f - 0.6f * age) : new Color(Ui.Rival, 0.35f - 0.25f * age), burn ? 3f : 1f);
            }
        }
        foreach (var f in w.Fleets)
        {
            if (f.Side == Side.Player || !Shown(f) || !_drawn.TryGetValue(f.Id, out var world)) continue;
            var at = Camera.ToScreen(world);
            if (!Camera.OnScreen(at, 300)) continue;
            if (f.Velocity.Length > 1e-6)
            {
                // Its course for the next day if nothing changes.
                var ahead = Camera.ToScreen(world + f.Velocity * 24);
                DrawDashedLine(at, ahead, new Color(Ui.Rival, 0.4f), 1, 6);
            }
            if (Sensors.StateOf(f) == SignatureState.Burn && Graphics.Fancy)
            {
                // A plume out of the engines, and the ring that says it can be seen from far off.
                var facing = (float)(_shownFacing.TryGetValue(f.Id, out var sh) ? sh : f.Facing);
                AddPlume(at - new Vector2(Mathf.Cos(facing), Mathf.Sin(facing)) * 6, facing, 34, 10, 1f, f.Id);
                var pulse = 0.75f + 0.25f * Mathf.Sin((float)Time.GetTicksMsec() / 90f);
                DrawArc(at, 20 + 4 * pulse, 0, Mathf.Tau, 32, new Color(1f, 0.6f, 0.2f, 0.5f), 1.5f, true);
            }
            else if (Sensors.StateOf(f) == SignatureState.Burn)
            {
                // A flame out of the engines, opposite the way the thrust pushes.
                var facing = (float)(_shownFacing.TryGetValue(f.Id, out var sh) ? sh : f.Facing);
                var back = new Vector2(-Mathf.Cos(facing), -Mathf.Sin(facing));
                var pulse = 0.75f + 0.25f * Mathf.Sin((float)Time.GetTicksMsec() / 90f);
                DrawLine(at + back * 8, at + back * (30 * pulse), new Color(1f, 0.75f, 0.3f, 0.9f), 4f);
                DrawLine(at + back * 8, at + back * (18 * pulse), new Color(1f, 1f, 0.85f, 0.95f), 2f);
                DrawArc(at, 20 + 4 * pulse, 0, Mathf.Tau, 32, new Color(1f, 0.6f, 0.2f, 0.5f), 1.5f, true);
            }
        }
    }

    /// <summary>Enemy fleets out of sight: a faint mark where they were last seen, and a dashed guess of where they went.</summary>
    private void DrawLastSeen()
    {
        var w = Sim.World;
        foreach (var s in Sensors.Known(w, seat: Lagging))
        {
            if (s.SeenBy != Side.Player || s.Visible || s.Own) continue;
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
            // Where it should be now if it kept its course: a hollow marker that moves along the track.
            var moving = at.DistanceTo(guess) > 4;
            // One faint pip per ship seen, under the marker.
            var mark = moving ? guess : at;
            for (var i = 0; i < Math.Min(s.Ships, 8); i++)
                DrawCircle(mark + new Vector2(-3.5f * (Math.Min(s.Ships, 8) - 1) + 7 * i, 14), 2.2f, new Color(Ui.Rival, 0.6f));
            if (moving)
            {
                DrawDiamond(guess, 7, new Color(Ui.Rival, 0.75f), false);
                DrawString(_font, guess + new Vector2(12, -8), $"{s.Name} ({s.Ships} ships): predicted, last seen {ago} ago", HorizontalAlignment.Left, -1, 12, new Color(Ui.Rival, 0.75f));
            }
            else if (s.FleetId == SelectedId || s.FleetId == HoverId || Camera.Scale > 0.01)
                DrawString(_font, at + new Vector2(12, -8), $"{s.Name} ({s.Ships} ships): last seen {ago} ago", HorizontalAlignment.Left, -1, 12, color);
            if (s.FleetId == SelectedId) DrawArc(moving ? guess : at, 15, 0, Mathf.Tau, 32, Ui.Rival, 1.5f, true);
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

    /// <summary>Where the i-th ship of a zoomed-out fleet sits: the lead at the centre, the rest in a small V behind it.</summary>
    private static Vector2 ClusterOffset(int i, float heading)
    {
        if (i == 0) return Vector2.Zero;
        var row = (i + 1) / 2;
        var side = i % 2 == 1 ? -1 : 1;
        return new Vector2(-9 * row, side * 7 * row).Rotated(heading);
    }

    /// <summary>Zoomed in close: ships are drawn from the approved drawings instead of icons.</summary>
    private bool CloseUp => Fleets.FormationSpacing * Camera.Scale >= 90;

    private void DrawShipOrBlip(Vector2 at, Ship s, Color color, float heading, bool known)
    {
        // A crippled ship tumbles slowly.
        var shown = s.Crippled ? heading + Tumble(s.Id) : heading;
        if (CloseUp && DrawShipDrawing(at, s, shown, known)) return;
        if (known)
        {
            if (Sim.World.FleetOf(s) is { Thrusting: true } fleet && EnginesLit(s)) IconPlume(at, s, heading, fleet);
            DrawHeatGlow(at, s, 9f);
            DrawShipIcon(at, s, color, shown);
            return;
        }
        DrawCircle(at, 4, color);
        DrawArc(at, 4, 0, Mathf.Tau, 12, color.Darkened(0.5f), 1, true);
    }

    /// <summary>
    /// A ship from its approved drawing, nose along its heading. Sizes keep the real lengths in step
    /// (a cruiser is about 2.4 frigates long), scaled up with the zoom so the formation stays readable.
    /// Rival ships are only drawn in full once a close look tells their fittings; until then, a dark
    /// outline. Damage shows on the drawing: darker when hurt, a red mark for a part knocked out, a glow
    /// when overheated, a brighter plume while burning.
    /// </summary>
    private bool DrawShipDrawing(Vector2 at, Ship s, float heading, bool known)
    {
        if (!GameData.ShipClasses.TryGetValue(s.ClassId, out var cls)) return false;
        var art = ShipArt.For(cls);
        if (Art.Texture("Ships", art) is not Texture2D tex) return false;
        var spacingPx = (float)(Fleets.FormationSpacing * Camera.Scale);
        var hullPx = Mathf.Clamp(spacingPx * 0.75f * (float)(cls.LengthM / 260.0), 10f, 900f);
        var width = hullPx / ShipArt.HullShare(art);
        var height = width * tex.GetHeight() / tex.GetWidth();
        var share = (float)Math.Clamp(s.Armour / Tech.MaxArmour(Sim.World, s), 0, 1);
        var tint = !known ? new Color(0.18f, 0.21f, 0.27f, 0.9f)
            : s.Crippled ? new Color(0.3f, 0.29f, 0.3f)
            : s.Side == Side.Player ? Colors.White.Lerp(new Color(0.45f, 0.45f, 0.5f), (1 - share) * 0.6f)
            : new Color(1f, 0.88f, 0.75f).Lerp(new Color(0.45f, 0.4f, 0.35f), (1 - share) * 0.6f);
        var fleet = Sim.World.FleetOf(s);
        var fancy = Graphics.Fancy;
        if (known && fancy)
        {
            if (fleet is { Thrusting: true } && EnginesLit(s))
            {
                var burn = BurnLevel(fleet);
                var stern = at - new Vector2(Mathf.Cos(heading), Mathf.Sin(heading)) * (hullPx * 0.5f);
                AddPlume(stern, heading, hullPx * (0.45f + 1.3f * burn), Mathf.Max(6f, hullPx * (0.16f + 0.08f * burn)), burn, s.Id);
            }
            DrawHeatGlow(at, s, hullPx * 0.45f);
        }
        DrawSetTransform(at, heading, Vector2.One);
        if (known && !fancy && fleet is { Thrusting: true } && EnginesLit(s))
        {
            // A burn: a bright plume behind the engine bell.
            var stern = -hullPx / 2;
            for (var i = 3; i >= 1; i--)
                DrawCircle(new Vector2(stern - hullPx * 0.12f * i, 0), hullPx * 0.05f * (4 - i), new Color(0.75f, 0.92f, 1f, 0.12f * i));
        }
        if (known && !fancy && s.Overheated) DrawCircle(Vector2.Zero, hullPx * 0.45f, new Color(1f, 0.5f, 0.2f, 0.18f));
        DrawTextureRect(tex, new Rect2(-(width - hullPx) - hullPx / 2, -height / 2, width, height), false, tint);
        if (known)
        {
            DrawShipDamage(s, hullPx, height);
            if (!s.Crippled) DrawRunningLights(hullPx, height, s.Id);
        }
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
        return true;
    }

    private void DrawShipIcon(Vector2 at, Ship s, Color color, float heading)
    {
        // Damaged ships darken; a ship with a part knocked out gets a red mark.
        var share = (float)(s.Armour / Fitting.Spec(s).Armour);
        color = color.Lerp(new Color(0.35f, 0.35f, 0.4f), Math.Clamp(1 - share, 0, 1) * 0.7f);
        if (s.Disabled != null) DrawCircle(at + new Vector2(0, -10), 2.5f, Ui.Danger);
        var shape = ShipIcons.For(GameData.ShipClasses.TryGetValue(s.ClassId, out var cls) ? cls.Icon : "frigate");
        // A crippled ship fades and gets a cross.
        if (s.Crippled) color = new Color(color.Darkened(0.3f), 0.45f);
        var pts = new Vector2[shape.Length];
        for (var i = 0; i < shape.Length; i++) pts[i] = at + shape[i].Rotated(heading);
        DrawColoredPolygon(pts, color);
        var outline = new Vector2[pts.Length + 1];
        pts.CopyTo(outline, 0);
        outline[^1] = pts[0];
        DrawPolyline(outline, color.Darkened(0.5f), 1, true);
        if (s.Crippled) DrawCross(at, 7, Ui.Danger, 1.6f);
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
            var stops = flight.WaypointPlan.Count > 0 ? flight.ChosenWaypointsAhead(DrawHour).Select(w => w.Stop).ToList() : null;
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
        if (flight.FlybyBodyId is int flybyId && flight.FlybyHour > DrawHour && Sim.World.FindBody(flybyId) is Body swing)
        {
            // The slingshot: where the planet will be at the swing, and the swing point.
            var at = Camera.ToScreen(flight.FlybyPoint);
            var planetThen = Camera.ToScreen(FlightPlanner.PredictBody(Sim.World, swing, flight.FlybyHour));
            DrawArc(planetThen, (float)ScreenRadius(swing) + 3, 0, Mathf.Tau, 24, new Color(baseColor, alpha * 0.5f), 1, true);
            DrawArc(at, 6, 0, Mathf.Tau, 12, new Color(baseColor, Math.Min(1f, alpha + 0.2f)), 1.5f, true);
            DrawString(_font, at + new Vector2(9, 14), $"swing past {swing.Name}", HorizontalAlignment.Left, -1, 11, new Color(baseColor, 0.8f));
        }
        if (flight.TargetBodyId is int bodyId && Sim.World.FindBody(bodyId) is Body body)
        {
            var ghost = Camera.ToScreen(FlightPlanner.PredictBody(Sim.World, body, flight.ArriveHour));
            DrawArc(ghost, (float)ScreenRadius(body) + 3, 0, Mathf.Tau, 24, new Color(baseColor, alpha * 0.5f), 1, true);
        }
        // Time and distance still to go, on every route of yours (in full on the selected one).
        var left = Math.Max(0, flight.ArriveHour - now);
        var togo = RemainingLength(flight, now);
        var short_ = $"{Format.Hours(left)} · {Format.Distance(togo)} to go";
        if (preview || f.Id == SelectedId)
            DrawString(_font, end + new Vector2(8, 16), $"{GameTime.Format((long)flight.ArriveHour)} · {short_}", HorizontalAlignment.Left, -1, 12, new Color(baseColor, alpha));
        else if (f.Side == Side.Player || RevealAll)
            DrawString(_font, end + new Vector2(8, 16), short_, HorizontalAlignment.Left, -1, 11, new Color(baseColor, alpha * 0.9f));
    }

    /// <summary>Length of the path still ahead (Mm), sampled along each leg.</summary>
    private static double RemainingLength(Flight flight, double now)
    {
        double total = 0;
        foreach (var leg in flight.Legs)
        {
            if (leg.End <= now) continue;
            var from = Math.Max(leg.Start, now);
            const int n = 12;
            var prev = leg.PositionAt(from);
            for (var i = 1; i <= n; i++)
            {
                var p = leg.PositionAt(from + (leg.End - from) * i / n);
                total += Vec2.Distance(prev, p);
                prev = p;
            }
        }
        return total;
    }

    /// <summary>Orders given while paused only reach the simulation on its next tick; show them now anyway.</summary>
    private IEnumerable<(Fleet, Flight)> PendingFlights()
    {
        // Several orders to one fleet while paused: only the last one counts, so only it is drawn.
        var last = new Dictionary<int, GameCommand>();
        foreach (var c in Sim.PendingCommands)
            if (c is PlanFlightCommand pf) last[pf.FleetId] = c;
            else if (c is SendAheadCommand sa) last[sa.FleetId] = c;
        foreach (var c in last.Values)
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
        foreach (var sight in Sensors.Known(Sim.World, seat: Lagging))
        {
            if (sight.SeenBy != Side.Player || sight.Visible || sight.Own) continue;
            var d = Math.Min(Camera.ToScreen(sight.Position).DistanceTo(screen), Camera.ToScreen(sight.Guess(DrawHour)).DistanceTo(screen));
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

    private static readonly Vector2[] Escort = { new(7, 0), new(-5, 4.5f), new(-3, 0), new(-5, -4.5f) };
    private static readonly Vector2[] Cruiser = { new(14, 0), new(6, 5.5f), new(-11, 5.5f), new(-8, 0), new(-11, -5.5f), new(6, -5.5f) };
    private static readonly Vector2[] Hauler = { new(9, 0), new(6, 4), new(-9, 4), new(-9, -4), new(6, -4) };

    public static Vector2[] For(string icon) => icon switch
    {
        "destroyer" => Destroyer,
        "escort" => Escort,
        "cruiser" => Cruiser,
        "tanker" or "freighter" => Hauler,
        _ => Frigate,
    };
}
