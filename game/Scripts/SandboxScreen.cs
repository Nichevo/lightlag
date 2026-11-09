using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// Battle sandbox set-up: how many frigates and destroyers each side has, missile brains, the rival's
/// standing order and how far apart the fleets start. Start drops into a paused battle.
/// </summary>
public partial class SandboxScreen : Control
{
    private SpinBox _pf = null!, _pd = null!, _rf = null!, _rd = null!, _pb = null!, _rb = null!, _dist = null!;
    private OptionButton _rivalStance = null!;

    public override void _Ready()
    {
        var main = Main.Instance;
        var last = main.LastSandbox;
        SetAnchorsPreset(LayoutPreset.FullRect);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        col.AddChild(Ui.Label("BATTLE SANDBOX", 40, Ui.Player));
        col.AddChild(Ui.Label("Set up two fleets in open space and fight. Try different mixes and approaches.", 15, Ui.Dim));
        col.AddChild(new Control { CustomMinimumSize = new Vector2(0, 12) });

        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 24);
        grid.AddThemeConstantOverride("v_separation", 8);
        grid.AddChild(Ui.Label("", 16));
        grid.AddChild(Ui.Label("You", 18, Ui.Player));
        grid.AddChild(Ui.Label("Rival", 18, Ui.Rival));
        grid.AddChild(Ui.Label("Gun frigates", 16));
        grid.AddChild(_pf = Spin(last.PlayerFrigates, 0, 12));
        grid.AddChild(_rf = Spin(last.RivalFrigates, 0, 12));
        grid.AddChild(Ui.Label("Missile destroyers", 16));
        grid.AddChild(_pd = Spin(last.PlayerDestroyers, 0, 12));
        grid.AddChild(_rd = Spin(last.RivalDestroyers, 0, 12));
        grid.AddChild(Ui.Label("Missile brain (1 or 2)", 16));
        grid.AddChild(_pb = Spin(last.PlayerBrain, 1, 2));
        grid.AddChild(_rb = Spin(last.RivalBrain, 1, 2));
        grid.AddChild(Ui.Label("Rival standing order", 16));
        grid.AddChild(Ui.Label("you choose in game", 14, Ui.Dim));
        _rivalStance = new OptionButton();
        _rivalStance.AddItem("Hold (waits)", 0);
        _rivalStance.AddItem("Engage (comes at you)", 1);
        _rivalStance.Selected = last.RivalStance == Stance.Engage ? 1 : 0;
        grid.AddChild(_rivalStance);
        grid.AddChild(Ui.Label("Start apart (thousand km)", 16));
        grid.AddChild(_dist = Spin(last.DistanceMm, 20, 2000, 10));
        grid.AddChild(Ui.Label("contact at 100", 14, Ui.Dim));
        col.AddChild(grid);

        col.AddChild(new Control { CustomMinimumSize = new Vector2(0, 12) });
        col.AddChild(Ui.Button("Start battle", Start));
        col.AddChild(Ui.Button("Back", () => main.ShowTitle(), 16));
        col.AddChild(Ui.Label("In the battle: F6 restarts it, Esc comes back here.", 14, Ui.Dim));
        AddChild(Ui.Centered(col));
    }

    private static SpinBox Spin(double value, double min, double max, double step = 1)
    {
        var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(120, 0) };
        return s;
    }

    private void Start()
    {
        var setup = new SandboxSetup(
            (int)_pf.Value, (int)_pd.Value, (int)_rf.Value, (int)_rd.Value, (int)_pb.Value, (int)_rb.Value,
            Stance.Engage, _rivalStance.Selected == 1 ? Stance.Engage : Stance.Hold, _dist.Value);
        if (setup.PlayerFrigates + setup.PlayerDestroyers == 0 || setup.RivalFrigates + setup.RivalDestroyers == 0)
        {
            Main.Instance.Toast("Both sides need at least one ship");
            return;
        }
        Main.Instance.StartSandbox(setup);
    }
}
