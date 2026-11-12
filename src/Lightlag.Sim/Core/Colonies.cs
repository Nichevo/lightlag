using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

// ---- The build board: tiles, deposits and placed pieces ----------------------------------

/// <summary>A body's surface as a grid of tiles: land (l), ocean (o), ice (i) and mountains (m, no building).</summary>
public sealed class Board
{
    public int W { get; set; }
    public int H { get; set; }

    /// <summary>One character per tile, row by row from the top left.</summary>
    public string Tiles { get; set; } = "";

    /// <summary>Mineral deposits, each on one land tile.</summary>
    public List<Deposit> Deposits { get; set; } = new();

    public char At(int x, int y) => x < 0 || y < 0 || x >= W || y >= H ? 'm' : Tiles[y * W + x];
    public Deposit? DepositAt(int x, int y) => Deposits.Find(d => d.X == x && d.Y == y);
    [System.Text.Json.Serialization.JsonIgnore] public int Buildable => Tiles.Count(c => c != 'm');

    /// <summary>Rare-element assays have found this board's extra deposit.</summary>
    public bool Assayed { get; set; }
}

public sealed class Deposit
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Mineral { get; set; } = "";
    public bool Rich { get; set; }
}

/// <summary>A building on the board. Built when HoursLeft reaches 0; Condition wears without spare parts.</summary>
public sealed class Piece
{
    public int Id { get; set; }
    public string Kind { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 1;
    public int H { get; set; } = 1;
    public double HoursLeft { get; set; }
    public double Condition { get; set; } = 1;

    /// <summary>Upgrades fitted (research it has been refitted for), and hours left of a refit under way.</summary>
    public int Level { get; set; }
    public double RefitHoursLeft { get; set; }

    /// <summary>Worked out each hour: share of its staff it has, and whether it has power.</summary>
    public double Staff { get; set; } = 1;
    public bool Powered { get; set; } = true;
    public double SparesShare { get; set; } = 1;

    /// <summary>
    /// Queued by the player behind other work or before the Industry was there: not paid for or started yet.
    /// It holds its tiles and starts (paying its Industry then) once nothing else is being built on the planet.
    /// </summary>
    public bool Waiting { get; set; }

    /// <summary>Materials still to come out of the planet's store before building can go on (null: none).</summary>
    public Dictionary<string, double>? MaterialsLeft { get; set; }

    /// <summary>Research that makes this kind of piece make more (worked out each hour; see Colonies.MakesFactor).</summary>
    public double Boost { get; set; } = 1;

    /// <summary>Academies: hours of training done towards the next batch of scientists.</summary>
    public double TrainHours { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public bool Built => HoursLeft <= 1e-9;
    public bool Covers(int x, int y) => x >= X && y >= Y && x < X + W && y < Y + H;
}

/// <summary>Freighters carrying goods between planets: from a planet's spare to one that asked, or on a fixed run.</summary>
public sealed class Shipment
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public int FleetId { get; set; }
    public int FromBodyId { get; set; }
    public int ToBodyId { get; set; }
    public Dictionary<string, double> Cargo { get; set; } = new();
    public bool Loaded { get; set; }
    public int? RunId { get; set; }
    public double StartHour { get; set; }

    /// <summary>A planet to go by on the way (the run's route setting, see SupplyChain), and whether it has been passed.</summary>
    public int? ViaBodyId { get; set; }
    public bool ViaDone { get; set; }
}

/// <summary>A run the player set: carry a good (or all spare) from one planet to another every so many days, perhaps escorted.</summary>
public sealed class FixedRun
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public int FromBodyId { get; set; }
    public int ToBodyId { get; set; }
    public string Good { get; set; } = "";
    public double EveryDays { get; set; } = 20;
    public int? EscortFleetId { get; set; }
    public double NextHour { get; set; }
}

public sealed record BonusData(string Next, double Share);

public sealed record GoodData(string Id, string Name, string Short);

public sealed record ColoniesData(
    List<GoodData>? Goods = null, double WearPerDay = 0.02, double MendPerDay = 0.05, double BaseStoreDays = 20,
    double FuelPerSupply = 0.01, double NoFuelRefill = 0.4, double RefitDays = 5, double ShipyardRefitDays = 10,
    double RefitOutput = 0.75, double LevelBoost = 0.2, double LabBoostCap = 0.5, double AskBelow = 0.5,
    double SettlerSpares = 10, double BasePower = 6, Dictionary<string, double>? HomeStock = null,
    Dictionary<string, List<string>>? Presets = null);

/// <summary>
/// Colonies: each held body has a board of tiles where buildings are placed as shaped pieces. Pieces need
/// people (staffed in priority order), power (from power plants and solar fields) and spare parts to stay
/// in good repair, and they make food, Industry, goods and defence. Goods are kept per planet, up to what
/// its storehouses hold, and freighters carry them where they are asked for: Auto first (a planet asks when
/// it falls under its siege stock target), or on fixed runs. Shot-down cargo never arrives. Runs once per
/// game hour in full games, before food (see Planets.Update).
/// </summary>
public static class Colonies
{
    public static ColoniesData C => GameData.Economy.Colonies ?? new ColoniesData();
    private static PlanetsData D => Planets.D;

    public const string Spares = "spares", Fuel = "fuel", Shipyard = "shipyard";

    public static readonly string[] Areas = { "food", "power", "defence", "shipyard", "industry", "research", "housing" };

    public static string AreaName(string area) => area switch
    {
        "food" => "Food", "power" => "Power", "defence" => "Defence", "shipyard" => "Shipyard",
        "industry" => "Mines and workshops", "research" => "Research", "housing" => "Housing and stores", _ => area,
    };

    public static readonly string[] PresetNames = { "balanced", "hold-out", "war-output", "growth" };

    public static string PresetName(string id) => id switch
    {
        "balanced" => "Balanced", "hold-out" => "Hold out", "war-output" => "War output", "growth" => "Growth", "custom" => "Custom", _ => id,
    };

    public static List<string> PresetOrder(string preset) =>
        C.Presets != null && C.Presets.TryGetValue(preset, out var order) ? new List<string>(order) : new List<string>(Areas);

    public static List<GoodData> Goods => C.Goods ?? new List<GoodData>();
    public static string GoodName(string id) => Goods.Find(g => g.Id == id)?.Name ?? id;

    /// <summary>The area order this planet staffs and supplies in.</summary>
    public static List<string> Priority(Body b) => b.Priority ?? PresetOrder(b.Preset);

    public static int Rank(Body b, string area)
    {
        var i = Priority(b).IndexOf(area);
        return i < 0 ? Areas.Length : i;
    }

    public static IEnumerable<Piece> Built(Body b) => b.Pieces.Where(p => p.Built);

    // ---- The board ------------------------------------------------------------------------

    /// <summary>Board size for a body: by its type, smaller for moons; none for giants and the star.</summary>
    public static (int W, int H) BoardSize(Body b)
    {
        var t = Planets.TypeOf(b);
        if (b.Kind == BodyKind.Star || t.W <= 0 || t.H <= 0) return (0, 0);
        if (b.Kind != BodyKind.Moon) return (t.W, t.H);
        return (Math.Max(4, (int)Math.Round(t.W * 0.7)), Math.Max(3, (int)Math.Round(t.H * 0.7)));
    }

    /// <summary>Lays out every body's tiles and deposits (from a stream of their own, so other randomness is untouched).</summary>
    public static void MakeBoards(World w)
    {
        var rng = new SimRandom(w.Seed, 79);
        foreach (var b in w.Bodies.OrderBy(b => b.Id))
        {
            var (bw, bh) = BoardSize(b);
            if (bw == 0) { b.Board = null; continue; }
            b.Board = MakeBoard(Planets.TypeOf(b), bw, bh, rng, b.Shipyard && b.Owner != null);
        }
    }

