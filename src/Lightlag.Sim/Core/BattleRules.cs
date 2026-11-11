using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>Pull-back presets: Cautious, Standard (the default), Fight on, To the last; Custom for a hand-made rule.</summary>
public enum PullPreset { Cautious, Standard, FightOn, ToTheLast, Custom }

/// <summary>Where a fleet pulling back goes: cruise home, fall back to the nearest friendly planet, or regroup at a point.</summary>
public enum PullAction { CruiseHome, NearestPlanet, Regroup }

/// <summary>Who pulls back: the whole fleet, or only its damaged ships.</summary>
public enum PullWho { WholeFleet, DamagedShips }

/// <summary>How a battle ended: one side wiped out (all destroyed), one withdrew, both withdrew, or they drifted apart.</summary>
public enum BattleEnding { Wipe, OneWithdrew, BothWithdrew, BrokeOff }

/// <summary>A fleet's pull-back rule: what makes it pull back, where it goes, who goes, and the ship rule.</summary>
public sealed class PullRule
{
    public PullPreset Preset { get; set; } = PullPreset.Standard;

    /// <summary>Triggers: share of its strength lost since the fight began (1 or more: never), ships crippled or lost (0: off),
    /// low ammunition, the flagship hit, any damage bar (fleet average) below a level (0: off).</summary>
    public double StrengthLost { get; set; } = 1.0 / 3;
    public int ShipsDown { get; set; } = 2;
    public bool LowAmmo { get; set; } = true;
    public bool FlagshipHit { get; set; }
    public double BarBelow { get; set; }

    public PullAction Action { get; set; } = PullAction.CruiseHome;
    public PullWho Who { get; set; } = PullWho.WholeFleet;
    public Vec2? RegroupAt { get; set; }

    /// <summary>The ship rule: a ship below ShipHullBelow hull, without engines, or crippled pulls out alone and cruises home
    /// (with an escort if Escort), rejoining once mended if Rejoin.</summary>
    public bool ShipRule { get; set; } = true;
    public double ShipHullBelow { get; set; } = 1.0 / 3;
    public bool Rejoin { get; set; } = true;
    public bool Escort { get; set; }

    public static PullRule For(PullPreset p) => p switch
    {
        PullPreset.Cautious => new PullRule { Preset = p, StrengthLost = 0.25, ShipsDown = 1, LowAmmo = true, FlagshipHit = true, ShipHullBelow = 0.5 },
        PullPreset.FightOn => new PullRule { Preset = p, StrengthLost = 0.5, ShipsDown = 0, LowAmmo = false, ShipHullBelow = 0.2 },
        PullPreset.ToTheLast => new PullRule { Preset = p, StrengthLost = 1, ShipsDown = 0, LowAmmo = false, ShipRule = false },
        _ => new PullRule { Preset = PullPreset.Standard },
    };

    public PullRule Copy() => (PullRule)MemberwiseClone();

    public static string Name(PullPreset p) => p switch
    {
        PullPreset.Cautious => "Cautious",
        PullPreset.FightOn => "Fight on",
        PullPreset.ToTheLast => "To the last",
        PullPreset.Custom => "Custom",
        _ => "Standard",
    };

    /// <summary>One line for the fleet panel: what makes it pull back.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (StrengthLost < 1) parts.Add($"{StrengthLost:P0} strength lost");
        if (ShipsDown > 0) parts.Add($"{ShipsDown} ship{(ShipsDown == 1 ? "" : "s")} down");
        if (LowAmmo) parts.Add("low ammunition");
        if (FlagshipHit) parts.Add("flagship hit");
        if (BarBelow > 0) parts.Add($"a damage bar under {BarBelow:P0}");
        return parts.Count == 0 ? "never pulls back" : "pulls back at " + string.Join(", ", parts);
    }
}

