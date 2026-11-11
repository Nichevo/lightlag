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
    public List<Wreck> Wrecks { get; set; } = new();
    public List<Lane> Lanes { get; set; } = new();
    public List<Zone> Zones { get; set; } = new();
    public List<LaneObs> LaneObs { get; set; } = new();
    public List<LossSpot> LossSpots { get; set; } = new();
    public List<Choice> Choices { get; set; } = new();
    public List<Battle> Battles { get; set; } = new();

    /// <summary>What each side knows about the other's fleets (see Sensors).</summary>
    public List<Sighting> Sightings { get; set; } = new();

    /// <summary>
    /// Under light lag, what each side's command knows: the sightings above (and its own fleets) as the
    /// last reports carried them home. Empty with light lag off, when command sees what the fleets see.
    /// </summary>
    public List<Sighting> Picture { get; set; } = new();

    /// <summary>Light lag: whether it is on, light speed, and orders on their way (see Light).</summary>
    public LightState Light { get; set; } = new();

    /// <summary>Missile guidance level per side (1 or 2). Research raises it later.</summary>
    public int[] MissileBrain { get; set; } = { 1, 1 };
    public List<Belt> Belts { get; set; } = new();

    /// <summary>True in a full game (income, building, research, claims, winning); false in the battle sandbox.</summary>
    public bool Loop { get; set; }

    /// <summary>Each side's Industry, research and planet-holding clock, indexed by Side.</summary>
    public SideState[] Sides { get; set; } = { new() { Side = Side.Player }, new() { Side = Side.Rival } };

    /// <summary>Ships being built and upgrades being researched, in queue order (see Economy).</summary>
    public List<WorkOrder> Orders { get; set; } = new();

    /// <summary>Messenger drones in flight (see Drones).</summary>
    public List<Drone> Drones { get; set; } = new();

    /// <summary>Missiles dropped to drift dark until their timer runs out (see DroppedMissiles).</summary>
    public List<Missile> Dropped { get; set; } = new();

    /// <summary>Relays and sensor stations, built and being built (see Economy).</summary>
    public List<Station> Stations { get; set; } = new();

    /// <summary>Freighters carrying goods, and fixed runs set by the player (see Colonies).</summary>
    public List<Shipment> Shipments { get; set; } = new();
    public List<FixedRun> FixedRuns { get; set; } = new();

    /// <summary>How many ships of each class each side has had, so new ships get fresh numbers ("player:gun-frigate").</summary>
    public Dictionary<string, int> ShipNumbers { get; set; } = new();

    public Side? Winner { get; set; }
    public string? WinReason { get; set; }
    public double? WinHour { get; set; }

    /// <summary>Game statistics: each side at the end of each day (see Stats).</summary>
    public List<DayRecord> History { get; set; } = new();

    /// <summary>Computer-run sides: their settings, plans and memory (see Mind).</summary>
    public List<MindState> Minds { get; set; } = new();

    public int NextId { get; set; } = 1;

    /// <summary>Game-start switches for the optional systems (see GameRules).</summary>
    public GameRules Rules { get; set; } = new();

    /// <summary>Planets have been given their first people (old saves get them on load).</summary>
    public bool PeopleStarted { get; set; }

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

/// <summary>Optional systems, chosen at the start of a game.</summary>
public sealed class GameRules
{
    /// <summary>Happiness moves with food, blockades and bombing (off: it stays put, and only hunger makes planets give up).</summary>
    public bool Happiness { get; set; } = true;

    /// <summary>Ammunition and food run down away from home (off: ships keep full stocks).</summary>
    public bool SupplyLines { get; set; } = true;

    /// <summary>Planets start with a few people, not empty.</summary>
    public bool StartPopulation { get; set; }

    /// <summary>The two homes are picked at random from the planets that can hold people.</summary>
    public bool RandomStarts { get; set; }
}

