using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The Empire screen's Budget tab: the six pots with their shares of the day's Industry, what each has saved and
/// what it is paying for; a pot opens to the items drawing on it in the player's order (move, pause, make a goal);
/// a cut-off place offers "Share it out again" or "Hold it for ..."; By place compares what each planet earns with
/// what is spent there; By project lists what is being saved for; the Reserve card sends Reserve to a pot.
/// Every button issues a command, so it all works while paused. Numbers update through the live list.
/// </summary>
public sealed class BudgetTab
{
    public const float Width = 640;
    private readonly GameScreen _screen;
    private readonly VBoxContainer _box;
    private readonly List<(Label Label, Func<World, string> Text)> _live;

    public enum View { ByPot, ByPlace, ByProject }

    private static View _view = View.ByPot;
    private static readonly HashSet<Pot> Open = new();
    private static Pot _sendTo = Pot.Ships;

    public BudgetTab(GameScreen screen, VBoxContainer box, List<(Label Label, Func<World, string> Text)> live)
    {
        _screen = screen;
        _box = box;
        _live = live;
    }

    /// <summary>Everything that changes which rows and buttons exist.</summary>
    public static string Shape(World w)
    {
        if (!Budget.On(w)) return "off";
        var bs = Budget.Of(w, Side.Player);
        return $"{_view}|{string.Join(",", Open)}|{_sendTo}|{bs.OverflowTo}|{Stewards.LevelFor(w, Side.Player, AutoArea.Budget)}|"
               + string.Join(",", bs.Pots.Select(p => $"{p.Name}{p.Share}{p.Locked}{p.GoalKey}"))
               + "|" + string.Join(",", Budget.Items(w, Side.Player).Select(i => $"{i.Key}{i.Paused}{i.Blocked}{i.Next}"))
               + "|" + string.Join(",", bs.Places.Select(kv => $"{kv.Key}{kv.Value}"))
               + "|" + (_view == View.ByPlace ? string.Join(",", w.Bodies.Where(b => b.Owner == Side.Player).Select(b => b.Id)) : "")
               + "|" + (_view == View.ByProject ? Budget.Projects(w, Side.Player).Count : 0);
    }

    private static void Go(View v) => _view = v;

    /// <summary>Show a view with one pot open (for the screenshot run).</summary>
    public static void Show(View v, Pot? open)
    {
        _view = v;
        Open.Clear();
        if (open is Pot p) Open.Add(p);
    }

    public void Build(World w)
    {
        _box.CustomMinimumSize = new Vector2(Width, 0);
        if (!Budget.On(w))
        {
            Heading("Industry budget");
            Text("The budget was switched off when this game began: Industry is one pool, spent by whatever asks first.");
            return;
        }
        Summary(w);
        var views = Row();
        foreach (var v in Enum.GetValues<View>())
        {
            var view = v;
            var b = Small(v switch { View.ByPot => "By pot", View.ByPlace => "By place", _ => "By project" }, () => Go(view),
                v switch
                {
                    View.ByPot => "Each pot, and the items drawing on it in your order",
                    View.ByPlace => "Where the Industry is earned against where it is spent",
                    _ => "The things you are saving for, side by side",
                });
            if (v == _view) b.Modulate = Ui.Player;
            views.AddChild(b);
        }
        switch (_view)
        {
            case View.ByPlace: ByPlace(w); break;
            case View.ByProject: ByProject(w); break;
            default: ByPot(w); break;
        }
        ReserveCard(w);
        Control(w);
    }

    // ---- The strip along the top -------------------------------------------------------------------

