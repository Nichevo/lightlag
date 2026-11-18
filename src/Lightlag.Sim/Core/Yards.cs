using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>One place a hull is laid down: a planet's ground yard, or one yard bay of the station over it.</summary>
/// <param name="Key">"ground", or "orbit0"/"orbit1": the station's first or second yard bay.</param>
/// <param name="MaxM">The longest hull it lays down.</param>
/// <param name="Rate">How fast it builds (1 is a full-speed shipyard).</param>
public sealed record Berth(Body Body, string Key, double MaxM, double Rate, string Name)
{
    public bool Orbit => Key != Yards.Ground;
}

/// <summary>
/// More than one shipyard. Every planet a side holds can have up to three berths: a ground yard (the Shipyard,
/// 180 m, or a dry dock, 120 m at 60 % speed once Colony shipyards is researched) and up to two yard bays on the
/// station over it (a dock bay 120 m, a great slipway 260 m, a capital slipway 360 m). Each berth builds one
/// hull at a time from one queue: a ship placed on Auto goes to the free berth that takes its hull, has its
/// materials and is nearest the fleet it joins (else the home yard); a ship pinned to a planet waits for a berth
/// there. A yard planet is one with a Shipyard or a station with a great or capital slipway; a side loses when
/// its last yard planet falls. The home yard is its seat: when it falls the next yard planet takes over and
/// every yard stops for a while. A dry dock at a big colony can be upgraded to a Shipyard.
/// </summary>
public static class Yards
{
    public const string Ground = "ground";
    private static ShipyardData Y => GameData.Economy.Shipyard;

    // ---- Berths --------------------------------------------------------------------------------------

    /// <summary>The working berths at this planet for this side, ground first.</summary>
    public static IEnumerable<Berth> At(World w, Side side, Body b)
    {
        if (b.Owner != side) yield break;
        if (b.Shipyard) yield return new Berth(b, Ground, Starbases.YardSize("yard").MaxLengthM, Colonies.ShipyardRate(w, b), "Shipyard");
        else if (Colonies.DockBuilds(w, b, side)) yield return new Berth(b, Ground, Starbases.YardSize("dock-bay").MaxLengthM, Colonies.ShipyardRate(w, b), "Dry dock");
        if (Starbases.WorkingAt(w, b.Id, side) is not Starbase st) yield break;
        var working = Starbases.WorkingFit(st);
        var n = 0;
        for (var i = 0; i < st.Fit.Count; i++)
        {
            if (GameData.Module(st.Fit[i]) is not { Slot: "B", YardM: > 0 } m) continue;
            var key = "orbit" + n++;
            if (i >= working.Count || working[i] != st.Fit[i]) continue; // being swapped: out for now
            if (m.Bay == "dock" && !Tech.Has(w, side, "colony-shipyards")) continue;
            yield return new Berth(b, key, m.YardM, 1, m.Name);
        }
    }

    /// <summary>Every working berth the side has, planet by planet.</summary>
    public static List<Berth> All(World w, Side side) =>
        w.Bodies.Where(b => b.Owner == side).OrderBy(b => b.Id).SelectMany(b => At(w, side, b)).ToList();

    /// <summary>Does this berth take this hull? (A settler ship is made by a settle order and fits any yard.)</summary>
    public static bool Fits(Berth berth, string classId) =>
        classId == Planets.SettlerClass || !GameData.ShipClasses.TryGetValue(classId, out var cls) || cls.LengthM <= berth.MaxM + 1e-9;

    /// <summary>The yard bays (dock bays and slipways) in a station fit.</summary>
    public static int YardBays(IEnumerable<string> fit) => fit.Count(id => GameData.Module(id) is { Slot: "B", YardM: > 0 });

    /// <summary>The order building on this berth now, if any.</summary>
    public static WorkOrder? On(World w, Side side, Berth berth) =>
        w.Orders.Find(o => o.Side == side && o.Kind == OrderKind.Ship && o.Paid && o.YardBodyId == berth.Body.Id && o.Berth == berth.Key);

    /// <summary>"ground" → "Ground", "orbit0" → "Orbit", "orbit1" → "Orbit 2".</summary>
    public static string BerthName(string? key) => key == null || key == Ground ? "Ground" : key == "orbit0" ? "Orbit" : "Orbit " + (key[5] - '0' + 1);

    // ---- Yard planets and the home yard ----------------------------------------------------------------

