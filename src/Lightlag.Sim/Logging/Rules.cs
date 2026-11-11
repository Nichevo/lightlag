using Lightlag.Sim.Core;

namespace Lightlag.Sim.Logging;

/// <summary>A "should never happen" check that failed.</summary>
public sealed record RuleViolation(string Rule, string Detail);

/// <summary>Thrown in strict mode (headless runs, tests) when a rule check fails.</summary>
public sealed class RuleBrokenException(RuleViolation violation)
    : Exception($"Rule broken: {violation.Rule}: {violation.Detail}")
{
    public RuleViolation Violation { get; } = violation;
}

/// <summary>
/// Rule checks: things that should always be true about the game state. The simulation runs them
/// regularly. In the game a failed rule is logged (area "rule") and play carries on; in headless runs
/// and tests "strict" mode turns it into a crash report so it cannot be missed.
/// Add a rule whenever a bug is found that a check could have caught early.
/// </summary>
public sealed class RuleBook
{
    private readonly List<(string Name, Func<World, IEnumerable<string>> Check)> _rules = new();

    public IReadOnlyList<string> Names => _rules.Select(r => r.Name).ToList();

    public RuleBook Add(string name, Func<World, IEnumerable<string>> check)
    {
        _rules.Add((name, check));
        return this;
    }

    public List<RuleViolation> Check(World world)
    {
        var found = new List<RuleViolation>();
        foreach (var (name, check) in _rules)
        {
            try
            {
                foreach (var detail in check(world)) found.Add(new RuleViolation(name, detail));
            }
            catch (Exception ex)
            {
                found.Add(new RuleViolation(name, $"the check itself threw {ex.GetType().Name}: {ex.Message}"));
            }
        }
        return found;
    }

