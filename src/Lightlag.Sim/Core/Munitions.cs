namespace Lightlag.Sim.Core;

/// <summary>How a munitions plant runs: Basic makes plain ammunition (more of it, at base staff and power);
/// Best keeps it tooled for the side's latest missile brain and railgun slugs.</summary>
public enum PlantMode { Basic, Best }

/// <summary>
/// Ammunition grades. Research (missile brains, Railgun Mk III) only says what could be made: a munitions
/// plant set to Best has to be refitted for it, and ships get the better missiles or slugs when they next
/// reload from a planet (or from a supply ship loaded there). Until then they fire what they carry, and any
/// launcher can fire basic missiles. A plant on Best needs more staff and power for each upgrade; on Basic it
/// makes about 30 % more of plain ammunition. Plants start on Basic. Automation (the Refits area) and the
/// rival AI decide when to switch: some rivals go for the latest at once, others when intelligence shows the
/// enemy's defences pulling ahead.
/// </summary>
public static class Munitions
{
    public const string Plant = "munitions-plant";

    /// <summary>Extra staff and power a Best plant needs per grade it is tooled above basic.</summary>
    public const double DrawPerGrade = 0.25;

    /// <summary>A Basic plant makes this much more plain ammunition.</summary>
    public const double BasicBoost = 1.3;

    public static bool IsPlant(Piece p) => p.Kind == Plant;

    /// <summary>The grades the side's research allows: missile brain 1-5, rail slugs 1 (basic) or 2 (Mk III).</summary>
    public static (int Missile, int Rail) Researched(World w, Side side) =>
        (Tech.MissileBrain(w, side), Tech.Has(w, side, Tech.RailgunMk3) ? 2 : 1);

    /// <summary>Grades above basic this plant is tooled for (0 for a basic one).</summary>
    public static int Upgrades(Piece p) => Math.Max(0, p.MissileGrade - 1) + Math.Max(0, p.RailGrade - 1);

    /// <summary>Staff and power factor for a piece: a Best munitions plant needs more for each upgrade.</summary>
    public static double DrawFactor(Piece p) => IsPlant(p) && p.PlantMode == PlantMode.Best ? 1 + DrawPerGrade * Upgrades(p) : 1;

    /// <summary>Output factor for a piece: a Basic munitions plant makes more.</summary>
    public static double MakeFactor(Piece p) => IsPlant(p) && p.PlantMode == PlantMode.Basic ? BasicBoost : 1;

    /// <summary>The best grades the side's munitions plants make now (basic with none on Best).</summary>
    public static (int Missile, int Rail) Made(World w, Side side)
    {
        int missile = 1, rail = 1;
        foreach (var b in w.Bodies.Where(b => b.Owner == side))
            foreach (var p in Colonies.Built(b))
                if (IsPlant(p) && p.PlantMode == PlantMode.Best && p.Staff > 0 && p.Powered)
                {
                    missile = Math.Max(missile, p.MissileGrade);
                    rail = Math.Max(rail, p.RailGrade);
                }
        return (missile, rail);
    }

    /// <summary>Does this plant need a refit to make what the research allows (on Best)?</summary>
    public static bool Behind(World w, Side side, Piece p)
    {
        var (m, r) = Researched(w, side);
        return p.MissileGrade < m || p.RailGrade < r;
    }

    /// <summary>A Best plant behind the research starts its refit (no-op if one is under way).</summary>
    public static void StartRefit(Simulation sim, Body b, Piece p)
    {
        if (b.Owner is not Side side || p.PlantMode != PlantMode.Best || p.RefitHoursLeft > 0 || !Behind(sim.World, side, p)) return;
        p.RefitHoursLeft = Colonies.C.RefitDays * 24;
        b.Stock[Colonies.Spares] = Math.Max(0, Colonies.Held(b, Colonies.Spares) - 2);
        sim.Log.Info("planets", "Munitions plant starts a refit", new { bodyId = b.Id, pieceId = p.Id, missile = Researched(sim.World, side).Missile, rail = Researched(sim.World, side).Rail });
    }

    /// <summary>A plant finished building or refitting on Best: it now makes what the research allows.</summary>
    public static void Tooled(World w, Body b, Piece p)
    {
        if (!IsPlant(p) || b.Owner is not Side side || p.PlantMode != PlantMode.Best) return;
        (p.MissileGrade, p.RailGrade) = Researched(w, side);
    }

    /// <summary>Research landed: Best plants refit for it.</summary>
    public static void OnResearched(Simulation sim, Side side)
    {
        foreach (var b in sim.World.Bodies.Where(b => b.Owner == side))
            foreach (var p in Colonies.Built(b).Where(IsPlant).ToList())
                StartRefit(sim, b, p);
    }

