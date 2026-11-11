using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>The Old sightings and Supply runs map layers.</summary>
public partial class MapView
{
    /// <summary>
    /// Rival fleets still on file but no longer tracked by sensors: a small grey cross where they were
    /// last seen, fainter with age, with how long ago. (Fresh sightings are drawn by DrawLastSeen.)
    /// </summary>
    private void DrawOldIntel()
    {
        var w = Sim.World;
        if (!w.Loop) return;
        var tracked = Sensors.Known(w, seat: Lagging).Where(s => s.SeenBy == Side.Player).Select(s => s.FleetId).ToHashSet();
        foreach (var f in Intel.Of(w, Side.Player).Fleets)
        {
            if (tracked.Contains(f.FleetId) || !f.Position.IsFinite) continue;
            var at = Camera.ToScreen(f.Position);
            if (!Camera.OnScreen(at, 60)) continue;
            var sure = (float)Intel.Sure(w, f);
            var color = new Color(Ui.Dim, 0.25f + 0.5f * sure);
            DrawLine(at + new Vector2(-4, -4), at + new Vector2(4, 4), color, 1);
            DrawLine(at + new Vector2(-4, 4), at + new Vector2(4, -4), color, 1);
            DrawString(_font, at + new Vector2(7, 4), $"{f.Name} · {f.Ships} · {Format.Hours(w.Hour - f.Hour)} ago", HorizontalAlignment.Left, -1, 10, color);
        }
    }

    /// <summary>Tankers and freighters on supply runs (to the fleet they serve) and settlers on their way (to their planet).</summary>
    private void DrawSupplyRuns()
    {
        var w = Sim.World;
        var color = new Color("7fd67f", 0.55f);
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player).OrderBy(f => f.Id))
        {
            if (!_drawn.TryGetValue(f.Id, out var from) || !from.IsFinite) continue;
            Vec2? to = null;
            if (f.SupplyRunFor is int run && _drawn.TryGetValue(run, out var served)) to = served;
            else if (f.PeopleRun is { } pr && _drawn.TryGetValue(pr.ToBodyId, out var body)) to = body;
            if (to is not Vec2 target || !target.IsFinite) continue;
            var a = Camera.ToScreen(from);
            var b = Camera.ToScreen(target);
            if (!Camera.OnScreen(a, 400) && !Camera.OnScreen(b, 400)) continue;
            DrawDashedLine(a, b, color, 1.2f, 6);
            var mid = (a + b) / 2;
            DrawString(_font, mid + new Vector2(4, -4), f.SupplyRunFor != null ? "supply run" : "settlers", HorizontalAlignment.Left, -1, 10, color);
        }
    }
}