    private static Board MakeBoard(PlanetTypeData t, int bw, int bh, SimRandom rng, bool home)
    {
        var tiles = new char[bw * bh];
        for (var i = 0; i < tiles.Length; i++) tiles[i] = 'l';
        // Oceans spread from the left edge, ice caps sit on the top and bottom rows, mountains are scattered.
        var ocean = (int)Math.Round(t.Ocean * tiles.Length);
        for (var placed = 0; placed < ocean;)
        {
            var y = rng.Range(0, bh);
            var x = 0;
            while (x < bw && tiles[y * bw + x] == 'o') x++;
            if (x >= bw / 2 + 1) { if (++placed > ocean * 4) break; continue; }
            tiles[y * bw + x] = 'o';
            placed++;
        }
        var ice = (int)Math.Round(t.Ice * tiles.Length);
        for (var tries = 0; ice > 0 && tries < 400; tries++)
        {
            var y = t.Ice >= 0.4 ? rng.Range(0, bh) : rng.Chance(0.5) ? 0 : bh - 1;
            var x = rng.Range(0, bw);
            if (tiles[y * bw + x] != 'l') continue;
            tiles[y * bw + x] = 'i';
            ice--;
        }
        var mountains = (int)Math.Round(t.Mountain * tiles.Length);
        for (var tries = 0; mountains > 0 && tries < 400; tries++)
        {
            var i = rng.Range(0, tiles.Length);
            if (tiles[i] != 'l') continue;
            tiles[i] = 'm';
            mountains--;
        }
        var board = new Board { W = bw, H = bh, Tiles = new string(tiles) };
        var minerals = t.Minerals ?? Array.Empty<string>();
        var count = minerals.Length == 0 ? 0 : t.Deposits;
        for (var tries = 0; board.Deposits.Count < count && tries < 400; tries++)
        {
            var x = rng.Range(0, bw);
            var y = rng.Range(0, bh);
            if (board.At(x, y) != 'l' || board.DepositAt(x, y) != null) continue;
            board.Deposits.Add(new Deposit { X = x, Y = y, Mineral = minerals[board.Deposits.Count % minerals.Length], Rich = rng.Chance(0.25) });
        }
        if (home)
        {
            // A home always has iron and uranium to start on, and water (ocean or ice) for fuel.
            foreach (var must in new[] { "iron", "uranium" })
                if (!board.Deposits.Any(d => d.Mineral == must) && FreeLand(board).FirstOrDefault() is var (fx, fy) && fx >= 0)
                    board.Deposits.Add(new Deposit { X = fx, Y = fy, Mineral = must });
            if (!board.Tiles.Contains('o') && !board.Tiles.Contains('i'))
            {
                var arr = board.Tiles.ToCharArray();
                arr[arr.Length - 1] = 'i';
                board.Tiles = new string(arr);
            }
        }
        return board;
    }

    /// <summary>
    /// Rare-element assays: the body shows one more deposit (once), on a free land tile, of the mineral its
    /// type has fewest of. No dice: the tile is picked from the body's id, so both sides see the same find.
    /// </summary>
    public static void ExtraDeposit(World w, Body b)
    {
        if (b.Board is not Board board || board.Assayed) return;
        board.Assayed = true;
        var minerals = Planets.TypeOf(b).Minerals ?? Array.Empty<string>();
        if (minerals.Length == 0) return;
        var mineral = minerals.OrderBy(m => board.Deposits.Count(d => d.Mineral == m)).ThenBy(m => Array.IndexOf(minerals, m)).First();
        var free = new List<(int X, int Y)>();
        for (var y = 0; y < board.H; y++)
            for (var x = 0; x < board.W; x++)
                if (board.At(x, y) == 'l' && board.DepositAt(x, y) == null && !b.Pieces.Any(p => p.Covers(x, y))) free.Add((x, y));
        if (free.Count == 0) return;
        var (fx, fy) = free[(b.Id * 7 + 3) % free.Count];
        board.Deposits.Add(new Deposit { X = fx, Y = fy, Mineral = mineral });
    }

    private static IEnumerable<(int X, int Y)> FreeLand(Board board)
    {
        for (var y = 0; y < board.H; y++)
        for (var x = 0; x < board.W; x++)
            if (board.At(x, y) == 'l' && board.DepositAt(x, y) == null) yield return (x, y);
        yield return (-1, -1);
    }

    private static bool TileFits(Board board, BuildingData data, int x, int y)
    {
        var c = board.At(x, y);
        return data.Terrain switch
        {
            "mountain" => c == 'm',
            "any" => c is 'l' or 'o' or 'i',
            "ocean" => c == 'o',
            "ice" => c == 'i',
            "oceanOrIce" => c is 'o' or 'i',
            "landOrIce" => c is 'l' or 'i',
            "deposit" => c == 'l',
            _ => c == 'l',
        };
    }

    /// <summary>Why this piece can't go at (x, y) on the body's board, or null if it fits.</summary>
    public static string? FitProblem(Body b, BuildingData data, int x, int y, bool turned) => FitProblem(b, data, x, y, turned, null);

    /// <summary>Which tiles pieces cover (row by row), for checking many spots at once.</summary>
    private static bool[] Covered(Body b, Board board)
    {
        var covered = new bool[board.W * board.H];
        foreach (var p in b.Pieces)
            for (var y = Math.Max(0, p.Y); y < Math.Min(board.H, p.Y + p.H); y++)
                for (var x = Math.Max(0, p.X); x < Math.Min(board.W, p.X + p.W); x++)
                    covered[y * board.W + x] = true;
        return covered;
    }

    private static string? FitProblem(Body b, BuildingData data, int x, int y, bool turned, bool[]? covered)
    {
        if (b.Board is not Board board) return $"Nothing can be built on {b.Name}";
        var (pw, ph) = turned ? (data.H, data.W) : (data.W, data.H);
        if (x < 0 || y < 0 || x + pw > board.W || y + ph > board.H) return "Off the edge of the map";
        var deposit = false;
        for (var dy = 0; dy < ph; dy++)
        for (var dx = 0; dx < pw; dx++)
        {
            var tx = x + dx;
            var ty = y + dy;
            if (board.At(tx, ty) == 'm' && data.Terrain != "mountain") return "Mountains: nothing can be built there";
            if (covered != null ? covered[ty * board.W + tx] : b.Pieces.Any(p => p.Covers(tx, ty))) return "Something is already there";
            if (!TileFits(board, data, tx, ty))
                return data.Terrain switch
                {
                    "ocean" => "Must sit on ocean", "ice" => "Needs ice", "oceanOrIce" => "Must sit on ocean or ice",
                    "landOrIce" => "Needs land or ice", "mountain" => "Must sit on mountains", "any" => "Not on mountains", _ => "Needs land",
                };
            if (board.DepositAt(tx, ty) != null) deposit = true;
        }
        if (data.Terrain == "deposit" && !deposit) return "Must sit on a deposit";
        return null;
    }

    /// <summary>The deposit a mine piece works, if any (the richest it covers).</summary>
    public static Deposit? DepositUnder(Body b, Piece p) =>
        b.Board?.Deposits.Where(d => p.Covers(d.X, d.Y)).OrderByDescending(d => d.Rich).FirstOrDefault();

    /// <summary>The best place for a piece (for the computer, and for orders given without a spot): next to a bonus partner, on a rich deposit.</summary>
    public static (int X, int Y, bool Turned)? BestSpot(Body b, BuildingData data)
    {
        if (b.Board is not Board board) return null;
        (int, int, bool)? best = null;
        var bestScore = double.MinValue;
        var covered = Covered(b, board);
        foreach (var turned in data.W == data.H ? new[] { false } : new[] { false, true })
        for (var y = 0; y < board.H; y++)
        for (var x = 0; x < board.W; x++)
        {
            if (FitProblem(b, data, x, y, turned, covered) != null) continue;
            var (pw, ph) = turned ? (data.H, data.W) : (data.W, data.H);
            var probe = new Piece { Kind = data.Id, X = x, Y = y, W = pw, H = ph };
            var score = 0.0;
            if (data.Terrain == "deposit") score += DepositUnder(b, probe)?.Rich == true ? 10 : 5;
            else for (var dy = 0; dy < ph; dy++) for (var dx = 0; dx < pw; dx++) if (board.DepositAt(x + dx, y + dy) != null) score -= 6; // keep deposits free for mines
            score += BonusFor(b, probe) * 20;
            score -= (x + y) * 0.01; // tidy: fill from the top left
            if (score > bestScore) { bestScore = score; best = (x, y, turned); }
        }
        return best;
    }

    /// <summary>Do two pieces touch along an edge?</summary>
    public static bool Touch(Piece a, Piece b) =>
        a.X < b.X + b.W && b.X < a.X + a.W && (a.Y + a.H == b.Y || b.Y + b.H == a.Y)
        || a.Y < b.Y + b.H && b.Y < a.Y + a.H && (a.X + a.W == b.X || b.X + b.W == a.X);

    /// <summary>The bonus a piece gets from built pieces it touches (each partner kind counts once).</summary>
    public static double BonusFor(Body b, Piece p)
    {
        if (Planets.BuildingKind(p.Kind)?.Bonus is not { Count: > 0 } bonuses) return 0;
        return bonuses.Where(x => b.Pieces.Any(o => o != p && o.Kind == x.Next && o.Built && Touch(o, p))).Sum(x => x.Share);
    }

