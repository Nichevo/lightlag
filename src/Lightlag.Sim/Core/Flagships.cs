namespace Lightlag.Sim.Core;

/// <summary>How a fleet guards its protected ships: Loose (no special care), Screen (others put themselves in the line of fire), Close (tight around them; better point defence for them, but the fleet dodges worse).</summary>
public enum CoverStyle { Loose, Screen, Close }

/// <summary>What a fleet's guns and missiles go for: the nearest ship, or the ship it takes to be the enemy flagship.</summary>
public enum FireAt { Nearest, Flagship, Largest, Weakest, Engines, Split }

/// <summary>What a side believes about a sighted fleet carrying the enemy flagship.</summary>
public enum FlagKnown { Unknown, Suspected, Confirmed, Cleared }

/// <summary>Numbers for cover and flagship hunting (ship-classes.json, "flagships").</summary>
public sealed record FlagshipData(
    double ScreenChance = 0.6, double CloseChance = 0.85, double CloseDodge = 0.5,
    double PdProtectFactor = 1.3, double PdOtherFactor = 0.9, double ClosePdFactor = 1.15,
    double UnshotAccuracy = 1.15, double UnshotHours = 0.05, double EscortScreenMm = 5,
    double SuspectAt = 0.5, double SignalPerHour = 0.12, double TightBeamLeak = 0.4, double CoverPerHour = 0.06,
    double DecayPerHour = 0.01, double MaxGuess = 0.95);

/// <summary>
/// The flagship war: protect marks and cover, going for the enemy flagship, and working out which
/// enemy fleet carries it. A side never knows for sure until a close look: it suspects a fleet from
/// the signal traffic around it (none in radio silence; less with Tight-beam comms) and from fleets
/// guarding something in it (which a decoy protect mark fakes). Confidence grows the longer a fleet
/// is watched.
/// </summary>
public static class Flagships
{
    public static FlagshipData D => GameData.Flagships;

    /// <summary>Is this ship marked to be protected? By default only the flagship is.</summary>
    public static bool IsProtected(World w, Ship s) => s.Protect ?? Light.Flagship(w, s.Side)?.Id == s.Id;

    public static bool GuardsSomething(World w, Fleet f) => f.Cover != CoverStyle.Loose && w.ShipsOf(f).Any(s => IsProtected(w, s));

    /// <summary>
    /// A shot or salvo aimed at a protected ship in a screening fleet may meet another ship first: one of
    /// the fleet's unprotected ships, or a ship of a fleet escorting it close by.
    /// </summary>
    public static Ship Screened(Simulation sim, Ship target)
    {
        var w = sim.World;
        if (w.FleetOf(target) is not Fleet f || f.Cover == CoverStyle.Loose || !IsProtected(w, target)) return target;
        var shields = w.ShipsOf(f).Where(s => !IsProtected(w, s)).ToList();
        foreach (var e in w.Fleets)
            if (e.Side == f.Side && e.Id != f.Id && e.Mission == Mission.Escort && e.MissionFleetId == f.Id
                && Vec2.Distance(e.Position, f.Position) <= D.EscortScreenMm)
                shields.AddRange(w.ShipsOf(e));
        if (shields.Count == 0) return target;
        var chance = f.Cover == CoverStyle.Close ? D.CloseChance : D.ScreenChance;
        if (sim.Rng.NextDouble() >= chance) return target;
        return shields[sim.Rng.Range(0, shields.Count)];
    }

    /// <summary>Point defence works harder for protected ships (and harder still in Close cover), a little less for the rest.</summary>
    public static double PointDefenceFactor(World w, Fleet f, Ship target)
    {
        if (!w.ShipsOf(f).Any(s => IsProtected(w, s))) return 1;
        if (!IsProtected(w, target)) return D.PdOtherFactor;
        return D.PdProtectFactor * (f.Cover == CoverStyle.Close ? D.ClosePdFactor : 1);
    }

    /// <summary>A fleet in Close cover keeps formation round its protected ships, so dodging helps it less.</summary>
    public static double DodgeFactor(Fleet f, double factor) => f.Cover == CoverStyle.Close ? 1 - (1 - factor) * D.CloseDodge : factor;

    /// <summary>A ship nobody has fired at lately aims steadier (the cost of everyone going for one target).</summary>
    public static double SteadyAim(Ship shooter, double hour) => hour - shooter.LastTargetedHour > D.UnshotHours ? D.UnshotAccuracy : 1;

    /// <summary>
    /// For a fleet set to go for the flagship: the ship to fire at among enemies in range. The fleet with the
    /// strongest suspicion first; in it the confirmed flagship, else a weighted guess among its destroyers.
    /// Null when no fleet in range is suspected (the fleet fires at the nearest as usual).
    /// </summary>
    public static Ship? FlagTarget(Simulation sim, Ship shooter, double range)
    {
        var w = sim.World;
        Sighting? best = null;
        foreach (var s in w.Sightings)
        {
            if (s.SeenBy != shooter.Side || !s.Visible || s.Flag is not (FlagKnown.Suspected or FlagKnown.Confirmed)) continue;
            if (w.FindFleet(s.FleetId) is not Fleet f || Vec2.Distance(f.Position, shooter.Position) > range) continue;
            if (best == null || s.FlagScore > best.FlagScore) best = s;
        }
        if (best == null || w.FindFleet(best.FleetId) is not Fleet target) return null;
        var ships = w.ShipsOf(target).Where(s => Vec2.Distance(s.Position, shooter.Position) <= range).ToList();
        if (ships.Count == 0) return null;
        if (best.Flag == FlagKnown.Confirmed && ships.Find(s => s.Id == best.FlagShipId) is Ship known) return known;
        var guesses = ships.Where(Light.CommandCapable).ToList();
        if (guesses.Count == 0) guesses = ships;
        // The same guess for a while (by the hour), so a fleet's volleys do not scatter.
        return guesses.OrderBy(s => s.Id).ElementAt((int)((ulong)(shooter.Id * 7919 + (long)w.Hour) % (ulong)guesses.Count));
    }

