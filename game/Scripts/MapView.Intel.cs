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

    /// <summary>How fast an unseen fleet's possible area grows beyond its own drift (Mm per hour), and the share of its speed it could turn.</summary>
    private const double PossibleSpreadMmPerHour = 2, PossibleTurnShare = 0.3;

    /// <summary>
    /// "Where they could be" layer: round the predicted spot of each enemy fleet out of sight, the area it could
    /// have reached since it was last seen. It grows with time, so an old sighting is a wide cloud, not a point.
    /// </summary>
    private void DrawPossible()
    {
        var w = Sim.World;
        foreach (var s in Sensors.Known(w, seat: Lagging))
        {
            if (s.SeenBy != Side.Player || s.Visible || s.Own) continue;
            var hours = Math.Max(0, DrawHour - s.Hour);
            if (hours < 0.5) continue;
            var guess = Camera.ToScreen(s.Guess(DrawHour));
            var from = Camera.ToScreen(s.Position);
            var r = (float)(hours * (PossibleSpreadMmPerHour + PossibleTurnShare * s.Velocity.Length) * Camera.Scale);
            if (r < 10 || !Camera.OnScreen(guess, r + 50)) continue;
            var color = new Color(Ui.Rival, 0.5f);
            DrawCircle(guess, Math.Min(r, 4000), new Color(Ui.Rival, 0.05f));
            DrawDashedCircle(guess, r, color, null);
            // A cone from the last sighting to the area: the two tangent lines.
            var d = from.DistanceTo(guess);
            if (d > r + 4)
            {
                var dir = (guess - from) / d;
                var half = Mathf.Asin(r / d);
                var len = Mathf.Sqrt(d * d - r * r);
                foreach (var sign in new[] { -1f, 1f })
                    DrawLine(from, from + dir.Rotated(sign * half) * len, new Color(Ui.Rival, 0.25f), 1);
            }
            var note = s.State == SignatureState.Burn ? "; it was burning, so it may have turned" : "";
            if (s.FleetId == SelectedId || s.FleetId == HoverId || r < 400)
                DrawString(_font, guess + new Vector2(-60, Math.Min(r, 300) + 14), $"{s.Name} could be in here ({Format.Hours(hours)} unseen{note})",
                    HorizontalAlignment.Left, -1, 11, color);
        }
    }

    /// <summary>
    /// "What they see of you" layer: round each of your fleets with an enemy fleet near, how far that fleet's
    /// sensors would pick it up as it flies now (burning shows from much further than coasting cold).
    /// </summary>
    private void DrawTheySee()
    {
        var w = Sim.World;
        var enemies = Sensors.Known(w, seat: Lagging).Where(s => s.SeenBy == Side.Player && !s.Own).ToList();
        if (enemies.Count == 0) return;
        var anySensor = GameData.ShipClasses.Values.Max(k => k.SensorMm);
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player).OrderBy(f => f.Id))
        {
            if (!_drawn.TryGetValue(f.Id, out var p) || !p.IsFinite) continue;
            var near = enemies.MinBy(s => Vec2.Distance(s.Guess(DrawHour), p))!;
            var sensor = near.Classes.Count > 0 ? near.Classes.Max(c => GameData.ShipClass(c).SensorMm) : anySensor;
            var range = Sensors.Range(sensor, Sensors.Signature(w, f));
            var gap = Vec2.Distance(near.Guess(DrawHour), p);
            if (f.Id != SelectedId && gap > range * 3) continue;
            var at = Camera.ToScreen(p);
            var r = (float)(range * Camera.Scale);
            var seen = gap <= range;
            var color = new Color(seen ? Ui.Danger : new Color("c98a8a"), seen ? 0.7f : 0.4f);
            DrawDashedCircle(at, r, color, f.Id == SelectedId || seen
                ? (seen ? $"{near.Name} can see {f.Name}" : $"{near.Name} would see {f.Name} inside this")
                : null);
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
