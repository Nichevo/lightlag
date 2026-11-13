using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Ship damage on the map, from the five damage bars. Zoomed out, each ship icon carries a tiny
/// five-segment strip (hull white, weapons blue, engines orange, defence green, crew sand; a segment turns
/// red below a quarter), and crippled ships fade and get a cross. Close up, the drawing shows it: a red
/// cross on each knocked-out part, scorch marks and small fires as the hull wears, and a crippled ship
/// goes dark, tumbles slowly, vents air and has no plume. A destroyed ship leaves a wreck broken in two,
/// with debris, that stays on the map as salvage.
/// </summary>
public partial class MapView
{
    private static readonly Color[] BarColors =
    {
        new(0.92f, 0.94f, 0.96f), // hull
        new(0.4f, 0.62f, 1f),     // weapons
        new(1f, 0.6f, 0.25f),     // engines
        new(0.42f, 0.85f, 0.45f), // defence
        new(0.86f, 0.78f, 0.58f), // crew
    };

    /// <summary>A crippled ship's slow tumble (radians to add to its heading).</summary>
    private static float Tumble(int id) => (float)(Time.GetTicksMsec() / 1000.0 * (0.25 + 0.1 * (id % 3)) + id);

    /// <summary>Are the engines able to burn (not crippled, not knocked out)?</summary>
    private static bool EnginesLit(Ship s) => !s.Crippled && !s.Knocked.Contains(Module.Engines);

    private static void DrawCrossOn(CanvasItem c, Vector2 at, float r, Color color, float width)
    {
        c.DrawLine(at + new Vector2(-r, -r), at + new Vector2(r, r), color, width, true);
        c.DrawLine(at + new Vector2(-r, r), at + new Vector2(r, -r), color, width, true);
    }

    private void DrawCross(Vector2 at, float r, Color color, float width)
    {
        DrawCrossOn(this, at, r + 1, new Color(0, 0, 0, 0.6f), width + 1.5f);
        DrawCrossOn(this, at, r, color, width);
    }

    /// <summary>The five-segment strip: for several ships, the worst of each bar.</summary>
    private void DrawDamageStrip(Vector2 origin, IReadOnlyList<Ship> ships)
    {
        var w = Sim.World;
        const float seg = 2.6f, gap = 0.8f, height = 2.2f;
        DrawRect(new Rect2(origin - new Vector2(1, 1), new Vector2(5 * seg + 4 * gap + 2, height + 2)), new Color(0, 0, 0, 0.55f));
        for (var i = 0; i < Wounds.Bars.Length; i++)
        {
            var share = ships.Min(s => s.Crippled && i == 0 ? 0 : Wounds.Share(w, s, Wounds.Bars[i]));
            var c = share < 0.25 ? Ui.Danger : BarColors[i];
            // Dimmer as the bar wears down.
            c = new Color(c, 0.35f + 0.65f * (float)Math.Clamp(share, 0, 1));
            DrawRect(new Rect2(origin + new Vector2(i * (seg + gap), 0), new Vector2(seg, height)), c);
        }
    }

    /// <summary>Where each part sits on a drawing, nose along +x, as shares of hull length and drawing height.</summary>
    private static Vector2 PartSpot(Module m) => m switch
    {
        Module.Railguns => new Vector2(0.28f, -0.18f),
        Module.Missiles => new Vector2(0.02f, 0.2f),
        Module.PointDefence => new Vector2(0.14f, -0.28f),
        _ => new Vector2(-0.42f, 0f),
    };

