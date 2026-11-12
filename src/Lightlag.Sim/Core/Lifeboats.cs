using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>
/// Survivors of a lost ship: escape pods from small hulls (half the crew, a few days of air, drifting
/// apart from the wreck), lifeboats from big ones (most of the crew, weeks of air, crawling towards the
/// nearest planet of their side). See Lifeboats.
/// </summary>
public sealed class Lifeboat
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public string ShipName { get; set; } = "";
    public string ClassId { get; set; } = "";

    /// <summary>A lifeboat (true) or escape pods (false).</summary>
    public bool Boat { get; set; }
    public double CrewK { get; set; }
    public double CrewXp { get; set; }
    public Vec2 Position { get; set; }
    public Vec2 Velocity { get; set; }
    public double LaunchHour { get; set; }
    public double AirUntilHour { get; set; }

    /// <summary>Pods lost in orbit stay with the body, drifting out from it (Offset); null out in space.</summary>
    public int? BodyId { get; set; }
    public Vec2 Offset { get; set; }

    /// <summary>A lifeboat's goal: the planet of its side it crawls towards (null: drifting).</summary>
    public int? HeadingBodyId { get; set; }

    /// <summary>The fleet sent to pick it up (see Lifeboats.Send), if any.</summary>
    public int? RescuerFleetId { get; set; }
}

/// <summary>A fleet out picking up survivors: the boat it is after and the planet it goes back to afterwards.</summary>
public sealed class RescueRun
{
    public int BoatId { get; set; }
    public int? ReturnBodyId { get; set; }
    public double StartHour { get; set; }

    /// <summary>Where the fleet was sent to meet it (a flight still going there is the rescue's; any other is a new order).</summary>
    public Vec2 Point { get; set; }
}

/// <summary>
/// Escape pods and lifeboats. A crewed ship that is destroyed puts its survivors out: escape pods
/// drift away from the wreck, lifeboats (cruisers and bigger) head for home at a crawl. Any fleet that
/// comes within reach out of battle picks them up: your own crew go home and man the next new ship with
/// their rank; enemy crews become prisoners (intel). When the air runs out they are lost. The After
/// battle area sends the nearest free fleet to pick up your own (Advise asks first); the rival does
/// the same. AI-run ships have no one aboard to save.
/// </summary>
public static class Lifeboats
{
    private static AfterData D => War.D.After;

    /// <summary>A crewed ship is lost: its survivors get off in pods or a lifeboat (no air, no luck: nothing for AI-run ships).</summary>
    public static Lifeboat? Launch(World w, Ship s, Fleet? f, Body? orbit)
    {
        if (!w.Loop) return null;
        var cls = GameData.ShipClass(s.ClassId);
        var boat = cls.Lifeboats;
        var crew = War.CrewK(s) * Math.Clamp(s.Crew, 0, 1) * (boat ? D.BoatShare : D.PodShare);
        if (crew <= 1e-6) return null;
        // They push off in a direction fixed by the ship (no dice): pods scatter, a lifeboat turns for home.
        var angle = s.Id * 2.399963;
        var push = new Vec2(DetMath.Cos(angle), DetMath.Sin(angle)) * D.PodScatterMmPerHour;
        var home = boat ? NearestHome(w, s.Side, s.Position) : null;
        var lb = new Lifeboat
        {
            Id = w.NextId++, Side = s.Side, ShipName = s.Name, ClassId = s.ClassId, Boat = boat, CrewK = crew, CrewXp = s.Xp,
            Position = s.Position, LaunchHour = w.Hour, AirUntilHour = w.Hour + (boat ? D.BoatDays : D.PodDays) * 24,
            Velocity = orbit == null ? (f?.Velocity ?? Vec2.Zero) + push : push,
            BodyId = boat ? null : orbit?.Id, Offset = orbit == null ? Vec2.Zero : s.Position - orbit.Position, HeadingBodyId = home?.Id,
        };
        if (lb.BodyId == null && orbit != null) lb.Velocity = push; // a lifeboat leaves orbit under its own power
        w.Lifeboats.Add(lb);
        return lb;
    }

