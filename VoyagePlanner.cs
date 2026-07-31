using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

namespace DeepwaterEngagementSuiteGGRN;

public class VoyagePlanner
{
    private const int GridSize = 3;

    private static readonly (Direction Dir, int Dr, int Dc)[] Directions =
    [
        (Direction.Up, 1, 0),
        (Direction.Down, -1, 0),
        (Direction.Left, 0, -1),
        (Direction.Right, 0, 1)
    ];

    private MapPiecePlacement[,] _grid;
    private bool[] _pieceUsed;
    private double _bestScore;
    private List<VoyageSolution> _topSolutions;
    private long _nodesExplored;
    private long _nodesPruned;
    private Stopwatch _stopwatch;
    private VoyagePuzzle _puzzle;
    private VoyageScorer _scorer;
    private bool _cancelled;
    private int _filledCount;

    // Precomputed: for each piece, all (rotation, connections) pairs.
    private record struct PieceOption(int PieceIdx, int Rotation, Direction Connections);
    private PieceOption[][] _pieceOptionsByGroup;
    private int[] _pieceToGroup;
    private int[] _pieceScanOrder;

    public IEnumerable<VoyageSolutionResult> Solve(VoyagePuzzle puzzle, VoyagePlannerSettings settings = null)
    {
        settings ??= new VoyagePlannerSettings();
        _puzzle = puzzle;
        _grid = new MapPiecePlacement[GridSize, GridSize];
        _pieceUsed = new bool[puzzle.AvailablePieces.Count];
        _bestScore = 0;
        _topSolutions = new List<VoyageSolution>(settings.TopN);
        _nodesExplored = 0;
        _nodesPruned = 0;
        _filledCount = 0;
        _stopwatch = Stopwatch.StartNew();
        _cancelled = false;

        _scorer = new VoyageScorer(puzzle);

        // Group pieces by (Type, BaseConnections, modifier signature) — pieces in the same group
        // are interchangeable for both connectivity and scoring. The signature must include tags,
        // since two pieces with equal weight sums but different tags score differently.
        var groupMap = new Dictionary<(PieceType, Direction, string), int>();
        var groups = new List<List<int>>();
        _pieceToGroup = new int[puzzle.AvailablePieces.Count];

        for (var i = 0; i < puzzle.AvailablePieces.Count; i++)
        {
            var p = puzzle.AvailablePieces[i];
            var key = (p.Type, p.BaseConnections, GetModifierSignature(p));
            if (!groupMap.TryGetValue(key, out var g))
            {
                g = groups.Count;
                groupMap[key] = g;
                groups.Add([]);
            }
            groups[g].Add(i);
            _pieceToGroup[i] = g;
        }

        // Precompute all (rotation, connections) options for each group.
        _pieceOptionsByGroup = new PieceOption[groups.Count][];
        for (var g = 0; g < groups.Count; g++)
        {
            var piece = puzzle.AvailablePieces[groups[g][0]];
            var opts = new List<PieceOption>();
            for (var rot = 0; rot < piece.DistinctRotations; rot++)
            {
                opts.Add(new PieceOption(groups[g][0], rot, piece.GetConnections(rot)));
            }
            _pieceOptionsByGroup[g] = opts.ToArray();
        }

        // Value ordering: try heavier pieces first so good solutions (and thus a high pruning
        // threshold) are found early.
        _pieceScanOrder = Enumerable.Range(0, puzzle.AvailablePieces.Count)
            .OrderByDescending(i => puzzle.AvailablePieces[i].LocalModifier + puzzle.AvailablePieces[i].GlobalModifier)
            .ToArray();

        // Handle locked placements
        var lockedCells = puzzle.LockedPlacements
            .Select(lp => (lp.Row, lp.Col))
            .ToHashSet();
        var lockedAssignments = puzzle.LockedPlacements
            .ToDictionary(
                lp => (lp.Row, lp.Col),
                lp => (puzzle.AvailablePieces.IndexOf(puzzle.AvailablePieces.First(p => p.Id == lp.PieceId)), lp.Rotation));

        // Place locked cells first
        foreach (var (r, c) in lockedCells)
        {
            var (pieceIdx, rotation) = lockedAssignments[(r, c)];
            var piece = puzzle.AvailablePieces[pieceIdx];
            var connections = piece.GetConnections(rotation);
            _grid[r, c] = new MapPiecePlacement(piece, rotation, connections);
            _pieceUsed[pieceIdx] = true;
            _filledCount++;
        }

        var results = Search(settings, lockedCells);

        foreach (var result in results)
        {
            if (_cancelled) yield break;
            yield return result;
        }

        yield return FinalResult();
    }