    /// <summary>Damage on a ship drawing (drawn in the ship's own frame: nose along +x).</summary>
    private void DrawShipDamage(Ship s, float hullPx, float height)
    {
        var w = Sim.World;
        var hull = (float)Wounds.Share(w, s, Wounds.Bar.Hull);
        var ms = Time.GetTicksMsec();
        // Scorch marks: more as the hull wears.
        var scorches = s.Crippled ? 9 : (int)Math.Round((1 - hull) * 8);
        for (var i = 0; i < scorches; i++)
        {
            var spot = new Vector2((float)(H(s.Id, i) - 0.5) * hullPx * 0.8f, (float)(H(s.Id, i + 40) - 0.5) * height * 0.5f);
            var r = hullPx * (0.03f + 0.04f * (float)H(s.Id, i + 80));
            DrawTextureRect(Graphics.SoftDot, new Rect2(spot - Vector2.One * r, Vector2.One * r * 2), false, new Color(0.05f, 0.04f, 0.03f, 0.75f));
        }
        // Knocked-out parts: a red cross on each.
        var cross = Mathf.Clamp(hullPx * 0.05f, 3.5f, 14f);
        foreach (var m in s.Knocked)
        {
            var p = PartSpot(m);
            DrawCross(new Vector2(p.X * hullPx, p.Y * height), cross, Ui.Danger, Mathf.Clamp(hullPx * 0.012f, 1.5f, 3.5f));
        }
        if (!Graphics.Fancy) return;
        // Small fires where it is hurt, flickering.
        var fires = s.Crippled ? 2 : hull < 0.75f || s.Knocked.Count > 0 ? 1 + (hull < 0.4f ? 1 : 0) : 0;
        for (var i = 0; i < fires; i++)
        {
            var spot = new Vector2((float)(H(s.Id, i + 300) - 0.5) * hullPx * 0.6f, (float)(H(s.Id, i + 340) - 0.5) * height * 0.35f);
            var flick = 0.7f + 0.3f * Mathf.Sin(ms / 70f + i * 3) * Mathf.Sin(ms / 113f + i);
            var r = Mathf.Max(3f, hullPx * 0.035f) * flick;
            DrawTextureRect(Graphics.SoftDot, new Rect2(spot - Vector2.One * r * 1.8f, Vector2.One * r * 3.6f), false, Graphics.Hot(new Color(1f, 0.45f, 0.1f, 0.7f), 1.8f));
            DrawTextureRect(Graphics.SoftDot, new Rect2(spot - Vector2.One * r * 0.7f, Vector2.One * r * 1.4f), false, Graphics.Hot(new Color(1f, 0.85f, 0.4f, 0.9f), 2.2f));
        }
        if (!s.Crippled) return;
        // Venting air: puffs streaming out of a breach, spreading as they go.
        var breach = new Vector2((float)(H(s.Id, 500) - 0.3) * hullPx * 0.5f, height * 0.22f * (H(s.Id, 501) < 0.5 ? -1 : 1));
        var outward = new Vector2(0.3f, Mathf.Sign(breach.Y)).Normalized();
        for (var i = 0; i < 6; i++)
        {
            var phase = ((ms / 1400f) + i / 6f) % 1f;
            var p = breach + outward * hullPx * 0.35f * phase;
            var r = Mathf.Max(2f, hullPx * (0.02f + 0.07f * phase));
            DrawTextureRect(Graphics.SoftDot, new Rect2(p - Vector2.One * r, Vector2.One * r * 2), false, new Color(0.9f, 0.93f, 1f, 0.5f * (1 - phase)));
        }
    }

    /// <summary>Is a wreck worth showing: yours, or where your ships or planets are near enough to have seen it.</summary>
    private bool WreckKnown(Wreck wr)
    {
        if (RevealAll || wr.Side == Side.Player) return true;
        var near = GameData.Combat.ContactRangeMm * 10;
        var w = Sim.World;
        return w.Fleets.Any(f => f.Side == Side.Player && Vec2.Distance(f.Position, wr.Position) < near)
               || w.Bodies.Any(b => b.Owner == Side.Player && Vec2.Distance(b.Position, wr.Position) < near);
    }

