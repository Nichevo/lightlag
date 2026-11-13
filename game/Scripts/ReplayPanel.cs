using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Watch a battle again from its recording: ships, missiles and shots on a battle-sized map, a
/// timeline with the moments that mattered (decisions fleets made for themselves, orders arriving at
/// light speed, ships lost, a flagship spotted) and play, pause and speed. A far battle can be watched
/// only as far as its light has reached the flagship: the end of the timeline grows as the light comes in.
/// </summary>
public partial class ReplayPanel : PanelContainer
{
    private readonly GameScreen _screen;
    private Battle? _battle;
    private double _at;            // battle hour being shown
    private bool _playing = true;
    private double _speed = 2;     // battle minutes per real second
    private Label _title = null!;
    private Label _status = null!;
    private Button _play = null!;
    private HSlider _slider = null!;
    private ItemList _marks = null!;
    private ReplayView _view = null!;
    private bool _settingSlider;
    private readonly List<BattleMark> _shownMarks = new();

    public ReplayPanel() : this(null!) { }
    public ReplayPanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 14);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        _title = Ui.Label("", 18, Ui.Player);
        box.AddChild(_title);

        var middle = new HBoxContainer();
        middle.AddThemeConstantOverride("separation", 10);
        box.AddChild(middle);
        _view = new ReplayView(this) { CustomMinimumSize = new Vector2(760, 470), ClipContents = true };
        middle.AddChild(_view);
        var side = new VBoxContainer { CustomMinimumSize = new Vector2(300, 0) };
        middle.AddChild(side);
        side.AddChild(Ui.Label("Moments (click to jump)", 13, Ui.Dim));
        _marks = new ItemList { CustomMinimumSize = new Vector2(300, 440), SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None };
        _marks.AddThemeFontSizeOverride("font_size", 12);
        _marks.ItemClicked += (i, _, _) => { if (i < _shownMarks.Count) Seek(_shownMarks[(int)i].Hour - 0.5 / 60); };
        side.AddChild(_marks);

        _slider = new HSlider { MinValue = 0, MaxValue = 1, Step = 0, CustomMinimumSize = new Vector2(1070, 20), FocusMode = FocusModeEnum.None };
        _slider.ValueChanged += v => { if (!_settingSlider) Seek(v); };
        box.AddChild(_slider);

        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 8);
        box.AddChild(bar);
        _play = Ui.SmallButton("Pause", () => _playing = !_playing, "Play or pause the replay (the game itself runs or stays paused as before)");
        bar.AddChild(_play);
        bar.AddChild(Ui.SmallButton("⏮ Start", () => Seek(_battle?.StartHour ?? 0), "Back to the start of the battle"));
        foreach (var (label, s) in new[] { ("½×", 0.5), ("1×", 2.0), ("4×", 8.0), ("16×", 32.0) })
            bar.AddChild(Ui.SmallButton(label, () => _speed = s, $"{s:0.#} battle minutes per second"));
        _status = Ui.Label("", 13, Ui.Dim);
        _status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        bar.AddChild(_status);
        bar.AddChild(Ui.SmallButton("Report", () => _screen.ShowReportOf(_battle), "The numbers of this battle"));
        bar.AddChild(Ui.SmallButton("Close (Esc)", () => _screen.CloseReplay(), "Close the replay"));
    }

    public Battle? Battle => _battle;
    public double At => _at;

    public void Open(Battle b)
    {
        if (_battle?.Id != b.Id) { _at = b.StartHour; _playing = true; }
        _battle = b;
        if (_screen.ReplaySeek is double at) { _screen.ReplaySeek = null; Seek(at); _playing = false; }
    }

    private void Seek(double hour)
    {
        if (_battle == null) return;
        _at = Math.Clamp(hour, _battle.StartHour, Until());
    }

    /// <summary>The latest moment of the battle whose light has reached the flagship.</summary>
    public double Until()
    {
        var w = Main.Instance.Sim!.World;
        if (_battle == null) return 0;
        var last = _battle.Frames.Count > 0 ? _battle.Frames[^1].Hour : _battle.StartHour;
        return Math.Min(last, BattleRecords.SeenUntil(w, _battle, Side.Player));
    }

    public override void _Process(double delta)
    {
        if (!Visible || _battle == null) return;
        var until = Until();
        if (_playing) _at = Math.Min(until, _at + delta * _speed / 60);
        _play.Text = _playing ? "Pause" : "Play";
        var b = _battle;
        var end = b.EndHour is double e ? e : (double?)null;
        _title.Text = $"Battle replay: {string.Join(", ", b.For(Side.Player).Fleets)} and {string.Join(", ", b.Foes(Side.Player).Fleets)}, {GameTime.Format(b.StartHour)}";
        var lag = BattleRecords.SeenFromHour(Main.Instance.Sim!.World, b, Side.Player) - b.StartHour;
        var light = end == null || until < end - 1e-6
            ? $" · light has arrived up to {Format.Hours(until - b.StartHour)} in" + (lag > 1.0 / 60 ? $" ({Format.Hours(lag)} away)" : "")
            : "";
        _status.Text = $"{Format.Hours(_at - b.StartHour)} of {Format.Hours((end ?? until) - b.StartHour)}{light}";
        _settingSlider = true;
        _slider.MinValue = b.StartHour;
        _slider.MaxValue = Math.Max(b.StartHour + 1e-6, end ?? until);
        _slider.Value = _at;
        _settingSlider = false;
        FillMarks(until);
        _view.QueueRedraw();
    }

    /// <summary>Our own side's moments, plus ships lost on either side, up to what the light has brought.</summary>
    public IEnumerable<BattleMark> VisibleMarks(double until) =>
        _battle == null ? Enumerable.Empty<BattleMark>()
            : _battle.Marks.Where(m => m.Hour <= until + 1e-9 && (m.Kind == MarkKind.ShipLost || m.Side == Side.Player));

    private void FillMarks(double until)
    {
        var marks = VisibleMarks(until).ToList();
        if (marks.Count == _shownMarks.Count) return;
        _shownMarks.Clear();
        _shownMarks.AddRange(marks);
        _marks.Clear();
        foreach (var m in marks)
        {
            var i = _marks.AddItem($"{Format.Hours(m.Hour - _battle!.StartHour)}  {m.Text}");
            _marks.SetItemCustomFgColor(i, MarkColour(m));
        }
    }

    public static Color MarkColour(BattleMark m) => m.Kind switch
    {
        MarkKind.Decision => new Color("ffe066"),
        MarkKind.OrderArrived => Ui.Player,
        MarkKind.ShipLost => Ui.Danger,
        _ => new Color("c792ea"),
    };
}

