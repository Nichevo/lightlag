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
    private CheckBox _dodge = null!;
    private Button _engage = null!, _hold = null!;
    private EngagementTimeline _timeline = null!;
    private bool _updating;

    public EngagementPanel() : this(null!) { }
    public EngagementPanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        // Header across the top, under the menu bar.
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 12);
        _header = Ui.Label("", 15, Ui.Text);
        _header.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _header.CustomMinimumSize = new Vector2(760, 0);
        head.AddChild(_header);
        head.AddChild(Ui.SmallButton("Close (V)", () => _screen.CloseEngagement(), "Back to the system map"));
        var headPanel = Ui.Panel(head, 10);
        headPanel.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop, LayoutPresetMode.KeepSize, 56);
        headPanel.GrowHorizontal = GrowDirection.Both;
        AddChild(headPanel);

        // Right: approach choices and defences.
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0) };
        right.AddThemeConstantOverride("separation", 8);
        right.AddChild(Ui.Label("Approach", 16, Ui.Text));
        _options = new VBoxContainer();
        _options.AddThemeConstantOverride("separation", 4);
        right.AddChild(_options);

        right.AddChild(Ui.Label("Defences", 16, Ui.Text));
        _dodge = new CheckBox { Text = "Dodge railgun fire", FocusMode = FocusModeEnum.None };
        _dodge.TooltipText = $"Sidestep slugs: each one aimed at this fleet hits {GameData.Combat.DodgeFactor:P0} as often and costs "
                             + $"{FlightPlanner.Kms(GameData.Combat.DodgeDeltaV)} of fuel. Stops below {GameData.Combat.DodgeStopsBelow:P0} supplies.";
        _dodge.Toggled += on =>
        {
            if (_updating || _screen.EngagementNow is not EngagementInfo e) return;
            Main.Instance.Sim!.Issue(new SetDodgeCommand(e.Mine.Id, on));
        };
        right.AddChild(_dodge);
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
        var rightPanel = Ui.Panel(right, 12);
        rightPanel.SetAnchorsAndOffsetsPreset(LayoutPreset.TopRight, LayoutPresetMode.KeepSize, 16);
        rightPanel.GrowHorizontal = GrowDirection.Begin;
        rightPanel.Position += new Vector2(0, 110);
        AddChild(rightPanel);

        // Left: the ships on both sides.
        _ships = Ui.Label("", 13, Ui.Text);
        _ships.CustomMinimumSize = new Vector2(300, 0);
        _ships.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        var leftPanel = Ui.Panel(_ships, 12);
        leftPanel.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft, LayoutPresetMode.KeepSize, 16);
        leftPanel.Position += new Vector2(0, 110);
        leftPanel.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(leftPanel);

        // Bottom: the timeline.
        _timeline = new EngagementTimeline { CustomMinimumSize = new Vector2(900, 150) };
        var bottom = Ui.Panel(_timeline, 10);
        bottom.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom, LayoutPresetMode.KeepSize, 16);
        bottom.GrowHorizontal = GrowDirection.Both;
        bottom.GrowVertical = GrowDirection.Begin;
        bottom.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(bottom);
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
        var seen = e.InSight ? "in sight" : $"out of sight, last seen {When(e.SeenHoursAgo)} ago (predicted)";
        var how = e.Closing > 1e-3 ? $"closing at {Format.Speed(e.Closing)}" : e.Closing < -1e-3 ? $"pulling apart at {Format.Speed(-e.Closing)}" : "holding distance";
        var pass = e.ClosestInHours < 1e-3
            ? "they are as close as they will get"
            : $"closest pass in {When(e.ClosestInHours)} at {Km(e.ClosestGap)}";
        _header.Text = $"ENGAGEMENT: {f.Name} and {e.EnemyName} ({e.EnemyShips} ships, {seen})\n"
                       + $"{Km(e.Gap)} apart, {how}; {pass}, if neither changes course.";

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
        _updating = true;
        _dodge.ButtonPressed = f.Dodge;
        _updating = false;
        _engage.Modulate = f.Stance == Stance.Engage ? Ui.Player : new Color(1, 1, 1, 0.7f);
        _hold.Modulate = f.Stance == Stance.Hold ? Ui.Player : new Color(1, 1, 1, 0.7f);
        var ships = w.ShipsOf(f).ToList();
        var supplies = Combat.SupplyShare(w, f);
        var armour = ships.Count == 0 ? 0 : ships.Average(s => s.Armour / GameData.ShipClass(s.ClassId).Armour);
        var pd = ships.Count(s => GameData.ShipClass(s.ClassId).PointDefence > 0 && s.Disabled != Module.PointDefence);
        var lines = new List<string>
        {
            f.Dodge ? (Combat.Dodging(w, f) ? "Dodging: on" : $"Dodging: stopped, supplies below {GameData.Combat.DodgeStopsBelow:P0}") : "Dodging: off",
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
        shipLines.Add($"  moving at {Format.Speed(e.EnemyVelocity.Length)}" + (e.InSight ? "" : " when last seen"));
        shipLines.Add($"  this fleet's top speed: {Format.Speed(e.OurTopSpeed)}");
        _ships.Text = string.Join("\n", shipLines);

        _timeline.Info = e;
        _timeline.QueueRedraw();
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
        var width = Size.X - labelW - 10;
        var span = e.LookAheadHours;
        float X(double hours) => labelW + (float)(Math.Clamp(hours / span, 0, 1) * width);
        string When(double h) => h < 1 / 60.0 ? $"{h * 3600:0} s" : h < 2 ? $"{h * 60:0} min" : $"{h:0.#} h";

        DrawString(_font, new Vector2(0, 14), "Timeline, if neither changes course", HorizontalAlignment.Left, -1, 13, Ui.Text);
        for (var i = 0; i <= 4; i++)
        {
            var t = span * i / 4;
            var x = X(t);
            DrawLine(new Vector2(x, top - 2), new Vector2(x, top + row * 5 + 2), new Color(Ui.Dim, 0.25f), 1);
            DrawString(_font, new Vector2(x - 20, top + row * 5 + 16), i == 0 ? "now" : $"+{When(t)}", HorizontalAlignment.Left, -1, 11, Ui.Dim);
        }
        if (e.ClosestInHours > 1e-3)
        {
            var cx = X(e.ClosestInHours);
            DrawDashedLine(new Vector2(cx, top - 4), new Vector2(cx, top + row * 5 + 2), new Color(1f, 0.83f, 0.47f), 1.5f, 4);
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
        Row(2, "Their railguns", e.TheirRailguns, Ui.Rival, "never in reach");
        Row(3, "Their missiles", e.TheirMissiles, Ui.Rival, "never in launch range (if they carry any)");

        // Missiles in flight: a mark at each salvo's impact time.
        var y4 = top + row * 4;
        DrawString(_font, new Vector2(0, y4 + 13), "Missiles flying", HorizontalAlignment.Left, -1, 12, Ui.Text);
        if (e.Salvos.Count == 0) DrawString(_font, new Vector2(labelW, y4 + 13), "none", HorizontalAlignment.Left, -1, 11, Ui.Dim);
        foreach (var s in e.Salvos)
        {
            var x = X(s.ImpactHours);
            var c = s.Ours ? Ui.Player : Ui.Danger;
            DrawCircle(new Vector2(x, y4 + 9), 5, c);
            DrawString(_font, new Vector2(x + 7, y4 + 13), $"{s.Count} {(s.Ours ? "yours" : "at you")}, {When(s.ImpactHours)}", HorizontalAlignment.Left, -1, 11, c);
        }
    }
}
