using System.Text.Json.Nodes;
using Godot;
using Lightlag.Sim.Core;
using Lightlag.Sim.Save;

namespace Lightlag.Game;

/// <summary>
/// Title screen: Continue, a quick New game box (your last set-up read back, Start, and the way into the New game
/// screen), Load game, Battle sandbox, then Settings, How to play and Quit. Machine settings live on the Settings
/// page, game rules on the New game screen.
/// </summary>
public partial class TitleScreen : Control
{
    /// <summary>Open straight on a page instead of the menu: "newgame" or "settings" (after a graphics change rebuilds the screen).</summary>
    public string? StartPage { get; set; }

    private Control _menu = null!;
    private SavesList _saves = null!;
    private HowToPlay _help = null!;
    private NewGameScreen? _newGame;
    private SettingsPage _settings = null!;
    private Label _summary = null!;
    private Control _tools = null!;
    private readonly List<(Difficulty Level, Button Button)> _levels = new();

    private void ShowPage(Control page)
    {
        // The New game screen is rebuilt each time it opens, so it starts from the current choices.
        if (_newGame != null && page != _newGame) { _newGame.QueueFree(); _newGame = null; }
        _menu.Visible = page == _menu;
        _saves.Visible = page == _saves;
        _help.Visible = page == _help;
        _settings.Visible = page == _settings;
        _tools.Visible = page == _menu;
        if (page == _saves) _saves.Refresh();
        if (page == _menu) ReadBack();
    }

    private void OpenNewGame()
    {
        _newGame?.QueueFree();
        _newGame = new NewGameScreen(() => ShowPage(_menu));
        AddChild(_newGame);
        _menu.Visible = _saves.Visible = _help.Visible = _settings.Visible = _tools.Visible = false;
    }

    private void OpenSettings()
    {
        _settings.ShowTab(SettingsPage.DisplayTab);
        ShowPage(_settings);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        // Esc on a page goes back to the menu (the New game screen handles its own Esc, keeping the choices).
        if (!e.IsActionPressed("ui_cancel") || _menu.Visible || _newGame != null) return;
        GetViewport().SetInputAsHandled();
        ShowPage(_menu);
    }

    /// <summary>A race's play style, strengths and weaknesses in a few lines.</summary>
    public static string About(string id)
    {
        if (id == Races.Random) return "Random: you find out which race you lead when the game starts.";
        var r = Races.Get(id);
        var text = $"{r.Name}: {r.Style}";
        foreach (var p in r.Pros ?? new()) text += $"\n+ {p}";
        foreach (var c in r.Cons ?? new()) text += $"\n- {c}";
        return text;
    }

    private static Button MenuButton(string text, Action pressed, int size, float height, string? tip = null)
    {
        var b = Ui.Button(text, pressed, size);
        b.CustomMinimumSize = new Vector2(0, height);
        b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (tip != null) b.TooltipText = tip;
        return b;
    }

