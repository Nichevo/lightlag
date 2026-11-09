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
    public RandomState Random { get; set; } = new(0, 1);
    public List<Body> Bodies { get; set; } = new();
    public List<Ship> Ships { get; set; } = new();
    public int NextId { get; set; } = 1;

    public int Day => GameTime.DayOf(Tick);

    public Body? FindBody(int id) => Bodies.Find(b => b.Id == id);
    public Ship? FindShip(int id) => Ships.Find(s => s.Id == id);
}

public enum BodyKind { Star, Planet, Moon, Station }

/// <summary>A sun, planet or moon on a circular orbit around its parent (or fixed, if no parent).</summary>
public sealed class Body
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public BodyKind Kind { get; set; }
    public int? ParentId { get; set; }
    public double OrbitRadius { get; set; }      // Mm
    public double OrbitPeriodDays { get; set; }
    public double Angle { get; set; }            // radians, where it is on its orbit now
    public double Radius { get; set; }           // Mm, the body's own size
    public Vec2 Position { get; set; }
}

public enum Side { Player, Rival }

/// <summary>Placeholder ship for M0: it can be ordered to fly to a point. Real flight comes in M2.</summary>
public sealed class Ship
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Side Side { get; set; }
    public Vec2 Position { get; set; }
    public Vec2? Destination { get; set; }
    public double MaxSpeed { get; set; }         // Mm per hour
}
