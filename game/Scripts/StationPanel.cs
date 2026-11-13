using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The station over a planet (the station line: platform, defence station, starbase, fortress, orbital ring):
/// what it is and does, build or upgrade it, swap its loadout, its defence plan, and scuttle it. Used by the
/// planet screen; the empire panel shows the short form. Only draws and issues commands.
/// </summary>
public static class StationPanel
{
    /// <summary>What the panel shows that needs a rebuild when it changes (live text updates on its own).</summary>
    public static string Shape(World w, Body b)
    {
        var st = Starbases.At(w, b.Id);
        return st == null ? $"none|{Starbases.TopTier(w, Side.Player)}|{b.Owner}"
            : $"{st.Id}|{st.Side}|{st.ClassId}|{st.DesignId}|{st.Refit?.ClassId}|{w.Hour >= st.ReadyHour}|{st.Fire}|{st.ScuttleIfTaken}|{Starbases.TopTier(w, Side.Player)}"
              + $"|{Designer.Of(w, Side.Player, st.ClassId).Count}";
    }

    /// <summary>The full panel. live: adds a label whose text follows the world; issue: sends a command at once.</summary>
    public static Control Build(World w, Body b, Func<Func<World, string>, int, Color?, Label> live, Action<GameCommand, string> issue)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        col.AddChild(Ui.Label("Station", 16));
        col.AddChild(live(w2 => $"Yard here: {Starbases.YardName(Starbases.YardLength(w2, Side.Player, b))}"
                                + (Starbases.YardLength(w2, Side.Player, b) is var m and > 0 ? $" (hulls up to {m:0} m)" : " (no ships built here)"), 13, Ui.Dim));
        var st = Starbases.At(w, b.Id);
        if (st != null && st.Side != Side.Player)
        {
            col.AddChild(Wrap($"An enemy {Starbases.ClassOf(st!).Name.ToLowerInvariant()} holds this orbit. Your ground guns keep firing at it; board it from low orbit to take it back.", Ui.Rival));
            col.AddChild(live(w2 => Starbases.At(w2, b.Id) is Starbase s ? Starbases.Summary(w2, s) : "Gone", 13, Ui.Rival));
            return col;
        }
        if (b.Owner != Side.Player) return col;
        if (st == null)
        {
            col.AddChild(Wrap($"No station over {b.Name}. A station guards the orbit, serves your fleets holding low orbit under it (refuel, rearm, repairs from tier 3), and its slipway lets this world build longer hulls.", Ui.Dim));
            var row = new HFlowContainer();
            row.AddThemeConstantOverride("h_separation", 6);
            foreach (var t in Starbases.D.Tiers)
            {
                var cls = GameData.ShipClass(t.ClassId);
                var why = Starbases.CanBuild(w, Side.Player, b.Id, t.ClassId);
                if (why != null && why.StartsWith("Research", StringComparison.Ordinal) && t.Tier > Starbases.TopTier(w, Side.Player) + 1) continue;
                var id = t.ClassId;
                var button = Ui.SmallButton($"{cls.Name} · {cls.Cost:0}", () => issue(new BuildStarbaseCommand(Side.Player, b.Id, id), "building a station"),
                    (why != null ? why + ". " : "") + $"Tier {t.Tier} ({t.Stage}): {cls.Cost:0} Industry, {t.Days:0} days. {t.Blurb} Building a higher tier straight away costs its full price; upgrading later costs the difference.");
                button.Disabled = why != null;
                row.AddChild(button);
            }
            col.AddChild(row);
            return col;
        }

        var stId = st.Id;
        col.AddChild(live(w2 => w2.Starbases.Find(s => s.Id == stId) is Starbase s ? Starbases.Summary(w2, s) : "Gone", 13, null));
        col.AddChild(live(w2 => w2.Starbases.Find(s => s.Id == stId) is Starbase s && Starbases.BayNotes(w2, s) is { Count: > 0 } notes
            ? "Bays: " + string.Join("; ", notes) : "No bays fitted", 12, Ui.Dim));
        col.AddChild(live(w2 => w2.Starbases.Find(s => s.Id == stId) is Starbase s
            ? $"Loadout: {Designer.NameOf(w2, Side.Player, s.DesignId)} · point defence {Starbases.Spec(s).PointDefence} (covers your fleets within {Starbases.D.UmbrellaMm:0} Mm)"
              + (Starbases.Tier(s) >= Starbases.D.LandingFactorTier ? $" · troops landing need {Starbases.D.LandingFactor:0}x the garrison" : "")
              + $" · {Starbases.GarrisonK(w2, s):0.#}k hold it against boarders"
              + (w2.Boardings.Any(x => x.Outcome == null && x.StationId == s.Id) ? " · BEING BOARDED" : "")
              + (s.LastHitHour is double hit && w2.Hour - hit < 2 ? " · UNDER FIRE" : "") : "", 12, Ui.Dim));

