using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The Organise tab of the fleets screen (K): every fleet of yours as a column with its ships. Click a ship, then
/// "Join here" on the fleet it should go to. Fleets at the same place swap at once; fleets apart get a planned
/// transfer (trip time, fuel, what the sender keeps; long trips warn and can wait until the fleets are closer) that
/// is sent with Confirm. Below, the supply web: which supply fleets look after which fleet. Works while paused.
/// </summary>
public partial class FleetScreen
{
    private (int Fleet, int Ship)? _orgPick;
    private readonly List<PlannedMove> _planned = new();

    private sealed class PlannedMove
    {
        public int From;
        public int Ship;
        public int To;
        public bool Wait;
    }

    /// <summary>A view asked for from outside (the screenshot run): a picked ship and planned transfers (from, ship, to, wait).</summary>
    private static ((int Fleet, int Ship)? Pick, List<(int From, int Ship, int To, bool Wait)> Planned)? _orgWanted;

    public static void ShowOrganise((int Fleet, int Ship)? pick, List<(int From, int Ship, int To, bool Wait)> planned) => _orgWanted = (pick, planned);

    private void TakeOrganiseWanted()
    {
        if (_orgWanted is not var (pick, planned)) return;
        _orgWanted = null;
        _orgPick = pick;
        _planned.Clear();
        _planned.AddRange(planned.Select(p => new PlannedMove { From = p.From, Ship = p.Ship, To = p.To, Wait = p.Wait }));
    }

    private string OrganiseShape(World w) =>
        $"{_orgPick}|" + string.Join(";", _planned.Select(p => $"{p.From}>{p.Ship}>{p.To}:{p.Wait}"))
        + "|" + string.Join(";", w.Fleets.Where(f => f.Side == Side.Player).Select(f => $"{f.Id}:{f.Locked}:{f.Transfers.Count}:{f.TransferTo}:{f.Role}:{f.SupplyRunFor}:{string.Join(",", f.SupplyFirst)}"));

    private void BuildOrganise(World w, List<Fleet> fleets)
    {
        // Drop plans whose ships or fleets are gone or moved.
        _planned.RemoveAll(p => w.FindFleet(p.From) is not Fleet f || !f.ShipIds.Contains(p.Ship) || w.FindFleet(p.To) is not Fleet t || t.ShipIds.Count == 0);
        if (_orgPick is (int pf, int ps) && (w.FindFleet(pf) is not Fleet pff || !pff.ShipIds.Contains(ps))) _orgPick = null;

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var page = Column(12);
        page.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(page);
        _root.AddChild(scroll);

        var hint = _orgPick is (int hf, int hs) && w.FindShip(hs) is Ship picked
            ? $"{picked.Name} picked: press Join here on the fleet it should go to (or click it again to put it back)"
            : "Click a ship, then Join here on the fleet it should join. Fleets at the same place swap at once; far ones are planned below. Works while paused.";
        page.AddChild(Ui.Label(hint, 13, _orgPick != null ? Warn : Ui.Dim));

        var cols = new HFlowContainer();
        cols.AddThemeConstantOverride("h_separation", 10);
        cols.AddThemeConstantOverride("v_separation", 10);
        foreach (var f in fleets) cols.AddChild(OrgColumn(w, f));
        page.AddChild(cols);
        page.AddChild(PlannedBox(w));
        page.AddChild(SupplyWeb(w, fleets));
    }

