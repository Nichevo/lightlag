using System.Text.Json.Serialization;

namespace Lightlag.Sim.Core;

/// <summary>
/// Everything a player (or AI) does to the game goes through a command. Commands are recorded with
/// the tick they took effect, which is what a replay is: the seed plus this list.
/// Never change the world from the UI directly; add a command instead.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlanFlightCommand), "planFlight")]
[JsonDerivedType(typeof(StopFleetCommand), "stopFleet")]
[JsonDerivedType(typeof(SetHoldCommand), "setHold")]
[JsonDerivedType(typeof(SplitFleetCommand), "splitFleet")]
[JsonDerivedType(typeof(MergeFleetsCommand), "mergeFleets")]
[JsonDerivedType(typeof(SetStanceCommand), "setStance")]
[JsonDerivedType(typeof(SetDodgeCommand), "setDodge")]
[JsonDerivedType(typeof(SetMissionCommand), "setMission")]
[JsonDerivedType(typeof(SetAutonomyCommand), "setAutonomy")]
[JsonDerivedType(typeof(SetProtectCommand), "setProtect")]
[JsonDerivedType(typeof(SetCoverCommand), "setCover")]
[JsonDerivedType(typeof(SetRadioSilenceCommand), "setRadioSilence")]
[JsonDerivedType(typeof(SendDroneCommand), "sendDrone")]
[JsonDerivedType(typeof(SetFireModeCommand), "setFireMode")]
[JsonDerivedType(typeof(SendAheadCommand), "sendAhead")]
[JsonDerivedType(typeof(GatherFleetsCommand), "gatherFleets")]
[JsonDerivedType(typeof(QueueOrderCommand), "queueOrder")]
[JsonDerivedType(typeof(CancelOrderCommand), "cancelOrder")]
[JsonDerivedType(typeof(BuildStationCommand), "buildStation")]
[JsonDerivedType(typeof(SetOrderFleetCommand), "setOrderFleet")]
[JsonDerivedType(typeof(FoundTeamCommand), "foundTeam")]
[JsonDerivedType(typeof(TrainTeamCommand), "trainTeam")]
[JsonDerivedType(typeof(SplitTeamCommand), "splitTeam")]
[JsonDerivedType(typeof(SetTeamKeptCommand), "setTeamKept")]
[JsonDerivedType(typeof(SetOrderTeamCommand), "setOrderTeam")]
[JsonDerivedType(typeof(SetFitCommand), "setFit")]
[JsonDerivedType(typeof(SetDesignCommand), "setDesign")]
[JsonDerivedType(typeof(SetSupplyRunCommand), "setSupplyRun")]
[JsonDerivedType(typeof(SetHeatCommand), "setHeat")]
[JsonDerivedType(typeof(EjectHeatSinksCommand), "ejectHeatSinks")]
[JsonDerivedType(typeof(SetFireControlCommand), "setFireControl")]
[JsonDerivedType(typeof(DropMissilesCommand), "dropMissiles")]
[JsonDerivedType(typeof(BuildOnPlanetCommand), "buildOnPlanet")]
[JsonDerivedType(typeof(DemolishCommand), "demolish")]
[JsonDerivedType(typeof(SendPeopleCommand), "sendPeople")]
[JsonDerivedType(typeof(SetAutoTopUpCommand), "setAutoTopUp")]
[JsonDerivedType(typeof(SettleCommand), "settle")]
[JsonDerivedType(typeof(SetOrbitHeightCommand), "setOrbitHeight")]
[JsonDerivedType(typeof(SetBombingCommand), "setBombing")]
[JsonDerivedType(typeof(LandTroopsCommand), "landTroops")]
[JsonDerivedType(typeof(BoardSlipwayCommand), "boardSlipway")]
[JsonDerivedType(typeof(SetPriorityCommand), "setPriority")]
[JsonDerivedType(typeof(SetSiegeStockCommand), "setSiegeStock")]
[JsonDerivedType(typeof(SetAutoTransportCommand), "setAutoTransport")]
[JsonDerivedType(typeof(FixedRunCommand), "fixedRun")]
[JsonDerivedType(typeof(ShipGoodsCommand), "shipGoods")]
[JsonDerivedType(typeof(SendForRefitCommand), "sendForRefit")]
[JsonDerivedType(typeof(CancelRefitCommand), "cancelRefit")]
[JsonDerivedType(typeof(SetPullRuleCommand), "setPullRule")]
[JsonDerivedType(typeof(HoldOnCommand), "holdOn")]
[JsonDerivedType(typeof(PullBackNowCommand), "pullBackNow")]
[JsonDerivedType(typeof(SetPullDefaultCommand), "setPullDefault")]
[JsonDerivedType(typeof(SetAutoLevelCommand), "setAutoLevel")]
[JsonDerivedType(typeof(AcceptNeedCommand), "acceptNeed")]
[JsonDerivedType(typeof(DismissNeedCommand), "dismissNeed")]
[JsonDerivedType(typeof(CreateZoneCommand), "createZone")]
[JsonDerivedType(typeof(EditZoneCommand), "editZone")]
[JsonDerivedType(typeof(DeleteZoneCommand), "deleteZone")]
[JsonDerivedType(typeof(SetZoneFleetCommand), "setZoneFleet")]
[JsonDerivedType(typeof(HuntCommand), "hunt")]
[JsonDerivedType(typeof(SetStockRuleCommand), "setStockRule")]
[JsonDerivedType(typeof(SetSiegeModeCommand), "setSiegeMode")]
[JsonDerivedType(typeof(SetRouteCommand), "setRoute")]
[JsonDerivedType(typeof(SetFixedRunEveryCommand), "setFixedRunEvery")]
[JsonDerivedType(typeof(TransferCrewCommand), "transferCrew")]
[JsonDerivedType(typeof(BoardShipCommand), "boardShip")]
[JsonDerivedType(typeof(BoardingCallCommand), "boardingCall")]
[JsonDerivedType(typeof(SetGroundPlanCommand), "setGroundPlan")]
[JsonDerivedType(typeof(GroundCallCommand), "groundCall")]
[JsonDerivedType(typeof(PullOutTroopsCommand), "pullOutTroops")]
[JsonDerivedType(typeof(SetAfterStepCommand), "setAfterStep")]
[JsonDerivedType(typeof(SetHomeChoiceCommand), "setHomeChoice")]
[JsonDerivedType(typeof(DebugCrashCommand), "debugCrash")]
[JsonDerivedType(typeof(DebugBreakRuleCommand), "debugBreakRule")]
public abstract record GameCommand
{
    /// <summary>Who issued it, e.g. "player", "ai:rival", "headless".</summary>
    public string Source { get; init; } = "player";

