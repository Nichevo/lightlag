using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>
/// Ship fittings. Each ship carries one module id per slot of its class (Ship.Fit). Its working numbers
/// (Spec) are the class numbers, which describe the standard fit, plus what its own modules add minus
/// what the standard ones add. A fit must stay inside the hull's power and heat budgets. Refits happen
/// at the side's shipyard; new ships get the side's design for the class (or the standard fit) with the
/// best engine researched.
/// </summary>
public static class Fitting
{
    /// <summary>The ship's working numbers for its fit. Cached on the ship until the fit changes.</summary>
    public static ShipClass Spec(Ship s)
    {
        var key = s.Fit.Count == 0 ? "" : string.Join("|", s.Fit);
        if (s.SpecCache != null && s.SpecKey == key && s.SpecCache.Id == s.ClassId) return s.SpecCache;
        var cls = GameData.ShipClass(s.ClassId);
        s.SpecCache = Compute(cls, s.Fit);
        s.SpecKey = key;
        return s.SpecCache;
    }

    /// <summary>A class's numbers with a given fit (an empty or mismatched fit: the standard numbers).</summary>
    public static ShipClass Compute(ShipClass cls, IReadOnlyList<string> fit)
    {
        var slots = cls.SlotList;
        if (fit.Count != slots.Count) return cls;
        int rail = 0, pd = 0, missiles = 0, sinks = 0;
        double shield = 0, armour = 0, sensor = 0, supplies = 0, heatCap = 0, accel = 1;
        for (var i = 0; i < slots.Count; i++)
        {
            if (fit[i] == slots[i].Fit) continue;
            var n = slots[i].Mounts;
            if (GameData.Module(fit[i]) is ModuleData add)
            {
                rail += add.Railguns * n; pd += add.PointDefence * n; missiles += add.Missiles * n; sinks += add.HeatSinks * n;
                shield += add.Shield * n; armour += add.Armour * n; sensor += add.SensorMm * n; supplies += add.Supplies * n; heatCap += add.HeatCapacity * n;
                for (var k = 0; k < n; k++) accel *= add.AccelFactor;
            }
            if (GameData.Module(slots[i].Fit) is ModuleData std)
            {
                rail -= std.Railguns * n; pd -= std.PointDefence * n; missiles -= std.Missiles * n; sinks -= std.HeatSinks * n;
                shield -= std.Shield * n; armour -= std.Armour * n; sensor -= std.SensorMm * n; supplies -= std.Supplies * n; heatCap -= std.HeatCapacity * n;
                for (var k = 0; k < n; k++) accel /= std.AccelFactor;
            }
        }
        return cls with
        {
            Railguns = Math.Max(0, cls.Railguns + rail), PointDefence = Math.Max(0, cls.PointDefence + pd),
            Missiles = Math.Max(0, cls.Missiles + missiles), HeatSinks = Math.Max(0, cls.HeatSinks + sinks),
            Shield = Math.Max(0, cls.Shield + shield), Armour = Math.Max(1, cls.Armour + armour),
            SensorMm = Math.Max(1, cls.SensorMm + sensor), Supplies = Math.Max(1, cls.Supplies + supplies),
            HeatCapacity = Math.Max(10, cls.HeatCapacity + heatCap), AccelMmPerHour2 = cls.AccelMmPerHour2 * accel,
        };
    }

    /// <summary>Most supplies the ship can hold: its fit, and Large tanks if researched.</summary>
    public static double MaxSupplies(World w, Ship s) => Spec(s).Supplies * Tech.Factor(w, s.Side, Tech.LargeTanks);

    public static double PowerUsed(ShipClass cls, IReadOnlyList<string> fit) =>
        Enumerable.Range(0, Math.Min(fit.Count, cls.SlotList.Count)).Sum(i => (GameData.Module(fit[i])?.Power ?? 0) * cls.SlotList[i].Mounts);

    public static double HeatUsed(ShipClass cls, IReadOnlyList<string> fit) =>
        Enumerable.Range(0, Math.Min(fit.Count, cls.SlotList.Count)).Sum(i => (GameData.Module(fit[i])?.Heat ?? 0) * cls.SlotList[i].Mounts);