    /// <summary>Wrecks: the drawing broken in two, drifting apart and turning, with a little debris; zoomed out a small broken mark.</summary>
    private void DrawWrecks()
    {
        var w = Sim.World;
        foreach (var wr in w.Wrecks)
        {
            if (!WreckKnown(wr)) continue;
            var at = Camera.ToScreen(wr.BodyId is int bid && _drawn.TryGetValue(bid, out var bp) ? bp + wr.Offset : wr.Position);
            if (!Camera.OnScreen(at, 300)) continue;
            var heading = (float)(H(wr.Id, 1) * Math.Tau);
            var turn = (float)(Time.GetTicksMsec() / 1000.0 * 0.05);
            var spacingPx = (float)(Fleets.FormationSpacing * Camera.Scale);
            GameData.ShipClasses.TryGetValue(wr.ClassId, out var cls);
            var hullPx = cls != null ? Mathf.Clamp(spacingPx * 0.75f * (float)(cls.LengthM / 260.0), 4f, 900f) : 8f;
            var color = Ui.SideColor(wr.Side);
            var wreckArt = cls != null ? ShipArt.For(cls, Races.Of(w, wr.Side).Id) : "";
            if (cls != null && hullPx >= 24 && Art.Texture("Ships", wreckArt) is Texture2D tex)
            {
                var art = wreckArt;
                var width = hullPx / ShipArt.HullShare(art);
                var height = width * tex.GetHeight() / tex.GetWidth();
                var left = -(width - hullPx) - hullPx / 2;
                var cut = 0.55f; // where it broke, as a share of the drawing's width
                var tint = new Color(0.34f, 0.32f, 0.31f);
                var apart = hullPx * 0.12f;
                // Stern half.
                var sternRect = new Rect2(left, -height / 2, width * cut, height);
                DrawSetTransform(at, heading - turn, Vector2.One);
                DrawTextureRectRegion(tex, new Rect2(sternRect.Position - new Vector2(apart, 0), sternRect.Size), new Rect2(0, 0, tex.GetWidth() * cut, tex.GetHeight()), tint);
                // Bow half, turning the other way.
                DrawSetTransform(at, heading + turn * 1.3f + 0.25f, Vector2.One);
                DrawTextureRectRegion(tex, new Rect2(new Vector2(left + width * cut + apart, -height / 2), new Vector2(width * (1 - cut), height)),
                    new Rect2(tex.GetWidth() * cut, 0, tex.GetWidth() * (1 - cut), tex.GetHeight()), tint);
                DrawSetTransform(Vector2.Zero, 0, Vector2.One);
                for (var i = 0; i < 10; i++)
                {
                    var a = (float)(H(wr.Id, i + 10) * Math.Tau) + turn * 0.5f;
                    var d = hullPx * (0.3f + 0.5f * (float)H(wr.Id, i + 30));
                    var p = at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * d;
                    var r = Mathf.Max(1.5f, hullPx * 0.015f * (1 + (float)H(wr.Id, i + 50)));
                    DrawRect(new Rect2(p - Vector2.One * r / 2, Vector2.One * r), new Color(0.5f, 0.48f, 0.46f, 0.85f));
                }
                if (Camera.Scale > 0.05)
                    DrawString(_font, at + new Vector2(hullPx * 0.6f, -8), $"Wreck of {wr.Name}", HorizontalAlignment.Left, -1, 12, new Color(color, 0.6f));
                continue;
            }
            // Zoomed out: two dim shards and a few specks.
            var dir = new Vector2(Mathf.Cos(heading), Mathf.Sin(heading));
            var across = new Vector2(-dir.Y, dir.X);
            var c = new Color(color.Darkened(0.4f), 0.7f);
            DrawColoredPolygon(new[] { at + dir * 2 + across * 3, at + dir * 7, at + dir * 2 - across * 3 }, c);
            DrawColoredPolygon(new[] { at - dir * 2 + across * 3, at - dir * 7 + across * 1, at - dir * 7 - across * 2, at - dir * 2 - across * 3 }, c);
            for (var i = 0; i < 4; i++)
                DrawCircle(at + new Vector2((float)(H(wr.Id, i) - 0.5) * 18, (float)(H(wr.Id, i + 9) - 0.5) * 18), 0.9f, c);
            if (Camera.Scale > 0.01)
                DrawString(_font, at + new Vector2(10, 4), "wreck", HorizontalAlignment.Left, -1, 10, new Color(color, 0.5f));
        }
    }

    /// <summary>Escape pods and lifeboats: a small blinking beacon with who they are and how much air is left.
    /// Yours always show; the rival's only where your ships or planets could see them.</summary>
    private void DrawLifeboats()
    {
        var w = Sim.World;
        var near = GameData.Combat.ContactRangeMm * 10;
        foreach (var lb in w.Lifeboats)
        {
            var known = RevealAll || lb.Side == Side.Player
                        || w.Fleets.Any(f => f.Side == Side.Player && Vec2.Distance(f.Position, lb.Position) < near)
                        || w.Bodies.Any(b => b.Owner == Side.Player && Vec2.Distance(b.Position, lb.Position) < near);
            if (!known) continue;
            var at = Camera.ToScreen(lb.BodyId is int bid && _drawn.TryGetValue(bid, out var bp) ? bp + lb.Offset : lb.Position);
            if (!Camera.OnScreen(at, 100)) continue;
            var color = Ui.SideColor(lb.Side);
            var blink = (Time.GetTicksMsec() / 600 + (ulong)lb.Id) % 2 == 0;
            var r = lb.Boat ? 3.5f : 2.5f;
            DrawCircle(at, r, new Color(color, 0.9f));
            if (blink) DrawArc(at, r + 4, 0, Mathf.Tau, 16, new Color(1f, 0.85f, 0.3f, 0.8f), 1.2f);
            if (Camera.Scale > 0.01)
            {
                var air = Lifeboats.AirLeftHours(w, lb);
                var airText = air >= 48 ? $"{air / 24:0} days of air" : $"{air:0} h of air";
                var label = $"{(lb.Boat ? "Lifeboat" : "Pods")} of {lb.ShipName}: {airText}";
                if (lb.RescuerFleetId is int rid && w.FindFleet(rid) is Fleet rf && rf.Side == Side.Player) label += $", {rf.Name} coming";
                DrawString(_font, at + new Vector2(8, 4), label, HorizontalAlignment.Left, -1, 10,
                    new Color(air < 24 ? Ui.Rival : color, 0.75f));
            }
        }
    }
}
