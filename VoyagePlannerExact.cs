using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Exact voyage solver built on linear assignment instead of backtracking.
///
/// The board has 12 internal edges. Fixing which of them are connected fixes, for every tile, the
/// exact set of directions the chart placed there must connect to — and therefore fixes every
/// border multiplier on the board. Once the multipliers are constants, a chart's contribution
/// depends only on the tile it sits on, so picking the best 9 charts out of the pool is a plain
/// linear assignment problem that the Hungarian algorithm solves optimally in microseconds.
///
/// Total work is (number of edge patterns) x (one assignment), which finishes in milliseconds even
/// with a large chart pool — where the backtracking planner would hit its time limit and return
/// nothing.
///
/// Per-connection borders are handled exactly: their multiplier depends only on the *count* of
/// connections, so the affected tiles get their candidate counts enumerated as part of the pattern.
/// </summary>
public class VoyagePlannerExact
{
    private const int GridSize = 3;
    private const int CellCount = GridSize * GridSize;

    /// <summary>
    /// How many times each pattern's assignment is re-solved with the multipliers read back from
    /// the previous solve. Two passes already converge on real boards; a third is cheap insurance.
    /// </summary>
    private const int RefinementPasses = 3;

    /// <summary>
    /// How many boards get the exhaustive swap search. Every candidate the pattern sweep produced
    /// is polished, because that pass is deterministic and is what makes a solve reproducible —
    /// cutting it short on a clock was why the same board could come back worth 8% less on a second
    /// press of Solve. The cap only exists so a pathological pool cannot run away.
    /// </summary>
    private const int PolishCount = 4000;

    /// <summary>How many boards the randomised stage keeps around to search from.</summary>
    private const int SearchPoolSize = 250;

    /// <summary>Relative gain that counts as progress worth extending the search for.</summary>
    private const double MeaningfulGain = 0.001;

    /// <summary>
    /// A board shape that at least one assignment fits, kept so the search can start again from a
    /// random assignment of that shape instead of only from the one the linearisation produced.
    /// </summary>
    /// <param name="Connections">
    /// Required connection count per tile, or -1 where any count will do. Only tiles carrying a
    /// per-connection border care how many stubs hang off the board edge; pinning the rest would
    /// throw away most of the legal boards.
    /// </param>
    private readonly record struct FeasibleShape(Direction[] Required, int[] Connections);

    /// <summary>
    /// Starting guesses for how much raw value lands on a tile, used to seed the refinement.
    ///
    /// Seeding with zero makes the first solve ignore self multipliers entirely and chase raw
    /// content, which converges on a board that is good at delivering and bad at keeping. Seeding
    /// high does the opposite. Running both and keeping the better result reaches boards neither
    /// seed finds alone.
    /// </summary>
    private static readonly double[] IncomingSeeds = [0, 100];

    // Same direction convention as VoyagePlanner: Up = row+1, Down = row-1, Left = col-1, Right = col+1.
    private static readonly (Direction Dir, int Dr, int Dc)[] Dirs =
    [
        (Direction.Up, 1, 0),
        (Direction.Down, -1, 0),
        (Direction.Left, 0, -1),
        (Direction.Right, 0, 1)
    ];

    private readonly record struct Edge(int CellA, Direction DirFromA, int CellB);

    private static readonly Edge[] Edges;
    private static readonly Direction[] InternalDirs = new Direction[CellCount];
    private static readonly int[][] Neighbors = new int[CellCount][];

    /// <summary>Edge indices touching each cell, paired with the direction they leave that cell by.</summary>
    private static readonly (int EdgeIdx, Direction Dir)[][] EdgesOfCell = new (int, Direction)[CellCount][];

    /// <summary>Edge-pattern bitmasks (over <see cref="Edges"/>) that join all nine tiles into one component.</summary>
    private static readonly int[] ConnectedPatterns;

    /// <summary>All edge-pattern bitmasks, used only as a fallback when nothing connected is placeable.</summary>
    private static readonly int[] AllPatterns;