    public abstract void Apply(World world, Simulation sim);

    /// <summary>
    /// The side giving the order and where it takes effect, for light lag (null: it takes effect at once,
    /// like debug commands). Fleet orders go to the fleet; see Light.
    /// </summary>
    public virtual (Side Side, Vec2 At)? Destination(World world) =>
        this is IFleetCommand fc && world.FindFleet(fc.FleetId) is Fleet f ? (f.Side, f.Position) : null;

    protected Fleet? GetFleet(World world, Simulation sim, int fleetId, string what)
    {
        var fleet = world.FindFleet(fleetId);
        if (fleet == null) sim.Log.Warn("fleet", $"{what} for a fleet that does not exist", new { fleetId, Source });
        return fleet;
    }
}

/// <summary>An order to one fleet: under light lag it travels to that fleet.</summary>
public interface IFleetCommand
{
    int FleetId { get; }
}

/// <summary>Fly a fleet to a body (it ends in orbit there, or drifting) or to a point in space.</summary>
public sealed record PlanFlightCommand(int FleetId, FlightRequest Request) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Flight order") is not Fleet fleet) return;
        var plan = FlightPlanner.Plan(world, fleet, Request);
        if (!plan.Ok || plan.Flight == null)
        {
            sim.Log.Warn("fleet", $"{fleet.Name} cannot fly that: {plan.Problem}", new { fleetId = fleet.Id, Request, Source });
            return;
        }
        fleet.Flight = plan.Flight;
        fleet.JoinFleetId = null;
        fleet.Chase = null;
        if (plan.Flight.Chase != ChaseMode.None && Request.TargetFleetId is int chased)
        {
            fleet.Chase = Chases.Order(world, fleet, chased, plan.Flight.Chase, Request.SpeedFraction);
            fleet.Chase.Detouring = plan.Flight.Detour;
            sim.Log.Info("fleet", $"{fleet.Name} chases {world.FindFleet(chased)?.Name}", new
            {
                fleetId = fleet.Id, targetFleetId = chased, mode = plan.Flight.Chase.ToString(), plan.Flight.ArriveHour, plan.DeltaV, Source,
            });
            return;
        }
        var target = Request.TargetBodyId is int b ? world.FindBody(b)?.Name : plan.Flight.Target.ToString();
        sim.Log.Info("fleet", $"{fleet.Name} plans a flight to {target}", new
        {
            fleetId = fleet.Id, Request, plan.Flight.DepartHour, plan.Flight.ArriveHour, plan.DeltaV, Source,
        });
    }
}

