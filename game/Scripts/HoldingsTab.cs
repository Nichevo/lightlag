using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The planets screen's "Stations and claims" tab (it was the Empire screen's Overview): the station line and
/// stations over each world you hold, with buttons to start them; satellites with a scrap button and the lift
/// to orbit paid; and the claims under way. Built into a box when its shape changes; numbers update live.
/// </summary>
public sealed class HoldingsTab
{
    private readonly GameScreen _screen;
    private readonly VBoxContainer _box;
    private readonly List<(Label Label, Func<World, string> Text)> _live;
    private readonly float Width;

    public HoldingsTab(GameScreen screen, VBoxContainer box, List<(Label Label, Func<World, string> Text)> live, float width = 720)
    {
        _screen = screen;
        _box = box;
        _live = live;
        Width = width;
    }

    /// <summary>Everything that changes which rows and buttons exist.</summary>
    public static string Shape(World w) =>
        Economy.Of(w, Side.Player).Researched.Count
        + "|" + string.Join(",", w.Bodies.Where(b => b.Owner == Side.Player).Select(b => b.Id))
        + "|" + string.Join(",", w.Stations.Where(st => st.Side == Side.Player).Select(st => $"{st.Id}{(w.Hour >= st.ReadyHour ? "r" : "")}"))
        + "|" + string.Join(",", w.Starbases.Select(st => $"{st.Id}{st.Side}{st.ClassId}"))
        + "|" + string.Join(",", w.StarbaseQueue.Select(q => q.Id))
        + "|" + string.Join(",", w.Satellites.Where(s => s.Side == Side.Player && s.LostHour == null).Select(s => s.Id)) + "|" + w.Winner;

