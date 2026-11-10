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
    private Label _defence = null!, _ships = null!;
    private Button _dodgeOff = null!, _sidestep = null!, _hardBurn = null!, _focused = null!, _spread = null!;
    private Button _engage = null!, _hold = null!;
    private EngagementTimeline _timeline = null!;
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
        right.AddChild(Row("Railguns", _focused, _spread));
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
        left.AddChild(_ships);
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
        _headPanel.Position = new Vector2(Mathf.Max(16, (size.X - _headPanel.Size.X) / 2), 56);
        var top = _headPanel.Position.Y + _headPanel.Size.Y + 10;
        // The choices scroll when the screen is too short for them all.
        var want = _right.GetCombinedMinimumSize();
        _rightScroll.CustomMinimumSize = new Vector2(want.X + 12, Mathf.Min(want.Y, size.Y - top - 50));
        var timelineW = Mathf.Clamp(size.X - _rightPanel.Size.X - 64, 500, 1100);
        _timeline.CustomMinimumSize = new Vector2(timelineW, 170);
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
        if (key != _tabsKey)
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
        if (key == _legendKey) return;
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

    private void SetStance(Stance stance)
    {
        if (_screen.EngagementNow is not EngagementInfo e) return;
        Main.Instance.Sim!.Issue(new SetStanceCommand(e.Mine.Id, stance, e.Mine.RetreatWhenDamaged));
    }

    private static string When(double hours) =>
        double.IsPositiveInfinity(hours) ? "later" : hours < 1 / 60.0 ? $"{hours * 3600:0} s" : hours < 2 ? $"{hours * 60:0} min" : $"{hours:0.#} h";

    private static string Km(double mm) => Format.Distance(mm);

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
                       + (Light.DelayHours(w, Lightlag.Sim.Core.Side.Player, f.Position) is var lag && lag > 0
                           ? $"\nOrders take {Format.Lag(lag)} to reach {f.Name}; this picture is {Format.Lag(Light.AgeHours(w, Lightlag.Sim.Core.Side.Player, e.EnemyId))} old."
                           : "");
        RefreshTabs(e);

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
        _engage.Modulate = stance == Stance.Engage ? Ui.Player : new Color(1, 1, 1, 0.7f);
        _hold.Modulate = stance == Stance.Hold ? Ui.Player : new Color(1, 1, 1, 0.7f);
        var ships = w.ShipsOf(f).ToList();
        var supplies = Combat.SupplyShare(w, f);
        var armour = ships.Count == 0 ? 0 : ships.Average(s => s.Armour / GameData.ShipClass(s.ClassId).Armour);
        var pd = ships.Count(s => GameData.ShipClass(s.ClassId).PointDefence > 0 && s.Disabled != Module.PointDefence);
        var lines = new List<string>
        {
            dodge == DodgeMode.Off ? "Not dodging" : Combat.Dodging(w, f) || dodge != f.Dodge ? $"Dodging ({(dodge == DodgeMode.HardBurn ? "hard burn" : "sidestep")})" : $"Dodging stopped: supplies below {GameData.Combat.DodgeStopsBelow:P0}",
            IncomingLine(w, e),
            $"Supplies {supplies:P0} · fuel for {Format.Speed(FleetPerformance.Of(w, f).DeltaVLeft)} of speed change",
            $"Armour {armour:P0} on average",
            $"Point defence on {pd} of {ships.Count} ships, reach {Km(GameData.Combat.PointDefence.RangeMm)}",
            $"Missiles left: {ships.Sum(s => s.Missiles)}",
        };
        if (f.Chase is { Problem: string problem }) lines.Add($"Chase trouble: {problem}");
        _defence.Text = string.Join("\n", lines);

        // Ships.
        var shipLines = new List<string> { $"{f.Name}" };
        foreach (var s in ships)
        {
            var cls = GameData.ShipClass(s.ClassId);
            var hurt = s.Disabled is Module m ? $", {Combat.ModuleName(m)} out" : "";
            shipLines.Add($"  {s.Name} ({cls.Short}): armour {s.Armour / cls.Armour:P0}, supplies {s.Supplies / cls.Supplies:P0}" + (cls.Missiles > 0 ? $", {s.Missiles} missiles" : "") + hurt);
        }
        shipLines.Add("");
        shipLines.Add($"{e.EnemyName}: {e.EnemyShips} ships");
        shipLines.Add(e.EnemyClasses is string kinds
            ? $"  {kinds}" + (e.ClassesSeenHoursAgo > 1 ? $" (on a close look {When(e.ClassesSeenHoursAgo)} ago)" : "")
            : $"  types unknown: come within {Km(Sensors.IdentifyRange(w, Lightlag.Sim.Core.Side.Player))} to tell");
        if (e.TheyHaveRailguns != null || e.TheyHaveMissiles != null)
            shipLines.Add($"  carry: {Carries(e)}");
        shipLines.Add($"  moving at {Format.Speed(e.EnemyVelocity.Length)}" + (e.InSight ? "" : " when last seen"));
        shipLines.Add($"  this fleet's top speed: {Format.Speed(e.OurTopSpeed)}");
        shipLines.Add("");
        shipLines.Add("Spotting");
        shipLines.Add($"  You see them within {Km(e.SpotCold)} coasting cold, {Km(e.SpotWarm)} with warm engines, {Km(e.SpotBurn)} burning.");
        if (e.Cued)
            shipLines.Add(e.InSight
                ? $"  That is holding a contact you already have; a fleet never seen shows up only within {Km(e.SpotColdUncued)} cold."
                : $"  That is with your sensors searching their predicted track: if they changed course, only {Km(e.SpotColdUncued)} cold.");
        if (!e.InSight)
            shipLines.Add(e.SpotWindow is TimeWindow sw
                ? $"  If they keep coasting you spot them in {When(sw.From)}."
                : $"  They are {Km(e.Gap)} away: too far to spot while they coast cold.");
        var us = e.OurState switch
        {
            SignatureState.Burn => "your engines are burning",
            SignatureState.Warm => "your engines are still warm",
            SignatureState.Orbit => "you are holding orbit",
            _ => "you are coasting cold",
        };
        shipLines.Add($"  They see you from at least {Km(e.TheySeeUsAt)}: {us}" + (e.Gap <= e.TheySeeUsAt ? ", so expect they can see you now." : "."));
        _ships.Text = string.Join("\n", shipLines);
        RefreshLegend(e);

        _timeline.Info = e;
        _timeline.QueueRedraw();
        Layout();
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
        return $"Incoming: {(fire == FireMode.Spread ? "spread fans" : "aimed slugs")}, arriving in {secs:0} s. "
               + $"A sidestep cuts hits by {Cut(DodgeMode.Sidestep)}, a hard burn by {Cut(DodgeMode.HardBurn)}.";
    }

    private void Choose(string key)
    {
        if (_screen.EngagementNow is not EngagementInfo e) return;
        var o = e.Options.FirstOrDefault(x => x.Key == key);
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
        if (e.TheyHaveRailguns != false)
            list.Add(new Ring(e.TheyHaveRailguns == true ? "Their railguns" : "Their railguns, if they carry any", c.Railgun.RangeMm, false, TheirRailguns, true, 2f));
        if (e.TheyHaveMissiles != false)
            list.Add(new Ring(e.TheyHaveMissiles == true ? "Their missile launch" : "Their missile launch, if they carry any", c.Missile.LaunchRangeMm, false, TheirMissiles, true, 1.5f));
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
    private Font _font = null!;

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        if (Info is not EngagementInfo e) return;
        const float labelW = 150, top = 22, row = 20;
        const int Rows = 6;
        var width = Size.X - labelW - 10;
        var span = e.LookAheadHours;
        float X(double hours) => labelW + (float)(Math.Clamp(hours / span, 0, 1) * width);
        string When(double h) => h < 1 / 60.0 ? $"{h * 3600:0} s" : h < 2 ? $"{h * 60:0} min" : $"{h:0.#} h";

        DrawString(_font, new Vector2(0, 14), "Timeline, if neither changes course", HorizontalAlignment.Left, -1, 13, Ui.Text);
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
            var text = win.From < 1e-3 ? "in reach now" : $"from {When(win.From)}";
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
