using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// One line along the bottom of the map with the top Needs you items (supply gaps, fleets in trouble, key
/// news), most urgent first. Click an item to go there; ◀ ▶ scroll the line. With UrgentOnly (battle) it
/// shows Urgent items only.
/// </summary>
public partial class BottomBar : PanelContainer
{
    private readonly Action<NeedItem> _jump;
    private HBoxContainer _row = null!;
    private int _offset;
    private string _shape = "";

    /// <summary>Show only Urgent items (in a battle).</summary>
    public bool UrgentOnly { get; set; }

    public BottomBar() : this(_ => { }) { }
    public BottomBar(Action<NeedItem> jump) => _jump = jump;

    public override void _Ready()
    {
        var style = new StyleBoxFlat { BgColor = new Color(0.03f, 0.05f, 0.08f, 0.88f), BorderColor = new Color(Ui.Player, 0.25f) };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(5);
        style.SetContentMarginAll(4);
        AddThemeStyleboxOverride("panel", style);
        _row = new HBoxContainer();
        _row.AddThemeConstantOverride("separation", 6);
        AddChild(_row);
    }

    public static Color ColorOf(NeedLevel level) => level switch
    {
        NeedLevel.Urgent => Ui.Danger,
        NeedLevel.Soon => new Color("ffb347"),
        _ => new Color("6fb6ff"),
    };

    public void Refresh(World w, float width)
    {
        var needs = Economy.Of(w, Side.Player).Needs.Where(n => !UrgentOnly || n.Level == NeedLevel.Urgent)
            .OrderBy(n => n.Level).ThenBy(n => n.Hour).ThenBy(n => n.Key, StringComparer.Ordinal).ToList();
        Visible = needs.Count > 0;
        if (!Visible) return;
        _offset = Math.Clamp(_offset, 0, Math.Max(0, needs.Count - 1));
        var shape = $"{width}|{_offset}|{UrgentOnly}|" + string.Join(";", needs.Select(n => n.Key + n.Text));
        if (shape == _shape || Ui.MouseHeld) return;
        _shape = shape;
        foreach (var c in _row.GetChildren()) c.QueueFree();
        var back = Ui.SmallButton("◀", () => { _offset = Math.Max(0, _offset - 1); _shape = ""; }, "Earlier items");
        back.Disabled = _offset == 0;
        _row.AddChild(back);
        var room = width - 120;
        var shown = 0;
        foreach (var n in needs.Skip(_offset))
        {
            var text = n.Text.Length > 70 ? n.Text[..67] + "…" : n.Text;
            var need = n;
            var b = Ui.SmallButton(text, () => _jump(need), $"{n.Level}: {n.Text}\nClick to go there");
            b.AddThemeColorOverride("font_color", ColorOf(n.Level));
            b.AddThemeFontSizeOverride("font_size", 13);
            var est = text.Length * 7.5f + 24;
            if (shown > 0 && est > room) break;
            room -= est;
            shown++;
            _row.AddChild(b);
        }
        var more = needs.Count - _offset - shown;
        var next = Ui.SmallButton(more > 0 ? $"▶ {more} more" : "▶", () => { _offset = Math.Min(needs.Count - 1, _offset + 1); _shape = ""; }, "Later items");
        next.Disabled = more <= 0;
        _row.AddChild(next);
    }
}