    /// <summary>Pairs of touching pieces that give a bonus, for the board's link markers.</summary>
    public static IEnumerable<(Piece A, Piece B)> Links(Body b)
    {
        foreach (var p in b.Pieces)
            if (Planets.BuildingKind(p.Kind)?.Bonus is { Count: > 0 } bonuses)
                foreach (var o in b.Pieces.Where(o => o != p && bonuses.Any(x => x.Next == o.Kind) && Touch(o, p)))
                    yield return (p, o);
    }

    // ---- Start ------------------------------------------------------------------------------

    /// <summary>Lays out a home: its starting buildings at their best spots, and a starting stock of goods.</summary>
    public static void StartHome(World w, Body b)
    {
        b.Pieces.Clear();
        foreach (var kind in D.HomeBuildings ?? new List<string>())
        {
            if (Planets.BuildingKind(kind) is not BuildingData data) continue;
            // The first mine digs iron, which the workshop turns into spare parts.
            var iron = data.Terrain == "deposit" && !b.Pieces.Any(p => DepositUnder(b, p)?.Mineral == "iron")
                ? b.Board?.Deposits.Where(d => d.Mineral == "iron" && FitProblem(b, data, d.X, d.Y, false) == null).OrderByDescending(d => d.Rich).FirstOrDefault()
                : null;
            if (iron != null) AddPiece(w, b, data, iron.X, iron.Y, false, built: true);
            else if (BestSpot(b, data) is var (x, y, turned)) AddPiece(w, b, data, x, y, turned, built: true);
        }
        foreach (var (good, amount) in C.HomeStock ?? new Dictionary<string, double>()) b.Stock[good] = amount;
        SyncBuildings(b);
    }

    public static Piece AddPiece(World w, Body b, BuildingData data, int x, int y, bool turned, bool built)
    {
        var (pw, ph) = turned ? (data.H, data.W) : (data.W, data.H);
        var piece = new Piece
        {
            Id = w.NextId++, Kind = data.Id, X = x, Y = y, W = pw, H = ph, HoursLeft = built ? 0 : data.Days * 24,
            Level = b.Owner is Side side ? LevelFor(w, side, data) : 0,
            MaterialsLeft = !built && data.Materials is { Count: > 0 } m ? new Dictionary<string, double>(m) : null,
        };
        b.Pieces.Add(piece);
        return piece;
    }

    /// <summary>Keeps Body.Buildings (the kinds standing, used by food, guns and bombing) in step with the board.</summary>
    public static void SyncBuildings(Body b) => b.Buildings = Built(b).Select(p => p.Kind).ToList();

    /// <summary>Takes one built piece of this kind off the board (bombed, failed or pulled down). Returns it.</summary>
    public static Piece? RemoveOne(Body b, Func<Piece, bool> which)
    {
        var p = b.Pieces.LastOrDefault(x => x.Built && which(x));
        if (p == null) return null;
        b.Pieces.Remove(p);
        SyncBuildings(b);
        return p;
    }

    // ---- The hour -----------------------------------------------------------------------------

    public static void Update(Simulation sim)
    {
        var w = sim.World;
        UpdateWants(w);
        Fuels.FadeDraws(w);
        foreach (var b in w.Bodies.Where(b => b.Pieces.Count > 0 || b.Stock.Count > 0))
        {
            if (b.Owner == null) { b.Pieces.RemoveAll(p => !p.Built); continue; }
            Construction(sim, b);
            Work(w, b);
        }
        Transport(sim);
    }

    private static void Construction(Simulation sim, Body b)
    {
        if (Economy.Blockaded(sim.World, b)) return;
        foreach (var p in b.Pieces.Where(p => !p.Built || p.RefitHoursLeft > 0))
        {
            if (p.Waiting)
            {
                StartQueued(sim, b, p);
                continue;
            }
            if (!p.Built)
            {
                // Big buildings wait for their materials (alloys) to come out of the planet's store.
                if (!TakeMaterials(sim.World, b, p)) continue;
                p.HoursLeft -= GameTime.HoursPerTick;
                if (!p.Built) continue;
                p.HoursLeft = 0;
                SyncBuildings(b);
                sim.Log.Info("planets", $"{Planets.BuildingKind(p.Kind)?.Name ?? p.Kind} built at {b.Name}", new { bodyId = b.Id, side = b.Owner.ToString(), kind = p.Kind });
                continue;
            }
            p.RefitHoursLeft = Math.Max(0, p.RefitHoursLeft - GameTime.HoursPerTick);
            if (p.RefitHoursLeft <= 0 && b.Owner is Side side && Planets.BuildingKind(p.Kind) is BuildingData data)
            {
                p.Level = LevelFor(sim.World, side, data);
                sim.Log.Info("planets", $"{data.Name} at {b.Name} is refitted", new { bodyId = b.Id, kind = p.Kind, p.Level });
            }
        }
    }

    /// <summary>
    /// A queued building starts when it is first in line (nothing else under way on the planet) and its
    /// Industry is there: it is paid for then and building begins the next hour.
    /// </summary>
    private static void StartQueued(Simulation sim, Body b, Piece p)
    {
        if (b.Owner is not Side side || Planets.BuildingKind(p.Kind) is not BuildingData data) return;
        if (b.Pieces.Any(o => !o.Built && !o.Waiting)) return; // one at a time: the one under way finishes first
        if (b.Pieces.First(o => o.Waiting) != p) return;         // and the queue goes in the order it was placed
        var state = Economy.Of(sim.World, side);
        if (state.Industry < data.Cost) return;
        state.Industry -= data.Cost;
        state.Totals.IndustrySpent += data.Cost;
        p.Waiting = false;
        sim.Log.Info("planets", $"{side} starts a queued {data.Name.ToLowerInvariant()} at {b.Name}", new { side = side.ToString(), bodyId = b.Id, kind = p.Kind, cost = data.Cost });
    }

    /// <summary>Why a building can't start now, for the screens: under way behind another, or short of Industry (null: it can).</summary>
    public static string? QueuedWhy(World w, Body b, Piece p)
    {
        if (!p.Waiting) return null;
        if (b.Pieces.Any(o => !o.Built && !o.Waiting)) return "queued behind the building under way";
        if (b.Pieces.First(o => o.Waiting) != p) return "queued behind another";
        var cost = Planets.BuildingKind(p.Kind)?.Cost ?? 0;
        var have = b.Owner is Side s ? Economy.Of(w, s).Industry : 0;
        return have < cost ? $"waiting for {cost - have:0} more Industry" : "starts next hour";
    }

    /// <summary>Takes what it can of a piece's materials from the planet's store; true once none are left to come.</summary>
    private static bool TakeMaterials(World w, Body b, Piece p)
    {
        if (p.MaterialsLeft is not { Count: > 0 } left) return true;
        foreach (var good in left.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            var take = Math.Min(left[good], Held(b, good));
            if (take > 0) b.Stock[good] = Held(b, good) - take;
            Ledger.Out(w, b.Owner, good, take, "buildings");
            left[good] -= take;
            if (left[good] <= 1e-9) left.Remove(good);
        }
        if (left.Count > 0) return false;
        p.MaterialsLeft = null;
        return true;
    }

    /// <summary>
    /// What each held body waits for: materials for its buildings under way, for the next ship at its yard
    /// (or dry dock), and (at home) for the research its teams start next. Freighters bring them.
    /// </summary>
    public static void UpdateWants(World w)
    {
        var tanks = new Dictionary<Side, List<(string Fuel, double Tank)>>();
        foreach (var b in w.Bodies)
        {
            if (b.Wants.Count > 0) b.Wants.Clear();
            if (b.Owner is not Side side) continue;
            foreach (var g in Goods)
                if (MaterialsWanted(b, g.Id) is var m && m > 0) b.Wants[g.Id] = m;
            foreach (var o in w.Orders.Where(o => o.Side == side && !o.Paid))
            {
                Dictionary<string, double>? mats = null;
                if (o.Kind == OrderKind.Ship && Economy.YardFor(w, o) == b) mats = Economy.MaterialsOf(o);
                else if (o.Kind == OrderKind.Research && Economy.HomeOf(w, side) == b && Research.Next(w, o)) mats = GameData.Research(o.Key).Materials;
                if (mats == null) continue;
                foreach (var (good, amount) in mats) b.Wants[good] = b.Wants.GetValueOrDefault(good) + amount;
            }
            // A yard or dry dock also wants what mending the damaged ships there (or on their way) and refitting
            // the fleets sent to it will take, so Auto transport ships those materials in.
            if (!RepairsHere(w, b, side)) continue;
            foreach (var (good, amount) in YardBills(w, b, side)) b.Wants[good] = b.Wants.GetValueOrDefault(good) + amount;
            // And the fuels its ships burn or should move up to (fission pellets made out at a uranium mine are
            // no use there): a tank's worth for each ship that can burn them, as far as the store holds.
            if (!tanks.TryGetValue(side, out var fuels)) tanks[side] = fuels = FuelTanks(w, side);
            foreach (var (fuel, tank) in fuels)
            {
                var want = Math.Min(tank, StoreCap(b, fuel) * 0.8) - Held(b, fuel);
                if (want > 0.5) b.Wants[fuel] = b.Wants.GetValueOrDefault(fuel) + want;
            }
        }
    }

