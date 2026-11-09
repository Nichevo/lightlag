using Godot;

namespace Lightlag.Game;

/// <summary>
/// The F8 report box. The player types what they saw; the game saves it with a screenshot, the save,
/// the replay and the recent log into the reports folder. Ctrl+Enter sends, Esc cancels.
/// </summary>
public partial class ReportDialog : Control
{
    /// <summary>The note, or null if cancelled.</summary>
    public event Action<string?>? Finished;

    private TextEdit _note = null!;
    private bool _done;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.5f), AnchorRight = 1, AnchorBottom = 1 });

        var col = new VBoxContainer { CustomMinimumSize = new Vector2(640, 0) };
        col.AddThemeConstantOverride("separation", 12);
        col.AddChild(Ui.Label("Report a problem (F8)", 24, Ui.Player));
        col.AddChild(Ui.Label("What happened, and what did you expect? A screenshot, the save, the replay and the log are attached for you.", 15, Ui.Dim));
        _note = new TextEdit { CustomMinimumSize = new Vector2(640, 180), WrapMode = TextEdit.LineWrappingMode.Boundary };
        col.AddChild(_note);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 12);
        var cancel = new Button { Text = "Cancel (Esc)" };
        cancel.Pressed += () => Finish(null);
        var send = new Button { Text = "Save report (Ctrl+Enter)" };
        send.Pressed += () => Finish(_note.Text);
        row.AddChild(cancel);
        row.AddChild(send);
        col.AddChild(row);
        AddChild(Ui.Centered(Ui.Panel(col)));
        _note.GrabFocus();
    }

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key) return;
        if (key.Keycode == Key.Escape) { Finish(null); GetViewport().SetInputAsHandled(); }
        else if (key.Keycode == Key.Enter && key.CtrlPressed) { Finish(_note.Text); GetViewport().SetInputAsHandled(); }
    }

    private void Finish(string? note)
    {
        if (_done) return;
        _done = true;
        Finished?.Invoke(note);
    }
}

/// <summary>Shown after a crash: says a report was saved and where.</summary>
public partial class CrashPanel : Control
{
    private readonly string _folder;
    private readonly string _message;

    public event Action? BackToTitle;

    public CrashPanel() : this("", "") { }

    public CrashPanel(string folder, string message)
    {
        _folder = folder;
        _message = message;
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.6f), AnchorRight = 1, AnchorBottom = 1 });
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(640, 0) };
        col.AddThemeConstantOverride("separation", 12);
        col.AddChild(Ui.Label("Something went wrong", 26, Ui.Danger));
        var msg = Ui.Label(_message, 15);
        msg.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        col.AddChild(msg);
        col.AddChild(Ui.Label("A crash report with a save and a replay was written to:", 15, Ui.Dim));
        var path = Ui.Label(_folder, 13, Ui.Dim);
        path.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
        col.AddChild(path);
        var open = new Button { Text = "Open report folder" };
        open.Pressed += () => OS.ShellOpen(_folder);
        var back = new Button { Text = "Back to title" };
        back.Pressed += () => BackToTitle?.Invoke();
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        row.AddChild(open);
        row.AddChild(back);
        col.AddChild(row);
        AddChild(Ui.Centered(Ui.Panel(col)));
    }
}