/// <summary>
/// Pull-back rules in battle. Each fleet has a rule (Standard by default; Auto picks one by the fleet's
/// job, place and odds). A ship that is crippled, without engines, or below its hull line pulls out alone
/// and cruises home, rejoining once mended. A fleet past its limits pulls back; the other side lets it go
/// (no chase unless ordered). "Hold on" ignores the rule until the fight ends; "Pull back now" goes at once.
/// </summary>
public static class BattleRules
{
    /// <summary>Called for every fleet before it steers (see Combat.Steer).</summary>
    /// <summary>How much stronger the enemy must be before a small group (see Combat.SmallGroup) slips away on contact.</summary>
    public const double OutgunnedRatio = 1.5;

    public static void Check(Simulation sim, Fleet f, Fleet? enemy)
    {
        var w = sim.World;
        if (f.ShipIds.Count == 0) return;
        if (enemy != null)
        {
            if (f.FightPower <= 0)
            {
                f.FightPower = Strength(w, f);
                f.FightShips = f.ShipIds.Count;
                f.FightLost = 0;
                f.FightPulledPower = 0;
            }
            f.FightLastHour = w.Hour;
        }
        else if (f.FightPower > 0 && w.Hour - f.FightLastHour > 1)
        {
            f.FightPower = 0;
            f.HoldOn = false;
        }
        if (f.FightPower <= 0 || f.Retreating) return;
        var ships = w.ShipsOf(f).ToList();
        if (ships.All(s => s.Crippled))
        {
            PullBack(sim, f, ships.Count == 1 ? "is crippled and limps home" : "is crippled to the last ship and limps home", toYard: true);
            return;
        }
        var rule = Effective(f);
        if (rule.ShipRule && ships.Count > 1) ShipRule(sim, f, rule);
        else if (rule.ShipRule && ships.Count == 1 && ShipShouldLeave(w, ships[0], rule) && !f.HoldOn)
        {
            PullBack(sim, f, $"pulls out ({(ships[0].Crippled ? "crippled" : "badly hurt")})", toYard: true);
            return;
        }
        if (f.HoldOn)
        {
            if (w.ActiveBattle is Battle held && held.For(f.Side).Fleets.Contains(f.Name)) held.For(f.Side).HeldOn = true;
            return;
        }
        // Already in orbit where it would pull back to (its yard): there is nowhere further to go, so it stays.
        if (NearestYard(w, f.Side, f.Position) is Body yard && Economy.InOrbitAt(f, yard) && f.Flight == null) return;
        // A lone ship or a pair met by a much stronger enemy slips away at once (small groups get away more easily);
        // one guarding home or set to fight to the last stays.
        if (w.Loop && enemy != null && f.ShipIds.Count <= Combat.SmallGroup && rule.Preset != PullPreset.ToTheLast && !f.Retreating
            && w.Fleets.Where(e => e.Side == enemy.Side && Vec2.Distance(e.Position, f.Position) <= GameData.Combat.ContactRangeMm).Sum(e => Strength(w, e))
               > OutgunnedRatio * Math.Max(1e-9, Strength(w, f)))
        {
            PullBack(sim, f, "is outgunned and slips away");
            return;
        }
        if (Trigger(w, f, rule) is string why) PullBack(sim, f, why);
    }

    /// <summary>The rule in force (the old "don't retreat" standing order means To the last).</summary>
    public static PullRule Effective(Fleet f) => f.RetreatWhenDamaged ? f.Rule : PullRule.For(PullPreset.ToTheLast);

    /// <summary>Strength lost since the fight began (ships that pulled out alone count at what they had left when they went).</summary>
    public static double StrengthLost(World w, Fleet f) => f.FightPower > 0 ? Math.Clamp(1 - (Strength(w, f) + f.FightPulledPower) / f.FightPower, 0, 1) : 0;

    /// <summary>Fighting strength for the pull-back rule: damage counts, missiles fired don't (low ammunition is its own trigger).</summary>
    public static double Strength(World w, Fleet f) => w.ShipsOf(f).Sum(s => Stats.ShipPower(w, s, racks: false));

