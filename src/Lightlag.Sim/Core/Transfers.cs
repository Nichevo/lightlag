namespace Lightlag.Sim.Core;

/// <summary>Ships of a fleet waiting to fly to another fleet once it is within Transfers.WaitWithinHours.</summary>
public sealed class PendingTransfer
{
    public List<int> ShipIds { get; set; } = new();
    public int ToFleetId { get; set; }
    public double SinceHour { get; set; }
}

/// <summary>What moving some ships to another fleet would take: at once (together), or a trip of so many hours and fuel.</summary>
public sealed record TransferEstimate(bool Together, double Hours, double FuelShare, string? Problem, double Speed = Economy.ReinforceSpeed)
{
    /// <summary>A long trip the screen warns about (and offers to wait until the fleets are closer).</summary>
    public bool Long => !Together && Problem == null && Hours > Transfers.WaitWithinHours;
}

/// <summary>
/// Moving ships between fleets of a side, however far apart. Fleets at the same place swap ships at once. Otherwise
/// the ships split off as a detachment ("To Defence fleet") that flies to the other fleet at the reinforcement speed
/// and joins it on arrival (the usual join: it follows a fleet that moves on). A long trip can instead wait: the
/// ships stay with their fleet until the other fleet is within a day, then go.
/// Also the fleet lock: a locked fleet keeps exactly the ships it has (its roster).
/// </summary>
public static class Transfers
{
    /// <summary>"Wait until closer" sends the ships once the trip would take at most this long (hours).</summary>
    public const double WaitWithinHours = 24;

    /// <summary>The trip a detachment of these ships would make to join that fleet, planned as it would fly. Changes nothing.</summary>
    public static TransferEstimate Estimate(World w, Fleet from, IReadOnlyCollection<int> shipIds, Fleet to)
    {
        if (from.Id == to.Id) return new TransferEstimate(true, 0, 0, "it is the same fleet");
        if (from.Side != to.Side) return new TransferEstimate(false, 0, 0, "they are on different sides");
        if (Fleets.CanMerge(from, to) == null || Fleets.CanJoin(from, to)) return new TransferEstimate(true, 0, 0, null);
        var ids = shipIds.Where(from.ShipIds.Contains).Distinct().ToList();
        if (ids.Count == 0) return new TransferEstimate(false, 0, 0, "no ships picked");
        var probe = Probe(from, ids);
        var speed = Speed(w, probe);
        FlightPlan PlanAt(double fraction) => Fleets.Meeting(w, to, fraction) is FlightRequest req
            ? FlightPlanner.Plan(w, probe, req)
            : FlightPlanner.PlanChase(w, probe, to, ChaseMode.Join, fraction, FleetPerformance.Of(w, probe));
        var plan = PlanAt(speed);
        // Too slow to catch a fleet on the move at the usual 70 %: full speed may still do it.
        if ((!plan.Ok || plan.Flight == null) && speed < 1 && PlanAt(1.0) is { Ok: true, Flight: not null } fast)
        {
            plan = fast;
            speed = 1.0;
        }
        if (!plan.Ok || plan.Flight == null) return new TransferEstimate(false, 0, 0, plan.Problem ?? "no route", speed);
        return new TransferEstimate(false, Math.Max(0, plan.Flight.ArriveHour - w.Hour), plan.FuelShare, null, speed);
    }

    /// <summary>A stand-in fleet of just these ships, where the fleet is, for planning (never added to the world).</summary>
    private static Fleet Probe(Fleet from, List<int> ids) => new()
    {
        Id = from.Id,
        Side = from.Side,
        ShipIds = ids,
        Position = from.Position,
        Velocity = from.Velocity,
        Hold = from.Hold,
        OrbitBodyId = from.OrbitBodyId,
        OrbitRadius = from.OrbitRadius,
        OrbitAngle = from.OrbitAngle,
        Fuel = from.Fuel,
        Docked = from.Docked,
    };