    /// <summary>The ship's fit, or the standard fit if it has none yet.</summary>
    public static IReadOnlyList<string> FitOf(Ship s) =>
        s.Fit.Count == GameData.ShipClass(s.ClassId).SlotList.Count ? s.Fit : StandardFit(GameData.ShipClass(s.ClassId));

    public static List<string> StandardFit(ShipClass cls) => cls.SlotList.Select(x => x.Fit).ToList();

    /// <summary>Burn heat factor of the ship's engine (Engine Mk II and III run cooler).</summary>
    public static double BurnHeatFactor(Ship s)
    {
        var cls = GameData.ShipClass(s.ClassId);
        var fit = FitOf(s);
        for (var i = 0; i < cls.SlotList.Count; i++)
            if (cls.SlotList[i].Type == "E" && GameData.Module(fit[i]) is ModuleData m) return m.BurnHeatFactor;
        return 1;
    }

    /// <summary>The best engine module this side has researched (with fuel types on, one whose fuel it can get).</summary>
    public static string BestEngine(World w, Side side) =>
        GameData.Modules.Values.Where(m => m.Slot == "E" && (m.Needs.Length == 0 || Tech.Has(w, side, m.Needs)) && Fuels.EngineUsable(w, side, m))
            .OrderByDescending(m => m.AccelFactor).ThenBy(m => m.Id, StringComparer.Ordinal).Select(m => m.Id).FirstOrDefault() ?? "";

    /// <summary>Why this module can't go in this slot of this class for this side, or null if it can (budgets not checked).</summary>
    public static string? CanUse(World w, Side side, ShipClass cls, int slot, string moduleId)
    {
        if (slot < 0 || slot >= cls.SlotList.Count) return "No such slot";
        if (moduleId.Length == 0) return cls.SlotList[slot].Type == "E" ? "A ship needs an engine" : null;
        if (GameData.Module(moduleId) is not ModuleData m) return $"Unknown module '{moduleId}'";
        if (m.Slot != cls.SlotList[slot].Type) return $"{m.Name} doesn't fit a {SlotName(cls.SlotList[slot].Type)} slot";
        if (m.Needs.Length > 0 && !Tech.Has(w, side, m.Needs)) return $"Needs {GameData.Research(m.Needs).Name}";
        return null;
    }

    /// <summary>Why this whole fit can't be used, or null.</summary>
    public static string? CanFit(World w, Side side, ShipClass cls, IReadOnlyList<string> fit)
    {
        if (fit.Count != cls.SlotList.Count) return "The fit doesn't match the hull's slots";
        for (var i = 0; i < fit.Count; i++)
            if (CanUse(w, side, cls, i, fit[i]) is string p) return p;
        if (PowerUsed(cls, fit) > cls.Power + 1e-9) return $"Over the power budget ({PowerUsed(cls, fit):0} of {cls.Power:0})";
        if (HeatUsed(cls, fit) > cls.HeatBudget + 1e-9) return $"Over the heat budget ({HeatUsed(cls, fit):0} of {cls.HeatBudget:0})";
        return null;
    }

    public static string SlotName(string type) => type switch { "W" => "weapon", "D" => "defence", "U" => "utility", "E" => "engine", _ => type };

    /// <summary>The fit a new ship of this class gets: the side's design (if still allowed) or the standard fit, with the best engine.</summary>
    public static List<string> AutoFit(World w, Side side, ShipClass cls)
    {
        var design = Economy.Of(w, side).Designs.Find(d => d.ClassId == cls.Id)?.Fit;
        var fit = design != null && design.Count == cls.SlotList.Count ? design.ToList() : StandardFit(cls);
        var engine = BestEngine(w, side);
        for (var i = 0; i < fit.Count; i++)
            if (cls.SlotList[i].Type == "E" && engine.Length > 0) fit[i] = engine;
        return CanFit(w, side, cls, fit) == null ? fit : StandardFit(cls);
    }