public enum BodyKind { Star, Planet, Moon, Station, Asteroid }

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

    /// <summary>The side that holds it: fleets in orbit here refill their supplies, and it adds to that side's income.</summary>
    public Side? Owner { get; set; }

    /// <summary>Industry per day for whoever holds it. 0 means it is not worth claiming.</summary>
    public double Industry { get; set; }

    /// <summary>A shipyard station orbits here: it builds ships, and fleets here are repaired and rearmed.</summary>
    public bool Shipyard { get; set; }
    public double ShipyardArmour { get; set; }
    public double? LastBombardedHour { get; set; }

    /// <summary>The side holding orbit here to claim it, and for how long so far.</summary>
    public Side? ClaimSide { get; set; }
    public double ClaimHours { get; set; }

    /// <summary>Per side (Player, Rival): hours spent in orbit here towards a survey, and whether it is done (see Research.Surveys).</summary>
    public double[] SurveyHours { get; set; } = { 0, 0 };
    public bool[] Surveyed { get; set; } = { false, false };

    // ---- People (see Planets) ----

    /// <summary>A side's starting world: it holds more people and its farms do well whatever the planet.</summary>
    public bool Home { get; set; }

    /// <summary>People living here, in thousands (0: nobody, an outpost at most).</summary>
    public double PopK { get; set; }

    /// <summary>0 to 100. Unhappy planets work worse, and a blockaded one gives itself up below the surrender line.</summary>
    public double Happiness { get; set; } = 60;

    /// <summary>Share of the people here who are the holder's own (settlers raise it; a taken planet starts at 0).</summary>
    public double OwnShare { get; set; } = 1;

    /// <summary>Food stored here, in days of what the people eat. A blockaded planet lives off it.</summary>
    public double FoodStock { get; set; }
    public bool Hungry { get; set; }
    public double HungryHours { get; set; }

    /// <summary>Buildings standing here (kinds from economy.json, one entry each): kept in step with Pieces.</summary>
    public List<string> Buildings { get; set; } = new();

    /// <summary>The first building under way, and its hours left (from Pieces).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? Building => Pieces.FirstOrDefault(p => !p.Built)?.Kind;
    [System.Text.Json.Serialization.JsonIgnore] public double BuildingHoursLeft => Pieces.FirstOrDefault(p => !p.Built)?.HoursLeft ?? 0;

    /// <summary>The surface (tiles and deposits; null where nothing can be built) and the pieces on it.</summary>
    public Board? Board { get; set; }
    public List<Piece> Pieces { get; set; } = new();

    /// <summary>Goods held here (spares, fuel, minerals), and how many days of them to keep for a siege.</summary>
    public Dictionary<string, double> Stock { get; set; } = new();
    public int SiegeDays { get; set; } = 30;

    /// <summary>The planet's own stock rules per good (keep, export above, import below), and siege mode (see SupplyChain).</summary>
    public Dictionary<string, StockRule> StockRules { get; set; } = new();
    public bool SiegeMode { get; set; }

    /// <summary>Priority order of the areas (null: the preset's), for staff, power and spare parts.</summary>
    public string Preset { get; set; } = "balanced";
    public List<string>? Priority { get; set; }

    /// <summary>Worked out each hour (see Colonies): power made, used and wanted, base work staffed, short of water.</summary>
    public double PowerMade { get; set; }
    public double PowerUsed { get; set; }
    public double PowerWanted { get; set; }
    public double BaseStaff { get; set; } = 1;
    public bool Thirsty { get; set; }

    /// <summary>Worked out each hour: spare parts upkeep factor (Recycling) and uranium a fission plant burns a day (Breeder reactors).</summary>
    public double UpkeepFactor { get; set; } = 1;
    public double FissionFuel { get; set; } = 0.2;

    /// <summary>Garrison factor for bunkers (Hardened bunkers: 1.5), worked out each hour.</summary>
    public double BunkerFactor { get; set; } = 1;

    /// <summary>People cap factor from research (Low-gravity medicine on small bodies), worked out each hour.</summary>
    public double CapFactor { get; set; } = 1;

    /// <summary>Goods waited for here (building materials, ships at the yard, research at home), worked out each hour: freighters bring them.</summary>
    public Dictionary<string, double> Wants { get; set; } = new();

    /// <summary>A side whose bombing of the people here they won't forgive: the planet never surrenders to it.</summary>
    public Side? Grudge { get; set; }
    public double? LastBombedHour { get; set; }
    public double? LastStruckHour { get; set; }

    /// <summary>Bombing damage so far towards knocking out the next building.</summary>
    public double StrikeDamage { get; set; }

    /// <summary>Hours this planet has been blockaded without a break (buildings start to fail after a while).</summary>
    public double BlockadeHours { get; set; }

    /// <summary>Auto top-up: settle orders fill this planet up, one at a time.</summary>
    public bool AutoTopUp { get; set; }

    /// <summary>Troops fighting on the ground: whose, how many (k) and the garrison left.</summary>
    public Side? LandingSide { get; set; }
    public double LandingTroops { get; set; }
    public double Garrison { get; set; }
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

    /// <summary>The hull bar (armour points; full is Tech.MaxArmour). At zero the ship is crippled (see Wounds).</summary>
    public double Armour { get; set; }
    public int Missiles { get; set; }

    /// <summary>The other four damage bars, each a share of full (0..1): weapons, engines, defence (point defence and shields), crew.</summary>
    public double Weapons { get; set; } = 1;
    public double Engines { get; set; } = 1;
    public double Defence { get; set; } = 1;
    public double Crew { get; set; } = 1;

    /// <summary>Hull gone: can't fire, limps at a quarter speed, leaves the fight. CrippleLeft more damage destroys it.</summary>
    public bool Crippled { get; set; }
    public double CrippleLeft { get; set; }

    /// <summary>Hours left of its refit in a yard bay (0: not in a bay).</summary>
    public double RefitHoursLeft { get; set; }

    /// <summary>Parts knocked out in battle (any number), until repaired (damage control or a yard).</summary>
    public List<Module> Knocked { get; set; } = new();

    /// <summary>The first part knocked out (older code and tests set one part this way).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Module? Disabled
    {
        get => Knocked.Count > 0 ? Knocked[0] : null;
        set { Knocked.Clear(); if (value is Module m) Knocked.Add(m); }
    }

    /// <summary>Is this part out of action (knocked out, or every weapon and defence on a crippled ship)?</summary>
    public bool Out(Module m) => Knocked.Contains(m) || Crippled && m != Module.Engines;

    /// <summary>Game hours when the railguns and missile racks can fire again.</summary>
    public double RailsReadyHour { get; set; }

    /// <summary>Marked to be protected (null: only if it is the flagship). See Flagships.</summary>
    public bool? Protect { get; set; }

    /// <summary>When an enemy last fired at it (ships not under fire aim steadier).</summary>
    public double LastTargetedHour { get; set; } = -1e9;
    public double SalvoReadyHour { get; set; }

    /// <summary>Heat (see Heat): how hot it is, whether it has overheated, heat sinks left, and its shield.</summary>
    public double Heat { get; set; }
    public bool Overheated { get; set; }
    public int HeatSinks { get; set; }
    public double Shield { get; set; }

    /// <summary>Research on heat (see Tech.Refresh): heat capacity factor and extra heat sinks.</summary>
    public double TechHeat { get; set; } = 1;

    /// <summary>Settler ships: people carried as a share of the hold (Cryo-sleep doubles it).</summary>
    public double TechPeople { get; set; } = 1;
    public int TechSinks { get; set; }

    /// <summary>Railgun ammunition (supply units; see SupplyLines) and food (days for the crew).</summary>
    public double Ammo { get; set; }
    public double Food { get; set; }

    /// <summary>Supply ships: cargo carried for other ships (fuel and ammunition on a tanker, food on a freighter).</summary>
    public double CargoFuel { get; set; }
    public double CargoAmmo { get; set; }
    public double CargoFood { get; set; }

    /// <summary>Colony ships: settlers aboard (k). Troop ships: soldiers aboard (k). See Planets.</summary>
    public double CargoPeople { get; set; }
    public double CargoTroops { get; set; }

    /// <summary>The module in each slot of its class (see Fitting). Empty: the standard fit.</summary>
    public List<string> Fit { get; set; } = new();

    /// <summary>History for the ship card: when and where it was built (null: at the start), battles fought, enemy ships lost in them.</summary>
    public double? BuiltHour { get; set; }
    public int? BuiltAtBodyId { get; set; }
    public int Battles { get; set; }
    public int EnemyLostInBattles { get; set; }

    /// <summary>Working numbers for the fit, worked out once per fit (not saved).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public ShipClass? SpecCache { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string SpecKey { get; set; } = "";
}

public enum Module { Engines, Railguns, Missiles, PointDefence }

/// <summary>A destroyed ship's drifting hull: salvage for materials until it drifts off (see Wounds).</summary>
public sealed class Wreck
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public string ClassId { get; set; } = "";
    public string Name { get; set; } = "";
    public Vec2 Position { get; set; }
    public Vec2 Velocity { get; set; }

    /// <summary>Lost in orbit: it stays in orbit of this body, at this offset.</summary>
    public int? BodyId { get; set; }
    public Vec2 Offset { get; set; }
    public double Hour { get; set; }
    public Dictionary<string, double> Salvage { get; set; } = new();
}