    /// <summary>
    /// For a brain 2 missile fired by a fleet going for the flagship: the confirmed flagship in its target's
    /// fleet, if that is not already its target. Brain 1 missiles can't change their minds.
    /// </summary>
    public static Ship? Retarget(World w, Missile missile, Ship target)
    {
        if (w.FindShip(missile.FromShipId) is not Ship from || w.FleetOf(from) is not { FireAt: FireAt.Flagship }) return null;
        var s = w.Sightings.Find(x => x.SeenBy == missile.Side && x.FleetId == target.FleetId);
        if (s is not { Flag: FlagKnown.Confirmed, FlagShipId: int id } || id == target.Id) return null;
        return w.FindShip(id) is Ship real && real.FleetId == target.FleetId ? real : null;
    }

    /// <summary>Once an hour: each side's watch on the enemy fleets it can see builds up (or fades).</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        if (!w.Light.On) return;
        foreach (var s in w.Sightings)
        {
            if (s.Own || w.FindFleet(s.FleetId) is not Fleet f) continue;
            var enemy = f.Side;
            var flag = Light.Flagship(w, enemy);
            var carries = flag != null && flag.FleetId == f.Id;
            var before = s.Flag;
            // A confirmed flagship that has left the fleet (or died): back to watching.
            if (s.Flag == FlagKnown.Confirmed && !carries) { s.Flag = FlagKnown.Unknown; s.FlagScore = 0; s.FlagShipId = null; }
            if (!s.Visible)
            {
                if (s.Flag != FlagKnown.Confirmed) s.FlagScore = Math.Max(0, s.FlagScore - D.DecayPerHour);
            }
            else if (s.Flag != FlagKnown.Confirmed)
            {
                // A close look settles it.
                if (s.Classes.Count > 0 && w.Hour - s.ClassesHour < 1)
                {
                    if (carries) { s.Flag = FlagKnown.Confirmed; s.FlagScore = 1; s.FlagShipId = flag!.Id; }
                    else if (s.Flag != FlagKnown.Cleared) { s.Flag = FlagKnown.Cleared; s.FlagScore = 0; }
                }
                else
                {
                    var rate = 0.0;
                    if (carries && !Economy.Of(w, enemy).RadioSilence)
                        rate += D.SignalPerHour * (Tech.Has(w, enemy, Tech.TightBeam) ? D.TightBeamLeak : 1);
                    if (GuardsSomething(w, f)) rate += D.CoverPerHour;
                    s.FlagScore = Math.Min(D.MaxGuess, s.FlagScore + rate);
                }
            }
            if (s.Flag is FlagKnown.Unknown or FlagKnown.Suspected)
                s.Flag = s.FlagScore >= D.SuspectAt ? FlagKnown.Suspected : FlagKnown.Unknown;
            if (s.Flag != before && s.Flag is FlagKnown.Suspected or FlagKnown.Confirmed && w.ActiveBattle is Battle b
                && b.For(enemy).Fleets.Contains(f.Name))
                BattleRecords.Mark(w, b, s.SeenBy, MarkKind.Flagship, $"{s.Name}: flagship {s.Flag.ToString().ToLowerInvariant()}");
            if (s.Flag != before && s.Flag is FlagKnown.Suspected or FlagKnown.Confirmed or FlagKnown.Cleared)
                sim.Log.Info("flagship", $"{s.SeenBy} thinks {s.Name}: {s.Flag}", new { side = s.SeenBy.ToString(), fleetId = s.FleetId, score = s.FlagScore, truth = carries });
        }
    }
}

/// <summary>Mark a ship protected (or not): fleets in Screen or Close cover guard it, and point defence puts it first.</summary>
public sealed record SetProtectCommand(int FleetId, int ShipId, bool Protect) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (world.FindShip(ShipId) is not Ship ship) return;
        ship.Protect = Protect;
        sim.Log.Info("fleet", $"{ship.Name} {(Protect ? "protected" : "not protected")}", new { ShipId, Protect, Source });
    }
}

/// <summary>Set how a fleet guards its protected ships, and what it fires at.</summary>
public sealed record SetCoverCommand(int FleetId, CoverStyle Cover, FireAt FireAt) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Cover") is not Fleet fleet) return;
        fleet.Cover = Cover;
        fleet.FireAt = FireAt;
        sim.Log.Info("fleet", $"{fleet.Name} cover {Cover}, fires at {FireAt}", new { FleetId, Cover, FireAt, Source });
    }
}

/// <summary>Radio silence: no orders leave the flagship (they wait), and the enemy can't follow its signal traffic.</summary>
public sealed record SetRadioSilenceCommand(Side Side, bool On) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        Economy.Of(world, Side).RadioSilence = On;
        if (!On)
        {
            // Held orders go out now.
            foreach (var t in world.Light.Held.Where(t => t.Side == Side).ToList())
            {
                world.Light.Held.Remove(t);
                var at = t.Command.Destination(world)?.At ?? Light.Seat(world, Side);
                t.SentHour = world.Hour;
                t.ArriveHour = world.Hour + Light.DelayHours(world, Side, at);
                world.Light.InTransit.Add(t);
            }
        }
        sim.Log.Info("light", $"{Side} radio silence {(On ? "on" : "off")}", new { side = Side.ToString(), On, Source });
    }
}
