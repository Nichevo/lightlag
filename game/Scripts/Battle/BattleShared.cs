using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

public enum RingStatus { Shown, TooSmall, Outside }

/// <summary>The rings the engagement screen draws: each side's weapon reach and how far you can spot them.</summary>
public static class EngagementRings
{
    public sealed record Ring(string Label, double Mm, bool AroundMine, Color Color, bool Dashed, float Width);

    public static readonly Color Railguns = new("3fd0ff");
    public static readonly Color Missiles = new("b58cff");
    public static readonly Color PointDefence = new("5fd6b0");
    public static readonly Color Spotting = new(0.62f, 0.92f, 0.62f);
    public static readonly Color TheirRailguns = new("ffb347");
    public static readonly Color TheirMissiles = new("ff7a59");

    public static List<Ring> For(EngagementInfo e)
    {
        var c = GameData.Combat;
        var list = new List<Ring>();
        if (e.WeHaveRailguns) list.Add(new Ring("Your railguns", c.Railgun.RangeMm, true, Railguns, false, 2.5f));
        if (e.WeHaveMissiles) list.Add(new Ring("Your missile launch", c.Missile.LaunchRangeMm, true, Missiles, false, 2f));
        list.Add(new Ring("Your point defence", c.PointDefence.RangeMm, true, PointDefence, false, 1.5f));
        list.Add(new Ring(e.Cued ? "You spot them coasting cold, on their track" : "You spot them coasting cold", e.SpotCold, true, Spotting, true, 1.5f));
        // Their reach is a guess until you have fought that fleet (board 26): what it showed, else other fleets, else your own tech.
        var guess = Main.Instance.Sim is { } sim ? RivalTech.Guess(sim.World, Lightlag.Sim.Core.Side.Player, e.EnemyId) : null;
        var tag = guess is { Plus: > 0 } ? " (guess)" : "";
        if (e.TheyHaveRailguns != false)
            list.Add(new Ring((e.TheyHaveRailguns == true ? "Their railguns" : "Their railguns, if they carry any") + tag, guess?.Rail ?? c.Railgun.RangeMm, false, TheirRailguns, true, 2f));
        if (e.TheyHaveMissiles != false)
            list.Add(new Ring((e.TheyHaveMissiles == true ? "Their missile launch" : "Their missile launch, if they carry any") + tag, guess?.Missile ?? c.Missile.LaunchRangeMm, false, TheirMissiles, true, 1.5f));
        if (guess is { Lance: > 0 })
            list.Add(new Ring("Their pulse lances" + tag, guess.Lance, false, TheirRailguns, true, 1f));
        return list;
    }

    /// <summary>Whether a ring of this screen radius shows: big enough, and crossing the view rather than wholly outside or around it.</summary>
    public static RingStatus Status(Vector2 at, float r, Rect2 view)
    {
        if (r < 6) return RingStatus.TooSmall;
        // Nearest and farthest points of the view from the centre.
        var nx = Mathf.Clamp(at.X, view.Position.X, view.End.X);
        var ny = Mathf.Clamp(at.Y, view.Position.Y, view.End.Y);
        var near = at.DistanceTo(new Vector2(nx, ny));
        var fx = Mathf.Max(Mathf.Abs(at.X - view.Position.X), Mathf.Abs(at.X - view.End.X));
        var fy = Mathf.Max(Mathf.Abs(at.Y - view.Position.Y), Mathf.Abs(at.Y - view.End.Y));
        var far = Mathf.Sqrt(fx * fx + fy * fy);
        return r < near || r > far ? RingStatus.Outside : RingStatus.Shown;
    }

    /// <summary>A point on the ring that is on screen, for its label (top first, then round the ring), or null.</summary>
    public static Vector2? LabelPoint(Vector2 at, float r, Rect2 view)
    {
        var inner = view.Grow(-30);
        for (var i = 0; i < 72; i++)
        {
            // -90 degrees (top), then alternating either side of it.
            var step = (i + 1) / 2 * (i % 2 == 0 ? 1 : -1);
            var a = -Mathf.Pi / 2 + step * Mathf.Tau / 72;
            var p = at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            if (inner.HasPoint(p)) return p;
        }
        return null;
    }
}

/// <summary>A thin labelled bar: green when whole, amber, then red; or for heat, red as it fills.</summary>
public partial class StatBar : Control
{
    private readonly string _label;
    private double _value = 1;
    private bool _alarm;
    private bool _hot;

    public StatBar() : this("") { }
    public StatBar(string label)
    {
        _label = label;
        CustomMinimumSize = new Vector2(58, 14);
        MouseFilter = MouseFilterEnum.Pass;
    }

    public void Set(double value, string tip, bool alarm = false, bool hot = false)
    {
        value = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);
        if (Math.Abs(value - _value) < 1e-3 && alarm == _alarm && TooltipText == tip) return;
        _value = value;
        _alarm = alarm;
        _hot = hot;
        TooltipText = tip;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.1f, 0.13f, 0.18f));
        var good = _hot ? 1 - _value : _value;
        var color = _alarm ? Ui.Danger : good > 0.66 ? new Color("5fd6b0") : good > 0.33 ? Ui.Warn : Ui.Danger;
        DrawRect(new Rect2(Vector2.Zero, new Vector2((float)(size.X * _value), size.Y)), new Color(color, 0.75f));
        DrawString(ThemeDB.FallbackFont, new Vector2(3, size.Y - 3), _label, HorizontalAlignment.Left, size.X - 4, 10, Colors.White);
    }
}
