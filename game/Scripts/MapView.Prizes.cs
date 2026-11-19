using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>Contested prizes on the map (see Prizes).</summary>
public partial class MapView
{
    private static readonly Color PrizeGold = new("ffd166");

    /// <summary>
    /// Each prize every side knows of: a gold diamond (hollow until it opens, in the holder's colour once someone holds
    /// the relay), the ring inside which warships count as there, and a label with its name and how it stands for you
    /// (when it opens, your boarding or mining, contested, who holds it).
    /// </summary>
    private void DrawPrizes()
    {
        var w = Sim.World;
        if (!Prizes.On(w)) return;
        foreach (var p in Prizes.Live(w))
        {
            var at = Camera.ToScreen(p.At);
            if (!Camera.OnScreen(at, 300)) continue;
            var open = p.Open(w);
            var color = p.Holder is Side h && p.Kind == PrizeKind.Relay ? Ui.SideColor(h) : PrizeGold;
            if (!open) color = new Color(color, 0.75f);
            var ring = (float)(Prizes.D.ReachMm * Camera.Scale);
            if (ring > 10) DrawDashedCircle(at, ring, new Color(color, 0.5f), null);
            DrawDiamond(at, 7, color, open);
            if (p.Contested) DrawArc(at, 12, 0, Mathf.Tau, 24, new Color(Ui.Warn, 0.9f), 1.5f, true);
            var line = open ? Prizes.Status(w, p, Side.Player) : $"opens in {Format.Hours(p.OpenHour - w.Hour)}";
            DrawString(_font, at + new Vector2(11, -3), p.Name, HorizontalAlignment.Left, -1, 12, color);
            DrawString(_font, at + new Vector2(11, 11), line, HorizontalAlignment.Left, 340, 10, new Color(color, 0.85f));
        }
    }

    /// <summary>The live prize under a screen point (within 14 px of its marker), for orders to fly there.</summary>
    public Prize? HitTestPrize(Vector2 screen)
    {
        var w = Sim.World;
        if (!Prizes.On(w)) return null;
        return Prizes.Live(w).Select(p => (p, d: Camera.ToScreen(p.At).DistanceTo(screen))).Where(x => x.d <= 14)
            .OrderBy(x => x.d).Select(x => x.p).FirstOrDefault();
    }
}