    /// <summary>A planet that keeps its side in the game: a Shipyard, or a finished station with a great or capital slipway.</summary>
    public static bool IsYardPlanet(World w, Side side, Body b) =>
        b.Owner == side && (b.Shipyard || Starbases.WorkingAt(w, b.Id, side) is Starbase st && st.Fit.Any(id => GameData.Module(id)?.Bay == "slipway"));

    /// <summary>The side's yard planets, Shipyards first.</summary>
    public static List<Body> YardPlanets(World w, Side side) =>
        w.Bodies.Where(b => IsYardPlanet(w, side, b)).OrderBy(b => b.Shipyard ? 0 : 1).ThenBy(b => b.Id).ToList();

    /// <summary>The home yard: the one chosen (or the first) while it stands, else the next yard planet (Shipyards first, nearest the old one).</summary>
    public static Body? Home(World w, Side side)
    {
        var s = Economy.Of(w, side);
        var old = s.HomeYardBodyId is int id ? w.FindBody(id) : null;
        if (old != null && IsYardPlanet(w, side, old)) return old;
        Body? best = null;
        foreach (var b in w.Bodies)
        {
            if (!IsYardPlanet(w, side, b)) continue;
            if (best == null || Better(b, best, old)) best = b;
        }
        return best;
    }

    private static bool Better(Body a, Body b, Body? near)
    {
        if (a.Shipyard != b.Shipyard) return a.Shipyard;
        if (near != null)
        {
            var da = Vec2.Distance(a.Position, near.Position);
            var db = Vec2.Distance(b.Position, near.Position);
            if (Math.Abs(da - db) > 1e-6) return da < db;
        }
        return a.Id < b.Id;
    }

    /// <summary>Why every yard is stopped (the home yard fell), or null.</summary>
    public static string? Stopped(World w, Side side) =>
        Economy.Of(w, side).YardsStoppedUntilHour > w.Hour
            ? $"every yard stopped for {Math.Ceiling((Economy.Of(w, side).YardsStoppedUntilHour - w.Hour) / 24):0} more days: the home yard fell"
            : null;

    /// <summary>Why this side can't make this planet its home yard, or null.</summary>
    public static string? CanMakeHome(World w, Side side, int bodyId)
    {
        if (w.FindBody(bodyId) is not Body b) return "Unknown planet";
        if (!IsYardPlanet(w, side, b)) return $"{b.Name} is not a yard planet: it needs a Shipyard, or a station with a great slipway";
        if (Home(w, side)?.Id == b.Id) return $"{b.Name} is already the home yard";
        return null;
    }

    // ---- Each hour --------------------------------------------------------------------------------

