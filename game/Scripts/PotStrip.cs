using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The budget line for one pot, shown on the screen of the area it pays for (research, planets, shipyard,
/// stations, refits, transport): its share and Industry a day, what it has saved, and the same line the
/// Budget screen shows under it. "Budget ▸" opens the Budget screen with that pot open. Updates itself.
/// </summary>
public partial class PotStrip : HBoxContainer
{
    private readonly Pot _pot;
    private readonly Func<World, string>? _extra;
    private readonly Label _label;
    private double _wait;

    /// <param name="extra">More text for this screen (for example what research teams have cost so far).</param>
    public PotStrip(Pot pot, float width, Func<World, string>? extra = null)
    {
        _pot = pot;
        _extra = extra;
        AddThemeConstantOverride("separation", 8);
        MouseFilter = MouseFilterEnum.Pass;
        var dot = new ColorRect { Color = BudgetTab.PotColor(pot), CustomMinimumSize = new Vector2(8, 8), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(dot);
        _label = Ui.Label("", 12, Ui.Dim);
        _label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _label.CustomMinimumSize = new Vector2(Math.Max(120, width - 110), 0);
        _label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _label.MouseFilter = MouseFilterEnum.Stop;
        _label.TooltipText = $"{Budget.Short(pot)} pot of the Industry budget: {Budget.What(pot)}";
        AddChild(_label);
        AddChild(Ui.SmallButton("Budget ▸", () => OpenBudget(pot), $"Open the Budget screen (E) with the {Budget.Short(pot)} pot open, to change its share or order"));
        Refresh();
    }

    /// <summary>Open the Budget screen on this pot.</summary>
    public static void OpenBudget(Pot pot)
    {
        if (Main.Instance.Screen is not GameScreen screen) return;
        screen.CloseMenuScreens();
        BudgetTab.Show(BudgetTab.View.ByPot, pot);
        screen.ShowEmpire = true;
        screen.EmpireTabWanted = EmpirePanel.Tab.Budget;
    }

    /// <summary>"Buildings pot · 25% · 12/day · 340 saved · (the pot's line)".</summary>
    public static string Text(World w, Pot pot)
    {
        var ps = Budget.Of(w, Side.Player).Pots[(int)pot];
        return $"{ps.Name} pot · {ps.Share:0}% · {Budget.PerDay(w, Side.Player, pot):0.#}/day · {ps.Saved:0} saved"
               + (ps.Locked ? " · locked" : "") + " · " + BudgetTab.PotLine(w, pot);
    }

    public override void _Process(double delta)
    {
        _wait -= delta;
        if (_wait > 0 || !IsVisibleInTree()) return;
        _wait = 0.5;
        Refresh();
    }

    private void Refresh()
    {
        if (Main.Instance?.Sim?.World is not World w) return;
        if (!Budget.On(w)) { Visible = false; return; }
        Visible = true;
        var text = Text(w, _pot) + (_extra?.Invoke(w) is { Length: > 0 } more ? " · " + more : "");
        if (_label.Text != text) _label.Text = text;
    }
}