/// <summary>Cancel the flight. A moving fleet brakes to a stop if it has the fuel, otherwise it drifts on.</summary>
public sealed record StopFleetCommand(int FleetId) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Stop order") is not Fleet fleet) return;
        fleet.Flight = null;
        fleet.Chase = null;
        fleet.JoinFleetId = null;
        if (fleet.Velocity.Length < 1e-9) return;
        if (FlightPlanner.BrakeToStop(world, fleet) is Flight brake)
        {
            fleet.Flight = brake;
            var full = brake.DeltaV >= fleet.Velocity.Length - 1e-6;
            if (full) sim.Log.Info("fleet", $"{fleet.Name} brakes to a stop", new { fleetId = fleet.Id, Source });
            else sim.Log.Warn("fleet", $"{fleet.Name} brakes with the fuel it has left and drifts on slower",
                new { fleetId = fleet.Id, speed = fleet.Velocity.Length, braked = brake.DeltaV, Source });
        }
        else
        {
            sim.Log.Warn("fleet", $"{fleet.Name} has no fuel to brake and drifts on", new { fleetId = fleet.Id, Source });
        }
    }
}

/// <summary>Orbit the nearest body, or drift with engines cold. The fleet must be stopped.</summary>
public sealed record SetHoldCommand(int FleetId, HoldStyle Hold) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Hold order") is not Fleet fleet) return;
        if (fleet.InFlight && fleet.Flight!.Started)
        {
            sim.Log.Warn("fleet", $"{fleet.Name} is flying; stop it first", new { fleetId = fleet.Id, Hold, Source });
            return;
        }
        if (fleet.InFlight) fleet.Flight!.HoldAfter = Hold;
        if (Hold == HoldStyle.Drift)
        {
            Fleets.EnterDrift(fleet);
            fleet.Velocity = Vec2.Zero;
            return;
        }
        if (fleet.Velocity.Length > 1e-9)
        {
            sim.Log.Warn("fleet", $"{fleet.Name} is still moving; stop it first", new { fleetId = fleet.Id, Source });
            return;
        }
        if (Fleets.OrbitableNear(world, fleet.Position) is Body body) Fleets.EnterOrbit(fleet, body);
        else sim.Log.Warn("fleet", $"Nothing near {fleet.Name} to orbit", new { fleetId = fleet.Id, Source });
    }
}

/// <summary>Take some ships out of a fleet into a new fleet that carries on doing the same thing.</summary>
public sealed record SplitFleetCommand(int FleetId, List<int> ShipIds) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Split order") is not Fleet fleet) return;
        var leaving = ShipIds.Distinct().Where(fleet.ShipIds.Contains).Select(id => world.FindShip(id)!).ToList();
        if (leaving.Count == 0 || leaving.Count == fleet.ShipIds.Count)
        {
            sim.Log.Warn("fleet", $"Split of {fleet.Name} needs some but not all of its ships", new { fleetId = fleet.Id, ShipIds, Source });
            return;
        }
        fleet.ShipIds.RemoveAll(id => leaving.Any(s => s.Id == id));
        var created = Fleets.Create(world, fleet.Side, leaving, fleet.Position);
        created.Velocity = fleet.Velocity;
        created.Hold = fleet.Hold;
        created.OrbitBodyId = fleet.OrbitBodyId;
        created.OrbitRadius = fleet.OrbitRadius;
        created.OrbitAngle = fleet.OrbitAngle;
        created.Flight = fleet.Flight?.Copy();
        created.Chase = fleet.Chase?.Copy();
        created.Stance = fleet.Stance;
        created.Dodge = fleet.Dodge;
        created.Fire = fleet.Fire;
        created.Facing = fleet.Facing;
        created.RetreatWhenDamaged = fleet.RetreatWhenDamaged;
        created.Rule = fleet.Rule.Copy();
        created.RuleByHand = fleet.RuleByHand;
        created.Autonomy = fleet.Autonomy;
        created.Cover = fleet.Cover;
        created.FireAt = fleet.FireAt;
        Fleets.Arrange(world, fleet);
        Fleets.SetApart(world, created);
        sim.Log.Info("fleet", $"{created.Name} splits from {fleet.Name}", new { fleetId = fleet.Id, newFleetId = created.Id, ShipIds, Source });
    }
}