    /// <summary>The rules every game uses.</summary>
    public static RuleBook Default() => new RuleBook()
        .Add("time-not-negative", w => w.Tick < 0 ? new[] { $"tick is {w.Tick}" } : Array.Empty<string>())
        .Add("research-teams-sane", ResearchTeamsSane)
        .Add("stocks-in-range", w => w.Ships.Where(s => GameData.ShipClasses.ContainsKey(s.ClassId)
                && !(s.Ammo >= -1e-9 && s.Ammo <= SupplyLines.MaxAmmo(s) + 1e-6 && s.Food >= -1e-9 && s.Food <= SupplyLines.MaxFood(s) + 1e-6
                     && s.CargoFuel >= -1e-9 && s.CargoAmmo >= -1e-9 && s.CargoFood >= -1e-9
                     && s.CargoFuel <= SupplyLines.Hold(s).Fuel + 1e-6 && s.CargoAmmo <= SupplyLines.Hold(s).Ammo + 1e-6 && s.CargoFood <= SupplyLines.Hold(s).Food + 1e-6
                     && s.CargoPeople >= -1e-9 && s.CargoPeople <= Planets.PeopleHold(s) + 1e-6 && s.CargoTroops >= -1e-9 && s.CargoTroops <= Planets.TroopHold(s) + 1e-6))
            .Select(s => $"ship {s.Id} {s.Name} has ammo {s.Ammo}, food {s.Food}, cargo {s.CargoFuel}/{s.CargoAmmo}/{s.CargoFood}, people {s.CargoPeople}, troops {s.CargoTroops}"))
        .Add("planets-sane", PlanetsSane)
        .Add("supply-rules-sane", SupplyRulesSane)
        .Add("positions-finite", PositionsFinite)
        .Add("ids-unique", IdsUnique)
        .Add("orbits-hold-radius", OrbitsHoldRadius)
        .Add("fleet-orbit-body-exists", w => w.Fleets.Where(f => f.OrbitBodyId is int b && w.FindBody(b) == null)
            .Select(f => $"fleet {f.Id} {f.Name} orbits missing body {f.OrbitBodyId}"))
        .Add("fleets-hold-their-ships", FleetsHoldTheirShips)
        .Add("fleet-joins-own-side", w => w.Fleets.Where(f => f.JoinFleetId is int j
                && (j == f.Id || w.FindFleet(j) is Fleet other && other.Side != f.Side))
            .Select(f => $"fleet {f.Id} {f.Name} is set to join fleet {f.JoinFleetId}"))
        .Add("intel-sane", w => w.Sides.Where(s => s.Intel.Points < 0 || double.IsNaN(s.Intel.Points)
                || s.Intel.Fleets.Any(f => !f.Position.IsFinite || w.FindFleet(f.FleetId) is Fleet own && own.Side == s.Side))
            .Select(s => $"{s.Side} intel file is broken: points {s.Intel.Points}, {s.Intel.Fleets.Count} fleets"))
        .Add("sightings-sane", w => w.Sightings.Where(s => !s.Position.IsFinite || !s.Velocity.IsFinite
                || w.FindFleet(s.FleetId) is Fleet f && f.Side == s.SeenBy)
            .Select(s => $"{s.SeenBy} sighting of fleet {s.FleetId} is broken"))
        .Add("flights-miss-bodies", w => w.Fleets.Where(f => f.Position.IsFinite && f.InFlight)
            .SelectMany(f => w.Bodies.Where(b => Vec2.Distance(f.Position, b.Position) < b.Radius && f.Flight?.Launching(w, b, w.Hour) != true)
                .Select(b => $"fleet {f.Id} {f.Name} is inside {b.Name} at hour {w.Hour:0.0}")))
        .Add("fleets-not-empty", w => w.Fleets.Where(f => f.ShipIds.Count == 0).Select(f => $"fleet {f.Id} {f.Name} has no ships"))
        .Add("supplies-in-range", w => w.Ships.Where(s => GameData.ShipClasses.ContainsKey(s.ClassId)
                && !(s.Supplies >= 0 && s.Supplies <= Fitting.MaxSupplies(w, s) + 1e-6))
            .Select(s => $"ship {s.Id} {s.Name} has {s.Supplies} supplies"))
        .Add("ship-class-known", w => w.Ships.Where(s => !GameData.ShipClasses.ContainsKey(s.ClassId))
            .Select(s => $"ship {s.Id} {s.Name} has unknown class '{s.ClassId}'"))
        .Add("battle-step-in-range", w => w.Sub is < 0 or >= GameTime.SubSteps ? new[] { $"battle step {w.Sub}" } : Array.Empty<string>())
        .Add("armour-in-range", w => w.Ships.Where(s => GameData.ShipClasses.ContainsKey(s.ClassId)
                && !((s.Armour > 0 || s.Crippled && s.Armour == 0 && s.CrippleLeft > 0) && s.Armour <= Tech.MaxArmour(w, s) + 1e-6) || s.Missiles < 0)
            .Select(s => $"ship {s.Id} {s.Name} has armour {s.Armour} (crippled {s.Crippled}, {s.CrippleLeft} left) and {s.Missiles} missiles"))
        .Add("damage-bars-in-range", w => w.Ships.Where(s => !(s.Weapons is >= 0 and <= 1 && s.Engines is >= 0 and <= 1 && s.Defence is >= 0 and <= 1 && s.Crew is >= 0 and <= 1)
                || s.Knocked.Distinct().Count() != s.Knocked.Count)
            .Select(s => $"ship {s.Id} {s.Name} has bars {s.Weapons}/{s.Engines}/{s.Defence}/{s.Crew}, knocked {string.Join(",", s.Knocked)}"))
        .Add("wrecks-sane", w => w.Wrecks.Where(r => !r.Position.IsFinite || !r.Velocity.IsFinite || r.Salvage.Values.Any(v => !(v > 0)))
            .Select(r => $"wreck {r.Id} at {r.Position} holds {string.Join(",", r.Salvage)}"))
        .Add("zones-sane", w => w.Zones.Where(z => !z.Center.IsFinite || !(z.RadiusMm > 0) || z.T < 0 || z.T > 1)
            .Select(z => $"zone {z.Id} at {z.Center} radius {z.RadiusMm} t {z.T}")
            .Concat(w.Fleets.Where(f => f.ZoneId is int id && !w.Zones.Any(z => z.Id == id && z.Side == f.Side))
                .Select(f => $"fleet {f.Id} {f.Name} holds zone {f.ZoneId}, which it does not have")))
        .Add("lanes-sane", w => w.Lanes.Where(l => l.FromBodyId == l.ToBodyId || l.Seen < 1 || l.LastHour < l.FirstHour)
            .Select(l => $"lane {l.Id} {l.FromBodyId}-{l.ToBodyId} seen {l.Seen} from {l.FirstHour} to {l.LastHour}"))
        .Add("heat-sane", w => w.Ships.Where(s => GameData.ShipClasses.ContainsKey(s.ClassId)
                && !(double.IsFinite(s.Heat) && s.Heat >= 0 && s.HeatSinks >= 0 && s.Shield >= -1e-9 && s.Shield <= Heat.MaxShield(w, s) + 1e-6))
            .Select(s => $"ship {s.Id} {s.Name} has heat {s.Heat}, {s.HeatSinks} sinks and shield {s.Shield}"))
        .Add("dropped-missiles-sane", w => w.Dropped.Where(m => !m.Position.IsFinite || !m.Velocity.IsFinite || m.ExpiresHour < m.WakeHour)
            .Select(m => $"dropped missile {m.Id} is at {m.Position}, wakes {m.WakeHour}, expires {m.ExpiresHour}"))
        .Add("missiles-sane", w => w.Missiles.Where(m => !m.Position.IsFinite || !m.Velocity.IsFinite || m.DeltaVLeft < -1e-9)
            .Select(m => $"missile {m.Id} is at {m.Position} moving {m.Velocity} with {m.DeltaVLeft} fuel"))
        .Add("battle-not-endless", w => w.ActiveBattle is { } b && w.Hour - b.FightingSinceHour > 30 * 24 && !Combat.Standoff(w)
            ? new[] { $"battle {b.Id} has been fighting without a pause since {GameTime.Format(b.FightingSinceHour)}" } : Array.Empty<string>())
        .Add("ship-speed-sane", w => w.Ships.Where(s => !(s.MaxSpeed > 0 && double.IsFinite(s.MaxSpeed)))
            .Select(s => $"ship {s.Id} {s.Name} has max speed {s.MaxSpeed}"))
        .Add("industry-sane", w => w.Sides.Where(s => !(double.IsFinite(s.Industry) && s.Industry >= -1e-6))
            .Select(s => $"{s.Side} has {s.Industry} Industry"))
        .Add("orders-sane", w => w.Orders.Where(o => !double.IsFinite(o.HoursLeft) || o.HoursLeft > o.TotalHours + 1e-6
                || (o.Kind == OrderKind.Ship ? !GameData.ShipClasses.ContainsKey(o.Key) : !GameData.ResearchById.ContainsKey(o.Key)))
            .Select(o => $"order {o.Id} ({o.Kind} {o.Key}) has {o.HoursLeft} of {o.TotalHours} hours left"))
        .Add("claims-sane", w => w.Bodies.Where(b => b.ClaimHours < 0 || b.ClaimHours > Economy.ClaimHoursFor(b)
                || (b.ClaimSide == null && b.ClaimHours != 0) || (b.ClaimSide != null && b.ClaimSide == b.Owner))
            .Select(b => $"{b.Name} claim by {b.ClaimSide} at {b.ClaimHours} hours (owner {b.Owner})"))
        .Add("shipyards-sane", w => w.Bodies.Where(b => b.Shipyard && (b.Owner == null || !(b.ShipyardArmour > 0)
                || b.ShipyardArmour > GameData.Economy.Shipyard.Armour + 1e-6))
            .Select(b => $"{b.Name} shipyard has armour {b.ShipyardArmour} and owner {b.Owner}"))
        .Add("flagships-sane", w => w.Sides.Where(s => s.FlagshipId is int id && w.FindShip(id) is not { } ship
                || s.FlagshipId is int id2 && w.FindShip(id2)?.Side != s.Side)
            .Select(s => $"{s.Side} flagship {s.FlagshipId} is not one of its ships"))
        .Add("light-sane", w => w.Light.InTransit.Where(t => !(t.ArriveHour >= t.SentHour) || !double.IsFinite(t.ArriveHour))
                .Select(t => $"order {t.Command.GetType().Name} sent at {t.SentHour} arrives at {t.ArriveHour}")
            .Concat(w.Light.Reports.Where(r => !double.IsFinite(r.ArriveHour) || r.ArriveHour < r.Snapshot.Hour - 1e-9)
                .Select(r => $"report on fleet {r.Snapshot.FleetId} arrives at {r.ArriveHour}, before it was made"))
            .Concat(!w.Light.On && (w.Light.InTransit.Count > 0 || w.Picture.Count > 0) ? new[] { "light lag is off but orders or reports are in transit" } : Array.Empty<string>()))
        .Add("winner-only-in-loop", w => w.Winner != null && !w.Loop ? new[] { "a sandbox game has a winner" } : Array.Empty<string>());