    public void Cancel() => _cancelled = true;

    /// <summary>
    /// MRV-based backtracking search: at each step, pick the empty cell with the fewest valid
    /// piece options (Minimum Remaining Values). This dramatically reduces the search space
    /// because highly-constrained cells are resolved first, propagating adjacency constraints
    /// to the remaining cells.
    /// </summary>
    private IEnumerable<VoyageSolutionResult> Search(VoyagePlannerSettings settings, HashSet<(int, int)> lockedCells)
    {
        if (_cancelled) yield break;

        if (settings.TimeLimitSeconds.HasValue &&
            _stopwatch.Elapsed.TotalSeconds >= settings.TimeLimitSeconds.Value)
        {
            yield break;
        }

        if (_filledCount == GridSize * GridSize)
        {
            if (IsFullyConnected())
            {
                var score = _scorer.Score(_grid);
                if (score >= _bestScore)
                {
                    if (score > _bestScore)
                    {
                        _bestScore = score;
                        // New best score — clear previous solutions since they're worse
                        _topSolutions.Clear();
                    }

                    var solution = new VoyageSolution(CloneGrid(), score, true);
                    _topSolutions.Insert(0, solution);
                    if (_topSolutions.Count > settings.TopN)
                        _topSolutions.RemoveAt(_topSolutions.Count - 1);

                    if (settings.YieldIntermediate)
                    {
                        yield return new VoyageSolutionResult(
                            new List<VoyageSolution>(_topSolutions),
                            _nodesExplored,
                            _nodesPruned);
                    }
                }
            }

            yield break;
        }

        // Upper-bound prune: only prune if the upper bound is strictly worse than best.
        // Use < (not <=) so equal-scoring subtrees are still explored, allowing TopN to fill.
        if (_scorer.UpperBound(_grid, _pieceUsed, _filledCount) < _bestScore)
        {
            _nodesPruned++;
            yield break;
        }

        // Find the most-constrained empty cell (MRV)
        var bestCell = (-1, -1);
        var bestOptions = new List<(int PieceIdx, int Rotation, Direction Connections)>();
        var bestOptionCount = int.MaxValue;

        for (var r = 0; r < GridSize; r++)
        {
            for (var c = 0; c < GridSize; c++)
            {
                if (_grid[r, c] != null) continue;

                var options = GetValidOptions(r, c);
                if (options.Count < bestOptionCount)
                {
                    bestOptionCount = options.Count;
                    bestCell = (r, c);
                    bestOptions = options;
                    if (bestOptionCount == 0) break;
                    if (bestOptionCount == 1) break;
                }
            }

            if (bestOptionCount == 0) break;
        }

        if (bestOptionCount == 0)
        {
            _nodesPruned++;
            yield break;
        }

        var (br, bc) = bestCell;
        _nodesExplored++;

        foreach (var (pieceIdx, rotation, connections) in bestOptions)
        {
            if (_cancelled) yield break;

            var piece = _puzzle.AvailablePieces[pieceIdx];
            _grid[br, bc] = new MapPiecePlacement(piece, rotation, connections);
            _pieceUsed[pieceIdx] = true;
            _filledCount++;

            if (IsConnectivityFeasible())
            {
                foreach (var result in Search(settings, lockedCells))
                {
                    yield return result;
                }
            }
            else
            {
                _nodesPruned++;
            }

            _pieceUsed[pieceIdx] = false;
            _grid[br, bc] = null;
            _filledCount--;
        }
    }

    /// <summary>
    /// Returns all valid (pieceIdx, rotation, connections) options for cell (r, c), considering
    /// adjacency constraints with already-placed neighbors. Only one piece per interchangeable
    /// group is included (symmetry breaking).
    /// </summary>
    private List<(int PieceIdx, int Rotation, Direction Connections)> GetValidOptions(int r, int c)
    {
        var result = new List<(int, int, Direction)>();
        var triedGroups = new HashSet<int>();

        foreach (var i in _pieceScanOrder)
        {
            if (_pieceUsed[i]) continue;
            var g = _pieceToGroup[i];
            if (!triedGroups.Add(g)) continue;

            foreach (var opt in _pieceOptionsByGroup[g])
            {
                if (CheckAdjacency(r, c, opt.Connections))
                {
                    result.Add((i, opt.Rotation, opt.Connections));
                }
            }
        }

        return result;
    }

