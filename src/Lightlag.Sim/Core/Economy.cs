using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

// ---- Data (Data/economy.json) -----------------------------------------------------------

public sealed record ShipyardData(double Armour, double RepairPerDay, double RailDamagePerHour, double RailSuppliesPerHour,
    double MissileDamage, double MissileEveryHours);

/// <summary>One research upgrade. What Factor does depends on the upgrade (see Tech).</summary>
public sealed record ResearchData(string Id, string Name, string Effect, double Cost, double Days, double Factor);

public sealed record EconomyData(double StartIndustry, double BuildIndustryPerDay, double ClaimDays, double HoldToWinDays,
    ShipyardData Shipyard, List<ResearchData> Research, double ClaimHeldDays = 0);

// ---- State --------------------------------------------------------------------------------

/// <summary>What one side has: its Industry stock, what it has researched, and how long it has held most planets.</summary>
public sealed class SideState
{
    public Side Side { get; set; }
    public double Industry { get; set; }
    public List<string> Researched { get; set; } = new();

    /// <summary>When this side started holding more than half the planets (null while it doesn't).</summary>
    public double? MajoritySinceHour { get; set; }
}

public enum OrderKind { Ship, Research }

/// <summary>A ship being built at the shipyard, or an upgrade being researched. Paid for when it starts.</summary>
public sealed class WorkOrder
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public OrderKind Kind { get; set; }

    /// <summary>The ship class id or research id.</summary>
    public string Key { get; set; } = "";
    public bool Paid { get; set; }

    /// <summary>Ship orders: the fleet the new ship flies out to join (null: the fleet at the shipyard).</summary>
    public int? JoinFleetId { get; set; }
    public double HoursLeft { get; set; }
    public double TotalHours { get; set; }
}

// ---- Research effects ---------------------------------------------------------------------

/// <summary>Where research changes the numbers. Every place that reads an upgradable number asks here.</summary>
public static class Tech
{
    public const string MissileBrain2 = "missile-brain-2";
    public const string PointDefence2 = "point-defence-2";
    public const string HeavyArmour = "heavy-armour";
    public const string EfficientDrives = "efficient-drives";
    public const string LongSensors = "long-sensors";
    public const string QuietDrives = "quiet-drives";
    public const string FastRailguns = "fast-railguns";
    public const string Automation = "automation";

    public static bool Has(World w, Side side, string id) => w.Sides[(int)side].Researched.Contains(id);

    /// <summary>The upgrade's factor once researched, otherwise 1.</summary>
    public static double Factor(World w, Side side, string id) => Has(w, side, id) ? GameData.Research(id).Factor : 1;

    public static double MaxArmour(World w, Ship s) => GameData.ShipClass(s.ClassId).Armour * Factor(w, s.Side, HeavyArmour);

    public static double SuppliesPerDeltaV(World w, Ship s) =>
        GameData.ShipClass(s.ClassId).SuppliesPerDeltaV * Factor(w, s.Side, EfficientDrives);
}

// ---- The loop -----------------------------------------------------------------------------

/// <summary>
/// The game loop around the battles: Industry income from held planets, the shipyard build queue,
/// research, planet claims, shipyard bombardment and the win check. Runs once per game hour, only in
/// full games (not the battle sandbox).
/// </summary>
public static class Economy
{
    private static EconomyData E => GameData.Economy;

    public static SideState Of(World w, Side side) => w.Sides[(int)side];

    /// <summary>The side's shipyard planet, if it still has one.</summary>
    public static Body? ShipyardOf(World w, Side side) => w.Bodies.Find(b => b.Shipyard && b.Owner == side);

    /// <summary>Where a side's fleets go to refill and repair: the shipyard, or else any planet it holds.</summary>
    public static Body? HomeOf(World w, Side side) => ShipyardOf(w, side) ?? w.Bodies.Find(b => b.Owner == side);

    /// <summary>Bodies worth holding (they make Industry). Holding more than half of them for a while wins.</summary>
    public static IEnumerable<Body> Claimable(World w) => w.Bodies.Where(b => b.Industry > 0);