        var actions = new HFlowContainer();
        actions.AddThemeConstantOverride("h_separation", 6);
        if (Starbases.NextTier(st) is StationTier up)
        {
            var cls = GameData.ShipClass(up.ClassId);
            var design = Starbases.SameVariant(w, Side.Player, st, up.ClassId);
            var why = Starbases.CanRefit(w, Side.Player, st, up.ClassId, design);
            var cost = Starbases.UpgradeCost(st, up.ClassId);
            var button = Ui.SmallButton($"Upgrade to {cls.Name.ToLowerInvariant()} · {cost:0}", () => issue(new RefitStarbaseCommand(Side.Player, stId, up.ClassId, design), "upgrading a station"),
                (why != null ? why + ". " : "") + $"Pays the difference ({cost:0} Industry) and takes {up.Days / 2:0} days; the station keeps working meanwhile. {up.Blurb}");
            button.Disabled = why != null;
            actions.AddChild(button);
        }
        // Loadouts: the Standard and the variants of this tier; picking one is a bay swap (a refit).
        var variants = new List<(int Id, string Name)> { (Designer.StandardId, "Standard") };
        variants.AddRange(Designer.Of(w, Side.Player, st.ClassId).Select(d => (d.Id, d.Name)));
        var loadout = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "Refit the station to another loadout: each new part at full price, half a day a part; the bays being swapped are out until it is done. Edit loadouts in the Designs screen (J)." };
        loadout.AddThemeFontSizeOverride("font_size", 14);
        for (var i = 0; i < variants.Count; i++)
        {
            loadout.AddItem($"Loadout: {variants[i].Name}", i);
            if (variants[i].Id == st.DesignId) loadout.Selected = i;
        }
        loadout.Disabled = st.Refit != null || w.Hour < st.ReadyHour;
        loadout.ItemSelected += i => Main.Instance.RunGuarded("loadout", () =>
        {
            var pick = variants[(int)i].Id;
            if (w.Starbases.Find(s => s.Id == stId) is not Starbase s) return;
            if (Starbases.CanRefit(w, Side.Player, s, s.ClassId, pick) is string problem) { Main.Instance.Toast(problem); return; }
            issue(new RefitStarbaseCommand(Side.Player, stId, s.ClassId, pick), "refitting a station");
        });
        actions.AddChild(loadout);
        col.AddChild(actions);

        // The defence plan.
        var plan = new HFlowContainer();
        plan.AddThemeConstantOverride("h_separation", 6);
        var fire = new OptionButton { FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Both orbits: guns and missiles reach low and high orbit. Low orbit only: hides how far they reach until it matters. Hold fire: keep it quiet." };
        fire.AddThemeFontSizeOverride("font_size", 14);
        foreach (var (label, f) in new[] { ("Fire at both orbits", StationFire.Both), ("Fire at low orbit only (hide the reach)", StationFire.LowOnly), ("Hold fire", StationFire.Hold) })
        {
            fire.AddItem(label, (int)f);
            if (st.Fire == f) fire.Selected = fire.ItemCount - 1;
        }
        fire.ItemSelected += i => Main.Instance.RunGuarded("station fire", () =>
            issue(new SetStationPlanCommand(Side.Player, stId, (StationFire)fire.GetItemId((int)i), st.ScuttleIfTaken), "station plan"));
        plan.AddChild(fire);
        var scuttleRule = new CheckBox { Text = "Scuttle rather than lose it", ButtonPressed = st.ScuttleIfTaken, FocusMode = Control.FocusModeEnum.None,
            TooltipText = "If boarders win, the garrison blows the station up: its stores are lost, but nobody takes it or the hull on its slipway" };
        scuttleRule.Toggled += on => Main.Instance.RunGuarded("station scuttle rule", () => issue(new SetStationPlanCommand(Side.Player, stId, st.Fire, on), "station plan"));
        plan.AddChild(scuttleRule);
        Button? scuttle = null;
        scuttle = Ui.SmallButton("Scuttle…", () =>
        {
            if (scuttle!.Text != "Click again to blow it up") { scuttle.Text = "Click again to blow it up"; return; }
            issue(new ScuttleStarbaseCommand(Side.Player, stId), "scuttling a station");
        }, "Blow the station up now: its stores are lost, but nobody can take it");
        scuttle.Modulate = Ui.Danger;
        plan.AddChild(scuttle);
        col.AddChild(plan);
        return col;
    }

    /// <summary>One line for the empire panel: the station or what can be built.</summary>
    public static string Short(World w, Body b)
    {
        if (Starbases.At(w, b.Id) is not Starbase st) return Starbases.TopTier(w, Side.Player) > 0 ? "no station" : "";
        if (st.Side != Side.Player) return $"ENEMY {Starbases.ClassOf(st).Name.ToLowerInvariant()} in orbit";
        if (w.Hour < st.ReadyHour) return $"{Starbases.ClassOf(st).Name}: {Format.Hours(st.ReadyHour - w.Hour)}";
        return $"{Starbases.ClassOf(st).Name} ✓ {100 * st.Armour / Math.Max(1, Starbases.MaxArmour(st)):0} %"
               + (st.Refit != null ? " (refit)" : "") + (st.LastHitHour is double hit && w.Hour - hit < 2 ? " UNDER FIRE" : "");
    }

    private static Label Wrap(string text, Color? color = null)
    {
        var l = Ui.Label(text, 13, color ?? Ui.Text);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(240, 0);
        return l;
    }
}
