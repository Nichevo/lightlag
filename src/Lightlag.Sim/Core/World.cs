namespace Lightlag.Sim.Core;

/// <summary>
/// The whole game state. Plain data only: everything the simulation needs to continue lives here,
/// so a save is just this object written out, and a state hash is a hash of it.
/// </summary>
public sealed class World
{
    public string Scenario { get; set; } = "";
    public ulong Seed { get; set; }
    public long Tick { get; set; }

    /// <summary>
    /// Battle time: when fleets could meet, each hour is run in short steps (see GameTime.SubSteps).
    /// This is how far into the current hour the game is; 0 between hours.
    /// </summary>
    public int Sub { get; set; }
    public RandomState Random { get; set; } = new(0, 1);
    public List<Body> Bodies { get; set; } = new();
    public List<Ship> Ships { get; set; } = new();
    public List<Fleet> Fleets { get; set; } = new();
    public List<Missile> Missiles { get; set; } = new();
    public List<Battle> Battles { get; set; } = new();

    /// <summary>Missile guidance level per side (1 or 2). Research raises it later.</summary>
    public int[] MissileBrain { get; set; } = { 1, 1 };
    public List<Belt> Belts { get; set; } = new();
    public int NextId { get; set; } = 1;

    /// <summary>How many fleets each side has numbered so far, so new fleets get fresh numbers.</summary>
    public int[] FleetNumbers { get; set; } = new int[2];

    public int Day => GameTime.DayOf(Tick);

    /// <summary>The game time in hours, including battle-time steps within the hour.</summary>
    public double Hour => Tick + (double)Sub / GameTime.SubSteps;

    public Battle? ActiveBattle => Battles.Count > 0 && Battles[^1].EndHour == null ? Battles[^1] : null;

    public Body? FindBody(int id) => Bodies.Find(b => b.Id == id);
    public Ship? FindShip(int id) => Ships.Find(s => s.Id == id);
    public Fleet? FindFleet(int id) => Fleets.Find(f => f.Id == id);
    public IEnumerable<Ship> ShipsOf(Fleet f) => f.ShipIds.Select(FindShip).OfType<Ship>();
    public Fleet? FleetOf(Ship s) => FindFleet(s.FleetId);
}

public enum BodyKind { Star, Planet, Moon, Station }

/// <summary>A sun, planet or moon on a circular orbit around its parent (or fixed, if no parent).</summary>
public sealed class Body
{
    public int Id { get; set; }
    public string Key { get; set; } = "";        // the id in the data file, e.g. "verdant"
    public string Name { get; set; } = "";
    public BodyKind Kind { get; set; }
    public string Type { get; set; } = "";       // rocky, terran, gas, ice... (looks and, later, resources)
    public string Color { get; set; } = "ffffff";
    public string? Atmosphere { get; set; }      // rim colour, if it has an atmosphere
    public double Gm { get; set; }               // gravitational parameter, Mm^3 per hour^2
    public int? ParentId { get; set; }
    public double OrbitRadius { get; set; }      // Mm
    public double OrbitPeriodDays { get; set; }
    public double Angle { get; set; }            // radians, where it is on its orbit now
    public double Radius { get; set; }           // Mm, the body's own size
    public Vec2 Position { get; set; }

    /// <summary>The side whose home this is: fleets in orbit here refill their supplies.</summary>
    public Side? Owner { get; set; }
}

/// <summary>An asteroid belt. Scenery for now; hiding places come later.</summary>
public sealed class Belt
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int ParentId { get; set; }
    public double InnerRadius { get; set; }      // Mm
    public double OuterRadius { get; set; }      // Mm
    public string Color { get; set; } = "888888";
}

public enum Side { Player, Rival }

/// <summary>
/// One ship. Ships always fly in a fleet (a lone ship is a fleet of one); the fleet does the flying
/// and each ship sits at its place in the fleet's loose formation.
/// </summary>
public sealed class Ship
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Side Side { get; set; }
    public string ClassId { get; set; } = "";
    public int FleetId { get; set; }
    public Vec2 Position { get; set; }
    public Vec2 FormationOffset { get; set; }
    public double MaxSpeed { get; set; }         // Mm per hour
    public double Accel { get; set; }            // Mm per hour per hour

    /// <summary>The ship's one stock of fuel and railgun ammunition.</summary>
    public double Supplies { get; set; }

    public double Armour { get; set; }
    public int Missiles { get; set; }

    /// <summary>A part knocked out in battle, until repaired at home.</summary>
    public Module? Disabled { get; set; }

    /// <summary>Game hours when the railguns and missile racks can fire again.</summary>
    public double RailsReadyHour { get; set; }
    public double SalvoReadyHour { get; set; }
}

public enum Module { Engines, Railguns, Missiles, PointDefence }
