using Godot;
using Lightlag.Sim.Core;
using Lightlag.Sim.Save;

namespace Lightlag.Game;

/// <summary>
/// The game menu (Esc): resume, save, load, how to play, and back to the title screen. The game is
/// paused while it is open.
/// </summary>
public partial class GameMenu : Control
{
    private readonly GameScreen _screen;
    private Control _main = null!;
    private SavesList _saves = null!;
    private HowToPlay _help = null!;

    public GameMenu() : this(null!) { }
    public GameMenu(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // A dim layer over the map; clicks on it do nothing.
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.55f), AnchorRight = 1, AnchorBottom = 1 });

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        col.AddChild(Ui.Label("Game menu", 26, Ui.Player));
        col.AddChild(Ui.Button("Resume  (Esc)", () => _screen.CloseMenu()));
        col.AddChild(Ui.Button("Save game", () => _screen.SaveManual()));
        col.AddChild(Ui.Button("Load game", () => Show(_saves)));
        col.AddChild(Ui.Button("How to play", () => Show(_help)));
        col.AddChild(Ui.Button("Quit to title screen", () => _screen.QuitToTitle()));
        col.AddChild(Ui.Label("Quitting saves an autosave first. F5 quick save, F9 quick load.", 13, Ui.Dim));
        _main = Ui.Centered(Ui.Panel(col, 24));
        AddChild(_main);

        _saves = new SavesList(() => Show(_main));
        _saves.Visible = false;
        AddChild(_saves);
        _help = new HowToPlay(() => Show(_main));
        _help.Visible = false;
        AddChild(_help);
    }

    /// <summary>Opens on the main page every time.</summary>
    public void Open()
    {
        Visible = true;
        Show(_main);
    }

    private void Show(Control page)
    {
        _main.Visible = page == _main;
        _saves.Visible = page == _saves;
        _help.Visible = page == _help;
        if (page == _saves) _saves.Refresh();
    }
}

/// <summary>Every save, newest first, with its game day; click one to load it.</summary>
public partial class SavesList : CenterContainer
{
    private readonly Action _back;
    private VBoxContainer _rows = null!;

    public SavesList() : this(() => { }) { }
    public SavesList(Action back) => _back = back;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
        col.AddThemeConstantOverride("separation", 8);
        col.AddChild(Ui.Label("Load game", 26, Ui.Player));
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 4);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(560, 420), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(_rows);
        _rows.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        col.AddChild(scroll);
        col.AddChild(Ui.Button("Back", _back, 16));
        AddChild(Ui.Panel(col, 20));
    }

    public void Refresh()
    {
        foreach (var c in _rows.GetChildren()) c.QueueFree();
        var saves = SaveSlots.List(Main.Instance.Paths);
        if (saves.Count == 0) _rows.AddChild(Ui.Label("No saves yet.", 15, Ui.Dim));
        foreach (var s in saves.Take(40))
        {
            var path = s.Path;
            var when = s.SavedUtc.ToLocalTime();
            var text = $"{s.Kind}  ·  {GameTime.Format((long)s.Hour)}" + (s.Rival != null ? $"  ·  {Format.Title(s.Rival)} rival" : "")
                       + (s.Over ? "  ·  game over" : "") + $"\n{when:ddd d MMM, HH:mm}";
            var b = new Button { Text = text, Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(520, 52) };
            b.AddThemeFontSizeOverride("font_size", 15);
            b.Pressed += () => Main.Instance.LoadGame(path);
            _rows.AddChild(b);
        }
    }
}

/// <summary>A one-page guide: what you are trying to do, the basics, and the keys.</summary>
public partial class HowToPlay : CenterContainer
{
    private readonly Action _back;

    public HowToPlay() : this(() => { }) { }
    public HowToPlay(Action back) => _back = back;

    public const string Text =
        "THE GOAL\n" +
        "Hold more than half of the planets and moons that have Industry for 30 days, or destroy the rival's shipyard by bombarding it from orbit. The Empire panel (E) shows the count.\n\n" +
        "THE BASICS\n" +
        "• Space pauses; 1-4 set the speed. The game pauses itself when enemies are spotted (P turns that off) and when fighting starts.\n" +
        "• Click a fleet to select it. Right-click a planet or a point to plan a route, then Enter to go. Shift+right-click goes at once.\n" +
        "• Planets with Industry can be claimed: park a fleet in orbit and keep the enemy away for 10 days (30 if the rival holds it).\n" +
        "• Industry pays for ships at your shipyard and for research (Empire panel, E).\n" +
        "• You only see what your ships and planets can see. Burning engines show from far away; coasting cold hides you. B cycles the sight view.\n" +
        "• Right-click an enemy fleet to attack it. V opens the engagement screen: approach, burn and dodge choices, rings and a timeline.\n" +
        "• Railguns hit hard up close, missiles reach further, point defence shoots missiles down.\n\n" +
        "KEYS\n" +
        "Wheel zoom · WASD or right-drag pan · Z zoom to the selection · H whole system · Tab next fleet · M merge or gather · " +
        "O orbit · G drift · X stop · C change route · R last combat report · F5 quick save · F9 quick load · F8 report a problem · Esc menu";

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 12);
        col.AddChild(Ui.Label("How to play", 26, Ui.Player));
        var body = Ui.Label(Text, 15);
        body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.CustomMinimumSize = new Vector2(720, 0);
        col.AddChild(body);
        col.AddChild(Ui.Button("Back", _back, 16));
        AddChild(Ui.Panel(col, 24));
    }
}
