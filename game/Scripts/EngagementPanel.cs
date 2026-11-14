using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// The engagement screen (V): one of your fleets against one enemy fleet, zoomed in on them. A header
/// with the gap and closing speed, the ways to play it (each worked out now, or why it can't be done),
/// defences (dodging, supplies, armour), both fleets' ships, and a timeline of when each side's weapons
/// can reach and when missiles land.
/// </summary>
public partial class EngagementPanel : Control
{
    private readonly GameScreen _screen;
    private Label _header = null!;
    private VBoxContainer _options = null!;
    private readonly List<(Button Button, string Key)> _optionButtons = new();
    private Label _defence = null!, _ships = null!, _deadline = null!;
    private VBoxContainer _shipBox = null!;
    private Label _spot = null!;
    private Button _spotButton = null!;
    private static bool _spotOpen;                   // Spotting details folded away by default          // your fleet's ships: one row each with heat and quick buttons
    private string _shipKey = "";
    private readonly List<Action<World, Fleet>> _shipLive = new();
    private Button _dodgeOff = null!, _sidestep = null!, _hardBurn = null!, _focused = null!, _spread = null!, _longRange = null!;
    private Button _engage = null!, _hold = null!;
    private EngagementTimeline _timeline = null!;
    private VBoxContainer _together = null!;
    private Label? _othersLine;

    /// <summary>Your other fleets facing the same enemy: each one's engagement and a plan to arrive together. Worked out about once a second.</summary>
    private readonly List<(Fleet Fleet, EngagementInfo Info, Lightlag.Sim.Core.Engagement.TogetherPlan Plan)> _others = new();
    private ulong _othersAt;
    private string _othersKey = "";
    private VBoxContainer _right = null!;
    private ScrollContainer _rightScroll = null!, _leftScroll = null!;
    private VBoxContainer _left = null!;
    private readonly List<(Button Button, double Share)> _burnButtons = new();
    private VBoxContainer _tabs = null!;
    private string _tabsKey = "";
    private VBoxContainer _legend = null!;
    private PanelContainer _headPanel = null!, _rightPanel = null!, _leftPanel = null!, _bottomPanel = null!;