    /// <summary>
    /// For each fuel other than water (made anywhere): a full tank for every ship that burns it now, or whose
    /// engine burns it and would take it over what it has (the side makes it or has some), or that the side wants made.
    /// </summary>
    private static List<(string, double)> FuelTanks(World w, Side side)
    {
        var tanks = new Dictionary<string, double>();
        if (!Fuels.On(w)) return new List<(string, double)>();
        var wanted = Planner.FuelsWanted(w, side);
        var got = GameData.Fuels.Where(f => !f.Anywhere && (wanted.Contains(f.Id) || Fuels.Obtainable(w, side, f.Id))).ToList();
        foreach (var s in w.Ships.Where(s => s.Side == side).OrderBy(s => s.Id))
        {
            var burns = Fuels.Burns(w, s);
            var job = Fuels.JobOf(w, w.FleetOf(s));
            var best = got.Where(f => burns.Contains(f.Id)).OrderByDescending(f => Fuels.Score(f, job)).ThenBy(f => f.Id, StringComparer.Ordinal).FirstOrDefault();
            var full = Fitting.MaxSupplies(w, s);
            foreach (var f in got.Where(f => f.Id == Fuels.IdOf(s) || f == best))
                tanks[f.Id] = tanks.GetValueOrDefault(f.Id) + full * f.PerSupply;
        }
        return tanks.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Materials a yard needs for the repairs and refits it has coming: damaged ships in orbit or flying there, and fleets sent to it for refit.</summary>
    public static Dictionary<string, double> YardBills(World w, Body yard, Side side)
    {
        var bills = new Dictionary<string, double>();
        void Add(Dictionary<string, double> mats) { foreach (var (g, a) in mats) if (a > 0) bills[g] = bills.GetValueOrDefault(g) + a; }
        foreach (var f in w.Fleets)
        {
            if (f.Side != side || f.ShipIds.Count == 0) continue;
            var refit = f.Refit?.YardBodyId == yard.Id;
            var here = f.OrbitBodyId == yard.Id && f.Flight == null || f.Flight?.TargetBodyId == yard.Id;
            if (!refit && !here) continue;
            foreach (var s in w.ShipsOf(f))
            {
                Add(Wounds.RepairBill(w, s));
                if (refit && s.RefitHoursLeft <= 0 && Refits.Outdated(w, s)) Add(Refits.MaterialsOf(s));
            }
        }
        return bills;
    }

    public static double Want(Body b, string good) => b.Wants.TryGetValue(good, out var v) ? v : 0;

    /// <summary>Materials pieces under way here still wait for (each good summed).</summary>
    public static double MaterialsWanted(Body b, string good) =>
        b.Pieces.Sum(p => p.MaterialsLeft != null && p.MaterialsLeft.TryGetValue(good, out var v) ? v : 0);

    /// <summary>
    /// Staff, power, goods and upkeep for one hour, in the planet's priority order: the top areas are
    /// staffed, powered and kept in spare parts first, and what runs short falls on the bottom.
    /// </summary>
    private static void Work(World w, Body b)
    {
        var built = Built(b).Select(p => (Piece: p, Data: Planets.BuildingKind(p.Kind))).Where(x => x.Data != null)
            .OrderBy(x => Rank(b, x.Data!.Area)).ThenBy(x => x.Piece.Id).ToList();
        // Staff: the planet's base work counts in the industry area.
        var people = b.PopK;
        var baseRank = Rank(b, "industry");
        var baseStaff = b.Industry > 0 ? D.BaseStaffK : 0;
        var baseTaken = false;
        var baseGot = 0.0;
        foreach (var (p, data) in built)
        {
            if (!baseTaken && Rank(b, data!.Area) > baseRank)
            {
                baseGot = Math.Min(Math.Max(0, people), baseStaff);
                people -= baseGot;
                baseTaken = true;
            }
            var need = StaffKOf(w, b, data!);
            p.Staff = need <= 0 ? 1 : Math.Clamp(people / need, 0, 1);
            people -= need * p.Staff;
        }
        if (!baseTaken) baseGot = Math.Min(Math.Max(0, people), baseStaff);
        b.BaseStaff = baseStaff <= 0 ? 1 : Math.Clamp(baseGot / baseStaff, 0, 1);
        // An empty world you hold still works its buildings at the unsettled share, with nobody to staff them.
        if (b.PopK <= 0) foreach (var (p, data) in built) p.Staff = StaffKOf(w, b, data!) <= 0 ? 1 : D.UnsettledShare;

        // Power.
        // Every settled world has a little power of its own (small generators), enough for a first building or two.
        var made = (b.PopK > 0 ? C.BasePower : 0) + built.Where(x => x.Data!.Power < 0).Sum(x => PowerOf(w, b, x.Piece, x.Data!, consume: true)) * (1 - Events.PowerCut(w, b));
        b.PowerMade = made;
        var used = 0.0;
        foreach (var (p, data) in built)
        {
            if (data!.Power <= 0) { p.Powered = true; continue; }
            p.Powered = used + data.Power <= made + 1e-9;
            if (p.Powered) used += data.Power;
        }
        b.PowerUsed = used;
        b.PowerWanted = built.Where(x => x.Data!.Power > 0).Sum(x => x.Data!.Power);

        // Goods: inputs first, then what each piece makes, then upkeep in spare parts.
        var cap = StoreDaysOf(b);
        var caps = new Dictionary<string, double>(); // storage room per good, worked out once this hour
        void Add(string good, double amount, string why)
        {
            if (amount <= 0) return;
            if (!caps.TryGetValue(good, out var room)) caps[good] = room = StoreCap(b, good, cap);
            var before = Held(b, good);
            b.Stock[good] = Math.Min(Math.Max(room, before), before + amount);
            Ledger.In(w, b.Owner, good, amount, why);
            Ledger.Out(w, b.Owner, good, before + amount - Held(b, good), Ledger.StoreFull);
        }
        foreach (var (p, data) in built)
        {
            p.Boost = MakesFactor(w, b, data!);
            var output = Output(w, b, p, data!) * p.Boost;
            if (data!.Makes != null)
                foreach (var (good, perDay) in data.Makes) Add(good, perDay * output * InputShare(w, b, p, data, consume: true) / 24, Ledger.Made);
            else if (data.Uses != null && data.Power >= 0) InputShare(w, b, p, data, consume: true); // labs, academies, arcologies, food vats
            if (data.Mines > 0 && DepositUnder(b, p) is Deposit d && d.Mineral != "")
                Add(d.Mineral, data.Mines * (d.Rich ? 1.5 : 1) * output / 24, Ledger.Mined);
        }
        var upkeepFactor = b.UpkeepFactor = b.Owner is Side keeper ? Tech.Factor(w, keeper, "recycling") : 1;
        b.FissionFuel = FissionFuelPerDay(w, b);
        foreach (var (p, data) in built)
        {
            var want = data!.Upkeep * upkeepFactor / 24;
            var have = Held(b, Spares);
            var got = Math.Min(want, have);
            if (want > 0) b.Stock[Spares] = have - got;
            Ledger.Out(w, b.Owner, Spares, got, "building upkeep");
            p.SparesShare = want <= 0 ? 1 : got / want;
            if (p.SparesShare >= 0.999) p.Condition = Math.Min(1, p.Condition + C.MendPerDay / 24);
            else p.Condition = Math.Max(0, p.Condition - C.WearPerDay * (1 - p.SparesShare) / 24);
        }
        // Water for habitats on airless worlds (Sealed habitats does without).
        b.Thirsty = false;
        if (NeedsWater(w, b))
        {
            var want = built.Count(x => x.Data!.HousingK > 0) * 0.2 / 24;
            var have = Held(b, "water");
            b.Thirsty = have < want - 1e-12;
            b.Stock["water"] = Math.Max(0, have - want);
            Ledger.Out(w, b.Owner, "water", Math.Min(have, want), "habitats");
        }
    }

    public static double Held(Body b, string good) => b.Stock.TryGetValue(good, out var v) ? v : 0;

    /// <summary>Days of storage the planet has: a base store, plus what its storehouses add.</summary>
    public static double StoreDaysOf(Body b) => C.BaseStoreDays + Built(b).Sum(p => Planets.BuildingKind(p.Kind)?.StoreDays ?? 0);

    /// <summary>How much of a good the planet can hold: its storage days of what it uses, or of what it makes, at least 50.</summary>
    public static double StoreCap(Body b, string good, double? days = null) =>
        Math.Max(50, (days ?? StoreDaysOf(b)) * Math.Max(UsePerDay(b, good), MadePerDay(b, good)));

    private static bool NeedsWater(World w, Body b) =>
        Planets.TypeOf(b).Farm <= 0 && !b.Home && b.Owner is Side side && !Tech.Has(w, side, "sealed-habitats")
        && Built(b).Any(p => (Planets.BuildingKind(p.Kind)?.HousingK ?? 0) > 0);

    /// <summary>What a piece takes in a day of each good it uses (fuel plants on ocean draw their own water).</summary>
    public static IEnumerable<(string Good, double PerDay)> Inputs(Body b, Piece p, BuildingData data)
    {
        if (data.Uses == null) yield break;
        foreach (var (good, perDay) in data.Uses)
        {
            if (good == "water" && data.Id is "fuel-plant" or "deuterium-plant" && b.Board?.At(p.X, p.Y) == 'o') continue;
            yield return (good, perDay);
        }
    }

    public const string ElectronicsPlant = "electronics-plant";

    /// <summary>Share of an electronics plant's output without rare earths (copper only).</summary>
    public const double LeanElectronics = 1.0 / 3;

    /// <summary>Share of the piece's inputs on hand this hour (taking them if asked): 1 with no inputs.</summary>
    private static double InputShare(World w, Body b, Piece p, BuildingData data, bool consume)
    {
        var share = 1.0;
        var copper = 1.0;
        var any = false;
        foreach (var (good, perDay) in Inputs(b, p, data))
        {
            any = true;
            var want = perDay * Math.Max(0.01, p.Staff) / 24;
            var have = Held(b, good);
            var got = want <= 0 ? 1 : Math.Min(1, have / want);
            share = Math.Min(share, got);
            if (good == "copper") copper = got;
        }
        if (!any) return 1;
        // Without rare earths an electronics plant still makes a third, from copper alone.
        var lean = data.Id == ElectronicsPlant && share < LeanElectronics * copper;
        if (consume)
            foreach (var (good, perDay) in Inputs(b, p, data))
            {
                var used = lean ? (good == "copper" ? LeanElectronics * copper : 0) : share;
                var before = Held(b, good);
                b.Stock[good] = Math.Max(0, before - perDay * Math.Max(0.01, p.Staff) / 24 * used);
                Ledger.Out(w, b.Owner, good, before - Held(b, good), data.Id);
            }
        if (lean) return LeanElectronics * copper;
        // Without iron a workshop still makes half; labs and academies still work without electronics (no boost).
        return data.Id == "workshop" ? 0.5 + 0.5 * share : data.Research > 0 || data.TrainScientists > 0 ? 1 + 0.5 * share - 0.5 : share;
    }

    /// <summary>Did this piece have its inputs last hour (labs boost, vats grow food, arcologies stay in repair)?</summary>
    public static bool Supplied(Body b, Piece p, BuildingData data) =>
        Inputs(b, p, data).All(x => Held(b, x.Good) >= x.PerDay * Math.Max(0.01, p.Staff) / 24 - 1e-9);

    /// <summary>People a building needs (self-replicating miners run mines with nobody).</summary>
    public static double StaffKOf(World w, Body b, BuildingData data) =>
        data.Mines > 0 && b.Owner is Side side && Tech.Has(w, side, "self-replicating-miners") ? 0 : data.StaffK;

    /// <summary>Research that makes a kind of building make more: deuterium for fuel plants, self-replicating miners for mines.</summary>
    public static double MakesFactor(World w, Body b, BuildingData data)
    {
        if (b.Owner is not Side side) return 1;
        if (data.Id == "fuel-plant") return Tech.Factor(w, side, "deuterium-extraction");
        if (data.Mines > 0) return Tech.Factor(w, side, "self-replicating-miners");
        return 1;
    }

    /// <summary>Power a plant or solar field makes this hour, using its fuel if asked.</summary>
    private static double PowerOf(World w, Body b, Piece p, BuildingData data, bool consume)
    {
        var basePower = -data.Power * Output(w, b, p, data, powered: false);
        if (data.Solar)
        {
            var au = Math.Max(0.2, Vec2.Distance(b.Position, Vec2.Zero) / Physics.MmPerAu);
            return basePower * Math.Clamp(1 / (au * au), 0.05, 2);
        }
        if (data.Hot) return basePower; // geothermal: no fuel
        var staff = Math.Max(0.01, p.Staff);
        var side = b.Owner;
        if (data.Id == "fusion-plant")
        {
            // Water, or a little helium-3 for half again (Helium-3 reactors: half again more).
            var he3 = 0.1 * staff / 24;
            var water = 0.4 * staff / 24;
            if (Held(b, "helium-3") >= he3)
            {
                if (consume) { b.Stock["helium-3"] = Held(b, "helium-3") - he3; Ledger.Out(w, side, "helium-3", he3, "power"); }
                return basePower * 1.5 * (side is Side s1 ? Tech.Factor(w, s1, "helium-3-reactors") : 1);
            }
            if (Held(b, "water") >= water)
            {
                if (consume) { b.Stock["water"] = Held(b, "water") - water; Ledger.Out(w, side, "water", water, "power"); }
                return basePower;
            }
            return basePower * 0.2;
        }
        var factor = 0.3;
        var want = FissionFuelPerDay(w, b) * staff / 24;
        if (Held(b, "helium-3") >= want)
        {
            if (consume) { b.Stock["helium-3"] = Held(b, "helium-3") - want; Ledger.Out(w, side, "helium-3", want, "power"); }
            factor = 1.5;
        }
        else if (Held(b, "uranium") >= want)
        {
            if (consume) { b.Stock["uranium"] = Held(b, "uranium") - want; Ledger.Out(w, side, "uranium", want, "power"); }
            factor = 1;
        }
        return basePower * factor;
    }

    /// <summary>Uranium a fission plant burns a day (Breeder reactors: 40 % less).</summary>
    public static double FissionFuelPerDay(World w, Body b) => 0.2 * (b.Owner is Side side ? Tech.Factor(w, side, "breeder-reactors") : 1);

    /// <summary>
    /// How hard a built piece works, 0 to about 1.5: its staff, power, repair (worn pieces make down to
    /// half), refit (reduced output while it is refitted), upgrades fitted and bonuses from neighbours.
    /// </summary>
    public static double Output(World w, Body b, Piece p, BuildingData data, bool powered = true)
    {
        if (!p.Built) return 0;
        if (powered && !p.Powered) return 0;
        var condition = 0.5 + 0.5 * p.Condition;
        var refit = p.RefitHoursLeft > 0 ? C.RefitOutput : 1;
        return p.Staff * condition * refit * (1 + C.LevelBoost * p.Level) * (1 + BonusFor(b, p));
    }

    public static double Output(World w, Body b, Piece p) => Planets.BuildingKind(p.Kind) is BuildingData data ? Output(w, b, p, data) : 0;

    /// <summary>The sum of a number over the planet's working pieces, each weighted by how hard it works.</summary>
    public static double Sum(World w, Body b, Func<BuildingData, double> what) =>
        Built(b).Sum(p => Planets.BuildingKind(p.Kind) is BuildingData data && what(data) != 0 ? what(data) * Output(w, b, p, data) : 0);

    /// <summary>A good made here per day (at the current staffing, power and repair).</summary>
    public static double MadePerDay(Body b, string good)
    {
        var total = 0.0;
        foreach (var p in Built(b))
        {
            if (Planets.BuildingKind(p.Kind) is not BuildingData data) continue;
            var run = p.Powered ? p.Staff * (0.5 + 0.5 * p.Condition) * (p.RefitHoursLeft > 0 ? C.RefitOutput : 1) * (1 + C.LevelBoost * p.Level) * (1 + BonusFor(b, p)) * p.Boost : 0;
            if (data.Makes != null && data.Makes.TryGetValue(good, out var perDay)) total += perDay * run;
            if (data.Mines > 0 && DepositUnder(b, p) is Deposit d && d.Mineral == good) total += data.Mines * (d.Rich ? 1.5 : 1) * run;
        }
        return total;
    }

    /// <summary>A good used here per day (inputs and upkeep).</summary>
    public static double UsePerDay(Body b, string good)
    {
        var total = b.FuelUse.TryGetValue(good, out var drawn) ? drawn : 0;
        foreach (var p in Built(b))
        {
            if (Planets.BuildingKind(p.Kind) is not BuildingData data) continue;
            foreach (var (g, perDay) in Inputs(b, p, data)) if (g == good) total += perDay * Math.Max(0.01, p.Staff);
            if (good == Spares) total += data.Upkeep * b.UpkeepFactor;
            if (good == "uranium" && data.Id == "power-plant") total += b.FissionFuel;
            if (good == "water" && data.Id == "fusion-plant") total += 0.4;
            if (good == "water" && data.HousingK > 0 && b.Thirsty) total += 0.2;
            if (good == "ammunition") total += data.Ammo * 0.25; // guns fire now and then: keep some in store
        }
        return total;
    }

    /// <summary>
    /// The stock the planet aims to keep of a good: its siege days of what it uses, plus what is waited
    /// for here (materials for buildings under way, ships at the yard, research at home).
    /// A planet's own keep rule replaces the siege days part; siege mode raises both (see SupplyChain).
    /// </summary>
    public static double Target(Body b, string good)
    {
        var want = b.Wants.TryGetValue(good, out var w) ? w : 0;
        if (b.StockRules.Count > 0 && b.StockRules.TryGetValue(good, out var rule) && rule.Keep is double keep)
            return keep * (b.SiegeMode ? SupplyChain.SiegeKeepFactor : 1) + want;
        var days = b.SiegeMode ? Math.Max(b.SiegeDays, SupplyChain.SiegeModeDays) : b.SiegeDays;
        return Math.Min(StoreCap(b, good), days * UsePerDay(b, good)) + want;
    }

    /// <summary>What can be sent away: stock beyond the planet's own target (or its export-above rule); nothing in siege mode.</summary>
    public static double SpareOf(Body b, string good) => b.SiegeMode ? 0 : Math.Max(0, Held(b, good) - SupplyChain.ExportLine(b, good));

    /// <summary>Does the planet ask for more of a good (under its ask line, or waiting for it)?</summary>
    public static bool Asks(Body b, string good, out double upTo)
    {
        var target = Target(b, good);
        var rule = b.StockRules.Count > 0 && b.StockRules.TryGetValue(good, out var r) ? r : null;
        upTo = Math.Max(Math.Max(target * 1.5, 20), rule?.ImportBelow * 1.5 ?? 0);
        if (target <= 0.5 && rule?.ImportBelow == null) return false;
        return Held(b, good) < SupplyChain.AskLine(b, good) || Held(b, good) < Want(b, good) - 1e-6;
    }

    /// <summary>
    /// Days the planet holds out if cut off: food (its own farms against what its people eat) and spare
    /// parts (after which buildings wear). The lower of the two; null if it could hold out for ever.
    /// </summary>
    public static double? HoldsOutDays(Body b)
    {
        double? days = null;
        var foodNet = Planets.FoodNeedPerDay(b) - Planets.FoodMadePerDay(b);
        if (foodNet > 1e-9) days = b.FoodStock / foodNet;
        var sparesNet = UsePerDay(b, Spares) - MadePerDay(b, Spares);
        if (sparesNet > 1e-9) days = Math.Min(days ?? double.MaxValue, Held(b, Spares) / sparesNet);
        return days;
    }

    /// <summary>People the planet can house: its type's room plus habitats (half without water where it is needed).</summary>
    public static double HousingK(Body b) =>
        Built(b).Sum(p => (Planets.BuildingKind(p.Kind)?.HousingK ?? 0) * (b.Thirsty ? 0.5 : 1));

    /// <summary>Your research labs speed up every team: 5 % a lab (more with rare earths), up to the cap.</summary>
    public static double ResearchBoost(World w, Side side)
    {
        var boost = w.Bodies.Where(b => b.Owner == side && !Economy.Blockaded(w, b))
            .Sum(b => Built(b).Where(p => (Planets.BuildingKind(p.Kind)?.Research ?? 0) > 0)
                .Sum(p => Planets.BuildingKind(p.Kind)!.Research * Output(w, b, p) * (Supplied(b, p, Planets.BuildingKind(p.Kind)!) ? 1.5 : 1)));
        return 1 + Math.Min(C.LabBoostCap, boost);
    }

    /// <summary>How fast a shipyard (or a dry dock building ships) works: slower while its tools are refitted, it is worn or short of power.</summary>
    public static double ShipyardRate(World w, Body yard)
    {
        var piece = Built(yard).FirstOrDefault(p => p.Kind == Shipyard) ?? Built(yard).FirstOrDefault(p => p.Kind == DryDock);
        if (piece == null) return 1;
        return (piece.RefitHoursLeft > 0 ? C.RefitOutput : 1) * (0.5 + 0.5 * piece.Condition) * (piece.Powered ? 1 : 0.5) * (piece.Kind == DryDock ? 0.6 : 1);
    }

    public const string DryDock = "dry-dock";

    /// <summary>Does this body have a working repair yard of this side's (the shipyard, or a built dry dock)?</summary>
    public static bool RepairsHere(World w, Body b, Side side) =>
        b.Owner == side && (b.Shipyard || Built(b).Any(p => p.Kind == DryDock && p.Powered));

    /// <summary>A body whose dry dock can build ships (Colony shipyards researched).</summary>
    public static bool DockBuilds(World w, Body b, Side side) =>
        b.Owner == side && !b.Shipyard && Tech.Has(w, side, "colony-shipyards") && Built(b).Any(p => p.Kind == DryDock);

    // ---- Research: refits ---------------------------------------------------------------------

    /// <summary>How many of the research ideas that upgrade this kind of building the side has.</summary>
    public static int LevelFor(World w, Side side, BuildingData data) =>
        (data.RefitBy ?? Array.Empty<string>()).Count(id => Tech.Has(w, side, id))
        + (data.Id == Shipyard ? Economy.Of(w, side).Researched.Count(id => GameData.ResearchById.TryGetValue(id, out var r) && r.Unlocks is "hulls" or "engines") : 0);

    /// <summary>
    /// Research landed: pieces it upgrades start refitting (in priority order, at reduced output), for a
    /// little Industry and spare parts each. New pieces get the upgrade as they are built.
    /// </summary>
    public static void OnResearched(Simulation sim, Side side, string id)
    {
        var w = sim.World;
        var r = GameData.ResearchById.TryGetValue(id, out var data) ? data : null;
        foreach (var b in w.Bodies.Where(b => b.Owner == side))
        {
            var refits = 0;
            foreach (var p in Built(b).OrderBy(p => Rank(b, Planets.BuildingKind(p.Kind)?.Area ?? "")).ThenBy(p => p.Id))
            {
                if (Planets.BuildingKind(p.Kind) is not BuildingData kind) continue;
                var upgrades = kind.RefitBy?.Contains(id) == true || kind.Id == Shipyard && r?.Unlocks is "hulls" or "engines";
                if (!upgrades || LevelFor(w, side, kind) <= p.Level) continue;
                // One after another, so not everything is down at once.
                var days = kind.Id == Shipyard ? C.ShipyardRefitDays : C.RefitDays;
                p.RefitHoursLeft = days * 24 * (1 + 0.5 * refits++);
                Ledger.Out(w, side, Spares, Math.Min(2, Held(b, Spares)), "building refits");
                b.Stock[Spares] = Math.Max(0, Held(b, Spares) - 2);
            }
            if (refits > 0)
                sim.Log.Info("planets", $"{b.Name} starts refits", new { bodyId = b.Id, research = id, refits });
        }
    }

    // ---- Transport ----------------------------------------------------------------------------

    /// <summary>A ship that can carry goods: a freighter or tanker.</summary>
    public static bool IsHauler(Ship s) => SupplyLines.IsSupplyShip(s);

    public static double Hold(World w, Fleet f) => w.ShipsOf(f).Where(IsHauler).Sum(s => Fitting.Spec(s).Cargo);

    /// <summary>Freighter fleets with nothing to do: no supply run, no shipment, stopped, of only cargo ships.</summary>
    public static IEnumerable<Fleet> FreeHaulers(World w, Side side) =>
        w.Fleets.Where(f => f.Side == side && f.ShipIds.Count > 0 && f.SupplyRunFor == null && f.PeopleRun == null && f.Flight == null && !f.Retreating
                            && w.ShipsOf(f).All(IsHauler) && !w.Shipments.Any(s => s.FleetId == f.Id));

    public static string? ShipmentTo(World w, Side side, int bodyId, string good) =>
        w.Shipments.Any(s => s.Side == side && s.ToBodyId == bodyId && s.Cargo.ContainsKey(good)) ? good : null;

    private static void Transport(Simulation sim)
    {
        var w = sim.World;
        RunShipments(sim);
        if (w.Tick % 6 != 0) return;
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            // Planets that ask come first; fixed runs take the freighters left over.
            if (Economy.Of(w, side).AutoTransport) AutoTransport(sim, side);
            FixedRuns(sim, side);
        }
    }