    /// <summary>Stock rules are known goods with numbers that make sense; route settings and loads going by a planet name real planets.</summary>
    private static IEnumerable<string> SupplyRulesSane(World w)
    {
        static bool Ok(double? v) => v is not double x || (!double.IsNaN(x) && x >= 0 && x <= 100_000);
        foreach (var b in w.Bodies)
            foreach (var (good, r) in b.StockRules)
            {
                if (Colonies.Goods.All(g => g.Id != good)) yield return $"{b.Name} has a stock rule for unknown good '{good}'";
                if (!Ok(r.Keep) || !Ok(r.ExportAbove) || !Ok(r.ImportBelow) || r.Empty)
                    yield return $"{b.Name} {good} stock rule is keep {r.Keep}, export above {r.ExportAbove}, import below {r.ImportBelow}";
            }
        foreach (var side in w.Sides)
            foreach (var r in side.Supply.Routes)
                if (r.FromBodyId == r.ToBodyId || r.ViaBodyId is int v && (v == r.FromBodyId || v == r.ToBodyId) || r.EscortFleetId == null && r.ViaBodyId == null)
                    yield return $"{side.Side} route {r.FromBodyId}->{r.ToBodyId} via {r.ViaBodyId} escort {r.EscortFleetId} makes no sense";
        foreach (var s in w.Shipments)
            if (s.ViaBodyId is int v && (v == s.FromBodyId || v == s.ToBodyId))
                yield return $"shipment {s.Id} goes by its own end {v}";
    }