    /// <summary>Detachments fly at the speed new ships use to reach their fleet (troop ships at full speed).</summary>
    private static double Speed(World w, Fleet f) => Economy.JoinSpeed(w, f);

    /// <summary>Why a fleet may not be added to another, or lose its ships to it, because of a lock (null: it may).</summary>
    public static string? LockProblem(World w, Fleet into, Fleet from)
    {
        if (into.Locked && from.ShipIds.Any(id => !into.Roster.Contains(id))) return $"{into.Name} is locked";
        // A locked fleet's ships are not gathered into another fleet (its own ships going back to it are fine).
        if (from.Locked && from.ShipIds.Any(id => !into.Roster.Contains(id))) return $"{from.Name} is locked";
        return null;
    }

    /// <summary>Lock or unlock: a locked fleet keeps the ships it has now.</summary>
    public static void SetLock(Fleet f, bool locked)
    {
        f.Locked = locked;
        f.Roster = locked ? f.ShipIds.ToList() : new List<int>();
    }

    /// <summary>The player moved these ships into (or out of) a fleet by hand: a lock keeps them from now on (or lets them go).</summary>
    private static void Rosters(Fleet from, Fleet to, IEnumerable<int> ids)
    {
        foreach (var id in ids)
        {
            from.Roster.Remove(id);
            if (to.Locked && !to.Roster.Contains(id)) to.Roster.Add(id);
        }
    }

    /// <summary>Moves ships between two fleets at the same place now (all of them: the fleets merge).</summary>
    public static void MoveNow(World w, Simulation sim, Fleet from, Fleet to, List<int> ids, string source)
    {
        Rosters(from, to, ids);
        if (ids.Count == from.ShipIds.Count)
        {
            Fleets.Absorb(w, to, from);
            sim.Log.Info("fleet", "All ships moved: fleets merged", new { fleetId = from.Id, toFleetId = to.Id, source });
            return;
        }
        foreach (var id in ids)
        {
            from.ShipIds.Remove(id);
            to.ShipIds.Add(id);
            if (w.FindShip(id) is Ship s) s.FleetId = to.Id;
        }
        Fleets.Arrange(w, from);
        Fleets.Arrange(w, to);
        sim.Log.Info("fleet", "Ships moved between fleets", new { fleetId = from.Id, toFleetId = to.Id, ships = ids, source });
    }

    /// <summary>
    /// Sends the ships off to join the other fleet: as a detachment, or the whole fleet when it is all of its ships.
    /// Returns why it could not (and leaves everything as it was), or null.
    /// </summary>
    public static string? Send(World w, Simulation sim, Fleet from, Fleet to, List<int> ids, string source)
    {
        if (Fleets.CanMerge(from, to) == null || Fleets.CanJoin(from, to))
        {
            MoveNow(w, sim, from, to, ids, source);
            return null;
        }
        Fleet going;
        if (ids.Count == from.ShipIds.Count)
            going = from;
        else
        {
            var before = w.Fleets.Count;
            new SplitFleetCommand(from.Id, ids) { Source = source }.Apply(w, sim);
            if (w.Fleets.Count == before) return "the ships could not split off";
            going = w.Fleets[^1];
            going.Role = FleetRole.None; // a detachment on its way answers no calls
            going.Mission = Mission.None;
        }
        // The usual 70 %, else full speed (to catch a fleet on the move).
        var problem = Fleets.FlyToJoin(w, going, to, Speed(w, going));
        if (problem != null && Speed(w, going) < 1 && Fleets.FlyToJoin(w, going, to, 1.0) == null) problem = null;
        if (problem != null)
        {
            if (going != from) Fleets.Absorb(w, from, going);
            return problem;
        }
        Rosters(from, to, ids);
        going.JoinFleetId = to.Id;
        going.TransferTo = to.Id;
        going.ZoneId = null;
        FleetNames.Refresh(w, going);
        sim.Log.Info("fleet", "Ships fly to join another fleet", new
        {
            fleetId = going.Id, fromFleetId = from.Id, toFleetId = to.Id, ships = ids, arrive = going.Flight?.ArriveHour, source,
        });
        return null;
    }

