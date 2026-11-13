using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>
/// Fuel types (a game-start switch, GameRules.FuelTypes). Each fuel is a good with its own thrust, reach,
/// burn heat, burn signature and rarity (ship-classes.json, "fuels"). Each engine burns one fuel family
/// (its module's "fuels", the first being its usual one) and a ship carries one fuel at a time in its tanks
/// (Ship.Fuel; Ship.Supplies is how much). Refuelling at a planet takes the fuel's good out of its store;
/// Auto picks the fuel for the fleet's job from what the planet has, unless the fleet was told a fuel.
/// Tankers carry each fuel separately (Ship.CargoFuels). Off: every engine burns the one "fuel" good.
/// </summary>
public static class Fuels
{
    /// <summary>Hydrogen + oxygen: the good "fuel", and the one fuel when fuel types are off.</summary>
    public const string Standard = "fuel";

    public static IReadOnlyList<FuelData> All => GameData.Fuels;

    public static FuelData Data(string id) =>
        All.FirstOrDefault(f => f.Id == id) ?? All.First(f => f.Id == Standard);

    public static bool On(World w) => w.Rules.FuelTypes;

    public static string IdOf(Ship s) => string.IsNullOrEmpty(s.Fuel) ? Standard : s.Fuel;

    /// <summary>The fuel in the ship's tanks (always the standard fuel with fuel types off).</summary>
    public static FuelData Of(World w, Ship s) => On(w) ? Data(IdOf(s)) : Data(Standard);

    public static string Name(string id) => Data(id).Name;

    /// <summary>The ship's engine module, if it has one fitted.</summary>
    public static ModuleData? Engine(Ship s)
    {
        var cls = GameData.ShipClass(s.ClassId);
        var fit = Fitting.FitOf(s);
        for (var i = 0; i < cls.SlotList.Count && i < fit.Count; i++)
            if (cls.SlotList[i].Type == "E" && GameData.Module(fit[i]) is ModuleData m) return m;
        return null;
    }

    private static readonly IReadOnlyList<string> StandardOnly = new[] { Standard };

    /// <summary>The fuels an engine burns, its usual one first.</summary>
    public static IReadOnlyList<string> Burns(ModuleData? engine) => engine?.Fuels is { Count: > 0 } f ? f : StandardOnly;

    /// <summary>The fuels this ship's engine can burn (only the standard fuel with fuel types off).</summary>
    public static IReadOnlyList<string> Burns(World w, Ship s) => On(w) ? Burns(Engine(s)) : StandardOnly;

    public static bool CanBurn(World w, Ship s, string fuel) => Burns(w, s).Contains(fuel);

    // ---- How a fuel burns -----------------------------------------------------------------------

    /// <summary>Sail ships burn nothing: no thrust factor from fuel, and no burn heat (see Sails).</summary>
    public static double Thrust(World w, Ship s) => Sails.Is(s) ? 1 : Of(w, s).Thrust;
    public static double Reach(World w, Ship s) => Of(w, s).Reach;
    public static double BurnHeat(World w, Ship s) => Sails.Is(s) ? 0 : Of(w, s).BurnHeat;

    /// <summary>How bright the fleet's burn shows: the brightest plume among its ships.</summary>
    public static double Signature(World w, Fleet f)
    {
        if (!On(w)) return 1;
        var most = 0.0;
        foreach (var s in w.ShipsOf(f)) if (!Sails.Is(s)) most = Math.Max(most, Of(w, s).Signature);
        return most > 0 ? most : 1;
    }

    // ---- What a side can get ---------------------------------------------------------------------

    /// <summary>
    /// Can the side count on this fuel: water and hydrogen + oxygen always; others once it has a plant or
    /// station making it, or a stock of it. New ships and refits take an engine only if they can get its fuel.
    /// </summary>
    public static bool Obtainable(World w, Side side, string fuel)
    {
        if (!On(w) || fuel == Standard || Data(fuel).Anywhere) return true;
        var stock = 0.0;
        foreach (var b in w.Bodies)
        {
            if (b.Owner != side) continue;
            stock += Colonies.Held(b, fuel);
            foreach (var p in Colonies.Built(b))
                if (Planets.BuildingKind(p.Kind)?.Makes?.ContainsKey(fuel) == true) return true;
        }
        if (stock >= 30) return true;
        return w.Stations.Any(st => st.Side == side && Economy.StationKind(st.Kind)?.Makes?.ContainsKey(fuel) == true);
    }

    /// <summary>Can a ship with this engine be kept fuelled by the side?</summary>
    public static bool EngineUsable(World w, Side side, ModuleData engine) => !On(w) || Burns(engine).Any(f => Obtainable(w, side, f));

    // ---- Choosing a fuel --------------------------------------------------------------------------

