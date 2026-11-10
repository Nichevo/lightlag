namespace Lightlag.Sim.Core;

/// <summary>A messenger drone: a small fast craft carrying orders to a fleet that light can't reach.</summary>
public sealed class Drone
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public int TargetFleetId { get; set; }
    public Vec2 Position { get; set; }
    public List<GameCommand> Orders { get; set; } = new();
    public double LaunchHour { get; set; }
}

/// <summary>Numbers for messenger drones (ship-classes.json, "drones").</summary>
public sealed record DroneData(double SpeedMmPerHour = 20000, double Cost = 5, double ShotRangeMm = 40, double KillPerHour = 3, double ReachMm = 1);

/// <summary>
/// Messenger drones. An order held up because a planet blocks the signal can be sent by drone instead:
/// it flies from the flagship straight to the fleet (round anything in the way) and hands the order
/// over when it gets there. Drones are slower than light and easy to shoot down: an enemy fleet that
/// sees one within railgun reach soon kills it.
/// </summary>
public static class Drones
{
    public static DroneData D => GameData.Drones;

    /// <summary>Orders to this fleet waiting for its signal to clear (they have arrived but can't get through).</summary>
    public static List<Transit> Blocked(World w, int fleetId) =>
        w.Light.InTransit.Where(t => t.Command is IFleetCommand fc && fc.FleetId == fleetId && t.ArriveHour <= w.Hour + 1e-9).ToList();

    /// <summary>Orders to this fleet still on their way or waiting (what a drone would carry).</summary>
    public static List<Transit> Pending(World w, Side side, int fleetId) =>
        w.Light.InTransit.Where(t => t.Side == side && t.Command is IFleetCommand fc && fc.FleetId == fleetId).ToList();

    public static string? CanSend(World w, Side side, int fleetId)
    {
        if (!w.Light.On) return "Drones carry orders under light lag only";
        if (w.FindFleet(fleetId) is not Fleet f || f.Side != side) return "Not one of your fleets";
        if (Pending(w, side, fleetId).Count == 0) return "No orders on their way to it";
        if (w.Loop && Economy.Of(w, side).Industry < D.Cost) return $"Needs {D.Cost:0} Industry";
        return null;
    }

    /// <summary>Each step: drones fly on, deliver, or are shot down.</summary>
    public static void Move(Simulation sim, double dt)
    {
        var w = sim.World;
        if (w.Drones.Count == 0 || dt <= 0) return;
        foreach (var d in w.Drones.ToList())
        {
            if (w.FindFleet(d.TargetFleetId) is not Fleet f || f.ShipIds.Count == 0)
            {
                w.Drones.Remove(d);
                sim.Log.Info("drone", "A drone lost its fleet", new { droneId = d.Id, side = d.Side.ToString() });
                continue;
            }
            var gap = f.Position - d.Position;
            var step = D.SpeedMmPerHour * dt;
            d.Position = gap.Length <= step ? f.Position : d.Position + gap.Normalized() * step;
            if (Vec2.Distance(d.Position, f.Position) <= D.ReachMm)
            {
                w.Drones.Remove(d);
                sim.Log.Info("drone", $"A drone reaches {f.Name}", new { droneId = d.Id, fleetId = f.Id, orders = d.Orders.Count, hours = w.Hour - d.LaunchHour });
                foreach (var c in d.Orders) sim.Carry(c);
                continue;
            }
            // Enemy fleets that see it close by shoot it down.
            foreach (var e in w.Fleets)
            {
                if (e.Side == d.Side || Vec2.Distance(e.Position, d.Position) > D.ShotRangeMm || !Combat.HasWorking(w, e, Module.Railguns)) continue;
                if (sim.Rng.NextDouble() < Math.Min(1, D.KillPerHour * dt))
                {
                    w.Drones.Remove(d);
                    sim.Log.Info("drone", $"{e.Name} shoots down a drone", new { droneId = d.Id, fleetId = e.Id, side = d.Side.ToString() });
                    break;
                }
            }
        }
    }
}

/// <summary>Send the orders on their way to a fleet by messenger drone instead (for a fleet behind a planet).</summary>
public sealed record SendDroneCommand(Side Side, int FleetId) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (Drones.CanSend(world, Side, FleetId) is string problem)
        {
            sim.Log.Warn("drone", $"Can't send a drone: {problem}", new { side = Side.ToString(), FleetId, Source });
            return;
        }
        var orders = Drones.Pending(world, Side, FleetId);
        foreach (var t in orders) world.Light.InTransit.Remove(t);
        if (world.Loop) Economy.Of(world, Side).Industry -= Drones.D.Cost;
        world.Drones.Add(new Drone
        {
            Id = world.NextId++, Side = Side, TargetFleetId = FleetId, Position = Light.Seat(world, Side), LaunchHour = world.Hour,
            Orders = orders.Select(t => t.Command).ToList(),
        });
        sim.Log.Info("drone", $"{Side} sends a drone to {world.FindFleet(FleetId)!.Name}", new { side = Side.ToString(), FleetId, orders = orders.Count, Source });
    }
}
