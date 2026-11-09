using System.Text.Json.Nodes;
using Godot;
using Lightlag.Sim.Save;

namespace Lightlag.Game;

/// <summary>Title screen: Continue, new test game, battle sandbox, folders, and the build version.</summary>
public partial class TitleScreen : Control
{
    public override void _Ready()
    {
        var main = Main.Instance;
        SetAnchorsPreset(LayoutPreset.FullRect);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 14);
        col.AddChild(Ui.Label("LIGHTLAG", 64, Ui.Player));
        col.AddChild(Ui.Label("Milestones 2 and 3: fleets fly, first battles", 16, Ui.Dim));
        col.AddChild(new Control { CustomMinimumSize = new Vector2(0, 24) });

        var latest = SaveSlots.Latest(main.Paths);
        var cont = Ui.Button(latest != null ? $"Continue  ({System.IO.Path.GetFileNameWithoutExtension(latest)})" : "Continue",
            () => main.LoadGame(latest!));
        cont.Disabled = latest == null;
        col.AddChild(cont);
        col.AddChild(Ui.Button("New test game", () => main.StartNewGame()));
        col.AddChild(Ui.Button("Battle sandbox", () => main.ShowSandboxSetup()));
        col.AddChild(Ui.Button("Open reports folder", () => OS.ShellOpen(main.Paths.Root), 16));
        col.AddChild(Ui.Button("Quit", () => main.Quit(), 16));
        col.AddChild(new Control { CustomMinimumSize = new Vector2(0, 16) });
        col.AddChild(Ui.Label(NightlyLine(main), 14, Ui.Dim));

        var center = Ui.Centered(col);
        AddChild(center);

        var version = Ui.Label($"Build {main.Build}   ·   F8 report a problem", 14, Ui.Dim);
        version.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight, LayoutPresetMode.KeepSize, 16);
        version.GrowHorizontal = GrowDirection.Begin;
        version.GrowVertical = GrowDirection.Begin;
        AddChild(version);
    }

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