    private Control OrgColumn(World w, Fleet f)
    {
        var id = f.Id;
        var col = Column(4);
        col.CustomMinimumSize = new Vector2(250, 0);
        var head = Row(6);
        var name = Ui.Label(f.Name, 15, Ui.Player);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(name);
        if (f.Locked) head.AddChild(Ui.Label("Locked", 12, Warn));
        col.AddChild(head);
        col.AddChild(Ui.Label($"{RoleChip(f.Role)} · {f.ShipIds.Count} ship{(f.ShipIds.Count == 1 ? "" : "s")}", 12, RoleColor(f.Role)));
        col.AddChild(Live(w2 => w2.FindFleet(id) is Fleet g ? Short(FleetPanel.DoingNow(w2, g), 40) : "", 11, Ui.Dim));

        // Joining here: with the ship picked elsewhere, how long it would take.
        if (_orgPick is (int from, int ship) && from != id && w.FindFleet(from) is Fleet src)
        {
            var est = Transfers.Estimate(w, src, new[] { ship }, f);
            var row = Row(6);
            if (f.Locked)
                row.AddChild(Ui.Label("Locked: unlock it to add ships", 12, Ui.Danger));
            else if (est.Problem != null)
                row.AddChild(Ui.Label($"Can't: {est.Problem}", 12, Ui.Danger));
            else
            {
                var text = est.Together ? "Join here · at once" : $"Join here · {Transfers.Dur(est.Hours)} · {est.FuelShare:P0} fuel";
                row.AddChild(Ui.SmallButton(text, () => Plan(from, ship, id), est.Together
                    ? "Both fleets are here: the ship moves now"
                    : "Plan this transfer: it flies over at 70 % speed and joins on arrival. Confirm it below"));
            }
            col.AddChild(row);
        }

        foreach (var s in w.ShipsOf(f).ToList())
        {
            var shipId = s.Id;
            var leaving = _planned.Find(p => p.Ship == shipId);
            var label = $"{s.Name}  ·  {Fitting.Spec(s).Name}" + (leaving != null ? $"  → {w.FindFleet(leaving.To)?.Name}" : "");
            var b = new Button { Text = label, ToggleMode = true, FocusMode = FocusModeEnum.None, Alignment = HorizontalAlignment.Left,
                TooltipText = leaving != null ? "Planned to move: remove it from the list below to change it" : "Pick this ship to move it to another fleet" };
            b.AddThemeFontSizeOverride("font_size", 13);
            b.SetPressedNoSignal(_orgPick is (int pf, int ps) && pf == id && ps == shipId);
            b.Disabled = leaving != null;
            if (leaving != null) b.Modulate = Warn;
            b.Pressed += () => _orgPick = _orgPick is (int pf, int ps) && pf == id && ps == shipId ? null : (id, shipId);
            col.AddChild(b);
        }
        foreach (var p in _planned.Where(p => p.To == id))
            if (w.FindShip(p.Ship) is Ship coming) col.AddChild(Ui.Label($"+ {coming.Name} (planned, from {w.FindFleet(p.From)?.Name})", 12, Warn));
        foreach (var line in Transfers.Status(w, f)) col.AddChild(Ui.Label(Short(line, 60), 11, new Color("9fd6ff")));
        return Box(col, 10);
    }