    /// <summary>Once an hour: waiting transfers whose fleets are now close enough set off; ones that can't go any more are dropped.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        foreach (var f in w.Fleets.Where(f => f.Transfers.Count > 0).OrderBy(f => f.Id).ToList())
        {
            foreach (var t in f.Transfers.ToList())
            {
                if (!w.Fleets.Contains(f)) break;
                t.ShipIds.RemoveAll(id => !f.ShipIds.Contains(id));
                var to = w.FindFleet(t.ToFleetId);
                if (t.ShipIds.Count == 0 || to == null || to.ShipIds.Count == 0 || to.Side != f.Side)
                {
                    f.Transfers.Remove(t);
                    if (to == null || to.ShipIds.Count == 0)
                        Autonomy.Report(sim, f, $"{f.Name}: the ships waiting to join another fleet stay, that fleet is gone", NoteKind.StandingOrder);
                    continue;
                }
                // Busy fleets (in a fight, retreating) hold their ships until that is over.
                if (Heat.InBattle(w, f) || f.Retreating) continue;
                var est = Estimate(w, f, t.ShipIds, to);
                if (est.Problem != null || (!est.Together && est.Hours > WaitWithinHours)) continue;
                f.Transfers.Remove(t);
                var names = Names(w, t.ShipIds);
                if (Send(w, sim, f, to, t.ShipIds.ToList(), "transfer") is string problem)
                {
                    f.Refused = $"send {names} to {to.Name}: {problem}";
                    f.RefusedHour = w.Hour;
                    sim.Log.Warn("fleet", "Waiting transfer can't set off", new { fleetId = f.Id, toFleetId = to.Id, problem });
                    continue;
                }
                Autonomy.Report(sim, f, est.Together ? $"{names} joined {to.Name}" : $"{to.Name} is close now: {names} set off to join it", NoteKind.StandingOrder);
            }
        }
    }

    /// <summary>A trip's length in words: "20 h", "2.4 days".</summary>
    public static string Dur(double hours) => hours < 36 ? $"{Math.Max(1, Math.Round(hours)):0} h" : $"{hours / 24:0.#} days";

    /// <summary>"Hope and Resolve" / "3 ships".</summary>
    public static string Names(World w, IReadOnlyCollection<int> ids)
    {
        var names = ids.Select(id => w.FindShip(id)?.Name).Where(n => n != null).ToList();
        return names.Count switch { 0 => "no ships", 1 => names[0]!, 2 => $"{names[0]} and {names[1]}", _ => $"{names.Count} ships" };
    }

    /// <summary>The stage of every transfer to or from this fleet, for its status lines (fleets list, fleet panel).</summary>
    public static List<string> Status(World w, Fleet f)
    {
        var list = new List<string>();
        if (f.TransferTo is int t && f.JoinFleetId == t && w.FindFleet(t) is Fleet to)
            list.Add(f.Flight is Flight fl
                ? $"Flying to join {to.Name}, arrives in {Dur(Math.Max(0, fl.ArriveHour - w.Hour))}"
                : $"Waiting to join {to.Name}" + (f.Chase?.Problem is string p ? $" ({p})" : ""));
        foreach (var p in f.Transfers)
            if (w.FindFleet(p.ToFleetId) is Fleet dest)
                list.Add($"{Names(w, p.ShipIds)} wait to join {dest.Name}: "
                         + (Estimate(w, f, p.ShipIds, dest) is { Problem: string why } ? $"they go once they can ({why})" : "they go once it is within a day"));
        foreach (var g in w.Fleets.Where(g => g.Side == f.Side && g.TransferTo == f.Id && g.JoinFleetId == f.Id).OrderBy(g => g.Id))
            list.Add($"{Names(w, g.ShipIds)} joining" + (g.Flight is Flight gf ? $" in {Dur(Math.Max(0, gf.ArriveHour - w.Hour))}" : ""));
        foreach (var g in w.Fleets.Where(g => g.Side == f.Side && g.Id != f.Id))
            foreach (var p in g.Transfers.Where(p => p.ToFleetId == f.Id))
                list.Add($"{Names(w, p.ShipIds)} of {g.Name} will join when closer");
        return list;
    }
}