    public EngagementPanel() : this(null!) { }
    public EngagementPanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // Header across the top, under the menu bar.
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 12);
        _header = Ui.Label("", 15, Ui.Text);
        _header.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _header.CustomMinimumSize = new Vector2(760, 0);
        head.AddChild(_header);
        head.AddChild(Ui.SmallButton("Close (V)", () => _screen.CloseEngagement(), "Back to the system map"));
        var headBox = new VBoxContainer();
        headBox.AddThemeConstantOverride("separation", 6);
        headBox.AddChild(head);
        _deadline = Ui.Label("", 14, new Color("ffd166"));
        headBox.AddChild(_deadline);
        _othersLine = Ui.Label("", 13, Ui.Dim);
        _othersLine.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _othersLine.CustomMinimumSize = new Vector2(760, 0);
        headBox.AddChild(_othersLine);
        _headPanel = Ui.Panel(headBox, 10);
        AddChild(_headPanel);

        // Right: approach choices and defences.
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0) };
        right.AddThemeConstantOverride("separation", 8);
        right.AddChild(Ui.Label("Approach", 16, Ui.Text));
        _burnButtons.Clear();
        var burnRow = new HBoxContainer();
        burnRow.AddThemeConstantOverride("separation", 4);
        var burnLabel = Ui.Label("Burn", 13, Ui.Dim);
        burnLabel.CustomMinimumSize = new Vector2(64, 0);
        burnRow.AddChild(burnLabel);
        foreach (var (name, share, tip) in new[]
                 {
                     ("Gentle", 0.4, "40 % thrust and top speed: slower, saves fuel, a dimmer engine flare"),
                     ("Cruise", 0.7, "70 % thrust and top speed"),
                     ("Hard", 1.0, "Full thrust, then brake: closes fastest, burns the most fuel and lights you up"),
                 })
        {
            var b = Ui.SmallButton(name, () => _screen.SetEngagementBurn(share), tip);
            burnRow.AddChild(b);
            _burnButtons.Add((b, share));
        }
        right.AddChild(burnRow);
        _options = new VBoxContainer();
        _options.AddThemeConstantOverride("separation", 4);
        right.AddChild(_options);
        _together = new VBoxContainer();
        _together.AddThemeConstantOverride("separation", 4);
        right.AddChild(_together);

        right.AddChild(Ui.Label("Defences", 16, Ui.Text));
        var c = GameData.Combat;
        var sp = c.Spread;
        _dodgeOff = Ui.SmallButton("Off", () => SetDodge(DodgeMode.Off), "Fly straight: no fuel spent on dodging");
        _sidestep = Ui.SmallButton("Sidestep", () => SetDodge(DodgeMode.Sidestep),
            $"Jink as slugs come in: aimed slugs hit {c.DodgeFactor:P0} as often" + (sp != null ? $", a spread's fan still {sp.SidestepVsSpread:P0}" : "")
            + $". Costs {FlightPlanner.Kms(c.DodgeDeltaV)} of fuel per slug. Works less close up, where slugs arrive too fast to dodge.");
        _hardBurn = Ui.SmallButton("Hard burn", () => SetDodge(DodgeMode.HardBurn),
            sp == null ? "" : $"Burn hard out of the line of fire: aimed slugs hit {sp.HardVsFocused:P0} as often, a spread's fan {sp.HardVsSpread:P0}. "
                              + $"Costs {sp.HardCost:0}x the fuel of a sidestep. Works less close up.");
        right.AddChild(Row("Dodge", _dodgeOff, _sidestep, _hardBurn));
        _focused = Ui.SmallButton("Focused", () => SetFire(FireMode.Focused), "Aimed slugs: the most hits on a ship flying straight");
        _spread = Ui.SmallButton("Spread", () => SetFire(FireMode.Spread),
            sp == null ? "" : $"Each gun fires {sp.SlugsPerGun} slugs at once in a fan {sp.FanMm * 1000:#,0} km wide: each hits less often but a sidestep hardly helps. "
                              + $"The guns then cool for {sp.Cooldown:0} reloads.");
        _longRange = Ui.SmallButton("Long range", ToggleLongRange,
            $"Also fire beyond normal reach, out to {Km(Heat.D.LongRangeMm)}: slugs fly so long that they hit only a ship holding its course "
            + $"(at best about {GameData.Combat.Railgun.Accuracy * Heat.D.LongAccuracy:P0}), at half the rate. It makes them dodge and burn fuel. "
            + "The same switch as Long-range fire on the fleet panel.");
        right.AddChild(Row("Railguns", _focused, _spread, _longRange));
        _engage = Ui.SmallButton("Engage", () => SetStance(Stance.Engage), "Close in and fight whatever comes into contact");
        _hold = Ui.SmallButton("Hold course", () => SetStance(Stance.Hold), "Keep course and fire only at what comes into reach");
        var stance = new HBoxContainer();
        stance.AddThemeConstantOverride("separation", 4);
        stance.AddChild(Ui.Label("Orders", 13, Ui.Dim));
        stance.AddChild(_engage);
        stance.AddChild(_hold);
        right.AddChild(stance);
        _defence = Ui.Label("", 13, Ui.Text);
        _defence.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _defence.CustomMinimumSize = new Vector2(380, 0);
        right.AddChild(_defence);
        _right = right;
        _rightScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _rightScroll.AddChild(right);
        _rightPanel = Ui.Panel(_rightScroll, 12);
        AddChild(_rightPanel);

        // Left: the ships on both sides.
        _ships = Ui.Label("", 13, Ui.Text);
        _ships.CustomMinimumSize = new Vector2(300, 0);
        _ships.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        var left = new VBoxContainer();
        left.AddThemeConstantOverride("separation", 4);
        // Other engagements, out of the way at the top of this column (Tab steps through them).
        _tabs = new VBoxContainer();
        _tabs.AddThemeConstantOverride("separation", 1);
        left.AddChild(_tabs);
        _shipBox = new VBoxContainer { CustomMinimumSize = new Vector2(330, 0) };
        _shipBox.AddThemeConstantOverride("separation", 3);
        left.AddChild(_shipBox);
        left.AddChild(_ships);
        _spotButton = new Button { Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None, ClipText = true,
            TooltipText = "How far each side can see the other (click to open or fold)" };
        _spotButton.AddThemeFontSizeOverride("font_size", 12);
        _spotButton.Pressed += () => { _spotOpen = !_spotOpen; if (_screen.EngagementNow is EngagementInfo e2) Refresh(Main.Instance.Sim!, e2); };
        left.AddChild(_spotButton);
        _spot = Ui.Label("", 12, Ui.Dim);
        _spot.CustomMinimumSize = new Vector2(300, 0);
        _spot.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        left.AddChild(_spot);
        _legend = new VBoxContainer();
        _legend.AddThemeConstantOverride("separation", 2);
        left.AddChild(_legend);
        _left = left;
        _leftScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _leftScroll.AddChild(left);
        _leftPanel = Ui.Panel(_leftScroll, 12);
        AddChild(_leftPanel);

        // Bottom: the timeline.
        _timeline = new EngagementTimeline { CustomMinimumSize = new Vector2(900, 170) };
        _bottomPanel = Ui.Panel(_timeline, 10);
        _bottomPanel.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_bottomPanel);
    }

    /// <summary>Places the panels for the current screen size: header top middle, choices right, ships left, timeline bottom.</summary>
    private void Layout()
    {
        var size = GetViewportRect().Size;
        Position = Vector2.Zero;
        Size = size;
        _headPanel.Size = _headPanel.GetCombinedMinimumSize();
        _headPanel.Position = new Vector2(Mathf.Max(16, (size.X - _headPanel.Size.X) / 2), Hud.ContentTop + 6);
        var top = _headPanel.Position.Y + _headPanel.Size.Y + 10;
        // The choices scroll when the screen is too short for them all.
        var want = _right.GetCombinedMinimumSize();
        _rightScroll.CustomMinimumSize = new Vector2(want.X + 12, Mathf.Min(want.Y, size.Y - top - 50));
        var timelineW = Mathf.Clamp(size.X - _rightPanel.Size.X - 64, 500, 1100);
        _timeline.CustomMinimumSize = new Vector2(timelineW, _timeline.WantHeight);
        _bottomPanel.ResetSize();
        _bottomPanel.Size = _bottomPanel.GetCombinedMinimumSize();
        _bottomPanel.Position = new Vector2(16, size.Y - _bottomPanel.Size.Y - 60);
        // Ships, spotting and the ring legend scroll when they would run into the timeline.
        var wantLeft = _left.GetCombinedMinimumSize();
        _leftScroll.CustomMinimumSize = new Vector2(wantLeft.X + 12, Mathf.Max(80, Mathf.Min(wantLeft.Y, _bottomPanel.Position.Y - top - 34)));
        foreach (var p in new[] { _rightPanel, _leftPanel }) p.Size = p.GetCombinedMinimumSize();
        _rightPanel.Position = new Vector2(size.X - _rightPanel.Size.X - 16, top);
        _leftPanel.Position = new Vector2(16, top);
    }

    private static HBoxContainer Row(string label, params Button[] buttons)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        var l = Ui.Label(label, 13, Ui.Dim);
        l.CustomMinimumSize = new Vector2(64, 0);
        row.AddChild(l);
        foreach (var b in buttons) row.AddChild(b);
        return row;
    }

    private static string Carries(EngagementInfo e)
    {
        var parts = new List<string>();
        if (e.TheyHaveRailguns == true) parts.Add("railguns");
        if (e.TheyHaveMissiles == true) parts.Add("missiles");
        parts.Add("point defence");
        return string.Join(", ", parts);
    }

    /// <summary>One button per engagement you could look at; the one on screen is lit. Tab steps through them.</summary>
    private void RefreshTabs(EngagementInfo e)
    {
        var list = _screen.Engagements.Take(8).ToList();
        var key = string.Join(";", list.Select(p => $"{p.Mine}:{p.Enemy}")) + $"|{e.Mine.Id}:{e.EnemyId}";
        if (key != _tabsKey && !Ui.MouseHeld)
        {
            _tabsKey = key;
            foreach (var c in _tabs.GetChildren()) c.QueueFree();
            if (list.Count > 1)
            {
                _tabs.AddChild(Ui.Label("Engagements (Tab)", 12, Ui.Dim));
                foreach (var p in list)
                {
                    var mine = p.Mine;
                    var enemy = p.Enemy;
                    var b = Ui.SmallButton("", () => _screen.OpenEngagement(mine, enemy),
                        $"{p.MineName} and {p.EnemyName}, {Km(p.Gap)} apart" + (p.InSight ? "" : " (not in sight)"));
                    b.AddThemeFontSizeOverride("font_size", 12);
                    b.Alignment = HorizontalAlignment.Left;
                    b.SetMeta("pair", $"{p.Mine}:{p.Enemy}");
                    _tabs.AddChild(b);
                }
                _tabs.AddChild(new HSeparator());
            }
        }
        // Countdowns change all the time: update the labels without rebuilding.
        foreach (var c in _tabs.GetChildren())
        {
            if (c is not Button b || !b.HasMeta("pair")) continue;
            var p = list.FirstOrDefault(x => $"{x.Mine}:{x.Enemy}" == b.GetMeta("pair").AsString());
            if (p == null) continue;
            var when = p.ContactHours is double h ? (h <= 0 ? "in contact" : $"contact in {Format.Hours(h)}") : $"{Km(p.Gap)} apart";
            b.Text = $"{p.MineName} v {p.EnemyName} · {when}";
            b.Modulate = p.Mine == e.Mine.Id && p.Enemy == e.EnemyId ? Ui.Player : new Color(1, 1, 1, p.InSight ? 0.85f : 0.6f);
        }
        _tabs.Visible = list.Count > 1;
    }

    /// <summary>What each ring on the map is, and whether it is on screen at this zoom.</summary>
    private string? _legendKey;

    private void RefreshLegend(EngagementInfo e)
    {
        // Worked out every frame, but the rows are only rebuilt when their text changes.
        var cam = _screen.Camera;
        var view = new Rect2(Vector2.Zero, GetViewportRect().Size);
        var rows = new List<(Color Color, string Text, bool OnScreen)>();
        foreach (var ring in EngagementRings.For(e))
        {
            var at = cam.ToScreen(ring.AroundMine ? e.Mine.Position : e.EnemyAt);
            var r = (float)(ring.Mm * cam.Scale);
            var where = EngagementRings.Status(at, r, view) switch
            {
                RingStatus.TooSmall => " (too small: zoom in)",
                RingStatus.Outside => " (off screen: zoom out)",
                _ => "",
            };
            rows.Add((ring.Color, $"{ring.Label} {Km(ring.Mm)}{(ring.Dashed ? " (dashed)" : "")}{where}", where == ""));
        }
        var key = string.Join("\n", rows.Select(x => $"{x.Color}{x.Text}"));
        if (key == _legendKey || Ui.MouseHeld) return;
        _legendKey = key;
        foreach (var c in _legend.GetChildren()) c.QueueFree();
        _legend.AddChild(Ui.Label("Rings on the map", 13, Ui.Text));
        foreach (var (color, text, onScreen) in rows)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            row.AddChild(new ColorRect { Color = color, CustomMinimumSize = new Vector2(14, 3), SizeFlagsVertical = SizeFlags.ShrinkCenter });
            row.AddChild(Ui.Label(text, 12, onScreen ? color.Lightened(0.2f) : Ui.Dim));
            _legend.AddChild(row);
        }
    }

    private void SetDodge(DodgeMode mode)
    {
        if (_screen.EngagementNow is EngagementInfo e) Main.Instance.Sim!.Issue(new SetDodgeCommand(e.Mine.Id, mode));
    }

    private void SetFire(FireMode mode)
    {
        if (_screen.EngagementNow is EngagementInfo e) Main.Instance.Sim!.Issue(new SetFireModeCommand(e.Mine.Id, mode));
    }

    private void ToggleLongRange()
    {
        if (_screen.EngagementNow is not EngagementInfo e) return;
        var sim = Main.Instance.Sim!;
        var now = sim.PendingCommands.OfType<SetFireControlCommand>().LastOrDefault(c => c.FleetId == e.Mine.Id) is { } p ? p.LongShots : e.Mine.LongShots;
        sim.Issue(new SetFireControlCommand(e.Mine.Id, !now, e.Mine.MissileLaunch));
    }

    private void SetStance(Stance stance)
    {
        if (_screen.EngagementNow is not EngagementInfo e) return;
        Main.Instance.Sim!.Issue(new SetStanceCommand(e.Mine.Id, stance, e.Mine.RetreatWhenDamaged));
    }

    private static string When(double hours) =>
        double.IsPositiveInfinity(hours) ? "later" : hours < 1 / 60.0 ? $"{hours * 3600:0} s" : hours < 2 ? $"{hours * 60:0} min" : $"{hours:0.#} h";

    private static string Km(double mm) => Format.Distance(mm);

    /// <summary>
    /// Under light lag: how long orders take, how old the picture is, and the order deadline: the last
    /// moment an order sent now still reaches the fleet before contact. Choices made here go out together
    /// as its battle plan.
    /// </summary>
    private static string LagLine(World w, Fleet f, EngagementInfo e)
    {
        var me = Lightlag.Sim.Core.Side.Player;
        var lag = Light.DelayHours(w, me, f.Position);
        if (lag <= 0) return "";
        var line = $"\nOrders take {Format.Lag(lag)} to reach {f.Name}; this picture is {Format.Lag(Light.AgeHours(w, me, e.EnemyId))} old.";
        if (!Light.SignalClear(w, me, f.Position)) return line + " A planet blocks the signal: orders wait until it clears.";
        // Contact is worked out on their track carried on to now, so only the order's own trip counts.
        if (e.ContactInHours is double contact && contact > 0)
        {
            var left = contact - lag;
            line += left > 0
                ? $" Order deadline: send within {Format.Lag(left)} to reach it before contact. Choices made here go out together as its battle plan."
                : " Too late for orders before contact: it fights by its standing orders and autonomy.";
        }
        return line;
    }

    /// <summary>Two or more of your fleets against one: a line per other fleet, a timeline bar each, and Arrive together.</summary>
    private void RefreshOthers(World w, EngagementInfo e)
    {
        var pairs = _screen.Engagements.Where(p => p.Enemy == e.EnemyId && p.Mine != e.Mine.Id).Select(p => p.Mine).Distinct().ToList();
        var key = $"{e.Mine.Id}:{e.EnemyId}:{e.Burn}:" + string.Join(",", pairs);
        var now = Time.GetTicksMsec();
        if (key != _othersKey || now - _othersAt > 1000)
        {
            _othersAt = now;
            _others.Clear();
            foreach (var id in pairs)
                if (w.FindFleet(id) is Fleet other && Lightlag.Sim.Core.Engagement.Of(w, other, e.EnemyId, e.Burn) is EngagementInfo info)
                    _others.Add((other, info, Lightlag.Sim.Core.Engagement.Together(w, e.Mine, other, e.EnemyId, e.Burn)));
        }
        _othersLine!.Text = string.Join("\n", _others.Select(o =>
        {
            string Soon(TimeWindow? t) => t == null ? "never" : t.From < 1e-3 ? "now" : $"in {When(t.From)}";
            var guns = o.Info.WeHaveRailguns ? $"its guns {Soon(o.Info.OurRailguns)}" : o.Info.WeHaveMissiles ? $"its missiles {Soon(o.Info.OurMissiles)}" : "no weapons";
            return $"{o.Fleet.Name} also faces them: {Km(o.Info.Gap)} away, {guns}, their guns {Soon(o.Info.TheirRailguns)}.";
        }));
        _othersLine.Visible = _others.Count > 0;
        _timeline.Others = _others.Select(o => o.Info).ToList();

        if (key != _othersKey)
        {
            foreach (var c in _together.GetChildren()) c.QueueFree();
            foreach (var (other, _, _) in _others)
            {
                var otherId = other.Id;
                var b = new Button { FocusMode = FocusModeEnum.None, Alignment = HorizontalAlignment.Left, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(380, 0) };
                b.AddThemeFontSizeOverride("font_size", 13);
                b.SetMeta("other", otherId);
                b.Pressed += () => Main.Instance.RunGuarded("arrive together", () => ArriveTogether(otherId));
                _together.AddChild(b);
            }
        }
        _othersKey = key;
        foreach (var c in _together.GetChildren())
        {
            if (c is not Button b || !b.HasMeta("other")) continue;
            var id = b.GetMeta("other").AsInt32();
            var o = _others.FirstOrDefault(x => x.Fleet.Id == id);
            if (o.Fleet == null) continue;
            b.Text = o.Plan.Problem is string why
                ? $"Arrive together with {o.Fleet.Name}: can't\n{why}"
                : $"Arrive together with {o.Fleet.Name}\nThe faster one eases its burn: both in reach about {When(Math.Max(0, o.Plan.ArriveHour - w.Hour))} from now.";
            b.Disabled = o.Plan.Problem != null;
            b.Modulate = o.Plan.Problem != null ? new Color(1, 1, 1, 0.6f) : Colors.White;
        }
    }

    private void ArriveTogether(int otherId)
    {
        if (_screen.EngagementNow is not EngagementInfo e || _others.FirstOrDefault(x => x.Fleet.Id == otherId) is not { Fleet: not null } o) return;
        if (o.Plan is not { First: FlightRequest a, Second: FlightRequest b }) return;
        var sim = Main.Instance.Sim!;
        sim.Issue(new PlanFlightCommand(e.Mine.Id, a with { DepartTick = sim.World.Tick }));
        sim.Issue(new PlanFlightCommand(otherId, b with { DepartTick = sim.World.Tick }));
        Main.Instance.Toast($"{e.Mine.Name} and {o.Fleet.Name} close in together");
    }

    /// <summary>The order deadline as a countdown of its own: time left for an order sent now to reach the fleet before contact.</summary>
    private static (string Text, bool Late) Deadline(World w, Fleet f, EngagementInfo e)
    {
        var me = Lightlag.Sim.Core.Side.Player;
        var lag = Light.DelayHours(w, me, f.Position);
        if (lag <= 0 || e.ContactInHours is not double contact || contact <= 0) return ("", false);
        if (!Light.SignalClear(w, me, f.Position)) return ("⏱ Orders blocked by a planet: they wait until it clears", true);
        var left = contact - lag;
        return left > 0 ? ($"⏱ {Format.Lag(left)} left to give {f.Name} orders before contact", false) : ("⏱ Too late for orders: it fights by its standing orders", true);
    }

    public void Refresh(Simulation sim, EngagementInfo e)
    {
        var w = sim.World;
        var f = e.Mine;
        var seen = e.InSight ? "in sight" : $"not in sight since {When(e.SeenHoursAgo)} ago: the marker is where they should be if they kept going";
        var how = e.Closing > 1e-3 ? $"closing at {Format.Speed(e.Closing)}" : e.Closing < -1e-3 ? $"pulling apart at {Format.Speed(-e.Closing)}" : "holding distance";
        var pass = e.ClosestInHours < 1e-3
            ? "they are as close as they will get"
            : $"closest pass in {When(e.ClosestInHours)} at {Km(e.ClosestGap)}";
        _header.Text = $"ENGAGEMENT: {f.Name} and {e.EnemyName} ({e.EnemyShips} ships, {seen})\n"
                       + $"{Km(e.Gap)} apart, {how}; {pass}, if neither changes course."
                       + LagLine(w, f, e);
        RefreshTabs(e);
        RefreshOthers(w, e);
        var (deadline, late) = Deadline(w, f, e);
        _deadline.Text = deadline;
        _deadline.Visible = deadline != "";
        _deadline.AddThemeColorOverride("font_color", late ? Ui.Danger : new Color("ffd166"));

        // Approach options.
        if (_optionButtons.Count != e.Options.Count || _optionButtons.Select(o => o.Key).Where((k, i) => k != e.Options[i].Key).Any())
        {
            foreach (var c in _options.GetChildren()) c.QueueFree();
            _optionButtons.Clear();
            foreach (var o in e.Options)
            {
                var key = o.Key;
                var b = new Button
                {
                    FocusMode = FocusModeEnum.None, Alignment = HorizontalAlignment.Left,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(380, 0),
                };
                b.AddThemeFontSizeOverride("font_size", 13);
                b.Pressed += () => Main.Instance.RunGuarded("engagement order", () => Choose(key));
                _options.AddChild(b);
                _optionButtons.Add((b, key));
            }
        }
        for (var i = 0; i < e.Options.Count; i++)
        {
            var o = e.Options[i];
            var b = _optionButtons[i].Button;
            b.Text = o.Problem != null ? $"{o.Label}: can't\n{o.Problem}" : $"{o.Label}{(o.Current ? "  (doing this)" : "")}\n{o.Summary}";
            b.Disabled = o.Problem != null;
            b.Modulate = o.Current ? Ui.Player : o.Problem != null ? new Color(1, 1, 1, 0.6f) : Colors.White;
        }

        // Defences.
        static Color On(bool on) => on ? Ui.Player : new Color(1, 1, 1, 0.7f);
        foreach (var (b, share) in _burnButtons) b.Modulate = On(Math.Abs(share - e.Burn) < 1e-6);
        // Orders given while paused wait for the next step: show them as chosen straight away.
        var pending = sim.PendingCommands;
        var dodge = pending.OfType<SetDodgeCommand>().LastOrDefault(c => c.FleetId == f.Id)?.Dodge ?? f.Dodge;
        var fire = pending.OfType<SetFireModeCommand>().LastOrDefault(c => c.FleetId == f.Id)?.Fire ?? f.Fire;
        var stance = pending.OfType<SetStanceCommand>().LastOrDefault(c => c.FleetId == f.Id)?.Stance ?? f.Stance;
        _dodgeOff.Modulate = On(dodge == DodgeMode.Off);
        _sidestep.Modulate = On(dodge == DodgeMode.Sidestep);
        _hardBurn.Modulate = On(dodge == DodgeMode.HardBurn);
        _focused.Modulate = On(fire == FireMode.Focused);
        _spread.Modulate = On(fire == FireMode.Spread);
        var longShots = pending.OfType<SetFireControlCommand>().LastOrDefault(c => c.FleetId == f.Id)?.LongShots ?? f.LongShots;
        _longRange.Modulate = On(longShots);
        _longRange.Text = longShots ? "Long range ✓" : "Long range";
        _engage.Modulate = stance == Stance.Engage ? Ui.Player : new Color(1, 1, 1, 0.7f);
        _hold.Modulate = stance == Stance.Hold ? Ui.Player : new Color(1, 1, 1, 0.7f);
        var ships = w.ShipsOf(f).ToList();
        var supplies = Combat.SupplyShare(w, f);
        var armour = ships.Count == 0 ? 0 : ships.Average(s => s.Armour / Fitting.Spec(s).Armour);
        var pd = ships.Count(s => Fitting.Spec(s).PointDefence > 0 && s.Disabled != Module.PointDefence);
        var lines = new List<string>
        {
            dodge == DodgeMode.Off ? "Not dodging" : Combat.Dodging(w, f) || dodge != f.Dodge ? $"Dodging ({(dodge == DodgeMode.HardBurn ? "hard burn" : "sidestep")})" : $"Dodging stopped: supplies below {GameData.Combat.DodgeStopsBelow:P0}",
            IncomingLine(w, e),
            $"Supplies {supplies:P0} · fuel for {Format.Speed(FleetPerformance.Of(w, f).DeltaVLeft)} of speed change",
            $"Armour {armour:P0} on average",
            $"Point defence on {pd} of {ships.Count} ships, reach {Km(GameData.Combat.PointDefence.RangeMm)}",
            ships.Sum(s => s.Missiles) <= 0 ? "Missiles left: NONE (out of missiles)" : $"Missiles left: {ships.Sum(s => s.Missiles)} of {ships.Sum(s => Tech.Racks(w, s))}",
        };
        if (f.Chase is { Problem: string problem }) lines.Add($"Chase trouble: {problem}");
        if (HoldingFire(w, f, e) is string held) lines.Insert(0, held);
        if (Combat.Surprised(w, f)) lines.Add($"CAUGHT UNAWARES: no return fire or dodging for {(f.SurprisedUntilHour!.Value - w.Hour) * 60:0} more minutes");
        else if (Combat.Unseen(w, f))
            lines.Add($"Unseen: they haven't spotted you. Your first shots catch them unawares for {Combat.SurpriseHours * 60:0} minutes "
                      + $"(missiles from {Km(Heat.LaunchRange(f))}, guns from {Km(GameData.Combat.Railgun.RangeMm)}). Burns and orbit make you easier to see");
        _defence.Text = string.Join("\n", lines);

        // Ships: your fleet as rows with heat and quick buttons, then the enemy and spotting as text.
        RefreshShipRows(w, f, ships);
        var shipLines = new List<string> { $"{e.EnemyName}: {e.EnemyShips} ships" };
        // One row per kind of ship once identified: how many, what it carries, and its burn.
        if (Sensors.Find(w, Lightlag.Sim.Core.Side.Player, e.EnemyId) is { Classes.Count: > 0 } seenShips)
        {
            foreach (var g in seenShips.Classes.GroupBy(c => c).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                if (!GameData.ShipClasses.TryGetValue(g.Key, out var k)) { shipLines.Add($"  {g.Count()}× {g.Key}"); continue; }
                var arms = new List<string>();
                if (k.Railguns > 0) arms.Add($"{k.Railguns} railgun{(k.Railguns == 1 ? "" : "s")}");
                if (k.Lances > 0) arms.Add($"{k.Lances} lance{(k.Lances == 1 ? "" : "s")}");
                if (k.Missiles > 0) arms.Add("missiles");
                if (k.PointDefence > 0) arms.Add("point defence");
                shipLines.Add($"  {g.Count()}× {k.Name} ({k.Short}): {(arms.Count > 0 ? string.Join(", ", arms) : "unarmed")}, burns {k.AccelMmPerHour2 / 3.6:0.#} km/s each hour");
            }
            if (e.ClassesSeenHoursAgo > 1) shipLines.Add($"  (from a close look {When(e.ClassesSeenHoursAgo)} ago: may have changed)");
        }
        else
            shipLines.Add($"  types unknown: come within {Km(Sensors.IdentifyRange(w, Lightlag.Sim.Core.Side.Player))} to tell");
        if (e.TheyHaveRailguns != null || e.TheyHaveMissiles != null)
            shipLines.Add($"  carry: {Carries(e)}");
        var reach = RivalTech.Guess(w, Lightlag.Sim.Core.Side.Player, e.EnemyId);
        shipLines.Add($"  reach: railguns {reach.Rail:0} Mm, missiles {reach.Missile:0} Mm{(reach.Lance > 0 ? $", lances {reach.Lance:0} Mm" : "")} ({reach.How}{(reach.Plus > 0 ? $", ±{reach.Plus:P0}" : "")})");
        shipLines.Add($"  moving at {Format.Speed(e.EnemyVelocity.Length)}" + (e.InSight ? "" : " when last seen"));
        shipLines.Add($"  this fleet's top speed: {Format.Speed(e.OurTopSpeed)}");
        var spotLines = new List<string>();
        spotLines.Add($"  You see them within {Km(e.SpotCold)} coasting cold, {Km(e.SpotWarm)} with warm engines, {Km(e.SpotBurn)} burning.");
        if (e.Cued)
            spotLines.Add(e.InSight
                ? $"  That is holding a contact you already have; a fleet never seen shows up only within {Km(e.SpotColdUncued)} cold."
                : $"  That is with your sensors searching their predicted track: if they changed course, only {Km(e.SpotColdUncued)} cold.");
        if (!e.InSight)
            spotLines.Add(e.SpotWindow is TimeWindow sw
                ? $"  If they keep coasting you spot them in {When(sw.From)}."
                : $"  They are {Km(e.Gap)} away: too far to spot while they coast cold.");
        var us = e.OurState switch
        {
            SignatureState.Burn => "your engines are burning",
            SignatureState.Warm => "your engines are still warm",
            SignatureState.Orbit => "you are holding orbit",
            _ => "you are coasting cold",
        };
        spotLines.Add($"  They see you from at least {Km(e.TheySeeUsAt)}: {us}" + (e.Gap <= e.TheySeeUsAt ? ", so expect they can see you now." : "."));
        _ships.Text = string.Join("\n", shipLines);
        _spotButton.Text = _spotOpen ? "▾ Spotting" : $"▸ Spotting: you see them {Km(e.SpotCold)} cold · they see you from {Km(e.TheySeeUsAt)}";
        _spotButton.Modulate = e.Gap <= e.TheySeeUsAt ? new Color("ffd166") : Colors.White;
        _spot.Text = string.Join("\n", spotLines);
        _spot.Visible = _spotOpen;
        RefreshLegend(e);

        _timeline.Info = e;
        _timeline.QueueRedraw();
        Layout();
    }

    /// <summary>
    /// Your fleet's ships, one row each: armour, supplies, missiles and heat (red when hot), with Eject sink,
    /// the ship's card and Split off; and a fleet row with Eject all sinks, radiators, Pull back now and Hold on.
    /// Rebuilt only when the fleet or its ships change; the numbers update every refresh.
    /// </summary>
    private void RefreshShipRows(World w, Fleet f, List<Ship> ships)
    {
        var key = $"{f.Id}|{string.Join(",", ships.Select(s => s.Id))}";
        if (key != _shipKey)
        {
            _shipKey = key;
            _shipLive.Clear();
            foreach (var c in _shipBox.GetChildren()) c.QueueFree();
            BuildFleetRow(f);
            foreach (var s in ships) BuildShipRow(s.Id, ships.Count > 1);
        }
        foreach (var live in _shipLive) live(w, f);
    }

    private static Button Small(string text, string tip, Action act)
    {
        var b = Ui.SmallButton(text, () => Main.Instance.RunGuarded("engagement ship order", act), tip);
        b.CustomMinimumSize = new Vector2(0, 22);
        b.AddThemeFontSizeOverride("font_size", 11);
        return b;
    }

    private void BuildFleetRow(Fleet fleet)
    {
        var id = fleet.Id;
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 4);
        var name = Ui.Label(fleet.Name, 14, Ui.Player);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(name);
        head.AddChild(Small("Eject all sinks", "Every ship throws a heat sink: sheds heat at once and can draw off missiles closing in", () => _screen.IssueNow(new EjectHeatSinksCommand(id), "eject heat sinks")));
        var rad = Small("", "Radiators: Auto (pulled in for battle), Out (cool but fragile), Run cold (hidden, heat builds). Click to change", () =>
        {
            if (Main.Instance.Sim!.World.FindFleet(id) is Fleet f2)
                _screen.IssueNow(new SetHeatCommand(id, (RadiatorMode)(((int)f2.Radiators + 1) % 3), f2.AutoSinks), "radiators");
        });
        head.AddChild(rad);
        _shipBox.AddChild(head);
        var orders = new HBoxContainer();
        orders.AddThemeConstantOverride("separation", 4);
        var sinks = new CheckButton { Text = "Auto sinks", FocusMode = FocusModeEnum.None, TooltipText = "Ships throw heat sinks on their own when they run hot or missiles close in" };
        sinks.AddThemeFontSizeOverride("font_size", 11);
        sinks.Toggled += on => Main.Instance.RunGuarded("auto sinks", () =>
        {
            if (Main.Instance.Sim!.World.FindFleet(id) is Fleet f2) _screen.IssueNow(new SetHeatCommand(id, f2.Radiators, on), "auto sinks");
        });
        orders.AddChild(sinks);
        var support = new CheckButton { Text = "Leave support behind", FocusMode = FocusModeEnum.None,
            TooltipText = "When this fleet goes in to fight (Burn and engage, Hold at missile range, Burn past), its unarmed ships such as supply tenders split off and wait, then fly to rejoin once the fight is over. The fleet then burns at its warships' pace" };
        support.AddThemeFontSizeOverride("font_size", 11);
        support.Toggled += on => Main.Instance.RunGuarded("leave support", () => _screen.IssueNow(new SetLeaveSupportCommand(id, on), "leave support behind"));
        orders.AddChild(support);
        orders.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var hold = Small("Hold on", "Ignore the pull-back rules and fight on (click again to let them apply)", () =>
        {
            if (Main.Instance.Sim!.World.FindFleet(id) is Fleet f2) _screen.IssueNow(new HoldOnCommand(id, !f2.HoldOn), "hold on");
        });
        orders.AddChild(hold);
        orders.AddChild(Small("Pull back now", "Break off and leave the fight now, by the fleet's pull-back rule", () => _screen.IssueNow(new PullBackNowCommand(id), "pull back now")));
        _shipBox.AddChild(orders);
        var budget = new HBoxContainer();
        budget.AddThemeConstantOverride("separation", 4);
        budget.AddChild(Ui.Label("Missiles per target", 11, Ui.Dim));
        var budgetButtons = new List<(Button B, int N)>();
        foreach (var (n, text) in new[] { (2, "2"), (4, "4"), (0, "All") })
        {
            var b = Small(text, n == 0 ? "No limit: salvos keep going at a target until it is finished" : $"At most {n} missiles in flight at one ship; the rest wait for another target", () => _screen.IssueNow(new SetMissileBudgetCommand(id, n), "missile budget"));
            budget.AddChild(b);
            budgetButtons.Add((b, n));
        }
        _shipBox.AddChild(budget);
        _shipLive.Add((_, f) =>
        {
            rad.Text = f.Radiators switch { RadiatorMode.Out => "Radiators out", RadiatorMode.Cold => "Running cold", _ => "Radiators auto" };
            sinks.SetPressedNoSignal(f.AutoSinks);
            support.SetPressedNoSignal(f.LeaveSupportBehind);
            support.Visible = Main.Instance.Sim!.World.ShipsOf(f).Any(Fleets.Unarmed) || f.LeaveSupportBehind;
            hold.Modulate = f.HoldOn ? Ui.Player : Colors.White;
            foreach (var (b, n) in budgetButtons) b.Modulate = f.MissilesPerTarget == n ? Ui.Player : Colors.White;
            budget.Visible = Main.Instance.Sim!.World.ShipsOf(f).Any(x => Fitting.Spec(x).Missiles > 0);
        });
    }

    private void BuildShipRow(int shipId, bool canSplit)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 1);
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 4);
        var name = Ui.Label("", 13, Ui.Text);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.ClipText = true;
        name.MouseFilter = MouseFilterEnum.Stop;
        name.TooltipText = "Click for this ship's card";
        name.TooltipText = "Click for this ship's card; right-click for its orders";
        var menu = new PopupMenu();
        menu.AddItem("Send home (to the nearest yard, rejoins when mended)", 1);
        menu.AddItem("Eject a heat sink", 2);
        menu.AddItem("Split off as its own fleet", 3);
        menu.AddItem("Ship card", 4);
        menu.IdPressed += mid => Main.Instance.RunGuarded("ship order", () =>
        {
            if (Main.Instance.Sim!.World.FindShip(shipId) is not Ship sh) return;
            switch (mid)
            {
                case 1: _screen.IssueNow(new SendShipHomeCommand(sh.FleetId, shipId), "send ship home"); break;
                case 2: _screen.IssueNow(new EjectShipSinkCommand(sh.FleetId, shipId), "eject heat sink"); break;
                case 3: _screen.IssueNow(new SplitFleetCommand(sh.FleetId, new List<int> { shipId }), "split off ship"); break;
                case 4: _screen.ShipCardId = shipId; break;
            }
        });
        v.AddChild(menu);
        void OnClick(InputEvent ev)
        {
            if (ev is not InputEventMouseButton { Pressed: true } mb) return;
            if (mb.ButtonIndex == MouseButton.Left) _screen.ShipCardId = shipId;
            if (mb.ButtonIndex == MouseButton.Right)
            {
                var world = Main.Instance.Sim!.World;
                var together = world.FindShip(shipId) is Ship sh && world.FindFleet(sh.FleetId) is Fleet ff ? ff.ShipIds.Count : 0;
                menu.SetItemDisabled(2, together < 2);
                menu.Position = (Vector2I)GetGlobalMousePosition();
                menu.Popup();
            }
        }
        name.GuiInput += OnClick;
        top.AddChild(name);
        var eject = Small("Eject sink", "Dump a heat sink from this ship now: drops its heat at once", () =>
        {
            if (Main.Instance.Sim!.World.FindShip(shipId) is Ship sh) _screen.IssueNow(new EjectShipSinkCommand(sh.FleetId, shipId), "eject heat sink");
        });
        top.AddChild(eject);
        top.AddChild(Small("ⓘ", "This ship's card", () => _screen.ShipCardId = shipId));
        Button? split = null;
        if (canSplit)
        {
            split = Small("Split off", "Make this ship its own fleet (for example a slow tender, or a hurt ship to send home)", () =>
            {
                if (Main.Instance.Sim!.World.FindShip(shipId) is Ship sh) _screen.IssueNow(new SplitFleetCommand(sh.FleetId, new List<int> { shipId }), "split off ship");
            });
            top.AddChild(split);
        }
        v.AddChild(top);
        var heat = new StatBar("Heat") { CustomMinimumSize = new Vector2(0, 12) };
        v.AddChild(heat);
        var info = Ui.Label("", 11, Ui.Dim);
        info.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(info);
        _shipBox.AddChild(v);
        _shipLive.Add((w, _) =>
        {
            if (w.FindShip(shipId) is not Ship s) { v.Modulate = new Color(1, 1, 1, 0.35f); return; }
            var cls = Fitting.Spec(s);
            var tag = s.Crippled ? "  CRIPPLED" : s.Overheated ? "  OVERHEATED" : s.Disabled is Module m ? $"  {Combat.ModuleName(m)} out" : "";
            name.Text = $"{s.Name} ({s.Code}){tag}";
            name.AddThemeColorOverride("font_color", s.Crippled || s.Overheated ? Ui.Danger : Ui.Text);
            var cap = Math.Max(1e-6, Heat.Capacity(s));
            heat.Set(s.Heat / cap, s.Overheated ? "OVERHEATED: weapons hold fire until it cools; eject a sink" : $"Heat {s.Heat / cap:P0} of capacity", s.Overheated, hot: true);
            var bits = new List<string> { $"armour {s.Armour / Math.Max(1e-6, cls.Armour):P0}", $"supplies {s.Supplies / Math.Max(1e-6, cls.Supplies):P0}", $"heat {s.Heat / cap:P0}" };
            if (cls.Missiles > 0) bits.Add($"missiles {s.Missiles}/{Tech.Racks(w, s)}");
            if (Heat.MaxShield(w, s) > 0) bits.Add($"shield {s.Shield / Math.Max(1e-6, Heat.MaxShield(w, s)):P0}");
            bits.Add($"sinks {s.HeatSinks}");
            info.Text = string.Join(" · ", bits);
            eject.Disabled = s.HeatSinks <= 0 || s.Heat <= 0;
            eject.Text = $"Eject sink ({s.HeatSinks})";
            eject.Modulate = s.Overheated && s.HeatSinks > 0 ? Ui.Danger : Colors.White;
        });
    }

    /// <summary>
    /// Why this fleet is not firing at the enemy it faces, in words, with what to do about it (or null when nothing holds
    /// it back): the enemy pulling back and not chased, this fleet pulling back, missiles waiting for a closer launch.
    /// </summary>
    private static string? HoldingFire(World w, Fleet f, EngagementInfo e)
    {
        if (w.FindFleet(e.EnemyId) is not Fleet enemy || !e.InSight) return null;
        if (f.Retreating && enemy.Chase?.TargetFleetId != f.Id)
            return "HOLDING FIRE: your fleet is pulling back, so it doesn't shoot. Pick an option above to fight again.";
        if (enemy.Retreating && f.Chase?.TargetFleetId != enemy.Id)
            return $"HOLDING FIRE: {e.EnemyName} is pulling back, and your fleets let a retreating fleet go. "
                   + "To keep shooting, chase it: pick Burn and engage or Hold at missile range above.";
        var d = Vec2.Distance(f.Position, enemy.Position);
        var launch = Heat.LaunchRange(f);
        if (w.ShipsOf(f).Any(s => s.Missiles > 0) && d > launch && d <= GameData.Combat.Missile.LaunchRangeMm)
            return $"Missiles wait: this fleet launches from {Km(launch)} (they are {Km(d)} away). Set Launch far on the fleet panel to fire from {Km(GameData.Combat.Missile.LaunchRangeMm)}.";
        return null;
    }

    /// <summary>What railgun fire is coming in, and how much this fleet's dodge does against it at this range.</summary>
    private static string IncomingLine(World w, EngagementInfo e)
    {
        var enemy = w.FindFleet(e.EnemyId);
        if (enemy == null || !e.InSight || enemy.LastVolley is not FireMode fire || w.Hour - enemy.LastVolleyHour > 0.05)
            return "No railgun fire coming in";
        var d = Vec2.Distance(e.Mine.Position, enemy.Position);
        var secs = Combat.SlugSeconds(d, enemy, e.Mine);
        string Cut(DodgeMode m) => $"{1 - Combat.DodgeFactor(m, fire, secs):P0}";
        // Beyond their normal reach: long shots, meant to make you dodge.
        if (d > Secrecy.RailRange(w, enemy) + 1e-6)
            return $"Incoming: long-range slugs from {Km(d)}, past their guns' normal reach. Each takes {secs:0} s and hits only a ship holding its course, "
                   + "so any dodge makes them miss (it costs fuel). Answer in kind with Long range on Railguns, or close in.";
        return $"Incoming: {(fire == FireMode.Spread ? "spread fans" : "aimed slugs")}, arriving in {secs:0} s. "
               + $"A sidestep cuts hits by {Cut(DodgeMode.Sidestep)}, a hard burn by {Cut(DodgeMode.HardBurn)}.";
    }

    private void Choose(string key)
    {
        if (_screen.EngagementNow is not EngagementInfo e) return;
        var o = e.Options.FirstOrDefault(x => x.Key == key);
        if (key == Engagement.LetGoKey)
        {
            _screen.IssueNow(new StopFleetCommand(e.Mine.Id), "let them go");
            Main.Instance.Toast($"{e.Mine.Name}: lets them go");
            return;
        }
        if (o?.Request is not FlightRequest req) return;
        Main.Instance.Sim!.Issue(new PlanFlightCommand(e.Mine.Id, req with { DepartTick = Main.Instance.Sim!.World.Tick }));
        Main.Instance.Toast($"{e.Mine.Name}: {o.Label.ToLowerInvariant()}");
    }
}

