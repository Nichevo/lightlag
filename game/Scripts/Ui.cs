using Godot;

namespace Lightlag.Game;

/// <summary>Shared colours and small helpers. Cyan is the player, amber the rival.</summary>
public static class Ui
{
    public static readonly Color Player = new("3fd0ff");
    public static readonly Color Rival = new("ffb347");
    public static readonly Color Text = new("d8e2f0");
    public static readonly Color Dim = new("7d8aa0");
    public static readonly Color Orbit = new(0.45f, 0.55f, 0.7f, 0.25f);
    public static readonly Color PanelBg = new(0.04f, 0.06f, 0.1f, 0.94f);
    public static readonly Color Danger = new("ff6b6b");

    public static Label Label(string text, int size = 16, Color? color = null)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color ?? Text);
        return l;
    }

    public static Button Button(string text, Action onPressed, int size = 20)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(320, 48) };
        b.AddThemeFontSizeOverride("font_size", size);
        b.Pressed += () => Main.Instance.RunGuarded($"button '{text}'", onPressed);
        return b;
    }

    public static PanelContainer Panel(Control content, int padding = 24)
    {
        var p = new PanelContainer();
        StylePanel(p, padding);
        p.AddChild(content);
        return p;
    }

    public static void StylePanel(PanelContainer p, int padding)
    {
        var box = new StyleBoxFlat { BgColor = PanelBg, BorderColor = new Color(Player, 0.4f) };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(6);
        box.SetContentMarginAll(padding);
        if (Graphics.Fancy)
        {
            // Medium and High: a faint glass edge (a lighter rim and a soft shadow) and a short fade when it opens.
            box.BgColor = new Color(PanelBg, 0.9f);
            box.BorderColor = new Color(Player.Lightened(0.35f), 0.45f);
            box.ShadowColor = new Color(0, 0, 0, 0.35f);
            box.ShadowSize = 8;
            FadeIn(p);
        }
        p.AddThemeStyleboxOverride("panel", box);
    }

    /// <summary>Fade a control in over a moment each time it is shown.</summary>
    public static void FadeIn(CanvasItem c)
    {
        c.VisibilityChanged += () =>
        {
            if (!c.Visible || !c.IsInsideTree()) return;
            c.Modulate = new Color(c.Modulate, 0);
            c.CreateTween().TweenProperty(c, "modulate:a", 1f, 0.14);
        };
    }

    /// <summary>A compact button for HUD bars and panels. Keyboard focus is off so keys keep driving the map.</summary>
    public static Button SmallButton(string text, Action pressed, string tip)
    {
        var b = new Button { Text = text, TooltipText = tip, CustomMinimumSize = new Vector2(44, 32), FocusMode = Control.FocusModeEnum.None };
        b.AddThemeFontSizeOverride("font_size", 15);
        b.Pressed += () => Main.Instance.RunGuarded($"button '{text}'", pressed);
        return b;
    }

    /// <summary>A full-screen layer that centres one child.</summary>
    public static CenterContainer Centered(Control child)
    {
        var c = new CenterContainer();
        c.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        c.AddChild(child);
        return c;
    }
}

/// <summary>A short message at the top of the screen that fades away. Click it to open the folder it mentions.</summary>
public partial class Toast : Control
{
    private readonly string _text;
    private readonly string? _folder;
    private readonly Action? _onClick;

    public Toast() : this("") { }

    public Toast(string text, string? folder = null, Action? onClick = null)
    {
        _text = text;
        _folder = folder;
        _onClick = onClick;
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Ready()
    {
        // Centred at the top of the window (worked out from the window, so it never slides off the left edge).
        Position = new Vector2(GetViewportRect().Size.X / 2, 0);
        var label = Ui.Label(_folder != null ? $"{_text}  (click to open)" : _onClick != null ? $"{_text}  (click to go there)" : _text, 18);
        var panel = Ui.Panel(label, 12);
        panel.Position = new Vector2(-250, 24);
        panel.CustomMinimumSize = new Vector2(500, 0);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        if (_folder != null)
            panel.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) OS.ShellOpen(_folder); };
        if (_onClick != null)
            panel.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { Pressed: true }) return;
                Main.Instance.RunGuarded("toast click", _onClick);
                QueueFree();
            };
        AddChild(panel);
        var tween = CreateTween();
        tween.TweenInterval(_onClick != null ? 8.0 : 4.0);
        tween.TweenProperty(this, "modulate:a", 0.0, 1.0);
        tween.TweenCallback(Callable.From(QueueFree));
    }
}
