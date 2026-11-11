using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// A folding section of a panel: a header with its name and one summary line; click to open the rows
/// below. Which sections are open is remembered (see PlayerInfo); in Full detail every section is open.
/// </summary>
public partial class FoldSection : VBoxContainer
{
    private readonly string _name;
    private readonly string _tip;
    private Button _head = null!;
    private Label _summary = null!;
    private MarginContainer _indent = null!;

    public VBoxContainer Body { get; } = new();

    public FoldSection() : this("", "") { }

    public FoldSection(string name, string tip)
    {
        _name = name;
        _tip = tip;
        AddThemeConstantOverride("separation", 2);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        _head = new Button { Flat = true, FocusMode = FocusModeEnum.None, Alignment = HorizontalAlignment.Left, TooltipText = tip, CustomMinimumSize = new Vector2(0, 24) };
        _head.AddThemeFontSizeOverride("font_size", 13);
        _head.AddThemeColorOverride("font_color", Ui.Text);
        _head.Pressed += Flip;
        head.AddChild(_head);
        _summary = Ui.Label("", 12, Ui.Dim);
        _summary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _summary.ClipText = true;
        _summary.MouseFilter = MouseFilterEnum.Stop;
        _summary.TooltipText = tip;
        _summary.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Flip(); };
        head.AddChild(_summary);
        AddChild(head);
        _indent = new MarginContainer();
        _indent.AddThemeConstantOverride("margin_left", 10);
        Body.AddThemeConstantOverride("separation", 4);
        _indent.AddChild(Body);
        AddChild(_indent);
    }

    private void Flip()
    {
        if (PlayerInfo.Detail == DetailLevel.Full) return;
        PlayerInfo.SetOpen(_name, !PlayerInfo.IsOpen(_name));
    }

    /// <summary>Show or hide the section, with its summary line (and a title other than its name, such as "Ships (4)").</summary>
    public void Show(bool visible, string summary, string? title = null)
    {
        Visible = visible;
        if (!visible) return;
        var open = PlayerInfo.IsOpen(_name);
        _head.Text = $"{(open ? "▾" : "▸")} {title ?? _name}";
        _head.TooltipText = PlayerInfo.Detail == DetailLevel.Full ? _tip + " (Full detail keeps every section open)" : _tip + (open ? " · click to fold" : " · click to open");
        _summary.Text = summary;
        _summary.Visible = !open || summary.Length < 40;
        _indent.Visible = open;
    }
}

/// <summary>The five damage bars of a fleet (hull, weapons, engines, defence, crew): the fleet's average, worst ship in the tooltip.</summary>
public partial class WoundBars : Control
{
    public static readonly string[] Names = { "Hull", "Weapons", "Engines", "Defence", "Crew" };
    private readonly double[] _values = new double[5];

    public WoundBars()
    {
        CustomMinimumSize = new Vector2(330, 26);
        MouseFilter = MouseFilterEnum.Pass;
    }

    public static Color BarColor(double v) => v < 0.35 ? Ui.Danger : v < 0.7 ? Ui.Rival : new Color("7fd67f");

    public static double[] Of(World w, Ship s) =>
        new[] { Math.Clamp(s.Armour / Tech.MaxArmour(w, s), 0, 1), s.Weapons, s.Engines, s.Defence, s.Crew };

    public void Set(World w, IReadOnlyList<Ship> ships)
    {
        if (ships.Count == 0) return;
        var all = ships.Select(s => Of(w, s)).ToList();
        var tips = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            _values[i] = all.Average(v => v[i]);
            var worst = ships.Zip(all).OrderBy(p => p.Second[i]).First();
            tips.Add($"{Names[i]} {_values[i]:P0} average" + (ships.Count > 1 ? $", lowest {worst.First.Name} {worst.Second[i]:P0}" : ""));
        }
        tips.Add("Engines at 0: a quarter speed. Weapons, defence and crew mend at a yard; hull needs alloys");
        TooltipText = string.Join("\n", tips);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        var w = (Size.X - 4 * 6) / 5;
        for (var i = 0; i < 5; i++)
        {
            var x = i * (w + 6);
            var v = (float)Math.Clamp(_values[i], 0, 1);
            DrawRect(new Rect2(x, 2, w, 6), new Color(1, 1, 1, 0.08f));
            DrawRect(new Rect2(x, 2, w * v, 6), BarColor(v));
            DrawString(font, new Vector2(x, 22), Names[i], HorizontalAlignment.Left, w, 11, Ui.Dim);
        }
    }
}