    /// <summary>What the fleet is doing, for Auto's fuel choice.</summary>
    public enum Job { Fight, Quiet, Haul }

    public static Job JobOf(World w, Fleet? f)
    {
        if (f == null) return Job.Fight;
        if (f.Radiators == RadiatorMode.Cold || f.ZoneId != null) return Job.Quiet;
        if (w.ShipsOf(f).All(s => SupplyLines.IsSupplyShip(s) || !Planner.Warship(s))) return Job.Haul;
        return Job.Fight;
    }

    /// <summary>How well a fuel suits a job: far reach for warships, cool and dim for ambushers, cheap for haulers.</summary>
    public static double Score(FuelData f, Job job) => job switch
    {
        Job.Quiet => -2 * f.Signature - f.BurnHeat + 0.3 * f.Reach,
        Job.Haul => -f.PerSupply * 4 + 0.2 * f.Reach,
        _ => 1.5 * f.Reach + 0.5 * f.Thrust - 0.4 * f.BurnHeat,
    };

    /// <summary>Is there enough of this fuel at the planet for a worthwhile fill (a store-less outpost counts as having all)?</summary>
    private static bool InStock(Body b, string fuel, double supplies) =>
        b.Board == null && b.Stock.Count == 0 || Colonies.Held(b, fuel) >= Data(fuel).PerSupply * Math.Min(supplies, 10) - 1e-9;

    /// <summary>Tanks below this share may change fuel (what is left goes back to the store); above it the ship keeps its fuel.</summary>
    public const double SwitchBelow = 0.25;

    /// <summary>
    /// The fuel a ship should fill up with here. A ship keeps the fuel it has while its tanks are a quarter
    /// full or more (so fuel is not poured back and forth), unless a clearly better one (or the fleet's chosen
    /// fuel) fills the whole tank here. Below that: the fleet's chosen fuel if it is in
    /// stock, else Auto's pick for the fleet's job among the fuels in stock (the one it has unless another
    /// suits clearly better); with none in stock, water if the engine takes it and the tanks are all but dry.
    /// </summary>
    public static string Pick(World w, Ship s, Fleet? f, Body b, double wanted)
    {
        var burns = Burns(w, s);
        var current = IdOf(s);
        if (!On(w) || burns.Count == 1) return burns[0];
        var full = Fitting.MaxSupplies(w, s);
        var share = full <= 0 ? 0 : s.Supplies / full;
        var burnable = burns.Contains(current);
        if (burnable && share >= SwitchBelow) return Upgrade(w, s, f, b, burns, current, full) ?? current;
        if (f?.Fuel is { Length: > 0 } chosen && burns.Contains(chosen) && InStock(b, chosen, wanted)) return chosen;
        var job = JobOf(w, f);
        var stocked = burns.Where(x => InStock(b, x, wanted)).ToList();
        if (stocked.Count == 0)
        {
            var water = burns.FirstOrDefault(x => Data(x).Anywhere);
            if (burnable && (water == null || share >= 0.05)) return current;
            return water ?? burns[0];
        }
        var best = stocked.OrderByDescending(x => Score(Data(x), job)).ThenBy(x => x, StringComparer.Ordinal).First();
        if (!burnable || !stocked.Contains(current)) return best;
        return Score(Data(best), job) > Score(Data(current), job) + 0.15 ? best : current;
    }

    /// <summary>Tanks a quarter full or more change fuel only for a clearly better one (Auto, or the fleet's choice) with a full tank's worth in the store.</summary>
    public const double UpgradeMargin = 0.2;

