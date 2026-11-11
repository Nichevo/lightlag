using System.Text.Json.Nodes;
using Godot;
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
        Box("People everywhere", main.SpreadPeopleChoice, v => main.SpreadPeopleChoice = v,
            "Planets start with a few independent people instead of empty: hold orbit to win them over");
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
        col.AddChild(Ui.Button("Load game", () => ShowPage(_saves), 16));
        col.AddChild(Ui.Button("How to play", () => ShowPage(_help), 16));
        col.AddChild(Ui.Button("Battle sandbox", () => main.ShowSandboxSetup()));
        col.AddChild(Ui.Button("Open reports folder", () => OS.ShellOpen(main.Paths.Root), 16));
        col.AddChild(Ui.Button("Quit", () => main.Quit(), 16));
        col.AddChild(new Control { CustomMinimumSize = new Vector2(0, 16) });
        col.AddChild(Ui.Label(NightlyLine(main), 14, Ui.Dim));

        var center = Ui.Centered(col);
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
