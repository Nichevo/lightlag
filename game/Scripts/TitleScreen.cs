using System.Text.Json.Nodes;
using Godot;
using Lightlag.Sim.Core;
using Lightlag.Sim.Save;

namespace Lightlag.Game;

/// <summary>Title screen: Continue, new test game, battle sandbox, folders, and the build version.</summary>
public partial class TitleScreen : Control
{
    private Control _menu = null!;
    private SavesList _saves = null!;
    private HowToPlay _help = null!;

    private void ShowPage(Control page)
    {
        _menu.Visible = page == _menu;
        _saves.Visible = page == _saves;
        _help.Visible = page == _help;
        if (page == _saves) _saves.Refresh();
    }

    /// <summary>Light lag for the next new game: real light speed (the test default), ten times slower light, or off.</summary>
    private static Control LightPicker(Main main)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(Ui.Label("Light lag:", 15, Ui.Dim));
        var pick = new OptionButton { TooltipText = "How long news and orders take to cross the system. Real light: Tamber is about 45 minutes from Verdant." };
        pick.AddItem("real light speed");
        pick.AddItem("light 10x slower (test)");
        pick.AddItem("off");
        pick.Selected = !main.LightChoice.On ? 2 : main.LightChoice.SpeedFactor < 0.5 ? 1 : 0;
        pick.ItemSelected += i => main.LightChoice = i switch
        {
            1 => new Lightlag.Sim.Core.LightSetup(true, 0.1),
            2 => new Lightlag.Sim.Core.LightSetup(false, 1.0),
            _ => new Lightlag.Sim.Core.LightSetup(true, 1.0),
        };
        row.AddChild(pick);
        var decap = new CheckBox { Text = "Decapitation", ButtonPressed = main.DecapitationChoice, FocusMode = FocusModeEnum.None,
            TooltipText = "With light lag on: lose your flagship with no destroyer left to take command and you lose the game (the rival too)" };
        decap.Toggled += on => main.DecapitationChoice = on;
        row.AddChild(decap);
        return row;
    }

    /// <summary>
    /// Races for the next new game: a portrait card for each race you can play (and Random), the rival's race,
    /// and what the picked race is good and bad at.
    /// </summary>
    private static Control RacePicker(Main main)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        col.AddChild(Ui.Label("Your race", 15, Ui.Dim));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        col.AddChild(row);
        var about = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(430, 0) };
        about.AddThemeFontSizeOverride("font_size", 13);
        about.AddThemeColorOverride("font_color", Ui.Text);
        void Describe(string id) => about.Text = About(id);
        var group = new ButtonGroup();
        foreach (var (id, name) in Races.Choices())
        {
            var random = id == Races.Random;
            var card = new Button
            {
                Text = name, ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.None,
                Icon = random ? null : Art.Texture("Races", id, 0.42f),
                IconAlignment = HorizontalAlignment.Center, VerticalIconAlignment = VerticalAlignment.Top,
                CustomMinimumSize = new Vector2(random ? 110 : 136, 196), ButtonPressed = main.PlayerRaceChoice == id,
                TooltipText = random ? "A race picked at random when the game starts" : Races.Get(id).Blurb,
            };
            card.AddThemeFontSizeOverride("font_size", 14);
            // The picked race has a bright frame.
            var picked = new StyleBoxFlat { BgColor = new Color(0.1f, 0.16f, 0.24f, 0.95f), BorderColor = Ui.Player };
            picked.SetBorderWidthAll(2);
            picked.SetCornerRadiusAll(4);
            picked.SetContentMarginAll(4);
            card.AddThemeStyleboxOverride("pressed", picked);
            card.AddThemeStyleboxOverride("hover_pressed", picked);
            card.Pressed += () => { main.PlayerRaceChoice = id; Describe(id); };
            row.AddChild(card);
        }
        var rivalRow = new HBoxContainer();
        rivalRow.AddThemeConstantOverride("separation", 8);
        rivalRow.AddChild(Ui.Label("Rival's race:", 15, Ui.Dim));
        var rival = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "The rival's race. Random: rolled when the game starts; the Intelligence screen (I) says which it is" };
        var choices = Races.Choices().ToList();
        foreach (var (_, name) in choices) rival.AddItem(name);
        rival.Selected = Math.Max(0, choices.FindIndex(c => c.Id == main.RivalRaceChoice));
        rival.ItemSelected += i => main.RivalRaceChoice = choices[(int)i].Id;
        rivalRow.AddChild(rival);
        col.AddChild(rivalRow);
        Describe(main.PlayerRaceChoice);
        col.AddChild(about);
        return col;
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

    /// <summary>The optional systems for the next new game.</summary>
    private static Control SystemsPicker(Main main)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        row.AddChild(Ui.Label("Systems:", 15, Ui.Dim));
        CheckBox Box(string text, bool on, Action<bool> set, string tip)
        {
            var box = new CheckBox { Text = text, ButtonPressed = on, FocusMode = FocusModeEnum.None, TooltipText = tip };
            box.Toggled += v => set(v);
            row.AddChild(box);
            return box;
        }
        Box("Random homes", main.RandomStartsChoice, v => main.RandomStartsChoice = v, "You and the rival start on planets picked at random");
        Box("Happiness", main.HappinessChoice, v => main.HappinessChoice = v,
            "Happiness moves with food, blockades and bombing, and blockaded planets give up when unhappy. Off: only starving planets give up");
        Box("Supply lines", main.SupplyLinesChoice, v => main.SupplyLinesChoice = v, "Ammunition and food run down away from home. Off: ships keep full stocks (fuel still counts)");
        Box("Fuel types", main.FuelTypesChoice, v => main.FuelTypesChoice = v,
            "Each engine burns its own fuel (water, hydrogen + oxygen, fission pellets, deuterium, helium-3, antimatter), each with its own thrust, reach, heat and signature. Off: one fuel for every engine");
        Box("People everywhere", main.SpreadPeopleChoice, v => main.SpreadPeopleChoice = v,
            "Planets start with a few independent people instead of empty: hold orbit to win them over");
        Box("Hazards", main.HazardsChoice, v => main.HazardsChoice = v,
            "Rocks in belts, rings and battle debris hit ships at speed (big ships most), radiation belts strain crews, ships run hot close to the sun. Off: space is empty");
        row.AddChild(Ui.Label("Events:", 15, Ui.Dim));
        var events = new OptionButton { FocusMode = FocusModeEnum.None,
            TooltipText = "How often events come (flares, finds, reactor faults, rock strikes, defectors, comets): the same for the rival. Each has an Auto pick" };
        foreach (var r in Enum.GetValues<Lightlag.Sim.Core.EventRate>()) events.AddItem(r.ToString(), (int)r);
        events.Selected = (int)main.EventsChoice;
        events.ItemSelected += i => main.EventsChoice = (Lightlag.Sim.Core.EventRate)(int)i;
        row.AddChild(events);
        return row;
    }

    public override void _Ready()
    {
        var main = Main.Instance;
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new TitleBackdrop { Name = "Backdrop" });
        Graphics.Changed += Rebuild;

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 14);
        col.AddChild(Ui.Label("LIGHTLAG", 64, Ui.Player));
        col.AddChild(Ui.Label("First playable: one star system, you against a rival that plays by your rules", 16, Ui.Dim));
        col.AddChild(new Control { CustomMinimumSize = new Vector2(0, 24) });

        var latest = SaveSlots.Latest(main.Paths);
        var cont = Ui.Button(latest != null ? $"Continue  ({System.IO.Path.GetFileNameWithoutExtension(latest)})" : "Continue",
            () => main.LoadGame(latest!));
        cont.Disabled = latest == null;
        col.AddChild(cont);
        col.AddChild(Ui.Button("New game: Normal rival", () => main.StartNewGame()));
        col.AddChild(Ui.Button("New game: Easy rival", () => main.StartNewGame(difficulty: Lightlag.Sim.Core.Difficulty.Easy), 16));
        col.AddChild(LightPicker(main));
        col.AddChild(SystemsPicker(main));
        col.AddChild(Graphics.Picker(main));
        col.AddChild(Display.Picker());
        col.AddChild(PlayerInfo.Picker());
        col.AddChild(Ui.Button("Load game", () => ShowPage(_saves), 16));
        col.AddChild(Ui.Button("How to play", () => ShowPage(_help), 16));
        col.AddChild(Ui.Button("Battle sandbox", () => main.ShowSandboxSetup()));
        col.AddChild(Ui.Button("Open reports folder", () => OS.ShellOpen(main.Paths.Root), 16));
        col.AddChild(Ui.Button("Quit", () => main.Quit(), 16));
        col.AddChild(new Control { CustomMinimumSize = new Vector2(0, 16) });
        col.AddChild(Ui.Label(NightlyLine(main), 14, Ui.Dim));

        // The race picker sits beside the menu, so the menu keeps its height.
        var both = new HBoxContainer();
        both.AddThemeConstantOverride("separation", 48);
        both.AddChild(col);
        var races = RacePicker(main);
        races.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        both.AddChild(races);
        var center = Ui.Centered(both);
        AddChild(center);
        _menu = center;
        _saves = new SavesList(() => ShowPage(_menu)) { Visible = false };
        AddChild(_saves);
        _help = new HowToPlay(() => ShowPage(_menu)) { Visible = false };
        AddChild(_help);

        var version = Ui.Label($"Build {main.Build}   ·   F8 report a problem", 14, Ui.Dim);
        version.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight, LayoutPresetMode.KeepSize, 16);
        version.GrowHorizontal = GrowDirection.Begin;
        version.GrowVertical = GrowDirection.Begin;
        AddChild(version);
    }

    public override void _ExitTree() => Graphics.Changed -= Rebuild;

    /// <summary>A new graphics setting: rebuild the title so the backdrop follows it.</summary>
    private void Rebuild() => Callable.From(() => { if (IsInsideTree()) Main.Instance.ShowTitle(); }).CallDeferred();

    /// <summary>One line about the last overnight run, from nightly/latest-summary.json.</summary>
    private static string NightlyLine(Main main)
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