    private void Summary(World w)
    {
        Heading("Industry budget");
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 18);
        _box.AddChild(grid);
        Stat(grid, "Industry a day", w2 => $"{Economy.IncomePerDay(w2, Side.Player):0.#}");
        Stat(grid, "In hand", w2 => $"{Economy.Of(w2, Side.Player).Industry:0}");
        Stat(grid, "Spent last 30 days", w2 => $"{Budget.Of(w2, Side.Player).Pots.Sum(p => p.Days.Sum() + p.SpentToday):0}");
        Stat(grid, "Waiting on Industry", w2 =>
        {
            var n = w2.Orders.Count(o => o.Side == Side.Player && o.Waiting?.Contains("Industry", StringComparison.Ordinal) == true)
                    + w2.Bodies.Where(b => b.Owner == Side.Player).Sum(b => b.Pieces.Count(p => p.Waiting && Colonies.QueuedWhy(w2, b, p)?.Contains("Industry", StringComparison.Ordinal) == true));
            return n == 1 ? "1 order" : $"{n} orders";
        });
        // The six shares as one coloured bar, with a key (as in the design).
        var bar = new HBoxContainer { CustomMinimumSize = new Vector2(Width - 40, 12) };
        bar.AddThemeConstantOverride("separation", 2);
        var bs = Budget.Of(w, Side.Player);
        foreach (var pot in Budget.All)
        {
            var share = (float)bs.Pots[(int)pot].Share;
            if (share <= 0) continue;
            bar.AddChild(new ColorRect { Color = PotColor(pot), CustomMinimumSize = new Vector2(0, 12), SizeFlagsHorizontal = Godot.Control.SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = share, TooltipText = $"{bs.Pots[(int)pot].Name} {share:0} %", MouseFilter = Godot.Control.MouseFilterEnum.Pass });
        }
        _box.AddChild(bar);
        var key = new HFlowContainer();
        key.AddThemeConstantOverride("h_separation", 14);
        foreach (var pot in Budget.All)
        {
            var item = new HBoxContainer();
            item.AddThemeConstantOverride("separation", 5);
            item.AddChild(Swatch(pot));
            item.AddChild(Ui.Label($"{bs.Pots[(int)pot].Name} {bs.Pots[(int)pot].Share:0} %", 11, Ui.Dim));
            key.AddChild(item);
        }
        _box.AddChild(key);
    }

    /// <summary>Each pot's colour, from the design: ships blue, buildings green, stations gold, refits sand, transport violet, reserve grey.</summary>
    public static Color PotColor(Pot pot) => pot switch
    {
        Pot.Ships => new Color("5ec4ff"),
        Pot.Buildings => new Color("6fdc9a"),
        Pot.Stations => new Color("f2cf72"),
        Pot.Refits => new Color("dcc394"),
        Pot.Transport => new Color("c99bff"),
        _ => new Color("8a96a8"),
    };

    private static ColorRect Swatch(Pot pot) => new()
    {
        Color = PotColor(pot), CustomMinimumSize = new Vector2(10, 10), SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter, MouseFilter = Godot.Control.MouseFilterEnum.Ignore,
    };

    /// <summary>A small outlined tag ("locked", "saving for a goal").</summary>
    private static PanelContainer Badge(string text, Color color, string tip)
    {
        var p = new PanelContainer { TooltipText = tip, SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter };
        var box = new StyleBoxFlat { BgColor = new Color(color, 0.12f), BorderColor = color };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(4);
        box.ContentMarginLeft = box.ContentMarginRight = 6;
        box.ContentMarginTop = box.ContentMarginBottom = 1;
        p.AddThemeStyleboxOverride("panel", box);
        p.AddChild(Ui.Label(text, 11, color));
        return p;
    }

    private void Stat(GridContainer grid, string name, Func<World, string> value)
    {
        var box = new VBoxContainer();
        box.AddChild(Ui.Label(name, 11, Ui.Dim));
        var v = Ui.Label("", 18, Ui.Text);
        _live.Add((v, value));
        box.AddChild(v);
        grid.AddChild(box);
    }

    // ---- By pot ------------------------------------------------------------------------------------

    private void ByPot(World w)
    {
        var bs = Budget.Of(w, Side.Player);
        Text("Each pot fills from its share of the day's Industry and keeps what it does not spend. An order draws only from its own pot. "
             + "Open a pot to put its items in your order, pause one, or save for one as a goal.", 12);
        foreach (var pot in Budget.All) PotRow(w, bs, pot);
    }

    private void PotRow(World w, BudgetState bs, Pot pot)
    {
        var ps = bs.Pots[(int)pot];
        var open = Open.Contains(pot);
        var row = Row();
        row.AddChild(Small(open ? "▾" : "▸", () => { if (!Open.Remove(pot)) Open.Add(pot); }, open ? "Close the pot" : "Open the pot: the items drawing on it"));
        row.AddChild(Swatch(pot));
        var name = Ui.Label(ps.Name, 14, Ui.Text);
        name.CustomMinimumSize = new Vector2(156, 0);
        name.TooltipText = Budget.What(pot);
        row.AddChild(name);
        row.AddChild(Small("−", () => _screen.IssueNow(new SetBudgetShareCommand(Side.Player, pot, Math.Max(0, ps.Share - 5)), "setting a budget share"),
            ShareTip(w, pot, -5)));
        var share = Ui.Label($"{ps.Share:0} %", 14, Ui.Text);
        share.CustomMinimumSize = new Vector2(44, 0);
        share.HorizontalAlignment = HorizontalAlignment.Center;
        row.AddChild(share);
        row.AddChild(Small("+", () => _screen.IssueNow(new SetBudgetShareCommand(Side.Player, pot, ps.Share + 5), "setting a budget share"),
            ShareTip(w, pot, +5)));
        // The share as a bar in the pot's colour.
        var back = new ColorRect { Color = new Color("1a2230"), CustomMinimumSize = new Vector2(70, 6), SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter, MouseFilter = Godot.Control.MouseFilterEnum.Ignore };
        back.AddChild(new ColorRect { Color = PotColor(pot), Size = new Vector2((float)(70 * Math.Clamp(ps.Share / 50.0, 0, 1)), 6), MouseFilter = Godot.Control.MouseFilterEnum.Ignore });
        row.AddChild(back);
        var nums = Ui.Label("", 12, Ui.Dim);
        nums.CustomMinimumSize = new Vector2(150, 0);
        _live.Add((nums, w2 => $"{Budget.PerDay(w2, Side.Player, pot):0.#}/day · {Budget.Of(w2, Side.Player).Pots[(int)pot].Saved:0} saved"));
        row.AddChild(nums);
        if (pot != Pot.Reserve)
            row.AddChild(Small("+ Reserve", () => _screen.IssueNow(new MoveIndustryCommand(Side.Player, Pot.Reserve, pot, Math.Min(50, bs.Pots[(int)Pot.Reserve].Saved)), "moving Industry"),
                "Send 50 from Reserve into this pot (the Reserve card below sends more)"));
        var lockB = Small(ps.Locked ? "Locked" : "Lock", () => _screen.IssueNow(new LockPotCommand(Side.Player, pot, !ps.Locked), "locking a pot"),
            ps.Locked ? "Automation leaves this share alone. Press to let it move the share again." : "Stop automation moving this pot's share (a share you set by hand locks it too)");
        row.AddChild(lockB);
        if (ps.Locked) row.AddChild(Badge("locked", Ui.Warn, "Automation leaves this share alone"));
        if (ps.GoalKey != null) row.AddChild(Badge("saving for a goal", Ui.Warn, $"Saving for {ps.GoalName}: nothing else may spend this pot"));
        var sub = Ui.Label("", 12, Ui.Dim);
        sub.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        sub.CustomMinimumSize = new Vector2(Width - 40, 0);
        _live.Add((sub, w2 => PotLine(w2, pot)));
        Indent(sub);
        if (ps.GoalKey != null) GoalCard(w, pot);
        if (open) OpenPot(w, bs, pot);
    }

    /// <summary>The line under a pot: its goal, its first item, or what it pays for.</summary>
    private static string PotLine(World w, Pot pot)
    {
        var bs = Budget.Of(w, Side.Player);
        var ps = bs.Pots[(int)pot];
        if (pot == Pot.Reserve)
            return "Kept back for trouble: urgent defence and events may draw on it" + (bs.RolledRate > 0.05 ? $" · +{bs.RolledRate:0.#} a day from cut-off places" : "")
                   + (bs.OverflowTo == Pot.Reserve ? " · full pots overflow here" : "");
        if (ps.GoalKey != null) return $"Saving for {ps.GoalName} · {Budget.GoalLine(w, Side.Player, pot)}";
        var items = Budget.Items(w, Side.Player).Where(i => i.Pot == pot).ToList();
        if (items.Count == 0) return pot == Pot.Transport ? "Runs and lift pay as they go" : pot == Pot.Stations ? "Nothing queued: stations are bought from a planet's panel" : "Nothing waiting";
        var top = items.FirstOrDefault(i => !i.Paused) ?? items[0];
        var more = items.Count > 1 ? $" · {items.Count - 1} more" : "";
        return $"{top.What} {top.Cost:0} · {Budget.When(w, Side.Player, top)}{more}";
    }

    /// <summary>What moving this pot's share by delta does to its first waiting item.</summary>
    private static string ShareTip(World w, Pot pot, double delta)
    {
        var bs = Budget.Of(w, Side.Player);
        var ps = bs.Pots[(int)pot];
        var tip = $"{(delta > 0 ? "Raise" : "Lower")} {ps.Name} by {Math.Abs(delta):0} points (the unlocked pots make up the rest); a share set by hand locks the pot.";
        if (Budget.Items(w, Side.Player).FirstOrDefault(i => i.Pot == pot && i.Holds) is not BudgetItem top) return tip;
        var need = top.Cost - Budget.Free(w, Side.Player, pot, top.Key);
        if (need <= 0 || ps.Share + delta <= 0) return tip;
        var perDay = Economy.IncomePerDay(w, Side.Player);
        var now = perDay * ps.Share / 100;
        var then = perDay * (ps.Share + delta) / 100;
        if (now <= 0 || then <= 0) return tip;
        return tip + $"\n{top.What}: ready in about {Math.Ceiling(need / then):0} d instead of {Math.Ceiling(need / now):0} d.";
    }

    private void GoalCard(World w, Pot pot)
    {
        var ps = Budget.Of(w, Side.Player).Pots[(int)pot];
        var row = Row();
        row.AddChild(Spacer(28));
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 3);
        var label = Ui.Label("", 13, Ui.Warn);
        label.CustomMinimumSize = new Vector2(330, 0);
        _live.Add((label, w2 =>
        {
            var p = Budget.Of(w2, Side.Player).Pots[(int)pot];
            return p.GoalKey == null ? "" : $"Goal: {p.GoalName} · {p.GoalCost:0}, {Math.Min(p.Saved, p.GoalCost):0} saved";
        }));
        label.TooltipText = "Nothing else may spend this pot while a goal is set, except an urgent defence, which says so in the log.";
        col.AddChild(label);
        var back = new ColorRect { Color = new Color("1a2230"), CustomMinimumSize = new Vector2(300, 6), MouseFilter = Godot.Control.MouseFilterEnum.Ignore };
        var fill = new ColorRect { Color = PotColor(pot), Size = new Vector2(0, 6), MouseFilter = Godot.Control.MouseFilterEnum.Ignore };
        back.AddChild(fill);
        col.AddChild(back);
        _live.Add((label, w2 =>
        {
            var p = Budget.Of(w2, Side.Player).Pots[(int)pot];
            fill.Size = new Vector2(p.GoalCost > 0 ? (float)(300 * Math.Clamp(p.Saved / p.GoalCost, 0, 1)) : 0, 6);
            return label.Text;
        }));
        row.AddChild(col);
        row.AddChild(Small("Add from Reserve", () =>
        {
            var bs = Budget.Of(_screen.World, Side.Player);
            var left = Math.Max(0, ps.GoalCost - bs.Pots[(int)pot].Saved);
            _screen.IssueNow(new MoveIndustryCommand(Side.Player, Pot.Reserve, pot, Math.Min(left, bs.Pots[(int)Pot.Reserve].Saved)), "adding from Reserve");
        }, "Move what the goal still needs from Reserve (as much as Reserve has)"));
        row.AddChild(Small("Drop", () => _screen.IssueNow(new SetGoalCommand(Side.Player, pot, null), "dropping a goal"), "Stop saving for it: the pot spends as usual"));
    }

    /// <summary>A pot opened: its items in your order, cut-off places, and its name.</summary>
    private void OpenPot(World w, BudgetState bs, Pot pot)
    {
        var items = Budget.Items(w, Side.Player).Where(i => i.Pot == pot).ToList();
        var grid = new GridContainer { Columns = 8 };
        grid.AddThemeConstantOverride("h_separation", 8);
        Indent(grid);
        if (items.Count == 0)
        {
            grid.AddChild(Ui.Label("Nothing is drawing on this pot just now.", 12, Ui.Dim));
            for (var i = 0; i < 7; i++) grid.AddChild(new Control());
        }
        else
            foreach (var h in new[] { "#", "", "Where", "What", "Cost", "When", "", "" }) grid.AddChild(Ui.Label(h, 11, Ui.Dim));
        var n = 0;
        foreach (var it in items)
        {
            n++;
            var item = it;
            grid.AddChild(Ui.Label($"{n}", 12, Ui.Dim));
            var arrows = new HBoxContainer();
            arrows.AddChild(Small("▲", () => _screen.IssueNow(new RankItemCommand(Side.Player, item.Key, -1), "moving an item up"), "Move up: the money goes to the top one first"));
            arrows.AddChild(Small("▼", () => _screen.IssueNow(new RankItemCommand(Side.Player, item.Key, +1), "moving an item down"), "Move down"));
            grid.AddChild(arrows);
            var where = item.BodyId is int b && w.FindBody(b) is Body body ? body.Name : "";
            var whereB = Small(where, () => { if (item.BodyId is int id) _screen.OpenPlanet(id); }, "Open the planet");
            grid.AddChild(whereB);
            grid.AddChild(Ui.Label(item.What, 12, item.Paused ? Ui.Dim : Ui.Text));
            grid.AddChild(Ui.Label($"{item.Cost:0}", 12, Ui.Text));
            var when = Ui.Label("", 12, item.Blocked ? Ui.Danger : Ui.Dim);
            when.CustomMinimumSize = new Vector2(150, 0);
            when.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _live.Add((when, w2 => Budget.Items(w2, Side.Player).Find(i => i.Key == item.Key) is BudgetItem now ? Budget.When(w2, Side.Player, now) : "done"));
            grid.AddChild(when);
            grid.AddChild(item.Paused
                ? Small("Start it", () => _screen.IssueNow(new PauseItemCommand(Side.Player, item.Key, false), "starting an item"), "Start it again: it is paid in its place in the list")
                : Small("Pause", () => _screen.IssueNow(new PauseItemCommand(Side.Player, item.Key, true), "pausing an item"),
                    "It keeps its place in the list and holds nothing; its share goes to the next one down"));
            var isGoal = bs.Pots[(int)pot].GoalKey == item.Key;
            grid.AddChild(Small(isGoal ? "Goal ✓" : "Goal", () => _screen.IssueNow(isGoal
                    ? new SetGoalCommand(Side.Player, pot, null)
                    : new SetGoalCommand(Side.Player, pot, item.Key, item.What + (where.Length > 0 ? $" at {where}" : ""), item.Cost, item.BodyId), "setting a goal"),
                isGoal ? "Drop the goal" : "Save this pot for it: nothing else may spend it until it is paid"));
        }
        // Cut-off places with items in this pot.
        foreach (var g in items.Where(i => i.Blocked && i.BodyId != null).GroupBy(i => i.BodyId!.Value))
        {
            if (w.FindBody(g.Key) is not Body place) continue;
            var mode = bs.Places.GetValueOrDefault(place.Id);
            var row = Row();
            row.AddChild(Spacer(28));
            var note = Ui.Label(mode switch
            {
                PlaceMode.Hold => $"{place.Name} is cut off: its part of this pot is held for it",
                PlaceMode.ShareOut => $"{place.Name} is cut off: its part of this pot goes to the rest of the list",
                _ => $"{place.Name} is cut off, so its part of this pot is paused and rolls to Reserve until the blockade lifts",
            }, 12, Ui.Danger);
            note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            note.CustomMinimumSize = new Vector2(300, 0);
            row.AddChild(note);
            var id = place.Id;
            if (mode != PlaceMode.ShareOut)
                row.AddChild(Small("Share it out again", () => _screen.IssueNow(new SetPlaceModeCommand(Side.Player, id, PlaceMode.ShareOut), "sharing a place's Industry out"),
                    "Let the rest of this pot's list have it while the place is cut off"));
            if (mode != PlaceMode.Hold)
                row.AddChild(Small($"Hold it for {place.Name}", () => _screen.IssueNow(new SetPlaceModeCommand(Side.Player, id, PlaceMode.Hold), "holding Industry for a place"),
                    "Keep it in this pot for the place, ready when the blockade lifts"));
            if (mode != PlaceMode.Roll)
                row.AddChild(Small("Roll to Reserve", () => _screen.IssueNow(new SetPlaceModeCommand(Side.Player, id, PlaceMode.Roll), "rolling a place's Industry to Reserve"),
                    "Back to the default: its part goes to Reserve"));
        }
        var rename = new LineEdit { Text = bs.Pots[(int)pot].Name, MaxLength = 32, CustomMinimumSize = new Vector2(200, 26), PlaceholderText = "Pot name (Enter)" };
        rename.AddThemeFontSizeOverride("font_size", 12);
        rename.TextSubmitted += text => _screen.IssueNow(new RenamePotCommand(Side.Player, pot, text), "renaming a pot");
        var renameRow = Row();
        renameRow.AddChild(Spacer(28));
        renameRow.AddChild(Ui.Label("Name", 12, Ui.Dim));
        renameRow.AddChild(rename);
        if (pot != Pot.Reserve)
            renameRow.AddChild(Small("Give savings to Reserve", () => _screen.IssueNow(new MoveIndustryCommand(Side.Player, pot, Pot.Reserve, -1), "moving Industry"),
                "Move everything this pot has saved back into Reserve"));
    }

    // ---- By place ----------------------------------------------------------------------------------

    private void ByPlace(World w)
    {
        Text("What each place earns a day against what has been spent there a day (30-day average). A colony that earns well and gets nothing back shows up here. "
             + "Click a place to open it.", 12);
        var grid = new GridContainer { Columns = 5 };
        grid.AddThemeConstantOverride("h_separation", 14);
        _box.AddChild(grid);
        foreach (var h in new[] { "Place", "Earns", "Spent there", "On what", "Balance" }) grid.AddChild(Ui.Label(h, 11, Ui.Dim));
        foreach (var (body, _, _, _) in Budget.Places(w, Side.Player))
        {
            var id = body.Id;
            grid.AddChild(Small(body.Name, () => _screen.OpenPlanet(id), "Open the planet"));
            Cell(grid, w2 => Row(w2, id) is var r && r != null ? $"{r.Value.Earns:0.0}" : "");
            Cell(grid, w2 => Row(w2, id) is var r && r != null ? $"{r.Value.Spends:0.0}" : "");
            var on = Ui.Label("", 12, Ui.Dim);
            on.CustomMinimumSize = new Vector2(190, 0);
            on.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _live.Add((on, w2 => Row(w2, id)?.On ?? ""));
            grid.AddChild(on);
            Cell(grid, w2 => Row(w2, id) is var r && r != null
                ? r.Value.Spends >= r.Value.Earns ? $"+{r.Value.Spends - r.Value.Earns:0.0} a day in" : $"{r.Value.Earns - r.Value.Spends:0.0} a day out"
                : "");
        }
        static (double Earns, double Spends, string On)? Row(World w2, int id) =>
            Budget.Places(w2, Side.Player).Where(p => p.Body.Id == id).Select(p => ((double Earns, double Spends, string On)?)(p.Earns, p.Spends, p.On)).FirstOrDefault();
    }

    // ---- By project --------------------------------------------------------------------------------

    private void ByProject(World w)
    {
        var bs = Budget.Of(w, Side.Player);
        Text("Queued items and the next station of the line at each of your worlds. \"Goal\" sets that pot to save for it: nothing else may spend it until it is paid.", 12);
        var grid = new GridContainer { Columns = 6 };
        grid.AddThemeConstantOverride("h_separation", 12);
        _box.AddChild(grid);
        foreach (var h in new[] { "Project", "Pot", "Cost", "Saved in pot", "When", "" }) grid.AddChild(Ui.Label(h, 11, Ui.Dim));
        foreach (var (pot, key, name, cost, bodyId) in Budget.Projects(w, Side.Player))
        {
            var p = pot;
            var k = key;
            var isGoal = bs.Pots[(int)pot].GoalKey == key;
            var label = Ui.Label(name, 12, isGoal ? Ui.Rival : Ui.Text);
            label.CustomMinimumSize = new Vector2(210, 0);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            grid.AddChild(label);
            grid.AddChild(Ui.Label(Budget.Short(pot), 12, Ui.Dim));
            grid.AddChild(Ui.Label($"{cost:0}", 12, Ui.Text));
            Cell(grid, w2 => $"{Budget.Of(w2, Side.Player).Pots[(int)p].Saved:0}");
            var when = Ui.Label("", 12, Ui.Dim);
            when.CustomMinimumSize = new Vector2(140, 0);
            when.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _live.Add((when, w2 => Budget.Items(w2, Side.Player).Find(i => i.Key == k) is BudgetItem it ? Budget.When(w2, Side.Player, it)
                : Budget.Free(w2, Side.Player, p, k) >= cost ? "can be paid now" : $"in {Budget.Days(w2, Side.Player, p, cost - Budget.Free(w2, Side.Player, p, k)).Replace("about ", "")}"));
            grid.AddChild(when);
            var (n, c, b) = (name, cost, bodyId);
            grid.AddChild(Small(isGoal ? "Goal ✓" : "Goal", () => _screen.IssueNow(isGoal ? new SetGoalCommand(Side.Player, p, null) : new SetGoalCommand(Side.Player, p, k, n, c, b), "setting a goal"),
                isGoal ? "Drop the goal" : $"Save the {Budget.Of(w, Side.Player).Pots[(int)p].Name} pot for it"));
        }
    }

    // ---- Reserve and control -----------------------------------------------------------------------

    private void ReserveCard(World w)
    {
        Heading("Moving the Reserve");
        var line = Ui.Label("", 12, Ui.Dim);
        _live.Add((line, w2 => $"Reserve has {Budget.Of(w2, Side.Player).Pots[(int)Pot.Reserve].Saved:0} saved · {Budget.PerDay(w2, Side.Player, Pot.Reserve):0.#} a day, plus what full pots cannot hold"));
        _box.AddChild(line);
        var row = Row();
        row.AddChild(Ui.Label("Send", 13, Ui.Text));
        foreach (var amount in new double[] { 25, 50, 100, -1 })
        {
            var a = amount;
            row.AddChild(Small(a < 0 ? "All" : $"{a:0}", () => _screen.IssueNow(new MoveIndustryCommand(Side.Player, Pot.Reserve, _sendTo, a), "moving Industry"),
                a < 0 ? "Send all of Reserve" : $"Send {a:0} from Reserve (or what it has)"));
        }
        row.AddChild(Ui.Label("to", 13, Ui.Text));
        var pick = new OptionButton { FocusMode = Godot.Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(190, 26) };
        pick.AddThemeFontSizeOverride("font_size", 12);
        var bs = Budget.Of(w, Side.Player);
        var targets = Budget.All.Where(p => p != Pot.Reserve).ToList();
        foreach (var p in targets) pick.AddItem(bs.Pots[(int)p].Name);
        pick.Selected = Math.Max(0, targets.IndexOf(_sendTo));
        pick.ItemSelected += i => _sendTo = targets[(int)i];
        row.AddChild(pick);
    }

    private void Control(World w)
    {
        var bs = Budget.Of(w, Side.Player);
        Heading("Who sets the shares");
        var row = Row();
        var now = Stewards.LevelFor(w, Side.Player, AutoArea.Budget);
        foreach (var level in Enum.GetValues<AutoLevel>())
        {
            var l = level;
            var b = Small(level.ToString(), () => _screen.IssueNow(new SetAutoLevelCommand(Side.Player, AutoArea.Budget, l), "setting automation"), level switch
            {
                AutoLevel.Auto => "Automation sets the shares from what is queued and what is under threat, and notes each move",
                AutoLevel.Advise => "It works out the shares it wants and offers them in Needs you as one line you can accept",
                _ => "Your shares stand until you change them, even if something waits",
            });
            b.CustomMinimumSize = new Vector2(70, 26);
            if (level == now) b.Modulate = Ui.Player;
            row.AddChild(b);
        }
        var last = Ui.Label("", 12, Ui.Dim);
        last.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        last.CustomMinimumSize = new Vector2(Width, 0);
        _live.Add((last, w2 => Budget.Of(w2, Side.Player) is { LastMove: string m } b2 ? $"Last move ({GameTime.Format(b2.LastMoveHour)}): {m}" : "No share moved yet. Locked pots and goals are never overridden."));
        _box.AddChild(last);
        var over = Row();
        over.AddChild(Ui.Label("When a pot is full, the extra goes to", 13, Ui.Text));
        var pick = new OptionButton { FocusMode = Godot.Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(190, 26) };
        pick.AddThemeFontSizeOverride("font_size", 12);
        foreach (var p in Budget.All) pick.AddItem(bs.Pots[(int)p].Name);
        pick.Selected = (int)bs.OverflowTo;
        pick.ItemSelected += i => _screen.IssueNow(new SetOverflowCommand(Side.Player, (Pot)(int)i), "setting the overflow pot");
        over.AddChild(pick);
        Text($"A pot is full at {Budget.D.FullDays:0} days of its share (or its goal, or all its items, if more). Urgent defence may always draw on Reserve, and says so in the log.", 12);
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private void Heading(string text) => _box.AddChild(Ui.Label(text, 15, Ui.Player));

    private void Text(string text, int size = 13, Color? color = null)
    {
        var l = Ui.Label(text, size, color ?? Ui.Dim);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(Width, 0);
        _box.AddChild(l);
    }

    private HBoxContainer Row()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        _box.AddChild(row);
        return row;
    }

    private void Indent(Godot.Control c)
    {
        var row = Row();
        row.AddChild(Spacer(28));
        row.AddChild(c);
    }

    private static Godot.Control Spacer(float width) => new() { CustomMinimumSize = new Vector2(width, 0) };

    private void Cell(GridContainer grid, Func<World, string> text)
    {
        var l = Ui.Label("", 12, Ui.Text);
        l.HorizontalAlignment = HorizontalAlignment.Right;
        l.CustomMinimumSize = new Vector2(56, 0);
        grid.AddChild(l);
        _live.Add((l, text));
    }

    private static Button Small(string text, Action pressed, string tip)
    {
        var b = Ui.SmallButton(text, pressed, tip);
        b.CustomMinimumSize = new Vector2(0, 26);
        b.AddThemeFontSizeOverride("font_size", 12);
        return b;
    }
}