    private bool CheckAdjacency(int r, int c, Direction? connections = null)
    {
        var conn = connections ?? _grid[r, c].Connections;

        foreach (var (dir, dr, dc) in Directions)
        {
            var nr = r + dr;
            var nc = c + dc;

            if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize) continue;
            if (_grid[nr, nc] == null) continue;

            var neighborConn = _grid[nr, nc].Connections;
            var hasConnection = conn.HasFlag(dir);
            var neighborHasConnection = neighborConn.HasFlag(dir.Opposite());

            if (hasConnection != neighborHasConnection)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsFullyConnected()
    {
        var visited = new bool[GridSize, GridSize];
        var stack = new Stack<(int R, int C)>();

        // Find first filled cell
        int sr = -1, sc = -1;
        for (var i = 0; i < GridSize && sr == -1; i++)
            for (var j = 0; j < GridSize && sr == -1; j++)
                if (_grid[i, j] != null) { sr = i; sc = j; }

        if (sr == -1) return true;

        stack.Push((sr, sc));
        visited[sr, sc] = true;
        var count = 1;

        while (stack.TryPop(out var pos))
        {
            var (cr, cc) = pos;
            var conn = _grid[cr, cc].Connections;

            foreach (var (dir, dr, dc) in Directions)
            {
                if (!conn.HasFlag(dir)) continue;

                var nr = cr + dr;
                var nc = cc + dc;

                if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize) continue;
                if (visited[nr, nc]) continue;
                if (_grid[nr, nc] == null) continue;

                var neighborConn = _grid[nr, nc].Connections;
                if (!neighborConn.HasFlag(dir.Opposite())) continue;

                visited[nr, nc] = true;
                count++;
                if (count == GridSize * GridSize) return true;
                stack.Push((nr, nc));
            }
        }

        return count == GridSize * GridSize;
    }

    private bool IsConnectivityFeasible()
    {
        if (_filledCount <= 1) return true;
        if (_filledCount == GridSize * GridSize) return IsFullyConnected();

        var components = CountConnectedComponents();
        if (components <= 1) return true;

        var emptyCells = GridSize * GridSize - _filledCount;

        // Each unused piece can reduce component count by at most (maxConn - 1).
        var mergeCapacities = new List<int>();
        for (var i = 0; i < _pieceUsed.Length; i++)
        {
            if (_pieceUsed[i]) continue;
            var maxConn = _pieceOptionsByGroup[_pieceToGroup[i]]
                .Max(o => o.Connections.CountConnections());
            mergeCapacities.Add(Math.Max(0, maxConn - 1));
        }

        var totalMergeCapacity = mergeCapacities
            .OrderByDescending(x => x)
            .Take(emptyCells)
            .Sum();

        if (totalMergeCapacity < components - 1) return false;

        return true;
    }

    private static string GetModifierSignature(MapPiece piece)
    {
        return string.Join("|", piece.Modifiers
            .OrderBy(m => m.Tags)
            .ThenBy(m => m.IsGlobal)
            .ThenBy(m => m.Weight)
            .Select(m => $"{(int)m.Tags}:{(m.IsGlobal ? 1 : 0)}:{m.Weight:R}"));
    }

    private int CountConnectedComponents()
    {
        var visited = new bool[GridSize, GridSize];
        var components = 0;

        for (var sr = 0; sr < GridSize; sr++)
        {
            for (var sc = 0; sc < GridSize; sc++)
            {
                if (_grid[sr, sc] == null || visited[sr, sc]) continue;

                components++;
                visited[sr, sc] = true;
                var stack = new Stack<(int R, int C)>();
                stack.Push((sr, sc));

                while (stack.TryPop(out var pos))
                {
                    var (cr, cc) = pos;
                    var conn = _grid[cr, cc].Connections;

                    foreach (var (dir, dr, dc) in Directions)
                    {
                        if (!conn.HasFlag(dir)) continue;

                        var nr = cr + dr;
                        var nc = cc + dc;

                        if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize) continue;
                        if (visited[nr, nc] || _grid[nr, nc] == null) continue;

                        var neighborConn = _grid[nr, nc].Connections;
                        if (!neighborConn.HasFlag(dir.Opposite())) continue;

                        visited[nr, nc] = true;
                        stack.Push((nr, nc));
                    }
                }
            }
        }

        return components;
    }

    private MapPiecePlacement[,] CloneGrid()
    {
        var clone = new MapPiecePlacement[GridSize, GridSize];
        for (var i = 0; i < GridSize; i++)
            for (var j = 0; j < GridSize; j++)
                clone[i, j] = _grid[i, j];
        return clone;
    }

    private VoyageSolutionResult FinalResult()
    {
        return new VoyageSolutionResult(
            [.._topSolutions,],
            _nodesExplored,
            _nodesPruned);
    }
}