    public static double IncomePerDay(World w, Side side) =>
        w.Bodies.Where(b => b.Owner == side).Sum(b => b.Industry) * Tech.Factor(w, side, Tech.Automation);

    public static double CostOf(WorkOrder o) => o.Kind == OrderKind.Ship
        ? GameData.ShipClass(o.Key).Cost
        : GameData.Research(o.Key).Cost;

    public static double HoursFor(OrderKind kind, string key) => kind == OrderKind.Ship
        ? GameData.ShipClass(key).Cost / E.BuildIndustryPerDay * 24
        : GameData.Research(key).Days * 24;

    public static string NameOf(WorkOrder o) => o.Kind == OrderKind.Ship ? GameData.ShipClass(o.Key).Name : GameData.Research(o.Key).Name;

    /// <summary>A fleet that is holding orbit at this body (not flying off).</summary>
    public static bool InOrbitAt(Fleet f, Body b) =>
        f.Hold == HoldStyle.Orbit && f.OrbitBodyId == b.Id && f.Flight is not { Started: true } && f.ShipIds.Count > 0;

    /// <summary>Sets up a new full game: start Industry, and each start planet gets a shipyard.</summary>
    public static void Start(World w)
    {
        w.Loop = true;
        foreach (var s in w.Sides) s.Industry = E.StartIndustry;
    }

    public static void AddShipyard(Body b, Side side)
    {
        b.Owner = side;
        b.Shipyard = true;
        b.ShipyardArmour = GameData.Economy.Shipyard.Armour;
    }