/// <summary>Join another fleet into this one. Both must be stopped and close together.</summary>
public sealed record MergeFleetsCommand(int FleetId, int OtherFleetId) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Merge order") is not Fleet fleet) return;
        if (GetFleet(world, sim, OtherFleetId, "Merge order") is not Fleet other) return;
        if (Fleets.CanMerge(fleet, other) is string problem)
        {
            sim.Log.Warn("fleet", $"Cannot merge {other.Name} into {fleet.Name}: {problem}", new { FleetId, OtherFleetId, Source });
            return;
        }
        Fleets.Absorb(world, fleet, other);
        sim.Log.Info("fleet", $"{other.Name} merges into {fleet.Name}", new { FleetId, OtherFleetId, Source });
    }
}

/// <summary>
/// "Fast ships go ahead": the ships faster than the fleet's slowest split off and fly the same order at
/// their own pace, then wait at the destination and join the slower ships when they arrive.
/// </summary>
public sealed record SendAheadCommand(int FleetId, FlightRequest Request) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Send-ahead order") is not Fleet fleet) return;
        if (Request.Arrival != ArrivalStyle.Brake)
        {
            sim.Log.Warn("fleet", "Fast ships can only go ahead on a flight that brakes at the end", new { FleetId, Source });
            return;
        }
        var ships = world.ShipsOf(fleet).ToList();
        var slowest = ships.Count == 0 ? 0 : ships.Min(Fleets.ShipSpeed);
        var fast = ships.Where(s => Fleets.ShipSpeed(s) > slowest * 1.0001).ToList();
        if (fast.Count == 0 || fast.Count == ships.Count)
        {
            new PlanFlightCommand(FleetId, Request) { Source = Source }.Apply(world, sim);
            return;
        }

        fleet.ShipIds.RemoveAll(id => fast.Any(s => s.Id == id));
        var ahead = Fleets.Create(world, fleet.Side, fast, fleet.Position);
        ahead.Velocity = fleet.Velocity;
        ahead.Hold = fleet.Hold;
        ahead.OrbitBodyId = fleet.OrbitBodyId;
        ahead.OrbitRadius = fleet.OrbitRadius;
        ahead.OrbitAngle = fleet.OrbitAngle;
        ahead.Stance = fleet.Stance;
        ahead.Autonomy = fleet.Autonomy;
        ahead.Dodge = fleet.Dodge;
        ahead.Fire = fleet.Fire;
        ahead.Facing = fleet.Facing;
        ahead.RetreatWhenDamaged = fleet.RetreatWhenDamaged;
        ahead.Rule = fleet.Rule.Copy();
        ahead.RuleByHand = fleet.RuleByHand;
        Fleets.Arrange(world, fleet);

        var slowPlan = FlightPlanner.Plan(world, fleet, Request);
        var fastPlan = FlightPlanner.Plan(world, ahead, Request);
        if (!slowPlan.Ok || slowPlan.Flight == null || !fastPlan.Ok || fastPlan.Flight == null)
        {
            // Put things back as they were.
            Fleets.Absorb(world, fleet, ahead);
            world.FleetNumbers[(int)fleet.Side]--;
            sim.Log.Warn("fleet", $"{fleet.Name} cannot send its fast ships ahead: {slowPlan.Problem ?? fastPlan.Problem}", new { FleetId, Request, Source });
            return;
        }
        fleet.Flight = slowPlan.Flight;
        fleet.JoinFleetId = null;
        fleet.Chase = null;
        ahead.Flight = fastPlan.Flight;
        ahead.JoinFleetId = fleet.Id;
        sim.Log.Info("fleet", $"{ahead.Name} goes ahead of {fleet.Name}", new
        {
            FleetId, aheadFleetId = ahead.Id, Request, slowArrive = slowPlan.Flight.ArriveHour, fastArrive = fastPlan.Flight.ArriveHour, Source,
        });
    }
}