    public void Build(World w)
    {
        // Stations on the bodies you hold.
        if (GameData.Economy.Stations is { Count: > 0 } kinds)
        {
            Heading("Stations");
            Live(12, Ui.Dim, w2 => string.Join("\n", kinds.Where(k => k.Bay == null).Select(k => $"{k.Name} ({k.Cost:0}, {k.Days:0} d): {k.Effect}"
                + (k.Id == Economy.Relay && !w.Light.On ? " (light lag only)" : "")
                + (k.Needs is string n && !Tech.Has(w2, Side.Player, n) ? $" · needs {GameData.Research(n).Name}" : ""))));
            foreach (var b in w.Bodies.Where(b => b.Owner == Side.Player).OrderBy(b => b.Id))
            {
                var row = Row();
                var name = Ui.Label(b.Name, 13, Ui.Text);
                name.CustomMinimumSize = new Vector2(110, 0);
                row.AddChild(name);
                // The station line: what is over it (build and upgrade it on the planet screen, or here while there is none).
                var lineLabel = Ui.Label("", 12, Ui.Dim);
                lineLabel.CustomMinimumSize = new Vector2(150, 0);
                var bid = b.Id;
                _live.Add((lineLabel, w2 => w2.FindBody(bid) is Body bb ? StationPanel.Short(w2, bb) : ""));
                row.AddChild(lineLabel);
                if (Starbases.At(w, b.Id) == null && w.Winner == null && Starbases.TopTier(w, Side.Player) > 0 && Starbases.Queued(w, Side.Player, b.Id) is QueuedStarbase sbQueued)
                {
                    var qid = sbQueued.Id;
                    var qlabel = Ui.Label("", 12, Ui.Warn);
                    qlabel.CustomMinimumSize = new Vector2(130, 0);
                    _live.Add((qlabel, w2 => w2.StarbaseQueue.Find(x => x.Id == qid) is QueuedStarbase q2 ? $"Queued: {Starbases.QueuedWhy(w2, q2)}" : "Starting"));
                    row.AddChild(qlabel);
                    row.AddChild(Ui.SmallButton("✕", () => _screen.IssueNow(new CancelQueuedStarbaseCommand(Side.Player, qid), "cancelling a queued station"), "Take it off the queue"));
                }
                else if (Starbases.At(w, b.Id) == null && w.Winner == null && Starbases.TopTier(w, Side.Player) > 0)
                {
                    var t1 = Starbases.TierAt(1)!;
                    var cls = GameData.ShipClass(t1.ClassId);
                    var why = Starbases.CanBuild(w, Side.Player, b.Id, t1.ClassId);
                    var queue = why != null && Starbases.CanBuild(w, Side.Player, b.Id, t1.ClassId, queue: true) == null;
                    var build = Ui.SmallButton((queue ? "Queue " : "") + $"{cls.Name} · {cls.Cost:0}", () => _screen.IssueNow(new BuildStarbaseCommand(Side.Player, bid, t1.ClassId, Queue: queue), queue ? "queueing a station" : "building a station"),
                        (why != null ? why + (queue ? ": queue it and it starts when the Stations pot can pay" : "") + ". " : "") + $"Start the station line over {b.Name}: {cls.Cost:0} Industry, {t1.Days:0} days. {t1.Blurb} Upgrade it on the planet screen, or build a higher tier there.");
                    build.Disabled = why != null && !queue;
                    build.CustomMinimumSize = new Vector2(130, 26);
                    row.AddChild(build);
                }
                foreach (var k in kinds)
                {
                    if (k.Id == Economy.SensorStation && b.Shipyard) continue; // the shipyard planet already sees full range
                    var st = w.Stations.Find(x => x.Side == Side.Player && x.BodyId == b.Id && x.Kind == k.Id);
                    if (st != null)
                    {
                        var stId = st.Id;
                        var label = Ui.Label("", 12, Ui.Dim);
                        label.CustomMinimumSize = new Vector2(130, 0);
                        row.AddChild(label);
                        _live.Add((label, w2 => w2.Stations.Find(x => x.Id == stId) is Station s2
                            ? w2.Hour < s2.ReadyHour ? $"{k.Name}: {Format.Hours(s2.ReadyHour - w2.Hour)}"
                            : k.Armour > 0 ? $"{k.Name} ✓ armour {100 * s2.Armour / k.Armour:0} %" + (s2.LastHitHour is double hit && w2.Hour - hit < 2 ? " UNDER FIRE" : "")
                            : $"{k.Name} ✓" : ""));
                        continue;
                    }
                    if (w.Winner != null || k.Bay != null) continue; // relays, sensor and laser stations are bays on the line now
                    // Only stations that can go here and are researched (the list above says what the rest need).
                    if (k.Needs is string needs && !Tech.Has(w, Side.Player, needs)) continue;
                    if (k.OnTypes is { Count: > 0 } types && !types.Contains(b.Type) || k.Ground && b.Board == null) continue;
                    var bodyId = b.Id;
                    var kind = k.Id;
                    var button = Ui.SmallButton($"{k.Name} · {k.Cost:0}", () => _screen.BuildStation(bodyId, kind),
                        $"Build a {k.Name.ToLowerInvariant()} at {b.Name}: {k.Cost:0} Industry, {k.Days:0} days. {k.Effect}. The order goes out at light speed.");
                    button.CustomMinimumSize = new Vector2(130, 26);
                    row.AddChild(button);
                }
            }
        }

        // Satellites: each one, where it is and how it is doing, with a button to scrap it; and what the lift to orbit costs.
        Heading("Satellites and lift");
        Live(12, Ui.Dim, w2 =>
        {
            var lines = new List<string>();
            var mine = w2.Satellites.Where(s => s.Side == Side.Player).OrderBy(s => s.Id).ToList();
            lines.Add(mine.Count == 0
                ? (Tech.Has(w2, Side.Player, Satellites.D.Needs) ? "No satellites. Launch one from a planet's panel (Satellite ▾)" : $"Satellites need {GameData.Research(Satellites.D.Needs).Name}")
                : $"{Satellites.Live(w2, Side.Player).Count()} of {Satellites.D.MaxPerSide} satellites:");
            lines.AddRange(mine.Select(s => "  " + Satellites.Describe(w2, s)));
            lines.Add($"Lift to orbit paid so far: {Economy.Of(w2, Side.Player).Totals.LiftSpent:0} Industry");
            return string.Join("\n", lines);
        });
        var scrap = Row();
        foreach (var s in w.Satellites.Where(s => s.Side == Side.Player && s.LostHour == null).OrderBy(s => s.Id))
        {
            var id = s.Id;
            var button = Ui.SmallButton($"Scrap {s.Name}", () => _screen.IssueNow(new ScrapSatelliteCommand(Side.Player, id), "scrapping a satellite"),
                $"Switch {s.Name} off and let it fall: frees its place under the limit of {Satellites.D.MaxPerSide}");
            scrap.AddChild(button);
        }

        // Claims under way.
        Heading("Claims");
        Live(13, Ui.Dim, ClaimsText);
    }

    private static string ClaimsText(World w)
    {
        var lines = w.Bodies.Where(b => b.ClaimSide != null).Select(b =>
            $"{b.Name}: {(b.ClaimSide == Side.Player ? "you are" : "the rival is")} claiming it, {b.ClaimHours / 24:0.#} of {Economy.ClaimHoursFor(b) / 24:0} days").ToList();
        var e = GameData.Economy;
        return lines.Count > 0 ? string.Join("\n", lines)
            : $"Hold orbit at a planet or moon, with no enemy in orbit, to claim it: {e.ClaimDays:0} days for an empty one, {(e.ClaimHeldDays > 0 ? e.ClaimHeldDays : e.ClaimDays):0} for one the rival holds.";
    }

    private void Heading(string text) => _box.AddChild(Ui.Label(text, 15, Ui.Player));

    private void Live(int size, Color color, Func<World, string> text)
    {
        var l = Ui.Label("", size, color);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(Width, 0);
        _box.AddChild(l);
        _live.Add((l, text));
    }

    private HBoxContainer Row()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        _box.AddChild(row);
        return row;
    }
}