    /// <summary>
    /// Planets ask for goods they are short of (under their ask line, or waiting for materials); a free
    /// freighter brings them. The asks go in the supply chain's order (worst shortage first, see
    /// SupplyChain.ShortSeverity), and the source is the safest run with the most to spare, judged with
    /// the same route risk the Supply tab shows; a run at high risk gets an escort if one is free.
    /// </summary>
    private static void AutoTransport(Simulation sim, Side side)
    {
        var w = sim.World;
        var free = FreeHaulers(w, side).ToList();
        if (free.Count == 0) return;
        // Sources also take in stations' stores (a skimmer's helium-3 over a gas giant).
        var sources = w.Bodies.Where(b => b.Owner == side && (b.Board != null || b.Stock.Count > 0)).OrderBy(b => b.Id).ToList();
        var asks = new List<(Body To, string Good, double UpTo, double Severity)>();
        foreach (var to in w.Bodies.Where(b => b.Owner == side && b.Board != null).OrderBy(b => b.Id))
            foreach (var good in Goods.Select(g => g.Id))
                if (Asks(to, good, out var upTo) && ShipmentTo(w, side, to.Id, good) == null)
                    asks.Add((to, good, upTo, SupplyChain.ShortSeverity(to, good)));
        var served = new HashSet<int>();
        foreach (var (to, good, upTo, _) in asks.OrderByDescending(a => a.Severity).ThenBy(a => a.To.Id).ThenBy(a => a.Good, StringComparer.Ordinal))
        {
            if (free.Count == 0) return;
            if (served.Contains(to.Id)) continue; // one load a planet a round
            var from = sources.Where(b => b.Id != to.Id && !Economy.Blockaded(w, b) && SpareOf(b, good) >= 1)
                .Select(b => (Body: b, Risk: SupplyChain.RiskOf(w, side, b.Position, to.Position, null, false).Risk))
                .OrderBy(x => x.Risk).ThenByDescending(x => SpareOf(x.Body, good)).ThenBy(x => x.Body.Id).FirstOrDefault();
            if (from.Body == null) continue;
            var fleet = free.OrderBy(f => Vec2.Distance(f.Position, from.Body.Position)).ThenBy(f => f.Id).First();
            // Bring it up to half again its target (at least 20), so a freighter isn't sent for a handful.
            var amount = Math.Min(Math.Min(Hold(w, fleet), upTo - Held(to, good)), SpareOf(from.Body, good));
            if (amount < 0.5) continue;
            free.Remove(fleet);
            served.Add(to.Id);
            Start(sim, side, fleet, from.Body, to, new Dictionary<string, double> { [good] = amount }, null);
            if (from.Risk == RouteRisk.High || Lanes.RiskyRun(w, side, from.Body, to)) EscortRisky(sim, fleet);
        }
    }

