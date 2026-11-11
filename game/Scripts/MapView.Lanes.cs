using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Supply lanes you have spotted (a band between the two bodies, wider the longer since the last
/// sighting, fainter as confidence fades) and the zones you have drawn (raid amber, guard cyan, picket
/// grey), with the fleets holding them. Lanes and zones on a run swing round with their bodies.
/// </summary>
public partial class MapView
{
    private Vec2 DrawnAt(World w, int bodyId) => _drawn.TryGetValue(bodyId, out var p) ? p : w.FindBody(bodyId)?.Position ?? Vec2.Zero;

    private void DrawLanesAndZones()
    {
        var w = Sim!.World;
        var side = Side.Player;
        foreach (var l in w.Lanes.Where(l => l.SeenBy == side || RevealAll && l.SeenBy == Side.Rival))
        {
            var a = Camera.ToScreen(DrawnAt(w, l.FromBodyId));
            var b = Camera.ToScreen(DrawnAt(w, l.ToBodyId));
            var sure = (float)Lanes.Confidence(w, l);
            var width = Mathf.Clamp((float)(Lanes.WidthMm(w, l) * Camera.Scale), 2, 400);
            var tint = l.SeenBy == side ? Ui.Rival : Ui.Player; // a lane of the enemy's cargo
            DrawLine(a, b, new Color(tint, 0.06f + 0.10f * sure), width);
            DrawDashedLine(a, b, new Color(tint, 0.25f + 0.45f * sure), 1.2f, 8);
            if (width > 20 || Camera.Scale > 0.01)
            {
                var mid = (a + b) / 2;
                var every = Lanes.EveryDays(l) is double d ? $" · a ship every {d:0.#} d" : "";
                DrawString(_font, mid + new Vector2(6, -6), $"supply lane {sure:P0}{every}", HorizontalAlignment.Left, -1, 11, new Color(tint, 0.7f));
            }
        }
        foreach (var z in w.Zones.Where(z => z.Side == side || RevealAll))
        {
            var center = z.FromBodyId is int fa && z.ToBodyId is int fb
                ? DrawnAt(w, fa) + (DrawnAt(w, fb) - DrawnAt(w, fa)) * z.T
                : z.Center;
            var at = Camera.ToScreen(center);
            var r = (float)(z.RadiusMm * Camera.Scale);
            if (!Camera.OnScreen(at, r + 50)) continue;
            var color = z.Side != side ? Ui.Rival : z.Kind switch
            {
                ZoneKind.Raid => new Color("ffb347"),
                ZoneKind.Guard => Ui.Player,
                _ => Ui.Dim,
            };
            r = Mathf.Max(r, 6);
            DrawCircle(at, r, new Color(color, 0.05f));
            DrawArc(at, r, 0, Mathf.Tau, 64, new Color(color, 0.6f), 1.2f, true);
            var holders = w.Fleets.Count(f => f.ZoneId == z.Id);
            DrawString(_font, at + new Vector2(r * 0.7f + 4, -r * 0.7f), $"{z.Name}{(holders > 0 ? $" · {holders} fleet{(holders > 1 ? "s" : "")}" : "")}"
                + (z.Catches > 0 ? $" · {z.Catches} caught" : ""), HorizontalAlignment.Left, -1, 11, new Color(color, 0.85f));
        }
    }
}
