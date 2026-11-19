using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// A bar in steps (the intel level ladder, Seen → Studied → Unlocked): each step has a weight (its width), how full
/// it is (0-1) and a colour.
/// </summary>
public partial class StepBar : Control
{
    private readonly (double Weight, double Fill, Color Color)[] _steps;

    public StepBar() : this(Array.Empty<(double, double, Color)>()) { }

    public StepBar(params (double Weight, double Fill, Color Color)[] steps)
    {
        _steps = steps;
        MouseFilter = MouseFilterEnum.Pass;
        CustomMinimumSize = new Vector2(80, 7);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    public override void _Draw()
    {
        var total = _steps.Sum(s => s.Weight);
        if (total <= 0) return;
        const float gap = 3;
        var width = Size.X - gap * (_steps.Length - 1);
        var x = 0f;
        foreach (var (weight, fill, color) in _steps)
        {
            var w = (float)(width * weight / total);
            DrawRect(new Rect2(x, 0, w, Size.Y), new Color("24324a"));
            DrawRect(new Rect2(x, 0, w * (float)Math.Clamp(fill, 0, 1), Size.Y), color);
            x += w + gap;
        }
    }
}

/// <summary>An estimate as a range bar (low to high, in the rival's colour) with your own figure as a tick.</summary>
public partial class RangeBar : Control
{
    private readonly double _low, _high, _mine, _max;
    private readonly Color _theirs;

    public RangeBar() : this(0, 0, 0, Ui.Rival) { }

    public RangeBar(double low, double high, double mine, Color theirs)
    {
        (_low, _high, _mine, _theirs) = (low, Math.Max(low, high), mine, theirs);
        _max = Math.Max(1, Math.Max(_high, mine) * 1.15);
        MouseFilter = MouseFilterEnum.Pass;
        CustomMinimumSize = new Vector2(80, 10);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    public override void _Draw()
    {
        float X(double v) => (float)(Size.X * Math.Clamp(v / _max, 0, 1));
        DrawRect(new Rect2(0, 2, Size.X, Size.Y - 4), new Color("1b2638"));
        var a = X(_low);
        var b = Math.Max(a + 3, X(_high));
        DrawRect(new Rect2(a, 1, b - a, Size.Y - 2), new Color(_theirs, 0.75f));
        var m = X(_mine);
        DrawRect(new Rect2(m - 1.5f, -2, 3, Size.Y + 4), Ui.Player);
    }
}

/// <summary>
/// The listening map on Their plans: the system from above, your ears (fleets and planets) with the ring where
/// plans are heard in full and the ring past which nothing is, the rival's command as you know it, its fleets as last
/// seen, and the targets of plans heard. So how to hear more is something you can see.
/// </summary>
public partial class ListenMap : Control
{
    private World? _w;
    private Side _about;

    public ListenMap() => MouseFilter = MouseFilterEnum.Pass;

    public void Show(World w, Side about)
    {
        _w = w;
        _about = about;
        QueueRedraw();
    }

    /// <summary>Where your side believes the rival's command is: its flagship when in sight, else its home yard.</summary>
    public static Vec2? CommandAsKnown(World w, Side about)
    {
        if (Light.Flagship(w, about) is Ship s && w.FleetOf(s) is Fleet f && Sensors.Sees(w, Side.Player, f)) return f.Position;
        return Economy.ShipyardOf(w, about)?.Position;
    }

    public override void _Draw()
    {
        if (_w is not World w) return;
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color("0b111b"));
        var file = Intel.Of(w, Side.Player, _about);
        var ears = Intentions.Ears(w, Side.Player);
        var command = CommandAsKnown(w, _about);
        var points = w.Bodies.Where(b => b.Kind == BodyKind.Planet && b.Position.IsFinite).Select(b => b.Position).Concat(ears).ToList();
        if (command is Vec2 c) points.Add(c);
        var reach = points.Count == 0 ? 1 : points.Max(p => p.Length);
        reach = Math.Max(reach, Intentions.ListenMaxAu * Physics.MmPerAu) * 1.08;
        var centre = size / 2;
        var scale = (float)(Math.Min(size.X, size.Y) / 2 / reach);
        Vector2 At(Vec2 p) => centre + new Vector2((float)p.X, (float)p.Y) * scale;
        var font = ThemeDB.FallbackFont;

        // The listening rings first, faint, so everything else draws over them.
        var full = (float)(Intentions.ListenFullAu * Physics.MmPerAu) * scale;
        var max = (float)(Intentions.ListenMaxAu * Physics.MmPerAu) * scale;
        foreach (var e in ears)
        {
            DrawCircle(At(e), max, new Color(Ui.Player, 0.035f));
            DrawCircle(At(e), full, new Color(Ui.Player, 0.07f));
        }
        DrawCircle(centre, 5, new Color("ffd27a"));
        foreach (var b in w.Bodies.Where(b => b.Kind == BodyKind.Planet && b.Position.IsFinite))
        {
            DrawArc(centre, (float)b.Position.Length * scale, 0, Mathf.Tau, 96, Ui.Orbit, 1);
            var at = At(b.Position);
            var tint = b.Owner is Side o ? Ui.SideColor(o) : Ui.Dim;
            DrawCircle(at, 4, tint);
            DrawString(font, at + new Vector2(6, -4), b.Name, HorizontalAlignment.Left, -1, 10, new Color(tint, 0.8f));
        }
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player && f.ShipIds.Count > 0 && f.Position.IsFinite))
            DrawRect(new Rect2(At(f.Position) - new Vector2(3, 3), new Vector2(6, 6)), Ui.Player);
        // Its fleets where last seen, fading with age.
        foreach (var x in file.Fleets.Where(x => !x.Cargo && x.Position.IsFinite))
        {
            var sure = (float)Intel.Sure(w, x);
            if (sure <= 0) continue;
            var at = At(x.Position);
            var col = new Color(Ui.SideColor(_about), 0.3f + 0.7f * sure);
            DrawColoredPolygon(new[] { at + new Vector2(0, -5), at + new Vector2(5, 4), at + new Vector2(-5, 4) }, col);
        }
        // Targets of plans heard.
        foreach (var i in Intentions.Heard(w, Side.Player, _about).Where(i => i.BodyId != null).Take(6))
            if (w.FindBody(i.BodyId!.Value) is Body t)
                DrawArc(At(t.Position), 9, 0, Mathf.Tau, 24, new Color("ffd27a"), 1.5f);
        if (command is Vec2 cmd)
        {
            var at = At(cmd);
            var cover = Intentions.Cover(ears, cmd).Cover;
            DrawArc(at, 8, 0, Mathf.Tau, 24, Ui.SideColor(_about), 2);
            DrawLine(at + new Vector2(-5, 0), at + new Vector2(5, 0), Ui.SideColor(_about), 2);
            DrawLine(at + new Vector2(0, -5), at + new Vector2(0, 5), Ui.SideColor(_about), 2);
            DrawString(font, at + new Vector2(10, 14), cover > 0 ? $"their command · {cover:P0} heard" : "their command · out of reach",
                HorizontalAlignment.Left, -1, 10, cover > 0 ? new Color("ffd27a") : Ui.Dim);
        }
    }
}