    /// <summary>Railgun ammunition and missiles left, across the armed ships (1 with nothing to fire).</summary>
    public static double AmmoShare(World w, Fleet f)
    {
        double have = 0, full = 0;
        foreach (var s in w.ShipsOf(f))
        {
            if (SupplyLines.MaxAmmo(s) > 0) { have += Math.Clamp(s.Ammo / SupplyLines.MaxAmmo(s), 0, 1); full++; }
            if (Fitting.Spec(s).Missiles > 0) { have += (double)s.Missiles / Fitting.Spec(s).Missiles; full++; }
        }
        return full > 0 ? have / full : 1;
    }

    /// <summary>Why the rule says pull back now, if it does.</summary>
    public static string? Trigger(World w, Fleet f, PullRule rule)
    {
        var lost = StrengthLost(w, f);
        if (rule.StrengthLost < 1 && lost >= rule.StrengthLost) return $"lost {lost:P0} of its strength";
        var down = f.FightLost + w.ShipsOf(f).Count(s => s.Crippled);
        if (rule.ShipsDown > 0 && down >= rule.ShipsDown) return $"{down} ship{(down == 1 ? "" : "s")} down";
        if (rule.LowAmmo && w.Loop && AmmoShare(w, f) < 0.15) return "low on ammunition";
        if (rule.FlagshipHit && Light.Flagship(w, f.Side) is Ship flag && flag.FleetId == f.Id && Wounds.Share(w, flag, Wounds.Bar.Hull) < 0.9) return "the flagship is hit";
        if (rule.BarBelow > 0)
            foreach (var bar in Wounds.Bars)
                if (w.ShipsOf(f).Average(s => Wounds.Share(w, s, bar)) < rule.BarBelow) return $"{Wounds.BarName(bar)} below {rule.BarBelow:P0}";
        return null;
    }

    /// <summary>A ship that should leave on its own: crippled, engines out, or under the hull line.</summary>
    public static bool ShipShouldLeave(World w, Ship s, PullRule rule) =>
        s.Crippled || s.Out(Module.Engines) || Wounds.Share(w, s, Wounds.Bar.Hull) < rule.ShipHullBelow;

    private static void ShipRule(Simulation sim, Fleet f, PullRule rule)
    {
        var w = sim.World;
        var leaving = w.ShipsOf(f).Where(s => ShipShouldLeave(w, s, rule)).OrderBy(s => s.Id).ToList();
        if (leaving.Count == 0 || leaving.Count == f.ShipIds.Count) return;
        if (rule.Escort && w.ShipsOf(f).Where(s => !leaving.Contains(s) && Fitting.Spec(s).PointDefence > 0).OrderBy(s => Fitting.Spec(s).Cost).ThenBy(s => s.Id).FirstOrDefault() is Ship guard
            && f.ShipIds.Count - leaving.Count > 1)
            leaving.Add(guard);
        var pulled = leaving.Sum(s => Stats.ShipPower(w, s, racks: false));
        var group = Split(sim, f, leaving);
        if (group == null) return;
        f.FightPulledPower += pulled;
        f.FightLost += leaving.Count(s => s.Crippled || ShipShouldLeave(w, s, rule));
        group.RejoinFleetId = rule.Rejoin ? f.Id : null;
        var names = string.Join(", ", leaving.Select(s => s.Name));
        PullBack(sim, group, leaving.Count == 1 ? $"pulls out alone ({(leaving[0].Crippled ? "crippled" : "badly hurt")})" : $"pulls out the damaged ships ({names})", toYard: true, alone: true);
    }