/// <summary>
/// Bring fleets together into one: those already close join now; the others fly to it (to its
/// destination if it is flying) and join when they get there.
/// </summary>
public sealed record GatherFleetsCommand(int FleetId, List<int> OtherFleetIds, double SpeedFraction) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Gather order") is not Fleet into) return;
        foreach (var otherId in OtherFleetIds.Distinct())
        {
            if (world.FindFleet(otherId) is not Fleet other || other.Id == into.Id || other.Side != into.Side) continue;
            if (Fleets.CanJoin(other, into))
            {
                Fleets.Absorb(world, into, other);
                sim.Log.Info("fleet", $"{other.Name} merges into {into.Name}", new { FleetId, otherFleetId = otherId, Source });
                continue;
            }
            if (Fleets.FlyToJoin(world, other, into, SpeedFraction) is string problem)
            {
                sim.Log.Warn("fleet", $"{other.Name} cannot reach {into.Name}", new { FleetId, otherFleetId = otherId, problem, Source });
                if (problem == Fleets.DriftingProblem) return;
                continue;
            }
            other.JoinFleetId = into.Id;
            sim.Log.Info("fleet", $"{other.Name} flies to join {into.Name}", new { FleetId, otherFleetId = otherId, other.Flight!.ArriveHour, Source });
        }
    }
}

/// <summary>Set a fleet's standing orders: engage or hold, and whether to retreat when badly damaged.</summary>
public sealed record SetStanceCommand(int FleetId, Stance Stance, bool RetreatWhenDamaged) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Standing orders") is not Fleet fleet) return;
        fleet.Stance = Stance;
        fleet.RetreatWhenDamaged = RetreatWhenDamaged;
        sim.Log.Info("fleet", $"{fleet.Name} standing orders: {Stance}" + (RetreatWhenDamaged ? ", retreat when damaged" : ""), new { FleetId, Stance, RetreatWhenDamaged, Source });
    }
}

/// <summary>How a fleet dodges railgun fire.</summary>
public sealed record SetDodgeCommand(int FleetId, DodgeMode Dodge) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Dodge order") is not Fleet fleet) return;
        fleet.Dodge = Dodge;
        sim.Log.Info("fleet", $"{fleet.Name} dodging: {Dodge}", new { FleetId, Dodge = Dodge.ToString(), Source });
    }
}

/// <summary>How a fleet fires its railguns: focused or spread.</summary>
public sealed record SetFireModeCommand(int FleetId, FireMode Fire) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Fire mode order") is not Fleet fleet) return;
        fleet.Fire = Fire;
        sim.Log.Info("fleet", $"{fleet.Name} railguns: {Fire}", new { FleetId, Fire = Fire.ToString(), Source });
    }
}

/// <summary>Throws inside the simulation on purpose, to test that crash reports and replays work.</summary>
public sealed record DebugCrashCommand(string Reason) : GameCommand
{
    public override void Apply(World world, Simulation sim) =>
        throw new InvalidOperationException($"Test crash on purpose: {Reason}");
}

/// <summary>Breaks the game state on purpose, to test that rule checks catch it.</summary>
public sealed record DebugBreakRuleCommand : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (world.Fleets.Count == 0) return;
        var f = world.Fleets[0];
        f.Flight = null;
        Fleets.EnterDrift(f);
        f.Velocity = Vec2.Zero;
        f.Position = new Vec2(double.NaN, 0);
    }
}

/// <summary>Give a fleet a mission (or None to end it). The body or fleet it is about, where the mission needs one.</summary>
public sealed record SetMissionCommand(int FleetId, Mission Mission, int? BodyId = null, int? EscortFleetId = null) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Mission") is not Fleet fleet) return;
        fleet.Mission = Mission;
        fleet.MissionBodyId = BodyId;
        fleet.MissionFleetId = EscortFleetId;
        fleet.MissionLeg = 0;
        fleet.MissionUntil = 0;
        sim.Log.Info("fleet", $"{fleet.Name} mission: {Mission}", new { FleetId, Mission, BodyId, EscortFleetId, Source });
        Autonomy.Start(sim, fleet);
    }
}

/// <summary>Set how much a fleet decides for itself when cut off from command.</summary>
public sealed record SetAutonomyCommand(int FleetId, AutonomyLevel Level) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Autonomy") is not Fleet fleet) return;
        fleet.Autonomy = Level;
        sim.Log.Info("fleet", $"{fleet.Name} autonomy: {Level}", new { FleetId, Level, Source });
    }
}
