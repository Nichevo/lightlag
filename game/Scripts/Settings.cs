using Godot;

namespace Lightlag.Game;

/// <summary>
/// Settings for this computer, not for one game: display and graphics, interface (detail, top bar, Needs you),
/// pauses, and advanced (the Ollama rival advisor, the reports folder). The same page opens from the title screen
/// and from the game menu (Esc). Each part keeps its own file in the data folder.
/// </summary>
public partial class SettingsPage : CenterContainer
{
    public const string DisplayTab = "display", InterfaceTab = "interface", PausesTab = "pauses", AdvancedTab = "advanced";

    private static readonly (string Id, string Name)[] Tabs =
    {
        (DisplayTab, "Display and graphics"), (InterfaceTab, "Interface"), (PausesTab, "Pauses"), (AdvancedTab, "Advanced"),
    };

    private readonly Action _back;
    private string _tab = DisplayTab;
    private VBoxContainer _nav = null!;
    private VBoxContainer _body = null!;

    public SettingsPage() : this(() => { }) { }
    public SettingsPage(Action back) => _back = back;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 14);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 16);
        head.AddChild(Ui.Label("Settings", 26, Ui.Player));
        var note = Ui.Label("For this computer, not for one game.", 14, Ui.Dim);
        note.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(note);
        col.AddChild(head);

        var both = new HBoxContainer();
        both.AddThemeConstantOverride("separation", 20);
        _nav = new VBoxContainer { CustomMinimumSize = new Vector2(220, 0) };
        _nav.AddThemeConstantOverride("separation", 6);
        both.AddChild(_nav);
        _body = new VBoxContainer { CustomMinimumSize = new Vector2(760, 0) };
        _body.AddThemeConstantOverride("separation", 10);
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(780, 520), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        scroll.AddChild(_body);
        both.AddChild(scroll);
        col.AddChild(both);

        var back = Ui.Button("Back", _back, 16);
        back.CustomMinimumSize = new Vector2(200, 40);
        back.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        col.AddChild(back);
        AddChild(Ui.Panel(col, 24));
        Refresh();
    }

    /// <summary>Show one tab (DisplayTab, InterfaceTab, PausesTab or AdvancedTab).</summary>
    public void ShowTab(string tab)
    {
        _tab = Tabs.Any(t => t.Id == tab) ? tab : DisplayTab;
        if (IsNodeReady()) Refresh();
    }

    public void Refresh()
    {
        Ui.ClearChildren(_nav);
        foreach (var (id, name) in Tabs)
        {
            var tab = id;
            var b = Ui.Button(name, () => ShowTab(tab), 16);
            b.CustomMinimumSize = new Vector2(220, 44);
            b.Alignment = HorizontalAlignment.Left;
            b.Modulate = id == _tab ? Ui.Player : new Color(1, 1, 1, 0.75f);
            _nav.AddChild(b);
        }

        Ui.ClearChildren(_body);
        _body.AddChild(Ui.Label(Tabs.First(t => t.Id == _tab).Name, 20, Ui.Text));
        switch (_tab)
        {
            case InterfaceTab:
                _body.AddChild(PlayerInfo.Picker());
                Heading("Top bar");
                Choice("Menu bar", new[] { "Always shown", "Hide when idle", "Off (keys only)" }, (int)PlayerInfo.MenuBar,
                    i => PlayerInfo.SetMenuBar((MenuBarMode)i), "Hide when idle slides it away after 5 s; move the mouse to the bottom edge of row one, or press any screen key, to bring it back");
                Choice("Icons in the menu bar", new[] { "On", "Off" }, PlayerInfo.MenuIcons ? 0 : 1, i => PlayerInfo.SetMenuIcons(i == 0),
                    "Falls back to Off on its own when the window is narrower than about 1300 px");
                Choice("Strength bar", new[] { "Always shown" }, 0, _ => { }, "The control share (yours against the rival's estimate) stays on screen, even in Battle view");
                Heading("Needs you");
                Choice("Needs you tray", new[] { "Opens on the badge", "Off (keys only)" }, (int)PlayerInfo.Tray,
                    i => PlayerInfo.SetTray((TrayMode)i), "It never opens by itself: a new urgent item flashes the badge until you look. Off still leaves the badge count and the \".\" key (the badge then opens the Needs you list)");
                Choice("Pins on the map", new[] { "On", "Urgent and soon", "Off" }, PlayerInfo.PinMode, PlayerInfo.SetPinMode,
                    "Also a tick in Map layers, so you can flick it without opening settings");
                Choice("Advice from automation", new[] { "In the tray, folded", "Its own list", "Off" }, (int)PlayerInfo.Advice,
                    i => PlayerInfo.SetAdvice((AdviceMode)i), "Off means only trouble reaches you; what automation does is still listed under Automation handled");
                Choice("Pause while you work through", new[] { "On", "Off" }, PlayerInfo.PauseWhileWorking ? 0 : 1, i => PlayerInfo.SetPauseWhileWorking(i == 0),
                    "Applies to Work through them (the one-at-a-time cards), not the tray");
                break;
            case PausesTab:
                _body.AddChild(new PauseSettingsPage(null));
                break;
            case AdvancedTab:
                _body.AddChild(RivalAdvisor.Picker());
                Heading("Reports and test runs");
                var main = Main.Instance;
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 12);
                row.AddChild(Ui.SmallButton("Open reports folder", () => OS.ShellOpen(main.Paths.Root),
                    "Saves, logs, F8 reports and overnight run summaries"));
                var nightly = Ui.Label(TitleScreen.NightlyLine(main), 14, Ui.Dim);
                nightly.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                row.AddChild(nightly);
                _body.AddChild(row);
                break;
            default:
                _body.AddChild(Graphics.Picker(this));
                _body.AddChild(Display.Picker());
                var tip = Ui.Label("F11 or Alt+Enter switches between a window and full screen at any time.", 13, Ui.Dim);
                _body.AddChild(tip);
                break;
        }
    }

    private void Heading(string text)
    {
        var l = Ui.Label(text, 17, Ui.Player);
        _body.AddChild(l);
    }

    private void Choice(string name, string[] options, int selected, Action<int> pick, string tip)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var label = Ui.Label(name, 15, Ui.Text);
        label.CustomMinimumSize = new Vector2(230, 0);
        label.TooltipText = tip;
        label.MouseFilter = MouseFilterEnum.Pass;
        row.AddChild(label);
        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            var b = Ui.SmallButton(options[i], () => { pick(index); Refresh(); }, tip);
            b.AddThemeFontSizeOverride("font_size", 14);
            b.Modulate = i == selected ? Ui.Player : new Color(1, 1, 1, 0.6f);
            row.AddChild(b);
        }
        _body.AddChild(row);
    }
}