    /// <summary>Split ships off a fleet into a new group with the same rule.</summary>
    private static Fleet? Split(Simulation sim, Fleet f, List<Ship> ships)
    {
        new SplitFleetCommand(f.Id, ships.Select(s => s.Id).ToList()) { Source = "rule" }.Apply(sim.World, sim);
        var g = sim.World.FleetOf(ships[0]);
        return g == null || g.Id == f.Id ? null : g;
    }

    /// <summary>The nearest of the side's yards or dry docks (for crippled and damaged ships), else home.</summary>
    public static Body? NearestYard(World w, Side side, Vec2 from) =>
        w.Bodies.Where(b => b.Owner == side && Colonies.RepairsHere(w, b, side)).OrderBy(b => Vec2.Distance(b.Position, from)).ThenBy(b => b.Id).FirstOrDefault()
        ?? Economy.HomeOf(w, side);

    /// <summary>Pull back: whole fleet or damaged ships, to home (cruising), the nearest friendly planet, or the regroup point.</summary>
    public static void PullBack(Simulation sim, Fleet f, string why, bool toYard = false, bool alone = false, bool byHand = false)
    {
        var w = sim.World;
        var rule = Effective(f);
        if (!alone && !toYard && rule.Who == PullWho.DamagedShips)
        {
            var hurt = w.ShipsOf(f).Where(s => s.Crippled || Wounds.Worst(w, s) < 0.5).OrderBy(s => s.Id).ToList();
            if (hurt.Count > 0 && hurt.Count < f.ShipIds.Count && Split(sim, f, hurt) is Fleet g)
            {
                g.RejoinFleetId = rule.Rejoin ? f.Id : null;
                PullBack(sim, g, $"{why}: the damaged ships pull back", toYard: true, alone: true, byHand: byHand);
                return;
            }
        }
        Body? to = null;
        Vec2? point = null;
        var speed = 0.5; // cruise: saves fuel, and the other side lets it go
        if (toYard || w.ShipsOf(f).Any(s => s.Crippled)) to = NearestYard(w, f.Side, f.Position);
        else if (rule.Action == PullAction.NearestPlanet)
            to = w.Bodies.Where(b => b.Owner == f.Side).OrderBy(b => Vec2.Distance(b.Position, f.Position)).ThenBy(b => b.Id).FirstOrDefault();
        else if (rule.Action == PullAction.Regroup && rule.RegroupAt is Vec2 at) point = at;
        f.PullReason = why;
        f.HoldOn = false;
        Combat.Retreat(sim, f, byHand ? $"pulls back now (by order)" : $"pulls back: {why}", to, point, speed);
        Combat.StandingNote(sim, f, $"{f.Name} pulls back: {why} (rule: {PullRule.Name(rule.Preset)})");
        if (w.Loop) Economy.Of(w, f.Side).Totals.PullBacks++;
        if (w.ActiveBattle is Battle b && b.EndHour == null)
        {
            var side = b.For(f.Side);
            if (!alone || side.Fleets.Contains(f.Name)) side.Withdrew = true;
            if (!alone && side.RailShots + side.MissilesFired == 0 && !side.WithdrewBeforeShots)
            {
                side.WithdrewBeforeShots = true;
                if (w.Loop) Economy.Of(w, f.Side).Totals.PulledBeforeShots++;
            }
        }
    }

    /// <summary>How a battle ended.</summary>
    public static BattleEnding Ending(World w, Battle b)
    {
        // A wipe is total destruction; crippled ships that limp away count as pulling back.
        bool Wiped(BattleSide s) => s.ShipsIn > 0 && s.ShipsLost.Count >= s.ShipsIn;
        var p = b.For(Side.Player);
        var r = b.For(Side.Rival);
        if (Wiped(p) || Wiped(r)) return BattleEnding.Wipe;
        if (p.Withdrew && r.Withdrew) return BattleEnding.BothWithdrew;
        if (p.Withdrew || r.Withdrew) return BattleEnding.OneWithdrew;
        return BattleEnding.BrokeOff;
    }