public enum RingStatus { Shown, TooSmall, Outside }

/// <summary>The rings the engagement screen draws: each side's weapon reach and how far you can spot them.</summary>
public static class EngagementRings
{
    public sealed record Ring(string Label, double Mm, bool AroundMine, Color Color, bool Dashed, float Width);

    public static readonly Color Railguns = new("3fd0ff");
    public static readonly Color Missiles = new("b58cff");
    public static readonly Color PointDefence = new("5fd6b0");
    public static readonly Color Spotting = new(0.62f, 0.92f, 0.62f);
    public static readonly Color TheirRailguns = new("ffb347");
    public static readonly Color TheirMissiles = new("ff7a59");

    public static List<Ring> For(EngagementInfo e)
    {
        var c = GameData.Combat;
        var list = new List<Ring>();
        if (e.WeHaveRailguns) list.Add(new Ring("Your railguns", c.Railgun.RangeMm, true, Railguns, false, 2.5f));
        if (e.WeHaveMissiles) list.Add(new Ring("Your missile launch", c.Missile.LaunchRangeMm, true, Missiles, false, 2f));
        list.Add(new Ring("Your point defence", c.PointDefence.RangeMm, true, PointDefence, false, 1.5f));
        list.Add(new Ring(e.Cued ? "You spot them coasting cold, on their track" : "You spot them coasting cold", e.SpotCold, true, Spotting, true, 1.5f));
        // Their reach is a guess until you have fought that fleet (board 26): what it showed, else other fleets, else your own tech.
        var guess = Main.Instance.Sim is { } sim ? RivalTech.Guess(sim.World, Lightlag.Sim.Core.Side.Player, e.EnemyId) : null;
        var tag = guess is { Plus: > 0 } ? " (guess)" : "";
        if (e.TheyHaveRailguns != false)
            list.Add(new Ring((e.TheyHaveRailguns == true ? "Their railguns" : "Their railguns, if they carry any") + tag, guess?.Rail ?? c.Railgun.RangeMm, false, TheirRailguns, true, 2f));
        if (e.TheyHaveMissiles != false)
            list.Add(new Ring((e.TheyHaveMissiles == true ? "Their missile launch" : "Their missile launch, if they carry any") + tag, guess?.Missile ?? c.Missile.LaunchRangeMm, false, TheirMissiles, true, 1.5f));
        if (guess is { Lance: > 0 })
            list.Add(new Ring("Their pulse lances" + tag, guess.Lance, false, TheirRailguns, true, 1f));
        return list;
    }

