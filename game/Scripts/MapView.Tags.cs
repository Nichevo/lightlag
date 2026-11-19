using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Name tags for planets, moons and rocks (Medium and High, Name tags layer): the name on a soft dark
/// backing with a dot in the owner's colour, then smaller notes (shipyard, under attack, settlers, a claim)
/// and a count of the moons too close to show at this zoom. Tags never overlap each other or another body:
/// each tries beside its body first, and in a crowd (the inner worlds zoomed out) steps out to a free slot
/// with a thin leader line back. A tag with no room left is dropped, the selected and hovered ones last.
/// </summary>
public partial class MapView
{
    private readonly List<Rect2> _tagPlaced = new();
    private readonly List<Rect2> _tagDiscs = new();
    private StyleBoxFlat? _tagBack;

    private static readonly Color TagName = new(0.88f, 0.91f, 0.95f);
    private static readonly Color TagNote = new(0.6f, 0.66f, 0.75f);
    private static readonly Color TagLeader = new(0.63f, 0.7f, 0.8f, 0.5f);
    private static readonly Color StarName = new(0.92f, 0.84f, 0.68f, 0.9f);

    /// <summary>Whether a body gets a tag at this zoom.</summary>
    private bool ShowTag(Body b)
    {
        if (b.Id == SelectedId || b.Id == HoverId || b.Kind == BodyKind.Star) return true;
        var held = b.Owner != null || b.ClaimSide != null;
        return b.Kind switch
        {
            BodyKind.Moon => b.OrbitRadius * Camera.Scale >= 30 || (held && b.OrbitRadius * Camera.Scale >= 14),
            // Named rocks fold into their belt's name until the belt is wide on screen.
            BodyKind.Asteroid => held || b.OrbitRadius * Camera.Scale >= 400,
            _ => true,
        };
    }

    /// <summary>Tag order: the ones that matter most get the best spots.</summary>
    private int TagRank(Body b) =>
        b.Id == SelectedId ? 0 : b.Id == HoverId ? 1 : b.Kind == BodyKind.Star ? 2 : b.Owner == Side.Player ? 3 : b.Owner != null ? 4 : b.ClaimSide != null ? 5
        : b.Kind == BodyKind.Planet ? 6 : b.Kind == BodyKind.Asteroid ? 7 : 8;

    private void DrawBodyTags()
    {
        var w = Sim.World;
        _tagBack ??= new StyleBoxFlat
        {
            BgColor = new Color(0.024f, 0.04f, 0.07f, 0.62f),
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        };
        _tagPlaced.Clear();
        _tagDiscs.Clear();
        // Clear of the top bar and the bottom strip.
        var screen = new Rect2(0, Hud.ContentTop, Camera.ScreenSize.X, Mathf.Max(100, Camera.ScreenSize.Y - Hud.ContentTop - 70));
        var shown = new List<(Body Body, Vector2 At, float R)>();
        foreach (var b in w.Bodies)
        {
            if (!IsVisible(b)) continue;
            var at = Camera.ToScreen(_drawn[b.Id]);
            var r = (float)ScreenRadius(b);
            if (!Camera.OnScreen(at, r + 220)) continue;
            // Every body's disc is kept clear of tags, the star's glow too.
            var keep = b.Kind == BodyKind.Star ? r + 6 : r + 2;
            _tagDiscs.Add(new Rect2(at - new Vector2(keep, keep), new Vector2(keep * 2, keep * 2)));
            if (!ShowTag(b)) continue;
            if (!PlayerInfo.Has(MapLayer.Labels) && b.Id != SelectedId && b.Id != HoverId && b.ClaimSide == null && b.Owner == null) continue;
            shown.Add((b, at, r));
        }
        var tagged = shown.Select(s => s.Body.Id).ToHashSet();
        foreach (var (b, at, r) in shown.OrderBy(s => TagRank(s.Body)).ThenByDescending(s => s.Body.Radius))
        {
            // Rings first, so tags sit over them: the owner's ring (while the body is small on screen), a claim
            // filling up, the selection.
            if (b.Owner is Side owner && b.Kind != BodyKind.Star && r < 40) DrawArc(at, r + 3, 0, Mathf.Tau, 40, new Color(Ui.SideColor(owner), 0.85f), 1.4f, true);
            var notes = new List<string>();
            if (b.Shipyard) notes.Add("shipyard");
            if (b.Shipyard && b.LastBombardedHour is double hit && w.Hour - hit <= 24) notes.Add("under attack");
            if (w.Loop && w.Fleets.FirstOrDefault(f => f.Side == Side.Player && f.PeopleRun is { OneTrip: true } run && run.ToBodyId == b.Id) is { Flight: { } settling })
                notes.Add($"settlers land in {Format.Hours(Math.Max(0, settling.ArriveHour - w.Hour))}");
            var ring = r + 3;
            if (b.ClaimSide is Side claimer)
            {
                var share = (float)(b.ClaimHours / Economy.ClaimHoursFor(b));
                var cc = Ui.SideColor(claimer);
                ring = r + 9;
                DrawArc(at, ring, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * share, 40, cc, 2.5f, true);
                DrawArc(at, ring, 0, Mathf.Tau, 40, new Color(cc, 0.2f), 1, true);
                notes.Add($"claiming {share * 100:0} %");
            }
            if (b.Id == SelectedId) { ring = Math.Max(ring, r + 6); DrawArc(at, r + 6, 0, Mathf.Tau, 48, new Color(Ui.Text, 0.8f), 1.2f, true); }
            var hidden = b.Kind == BodyKind.Planet ? w.Bodies.Count(m => m.ParentId == b.Id && m.Kind == BodyKind.Moon && !tagged.Contains(m.Id)) : 0;
            DrawTag(b, at, ring, notes, hidden, screen);
        }
    }