    /// <summary>The nearest planet this side holds (lifeboats make for it).</summary>
    public static Body? NearestHome(World w, Side side, Vec2 from) =>
        w.Bodies.Where(b => b.Owner == side).OrderBy(b => Vec2.Distance(b.Position, from)).ThenBy(b => b.Id).FirstOrDefault();

    public static double AirLeftHours(World w, Lifeboat lb) => Math.Max(0, lb.AirUntilHour - w.Hour);

    public static string Kind(Lifeboat lb) => lb.Boat ? "lifeboat" : "escape pods";

    /// <summary>Hourly: boats drift or crawl home, get picked up, reach home or run out of air; fleets out on a rescue go back.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop) return;
        var hours = GameTime.HoursPerTick;
        foreach (var lb in w.Lifeboats.ToList())
        {
            Move(w, lb, hours);
            if (lb.Boat && lb.HeadingBodyId is int h && w.FindBody(h) is Body home && home.Owner == lb.Side && Vec2.Distance(home.Position, lb.Position) <= D.HomeMm)
            {
                CrewHome(w, lb);
                sim.Log.Info("crews", $"The lifeboat of {lb.ShipName} reaches {home.Name}", new { boat = lb.Id, side = lb.Side.ToString(), crewK = Math.Round(lb.CrewK, 3) });
                w.Lifeboats.Remove(lb);
                continue;
            }
            if (PickerUp(w, lb) is Fleet f)
            {
                PickUp(sim, f, lb);
                continue;
            }
            if (w.Hour >= lb.AirUntilHour)
            {
                Economy.Of(w, lb.Side).CrewsLostK += lb.CrewK;
                sim.Log.Info("crews", $"The {Kind(lb)} of {lb.ShipName} run out of air", new { boat = lb.Id, side = lb.Side.ToString(), crewK = Math.Round(lb.CrewK, 3) });
                w.Lifeboats.Remove(lb);
            }
        }
        foreach (var f in w.Fleets.Where(f => f.Rescue != null).ToList())
        {
            if (f.ShipIds.Count == 0) { f.Rescue = null; continue; }
            if (w.Lifeboats.Any(b => b.Id == f.Rescue!.BoatId)) continue;
            Done(w, f);
        }
        if (w.Tick % D.SendEveryTicks != 0) return;
        foreach (var side in new[] { Side.Player, Side.Rival })
            if (Mind.Of(w, side) != null)
                foreach (var (lb, f) in Wanted(w, side)) Send(sim, f, lb, "ai");
    }

    private static void Move(World w, Lifeboat lb, double hours)
    {
        if (lb.BodyId is int b && w.FindBody(b) is Body body)
        {
            lb.Offset += lb.Velocity * hours * D.OrbitDrift;
            lb.Position = body.Position + lb.Offset;
            return;
        }
        if (lb.Boat && lb.HeadingBodyId is int h && w.FindBody(h) is Body home)
        {
            // A lifeboat burns gently for home: it sheds the battle's speed and heads in at a crawl.
            var want = (home.Position - lb.Position).Normalized() * D.BoatSpeedMmPerHour;
            var change = want - lb.Velocity;
            var step = D.BoatSpeedMmPerHour * hours;
            lb.Velocity = change.Length <= step ? want : lb.Velocity + change.Normalized() * step;
        }
        lb.Position += lb.Velocity * hours;
    }

    /// <summary>The fleet that picks a boat up now: any fleet out of battle within reach, your own first, then the nearest.</summary>
    private static Fleet? PickerUp(World w, Lifeboat lb) =>
        w.Fleets.Where(f => f.ShipIds.Count > 0 && !Heat.InBattle(w, f) && !f.Retreating && !Boarders.Grappled(w, f)
                            && Vec2.Distance(f.Position, lb.Position) <= D.PickupMm)
            .OrderBy(f => f.Side == lb.Side ? 0 : 1).ThenBy(f => Vec2.Distance(f.Position, lb.Position)).ThenBy(f => f.Id).FirstOrDefault();

    private static void CrewHome(World w, Lifeboat lb)
    {
        Economy.Of(w, lb.Side).ReserveCrews.Add(lb.CrewXp);
        Economy.Of(w, lb.Side).Totals.CrewsRescued += lb.CrewK;
    }

    /// <summary>A fleet takes the survivors aboard: your own go home to man new ships, the enemy's become prisoners.</summary>
    public static void PickUp(Simulation sim, Fleet f, Lifeboat lb)
    {
        var w = sim.World;
        if (lb.Side == f.Side)
        {
            CrewHome(w, lb);
            sim.Log.Info("crews", $"{f.Name} picks up the {Kind(lb)} of {lb.ShipName}", new { fleetId = f.Id, boat = lb.Id, crewK = Math.Round(lb.CrewK, 3), xp = Math.Round(lb.CrewXp, 2) });
        }
        else
        {
            sim.Log.Info("crews", $"{f.Name} takes the {Kind(lb)} of {lb.ShipName} prisoner", new { fleetId = f.Id, boat = lb.Id, crewK = Math.Round(lb.CrewK, 3) });
            Spoils.TakePrisoners(sim, f.Side, lb.CrewK, lb.ShipName);
        }
        w.Lifeboats.Remove(lb);
        if (f.Rescue?.BoatId == lb.Id) Done(w, f);
    }

    /// <summary>A rescue run is over (picked up, or the boat is gone): back to where it came from.</summary>
    private static void Done(World w, Fleet f)
    {
        var run = f.Rescue;
        var back = run?.ReturnBodyId is int r ? w.FindBody(r) : null;
        f.Rescue = null;
        // Given another order meanwhile (a flight elsewhere): that order stands.
        if (back == null || run == null || f.Flight != null && (f.Flight.TargetBodyId != null || Vec2.Distance(f.Flight.Target, run.Point) > 1)) return;
        var plan = FlightPlanner.Plan(w, f, new FlightRequest(back.Id, back.Position, D.RescueSpeed, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick));
        if (plan is { Ok: true, Flight: Flight flight }) { f.Flight = flight; f.Chase = null; }
    }

    /// <summary>Where a boat will be in this many hours (it drifts or crawls on).</summary>
    public static Vec2 Ahead(World w, Lifeboat lb, double hours) =>
        lb.BodyId is int b && w.FindBody(b) is Body body ? body.Position + lb.Offset + lb.Velocity * hours * D.OrbitDrift : lb.Position + lb.Velocity * hours;

    /// <summary>Hours a fleet would take to reach a boat (a rough guess: most of its top speed, plus speeding up and slowing down).</summary>
    public static double HoursTo(World w, Fleet f, Lifeboat lb)
    {
        var ships = w.ShipsOf(f).ToList();
        if (ships.Count == 0) return double.PositiveInfinity;
        var speed = ships.Min(s => s.MaxSpeed) * D.RescueSpeed;
        var accel = Math.Max(1e-6, ships.Min(s => s.Accel));
        var d = Vec2.Distance(f.Position, lb.Position);
        // Speed up to cruise, coast, slow down; a short hop never reaches cruise.
        return d < speed * speed / accel ? 2 * Math.Sqrt(d / accel) : d / Math.Max(1e-6, speed) + speed / accel;
    }

    /// <summary>Why this fleet can't go and pick up this boat now, or null.</summary>
    public static string? CanSend(World w, Fleet f, Lifeboat lb)
    {
        if (f.ShipIds.Count == 0) return "No ships";
        if (Heat.InBattle(w, f)) return "In battle";
        if (f.Retreating || f.Refit != null) return "Busy (pulling back or going for refit)";
        if (Boarders.Grappled(w, f) || AfterBattle.Busy(w, f)) return "Busy clearing up after a battle";
        if (w.ShipsOf(f).Any(s => s.Crippled)) return "Has a crippled ship";
        var hours = HoursTo(w, f, lb);
        if (hours > AirLeftHours(w, lb)) return $"Can't get there in time (about {hours / 24:0.#} days; air for {AirLeftHours(w, lb) / 24:0.#})";
        return null;
    }

    /// <summary>The boats of this side nobody is going for, each with the nearest free fleet that can reach it in time (and no known enemy near it).</summary>
    public static List<(Lifeboat Boat, Fleet Fleet)> Wanted(World w, Side side)
    {
        var picks = new List<(Lifeboat, Fleet)>();
        var taken = new HashSet<int>();
        foreach (var lb in w.Lifeboats.Where(b => b.Side == side).OrderBy(b => b.AirUntilHour).ThenBy(b => b.Id))
        {
            if (lb.RescuerFleetId is int r && w.FindFleet(r) is Fleet going && going.Rescue?.BoatId == lb.Id) continue;
            // A fleet of ours already beside it picks it up within the hour; one clearing up after a battle (or still fighting) nearby has it in hand.
            if (w.Fleets.Any(f => f.Side == side && f.ShipIds.Count > 0 && (Vec2.Distance(f.Position, lb.Position) <= D.PickupMm * 2
                    || Vec2.Distance(f.Position, lb.Position) <= D.ReachMm && (AfterBattle.Busy(w, f) || Heat.InBattle(w, f))))) continue;
            if (Ground.ReliefNear(w, side, lb.Position, 24)) continue;
            var f = w.Fleets.Where(f => f.Side == side && !taken.Contains(f.Id) && f.Rescue == null && f.Flight == null && f.PeopleRun == null
                                         && f.Mission is Mission.None or Mission.Hold && f.ZoneId == null && CanSend(w, f, lb) == null)
                .OrderBy(f => HoursTo(w, f, lb)).ThenBy(f => f.Id).FirstOrDefault();
            if (f == null) continue;
            taken.Add(f.Id);
            picks.Add((lb, f));
        }
        return picks;
    }

    /// <summary>Sends a fleet to pick up a boat; it goes back to its planet afterwards.</summary>
    public static bool Send(Simulation sim, Fleet f, Lifeboat lb, string source)
    {
        var w = sim.World;
        var hours = HoursTo(w, f, lb);
        var at = Ahead(w, lb, hours);
        var plan = FlightPlanner.Plan(w, f, new FlightRequest(null, at, D.RescueSpeed, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick));
        if (plan is not { Ok: true, Flight: Flight flight })
        {
            sim.Log.Warn("crews", $"{f.Name} can't fly out to the {Kind(lb)}", new { fleetId = f.Id, boat = lb.Id, problem = plan.Problem, source });
            return false;
        }
        f.Rescue = new RescueRun { BoatId = lb.Id, ReturnBodyId = f.OrbitBodyId, StartHour = w.Hour, Point = at };
        f.Flight = flight;
        f.Chase = null;
        lb.RescuerFleetId = f.Id;
        sim.Log.Info("crews", $"{f.Name} goes to pick up the {Kind(lb)} of {lb.ShipName}", new { fleetId = f.Id, boat = lb.Id, hours = Math.Round(hours, 1), source });
        return true;
    }

    /// <summary>The player's After battle area (from Stewards): send a fleet to each boat adrift (Auto), or offer it (Advise).</summary>
    public static void Steward(Simulation sim, Side side, OfferFn offer)
    {
        var w = sim.World;
        foreach (var (lb, f) in Wanted(w, side))
            offer(AutoArea.AfterBattle, $"rescue:{lb.Id}",
                $"The {Kind(lb)} of {lb.ShipName} are adrift ({lb.CrewK * 1000:0} crew, air for {AirLeftHours(w, lb) / 24:0.#} days): send {f.Name} to pick them up?",
                new RescueCommand(side, f.Id, lb.Id), "Send it", fleetId: f.Id, advice: NeedLevel.Soon);
    }
}

/// <summary>Send a fleet to pick up survivors (escape pods or a lifeboat).</summary>
public sealed record RescueCommand(Side Side, int FleetId, int BoatId) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Rescue order") is not Fleet f || f.Side != Side) return;
        if (world.Lifeboats.Find(b => b.Id == BoatId) is not Lifeboat lb || Lifeboats.CanSend(world, f, lb) is string problem)
        {
            sim.Log.Warn("crews", "Can't send that fleet to the survivors", new { side = Side.ToString(), FleetId, BoatId, Source });
            return;
        }
        Lifeboats.Send(sim, f, lb, Source);
    }
}