    /// <summary>Whether a ring of this screen radius shows: big enough, and crossing the view rather than wholly outside or around it.</summary>
    public static RingStatus Status(Vector2 at, float r, Rect2 view)
    {
        if (r < 6) return RingStatus.TooSmall;
        // Nearest and farthest points of the view from the centre.
        var nx = Mathf.Clamp(at.X, view.Position.X, view.End.X);
        var ny = Mathf.Clamp(at.Y, view.Position.Y, view.End.Y);
        var near = at.DistanceTo(new Vector2(nx, ny));
        var fx = Mathf.Max(Mathf.Abs(at.X - view.Position.X), Mathf.Abs(at.X - view.End.X));
        var fy = Mathf.Max(Mathf.Abs(at.Y - view.Position.Y), Mathf.Abs(at.Y - view.End.Y));
        var far = Mathf.Sqrt(fx * fx + fy * fy);
        return r < near || r > far ? RingStatus.Outside : RingStatus.Shown;
    }

    /// <summary>A point on the ring that is on screen, for its label (top first, then round the ring), or null.</summary>
    public static Vector2? LabelPoint(Vector2 at, float r, Rect2 view)
    {
        var inner = view.Grow(-30);
        for (var i = 0; i < 72; i++)
        {
            // -90 degrees (top), then alternating either side of it.
            var step = (i + 1) / 2 * (i % 2 == 0 ? 1 : -1);
            var a = -Mathf.Pi / 2 + step * Mathf.Tau / 72;
            var p = at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            if (inner.HasPoint(p)) return p;
        }
        return null;
    }
}