    private static HBoxContainer Row(params Control[] items)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        foreach (var c in items) row.AddChild(c);
        return row;
    }

    /// <summary>The quick New game box: Easy / Normal, the last set-up in two lines, Start and Change race and rules.</summary>
    private Control NewGameBox(Main main)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        var name = Ui.Label("New game", 20, Ui.Text);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(name);
        foreach (var level in Enum.GetValues<Difficulty>())
        {
            var l = level;
            var b = Ui.SmallButton($"{level} rival", () => { main.DifficultyChoice = l; ReadBack(); },
                l == Difficulty.Easy ? "Rivals that build and fight less sharply: for learning the game" : "Rivals that play by your rules at full strength");
            head.AddChild(b);
            _levels.Add((l, b));
        }
        col.AddChild(head);
        _summary = Ui.Label("", 14, Ui.Text);
        _summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _summary.CustomMinimumSize = new Vector2(540, 0);
        col.AddChild(_summary);
        var start = MenuButton("Start", () => main.StartChosenGame(), 18, 48, "Start a new game with this set-up");
        start.Modulate = Ui.Player;
        var change = MenuButton("Change race and rules…", OpenNewGame, 15, 48, "Your race, the rivals and the rules, with Relaxed / Standard / Full presets");
        change.SizeFlagsHorizontal = SizeFlags.Fill;
        change.CustomMinimumSize = new Vector2(230, 48);
        col.AddChild(Row(start, change));
        return Ui.Panel(col, 16);
    }

    /// <summary>Read the current set-up back into the New game box.</summary>
    private void ReadBack()
    {
        var main = Main.Instance;
        _summary.Text = NewGameChoices.Summary(main);
        foreach (var (l, b) in _levels) b.Modulate = l == main.DifficultyChoice ? Ui.Player : new Color(1, 1, 1, 0.6f);
    }

    public override void _Ready()
    {
        var main = Main.Instance;
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new TitleBackdrop { Name = "Backdrop" });
        Graphics.Changed += Rebuild;

        var col = new VBoxContainer { CustomMinimumSize = new Vector2(580, 0) };
        col.AddThemeConstantOverride("separation", 12);
        col.AddChild(Ui.Label("LIGHTLAG", 72, Ui.Player));
        col.AddChild(Ui.Label("One star system. Every order travels at the speed of light.", 17, Ui.Dim));
        col.AddChild(new Control { CustomMinimumSize = new Vector2(0, 16) });

        var latest = SaveSlots.Latest(main.Paths);
        var cont = MenuButton(latest != null ? $"Continue  ·  {System.IO.Path.GetFileNameWithoutExtension(latest)}" : "Continue", () => main.LoadGame(latest!), 20, 56,
            latest != null ? $"Load the newest save ({File.GetLastWriteTime(latest):ddd d MMM, HH:mm})" : "No saves yet");
        cont.Disabled = latest == null;
        col.AddChild(cont);
        col.AddChild(NewGameBox(main));
        col.AddChild(Row(MenuButton("Load game", () => ShowPage(_saves), 17, 46), MenuButton("Battle sandbox", () => main.ShowSandboxSetup(), 17, 46)));
        col.AddChild(Row(MenuButton("Settings", OpenSettings, 15, 42, "Display, graphics, interface, pauses and the rival advisor: for this computer"),
            MenuButton("How to play", () => ShowPage(_help), 15, 42), MenuButton("Quit", () => main.Quit(), 15, 42)));

        // The menu sits left of centre, over the drifting system.
        var place = new MarginContainer();
        place.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        place.AddThemeConstantOverride("margin_left", 120);
        place.AddThemeConstantOverride("margin_bottom", 40);
        var middle = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        middle.AddChild(col);
        col.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        place.AddChild(middle);
        AddChild(place);
        _menu = place;

        _saves = new SavesList(() => ShowPage(_menu)) { Visible = false };
        AddChild(_saves);
        _help = new HowToPlay(() => ShowPage(_menu)) { Visible = false };
        AddChild(_help);
        _settings = new SettingsPage(() => ShowPage(_menu)) { Visible = false };
        AddChild(_settings);

        // Small things for test runs, bottom left; the build and F8, bottom right.
        var tools = new HBoxContainer();
        tools.AddThemeConstantOverride("separation", 14);
        var nightly = Ui.Label(NightlyLine(main), 13, Ui.Dim);
        nightly.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        tools.AddChild(nightly);
        var folder = Ui.SmallButton("Open reports folder", () => OS.ShellOpen(main.Paths.Root), "Saves, logs, F8 reports and overnight run summaries");
        folder.AddThemeFontSizeOverride("font_size", 13);
        folder.CustomMinimumSize = new Vector2(0, 28);
        tools.AddChild(folder);
        tools.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft, LayoutPresetMode.KeepSize, 16);
        tools.GrowVertical = GrowDirection.Begin;
        AddChild(tools);
        _tools = tools;

        var version = Ui.Label($"Build {main.Build}   ·   F8 report a problem", 14, Ui.Dim);
        version.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight, LayoutPresetMode.KeepSize, 16);
        version.GrowHorizontal = GrowDirection.Begin;
        version.GrowVertical = GrowDirection.Begin;
        AddChild(version);

        ReadBack();
        if (StartPage == "newgame") OpenNewGame();
        else if (StartPage == "settings") ShowPage(_settings);
    }

    public override void _ExitTree() => Graphics.Changed -= Rebuild;

    /// <summary>A new graphics setting: rebuild the title so the backdrop follows it, back on the page that was open.</summary>
    private void Rebuild() => Callable.From(() =>
    {
        if (IsInsideTree()) Main.Instance.ShowTitle(_settings.Visible ? "settings" : _newGame != null ? "newgame" : null);
    }).CallDeferred();

    /// <summary>One line about the last overnight run, from nightly/latest-summary.json.</summary>
    public static string NightlyLine(Main main)
    {
        try
        {
            var path = System.IO.Path.Combine(main.Paths.Nightly, "latest-summary.json");
            if (!File.Exists(path)) return "No overnight runs yet.";
            var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var games = o["games"]!.GetValue<int>();
            var crashed = o["crashed"]!.GetValue<int>();
            var when = DateTime.Parse(o["finished"]!.GetValue<string>()).ToLocalTime();
            return crashed == 0
                ? $"Last overnight run ({when:ddd d MMM}): {games} games, all clean."
                : $"Last overnight run ({when:ddd d MMM}): {crashed} of {games} games failed. See nightly/latest-summary.md.";
        }
        catch (Exception ex)
        {
            main.Log.Warn("ui", "Could not read the overnight summary", new { error = ex.Message });
            return "Could not read the overnight summary.";
        }
    }
}