    /// <summary>A freighter on a run where cargo was lost lately: the nearest idle warfleet without a mission goes with it.</summary>
    private static void EscortRisky(Simulation sim, Fleet freighter)
    {
        var w = sim.World;
        var escort = w.Fleets.Where(f => f.Side == freighter.Side && f.Id != freighter.Id && f.ShipIds.Count > 0 && f.Mission == Mission.None
                                         && f.Flight == null && f.Chase == null && !f.Retreating && f.Refit == null && Combat.Armed(w, f) && !Heat.InBattle(w, f))
            .OrderBy(f => Vec2.Distance(f.Position, freighter.Position)).ThenBy(f => f.Id).FirstOrDefault();
        if (escort == null) return;
        escort.Mission = Mission.Escort;
        escort.MissionFleetId = freighter.Id;
        escort.MissionBodyId = null;
        sim.Log.Info("planets", $"{escort.Name} escorts {freighter.Name} on a risky run", new { escort = escort.Id, freighter = freighter.Id });
    }

    /// <summary>
    /// The shipments Auto transport would send now if it had freighters: (from, to, good, amount), for
    /// planets under their ask line or waiting for materials, from the planet with most to spare.
    /// </summary>
    public static IEnumerable<(Body From, Body To, string Good, double Amount)> TransportWanted(World w, Side side)
    {
        var sources = w.Bodies.Where(b => b.Owner == side && (b.Board != null || b.Stock.Count > 0)).OrderBy(b => b.Id).ToList();
        foreach (var to in w.Bodies.Where(b => b.Owner == side && b.Board != null).OrderBy(b => b.Id))
            foreach (var good in Goods.Select(g => g.Id))
            {
                if (!Asks(to, good, out var upTo) || ShipmentTo(w, side, to.Id, good) != null) continue;
                var from = sources.Where(b => b.Id != to.Id && !Economy.Blockaded(w, b) && SpareOf(b, good) >= 1)
                    .OrderByDescending(b => SpareOf(b, good)).ThenBy(b => b.Id).FirstOrDefault();
                if (from == null) continue;
                var amount = Math.Min(upTo - Held(to, good), SpareOf(from, good));
                if (amount >= 0.5) yield return (from, to, good, amount);
            }
    }