    /// <summary>A missile's brain: what the side has researched, but no better than the rack it was loaded into.</summary>
    public static int Brain(World w, Ship s, int cap = 5) => Math.Clamp(Math.Min(w.MissileBrain[(int)s.Side], s.MissileGrade), 1, cap);

    /// <summary>
    /// The enemy has been seen with defences that outclass what the side's plants make: better point
    /// defence than its missiles are built for, or heavier armour or shields than its slugs.
    /// </summary>
    public static bool Pressed(World w, Side side)
    {
        var (made, rail) = Made(w, side);
        var (canMissile, canRail) = Researched(w, side);
        var pd = Intel.TechSeen(w, side, "point-defence")?.Level ?? 1;
        var armour = Math.Max(Intel.TechSeen(w, side, "armour")?.Level ?? 1, Intel.TechSeen(w, side, "shields")?.Level ?? 0);
        return canMissile > made && pd >= made + 1 || canRail > rail && armour >= 2;
    }

    /// <summary>Some rivals want the latest at once: the research-minded and the most aggressive.</summary>
    public static bool Eager(PersonalityData p) => p.Profile is { } pr && (pr.Research >= 0.6 || pr.Aggression >= 0.8);

    /// <summary>
    /// Plants on Basic that could make better: switched to Best (refit starts) by Auto when pressed (or at
    /// once when eager), offered under Needs you on Advise.
    /// </summary>
    public static void Steward(Simulation sim, Side side, OfferFn offer, bool eager = false)
    {
        var w = sim.World;
        var pressed = Pressed(w, side);
        var (m, r) = Researched(w, side);
        if (m <= 1 && r <= 1) return;
        foreach (var b in w.Bodies.Where(b => b.Owner == side).OrderBy(b => b.Id))
            foreach (var p in Colonies.Built(b).Where(p => IsPlant(p) && p.PlantMode == PlantMode.Basic).OrderBy(p => p.Id))
            {
                var what = string.Join(" and ", new[] { m > 1 ? $"brain {m} missiles" : null, r > 1 ? "Mk III slugs" : null }.Where(x => x != null));
                var why = pressed ? "the enemy has been seen with defences our ammunition is not made for" : "our ships still load basic ammunition";
                var text = $"Upgrade the munitions plant at {b.Name} to make {what}: {why} (it needs more staff and power)";
                var act = new SetPlantModeCommand(side, b.Id, p.Id, PlantMode.Best);
                if (pressed || eager) offer(AutoArea.Refits, $"munitions:{p.Id}", text, act, "Upgrade", b.Id, advice: pressed ? NeedLevel.Soon : NeedLevel.Advice);
                else if (Stewards.LevelFor(w, side, AutoArea.Refits, b.Id) == AutoLevel.Advise) offer(AutoArea.Refits, $"munitions:{p.Id}", text, act, "Upgrade", b.Id);
            }
    }

    /// <summary>Should the side build its first munitions plant (it has research for better ammunition but no plant)?</summary>
    public static bool WantPlant(World w, Side side) =>
        Researched(w, side) is var (m, r) && (m > 1 || r > 1) && !w.Bodies.Any(b => b.Owner == side && b.Pieces.Any(IsPlant));

    public static string ModeName(PlantMode mode) => mode == PlantMode.Best ? "Best" : "Basic only";

    /// <summary>What the plant makes, for the building card.</summary>
    public static string Describe(World w, Body b, Piece p)
    {
        var grades = p.PlantMode == PlantMode.Basic ? "basic missiles and slugs (30 % more)"
            : $"brain {p.MissileGrade} missiles, {(p.RailGrade >= 2 ? "Mk III" : "basic")} slugs";
        var behind = b.Owner is Side side && p.PlantMode == PlantMode.Best && Behind(w, side, p) ? (p.RefitHoursLeft > 0 ? " · refitting for the latest" : " · behind the research") : "";
        return $"Makes {grades}{behind}";
    }
}

/// <summary>Set a munitions plant to Basic only or Best (Best refits it for the latest research).</summary>
public sealed record SetPlantModeCommand(Side Side, int BodyId, int PieceId, PlantMode Mode) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        if (world.FindBody(BodyId) is not Body b || b.Owner != Side || b.Pieces.Find(p => p.Id == PieceId) is not Piece piece || !Munitions.IsPlant(piece))
        {
            sim.Log.Warn("planets", "Plant mode for a plant that is not ours", new { side = Side.ToString(), BodyId, PieceId, Source });
            return;
        }
        piece.PlantMode = Mode;
        if (piece.Built) Munitions.StartRefit(sim, b, piece);
        sim.Log.Info("planets", $"{Side} sets a munitions plant to {Munitions.ModeName(Mode)}", new { side = Side.ToString(), BodyId, PieceId, mode = Mode.ToString(), Source });
    }
}