    /// <summary>One game hour of the loop. Called at each whole hour.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop || w.Winner != null) return;
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            Of(w, side).Industry += IncomePerDay(w, side) / 24;
            Work(sim, side, OrderKind.Ship);
            Work(sim, side, OrderKind.Research);
        }
        Claims(sim);
        Bombard(sim);
        CheckWin(sim);
    }

    private static void Work(Simulation sim, Side side, OrderKind kind)
    {
        var w = sim.World;
        var order = w.Orders.Find(o => o.Side == side && o.Kind == kind);
        if (order == null) return;
        if (kind == OrderKind.Ship && ShipyardOf(w, side) == null) return;
        var state = Of(w, side);
        if (!order.Paid)
        {
            var cost = CostOf(order);
            if (state.Industry < cost) return;
            state.Industry -= cost;
            order.Paid = true;
            order.HoursLeft = order.TotalHours;
            sim.Log.Info("economy", $"{side} starts {NameOf(order)}", new { side = side.ToString(), order.Id, order.Key, cost });
        }
        order.HoursLeft -= GameTime.HoursPerTick;
        if (order.HoursLeft > 1e-9) return;
        w.Orders.Remove(order);
        if (kind == OrderKind.Ship) Launch(sim, side, order.Key, order.JoinFleetId);
        else Learn(sim, side, order.Key);
    }

    private static void Learn(Simulation sim, Side side, string id)
    {
        var w = sim.World;
        Of(w, side).Researched.Add(id);
        if (id == Tech.MissileBrain2) w.MissileBrain[(int)side] = Math.Max(w.MissileBrain[(int)side], 2);
        sim.Log.Info("economy", $"{side} finishes research: {GameData.Research(id).Name}", new { side = side.ToString(), id });
    }

    /// <summary>
    /// A new ship leaves the shipyard. With no fleet named it joins a fleet waiting there, or forms a new one.
    /// With a fleet named it joins it at once if it is at the shipyard, or forms its own fleet and flies out to it.
    /// </summary>
    private static void Launch(Simulation sim, Side side, string classId, int? joinFleetId = null)
    {
        var w = sim.World;
        var yard = ShipyardOf(w, side)!;
        var ship = NewShip(w, side, classId);
        var target = joinFleetId is int j && w.FindFleet(j) is Fleet t && t.Side == side ? t : null;
        Fleet? waiting;
        if (target != null)
            waiting = InOrbitAt(target, yard) && target.Flight == null && !target.Retreating ? target : null;
        else
            waiting = w.Fleets.FirstOrDefault(f => f.Side == side && f.Flight == null && f.JoinFleetId == null && !f.Retreating
                                                   && InOrbitAt(f, yard));
        if (waiting != null)
        {
            waiting.ShipIds.Add(ship.Id);
            ship.FleetId = waiting.Id;
            Fleets.Arrange(w, waiting);
        }
        else
        {
            waiting = Fleets.Create(w, side, new[] { ship }, yard.Position);
            var angle = sim.Rng.Range(0, Math.PI * 2);
            waiting.Hold = HoldStyle.Orbit;
            waiting.OrbitBodyId = yard.Id;
            waiting.OrbitRadius = Fleets.OrbitRadiusFor(yard);
            waiting.OrbitAngle = angle;
            waiting.Position = yard.Position + new Vec2(DetMath.Cos(angle), DetMath.Sin(angle)) * waiting.OrbitRadius;
        }
        foreach (var s in w.ShipsOf(waiting)) s.Position = waiting.Position + s.FormationOffset;
        sim.Log.Info("economy", $"{ship.Name} is launched at {yard.Name} and joins {waiting.Name}",
            new { side = side.ToString(), shipId = ship.Id, classId, fleetId = waiting.Id });
        if (target != null && waiting != target)
        {
            waiting.JoinFleetId = target.Id;
            if (Fleets.FlyToJoin(w, waiting, target, ReinforceSpeed) is string problem)
                sim.Log.Info("economy", $"{waiting.Name} waits at {yard.Name} to join {target.Name}", new { fleetId = waiting.Id, target = target.Id, problem });
            else
                sim.Log.Info("economy", $"{waiting.Name} flies out to join {target.Name}",
                    new { fleetId = waiting.Id, target = target.Id, arrive = waiting.Flight!.ArriveHour });
        }
    }

    /// <summary>Speed (share of top speed) new ships fly at to reach the fleet they were built for.</summary>
    public const double ReinforceSpeed = 0.7;

    /// <summary>Makes a ship with full supplies and armour, numbered after the side's earlier ships of its class.</summary>
    public static Ship NewShip(World w, Side side, string classId)
    {
        var cls = GameData.ShipClass(classId);
        var key = $"{side}:{cls.Id}";
        var number = w.ShipNumbers[key] = w.ShipNumbers.GetValueOrDefault(key) + 1;
        var ship = new Ship
        {
            Id = w.NextId++, Name = $"{cls.Short}-{(side == Side.Player ? "" : "R")}{number}", Side = side, ClassId = cls.Id,
            MaxSpeed = cls.MaxSpeedMmPerHour, Accel = cls.AccelMmPerHour2, Supplies = cls.Supplies, Missiles = cls.Missiles,
        };
        ship.Armour = Tech.MaxArmour(w, ship);
        w.Ships.Add(ship);
        return ship;
    }

    /// <summary>Hours of holding orbit to claim a body: longer to take one the other side holds than an empty one.</summary>
    public static double ClaimHoursFor(Body b) =>
        (b.Owner != null && E.ClaimHeldDays > 0 ? E.ClaimHeldDays : E.ClaimDays) * 24;

    /// <summary>
    /// A side that holds orbit at a body with no enemy fleet in orbit there claims it after ClaimDays
    /// (ClaimHeldDays for a body the other side holds).
    /// Leaving, or an enemy arriving, resets the count. Shipyard planets can't be claimed, only destroyed.
    /// </summary>
    private static void Claims(Simulation sim)
    {
        var w = sim.World;
        foreach (var b in Claimable(w))
        {
            var need = ClaimHoursFor(b);
            if (b.Shipyard) { b.ClaimSide = null; b.ClaimHours = 0; continue; }
            var sides = w.Fleets.Where(f => InOrbitAt(f, b)).Select(f => f.Side).Distinct().ToList();
            if (sides.Count != 1 || b.Owner == sides[0])
            {
                b.ClaimSide = null;
                b.ClaimHours = 0;
                continue;
            }
            var side = sides[0];
            if (b.ClaimSide != side)
            {
                b.ClaimSide = side;
                b.ClaimHours = 0;
                sim.Log.Info("economy", $"{side} starts claiming {b.Name}", new { side = side.ToString(), bodyId = b.Id });
            }
            b.ClaimHours += GameTime.HoursPerTick;
            if (b.ClaimHours < need) continue;
            var before = b.Owner;
            b.Owner = side;
            b.ClaimSide = null;
            b.ClaimHours = 0;
            sim.Log.Info("economy", $"{side} claims {b.Name}", new { side = side.ToString(), bodyId = b.Id, from = before?.ToString() });
        }
    }

    /// <summary>
    /// An armed fleet in orbit at an enemy shipyard with no defending fleet in contact range wears the
    /// shipyard down: railguns every hour, a missile from each destroyer every few hours. Shipyards mend
    /// slowly when left alone.
    /// </summary>
    private static void Bombard(Simulation sim)
    {
        var w = sim.World;
        var y = E.Shipyard;
        foreach (var b in w.Bodies.Where(b => b.Shipyard && b.Owner != null).ToList())
        {
            var owner = b.Owner!.Value;
            var attackers = w.Fleets.Where(f => f.Side != owner && InOrbitAt(f, b)).ToList();
            var defended = w.Fleets.Any(f => f.Side == owner && Vec2.Distance(f.Position, b.Position) <= GameData.Combat.ContactRangeMm);
            double damage = 0;
            if (attackers.Count > 0 && !defended)
            {
                foreach (var s in attackers.SelectMany(w.ShipsOf))
                {
                    var cls = GameData.ShipClass(s.ClassId);
                    if (cls.Railguns > 0 && s.Disabled != Module.Railguns && s.Supplies >= cls.Railguns * y.RailSuppliesPerHour)
                    {
                        s.Supplies -= cls.Railguns * y.RailSuppliesPerHour;
                        damage += cls.Railguns * y.RailDamagePerHour;
                    }
                    if (s.Missiles > 0 && s.Disabled != Module.Missiles && w.Tick % (long)Math.Max(1, y.MissileEveryHours) == 0)
                    {
                        s.Missiles--;
                        damage += y.MissileDamage;
                    }
                }
            }
            if (damage > 0)
            {
                if (b.LastBombardedHour == null || w.Hour - b.LastBombardedHour > 24)
                    sim.Log.Info("economy", $"{b.Name} shipyard is under attack", new { bodyId = b.Id, attacker = attackers[0].Side.ToString() });
                b.LastBombardedHour = w.Hour;
                b.ShipyardArmour -= damage;
                if (b.ShipyardArmour <= 0)
                {
                    b.ShipyardArmour = 0;
                    b.Shipyard = false;
                    w.Orders.RemoveAll(o => o.Side == owner && o.Kind == OrderKind.Ship);
                    sim.Log.Info("economy", $"{b.Name} shipyard is destroyed", new { bodyId = b.Id, side = owner.ToString() });
                }
            }
            else if (b.LastBombardedHour == null || w.Hour - b.LastBombardedHour > 24)
            {
                b.ShipyardArmour = Math.Min(y.Armour, b.ShipyardArmour + y.Armour * y.RepairPerDay / 24);
            }
        }
    }

    private static void CheckWin(Simulation sim)
    {
        var w = sim.World;
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            if (ShipyardOf(w, side) != null) continue;
            var other = side == Side.Player ? Side.Rival : Side.Player;
            Win(sim, other, other == Side.Player ? "destroyed the rival shipyard" : "destroyed your shipyard");
            return;
        }
        var total = Claimable(w).Count();
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var state = Of(w, side);
            var held = Claimable(w).Count(b => b.Owner == side);
            if (held * 2 <= total)
            {
                state.MajoritySinceHour = null;
                continue;
            }
            if (state.MajoritySinceHour == null)
            {
                state.MajoritySinceHour = w.Hour;
                sim.Log.Info("economy", $"{side} holds most planets", new { side = side.ToString(), held, total });
            }
            if (w.Hour - state.MajoritySinceHour >= E.HoldToWinDays * 24)
            {
                Win(sim, side, $"held most planets for {E.HoldToWinDays:0} days");
                return;
            }
        }
    }

    private static void Win(Simulation sim, Side side, string reason)
    {
        var w = sim.World;
        w.Winner = side;
        w.WinReason = reason;
        w.WinHour = w.Hour;
        sim.Log.Info("game", $"{side} wins: {reason}", new { winner = side.ToString(), reason, day = w.Day });
    }

    /// <summary>Why this order can't be queued, or null if it can.</summary>
    public static string? CanQueue(World w, Side side, OrderKind kind, string key)
    {
        if (!w.Loop) return "There is no economy in the battle sandbox";
        if (w.Winner != null) return "The game is over";
        if (kind == OrderKind.Ship)
        {
            if (!GameData.ShipClasses.ContainsKey(key)) return $"Unknown ship class '{key}'";
            if (ShipyardOf(w, side) == null) return "No shipyard";
            return null;
        }
        if (!GameData.ResearchById.ContainsKey(key)) return $"Unknown research '{key}'";
        if (Of(w, side).Researched.Contains(key)) return "Already researched";
        if (w.Orders.Any(o => o.Side == side && o.Kind == OrderKind.Research && o.Key == key)) return "Already in the queue";
        return null;
    }
}

