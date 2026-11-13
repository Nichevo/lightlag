using Godot;
using Lightlag.Sim;

namespace Lightlag.Game;

/// <summary>
/// The per-event pause settings (Lightlag.Sim.PauseRules), kept in pause.cfg in the data folder: for each thing that
/// can happen, Pause, Slow to 1x, Pop-up or News only, with presets. An interface setting, the same for every game.
/// </summary>
public static class PauseSettings
{
    private static string? _file;
    public static PauseRules Rules { get; private set; } = new();

    public static void Load(string dataDir)
    {
        _file = System.IO.Path.Combine(dataDir, "pause.cfg");
        try
        {
            if (File.Exists(_file)) Rules = PauseRules.Parse(File.ReadAllText(_file));
        }
        catch (Exception ex)
        {
            Main.Instance.Log.Warn("ui", "Could not read the pause settings", new { error = ex.Message });
        }
    }

    public static void Set(PauseEvent e, PauseResponse r)
    {
        Rules[e] = r;
        Save();
    }

    public static void ApplyPreset(string preset)
    {
        Rules.Apply(preset);
        Save();
    }

    private static void Save()
    {
        if (_file == null) return;
        try
        {
            File.WriteAllText(_file, Rules.ToText());
        }
        catch (Exception ex)
        {
            Main.Instance.Log.Warn("ui", "Could not save the pause settings", new { error = ex.Message });
        }
    }
}

/// <summary>The pause settings page of the game menu: a preset row, then one row per event with its response.</summary>
public partial class PauseSettingsPage : CenterContainer
{
    private readonly Action _back;
    private VBoxContainer _rows = null!;
    private Label _preset = null!;

    public PauseSettingsPage() : this(() => { }) { }
    public PauseSettingsPage(Action back) => _back = back;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) };
        col.AddThemeConstantOverride("separation", 8);
        col.AddChild(Ui.Label("Pause settings", 26, Ui.Player));
        var tip = Ui.Label("What the game does when each of these happens. The game's end always pauses. Contacts a role fleet has in hand, "
                           + "and the same fleets meeting again soon after, never pause (Fleets screen, K).", 13, Ui.Dim);
        tip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        tip.CustomMinimumSize = new Vector2(620, 0);
        col.AddChild(tip);
        var presets = new HBoxContainer();
        presets.AddThemeConstantOverride("separation", 8);
        presets.AddChild(Ui.Label("Presets:", 15, Ui.Dim));
        foreach (var p in PauseRules.Presets)
        {
            var name = p;
            presets.AddChild(Ui.SmallButton(name, () => { PauseSettings.ApplyPreset(name); Refresh(); }, PresetTip(name)));
        }
        _preset = Ui.Label("", 14, Ui.Text);
        presets.AddChild(_preset);
        col.AddChild(presets);
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 4);
        col.AddChild(_rows);
        col.AddChild(Ui.Button("Back", _back, 16));
        AddChild(Ui.Panel(col, 20));
        Refresh();
    }

    private static string PresetTip(string preset) => preset switch
    {
        PauseRules.Watchful => "Pause for nearly everything; slow down for supply warnings and planets changing hands",
        PauseRules.HandsOff => "Pause only for battles, calls and the shipyard; slow down for sightings and claims; news only for research and launches",
        _ => "How the game has always behaved: pause for sightings, battles, claims, the shipyard and calls; pop-ups for the rest",
    };

    public void Refresh()
    {
        foreach (var c in _rows.GetChildren()) c.QueueFree();
        _preset.Text = PauseSettings.Rules.Preset is string p ? $"(now: {p})" : "(now: your own)";
        foreach (var e in Enum.GetValues<PauseEvent>())
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var name = Ui.Label(PauseRules.Name(e), 15, Ui.Text);
            name.CustomMinimumSize = new Vector2(360, 0);
            row.AddChild(name);
            var pick = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(180, 0) };
            foreach (var r in Enum.GetValues<PauseResponse>()) pick.AddItem(PauseRules.Name(r), (int)r);
            pick.Selected = (int)PauseSettings.Rules[e];
            var ev = e;
            pick.ItemSelected += i =>
            {
                PauseSettings.Set(ev, (PauseResponse)(int)i);
                _preset.Text = PauseSettings.Rules.Preset is string now ? $"(now: {now})" : "(now: your own)";
            };
            row.AddChild(pick);
            _rows.AddChild(row);
        }
    }
}
