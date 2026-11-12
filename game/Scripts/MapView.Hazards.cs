using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// The Hazards map layer: gas giants' radiation belts as faint green bands, the zone close to the sun
/// where ships run hot as an orange ring, and battle debris as grey clouds that spread and fade. Belts
/// and rings of rock show as their specks anyway; with the layer on they get a name and a "rocks" note.
/// </summary>
public partial class MapView
{
    private static readonly Color RadiationColor = new("7fd18a");
    private static readonly Color HotColor = new("ff9a3c");
    private static readonly Color DebrisColor = new("b8b0a0");

    private void DrawHazards()
    {
        var w = Sim!.World;
        if (!Hazards.On(w)) return;
        foreach (var belt in w.Belts)
        {
            if (w.FindBody(belt.ParentId) is not Body parent || !_drawn.TryGetValue(parent.Id, out var c)) continue;
            var at = Camera.ToScreen(c);
            var inner = (float)(belt.InnerRadius * Camera.Scale);
            var outer = (float)(belt.OuterRadius * Camera.Scale);
            if (outer < 6 || !Camera.OnScreen(at, outer + 10)) continue;
            var mid = (inner + outer) / 2;
            if (belt.Kind == BeltKind.Radiation)
            {
                DrawArc(at, mid, 0, Mathf.Tau, 96, new Color(RadiationColor, 0.07f * (float)Math.Min(1.5, belt.Density)), Math.Max(1, outer - inner), true);
                DrawArc(at, outer, 0, Mathf.Tau, 96, new Color(RadiationColor, 0.25f), 1, true);
                if (outer > 40) DrawString(_font, at + new Vector2(-outer * 0.7f, -outer * 0.7f), "radiation", HorizontalAlignment.Left, -1, 11, new Color(RadiationColor, 0.75f));
            }
            else if (outer - inner > 4 && outer > 40)
            {
                var label = belt.Kind == BeltKind.Ring ? $"{belt.Name}: rocks" : $"{belt.Name}: rocks (slow down)";
                DrawString(_font, at + new Vector2(mid * 0.71f, -mid * 0.71f), label, HorizontalAlignment.Left, -1, 11, new Color(new Color(belt.Color), 0.85f));
            }
        }
        if (Hazards.Star(w) is Body star && _drawn.TryGetValue(star.Id, out var sunAt))
        {
            var r = (float)(Hazards.D.SunHotAu * Physics.MmPerAu * Camera.Scale);
            var at = Camera.ToScreen(sunAt);
            if (r > 8 && Camera.OnScreen(at, r + 10))
            {
                DrawArc(at, r, 0, Mathf.Tau, 128, new Color(HotColor, 0.3f), 1, true);
                DrawString(_font, at + new Vector2(r * 0.71f + 4, r * 0.71f), "hot: ships heat up", HorizontalAlignment.Left, -1, 11, new Color(HotColor, 0.7f));
            }
        }
        foreach (var d in w.Debris)
        {
            var at = Camera.ToScreen(Hazards.PositionOf(w, d));
            var r = Math.Max(4f, (float)(Hazards.RadiusOf(w, d) * Camera.Scale));
            if (!Camera.OnScreen(at, r + 10)) continue;
            var thick = (float)Math.Clamp(Hazards.DensityOf(w, d) / Hazards.D.DebrisMaxDensity, 0.1, 1);
            DrawCircle(at, r, new Color(DebrisColor, 0.06f + 0.12f * thick));
            DrawArc(at, r, 0, Mathf.Tau, 48, new Color(DebrisColor, 0.3f + 0.3f * thick), 1, true);
            for (var i = 0; i < 14; i++)
            {
                var a = (float)(Hash(d.Id * 31 + i) * Mathf.Tau);
                var rr = r * (float)Math.Sqrt(Hash(d.Id * 31 + i + 500));
                DrawCircle(at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr, 1.2f, new Color(DebrisColor, 0.6f * thick + 0.2f));
            }
            if (r > 12) DrawString(_font, at + new Vector2(r + 4, 4), "debris", HorizontalAlignment.Left, -1, 11, new Color(DebrisColor, 0.8f));
        }
    }
}