    private static string Short(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>Picked ship to that fleet: at once when they are together, else onto the planned list.</summary>
    private void Plan(int from, int ship, int to)
    {
        var w = _screen.World;
        _orgPick = null;
        if (w.FindFleet(from) is not Fleet src || w.FindFleet(to) is not Fleet dest) return;
        if (Transfers.Estimate(w, src, new[] { ship }, dest).Together)
        {
            _screen.IssueNow(new TransferShipsCommand(from, new List<int> { ship }, to), "moving ships");
            return;
        }
        _planned.RemoveAll(p => p.Ship == ship);
        _planned.Add(new PlannedMove { From = from, Ship = ship, To = to });
    }

    private Control PlannedBox(World w)
    {
        var col = Column(6);
        var head = Row(10);
        head.AddChild(Heading("Planned transfers"));
        head.AddChild(Ui.Label("Nothing moves until you confirm. The order goes to the fleet giving the ships, at light speed", 12, Ui.Dim));
        col.AddChild(head);
        if (_planned.Count == 0)
        {
            col.AddChild(Ui.Label("No transfers planned.", 13, Ui.Dim));
            return Box(col);
        }
        var latest = 0.0;
        foreach (var p in _planned.ToList())
        {
            if (w.FindFleet(p.From) is not Fleet src || w.FindFleet(p.To) is not Fleet dest || w.FindShip(p.Ship) is not Ship s) continue;
            var est = Transfers.Estimate(w, src, new[] { p.Ship }, dest);
            latest = Math.Max(latest, est.Hours);
            var keeps = src.ShipIds.Count - _planned.Count(q => q.From == p.From);
            var line = Column(2);
            var top = Row(8);
            var text = Ui.Label($"{s.Name}: {src.Name} → {dest.Name}", 14, Ui.Text);
            text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            top.AddChild(text);
            if (est.Long)
            {
                var wait = new CheckBox { Text = "Wait until closer", ButtonPressed = p.Wait, FocusMode = FocusModeEnum.None,
                    TooltipText = $"Stay with {src.Name} until {dest.Name} is within a day, then fly over (you are told when it sets off)" };
                wait.AddThemeFontSizeOverride("font_size", 13);
                var pm = p;
                wait.Toggled += on => pm.Wait = on;
                top.AddChild(wait);
            }
            var rm = p;
            top.AddChild(Ui.SmallButton("Remove", () => _planned.Remove(rm), "Take this transfer off the list"));
            line.AddChild(top);
            var detail = est.Problem != null ? $"Can't: {est.Problem}"
                : $"{Transfers.Dur(est.Hours)} at 70 % speed · {est.FuelShare:P0} of its fuel · {src.Name} keeps {keeps} ship{(keeps == 1 ? "" : "s")}"
                  + (keeps == 0 ? " (that fleet ends)" : "");
            line.AddChild(Ui.Label(detail, 12, est.Problem != null ? Ui.Danger : Ui.Dim));
            if (est.Long)
                line.AddChild(Ui.Label(p.Wait
                    ? $"Waits with {src.Name} until {dest.Name} is within a day, then flies over."
                    : $"Long trip: {Transfers.Dur(est.Hours)} alone, with no escort. Tick Wait until closer to keep it with {src.Name} until then.", 12, p.Wait ? new Color("9fd6ff") : Warn));
            col.AddChild(line);
        }
        var row = Row(8);
        row.AddChild(Ui.SmallButton("Confirm transfers", ConfirmPlanned, "Send these orders now (works while paused; they land when the light reaches each fleet)"));
        row.AddChild(Ui.SmallButton("Clear", () => _planned.Clear(), "Forget the planned transfers"));
        row.AddChild(Ui.Label($"{_planned.Count} planned · the last arrives in about {Transfers.Dur(latest)}", 12, Ui.Dim));
        col.AddChild(row);
        return Box(col);
    }

    private void ConfirmPlanned()
    {
        foreach (var g in _planned.GroupBy(p => (p.From, p.To, p.Wait)).ToList())
            _screen.IssueNow(new TransferShipsCommand(g.Key.From, g.Select(p => p.Ship).ToList(), g.Key.To, g.Key.Wait), "transfer");
        _planned.Clear();
        Main.Instance.Toast("Transfers sent: each fleet shows its stage");
    }

    // ---- Supply web ----------------------------------------------------------------------------

    private Control SupplyWeb(World w, List<Fleet> fleets)
    {
        var col = Column(6);
        var head = Row(10);
        head.AddChild(Heading("Supply web"));
        head.AddChild(Ui.Label("Who keeps each fleet supplied. Rows are fleets, columns are your supply fleets", 12, Ui.Dim));
        col.AddChild(head);
        var suppliers = fleets.Where(f => f.Role == FleetRole.Supply || HelpCalls.SupplyOnly(w, f)).ToList();
        var served = fleets.Where(f => !HelpCalls.SupplyOnly(w, f)).ToList();
        if (served.Count == 0)
        {
            col.AddChild(Ui.Label("No fleets to supply.", 13, Ui.Dim));
            return Box(col);
        }
        var grid = new GridContainer { Columns = 3 + suppliers.Count };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 6);
        grid.AddChild(Ui.Label("Fleet", 12, Ui.Dim));
        grid.AddChild(Ui.Label("Own tender", 12, Ui.Dim));
        foreach (var s in suppliers) grid.AddChild(Ui.Label(s.Name, 12, Ui.Dim));
        grid.AddChild(Ui.Label("Verdict", 12, Ui.Dim));
        foreach (var f in served)
        {
            var fid = f.Id;
            grid.AddChild(Ui.Label(f.Name, 13));
            grid.AddChild(Live(w2 => w2.FindFleet(fid) is Fleet g
                ? string.Join(", ", SupplyCover.Of(w2, g).Sources.Where(x => x.Kind == "tender").Select(x => x.Name)) is { Length: > 0 } t ? t : "none"
                : "", 12));
            foreach (var s in suppliers)
            {
                var sid = s.Id;
                grid.AddChild(Live(w2 =>
                {
                    if (w2.FindFleet(fid) is not Fleet g || w2.FindFleet(sid) is not Fleet sup) return "";
                    if (sup.SupplyRunFor == g.Id) return sup.TenderOf == g.Id ? "its tender, restocking" : "running to it now";
                    var src = SupplyCover.Of(w2, g).Sources.Find(x => x.FleetId == sid);
                    return src == null ? "—" : Short(src.Brief.Length > 0 ? src.Brief : src.State, 40);
                }, 12, new Color("c7a0ff")));
            }
            grid.AddChild(Live(w2 => w2.FindFleet(fid) is Fleet g ? SupplyCover.Word(SupplyCover.Of(w2, g).Verdict) : "", 13));
        }
        var hs = new ScrollContainer { VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 40 + 26 * served.Count) };
        hs.AddChild(grid);
        col.AddChild(hs);
        col.AddChild(Ui.Label("Covered: a supply fleet is on a run or in range and free. Thin: only its own tender, or its supplier is busy. None: it must go home to refill. "
                              + "Set a supply fleet's \"Serve first\" list on its Fleets list page.", 12, Ui.Dim));
        return Box(col);
    }

    // ---- Role chips ----------------------------------------------------------------------------

    private static string RoleChip(FleetRole r) => HelpCalls.RoleName(r);

    private static Color RoleColor(FleetRole r) => r switch
    {
        FleetRole.Defence => new Color("6fc3ff"),
        FleetRole.AntiPiracy => new Color("ff9c6b"),
        FleetRole.Support => new Color("ffd36b"),
        FleetRole.Supply => new Color("c7a0ff"),
        FleetRole.ForwardGuard => new Color("7fe0c4"),
        FleetRole.Scout => new Color("b8c4d6"),
        _ => Ui.Dim,
    };
}