    public static void OnBattleOver(Simulation sim, Battle b) { }

    /// <summary>
    /// Hourly (full games): ships that pulled out rejoin their fleet once mended at a yard; crippled ships
    /// sitting anywhere else head for the nearest yard.
    /// </summary>
    public static void Hourly(Simulation sim)
    {
        var w = sim.World;
        foreach (var g in w.Fleets.Where(g => g.ShipIds.Count > 0).OrderBy(g => g.Id).ToList())
        {
            if (g.RejoinFleetId is int id && g.Flight == null && !g.Retreating && !Heat.InBattle(w, g)
                && w.ShipsOf(g).All(s => Wounds.Worst(w, s) >= 0.95 && !s.Crippled))
            {
                g.RejoinFleetId = null;
                if (w.FindFleet(id) is Fleet target && target.Side == g.Side && target.ShipIds.Count > 0 && Fleets.FlyToJoin(w, g, target, 1.0) == null)
                {
                    g.JoinFleetId = target.Id;
                    sim.Log.Info("fleet", $"{g.Name} is mended and flies out to rejoin {target.Name}", new { fleetId = g.Id, target = target.Id });
                }
            }
            if (w.ShipsOf(g).Any(s => s.Crippled) && !g.Retreating && g.Flight == null && g.FightPower <= 0
                && NearestYard(w, g.Side, g.Position) is Body yard && !(Economy.InOrbitAt(g, yard) && Colonies.RepairsHere(w, yard, g.Side)))
                Combat.Retreat(sim, g, "has crippled ships and heads for a yard", yard, null, 0.5);
        }
    }

    // ---- Auto (the Battle rules area) ------------------------------------------------------------

    /// <summary>The situation a fleet is in, for Auto's pick and for learning the player's habits.</summary>
    public static string Situation(World w, Fleet f)
    {
        var home = Economy.HomeOf(w, f.Side);
        var at = f.OrbitBodyId is int b ? w.FindBody(b) : null;
        if (at != null && at.Owner == f.Side && Economy.InOrbitAt(f, at) && (at.Id == home?.Id || at.Shipyard || at.Home)) return "home";
        if (at != null && at.Owner == f.Side && Economy.InOrbitAt(f, at) && Economy.Blockaded(w, at)) return "colony";
        if (f.Mission is Mission.Raid or Mission.Ambush or Mission.Hunt) return "raid";
        if (w.ShipsOf(f).All(SupplyLines.IsSupplyShip)) return "support";
        var near = w.Sightings.Where(s => s.SeenBy == f.Side && !s.Own && w.Hour - s.Hour < 12 && Vec2.Distance(s.Position, f.Position) < 0.2 * Physics.MmPerAu).Sum(s => s.Ships);
        if (near >= f.ShipIds.Count * 2) return "outnumbered";
        return "general";
    }

    public static string SituationName(string s) => s switch
    {
        "home" => "home defence",
        "colony" => "colony defence",
        "raid" => "raids",
        "support" => "support ships",
        "outnumbered" => "outnumbered fleets",
        _ => "other fleets",
    };

    /// <summary>Auto's preset for a situation: the side's learned default if any, else raid Cautious, home To the last, a blockaded colony Fight on, else Standard.</summary>
    public static PullPreset AutoPick(World w, Side side, string situation) =>
        Economy.Of(w, side).PullDefaults.TryGetValue(situation, out var p) ? p : BuiltIn(situation);

    /// <summary>The built-in preset for a situation (before anything is learned).</summary>
    public static PullPreset BuiltIn(string situation) => situation switch
        {
            "home" => PullPreset.ToTheLast,
            "colony" => PullPreset.FightOn,
            "raid" or "support" or "outnumbered" => PullPreset.Cautious,
            _ => PullPreset.Standard,
        };

