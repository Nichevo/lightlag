using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Light lag on the map. Your command sits on your flagship: fleets inside its command range (the
/// dashed ring) are live; every other fleet, yours included, is drawn where it was when the light now
/// arriving left it, labelled with how old that news is, with a faint guess of where it is now. Orders
/// still on their way show under the fleet they are going to. Debug view (F7) shows the truth.
/// </summary>
public partial class MapView
{
    private static readonly Color CommandRing = new(Ui.Player, 0.3f);

    /// <summary>Is the map showing the delayed picture (light lag on, not the debug view)?</summary>
    public bool Lagging => Sim.World.Light.On && !RevealAll;

    /// <summary>The latest news of this fleet at your flagship (null without light lag, or none yet).</summary>
    private Sighting? News(Fleet f) => Sim.World.Light.On ? Light.Known(Sim.World, Side.Player, f.Id) : null;

    /// <summary>Is this fleet live (inside your command range, so the map shows it as it is)?</summary>
    private bool Live(Fleet f) => !Lagging || Light.DelayHours(Sim.World, Side.Player, f.Position) <= 0;

    /// <summary>Moves each fleet that isn't live (and its ships) to where the news of it puts it.</summary>
    private void ApplyLag()
    {
        if (!Lagging) return;
        var w = Sim.World;
        foreach (var f in w.Fleets)
        {
            if (Live(f) || OnPlan(f) || News(f) is not Sighting s || !_drawn.TryGetValue(f.Id, out var at)) continue;
            var shift = s.Position - f.Position;
            _drawn[f.Id] = at + shift;
            foreach (var id in f.ShipIds)
                if (_drawn.TryGetValue(id, out var p)) _drawn[id] = p + shift;
        }
    }

    /// <summary>
    /// One of your far fleets still doing what you last ordered: you know its plan, so the map shows it
    /// where the plan puts it now. Not when news is on its way that it did something else (a decision of
    /// its own, a standing order, a fight you have not heard of yet).
    /// </summary>
    private bool OnPlan(Fleet f)
    {
        var w = Sim.World;
        if (f.Side != Side.Player || f.Maneuvering) return false;
        if (w.Light.Notes.Any(n => n.FleetId == f.Id && n.ArriveHour > w.Hour)) return false;
        if (w.Light.InTransit.Any(t => t.Command is IFleetCommand fc && fc.FleetId == f.Id)) return false;
        if (w.ActiveBattle is Battle b && b.For(Side.Player).Fleets.Contains(f.Name)) return false;
        return true;
    }

    /// <summary>Under light lag: is the fleet on your map at all (yours that you have news of; enemies your command knows are in sight)?</summary>
    private bool ShownLagging(Fleet f) =>
        f.Side == Side.Player ? Live(f) || News(f) != null : Sensors.Sees(Sim.World, Side.Player, f, seat: true);

    /// <summary>The command range ring, the flagship mark, news ages and orders on their way.</summary>
    /// <summary>Your stations: a small tag under the body (relays and sensor stations, and those still being built).</summary>
    private void DrawStations()
    {
        var w = Sim.World;
        var row = new Dictionary<int, int>();
        foreach (var st in w.Stations)
        {
            if (st.Side != Side.Player || !_drawn.TryGetValue(st.BodyId, out var p) || w.FindBody(st.BodyId) is not Body b) continue;
            var at = Camera.ToScreen(p);
            if (!Camera.OnScreen(at, 100)) continue;
            row[b.Id] = row.GetValueOrDefault(b.Id) + 1;
            var name = st.Kind switch
            {
                Economy.Relay => "relay",
                Economy.SensorStation => "sensors",
                _ => Economy.StationKind(st.Kind)?.Name.ToLowerInvariant() ?? st.Kind,
            };
            var text = w.Hour < st.ReadyHour ? $"◇ {name} (building)"
                : Economy.StationKind(st.Kind) is { Armour: > 0 } fort ? $"◆ {name} {100 * st.Armour / fort.Armour:0} %"
                : $"◆ {name}";
            var y = (float)Math.Max(8, b.Radius * Camera.Scale) + 12 * row[b.Id] + 6;
            DrawString(_font, at + new Vector2(-24, y), text, HorizontalAlignment.Left, -1, 10, new Color(Ui.Player, 0.8f));
        }
    }

    /// <summary>
    /// Satellites: yours always (a small diamond, hollow on its way, crossed when lost, with a dotted line to where it
    /// is going); the rival's only where your sensors have found one (or in the debug view).
    /// </summary>
    private void DrawSatellites()
    {
        var w = Sim.World;
        foreach (var s in w.Satellites)
        {
            var mine = s.Side == Side.Player;
            if (!mine && !(s.Seen && s.LostHour == null) && !RevealAll) continue;
            var p = Satellites.PositionOf(w, s);
            var at = Camera.ToScreen(p);
            if (!Camera.OnScreen(at, 60)) continue;
            var color = mine ? Ui.Player : Ui.Rival;
            if (s.LostHour != null) color = new Color(color, 0.4f);
            var d = new[] { at + new Vector2(0, -4), at + new Vector2(4, 0), at + new Vector2(0, 4), at + new Vector2(-4, 0), at + new Vector2(0, -4) };
            if (s.Arrived && s.LostHour == null) DrawColoredPolygon(d[..4], color);
            DrawPolyline(d, color, 1, true);
            if (s.LostHour != null) DrawCross(at, 5, Ui.Danger, 1.2f);
            if (!s.Arrived && s.LostHour == null && w.FindBody(s.BodyId) is Body b)
                DrawDashedLine(at, Camera.ToScreen(Satellites.SpotAt(w, b, s.Spot, s.Id)), new Color(color, 0.3f), 1, 3);
            if (Camera.Scale * Physics.MmPerAu > 300 || s.LostHour != null)
                DrawString(_font, at + new Vector2(6, -4), Satellites.KindOf(s.Kind)?.Short + (s.LostHour != null ? " (lost)" : mine && s.Seen ? " (seen)" : ""),
                    HorizontalAlignment.Left, -1, 10, new Color(color, 0.8f));
        }
    }

    private void DrawLag()
    {
        var w = Sim.World;
        DrawStations();
        DrawSatellites();
        if (!w.Light.On) return;
        if (Light.Flagship(w, Side.Player) is Ship flag && _drawn.TryGetValue(flag.Id, out var fp))
        {
            var at = Camera.ToScreen(fp);
            var r = (float)(Light.CommandRangeMm(w, Side.Player) * Camera.Scale);
            if (r > 12 && r < 20000 && PlayerInfo.Has(MapLayer.LagRing)) DrawDashedCircle(at, r, CommandRing, "Command range");
            DrawFlagMark(at + new Vector2(-14, -18), Ui.Player);
        }
        // Our messenger drones (and enemy ones we would see only if close: not drawn).
        foreach (var d in w.Drones)
        {
            if (d.Side != Side.Player) continue;
            var at = Camera.ToScreen(d.Position);
            DrawCircle(at, 3, Ui.Player);
            if (w.FindFleet(d.TargetFleetId) is Fleet tf && _drawn.TryGetValue(tf.Id, out var tp))
                DrawDashedLine(at, Camera.ToScreen(tp), new Color(Ui.Player, 0.35f), 1, 3);
            DrawString(_font, at + new Vector2(6, -4), "drone", HorizontalAlignment.Left, -1, 10, new Color(Ui.Player, 0.8f));
        }
        // Enemy fleets taken to carry the rival flagship: a red flag, with a question mark while it is only a guess.
        foreach (var s in w.Picture)
        {
            if (s.SeenBy != Side.Player || s.Own || s.Flag is not (FlagKnown.Suspected or FlagKnown.Confirmed)) continue;
            if (!_drawn.TryGetValue(s.FleetId, out var ep)) continue;
            var at = Camera.ToScreen(ep);
            if (!Camera.OnScreen(at, 100)) continue;
            DrawFlagMark(at + new Vector2(-14, -18), Ui.Rival);
            if (s.Flag == FlagKnown.Suspected)
                DrawString(_font, at + new Vector2(-4, -20), "?", HorizontalAlignment.Left, -1, 12, Ui.Rival);
        }
        if (RevealAll) return;
        foreach (var f in w.Fleets)
        {
            if (Live(f) || !Shown(f) || News(f) is not Sighting s || !_drawn.TryGetValue(f.Id, out var p)) continue;
            var at = Camera.ToScreen(p);
            if (!Camera.OnScreen(at, 300)) continue;
            var color = Ui.SideColor(f.Side);
            var age = Math.Max(0, w.Hour - s.Hour);
            if (OnPlan(f))
            {
                DrawString(_font, at + new Vector2(12, 8), $"on plan · {Format.Lag(Light.DelayHours(w, Side.Player, f.Position))} of light, last report {Format.Lag(age)} ago",
                    HorizontalAlignment.Left, -1, 11, new Color(color, 0.75f));
                continue;
            }
            // Where it should be now if it kept going: a hollow mark at the end of a faint line.
            var now = Camera.ToScreen(s.Guess(w.Hour));
            if (at.DistanceTo(now) > 6)
            {
                DrawDashedLine(at, now, new Color(color, 0.3f), 1, 4);
                DrawArc(now, 6, 0, Mathf.Tau, 16, new Color(color, 0.45f), 1, true);
            }
            // The light delay itself, and the age of the picture: the picture is taken once a game hour,
            // so it can be up to an hour older than the light delay alone.
            var light = Light.DelayHours(w, Side.Player, s.Position);
            DrawString(_font, at + new Vector2(12, 8), $"{Format.Lag(light)} of light · seen {Format.Lag(age)} ago", HorizontalAlignment.Left, -1, 11, new Color(color, 0.75f));
        }
        var row = new Dictionary<int, int>();
        foreach (var t in w.Light.InTransit)
        {
            if (t.Side != Side.Player || t.Command is not IFleetCommand fc || w.FindFleet(fc.FleetId) is not Fleet f
                || !_drawn.TryGetValue(f.Id, out var p)) continue;
            var at = Camera.ToScreen(p);
            if (!Camera.OnScreen(at, 300)) continue;
            row[f.Id] = row.GetValueOrDefault(f.Id) + 1;
            DrawString(_font, at + new Vector2(12, 8 + 13 * row[f.Id]), $"order arrives in {Format.Lag(t.ArriveHour - w.Hour)}",
                HorizontalAlignment.Left, -1, 11, new Color(Ui.Text, 0.85f));
        }
    }

    /// <summary>A small pennant: the flagship.</summary>
    private void DrawFlagMark(Vector2 at, Color color)
    {
        DrawLine(at, at + new Vector2(0, 12), color, 1.5f);
        for (var i = 0; i <= 6; i++) DrawLine(at + new Vector2(0, i), at + new Vector2(9, 3), color, 1);
    }
}