/// <summary>When each side's weapons can reach over the next while, and when missiles land.</summary>
public partial class EngagementTimeline : Control
{
    public EngagementInfo? Info;

    /// <summary>Your other fleets facing the same enemy: one more bar each.</summary>
    public List<EngagementInfo> Others = new();
    private Font _font = null!;
    private Button _toggle = null!;
    private const string Section = "battle-timeline";

    /// <summary>Open: one row per weapon. Shut (the default): everything on one bar. Kept in ui.cfg.</summary>
    public static bool Open => PlayerInfo.IsOpen(Section);
    public float WantHeight => (Open ? 170 : 60) + Others.Count * 40;

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;
        MouseFilter = MouseFilterEnum.Ignore;
        _toggle = Ui.SmallButton("", () => { PlayerInfo.SetOpen(Section, !Open); Sync(); }, "Show one row per weapon, or everything on one bar");
        AddChild(_toggle);
        Sync();
    }

    private void Sync()
    {
        _toggle.Text = Open ? "▾ One bar" : "▸ Open up";
        CustomMinimumSize = new Vector2(CustomMinimumSize.X, WantHeight);
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        _toggle.Size = _toggle.GetCombinedMinimumSize();
        _toggle.Position = new Vector2(Size.X - _toggle.Size.X, -2);
    }

    private static string When(double h) => h < 1 / 60.0 ? $"{h * 3600:0} s" : h < 2 ? $"{h * 60:0} min" : $"{h:0.#} h";

    /// <summary>Shut: one bar. Upper half is you (railguns, then missiles), lower half them, with the closest pass and salvos marked.</summary>
    private void DrawBar(EngagementInfo e, float y0 = 0, string? who = null)
    {
        var top = y0 + 24f;
        const float h = 20;
        var width = Size.X - 10;
        var span = e.LookAheadHours;
        float X(double hours) => (float)(Math.Clamp(hours / span, 0, 1) * width);
        string? Soon(TimeWindow? w) => w == null ? null : w.From < 1e-3 ? "now" : $"in {When(w.From)}";

        var bits = new List<string>();
        if (e.WeHaveRailguns && Soon(e.OurRailguns) is string a) bits.Add($"your guns {a}");
        if (e.WeHaveMissiles && Soon(e.OurMissiles) is string b) bits.Add($"your missiles {b}");
        if (Soon(e.TheirRailguns) is string c) bits.Add($"their guns {c}");
        if (Soon(e.TheirMissiles) is string d) bits.Add($"their missiles {d}");
        if (e.Salvos.Count > 0) bits.Add($"{e.Salvos.Sum(x => x.Count)} missiles flying");
        if (bits.Count == 0) bits.Add("nothing in reach");
        var title = (who != null ? who + ": " : "") + (e.InSight ? "" : "GUESS: ") + string.Join(" · ", bits);
        DrawString(_font, new Vector2(0, y0 + 14), title, HorizontalAlignment.Left, Size.X - _toggle.Size.X - 8, 13, e.InSight ? Ui.Text : Ui.Rival);

        DrawRect(new Rect2(0, top, width, h), new Color(Ui.Dim, 0.12f));
        void Lane(TimeWindow? w, int lane, Color color)
        {
            if (w == null) return;
            var x0 = X(w.From);
            var x1 = double.IsPositiveInfinity(w.To) ? width : X(w.To);
            DrawRect(new Rect2(x0, top + 1 + lane * 4.5f, Math.Max(3, x1 - x0), 4), new Color(color, lane % 2 == 0 ? 0.9f : 0.55f));
        }
        if (e.WeHaveRailguns) Lane(e.OurRailguns, 0, Ui.Player);
        if (e.WeHaveMissiles) Lane(e.OurMissiles, 1, Ui.Player);
        Lane(e.TheirRailguns, 2, Ui.Rival);
        Lane(e.TheirMissiles, 3, Ui.Rival);
        if (!e.InSight && e.SpotWindow != null) DrawLine(new Vector2(X(e.SpotWindow.From), top - 3), new Vector2(X(e.SpotWindow.From), top + h + 3), new Color(0.62f, 0.92f, 0.62f), 2);
        if (e.ClosestInHours > 1e-3)
            DrawDashedLine(new Vector2(X(e.ClosestInHours), top - 3), new Vector2(X(e.ClosestInHours), top + h + 3), new Color(1f, 0.83f, 0.47f), 1.5f, 3);
        foreach (var sv in e.Salvos) DrawCircle(new Vector2(X(sv.ImpactHours), top + h / 2), 4, sv.Ours ? Ui.Player : Ui.Danger);
        DrawString(_font, new Vector2(0, top + h + 13), "now", HorizontalAlignment.Left, -1, 11, Ui.Dim);
        var end = $"+{When(span)}";
        DrawString(_font, new Vector2(width - _font.GetStringSize(end, HorizontalAlignment.Left, -1, 11).X, top + h + 13), end, HorizontalAlignment.Left, -1, 11, Ui.Dim);
        if (y0 == 0) DrawString(_font, new Vector2(60, top + h + 13), "top: you (guns, missiles) · bottom: them · dashed: closest · dots: missiles land", HorizontalAlignment.Left, -1, 11, new Color(Ui.Dim, 0.8f));
    }

    public override void _Draw()
    {
        if (Info is not EngagementInfo e) return;
        var multi = Others.Count > 0;
        if (!Open) DrawBar(e, 0, multi ? e.Mine.Name : null);
        // Two or more of your fleets on one enemy: a bar each for the others, under the main view.
        var y = Open ? 170f : 60f;
        foreach (var o in Others) { DrawBar(o, y - 6, o.Mine.Name); y += 40; }
        if (!Open) return;
        const float labelW = 150, top = 22, row = 20;
        const int Rows = 6;
        var width = Size.X - labelW - 10;
        var span = e.LookAheadHours;
        float X(double hours) => labelW + (float)(Math.Clamp(hours / span, 0, 1) * width);

        DrawString(_font, new Vector2(0, 14), e.InSight ? "Timeline, if neither changes course"
            : $"Timeline from a GUESS: not seen for {When(e.SeenHoursAgo)}, it may be elsewhere", HorizontalAlignment.Left, -1, 13, e.InSight ? Ui.Text : Ui.Rival);
        for (var i = 0; i <= 4; i++)
        {
            var t = span * i / 4;
            var x = X(t);
            DrawLine(new Vector2(x, top - 2), new Vector2(x, top + row * Rows + 2), new Color(Ui.Dim, 0.25f), 1);
            DrawString(_font, new Vector2(x - 20, top + row * Rows + 16), i == 0 ? "now" : $"+{When(t)}", HorizontalAlignment.Left, -1, 11, Ui.Dim);
        }
        if (e.ClosestInHours > 1e-3)
        {
            var cx = X(e.ClosestInHours);
            DrawDashedLine(new Vector2(cx, top - 4), new Vector2(cx, top + row * Rows + 2), new Color(1f, 0.83f, 0.47f), 1.5f, 4);
            DrawString(_font, new Vector2(cx + 4, top - 6), "closest", HorizontalAlignment.Left, -1, 11, new Color(1f, 0.83f, 0.47f));
        }

        void Row(int i, string label, TimeWindow? win, Color color, string none)
        {
            var y = top + row * i;
            DrawString(_font, new Vector2(0, y + 13), label, HorizontalAlignment.Left, -1, 12, Ui.Text);
            if (win == null)
            {
                DrawString(_font, new Vector2(labelW, y + 13), none, HorizontalAlignment.Left, -1, 11, Ui.Dim);
                return;
            }
            var x0 = X(win.From);
            var x1 = double.IsPositiveInfinity(win.To) ? labelW + width : X(win.To);
            var r = new Rect2(x0, y + 3, Math.Max(4, x1 - x0), 12);
            DrawRect(r, new Color(color, 0.35f));
            DrawRect(r, color, false, 1);
            var text = win.From < 1e-3 ? (e.InSight ? "in reach now" : "in reach of the guess") : $"from {When(win.From)}" + (e.InSight ? "" : " (guess)");
            if (!double.IsPositiveInfinity(win.To)) text += $" for {When(win.To - win.From)}";
            DrawString(_font, new Vector2(Math.Min(x1 + 4, labelW + width - 120), y + 13), text, HorizontalAlignment.Left, -1, 11, color);
        }

        Row(0, "Your railguns", e.WeHaveRailguns ? e.OurRailguns : null, Ui.Player, e.WeHaveRailguns ? "never in reach" : "none working");
        Row(1, "Your missiles", e.WeHaveMissiles ? e.OurMissiles : null, Ui.Player, e.WeHaveMissiles ? "never in launch range" : "none left");
        Row(2, "Their railguns", e.TheirRailguns, Ui.Rival, e.TheyHaveRailguns == false ? "they carry none" : "never in reach");
        Row(3, "Their missiles", e.TheirMissiles, Ui.Rival, e.TheyHaveMissiles == false ? "they carry none"
            : e.TheyHaveMissiles == true ? "never in launch range" : "never in launch range (if they carry any)");
        var spotColor = new Color(0.62f, 0.92f, 0.62f);
        if (e.InSight)
        {
            DrawString(_font, new Vector2(0, top + row * 5 + 13), "Spotting them", HorizontalAlignment.Left, -1, 12, Ui.Text);
            DrawString(_font, new Vector2(labelW, top + row * 5 + 13), "in sight now", HorizontalAlignment.Left, -1, 11, spotColor);
        }
        else Row(5, "Spotting them", e.SpotWindow, spotColor, $"not before closest pass, if they coast cold (seen within {Format.Distance(e.SpotCold)})");

        // Missiles in flight: a mark at each salvo's impact time.
        var y4 = top + row * 4;
        DrawString(_font, new Vector2(0, y4 + 13), "Missiles flying", HorizontalAlignment.Left, -1, 12, Ui.Text);
        if (e.Salvos.Count == 0) DrawString(_font, new Vector2(labelW, y4 + 13), "none", HorizontalAlignment.Left, -1, 11, Ui.Dim);
        foreach (var s in e.Salvos)
        {
            var x = X(s.ImpactHours);
            var c = s.Ours ? Ui.Player : Ui.Danger;
            DrawCircle(new Vector2(x, y4 + 9), 5, c);
            var label = $"{s.Count} {(s.Ours ? "yours" : "at you")}, {When(s.ImpactHours)}";
            var lx = Math.Min(x + 7, labelW + width - _font.GetStringSize(label, HorizontalAlignment.Left, -1, 11).X);
            DrawString(_font, new Vector2(lx, y4 + 13 + (lx < x ? 12 : 0)), label, HorizontalAlignment.Left, -1, 11, c);
        }
    }
}