/// <summary>The battle map of a replay: draws the recording at the replay's moment.</summary>
public partial class ReplayView : Control
{
    private readonly ReplayPanel _panel;
    public ReplayView() : this(null!) { }
    public ReplayView(ReplayPanel panel) => _panel = panel;

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.01f, 0.02f, 0.05f, 1));
        if (_panel.Battle is not Battle b || b.Frames.Count == 0)
        {
            DrawString(ThemeDB.FallbackFont, new Vector2(20, 40), "No recording of this battle (only the latest battles keep one).", fontSize: 14, modulate: Ui.Dim);
            return;
        }
        var until = _panel.Until();
        var frames = b.Frames.Where(f => f.Hour <= until + 1e-9).ToList();
        if (frames.Count == 0)
        {
            DrawString(ThemeDB.FallbackFont, new Vector2(20, 40), "The light from this battle has not reached the flagship yet.", fontSize: 14, modulate: Ui.Dim);
            return;
        }

        // Fit every ship position seen so far.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var f in frames)
            foreach (var s in f.Ships)
            {
                minX = Math.Min(minX, s.X); maxX = Math.Max(maxX, s.X);
                minY = Math.Min(minY, s.Y); maxY = Math.Max(maxY, s.Y);
            }
        if (minX > maxX) { minX = maxX = b.Where.X; minY = maxY = b.Where.Y; }
        var span = Math.Max(Math.Max(maxX - minX, maxY - minY), 20) * 1.15;
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        var scale = Math.Min(Size.X, Size.Y) / span;
        Vector2 P(double x, double y) => new((float)((x - cx) * scale + Size.X / 2), (float)((y - cy) * scale + Size.Y / 2));

        // Scale bar.
        var bar = Math.Pow(10, Math.Floor(Math.Log10(span / 3)));
        var px = (float)(bar * scale);
        DrawLine(new Vector2(16, Size.Y - 16), new Vector2(16 + px, Size.Y - 16), Ui.Dim, 2);
        DrawString(ThemeDB.FallbackFont, new Vector2(16, Size.Y - 22), Format.Distance(bar), fontSize: 11, modulate: Ui.Dim);

        // The frame pair around the moment shown, and how far between them.
        var at = _panel.At;
        var i = frames.FindLastIndex(f => f.Hour <= at + 1e-9);
        if (i < 0) i = 0;
        var a = frames[i];
        var c = i + 1 < frames.Count ? frames[i + 1] : a;
        var k = c.Hour > a.Hour ? (float)Math.Clamp((at - a.Hour) / (c.Hour - a.Hour), 0, 1) : 0f;

        // Shots recorded between the two frames.
        foreach (var s in c.Shots)
        {
            var col = Ui.SideColor(s.Side);
            switch (s.Kind)
            {
                case EffectKind.Volley:
                    DrawLine(P(s.X0, s.Y0), P(s.X1, s.Y1), col with { A = 0.35f }, 1);
                    break;
                case EffectKind.MissileHit:
                    DrawCircle(P(s.X1, s.Y1), 7, new Color(1, 0.6f, 0.2f, 0.7f));
                    break;
                case EffectKind.PointDefenceKill:
                    DrawCircle(P(s.X1, s.Y1), 3, new Color(1, 1, 1, 0.6f));
                    break;
                case EffectKind.Decoy:
                    DrawCircle(P(s.X0, s.Y0), 3, new Color(1, 0.55f, 0.15f, 0.7f));
                    break;
            }
        }

        var roster = b.Roster.ToDictionary(r => r.Id);
        var next = c.Ships.ToDictionary(s => s.Id);
        foreach (var s in a.Ships)
        {
            if (!roster.TryGetValue(s.Id, out var r)) continue;
            var to = next.TryGetValue(s.Id, out var n) ? n : s;
            var pos = P(s.X, s.Y).Lerp(P(to.X, to.Y), k);
            var col = Ui.SideColor(r.Side);
            var dead = !next.ContainsKey(s.Id) && c != a;
            var big = r.ClassId.Contains("destroyer");
            var size = big ? 7f : 5f;
            if (dead) col = col with { A = 1 - k };
            DrawColoredPolygon(new[] { pos + new Vector2(0, -size), pos + new Vector2(size, size * 0.8f), pos + new Vector2(-size, size * 0.8f) }, col);
            var share = r.MaxArmour > 0 ? (float)Math.Clamp(Mathf.Lerp((float)s.Armour, (float)to.Armour, k) / r.MaxArmour, 0, 1) : 1;
            DrawRect(new Rect2(pos + new Vector2(-8, size + 3), new Vector2(16, 2)), new Color(0.2f, 0.2f, 0.2f));
            DrawRect(new Rect2(pos + new Vector2(-8, size + 3), new Vector2(16 * share, 2)), share > 0.5f ? new Color("6bd16b") : Ui.Danger);
            if (r.Flagship) DrawString(ThemeDB.FallbackFont, pos + new Vector2(size + 2, -size), "⚑", fontSize: 12, modulate: col);
        }
        foreach (var m in a.Missiles)
            DrawCircle(P(m.X, m.Y), 1.8f, Ui.SideColor(m.Side));

        // Timeline marks along the bottom edge.
        var length = Math.Max(1e-6, (b.EndHour ?? until) - b.StartHour);
        foreach (var m in _panel.VisibleMarks(until))
        {
            var x = (float)((m.Hour - b.StartHour) / length) * (Size.X - 20) + 10;
            DrawLine(new Vector2(x, Size.Y - 8), new Vector2(x, Size.Y), ReplayPanel.MarkColour(m), 2);
        }

        // Fleet labels at their first ship.
        foreach (var g in a.Ships.Where(s => roster.ContainsKey(s.Id)).GroupBy(s => roster[s.Id].Fleet))
        {
            var s = g.First();
            var r = roster[s.Id];
            DrawString(ThemeDB.FallbackFont, P(s.X, s.Y) + new Vector2(10, 14), g.Key, fontSize: 11, modulate: Ui.SideColor(r.Side) with { A = 0.8f });
        }
    }
}