    private void DrawTag(Body b, Vector2 at, float ring, List<string> notes, int hiddenMoons, Rect2 screen)
    {
        var star = b.Kind == BodyKind.Star;
        var small = b.Kind is BodyKind.Moon or BodyKind.Asteroid;
        var size = small ? 12 : 14;
        const int noteSize = 11;
        var nameW = _font.GetStringSize(b.Name, HorizontalAlignment.Left, -1, size).X;
        var note = string.Join(" · ", notes);
        var noteW = note.Length > 0 ? _font.GetStringSize(note, HorizontalAlignment.Left, -1, noteSize).X + 7 : 0;
        var moons = hiddenMoons > 0 ? $"+{hiddenMoons} {(hiddenMoons == 1 ? "moon" : "moons")}" : null;
        var moonW = moons != null ? _font.GetStringSize(moons, HorizontalAlignment.Left, -1, 10).X + 14 : 0;
        var dot = b.Owner != null && !star ? 11f : 0f;
        var box = new Vector2(nameW + noteW + moonW + dot + (star ? 0 : 12), size + 8);
        var always = b.Id == SelectedId || b.Id == HoverId;

        // Beside the body first; then out to the side on a leader line, stepping up and down until it fits.
        var gap = ring + 5;
        Rect2? spot = null;
        var leader = false;
        foreach (var p in new[]
                 {
                     new Vector2(at.X + gap, at.Y - box.Y / 2), new Vector2(at.X - gap - box.X, at.Y - box.Y / 2),
                     new Vector2(at.X + gap - 2, at.Y - gap - box.Y + 4), new Vector2(at.X + gap - 2, at.Y + gap - 4),
                 })
        {
            if (Free(new Rect2(p, box))) { spot = new Rect2(p, box); break; }
        }
        if (spot == null && !star)
        {
            for (var k = 0; k <= 8 && spot == null; k++)
                foreach (var sign in k == 0 ? new[] { 1 } : new[] { 1, -1 })
                {
                    var y = at.Y - box.Y / 2 + sign * k * (box.Y + 4);
                    foreach (var x in new[] { at.X + gap + 34, at.X - gap - 34 - box.X })
                    {
                        var r = new Rect2(x, y, box.X, box.Y);
                        if (Free(r)) { spot = r; leader = true; break; }
                    }
                    if (spot != null) break;
                }
        }
        if (spot == null)
        {
            if (!always) return;
            spot = new Rect2(at.X + gap, at.Y - box.Y / 2, box.X, box.Y);
        }
        var rect = spot.Value;
        _tagPlaced.Add(rect.Grow(2));

        if (leader)
        {
            var end = new Vector2(rect.Position.X < at.X ? rect.End.X : rect.Position.X, rect.GetCenter().Y);
            var dir = (end - at).Normalized();
            DrawLine(at + dir * (ring + 1), end, TagLeader, 1, true);
        }
        var baseline = rect.Position.Y + box.Y / 2 + size * 0.36f;
        if (star)
        {
            DrawString(_font, new Vector2(rect.Position.X, baseline), b.Name, HorizontalAlignment.Left, -1, size, StarName);
            return;
        }
        DrawStyleBox(_tagBack!, rect);
        var x0 = rect.Position.X + 6;
        var side = b.Owner is Side o ? Ui.SideColor(o) : (Color?)null;
        if (side is Color sc)
        {
            DrawCircle(new Vector2(x0 + 3.5f, rect.GetCenter().Y), 3.5f, sc);
            x0 += dot;
        }
        var nameColor = b.Id == SelectedId ? Ui.Text : small && side == null ? new Color(TagName, 0.8f) : TagName;
        DrawString(_font, new Vector2(x0, baseline), b.Name, HorizontalAlignment.Left, -1, size, nameColor);
        x0 += nameW;
        if (note.Length > 0)
        {
            var nc = notes.Contains("under attack") ? Ui.Warn : side is Color c2 ? c2.Lightened(0.15f) : TagNote;
            DrawString(_font, new Vector2(x0 + 7, baseline), note, HorizontalAlignment.Left, -1, noteSize, nc);
            x0 += noteW;
        }
        if (moons != null)
        {
            var chip = new Rect2(x0 + 6, rect.GetCenter().Y - 7, moonW - 8, 14);
            DrawRect(chip, new Color(TagNote, 0.45f), false, 1);
            DrawString(_font, new Vector2(chip.Position.X + 3, chip.GetCenter().Y + 3.6f), moons, HorizontalAlignment.Left, -1, 10, TagNote);
        }

        bool Free(Rect2 r)
        {
            if (!screen.Encloses(r)) return false;
            foreach (var t in _tagPlaced) if (t.Intersects(r)) return false;
            foreach (var d in _tagDiscs) if (d.Intersects(r)) return false;
            return true;
        }
    }
}