    /// <summary>The Battle rules area: each fleet not set by hand gets Auto's preset (Advise: suggested; Manual: left as is).</summary>
    public static void Steward(Simulation sim, Side side, OfferFn offer)
    {
        var w = sim.World;
        foreach (var f in w.Fleets.Where(f => f.Side == side && f.ShipIds.Count > 0 && !f.RuleByHand && f.FightPower <= 0).OrderBy(f => f.Id).ToList())
        {
            var situation = Situation(w, f);
            var preset = AutoPick(w, side, situation);
            if (f.Rule.Preset == preset) continue;
            offer(AutoArea.Battle, $"rule:{f.Id}", $"{f.Name}: pull back {PullRule.Name(preset)} ({SituationName(situation)})",
                new SetPullRuleCommand(f.Id, preset), $"Use {PullRule.Name(preset)}", fleetId: f.Id);
        }
    }
}

/// <summary>Set a fleet's pull-back rule: a preset, or a hand-made rule (Custom). Null preset and rule: hand it back to Auto.</summary>
public sealed record SetPullRuleCommand(int FleetId, PullPreset? Preset, PullRule? Custom = null) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Pull-back rule") is not Fleet f) return;
        var byHand = Source == "player";
        if (Preset == null && Custom == null)
        {
            f.RuleByHand = false;
            sim.Log.Info("fleet", $"{f.Name} pull-back rule handed back to Auto", new { FleetId, Source });
            return;
        }
        var rule = Custom?.Copy() ?? PullRule.For(Preset!.Value);
        if (Custom != null) rule.Preset = Custom.Preset == PullPreset.Custom || Preset == null ? PullPreset.Custom : Preset.Value;
        if (byHand && world.Loop)
        {
            // Learning (see Stewards): the same hand choice for the same situation, again and again.
            var key = $"pull:{BattleRules.Situation(world, f)}:{rule.Preset}";
            var state = Economy.Of(world, f.Side);
            state.Habits[key] = state.Habits.GetValueOrDefault(key) + 1;
        }
        if (f.Rule.Preset != rule.Preset) Choices.Note(world, f.Side, "pullback", PullRule.Name(rule.Preset), "", Source);
        f.Rule = rule;
        f.RetreatWhenDamaged = true;
        if (byHand) f.RuleByHand = true;
        sim.Log.Info("fleet", $"{f.Name} pull-back rule: {PullRule.Name(rule.Preset)}", new { FleetId, preset = rule.Preset.ToString(), byHand, Source });
    }
}

/// <summary>In a fight: "Hold on" (ignore the pull-back rule until it ends) or not.</summary>
public sealed record HoldOnCommand(int FleetId, bool On) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Hold on order") is not Fleet f) return;
        f.HoldOn = On;
        sim.Log.Info("fleet", On ? $"{f.Name} holds on" : $"{f.Name} follows its pull-back rule again", new { FleetId, On, Source });
    }
}

/// <summary>"Pull back now": the fleet pulls back at once, by its rule's action.</summary>
public sealed record PullBackNowCommand(int FleetId) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Pull back order") is not Fleet f || f.Retreating) return;
        BattleRules.PullBack(sim, f, "ordered to pull back", byHand: true);
    }
}

/// <summary>Make a preset Auto's default for a situation (null: back to the built-in pick).</summary>
public sealed record SetPullDefaultCommand(Side Side, string Situation, PullPreset? Preset) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        var state = Economy.Of(world, Side);
        if (Preset is PullPreset p) state.PullDefaults[Situation] = p;
        else state.PullDefaults.Remove(Situation);
        foreach (var key in state.Habits.Keys.Where(k => k.StartsWith($"pull:{Situation}:", StringComparison.Ordinal)).ToList()) state.Habits.Remove(key);
        sim.Log.Info("auto", $"Pull-back default for {BattleRules.SituationName(Situation)}: {(Preset is PullPreset q ? PullRule.Name(q) : "built-in")}", new { side = Side.ToString(), Situation, Preset, Source });
    }
}