    private static string? Upgrade(World w, Ship s, Fleet? f, Body b, IReadOnlyList<string> burns, string current, double full)
    {
        if (b.Board == null || full <= 0) return null;
        bool FullTank(string x) => Colonies.Held(b, x) >= Data(x).PerSupply * full - 1e-9;
        if (f?.Fuel is { Length: > 0 } chosen)
            return chosen != current && burns.Contains(chosen) && FullTank(chosen) ? chosen : null;
        var job = JobOf(w, f);
        var bar = Score(Data(current), job) + UpgradeMargin;
        return burns.Where(x => x != current && FullTank(x) && Score(Data(x), job) > bar)
            .OrderByDescending(x => Score(Data(x), job)).ThenBy(x => x, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>
    /// Changes the fuel in the ship's tanks: what is left goes back into the planet's store (if it has one).
    /// </summary>
    public static void Switch(World w, Ship s, string fuel, Body? at)
    {
        var old = IdOf(s);
        if (old == fuel) return;
        if (at != null && (at.Board != null || at.Stock.Count > 0) && s.Supplies > 0)
        {
            at.Stock[old] = Colonies.Held(at, old) + s.Supplies * Data(old).PerSupply;
            Ledger.In(w, at.Owner, old, s.Supplies * Data(old).PerSupply, "back from tanks");
        }
        s.Supplies = 0;
        s.Fuel = fuel == Standard ? "" : fuel;
    }

    /// <summary>
    /// Refuelling at a held planet: picks the fuel, then takes its good out of the store. Returns the share
    /// of what was wanted that it gets this hour: all of it with stock, else what the stock covers, or the slow
    /// pace with none (with fuel types on, only water is scraped together anywhere; other fuels trickle).
    /// </summary>
    public static double Refuel(World w, Ship s, Fleet? f, Body b, double wanted)
    {
        if (b.Board == null && b.Stock.Count == 0) return 1;
        if (On(w))
        {
            var pick = Pick(w, s, f, b, wanted);
            if (pick != IdOf(s)) Switch(w, s, pick, b);
        }
        var fuel = Of(w, s);
        var need = wanted * fuel.PerSupply;
        if (need <= 0) return 1;
        var have = Colonies.Held(b, fuel.Id);
        var take = Math.Min(have, need);
        if (take > 0)
        {
            b.Stock[fuel.Id] = have - take;
            Ledger.Out(w, b.Owner, fuel.Id, take, "ship fuel");
            NoteDraw(b, fuel.Id, take);
            if (b.Owner == s.Side) Lift.Pay(w, s.Side, b, take);
        }
        if (take >= need - 1e-12) return 1;
        var slow = !On(w) || fuel.Anywhere ? Colonies.C.NoFuelRefill : Colonies.C.NoFuelRefill * 0.25;
        return Math.Max(slow, take / need);
    }

    // ---- What planets see drawn -------------------------------------------------------------------

    /// <summary>Fuel drawn at a planet (ships and tankers) adds to its use per day, so stock rules and freighters keep it supplied.</summary>
    public static void NoteDraw(Body b, string good, double amount)
    {
        if (amount <= 0) return;
        b.FuelUse[good] = b.FuelUse.GetValueOrDefault(good) + amount * DrawWeight;
    }

    /// <summary>Each hour the remembered draw fades by this share; with DrawWeight the remembered number settles at the draw per day.</summary>
    public const double DrawFade = 1.0 / 240, DrawWeight = DrawFade * 24;

    public static void FadeDraws(World w)
    {
        foreach (var b in w.Bodies)
        {
            if (b.FuelUse.Count == 0) continue;
            foreach (var g in b.FuelUse.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
            {
                var v = b.FuelUse[g] * (1 - DrawFade);
                if (v < 1e-4) b.FuelUse.Remove(g);
                else b.FuelUse[g] = v;
            }
        }
    }

    // ---- Tanker cargo -----------------------------------------------------------------------------

    public static double CargoOf(Ship s, string fuel) =>
        s.CargoFuels.Count == 0 ? (fuel == Standard ? s.CargoFuel : 0) : s.CargoFuels.GetValueOrDefault(fuel);

    /// <summary>The fuel cargo by fuel (all of it standard fuel when not split).</summary>
    public static IEnumerable<(string Fuel, double Amount)> Cargo(Ship s)
    {
        if (s.CargoFuels.Count == 0)
        {
            if (s.CargoFuel > 0) yield return (Standard, s.CargoFuel);
            yield break;
        }
        foreach (var (fuel, amount) in s.CargoFuels.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (amount > 0) yield return (fuel, amount);
    }

    public static void AddCargo(Ship s, string fuel, double amount)
    {
        if (amount <= 0) return;
        if (s.CargoFuels.Count == 0 && s.CargoFuel > 0) s.CargoFuels[Standard] = s.CargoFuel;
        s.CargoFuels[fuel] = s.CargoFuels.GetValueOrDefault(fuel) + amount;
        s.CargoFuel += amount;
    }

    public static double TakeCargo(Ship s, string fuel, double amount)
    {
        var take = Math.Min(amount, CargoOf(s, fuel));
        if (take <= 0) return 0;
        if (s.CargoFuels.Count == 0) s.CargoFuel -= take;
        else
        {
            s.CargoFuels[fuel] -= take;
            if (s.CargoFuels[fuel] <= 1e-9) s.CargoFuels.Remove(fuel);
            s.CargoFuel = s.CargoFuels.Values.Sum();
        }
        return take;
    }

    /// <summary>
    /// The fuels a tanker should carry, as shares: those burned by the fleet it supplies (its supply run's
    /// fleet, else its own fleet's other ships), by tank size. With fuel types off, or nothing to go by: standard fuel.
    /// </summary>
    public static Dictionary<string, double> Mix(World w, Ship tanker)
    {
        var mix = new Dictionary<string, double>();
        if (On(w) && w.FleetOf(tanker) is Fleet own)
        {
            var served = own.SupplyRunFor is int id && w.FindFleet(id) is Fleet run ? run : own;
            foreach (var s in w.ShipsOf(served))
            {
                if (SupplyLines.IsSupplyShip(s)) continue;
                var fuel = IdOf(s);
                mix[fuel] = mix.GetValueOrDefault(fuel) + Fitting.MaxSupplies(w, s);
            }
        }
        var total = mix.Values.Sum();
        if (total <= 0) return new Dictionary<string, double> { [Standard] = 1 };
        foreach (var k in mix.Keys.ToList()) mix[k] /= total;
        return mix;
    }

    /// <summary>
    /// A tanker in orbit at a held planet loads fuel for an hour: each fuel of its mix from the planet's store.
    /// With none in store it loads at the slow pace (water and, with fuel types off, the one fuel) or a trickle.
    /// </summary>
    public static void Load(World w, Ship s, double room, Body? at)
    {
        if (room <= 1e-12) return;
        foreach (var (fuel, share) in Mix(w, s).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var want = room * share;
            if (at == null || at.Board == null && at.Stock.Count == 0)
            {
                AddCargo(s, fuel, want);
                continue;
            }
            var data = Data(fuel);
            var need = want * data.PerSupply;
            var have = Colonies.Held(at, fuel);
            var take = Math.Min(have, need);
            if (take > 0)
            {
                at.Stock[fuel] = have - take;
                Ledger.Out(w, at.Owner, fuel, take, "tanker fuel");
                NoteDraw(at, fuel, take);
                if (at.Owner == s.Side) Lift.Pay(w, s.Side, at, take);
            }
            var slow = !On(w) || data.Anywhere ? Colonies.C.NoFuelRefill : Colonies.C.NoFuelRefill * 0.25;
            AddCargo(s, fuel, want * (take >= need - 1e-12 ? 1 : Math.Max(slow, take / need)));
        }
    }

    /// <summary>Did the ship's fuel and cargo stay sane (for the rule book)?</summary>
    public static string? Problem(World w, Ship s)
    {
        if (On(w) && s.Supplies > 1e-6 && !CanBurn(w, s, IdOf(s))) return $"carries {IdOf(s)} its engine can't burn";
        if (!On(w) && IdOf(s) != Standard) return $"carries {IdOf(s)} with fuel types off";
        if (s.CargoFuels.Count > 0 && Math.Abs(s.CargoFuels.Values.Sum() - s.CargoFuel) > 1e-6) return "fuel cargo by type does not add up";
        if (s.CargoFuels.Values.Any(v => v < -1e-9)) return "negative fuel cargo";
        return null;
    }

    /// <summary>A refit changed the engine: fuel it can no longer burn goes back to the store, and the tanks start on the usual fuel.</summary>
    public static void AfterRefit(World w, Ship s, Body? at)
    {
        if (!On(w) || CanBurn(w, s, IdOf(s))) return;
        var usual = Burns(w, s).FirstOrDefault(f => Obtainable(w, s.Side, f)) ?? Burns(w, s)[0];
        Switch(w, s, usual, at);
    }

    /// <summary>A new ship's tanks: the fuel its engine burns that suits its job best among those in the yard's store, else the usual one it can get.</summary>
    public static string ForNewShip(World w, Ship s, Body? yard)
    {
        var burns = Burns(w, s);
        if (!On(w)) return "";
        var job = Planner.Warship(s) ? Job.Fight : Job.Haul;
        var pick = yard == null ? null : burns.Where(f => Colonies.Held(yard, f) >= 10 && Obtainable(w, s.Side, f))
            .OrderByDescending(f => Score(Data(f), job)).ThenBy(f => f, StringComparer.Ordinal).FirstOrDefault();
        pick ??= burns.FirstOrDefault(f => Obtainable(w, s.Side, f)) ?? burns[0];
        return pick == Standard ? "" : pick;
    }
}

/// <summary>Set the fuel a fleet fills up with (null or "": Auto picks for its job). Works while paused.</summary>
public sealed record SetFleetFuelCommand(Side Side, int FleetId, string? Fuel) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a standing order, read when it next refuels

    public override void Apply(World world, Simulation sim)
    {
        var f = world.FindFleet(FleetId);
        if (f == null || f.Side != Side)
        {
            sim.Log.Warn("fuel", "Fuel choice for a fleet that is not ours", new { side = Side.ToString(), FleetId, Source });
            return;
        }
        var fuel = string.IsNullOrEmpty(Fuel) ? "" : Fuel;
        if (fuel.Length > 0 && Fuels.All.All(x => x.Id != fuel))
        {
            sim.Log.Warn("fuel", "Unknown fuel", new { FleetId, fuel, Source });
            return;
        }
        f.Fuel = fuel;
        sim.Log.Info("fuel", $"{f.Name} fills up with {(fuel.Length == 0 ? "Auto's pick" : Fuels.Name(fuel))}", new { FleetId, fuel, Source });
    }
}
