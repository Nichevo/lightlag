using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// A small list on the map of fights that are coming: which of your fleets meets which enemy fleet, a
/// countdown to contact, and whether the game pauses when it starts. Click a pair to open its engagement.
/// Below, enemy fleets whose course points at one of your planets, with when they get there (dim when the
/// course is from a fleet now out of sight). Click one to go to it.
/// </summary>
public partial class ContactsPanel : PanelContainer
{
    private const int MaxRows = 6, MaxInbound = 4;
    private readonly GameScreen _screen;
    private VBoxContainer _rows = null!;
    private string _key = "";
    private readonly List<(Label When, Button Pause, Engagement.Pair Pair)> _live = new();
    private readonly List<(Label When, Inbound.ToPlanet Threat)> _liveInbound = new();

    public ContactsPanel() : this(null!) { }
    public ContactsPanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 8);
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 2);
        AddChild(_rows);
    }

    public void Refresh()
    {
        var list = _screen.Engagements.Where(p => p.ContactHours != null)
            .OrderBy(p => p.ContactHours).ThenBy(p => p.Gap).Take(MaxRows).ToList();
        var inbound = _screen.InboundToPlanets.Take(MaxInbound).ToList();
        Visible = list.Count > 0 || inbound.Count > 0;
        if (!Visible) return;
        var key = string.Join(";", list.Select(p => $"{p.Mine}:{p.Enemy}")) + "|" + string.Join(";", inbound.Select(t => $"{t.FleetId}:{t.BodyId}"));
        if (key != _key && !Ui.MouseHeld)
        {
            _key = key;
            _live.Clear();
            _liveInbound.Clear();
            Ui.ClearChildren(_rows);
            if (list.Count > 0) _rows.AddChild(Ui.Label("Coming contacts", 13, Ui.Dim));
            foreach (var p in list)
            {
                var pair = p;
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);
                var name = Ui.SmallButton($"{p.MineName} v {p.EnemyName}", () => _screen.OpenEngagement(pair.Mine, pair.Enemy),
                    "Open the engagement screen for these two fleets");
                name.AddThemeFontSizeOverride("font_size", 12);
                row.AddChild(name);
                var when = Ui.Label("", 12, Ui.Text);
                when.CustomMinimumSize = new Vector2(92, 0);
                row.AddChild(when);
                var pause = Ui.SmallButton("", () => _screen.ToggleQuiet(pair.Enemy),
                    "Whether the game pauses when contact with this enemy fleet starts");
                pause.AddThemeFontSizeOverride("font_size", 12);
                row.AddChild(pause);
                _rows.AddChild(row);
                _live.Add((when, pause, p));
            }
            if (inbound.Count > 0) _rows.AddChild(Ui.Label("Heading for your planets", 13, Ui.Dim));
            foreach (var t in inbound)
            {
                var threat = t;
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);
                var name = Ui.SmallButton($"{t.Name} ({t.Ships}) to {t.BodyName}", () => _screen.GoToSighting(threat.FleetId),
                    "Go to this enemy fleet (or where its last seen course puts it)");
                name.AddThemeFontSizeOverride("font_size", 12);
                row.AddChild(name);
                var when = Ui.Label("", 12, Ui.Text);
                when.CustomMinimumSize = new Vector2(92, 0);
                row.AddChild(when);
                _rows.AddChild(row);
                _liveInbound.Add((when, t));
            }
        }
        foreach (var (when, t) in _liveInbound)
        {
            var now = inbound.FirstOrDefault(x => x.FleetId == t.FleetId && x.BodyId == t.BodyId) ?? t;
            when.Text = $"in {Format.Hours(now.Hours)}";
            when.TooltipText = now.InSight ? "In sight" : $"Out of sight: course last seen {Format.Hours(now.SeenHoursAgo)} ago";
            when.MouseFilter = MouseFilterEnum.Pass;
            when.Modulate = now.InSight ? Colors.White : new Color(1, 1, 1, 0.6f);
        }
        foreach (var (when, pause, p) in _live)
        {
            var now = list.FirstOrDefault(x => x.Mine == p.Mine && x.Enemy == p.Enemy) ?? p;
            when.Text = now.ContactHours is double h ? (h <= 0 ? "in contact" : $"in {Format.Hours(h)}") : "";
            when.Modulate = now.InSight ? Colors.White : new Color(1, 1, 1, 0.6f);
            var quiet = _screen.QuietEnemies.Contains(p.Enemy);
            pause.Text = quiet ? "no pause" : "pause";
            pause.Modulate = quiet ? new Color(1, 1, 1, 0.6f) : Ui.Player;
        }
    }
}