    private static IEnumerable<string> PlanetsSane(World w)
    {
        foreach (var b in w.Bodies)
        {
            if (!(double.IsFinite(b.PopK) && b.PopK >= 0 && b.PopK <= Planets.Cap(b) + 1e-6))
                yield return $"{b.Name} has {b.PopK}k people (cap {Planets.Cap(b)})";
            if (!(b.Happiness >= -1e-9 && b.Happiness <= 100 + 1e-9) || !(b.OwnShare >= -1e-9 && b.OwnShare <= 1 + 1e-9))
                yield return $"{b.Name} has happiness {b.Happiness} and own share {b.OwnShare}";
            if (!(b.FoodStock >= -1e-9 && double.IsFinite(b.FoodStock))) yield return $"{b.Name} has food stock {b.FoodStock}";
            if (b.Pieces.Any(p => Planets.BuildingKind(p.Kind) == null))
                yield return $"{b.Name} has unknown buildings {string.Join(",", b.Pieces.Select(p => p.Kind))}";
            if (b.Pieces.Count(p => !p.Built) > 1) yield return $"{b.Name} has more than one building under way";
            if (b.Board is Board board)
            {
                var used = new HashSet<(int, int)>();
                foreach (var p in b.Pieces)
                {
                    if (p.X < 0 || p.Y < 0 || p.X + p.W > board.W || p.Y + p.H > board.H)
                        yield return $"{b.Name} has a {p.Kind} off the board at {p.X},{p.Y}";
                    for (int x = p.X; x < p.X + p.W; x++)
                        for (int y = p.Y; y < p.Y + p.H; y++)
                            if (!used.Add((x, y))) yield return $"{b.Name} has pieces overlapping at {x},{y}";
                }
            }
            if (!b.Buildings.SequenceEqual(Colonies.Built(b).Select(p => p.Kind)))
                yield return $"{b.Name} building list is out of step with its board";
            if (b.Stock.Any(kv => !double.IsFinite(kv.Value) || kv.Value < -1e-6))
                yield return $"{b.Name} has a bad stock {string.Join(",", b.Stock.Select(kv => $"{kv.Key}={kv.Value}"))}";
            if (b.LandingSide != null && (b.LandingSide == b.Owner || b.LandingTroops < -1e-9 || b.Garrison < -1e-9))
                yield return $"{b.Name} has a broken landing by {b.LandingSide}";
            if (b.PopK > 0 && b.Owner == null && !w.Rules.StartPopulation) yield return $"{b.Name} has people but no holder";
        }
        foreach (var f in w.Fleets.Where(f => f.PeopleRun != null && !w.ShipsOf(f).Any(Planets.IsColonyShip)))
            yield return $"fleet {f.Id} {f.Name} is on a people run with no colony ship";
    }