    /// <summary>Gives a ship a fit and sets its speed, thrust and stocks to match (stocks are cut to the new maximum).</summary>
    public static void Apply(World w, Ship s, List<string> fit)
    {
        s.Fit = fit;
        Fuels.AfterRefit(w, s, w.FleetOf(s) is Fleet f && f.OrbitBodyId is int at ? w.FindBody(at) : null);
        var spec = Spec(s);
        s.MaxSpeed = spec.MaxSpeedMmPerHour;
        s.Accel = spec.AccelMmPerHour2;
        s.Supplies = Math.Min(s.Supplies, spec.Supplies);
        s.Missiles = Math.Min(s.Missiles, spec.Missiles);
        s.Ammo = Math.Min(s.Ammo, SupplyLines.MaxAmmo(s));
        s.Food = Math.Min(s.Food, SupplyLines.MaxFood(s));
        s.Armour = Math.Min(s.Armour, Tech.MaxArmour(w, s));
        s.HeatSinks = Math.Min(s.HeatSinks, spec.HeatSinks);
        s.Shield = Math.Min(s.Shield, Heat.MaxShield(w, s));
    }

    /// <summary>A ship can be refitted while its fleet holds orbit at its side's shipyard.</summary>
    public static string? CanRefit(World w, Side side, Ship s)
    {
        if (!w.Loop) return "Refits need a shipyard (not in the battle sandbox)";
        if (s.Side != side) return "Not our ship";
        if (Economy.ShipyardOf(w, side) is not Body yard) return "No shipyard";
        if (w.FleetOf(s) is not Fleet f || !Economy.InOrbitAt(f, yard)) return $"Refits happen in orbit at {yard.Name}";
        return null;
    }
}

/// <summary>A side's saved fit for new ships of a class.</summary>
public sealed class Design
{
    public string ClassId { get; set; } = "";
    public List<string> Fit { get; set; } = new();
}

/// <summary>Swap the module in one slot of a ship (at the shipyard). The new module's cost is paid in Industry.</summary>
public sealed record SetFitCommand(Side Side, int ShipId, int Slot, string Module) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        var ship = world.FindShip(ShipId);
        if (ship == null)
        {
            sim.Log.Warn("fitting", "Refit for a ship that does not exist", new { side = Side.ToString(), ShipId, Source });
            return;
        }
        var cls = GameData.ShipClass(ship.ClassId);
        var fit = Fitting.FitOf(ship).ToList();
        string? problem = Fitting.CanRefit(world, Side, ship);
        if (problem == null && (Slot < 0 || Slot >= fit.Count)) problem = "No such slot";
        if (problem == null)
        {
            fit[Slot] = Module;
            problem = Fitting.CanFit(world, Side, cls, fit);
        }
        var cost = GameData.Module(Module)?.Cost ?? 0;
        if (problem == null && Economy.Of(world, Side).Industry < cost) problem = $"Needs {cost:0} Industry";
        if (problem != null)
        {
            sim.Log.Warn("fitting", $"Can't refit {ship.Name}: {problem}", new { side = Side.ToString(), ShipId, Slot, Module, Source });
            return;
        }
        Economy.Of(world, Side).Industry -= cost;
        Economy.Of(world, Side).Totals.IndustrySpent += cost;
        Fitting.Apply(world, ship, fit);
        sim.Log.Info("fitting", $"{ship.Name} is refitted", new { side = Side.ToString(), ShipId, Slot, Module, cost, Source });
    }
}

/// <summary>Save the fit new ships of a class get (engines are always the best researched).</summary>
public sealed record SetDesignCommand(Side Side, string ClassId, List<string> Fit) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        if (!GameData.ShipClasses.TryGetValue(ClassId, out var cls) || Fitting.CanFit(world, Side, cls, Fit) != null)
        {
            sim.Log.Warn("fitting", "Can't save that design", new { side = Side.ToString(), ClassId, fit = string.Join(",", Fit), Source });
            return;
        }
        var designs = Economy.Of(world, Side).Designs;
        designs.RemoveAll(d => d.ClassId == ClassId);
        designs.Add(new Design { ClassId = ClassId, Fit = Fit.ToList() });
        sim.Log.Info("fitting", $"{Side} saves a {cls.Name} design", new { side = Side.ToString(), ClassId, fit = string.Join(",", Fit), Source });
    }
}
