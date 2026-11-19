using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// The Hazards map layer: gas giants' radiation belts as faint green bands, the zone close to the sun
/// where ships run hot as an orange ring, and battle debris as grey clouds that spread and fade. Belts
/// and rings of rock show as their specks anyway; with the layer on they get a name and a "rocks" note.
/// On Medium and High the radiation belts and the hot zone are a soft haze with a thin edge, and rock
/// belts always have a faint band of dust under their specks.
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
                if (Graphics.Fancy)
                {
                    DrawHaze(at, mid, (outer - inner) / 2, RadiationColor, 0.07f * (float)Math.Min(1.5, belt.Density));
                    DrawArc(at, outer, 0, Mathf.Tau, 96, new Color(RadiationColor, 0.2f), 1, true);
                }
                else
                {
                    DrawArc(at, mid, 0, Mathf.Tau, 96, new Color(RadiationColor, 0.07f * (float)Math.Min(1.5, belt.Density)), Math.Max(1, outer - inner), true);
                    DrawArc(at, outer, 0, Mathf.Tau, 96, new Color(RadiationColor, 0.25f), 1, true);
                }
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
                if (Graphics.Fancy) DrawHaze(at, r - Math.Min(r * 0.1f, 18f), Math.Min(r * 0.1f, 18f), HotColor, 0.035f);
                DrawArc(at, r, 0, Mathf.Tau, 128, new Color(HotColor, Graphics.Fancy ? 0.2f : 0.3f), 1, true);
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

    /// <summary>
    /// A soft ring of colour: strongest at radius mid and fading away either side over about half (all in
    /// screen pixels), drawn as a few wide arcs. Skipped when it is too big to matter or wholly off screen.
    /// </summary>
    private void DrawHaze(Vector2 at, float mid, float half, Color color, float peak)
    {
        if (half < 1 || mid > 20000 || !Camera.OnScreen(at, mid + half * 2)) return;
        const int steps = 9;
        var width = half * 3.2f / steps;
        var segments = (int)Math.Clamp(mid / 3, 48, 360);
        for (var i = 0; i < steps; i++)
        {
            var t = -1.6f + 3.2f * (i + 0.5f) / steps;
            var rr = mid + t * half;
            if (rr <= width / 2) continue;
            DrawArc(at, rr, 0, Mathf.Tau, segments, new Color(color, peak * Mathf.Exp(-t * t * 1.4f)), width + 0.5f, false);
        }
    }

    /// <summary>A faint band of dust under each belt of rock (Medium and High), so a belt reads at any zoom.</summary>
    private void DrawBeltDust()
    {
        var w = Sim!.World;
        foreach (var belt in w.Belts.Where(b => b.Kind == BeltKind.Belt))
        {
            if (w.FindBody(belt.ParentId) is not Body parent || !_drawn.TryGetValue(parent.Id, out var c)) continue;
            var inner = (float)(belt.InnerRadius * Camera.Scale);
            var outer = (float)(belt.OuterRadius * Camera.Scale);
            if (outer - inner < 3) continue;
            DrawHaze(Camera.ToScreen(c), (inner + outer) / 2, (outer - inner) * 0.4f, new Color(belt.Color).Lightened(0.15f), 0.06f);
        }
    }
}