    private static IEnumerable<string> PositionsFinite(World w)
    {
        foreach (var b in w.Bodies)
            if (!b.Position.IsFinite) yield return $"body {b.Id} {b.Name} is at {b.Position}";
        foreach (var s in w.Ships)
        {
            if (!s.Position.IsFinite) yield return $"ship {s.Id} {s.Name} is at {s.Position}";
        }
        foreach (var f in w.Fleets)
        {
            if (!f.Position.IsFinite || !f.Velocity.IsFinite) yield return $"fleet {f.Id} {f.Name} is at {f.Position} moving {f.Velocity}";
            if (f.Flight is { } fl && fl.Legs.Any(l => !l.P0.IsFinite || !l.V0.IsFinite || !l.A.IsFinite || !double.IsFinite(l.End)))
                yield return $"fleet {f.Id} {f.Name} has a flight plan with bad numbers";
        }
    }

    private static IEnumerable<string> FleetsHoldTheirShips(World w)
    {
        foreach (var f in w.Fleets)
        {
            if (f.ShipIds.Count == 0) yield return $"fleet {f.Id} {f.Name} has no ships";
            foreach (var id in f.ShipIds)
                if (w.FindShip(id) is not Ship s) yield return $"fleet {f.Id} {f.Name} lists missing ship {id}";
                else if (s.FleetId != f.Id) yield return $"fleet {f.Id} {f.Name} lists ship {s.Name}, which thinks it is in fleet {s.FleetId}";
                else if (s.Side != f.Side) yield return $"fleet {f.Id} {f.Name} has ship {s.Name} from the other side";
        }
        foreach (var s in w.Ships)
            if (w.Fleets.Count(f => f.ShipIds.Contains(s.Id)) != 1)
                yield return $"ship {s.Id} {s.Name} is in {w.Fleets.Count(f => f.ShipIds.Contains(s.Id))} fleets";
    }

    private static IEnumerable<string> IdsUnique(World w)
    {
        var ids = w.Bodies.Select(b => b.Id).Concat(w.Ships.Select(s => s.Id)).Concat(w.Fleets.Select(f => f.Id)).ToList();
        foreach (var g in ids.GroupBy(i => i).Where(g => g.Count() > 1))
            yield return $"id {g.Key} is used {g.Count()} times";
        var max = ids.DefaultIfEmpty(0).Max();
        if (w.NextId <= max) yield return $"next id {w.NextId} is not above the highest id {max}";
    }

    private static IEnumerable<string> OrbitsHoldRadius(World w)
    {
        foreach (var b in w.Bodies)
        {
            if (b.ParentId is not int parentId) continue;
            var parent = w.FindBody(parentId);
            if (parent == null) { yield return $"body {b.Id} {b.Name} orbits missing body {parentId}"; continue; }
            var r = Vec2.Distance(b.Position, parent.Position);
            if (Math.Abs(r - b.OrbitRadius) > Math.Max(1e-6, b.OrbitRadius * 1e-6))
                yield return $"body {b.Id} {b.Name} is {r:0.###} Mm from {parent.Name}, orbit says {b.OrbitRadius:0.###}";
        }
    }

    private static IEnumerable<string> ResearchTeamsSane(World w)
    {
        var t = Research.T;
        foreach (var side in w.Sides)
        {
            if (side.Teams.Count > t.Slots) yield return $"{side.Side} has {side.Teams.Count} teams for {t.Slots} slots";
            foreach (var team in side.Teams)
            {
                if (team.Skill.Length != Research.Fields.Length || team.Skill.Any(k => !(k >= 1 - 1e-9 && k <= t.MaxSkill + 1e-9)))
                    yield return $"{side.Side} {team.Name} has skills {string.Join(",", team.Skill)}";
                if (!(team.Scientists > 0) || team.Kept.Count > 2) yield return $"{side.Side} {team.Name} has {team.Scientists} scientists and keeps {team.Kept.Count} fields";
            }
        }
        foreach (var o in w.Orders.Where(o => o.Kind == OrderKind.Research && o.TeamId is int id && Research.Team(w, o.Side, id) == null))
            yield return $"research order {o.Id} is for missing team {o.TeamId}";
    }
}
