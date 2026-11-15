using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

// The battle view's paths on the map, drawn the same way every time:
//   your planned course: solid, green while speeding up, violet while braking (the normal route colours), with
//     ◇ where it flips to brake and ✕ where a chase meets them;
//   your course with no plan: blue dots, where it drifts;
//   their predicted track: an orange dashed line, out to the look-ahead;
//   closest pass: a gold line between the two, with a label;
//   out of sight: a hollow orange ring where they were last seen.
public partial class MapView
{
    /// <summary>The battle view's fleets and their engagements (empty: just the open engagement).</summary>
    public List<EngagementInfo> BattleRows { get; set; } = new();

    /// <summary>A path to draw while an approach choice is hovered.</summary>
    public (int FleetId, Flight Flight)? BattlePreview { get; set; }

    /// <summary>The reach ring lit while a weapon card is hovered.</summary>
    public Color? BattleReach { get; set; }

    private static readonly Color Gold = new(1f, 0.83f, 0.47f);

    private void DrawBattlePaths(EngagementInfo open)
    {
        var w = Sim.World;
        var rows = BattleRows.Count > 0 ? BattleRows : new List<EngagementInfo> { open };
        const int steps = 40;
        var enemiesDrawn = new HashSet<int>();
        foreach (var e in rows)
        {
            var lead = e.Mine.Id == open.Mine.Id;
            // Your course: the planned route is drawn with the other routes; mark its flip and where it meets them.
            if (e.Mine.Flight is Flight flight && flight.Legs.Count > 0) MarkFlight(e, flight, lead);
            else
                for (var i = 1; i <= steps; i++)
                    DrawCircle(Camera.ToScreen(Combat.PredictFleet(w, e.Mine, w.Hour + e.LookAheadHours * i / steps)), 1.6f, new Color(Ui.Player, 0.6f));

            // Their predicted track, once per enemy: a dashed line.
            if (enemiesDrawn.Add(e.EnemyId))
            {
                Vector2? last = null;
                for (var i = 0; i <= steps; i++)
                {
                    if (Sensors.Predict(w, Side.Player, e.EnemyId, w.Hour + e.LookAheadHours * i / steps) is not Vec2 p) continue;
                    var at = Camera.ToScreen(p);
                    if (last is Vector2 from && i % 2 == 1) DrawLine(from, at, new Color(Ui.Rival, 0.7f), 1.5f);
                    last = at;
                }
                // Out of sight: where they were last seen.
                if (!e.InSight && Sensors.Find(w, Side.Player, e.EnemyId, seat: true) is Sighting s)
                {
                    var seenAt = Camera.ToScreen(s.Position);
                    DrawArc(seenAt, 8, 0, Mathf.Tau, 20, new Color(Ui.Rival, 0.6f), 1.5f, true);
                    DrawString(_font, seenAt + new Vector2(11, -8), $"last seen {Lightlag.Sim.Core.Engagement.Duration(Math.Max(0, w.Hour - s.Hour))} ago",
                        HorizontalAlignment.Left, -1, 11, new Color(Ui.Rival, 0.7f));
                    DrawString(_font, Camera.ToScreen(e.EnemyAt) + new Vector2(12, 24), "predicted position", HorizontalAlignment.Left, -1, 11, new Color(Ui.Rival, 0.7f));
                }
            }

            // Closest pass: a gold line between the two, labelled for the fleet on the tab.
            if (e.ClosestInHours > 1e-3)
            {
                var t = w.Hour + e.ClosestInHours;
                var a = Camera.ToScreen(Combat.PredictFleet(w, e.Mine, t));
                var b = Sensors.Predict(w, Side.Player, e.EnemyId, t) is Vec2 bw ? Camera.ToScreen(bw) : a;
                DrawLine(a, b, new Color(Gold, lead ? 1f : 0.6f), lead ? 2 : 1.2f);
                DrawArc(a, 5, 0, Mathf.Tau, 16, Gold, 1.5f, true);
                var text = lead ? $"Closest pass in {Lightlag.Sim.Core.Engagement.Duration(e.ClosestInHours)} · {Format.Distance(e.ClosestGap)}" : $"{e.Mine.Name}: closest";
                DrawString(_font, a + new Vector2(10, 18), text, HorizontalAlignment.Left, -1, lead ? 12 : 11, Gold);
            }
        }
    }

    /// <summary>On a planned course: ◇ where the burn flips from speeding up to braking, and ✕ where a chase meets its quarry.</summary>
    private void MarkFlight(EngagementInfo e, Flight flight, bool lead)
    {
        var now = DrawHour;
        foreach (var leg in flight.Legs)
        {
            if (!leg.IsBurn || leg.End <= now) continue;
            const int n = 24;
            var from = Math.Max(leg.Start, now);
            var before = BurnColor(leg, from);
            for (var i = 1; i <= n; i++)
            {
                var t = from + (leg.End - from) * i / n;
                var c = BurnColor(leg, t);
                if (before == SpeedUp && c == Braking)
                {
                    var at = Camera.ToScreen(leg.PositionAt(t));
                    DrawDiamond(at, 6, Braking, false);
                    if (lead) DrawString(_font, at + new Vector2(9, -6), $"flip and brake in {Lightlag.Sim.Core.Engagement.Duration(t - now)}", HorizontalAlignment.Left, -1, 11, Braking);
                    goto flipped;
                }
                before = c;
            }
        }
        flipped:
        if (e.Mine.Chase is { } chase && chase.TargetFleetId == e.EnemyId && flight.ArriveHour > now)
        {
            var end = Camera.ToScreen(flight.Target);
            DrawLine(end + new Vector2(-6, -6), end + new Vector2(6, 6), Gold, 2);
            DrawLine(end + new Vector2(-6, 6), end + new Vector2(6, -6), Gold, 2);
            if (lead)
                DrawString(_font, end + new Vector2(10, -8), $"meets {e.EnemyName} in {Lightlag.Sim.Core.Engagement.Duration(flight.ArriveHour - now)}", HorizontalAlignment.Left, -1, 11, Gold);
        }
    }
}
