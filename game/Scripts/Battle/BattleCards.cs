using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The cards along the bottom of the battle view (U): one per weapon or system (move, railguns, missiles, lances,
/// defence, heat, formation, orders). Each card says when it shows, builds its rows once with a
/// <see cref="CardBuilder"/>, and gives a short summary for when it is folded.
///
/// To add a weapon or a new mechanic: write a class that extends <see cref="BattleCard"/> (see BuiltInCards.cs for
/// examples) and add it to <see cref="BattleCards.Makers"/>. The screen does the rest: placing, folding, pins,
/// "mixed" when several fleets differ, orders given while paused, and hover rings. docs/BATTLE-CARDS.md has a
/// walk-through.
/// </summary>
public abstract class BattleCard
{
    /// <summary>Stable id: kept in ui.cfg for the card's pin, so don't change it once shipped.</summary>
    public abstract string Id { get; }
    public abstract string Title { get; }

    /// <summary>Left to right along the bottom: lower first.</summary>
    public virtual int Order => 500;

    /// <summary>The reach ring this card lights on the map while the mouse is over it (null: none).</summary>
    public virtual Color? Reach => null;

    /// <summary>Does the card show for these fleets (for example: only if a ship carries the weapon)?</summary>
    public abstract bool Shows(CardContext c);

    /// <summary>Add the card's rows. Called again whenever the fleets on the tab change.</summary>
    public abstract void Build(CardBuilder b);

    /// <summary>One or two short lines for the folded card.</summary>
    public abstract string Folded(CardContext c);
}

/// <summary>Every card the battle view can show. Add a new card here.</summary>
public static class BattleCards
{
    public static readonly List<Func<BattleCard>> Makers = new()
    {
        () => new MoveCard(),
        () => new RailgunCard(),
        () => new MissileCard(),
        () => new LanceCard(),
        () => new DefenceCard(),
        () => new HeatCard(),
        () => new FormationCard(),
        () => new MatchupCard(),
        () => new SecretsCard(),
        () => new OrdersCard(),
    };

    /// <summary>For cards added from elsewhere (a mod or a later build) without editing the list above.</summary>
    public static void Register(Func<BattleCard> make) => Makers.Add(make);

    public static List<BattleCard> Make() => Makers.Select(m => m()).OrderBy(c => c.Order).ToList();
}

/// <summary>What a card works on: the fleets on the chosen tab (one, or all of yours in the fight) and their engagements.</summary>
public sealed class CardContext
{
    public required GameScreen Screen { get; init; }
    public required Simulation Sim { get; init; }
    public World World => Sim.World;

    /// <summary>The fleets the card's orders go to: one fleet, or every fleet of yours in this fight (the All fleets tab).</summary>
    public required IReadOnlyList<Fleet> Fleets { get; init; }
    public Fleet Lead => Fleets[0];
    public bool All => Fleets.Count > 1;

    /// <summary>Each fleet's engagement with the enemy on screen (missing when no enemy is in sight or on record).</summary>
    public required IReadOnlyDictionary<int, EngagementInfo> Infos { get; init; }
    public EngagementInfo? Info => Infos.TryGetValue(Lead.Id, out var e) ? e : null;

    public IEnumerable<Ship> Ships => Fleets.SelectMany(f => World.ShipsOf(f));

    /// <summary>
    /// The latest order of this kind given to the fleet that hasn't taken effect yet (paused, or on its way under light
    /// lag), so a card shows your choice straight away.
    /// </summary>
    public T? Pending<T>(Fleet f) where T : GameCommand, IFleetCommand =>
        Sim.PendingCommands.OfType<T>().LastOrDefault(c => c.FleetId == f.Id)
        ?? World.Light.InTransit.Concat(World.Light.Held).Where(t => t.Side == Side.Player).Select(t => t.Command).OfType<T>().LastOrDefault(c => c.FleetId == f.Id);

    /// <summary>Give an order to every fleet on the tab (null skips a fleet).</summary>
    public void Issue(Func<Fleet, GameCommand?> make, string what)
    {
        foreach (var f in Fleets.ToList())
            if (make(f) is GameCommand c) Screen.IssueNow(c, what);
    }
}

/// <summary>
/// Builds a card's rows: choices (one lit button per value, or a drop-down), switches, one-off actions and text lines.
/// Each row reads its value from the fleets every frame; with several fleets that differ it says "mixed".
/// </summary>
public sealed class CardBuilder
{
    private readonly VBoxContainer _body;
    private readonly List<Action<CardContext>> _live;
    private readonly Func<CardContext?> _context;
    internal CardBuilder(VBoxContainer body, List<Action<CardContext>> live, Func<CardContext?> context)
    {
        _body = body;
        _live = live;
        _context = context;
    }

    public const int LabelWidth = 50;
    private static readonly Color Off = new(1, 1, 1, 0.7f);

    private void Act(Action<CardContext> act, string what) =>
        Main.Instance.RunGuarded(what, () => { if (_context() is CardContext c) act(c); });

    private static Label RowLabel(string text)
    {
        var l = Ui.Label(text, 11, Ui.Dim);
        l.CustomMinimumSize = new Vector2(LabelWidth, 0);
        return l;
    }

    internal static Button Small(string text, string tip, Action act)
    {
        var b = new Button { Text = text, TooltipText = tip, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 22) };
        b.AddThemeFontSizeOverride("font_size", 11);
        b.Pressed += act;
        return b;
    }

    private bool _join;
    private HBoxContainer? _lastRow;

    /// <summary>The next row's controls go on the end of the row before (keeps a card to three lines).</summary>
    public void SameRow() => _join = true;

    private HBoxContainer NewRow(string? label)
    {
        if (_join && _lastRow != null)
        {
            _join = false;
            if (label != null)
            {
                var sep = RowLabel(label);
                sep.CustomMinimumSize = new Vector2(0, 0);
                sep.Text = "  " + label;
                _lastRow.AddChild(sep);
            }
            return _lastRow;
        }
        _join = false;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 3);
        if (label != null) row.AddChild(RowLabel(label));
        _body.AddChild(row);
        _lastRow = row;
        return row;
    }

    /// <summary>
    /// A row of buttons, one per value; the one in force is lit. <paramref name="current"/> reads a fleet's value
    /// (use c.Pending for orders not yet applied); <paramref name="command"/> makes the order for one fleet.
    /// </summary>
    public void Choice<T>(string label, IEnumerable<(string Text, string Tip, T Value)> options, Func<CardContext, Fleet, T> current,
        Func<Fleet, T, GameCommand?> command, string what, Func<CardContext, bool>? visible = null)
    {
        var row = NewRow(label);
        var tag = (Label)row.GetChild(row.GetChildCount() - 1);
        var mine = new List<Control> { tag };
        var buttons = new List<(Button B, T V)>();
        foreach (var (text, tip, value) in options)
        {
            var v = value;
            var b = Small(text, tip, () => Act(c => c.Issue(f => command(f, v), what), what));
            row.AddChild(b);
            buttons.Add((b, v));
            mine.Add(b);
        }
        var joined = tag.Text != label;
        _live.Add(c =>
        {
            Show(row, mine, visible?.Invoke(c) ?? true);
            var values = c.Fleets.Select(f => current(c, f)).Distinct().ToList();
            var mixed = values.Count > 1;
            tag.Text = (joined ? "  " : "") + (mixed ? $"{label} (mixed)" : label);
            tag.AddThemeColorOverride("font_color", mixed ? Ui.Warn : Ui.Dim);
            foreach (var (b, v) in buttons) b.Modulate = !mixed && EqualityComparer<T>.Default.Equals(values[0], v) ? Ui.Player : Off;
        });
    }

    /// <summary>A drop-down choice, for when the values would not fit as buttons (formation shapes and the like).</summary>
    public void Dropdown<T>(string label, IReadOnlyList<(string Text, string Tip, T Value)> options, Func<CardContext, Fleet, T> current,
        Func<Fleet, T, GameCommand?> command, string what)
    {
        var row = NewRow(label);
        var tag = (Label)row.GetChild(row.GetChildCount() - 1);
        var pick = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 22), FitToLongestItem = true };
        pick.AddThemeFontSizeOverride("font_size", 11);
        for (var i = 0; i < options.Count; i++)
        {
            pick.AddItem(options[i].Text, i);
            pick.SetItemTooltip(i, options[i].Tip);
        }
        var joinedPick = tag.Text != label;
        pick.ItemSelected += i => Act(c => c.Issue(f => command(f, options[(int)i].Value), what), what);
        row.AddChild(pick);
        _live.Add(c =>
        {
            var values = c.Fleets.Select(f => current(c, f)).Distinct().ToList();
            var mixed = values.Count > 1;
            tag.Text = (joinedPick ? "  " : "") + (mixed ? $"{label} (mixed)" : label);
            tag.AddThemeColorOverride("font_color", mixed ? Ui.Warn : Ui.Dim);
            var at = mixed ? -1 : options.ToList().FindIndex(o => EqualityComparer<T>.Default.Equals(o.Value, values[0]));
            if (pick.Selected != at && !pick.GetPopup().Visible) pick.Select(at);
            if (!mixed && at >= 0) pick.TooltipText = options[at].Tip;
        });
    }

    /// <summary>An on/off switch, lit when on ("mixed" when the fleets differ).</summary>
    public void Toggle(string text, string tip, Func<CardContext, Fleet, bool> current, Func<Fleet, bool, GameCommand?> command, string what,
        Func<CardContext, bool>? visible = null)
    {
        var row = NewRow(null);
        Button b = null!;
        b = Small(text, tip, () => Act(c =>
        {
            // From mixed, switch them all on.
            var on = c.Fleets.All(f => current(c, f));
            c.Issue(f => command(f, !on), what);
        }, what));
        row.AddChild(b);
        _live.Add(c =>
        {
            Show(row, new List<Control> { b }, visible?.Invoke(c) ?? true);
            var values = c.Fleets.Select(f => current(c, f)).Distinct().ToList();
            var mixed = values.Count > 1;
            b.Text = mixed ? $"{text}: mixed" : values[0] ? $"{text} ✓" : text;
            b.Modulate = mixed ? Ui.Warn : values[0] ? Ui.Player : Off;
        });
    }

    /// <summary>A row of one-off actions (eject sinks, pull back now). Danger ones are red.</summary>
    public void Actions(params (string Text, string Tip, Func<Fleet, GameCommand?> Command, bool Danger)[] actions)
    {
        var row = NewRow(null);
        foreach (var (text, tip, command, danger) in actions)
        {
            var make = command;
            var b = Small(text, tip, () => Act(c => c.Issue(make, text.ToLowerInvariant()), text));
            if (danger) b.AddThemeColorOverride("font_color", Ui.Danger);
            row.AddChild(b);
        }
    }

    /// <summary>Show or hide a row's own controls; the row hides when nothing on it shows.</summary>
    private static void Show(HBoxContainer row, List<Control> mine, bool on)
    {
        foreach (var m in mine) m.Visible = on;
        row.Visible = row.GetChildren().OfType<Control>().Any(x => x.Visible);
    }

    /// <summary>
    /// A line of text worked out each frame (empty hides it). It keeps to <paramref name="lines"/> lines across the card
    /// (lines given with \n run on with " · "); the whole text is in its tooltip.
    /// </summary>
    public Label Line(Func<CardContext, string> text, Color? color = null, int size = 11, Func<CardContext, Color>? colorNow = null, int lines = 1)
    {
        var l = Ui.Label("", size, color ?? Ui.Text);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(150, 0);
        l.MaxLinesVisible = lines;
        l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        l.MouseFilter = Control.MouseFilterEnum.Pass;
        _body.AddChild(l);
        _live.Add(c =>
        {
            var t = text(c);
            var flat = t.Replace("\n", " · ");
            if (l.Text != flat) { l.Text = flat; l.TooltipText = t; }
            l.Visible = t != "";
            if (colorNow != null) l.AddThemeColorOverride("font_color", colorNow(c));
        });
        return l;
    }

    /// <summary>Anything else: add your own control, with an update each frame.</summary>
    public void Custom(Control control, Action<CardContext>? refresh = null)
    {
        if (_join && _lastRow != null) { _join = false; _lastRow.AddChild(control); }
        else
        {
            _body.AddChild(control);
            if (control is HBoxContainer row) _lastRow = row;
        }
        if (refresh != null) _live.Add(refresh);
    }
}