    /// <summary>Hourly in full games: upgrades under way, and the home yard (it moves if it fell, and the yards stop).</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        foreach (var b in w.Bodies)
        {
            if (b.YardUpgradeHoursLeft is not double left) continue;
            if (b.Owner is not Side owner || b.Shipyard || !Colonies.Built(b).Any(p => p.Kind == Colonies.DryDock))
            {
                b.YardUpgradeHoursLeft = null;
                sim.Log.Info("yards", "Shipyard upgrade called off", new { bodyId = b.Id, side = b.Owner?.ToString() });
                continue;
            }
            if (Economy.Blockaded(w, b)) continue;
            left -= GameTime.HoursPerTick;
            if (left > 1e-9) { b.YardUpgradeHoursLeft = left; continue; }
            b.YardUpgradeHoursLeft = null;
            var dock = Colonies.Built(b).Where(p => p.Kind == Colonies.DryDock).OrderBy(p => p.Id).First();
            dock.Kind = Colonies.Shipyard;
            Colonies.SyncBuildings(b);
            b.Shipyard = true;
            b.ShipyardArmour = Y.Armour;
            sim.Log.Info("yards", "New shipyard", new { bodyId = b.Id, name = b.Name, side = owner.ToString() });
        }
        foreach (var side in Sides.InPlay(w))
        {
            var s = Economy.Of(w, side);
            var home = Home(w, side);
            if (s.HomeYardBodyId is int was && home != null && home.Id != was)
            {
                s.YardsStoppedUntilHour = w.Hour + Y.HomeLossStopDays * 24;
                sim.Log.Info("yards", "Home yard lost: the seat moves and every yard stops", new { side = side.ToString(), from = was, to = home.Id, toName = home.Name, days = Y.HomeLossStopDays });
            }
            if (home != null) s.HomeYardBodyId = home.Id;
        }
    }

    /// <summary>
    /// A planet's Shipyard is gone (destroyed from orbit, or the planet taken): what is left of it is a dry dock,
    /// and the hull on its ground berth is lost.
    /// </summary>
    public static void LoseShipyard(Simulation sim, Body b, Side loser, string why)
    {
        var w = sim.World;
        b.Shipyard = false;
        b.ShipyardArmour = 0;
        b.YardUpgradeHoursLeft = null;
        foreach (var p in b.Pieces.Where(p => p.Kind == Colonies.Shipyard)) p.Kind = Colonies.DryDock;
        Colonies.SyncBuildings(b);
        foreach (var o in w.Orders.Where(o => o.Side == loser && o.Kind == OrderKind.Ship && o.Paid && o.YardBodyId == b.Id && o.Berth == Ground).ToList())
        {
            w.Orders.Remove(o);
            sim.Log.Info("yards", "Hull lost with its yard", new { side = loser.ToString(), o.Id, o.Key, bodyId = b.Id, why });
        }
    }

    // ---- Placing orders ---------------------------------------------------------------------------

    /// <summary>Where Auto measures from: the fleet the ship joins, else the home yard.</summary>
    public static Vec2 Target(World w, Side side, WorkOrder o) =>
        o.JoinFleetId is int j && w.FindFleet(j) is Fleet f && f.Side == side ? f.Position
        : Home(w, side)?.Position ?? Light.Seat(w, side);

    /// <summary>
    /// The berths an order may use, best first: pinned to its planet, or (on Auto) any; settler ships only at the
    /// home yard (the settlers are its people). Best: has the materials, then nearest the target, then the
    /// smallest berth that takes the hull (keeping slipways free for big hulls).
    /// </summary>
    public static List<Berth> Choices(World w, Side side, WorkOrder o, IEnumerable<Berth> berths)
    {
        var home = Home(w, side);
        var mats = Races.Materials(w, side, Economy.MaterialsOf(o));
        var target = Target(w, side, o);
        return berths.Where(b => Fits(b, o.Key)
                                 && (o.SettleBodyId != null ? b.Body == home : !o.YardPinned || b.Body.Id == o.YardBodyId))
            .OrderBy(b => Economy.MaterialShort(b.Body, mats) == null ? 0 : 1)
            .ThenBy(b => Vec2.Distance(b.Body.Position, target)).ThenBy(b => b.MaxM).ThenBy(b => b.Body.Id).ThenBy(b => b.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Why a started hull is lost (its planet, ground yard or station is gone), or null.</summary>
    public static string? LostWhy(World w, Side side, WorkOrder o)
    {
        if (o.YardBodyId is not int id || w.FindBody(id) is not Body b || b.Owner != side) return "its planet was lost";
        if (o.Berth == Ground) return b.Shipyard || Colonies.Built(b).Any(p => p.Kind == Colonies.DryDock) ? null : "its yard was destroyed";
        if (Starbases.Of(w, b.Id, side) is not Starbase st) return "its station was lost";
        var n = o.Berth is { Length: > 5 } k && k.StartsWith("orbit", StringComparison.Ordinal) ? k[5] - '0' : 0;
        return YardBays(st.Fit) > n ? null : "its station's yard bay was taken out";
    }

    /// <summary>A started order from an old save with no berth: the home yard's ground berth, or a station bay that takes it.</summary>
    public static void Settle(World w, Side side, WorkOrder o)
    {
        o.YardBodyId ??= Home(w, side)?.Id;
        if (o.Berth != null || o.YardBodyId is not int id || w.FindBody(id) is not Body b) return;
        var taken = w.Orders.Where(x => x != o && x.Side == side && x.Paid && x.YardBodyId == id).Select(x => x.Berth).ToHashSet();
        o.Berth = At(w, side, b).Where(x => Fits(x, o.Key) && !taken.Contains(x.Key)).Select(x => x.Key).FirstOrDefault() ?? Ground;
    }

    // ---- Upgrading a dry dock -------------------------------------------------------------------------

    public static Dictionary<string, double> UpgradeMaterials => Y.UpgradeMaterials ?? new Dictionary<string, double>();

    /// <summary>Why this side can't upgrade the dry dock at this planet to a Shipyard, or null.</summary>
    public static string? CanUpgrade(World w, Side side, int bodyId)
    {
        if (!w.Loop) return "Only in a full game";
        if (w.FindBody(bodyId) is not Body b) return "Unknown planet";
        if (b.Owner != side) return $"{b.Name} is not yours";
        if (b.Shipyard) return $"{b.Name} already has a Shipyard";
        if (b.YardUpgradeHoursLeft != null) return "Already being upgraded";
        if (!Tech.Has(w, side, "colony-shipyards")) return "Research Colony shipyards first";
        if (!Colonies.Built(b).Any(p => p.Kind == Colonies.DryDock)) return "Build a dry dock here first";
        if (b.PopK < Y.UpgradeMinPopK - 1e-9) return $"Needs {Y.UpgradeMinPopK:0}k people ({b.Name} has {b.PopK:0.#}k)";
        if (Economy.Blockaded(w, b)) return $"{b.Name} is blockaded";
        if (Economy.MaterialShort(b, UpgradeMaterials) is string shortOf) return char.ToUpperInvariant(shortOf[0]) + shortOf[1..];
        if (Budget.Problem(w, side, Pot.Buildings, Y.UpgradeCost, "yard-upgrade:" + b.Id) is string tooLow) return tooLow;
        return null;
    }

    /// <summary>"Upgrade dry dock to Shipyard: 180 Industry, 40 alloys, 10 electronics, 15 days".</summary>
    public static string UpgradeText() =>
        $"{Y.UpgradeCost:0} Industry, " + string.Join(", ", UpgradeMaterials.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Value:0} {Colonies.GoodName(m.Key).ToLowerInvariant()}"))
        + $", {Y.UpgradeDays:0} days. Builds hulls up to {Starbases.YardSize("yard").MaxLengthM:0} m at full speed, and keeps you in the game while it stands";
}

// ---- Commands ---------------------------------------------------------------------------------

/// <summary>Upgrade the dry dock at a colony to a Shipyard: paid now (Buildings pot and materials from its store), done after upgradeDays.</summary>
public sealed record UpgradeYardCommand(Side Side, int BodyId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => (Side, world.FindBody(BodyId)?.Position ?? Light.Seat(world, Side));

    public override void Apply(World world, Simulation sim)
    {
        if (Yards.CanUpgrade(world, Side, BodyId) is string problem)
        {
            sim.Log.Warn("yards", "Can't upgrade to a shipyard", new { side = Side.ToString(), BodyId, problem, Source });
            return;
        }
        var b = world.FindBody(BodyId)!;
        var y = GameData.Economy.Shipyard;
        Budget.Pay(world, Side, Pot.Buildings, y.UpgradeCost, "yard-upgrade:" + b.Id, b.Id, log: sim.Log);
        Economy.Of(world, Side).Totals.IndustrySpent += y.UpgradeCost;
        Economy.TakeMaterials(world, b, Yards.UpgradeMaterials, "shipyard upgrade");
        b.YardUpgradeHoursLeft = y.UpgradeDays * 24;
        Choices.Note(world, Side, "building", Colonies.Shipyard, b.Name);
        sim.Log.Info("yards", "Shipyard upgrade started", new { side = Side.ToString(), BodyId, name = b.Name, days = y.UpgradeDays, Source });
    }
}

/// <summary>Pin a ship order to a planet's yard, or (null) hand it back to Auto. Only before it starts.</summary>
public sealed record SetOrderYardCommand(Side Side, int OrderId, int? BodyId) : GameCommand
{
    /// <summary>A home order: at once, even while paused.</summary>
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        var order = world.Orders.Find(o => o.Id == OrderId && o.Side == Side && o.Kind == OrderKind.Ship);
        string? problem = order == null ? "No such order" : order.Paid ? "Already being built" : order.SettleBodyId != null ? "Settler ships are built at the home yard" : null;
        if (problem == null && BodyId is int id)
            problem = world.FindBody(id) is not Body b ? "Unknown planet"
                : !Yards.At(world, Side, b).Any(x => Yards.Fits(x, order!.Key)) ? $"No berth at {b.Name} takes a {GameData.ShipClass(order!.Key).Name.ToLowerInvariant()}" : null;
        if (problem != null)
        {
            sim.Log.Warn("yards", "Can't pick that yard", new { side = Side.ToString(), OrderId, BodyId, problem, Source });
            return;
        }
        order!.YardPinned = BodyId != null;
        if (BodyId != null) order.YardBodyId = BodyId;
        sim.Log.Info("yards", "Ship order yard picked", new { side = Side.ToString(), OrderId, BodyId, Source });
    }
}

/// <summary>Make a yard planet the home yard (the side's seat). No stop: that only follows a home yard that falls.</summary>
public sealed record SetHomeYardCommand(Side Side, int BodyId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (Yards.CanMakeHome(world, Side, BodyId) is string problem)
        {
            sim.Log.Warn("yards", "Can't make that the home yard", new { side = Side.ToString(), BodyId, problem, Source });
            return;
        }
        Economy.Of(world, Side).HomeYardBodyId = BodyId;
        sim.Log.Info("yards", "Home yard moved", new { side = Side.ToString(), BodyId, Source });
    }
}