// ---- Commands -------------------------------------------------------------------------------

/// <summary>Add a ship to the shipyard queue, or an upgrade to the research queue.</summary>
public sealed record QueueOrderCommand(Side Side, OrderKind Kind, string Key, int? JoinFleetId = null) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (Economy.CanQueue(world, Side, Kind, Key) is string problem)
        {
            sim.Log.Warn("economy", $"Can't queue {Key}: {problem}", new { side = Side.ToString(), Kind, Key, Source });
            return;
        }
        var order = new WorkOrder { Id = world.NextId++, Side = Side, Kind = Kind, Key = Key, TotalHours = Economy.HoursFor(Kind, Key) };
        if (Kind == OrderKind.Ship && JoinFleetId is int j && world.FindFleet(j)?.Side == Side) order.JoinFleetId = j;
        order.HoursLeft = order.TotalHours;
        world.Orders.Add(order);
        sim.Log.Info("economy", $"{Side} queues {Economy.NameOf(order)}", new { side = Side.ToString(), order.Id, Kind, Key, Source });
    }
}

/// <summary>Choose the fleet a ship being built will join (null: the fleet at the shipyard).</summary>
public sealed record SetOrderFleetCommand(Side Side, int OrderId, int? JoinFleetId) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        var order = world.Orders.Find(o => o.Id == OrderId && o.Side == Side && o.Kind == OrderKind.Ship);
        if (order == null)
        {
            sim.Log.Warn("economy", "Fleet choice for an order that does not exist", new { side = Side.ToString(), OrderId, Source });
            return;
        }
        if (JoinFleetId is int j && world.FindFleet(j)?.Side != Side)
        {
            sim.Log.Warn("economy", "Fleet choice for a fleet that is not ours", new { side = Side.ToString(), OrderId, JoinFleetId, Source });
            return;
        }
        order.JoinFleetId = JoinFleetId;
        sim.Log.Info("economy", "New ship will join a fleet", new { side = Side.ToString(), OrderId, JoinFleetId, Source });
    }
}

/// <summary>Take an order off its queue. If it was already started, its Industry comes back.</summary>
public sealed record CancelOrderCommand(Side Side, int OrderId) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        var order = world.Orders.Find(o => o.Id == OrderId && o.Side == Side);
        if (order == null)
        {
            sim.Log.Warn("economy", "Cancel for an order that does not exist", new { side = Side.ToString(), OrderId, Source });
            return;
        }
        world.Orders.Remove(order);
        if (order.Paid) Economy.Of(world, Side).Industry += Economy.CostOf(order);
        sim.Log.Info("economy", $"{Side} cancels {Economy.NameOf(order)}", new { side = Side.ToString(), OrderId, refunded = order.Paid, Source });
    }
}