    /// <summary>Sends the nearest free freighter with one load (false: none free, or nothing to send).</summary>
    public static bool ShipOnce(Simulation sim, Side side, Body from, Body to, string good, double amount)
    {
        var w = sim.World;
        var fleet = FreeHaulers(w, side).OrderBy(f => Vec2.Distance(f.Position, from.Position)).ThenBy(f => f.Id).FirstOrDefault();
        if (fleet == null) return false;
        amount = Math.Min(Math.Min(amount, Hold(w, fleet)), SpareOf(from, good));
        if (amount < 0.5) return false;
        Start(sim, side, fleet, from, to, new Dictionary<string, double> { [good] = amount }, null);
        return true;
    }

    /// <summary>What a piece under construction still waits for (null: nothing it can't have).</summary>
    public static string? MaterialShort(Body b, Piece p)
    {
        if (p.MaterialsLeft is not { Count: > 0 } left) return null;
        foreach (var (good, amount) in left.OrderBy(m => m.Key, StringComparer.Ordinal))
            if (Held(b, good) < amount - 1e-9) return $"needs {amount - Held(b, good):0} more {GoodName(good).ToLowerInvariant()}";
        return null;
    }

    private static void FixedRuns(Simulation sim, Side side)
    {
        var w = sim.World;
        foreach (var run in w.FixedRuns.Where(r => r.Side == side && w.Hour >= r.NextHour).ToList())
        {
            if (w.FindBody(run.FromBodyId) is not Body from || w.FindBody(run.ToBodyId) is not Body to || from.Owner != side || to.Owner != side)
            {
                w.FixedRuns.Remove(run);
                continue;
            }
            if (w.Shipments.Any(s => s.RunId == run.Id)) continue;
            var fleet = FreeHaulers(w, side).OrderBy(f => Vec2.Distance(f.Position, from.Position)).ThenBy(f => f.Id).FirstOrDefault();
            if (fleet == null) continue;
            var cargo = new Dictionary<string, double>();
            var room = Hold(w, fleet);
            foreach (var good in run.Good == "" ? Goods.Select(g => g.Id) : new[] { run.Good })
            {
                var amount = Math.Min(room, SpareOf(from, good));
                if (amount < 0.5) continue;
                cargo[good] = amount;
                room -= amount;
            }
            run.NextHour = w.Hour + run.EveryDays * 24;
            if (cargo.Count == 0) continue;
            Start(sim, side, fleet, from, to, cargo, run.Id);
            if (run.EscortFleetId is int e && w.FindFleet(e) is Fleet escort && escort.Side == side && escort.Id != fleet.Id)
            {
                escort.Mission = Mission.Escort;
                escort.MissionFleetId = fleet.Id;
                escort.MissionBodyId = null;
            }
        }
    }

    private static void Start(Simulation sim, Side side, Fleet fleet, Body from, Body to, Dictionary<string, double> cargo, int? runId)
    {
        var w = sim.World;
        var shipment = new Shipment { Id = w.NextId++, Side = side, FleetId = fleet.Id, FromBodyId = from.Id, ToBodyId = to.Id, Cargo = cargo, RunId = runId, StartHour = w.Hour };
        w.Shipments.Add(shipment);
        SupplyChain.OnStart(sim, shipment, fleet);
        Autonomy.Report(sim, fleet, $"{fleet.Name} carries {string.Join(", ", cargo.Select(c => $"{c.Value:0} {GoodName(c.Key).ToLowerInvariant()}"))} from {from.Name} to {to.Name}", NoteKind.Supply);
    }

