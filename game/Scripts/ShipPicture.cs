using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// A class's side-view drawing (Art/Ships, nose right), with optional slot markers along the hull: one lettered
/// dot per slot (W weapon, D defence, U utility, E engine; engines at the back), filled when a part is fitted.
/// Classes without a drawing of their own borrow the nearest hull and say "drawing to come".
/// </summary>
public partial class ShipPicture : Control
{
    private readonly ShipClass _cls;
    private readonly List<string>? _fit;
    private readonly bool _cutaway;
    private readonly Texture2D? _tex;

    public ShipPicture() : this(null!, new Vector2(1, 1)) { }

    /// <param name="fit">Pass the fit to draw slot markers; null for a plain picture.</param>
    public ShipPicture(ShipClass cls, Vector2 size, List<string>? fit = null, bool cutaway = true)
    {
        _cls = cls;
        _fit = fit;
        _cutaway = cutaway;
        CustomMinimumSize = size;
        MouseFilter = MouseFilterEnum.Ignore;
        if (cls == null) return;
        var art = ShipArt.For(cls);
        _tex = (cutaway ? Art.Texture("Ships", art + "-cutaway") : null) ?? Art.Texture("Ships", art);
        TooltipText = ShipArt.HasOwn(cls) ? "" : $"Drawing to come: shown with the {art} hull";
        if (!ShipArt.HasOwn(cls)) MouseFilter = MouseFilterEnum.Pass;
    }

    public override void _Draw()
    {
        if (_cls == null) return;
        var font = ThemeDB.FallbackFont;
        var area = Size;
        if (_tex == null)
        {
            DrawRect(new Rect2(Vector2.Zero, area), new Color(1, 1, 1, 0.04f));
            DrawString(font, new Vector2(8, area.Y / 2 + 5), "No drawing", HorizontalAlignment.Left, -1, 12, Ui.Dim);
            return;
        }
        var ts = _tex.GetSize();
        var scale = Math.Min(area.X / ts.X, area.Y / ts.Y);
        var size = ts * scale;
        var at = (area - size) / 2;
        var own = ShipArt.HasOwn(_cls);
        DrawTextureRect(_tex, new Rect2(at, size), false, own ? Colors.White : new Color(0.75f, 0.8f, 0.9f, 0.7f));

        if (_fit != null && _cls.SlotList.Count > 0)
        {
            // Hull runs from the end of the plume (left) to the nose (right); engines sit at the back.
            var share = ShipArt.HullShare(ShipArt.For(_cls));
            var x0 = at.X + size.X * (1 - share) + size.X * share * 0.08f;
            var x1 = at.X + size.X * 0.94f;
            var order = Enumerable.Range(0, _cls.SlotList.Count)
                .OrderBy(i => _cls.SlotList[i].Type == "E" ? 0 : 1).ThenBy(i => i).ToList();
            var r = Math.Clamp(size.Y * 0.07f, 7f, 11f);
            var mid = at.Y + size.Y / 2;
            for (var k = 0; k < order.Count; k++)
            {
                var i = order[k];
                var slot = _cls.SlotList[i];
                var x = order.Count == 1 ? (x0 + x1) / 2 : x0 + (x1 - x0) * k / (order.Count - 1);
                var y = mid + (k % 2 == 0 ? -1 : 1) * Math.Min(size.Y * 0.2f, r * 2.2f);
                var color = slot.Type switch { "W" => new Color("e8785a"), "D" => new Color("6fb6ff"), "U" => new Color("8fe0a0"), "E" => new Color("f2c14e"), _ => Ui.Dim };
                var filled = i < _fit.Count && !string.IsNullOrEmpty(_fit[i]);
                DrawLine(new Vector2(x, mid), new Vector2(x, y), new Color(color, 0.6f), 1);
                DrawCircle(new Vector2(x, y), r, filled ? new Color(color, 0.9f) : new Color(0.05f, 0.07f, 0.1f, 0.9f));
                DrawArc(new Vector2(x, y), r, 0, Mathf.Tau, 24, color, 1.5f);
                var label = slot.Type + (slot.Pair ? "²" : "");
                var fs = (int)Math.Round(r * 1.3f);
                var w = font.GetStringSize(label, HorizontalAlignment.Left, -1, fs).X;
                DrawString(font, new Vector2(x - w / 2, y + fs * 0.35f), label, HorizontalAlignment.Left, -1, fs, filled ? new Color("0c111a") : color);
            }
        }

        if (!own)
        {
            var tag = "drawing to come";
            var fs = area.Y < 60 ? 10 : 12;
            var tw = font.GetStringSize(tag, HorizontalAlignment.Left, -1, fs).X;
            var box = new Rect2(new Vector2(area.X - tw - 10, 2), new Vector2(tw + 8, fs + 6));
            DrawRect(box, new Color(0.05f, 0.07f, 0.1f, 0.85f));
            DrawString(font, new Vector2(box.Position.X + 4, box.Position.Y + fs + 1), tag, HorizontalAlignment.Left, -1, fs, new Color("f2c14e"));
        }
    }
}