/// <summary>
/// Move some ships to another fleet of the side, near or far (an order to the fleet giving the ships). Together:
/// at once. Apart: they fly there as a detachment, or with WaitUntilClose and a trip over a day, they stay with
/// their fleet until the other fleet is within a day. Made on the fleets screen, so a lock on either fleet allows it.
/// </summary>
public sealed record TransferShipsCommand(int FleetId, List<int> ShipIds, int ToFleetId, bool WaitUntilClose = false) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Transfer") is not Fleet from) return;
        if (GetFleet(world, sim, ToFleetId, "Transfer") is not Fleet to) return;
        if (from.Id == to.Id || from.Side != to.Side) return;
        var ids = ShipIds.Where(from.ShipIds.Contains).Distinct().ToList();
        if (ids.Count == 0)
        {
            Refuse(world, sim, from, $"move ships to {to.Name}: none of them are in {from.Name} now", new { FleetId, ToFleetId, ShipIds, Source });
            return;
        }
        // A newer order for these ships replaces an older wait.
        foreach (var p in from.Transfers) p.ShipIds.RemoveAll(ids.Contains);
        from.Transfers.RemoveAll(p => p.ShipIds.Count == 0);
        var est = Transfers.Estimate(world, from, ids, to);
        if (!est.Together && WaitUntilClose && (est.Problem != null || est.Hours > Transfers.WaitWithinHours))
        {
            from.Transfers.Add(new PendingTransfer { ShipIds = ids, ToFleetId = to.Id, SinceHour = world.Hour });
            sim.Log.Info("fleet", "Ships wait to join another fleet when closer", new { FleetId, ToFleetId, ships = ids, hours = Math.Round(est.Hours, 1), Source });
            return;
        }
        if (Transfers.Send(world, sim, from, to, ids, Source) is string problem)
            Refuse(world, sim, from, $"send {Transfers.Names(world, ids)} to {to.Name}: {problem}", new { FleetId, ToFleetId, problem, Source });
    }
}

/// <summary>Call off ships waiting to join another fleet (they stay where they are).</summary>
public sealed record CancelTransferCommand(int FleetId, int ToFleetId) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Cancel transfer") is not Fleet f) return;
        var n = f.Transfers.RemoveAll(p => p.ToFleetId == ToFleetId);
        if (n > 0) sim.Log.Info("fleet", "Waiting transfer called off", new { FleetId, ToFleetId, Source });
    }
}

/// <summary>Lock or unlock a fleet (a home order: at once, even while paused). Locked, it keeps the ships it has now.</summary>
public sealed record SetFleetLockCommand(Side Side, int FleetId, bool Locked) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.FindFleet(FleetId) is not Fleet f || f.Side != Side)
        {
            sim.Log.Warn("fleet", "Lock for a fleet that is not yours", new { side = Side.ToString(), FleetId, Source });
            return;
        }
        Transfers.SetLock(f, Locked);
        sim.Log.Info("fleet", Locked ? "Fleet locked" : "Fleet unlocked", new { FleetId, fleet = f.Name, ships = f.Roster.Count, Source });
    }
}

/// <summary>Supply role: the fleets this supply fleet serves first, in order (then any other in range, nearest first).</summary>
public sealed record SetSupplyFirstCommand(int FleetId, List<int> FleetIds) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Supply priorities") is not Fleet f) return;
        f.SupplyFirst = FleetIds.Distinct().Where(id => id != f.Id && world.FindFleet(id) is Fleet o && o.Side == f.Side).ToList();
        sim.Log.Info("supply", "Supply priorities set", new { FleetId, fleet = f.Name, first = f.SupplyFirst, Source });
    }
}