    /// <summary>Freighters on a shipment: fly to the source, load, fly to the destination, unload. A lost freighter loses its cargo.</summary>
    private static void RunShipments(Simulation sim)
    {
        var w = sim.World;
        foreach (var s in w.Shipments.ToList())
        {
            var fleet = w.FindFleet(s.FleetId);
            var from = w.FindBody(s.FromBodyId);
            var to = w.FindBody(s.ToBodyId);
            if (fleet == null || fleet.ShipIds.Count == 0)
            {
                w.Shipments.Remove(s);
                Economy.Of(w, s.Side).CargoLost++;
                SupplyChain.NoteLoss(w, s, 1);
                sim.Log.Info("planets", $"{s.Side} freighter lost with its cargo", new { side = s.Side.ToString(), shipment = s.Id, cargo = s.Cargo });
                // The news comes home at light speed from where the freighter was heading.
                var delay = to != null ? Light.DelayHours(w, s.Side, to.Position) : 0;
                w.Light.Notes.Add(new FleetNote(w.Light.NextNoteId++, s.Side, s.FleetId, w.Hour, w.Hour + delay,
                    $"A freighter bound for {to?.Name ?? "a planet"} was lost with its cargo", NoteKind.Supply));
                continue;
            }
            if (from == null || to == null || to.Owner != s.Side) { w.Shipments.Remove(s); continue; }
            if (!s.Loaded)
            {
                if (!Economy.InOrbitAt(fleet, from)) { Fly(sim, fleet, from); continue; }
                foreach (var good in s.Cargo.Keys.ToList())
                {
                    var take = Math.Min(s.Cargo[good], Held(from, good));
                    from.Stock[good] = Held(from, good) - take;
                    s.Cargo[good] = take;
                }
                s.Loaded = true;
            }
            if (s.ViaBodyId is int via && !s.ViaDone)
            {
                if (w.FindBody(via) is not Body by || by.Owner != s.Side || Economy.InOrbitAt(fleet, by)) s.ViaDone = true;
                else { Fly(sim, fleet, by); continue; }
            }
            if (!Economy.InOrbitAt(fleet, to)) { Fly(sim, fleet, to); continue; }
            foreach (var (good, amount) in s.Cargo) to.Stock[good] = Held(to, good) + amount;
            w.Shipments.Remove(s);
            Economy.Of(w, s.Side).Totals.Deliveries++;
            SupplyChain.NoteDelivery(w, s);
            sim.Log.Info("planets", $"{fleet.Name} delivers to {to.Name}", new { side = s.Side.ToString(), cargo = s.Cargo });
        }
    }

    private static void Fly(Simulation sim, Fleet f, Body body)
    {
        if (f.Flight is { TargetBodyId: int t } && t == body.Id) return;
        if (f.Flight != null) return;
        var plan = FlightPlanner.Plan(sim.World, f, new FlightRequest(body.Id, body.Position, Economy.ReinforceSpeed, ArrivalStyle.Brake, HoldStyle.Orbit, sim.World.Tick));
        if (plan is { Ok: true, Flight: Flight flight }) { f.Flight = flight; f.Chase = null; }
    }

    // ---- Orders -------------------------------------------------------------------------------

    /// <summary>Why this building can't be ordered at (x, y) (or at its best spot, with no spot given), or null if it can.</summary>
    /// <summary>Why this building can't be placed (null: it can). With queue, a lack of Industry is no bar: it waits in line.</summary>
    public static string? CanPlace(World w, Side side, int bodyId, string kind, int? x, int? y, bool turned, bool queue = false)
    {
        if (!w.Loop) return "There is no economy in the battle sandbox";
        if (w.Winner != null) return "The game is over";
        if (Planets.BuildingKind(kind) is not BuildingData data || !data.Buildable) return $"Unknown building '{kind}'";
        if (w.FindBody(bodyId) is not Body b || b.Owner != side) return "Only on a planet you hold";
        if (b.Board == null) return $"Nothing can be built on {b.Name}: only stations";
        if (data.Needs is string needs && !Tech.Has(w, side, needs)) return $"Research {GameData.Research(needs).Name} first";
        if (data.Food > 0 && !data.AnyAir && data.Terrain != "ocean" && Planets.TypeOf(b).Farm <= 0 && !b.Home) return "Nothing grows here without a dome";
        if (data.Hot && !Hot(b)) return "Only on volcanic worlds and planets close to the sun";
        if (!queue && Economy.Of(w, side).Industry < data.Cost) return $"Needs {data.Cost:0} Industry";
        if (x is int px && y is int py) return FitProblem(b, data, px, py, turned);
        return BestSpot(b, data) == null ? $"No room for a {data.Name.ToLowerInvariant()} on {b.Name}" : null;
    }

    /// <summary>Hot enough for a geothermal plant: a volcanic world, or within 0.6 AU of the sun.</summary>
    public static bool Hot(Body b) => b.Type == "volcanic" || Vec2.Distance(b.Position, Vec2.Zero) < 0.6 * Physics.MmPerAu;

    /// <summary>The Industry and spare parts a refit costs, and other small numbers the screens show.</summary>
    public static IEnumerable<BuildingData> Kinds => (D.Buildings ?? new List<BuildingData>()).Where(k => k.Buildable);
}

// ---- Commands ---------------------------------------------------------------------------------

/// <summary>Set a planet's priority order (null: back to its preset) or preset.</summary>
public sealed record SetPriorityCommand(Side Side, int BodyId, string Preset, List<string>? Order = null) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        if (world.FindBody(BodyId) is not Body b || b.Owner != Side)
        {
            sim.Log.Warn("planets", "Priority order for a planet that is not ours", new { side = Side.ToString(), BodyId, Source });
            return;
        }
        if (Order != null && (Order.Count != Colonies.Areas.Length || Order.Except(Colonies.Areas).Any()))
        {
            sim.Log.Warn("planets", "Bad priority order", new { side = Side.ToString(), BodyId, Order, Source });
            return;
        }
        b.Preset = Order != null ? "custom" : Colonies.PresetNames.Contains(Preset) ? Preset : "balanced";
        b.Priority = Order != null ? new List<string>(Order) : null;
    }
}

/// <summary>Set how many days of goods a planet keeps in store for a siege (30, 60 or 90).</summary>
public sealed record SetSiegeStockCommand(Side Side, int BodyId, int Days) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        if (world.FindBody(BodyId) is not Body b || b.Owner != Side || Days is < 0 or > 365)
        {
            sim.Log.Warn("planets", "Bad siege stock order", new { side = Side.ToString(), BodyId, Days, Source });
            return;
        }
        b.SiegeDays = Days;
    }
}

/// <summary>Turn Auto transport on or off for a side.</summary>
/// <summary>Send one load of a good from one planet to another with the nearest free freighter.</summary>
public sealed record ShipGoodsCommand(Side Side, int FromBodyId, int ToBodyId, string Good, double Amount) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.FindBody(FromBodyId) is not Body from || world.FindBody(ToBodyId) is not Body to || from.Owner != Side || to.Owner != Side
            || !Colonies.ShipOnce(sim, Side, from, to, Good, Amount))
            sim.Log.Warn("transport", "Can't send that load", new { side = Side.ToString(), FromBodyId, ToBodyId, Good, Amount, Source });
    }
}

public sealed record SetAutoTransportCommand(Side Side, bool On) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        var state = Economy.Of(world, Side);
        state.AutoTransport = On;
        state.Auto[AutoArea.Transport] = On ? AutoLevel.Auto : AutoLevel.Manual;
    }
}

/// <summary>Add a fixed run (or, with RunId, remove one).</summary>
public sealed record FixedRunCommand(Side Side, int FromBodyId, int ToBodyId, string Good, double EveryDays, int? EscortFleetId = null, int? RemoveRunId = null) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        if (RemoveRunId is int id)
        {
            world.FixedRuns.RemoveAll(r => r.Id == id && r.Side == Side);
            return;
        }
        if (world.FindBody(FromBodyId) is not Body from || world.FindBody(ToBodyId) is not Body to || from.Owner != Side || to.Owner != Side || from.Id == to.Id
            || Good != "" && Colonies.Goods.All(g => g.Id != Good) || EveryDays < 1)
        {
            sim.Log.Warn("planets", "Bad fixed run", new { side = Side.ToString(), FromBodyId, ToBodyId, Good, EveryDays, Source });
            return;
        }
        world.FixedRuns.Add(new FixedRun
        {
            Id = world.NextId++, Side = Side, FromBodyId = FromBodyId, ToBodyId = ToBodyId, Good = Good, EveryDays = EveryDays,
            EscortFleetId = EscortFleetId is int e && world.FindFleet(e)?.Side == Side ? e : null, NextHour = world.Hour,
        });
        sim.Log.Info("planets", $"{Side} sets a fixed run {from.Name} to {to.Name}", new { side = Side.ToString(), Good, EveryDays, EscortFleetId });
    }
}
