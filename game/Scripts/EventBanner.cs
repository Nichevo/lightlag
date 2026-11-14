using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// A banner under the top bar while an event or hazard with wide impact lasts (a solar flare, a comet pass):
/// its name, what it does, and the time left (or until it hits). Click one to go to it or open Needs you.
/// Local trouble stays in Needs you and the bottom bar.
/// </summary>
public partial class EventBanner : PanelContainer
{
    private readonly GameScreen _screen;
    private HBoxContainer _row = null!;
    private string _shape = "";
    private readonly List<(Label Label, Events.WideEvent Event)> _times = new();

    public EventBanner() : this(null!) { }
    public EventBanner(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        var style = new StyleBoxFlat { BgColor = new Color(0.12f, 0.08f, 0.02f, 0.92f), BorderColor = Ui.Warn };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(5);
        style.SetContentMarginAll(5);
        AddThemeStyleboxOverride("panel", style);
        _row = new HBoxContainer();
        _row.AddThemeConstantOverride("separation", 14);
        AddChild(_row);
        MouseFilter = MouseFilterEnum.Pass;
    }

    public void Refresh(World w)
    {
        var list = Events.Wide(w, Side.Player);
        Visible = list.Count > 0;
        if (!Visible) return;
        var shape = string.Join(";", list.Select(e => e.Key + e.Effect + e.Coming));
        if (shape != _shape && !Ui.MouseHeld)
        {
            _shape = shape;
            _times.Clear();
            foreach (var c in _row.GetChildren()) c.QueueFree();
            foreach (var e in list)
            {
                var item = new HBoxContainer();
                item.AddThemeConstantOverride("separation", 6);
                var ev = e;
                var name = Ui.SmallButton(e.Title, () => Go(ev), "Click to go there, or to its card in Needs you");
                name.AddThemeColorOverride("font_color", Ui.Warn);
                item.AddChild(name);
                item.AddChild(Ui.Label(e.Effect, 13, Ui.Text));
                var time = Ui.Label("", 13, new Color("ffd166"));
                item.AddChild(time);
                _times.Add((time, e));
                _row.AddChild(item);
            }
            ResetSize();
        }
        foreach (var (label, e) in _times)
        {
            var left = Math.Max(0, e.UntilHour - w.Hour);
            label.Text = e.Coming ? $"· hits in {Format.Hours(left)}" : $"· {Format.Hours(left)} left";
        }
    }

    private void Go(Events.WideEvent e)
    {
        if (e.BodyId is int b) { _screen.PickBody(b); return; }
        _screen.ShowEmpire = true;
        _screen.EmpireTabWanted = EmpirePanel.Tab.Needs;
    }
}
