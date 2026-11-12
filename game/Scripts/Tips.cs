using Godot;

namespace Lightlag.Game;

/// <summary>
/// Readable hover popups: a styled card (title, lines, wrapped to a fixed width) in place of Godot's plain
/// tooltip text. Give a TipButton or TipLabel a Tip function; it is built fresh each time the popup opens.
/// </summary>
public static class Tips
{
    public const float Width = 340;

    public static Control Card(string title, Color titleColor, IEnumerable<(string Text, Color? Color)> lines)
    {
        var panel = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = new Color(0.04f, 0.06f, 0.1f, 0.97f), BorderColor = new Color(Ui.Player, 0.45f) };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(6);
        style.SetContentMarginAll(10);
        panel.AddThemeStyleboxOverride("panel", style);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        panel.AddChild(col);
        col.AddChild(Ui.Label(title, 16, titleColor));
        foreach (var (text, color) in lines)
        {
            if (string.IsNullOrEmpty(text)) continue;
            var l = Ui.Label(text, 13, color ?? Ui.Text);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.CustomMinimumSize = new Vector2(Width, 0);
            col.AddChild(l);
        }
        return panel;
    }
}

/// <summary>A button whose hover popup is a styled card (see Tips).</summary>
public partial class TipButton : Button
{
    public Func<Control>? Tip { get; set; }
    public override GodotObject _MakeCustomTooltip(string forText) => Tip?.Invoke() ?? null!;
}

/// <summary>A label that takes the mouse and shows a styled card on hover (see Tips).</summary>
public partial class TipLabel : Label
{
    public Func<Control>? Tip { get; set; }
    public TipLabel() { MouseFilter = MouseFilterEnum.Pass; TooltipText = " "; }
    public override GodotObject _MakeCustomTooltip(string forText) => Tip?.Invoke() ?? null!;
}