    static VoyagePlannerExact()
    {
        var edges = new List<Edge>();
        var edgesOfCell = new List<(int, Direction)>[CellCount];
        var neighbors = new List<int>[CellCount];
        for (var i = 0; i < CellCount; i++)
        {
            edgesOfCell[i] = [];
            neighbors[i] = [];
        }

        for (var cell = 0; cell < CellCount; cell++)
        {
            var r = cell / GridSize;
            var c = cell % GridSize;
            foreach (var (dir, dr, dc) in Dirs)
            {
                var nr = r + dr;
                var nc = c + dc;
                if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize)
                    continue;

                var other = nr * GridSize + nc;
                InternalDirs[cell] |= dir;
                neighbors[cell].Add(other);

                if (cell >= other)
                    continue; // record each edge once, from its lower-indexed cell

                var idx = edges.Count;
                edges.Add(new Edge(cell, dir, other));
                edgesOfCell[cell].Add((idx, dir));
                edgesOfCell[other].Add((idx, dir.Opposite()));
            }
        }

        Edges = edges.ToArray();
        for (var i = 0; i < CellCount; i++)
        {
            EdgesOfCell[i] = edgesOfCell[i].ToArray();
            Neighbors[i] = neighbors[i].ToArray();
        }

        var all = new List<int>();
        var connected = new List<int>();
        for (var mask = 0; mask < 1 << 12; mask++)
        {
            all.Add(mask);
            if (int.PopCount(mask) >= CellCount - 1 && JoinsAllCells(mask))
                connected.Add(mask);
        }

        // Denser boards first: more connections means more per-connection value and, empirically,
        // better solutions, so the good patterns are reached before any time limit bites.
        connected.Sort((a, b) => int.PopCount(b).CompareTo(int.PopCount(a)));
        all.Sort((a, b) => int.PopCount(b).CompareTo(int.PopCount(a)));
        ConnectedPatterns = connected.ToArray();
        AllPatterns = all.ToArray();
    }

    private static bool JoinsAllCells(int mask)
    {
        Span<int> stack = stackalloc int[CellCount];
        Span<bool> seen = stackalloc bool[CellCount];
        var top = 0;
        stack[top++] = 0;
        seen[0] = true;
        var count = 1;

        while (top > 0)
        {
            var cell = stack[--top];
            foreach (var (edgeIdx, _) in EdgesOfCell[cell])
            {
                if ((mask & (1 << edgeIdx)) == 0)
                    continue;

                var edge = Edges[edgeIdx];
                var other = edge.CellA == cell ? edge.CellB : edge.CellA;
                if (seen[other])
                    continue;

                seen[other] = true;
                count++;
                stack[top++] = other;
            }
        }

        return count == CellCount;
    }

    private volatile bool _cancelled;

    /// <summary>Human-readable explanation of why no board could be built, or null when one was.</summary>
    public string Diagnostics { get; private set; }

    /// <summary>Number of edge patterns that produced a placeable board.</summary>
    public long FeasiblePatterns { get; private set; }

    /// <summary>How many restarts and kicks the search stage got through.</summary>
    public long SearchIterations { get; private set; }

    /// <summary>Board shapes the search stage could restart from.</summary>
    public long ShapeCount { get; private set; }

    /// <summary>
    /// Seconds of search that had elapsed when the best board was last improved. Comparing it with
    /// the time limit says whether the search had settled or was cut off mid-climb.
    /// </summary>
    public double LastImprovementSeconds { get; private set; }

    /// <summary>Seconds the search actually ran.</summary>
    public double ElapsedSeconds { get; private set; }

    /// <summary>When the randomised search stage began, so patience is measured from there.</summary>
    public double SearchStartedSeconds { get; private set; }

    /// <summary>Best score found so far, updated live so a running solve can be watched.</summary>
    public double BestScoreSoFar { get; private set; }

    /// <summary>True when the solve ended because it stopped improving rather than running out of time.</summary>
    public bool Converged { get; private set; }

    /// <summary>
    /// Best board the deterministic phase found, before the randomised search. Pressing Solve twice
    /// on the same board always reaches at least this.
    /// </summary>
    public double DeterministicBest { get; private set; }

    public void Cancel() => _cancelled = true;

    public VoyageSolutionResult Solve(VoyagePuzzle puzzle, VoyagePlannerSettings settings = null)
    {
        settings ??= new VoyagePlannerSettings();
        _cancelled = false;
        Diagnostics = null;
        FeasiblePatterns = 0;

        var pieces = puzzle.AvailablePieces;
        if (pieces.Count < CellCount)
        {
            Diagnostics = $"Only {pieces.Count} charts available, a voyage needs {CellCount}.";
            return new VoyageSolutionResult([], 0, 0);
        }

        var scorer = new VoyageScorer(puzzle);
        var stopwatch = Stopwatch.StartNew();

        var result = Run(puzzle, scorer, settings, stopwatch, ConnectedPatterns, requireConnected: true);
        if (result.Solutions.Count > 0)
            return result;

        stopwatch.Restart();

        // Nothing fully connected fits the pool. Rather than reporting "no solution", fall back to
        // the best board that ignores full connectivity and flag it as invalid, so the user can see
        // what they are missing instead of an empty window.
        var relaxed = Run(puzzle, scorer, settings, stopwatch, AllPatterns, requireConnected: false);
        // The game refuses to launch a board that is not fully connected or not fully filled, so a
        // relaxed board is diagnostic only — it shows what the pool is short of, not a usable plan.
        Diagnostics = relaxed.Solutions.Count > 0
            ? "No fully connected board is possible with these charts, and the game will not launch " +
              "a disconnected one. " + DescribeShapes(pieces) +
              " The board below is shown only to make the gap visible; it cannot be run."
            : "No board could be built from these charts at all. " + DescribeShapes(pieces);

        return relaxed;
    }

    private static string DescribeShapes(List<MapPiece> pieces)
    {
        var byType = pieces.GroupBy(p => p.Type)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} x{g.Count()}");
        return $"Available shapes: {string.Join(", ", byType)}. " +
               "A connected 3x3 board needs at least 8 matched connections, so mostly Corner/Single pools cannot tile.";
    }

    private VoyageSolutionResult Run(
        VoyagePuzzle puzzle,
        VoyageScorer scorer,
        VoyagePlannerSettings settings,
        Stopwatch stopwatch,
        int[] patterns,
        bool requireConnected)
    {
        var pieces = puzzle.AvailablePieces;
        var pieceCount = pieces.Count;
        var maskCount = scorer.MaskCount;

        var connSensitive = new bool[CellCount];
        var sensitiveCells = new List<int>();
        for (var cell = 0; cell < CellCount; cell++)
        {
            connSensitive[cell] = scorer.IsConnectionSensitive(cell);
            if (connSensitive[cell])
                sensitiveCells.Add(cell);
        }

        // Which rotation of each piece produces which connection set — resolved once up front.
        var rotations = new (int Rotation, Direction Connections)[pieceCount][];
        for (var i = 0; i < pieceCount; i++)
        {
            var piece = pieces[i];
            var opts = new (int, Direction)[piece.DistinctRotations];
            for (var rot = 0; rot < piece.DistinctRotations; rot++)
                opts[rot] = (rot, piece.GetConnections(rot));
            rotations[i] = opts;
        }

        var locked = puzzle.LockedPlacements?.ToDictionary(lp => lp.Row * GridSize + lp.Col)
                     ?? new Dictionary<int, LockedPlacement>();

        var cost = new double[CellCount, pieceCount];
        var chosenRotation = new int[CellCount, pieceCount];
        var assignment = new int[CellCount];
        var required = new Direction[CellCount];
        var conn = new int[CellCount];
        var s = new double[maskCount];
        var connOptions = new int[CellCount][];

        var best = new List<VoyageSolution>();
        var shapes = new List<FeasibleShape>();
        long evaluated = 0;
        long skipped = 0;

        foreach (var pattern in patterns)
        {
            if (_cancelled)
                break;
            if (settings.TimeLimitSeconds is > 0 && stopwatch.Elapsed.TotalSeconds >= settings.TimeLimitSeconds.Value)
                break;

            for (var cell = 0; cell < CellCount; cell++)
            {
                Direction req = 0;
                foreach (var (edgeIdx, dir) in EdgesOfCell[cell])
                {
                    if ((pattern & (1 << edgeIdx)) != 0)
                        req |= dir;
                }

                required[cell] = req;

                // A tile's multipliers only react to the connection *count*, so connection-sensitive
                // tiles enumerate counts from "internal connections only" up to "plus every stub
                // that can dangle off the board edge". Everything else is pinned to its internal count.
                var internalConn = req.CountConnections();
                if (connSensitive[cell])
                {
                    var freeStubs = (InternalDirs[cell] ^ Direction.All).CountConnections();
                    var opts = new int[freeStubs + 1];
                    for (var k = 0; k <= freeStubs; k++)
                        opts[k] = internalConn + k;
                    connOptions[cell] = opts;
                }
                else
                {
                    connOptions[cell] = [internalConn];
                }
            }

            var profileCount = 1L;
            foreach (var cell in sensitiveCells)
                profileCount *= connOptions[cell].Length;

            for (long profile = 0; profile < profileCount; profile++)
            {
                if (_cancelled)
                    break;

                for (var cell = 0; cell < CellCount; cell++)
                    conn[cell] = connOptions[cell][0];

                var rest = profile;
                foreach (var cell in sensitiveCells)
                {
                    var opts = connOptions[cell];
                    conn[cell] = opts[(int)(rest % opts.Length)];
                    rest /= opts.Length;
                }

                for (var mi = 0; mi < maskCount; mi++)
                {
                    double sum = 0;
                    for (var cell = 0; cell < CellCount; cell++)
                        sum += scorer.TileMultiplier(cell, mi, conn[cell]);
                    s[mi] = sum;
                }

                // A chart's own quantity scales what its neighbours put in its area, so the value of
                // a placement depends on its neighbours and the assignment is no longer linear.
                // Iterate: solve the linear part, read the resulting multipliers back, solve again.
                var rawIncoming = new double[CellCount][];
                for (var cell = 0; cell < CellCount; cell++)
                    rawIncoming[cell] = new double[maskCount];

                var voyageMult = new double[maskCount];

                MapPiecePlacement[,] bestGrid = null;
                var bestScore = double.NegativeInfinity;
                var infeasible = false;

                foreach (var seed in IncomingSeeds)
                {
                    for (var cell = 0; cell < CellCount; cell++)
                        Array.Fill(rawIncoming[cell], seed);

                    Array.Fill(voyageMult, 1.0);

                    for (var iteration = 0; iteration < RefinementPasses; iteration++)
                    {
                        var feasible = BuildCostMatrix(
                            scorer, pieces, rotations, locked, required, conn, connSensitive, s,
                            rawIncoming, voyageMult, cost, chosenRotation);

                        if (!feasible)
                        {
                            infeasible = true;
                            break;
                        }

                        var total = Hungarian.Solve(cost, CellCount, pieceCount, assignment);
                        if (double.IsPositiveInfinity(total))
                        {
                            infeasible = true;
                            break;
                        }

                        var grid = new MapPiecePlacement[GridSize, GridSize];
                        for (var cell = 0; cell < CellCount; cell++)
                        {
                            var pieceIdx = assignment[cell];
                            var piece = pieces[pieceIdx];
                            var rot = chosenRotation[cell, pieceIdx];
                            grid[cell / GridSize, cell % GridSize] =
                                new MapPiecePlacement(piece, rot, piece.GetConnections(rot));
                        }

                        // Score through the shared scorer rather than trusting the assignment total:
                        // the linearised objective is only an approximation, the scorer is the truth.
                        var score = scorer.Score(grid);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestGrid = grid;
                        }

                        UpdateEstimates(scorer, pieces, assignment, conn, maskCount, rawIncoming, voyageMult);
                    }

                    if (infeasible)
                        break;
                }

                if (infeasible || bestGrid == null)
                {
                    skipped++;
                    continue;
                }

                evaluated++;
                FeasiblePatterns++;
                best.Add(new VoyageSolution(bestGrid, bestScore, requireConnected));
                var shapeConn = new int[CellCount];
                for (var cell = 0; cell < CellCount; cell++)
                    shapeConn[cell] = connSensitive[cell] ? conn[cell] : -1;

                shapes.Add(new FeasibleShape((Direction[])required.Clone(), shapeConn));
            }
        }

        // The linearised assignment reliably finds strong boards but not the best one, because a
        // chart's own quantity makes the value of a placement depend on its neighbours. Searching
        // from many of those boards with the real scorer closes the gap; whatever time is left then
        // goes into kicking the leader out of its local optimum and searching again.
        var candidates = best
            .OrderByDescending(x => x.TotalScore)
            .DistinctBy(GridSignature)
            .Take(PolishCount)
            .ToList();

        var polishRandom = new Random(20260731);
        // Deterministic phase: polish everything, interrupted only by the hard backstop. This is
        // the reproducible floor the answer never drops below.
        var polished = new List<VoyageSolution>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (_cancelled || OutOfTime(settings, stopwatch))
            {
                polished.Add(candidate);
                continue;
            }

            polished.Add(Polish(scorer, pieces, candidate, requireConnected, polishRandom));
        }

        polished = polished.OrderByDescending(x => x.TotalScore).DistinctBy(GridSignature).ToList();
        DeterministicBest = polished.Count > 0 ? polished[0].TotalScore : 0;
        BestScoreSoFar = DeterministicBest;

        polished = polished.OrderByDescending(x => x.TotalScore).DistinctBy(GridSignature).ToList();
        ShapeCount = shapes.Count;
        if (polished.Count > 0)
            polished = IteratedSearch(scorer, pieces, rotations, shapes, polished, requireConnected, settings, stopwatch);

        ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        Converged = !OutOfTime(settings, stopwatch) && !_cancelled;
        if (polished.Count > 0)
            BestScoreSoFar = polished[0].TotalScore;

        var top = polished
            .OrderByDescending(x => x.TotalScore)
            .DistinctBy(GridSignature)
            .Take(Math.Max(1, settings.TopN))
            .ToList();

        return new VoyageSolutionResult(top, evaluated, skipped);
    }

    /// <summary>
    /// Recomputes, from a solved assignment, the two quantities the linearisation needs: the raw
    /// value arriving on each tile before any self multiplier, and the voyage-wide multiplier of
    /// the charts that ended up placed.
    /// </summary>
    private static void UpdateEstimates(
        VoyageScorer scorer,
        List<MapPiece> pieces,
        int[] assignment,
        int[] conn,
        int maskCount,
        double[][] rawIncoming,
        double[] voyageMult)
    {
        for (var mi = 0; mi < maskCount; mi++)
            voyageMult[mi] = scorer.VoyageMultiplierFor(assignment, mi);

        for (var cell = 0; cell < CellCount; cell++)
            Array.Clear(rawIncoming[cell]);

        for (var cell = 0; cell < CellCount; cell++)
        {
            foreach (var neighbour in Neighbors[cell])
            {
                foreach (var e in scorer.LocalMods(assignment[neighbour]))
                {
                    rawIncoming[cell][e.MaskIdx] += e.Weight
                                                    * scorer.ChartMultiplier(neighbour, e.MaskIdx, conn[neighbour])
                                                    * scorer.TileMultiplier(cell, e.MaskIdx, conn[cell]);
                }
            }

            for (var source = 0; source < CellCount; source++)
            {
                foreach (var e in scorer.GlobalMods(assignment[source]))
                {
                    rawIncoming[cell][e.MaskIdx] += e.Weight
                                                    * scorer.ChartMultiplier(source, e.MaskIdx, conn[source])
                                                    * scorer.TileMultiplier(cell, e.MaskIdx, conn[cell]);
                }
            }
        }
    }

    private static bool BuildCostMatrix(
        VoyageScorer scorer,
        List<MapPiece> pieces,
        (int Rotation, Direction Connections)[][] rotations,
        Dictionary<int, LockedPlacement> locked,
        Direction[] required,
        int[] conn,
        bool[] connSensitive,
        double[] s,
        double[][] rawIncoming,
        double[] voyageMult,
        double[,] cost,
        int[,] chosenRotation)
    {
        for (var cell = 0; cell < CellCount; cell++)
        {
            var anyEligible = false;
            var lockedHere = locked.GetValueOrDefault(cell);

            for (var pieceIdx = 0; pieceIdx < pieces.Count; pieceIdx++)
            {
                var piece = pieces[pieceIdx];
                cost[cell, pieceIdx] = Hungarian.Forbidden;
                chosenRotation[cell, pieceIdx] = 0;

                if (lockedHere != null && piece.Id != lockedHere.PieceId)
                    continue;

                var rotation = -1;
                foreach (var (rot, conns) in rotations[pieceIdx])
                {
                    if (lockedHere != null && rot != lockedHere.Rotation)
                        continue;
                    if ((conns & InternalDirs[cell]) != required[cell])
                        continue;
                    if (connSensitive[cell] && conns.CountConnections() != conn[cell])
                        continue;

                    rotation = rot;
                    break;
                }

                if (rotation < 0)
                    continue;

                anyEligible = true;
                chosenRotation[cell, pieceIdx] = rotation;

                // What this chart delivers elsewhere, before the receiving tiles scale it. The
                // receiving side is accounted for once, by the chart sitting there.
                double value = 0;
                foreach (var neighbor in Neighbors[cell])
                {
                    foreach (var e in scorer.LocalMods(pieceIdx))
                    {
                        value += e.Weight
                                 * scorer.ChartMultiplier(cell, e.MaskIdx, conn[cell])
                                 * scorer.TileMultiplier(neighbor, e.MaskIdx, conn[neighbor])
                                 * voyageMult[e.MaskIdx];
                    }
                }

                foreach (var e in scorer.GlobalMods(pieceIdx))
                {
                    value += e.Weight
                             * scorer.ChartMultiplier(cell, e.MaskIdx, conn[cell])
                             * s[e.MaskIdx]
                             * voyageMult[e.MaskIdx];
                }

                // The area this chart opens. Both its own multiplier and the tile's borders are
                // known here, so this stays linear in (chart, tile) and needs no estimate.
                foreach (var e in scorer.SelfMods(pieceIdx))
                {
                    value += e.Weight
                             * scorer.TileMultiplier(cell, e.MaskIdx, conn[cell])
                             * scorer.SelfMultiplier(pieceIdx, e.MaskIdx)
                             * voyageMult[e.MaskIdx];
                }

                // What this chart's own quantity, rarity and pack size add to whatever arrives on
                // the tile it takes. Splitting it out this way counts each delivery exactly once.
                var incoming = rawIncoming[cell];
                for (var mi = 0; mi < incoming.Length; mi++)
                {
                    if (incoming[mi] != 0)
                        value += incoming[mi] * (scorer.SelfMultiplier(pieceIdx, mi) - 1) * voyageMult[mi];
                }

                cost[cell, pieceIdx] = -value; // Hungarian minimises, we want the richest board
            }

            if (!anyEligible)
                return false;
        }

        return true;
    }

    /// <summary>The hard backstop, which should normally never be what ends a solve.</summary>
    private static bool OutOfTime(VoyagePlannerSettings settings, Stopwatch stopwatch) =>
        settings.TimeLimitSeconds is > 0 && stopwatch.Elapsed.TotalSeconds >= settings.TimeLimitSeconds.Value;

    /// <summary>
    /// Whether the search has stopped paying for itself: either the backstop hit, or nothing better
    /// has turned up for long enough that continuing is not worth the wait.
    /// </summary>
    private bool ShouldStopSearching(VoyagePlannerSettings settings, Stopwatch stopwatch)
    {
        if (_cancelled || OutOfTime(settings, stopwatch))
            return true;

        if (settings.PatienceSeconds is not > 0)
            return false;

        var since = stopwatch.Elapsed.TotalSeconds - Math.Max(LastImprovementSeconds, SearchStartedSeconds);
        return since >= settings.PatienceSeconds.Value;
    }

    /// <summary>
    /// Spends the remaining time budget escaping local optima: perturb a leading board by forcing a
    /// few charts out of it, search again from there, and keep whatever comes back better. Boards
    /// only ever replace worse ones, so the result cannot regress.
    /// </summary>
    private List<VoyageSolution> IteratedSearch(
        VoyageScorer scorer,
        List<MapPiece> pieces,
        (int Rotation, Direction Connections)[][] rotations,
        List<FeasibleShape> shapes,
        List<VoyageSolution> seeds,
        bool requireConnected,
        VoyagePlannerSettings settings,
        Stopwatch stopwatch)
    {
        // Deterministic: the same board must always produce the same plan.
        var random = new Random(20260731);
        var pool = seeds.ToList();
        var attempt = 0;

        while (!_cancelled && !OutOfTime(settings, stopwatch))
        {
            attempt++;

            SearchIterations++;

            // Alternate between nudging a leading board and starting over from a random assignment
            // of a shape known to fit. The linearised solve lands in a good local optimum that swaps
            // alone cannot leave, so fresh starts are what actually finds better boards.
            VoyageSolution candidate;
            if (attempt % 2 == 0 || shapes.Count == 0)
            {
                var seed = pool[random.Next(Math.Min(8, pool.Count))];
                candidate = Kick(scorer, pieces, seed, random, requireConnected);
            }
            else
            {
                candidate = RandomBoard(scorer, pieces, rotations, shapes[random.Next(shapes.Count)],
                    random, requireConnected);
            }

            if (candidate == null)
                continue;

            var improved = Polish(scorer, pieces, candidate, requireConnected, random);
            if (improved.TotalScore <= pool[^1].TotalScore && pool.Count >= SearchPoolSize)
                continue;

            var previousBest = pool[0].TotalScore;
            pool.Add(improved);
            pool = pool.OrderByDescending(x => x.TotalScore).DistinctBy(GridSignature).ToList();
            if (pool.Count > PolishCount)
                pool.RemoveRange(PolishCount, pool.Count - PolishCount);

            // Only a gain worth waiting for resets the patience clock. The search dribbles out
            // fractions of a point indefinitely, and treating those as progress means it never
            // stops.
            if (pool[0].TotalScore > previousBest * (1 + MeaningfulGain))
            {
                LastImprovementSeconds = stopwatch.Elapsed.TotalSeconds;
                BestScoreSoFar = pool[0].TotalScore;
            }
            else if (pool[0].TotalScore > BestScoreSoFar)
            {
                BestScoreSoFar = pool[0].TotalScore;
            }
        }

        return pool;
    }

    /// <summary>
    /// Builds a random legal board of a known-feasible shape. Returns null if the random choices
    /// paint the board into a corner, which the caller simply retries.
    /// </summary>
    private static VoyageSolution RandomBoard(
        VoyageScorer scorer,
        List<MapPiece> pieces,
        (int Rotation, Direction Connections)[][] rotations,
        FeasibleShape shape,
        Random random,
        bool requireConnected)
    {
        var grid = new MapPiecePlacement[GridSize, GridSize];
        var used = new bool[pieces.Count];

        for (var cell = 0; cell < CellCount; cell++)
        {
            var options = new List<(int Piece, int Rotation)>();
            for (var pieceIdx = 0; pieceIdx < pieces.Count; pieceIdx++)
            {
                if (used[pieceIdx])
                    continue;

                foreach (var (rot, conns) in rotations[pieceIdx])
                {
                    if ((conns & InternalDirs[cell]) != shape.Required[cell])
                        continue;
                    if (shape.Connections[cell] >= 0 && conns.CountConnections() != shape.Connections[cell])
                        continue;

                    options.Add((pieceIdx, rot));
                    break;
                }
            }

            if (options.Count == 0)
                return null;

            var (pick, rotation) = options[random.Next(options.Count)];
            used[pick] = true;
            grid[cell / GridSize, cell % GridSize] =
                new MapPiecePlacement(pieces[pick], rotation, pieces[pick].GetConnections(rotation));
        }

        return new VoyageSolution(grid, scorer.Score(grid), requireConnected);
    }

    /// <summary>
    /// Replaces two random tiles' charts with random unused charts of the same connection shape.
    /// Returns null when the shapes leave nothing to swap in.
    /// </summary>
    private static VoyageSolution Kick(
        VoyageScorer scorer,
        List<MapPiece> pieces,
        VoyageSolution solution,
        Random random,
        bool requireConnected)
    {
        var grid = (MapPiecePlacement[,])solution.Grid.Clone();
        var placed = new HashSet<int>();
        for (var cell = 0; cell < CellCount; cell++)
            placed.Add(grid[cell / GridSize, cell % GridSize].Piece.Id);

        var changed = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var cell = random.Next(CellCount);
            var current = grid[cell / GridSize, cell % GridSize];
            var options = pieces
                .Where(p => !placed.Contains(p.Id) && FindRotation(p, current.Connections) >= 0)
                .ToList();
            if (options.Count == 0)
                continue;

            var pick = options[random.Next(options.Count)];
            placed.Remove(current.Piece.Id);
            placed.Add(pick.Id);
            grid[cell / GridSize, cell % GridSize] =
                new MapPiecePlacement(pick, FindRotation(pick, current.Connections), current.Connections);
            changed = true;
        }

        return changed ? new VoyageSolution(grid, scorer.Score(grid), requireConnected) : null;
    }

    /// <summary>
    /// Improves a board by swapping placed charts with each other and with unused ones, keeping the
    /// board's connection shape intact and judging every candidate with the real scorer. Runs until
    /// nothing improves, which for nine tiles is a handful of passes.
    /// </summary>
    private static VoyageSolution Polish(
        VoyageScorer scorer,
        List<MapPiece> pieces,
        VoyageSolution solution,
        bool requireConnected,
        Random random)
    {
        var grid = (MapPiecePlacement[,])solution.Grid.Clone();
        var score = solution.TotalScore;

        var improved = true;
        while (improved)
        {
            improved = false;

            for (var a = 0; a < CellCount && !improved; a++)
            for (var b = a + 1; b < CellCount && !improved; b++)
            {
                var pa = grid[a / GridSize, a % GridSize];
                var pb = grid[b / GridSize, b % GridSize];
                var rotA = FindRotation(pa.Piece, pb.Connections);
                var rotB = FindRotation(pb.Piece, pa.Connections);
                if (rotA < 0 || rotB < 0)
                    continue;

                grid[a / GridSize, a % GridSize] = new MapPiecePlacement(pb.Piece, rotB, pa.Connections);
                grid[b / GridSize, b % GridSize] = new MapPiecePlacement(pa.Piece, rotA, pb.Connections);
                var candidate = scorer.Score(grid);
                if (candidate > score + 1e-9)
                {
                    score = candidate;
                    improved = true;
                }
                else
                {
                    grid[a / GridSize, a % GridSize] = pa;
                    grid[b / GridSize, b % GridSize] = pb;
                }
            }

            if (improved)
                continue;

            var placed = new HashSet<int>();
            for (var cell = 0; cell < CellCount; cell++)
                placed.Add(grid[cell / GridSize, cell % GridSize].Piece.Id);

            for (var cell = 0; cell < CellCount && !improved; cell++)
            {
                var current = grid[cell / GridSize, cell % GridSize];

                // Scanning every unused chart is what makes a pass strong. Sampling was tried and
                // bought 3x the passes for no gain in score, so thoroughness wins over throughput.
                foreach (var piece in pieces)
                {
                    if (placed.Contains(piece.Id))
                        continue;

                    var rot = FindRotation(piece, current.Connections);
                    if (rot < 0)
                        continue;

                    grid[cell / GridSize, cell % GridSize] = new MapPiecePlacement(piece, rot, current.Connections);
                    var candidate = scorer.Score(grid);
                    if (candidate > score + 1e-9)
                    {
                        score = candidate;
                        improved = true;
                        break;
                    }

                    grid[cell / GridSize, cell % GridSize] = current;
                }
            }
        }

        return score > solution.TotalScore
            ? new VoyageSolution(grid, score, requireConnected)
            : solution;
    }

    private static int FindRotation(MapPiece piece, Direction connections)
    {
        for (var rot = 0; rot < piece.DistinctRotations; rot++)
        {
            if (piece.GetConnections(rot) == connections)
                return rot;
        }

        return -1;
    }

    private static string GridSignature(VoyageSolution solution)
    {
        var parts = new string[CellCount];
        for (var cell = 0; cell < CellCount; cell++)
        {
            var p = solution.Grid[cell / GridSize, cell % GridSize];
            parts[cell] = $"{p.Piece.Id}:{p.Rotation}";
        }

        return string.Join(",", parts);
    }
}
