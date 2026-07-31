using System;
using System.Collections.Generic;
using System.Linq;

namespace DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

/// <summary>
/// Tag-aware scoring for voyage puzzles.
///
/// Model:
/// - A chart's local ("Adjacent...") modifiers deliver their weight to each orthogonally adjacent
///   tile; global modifiers deliver their weight to every tile on the board.
/// - A tile-effect border multiplies rewards materializing on its tile, but only rewards whose
///   tags overlap the border's tags (an All-tagged border matches everything, including untagged
///   modifiers). Multiple matching borders compound multiplicatively.
/// - A chart-effect border (AffectsPlacedChart, e.g. "increased effect of adjacent Charts")
///   multiplies all modifiers carried by the chart placed on its tile, wherever their value lands.
/// - A per-connection border scales with the connection count of the piece placed on the affected
///   tile: effective multiplier = 1 + (multiplier - 1) * connections. This is evaluated live per
///   placement, since the connection count depends on the piece/rotation the solver picks.
///
/// All multiplier combinations are precomputed per (tile, tag-mask, connection count), so the hot
/// path is table lookups only. <see cref="UpperBound"/> is admissible with respect to
/// <see cref="Score"/>: every unknown (empty tile, unknown neighbor piece, unknown connection
/// count) is replaced by its maximum possible contribution.
///
/// Instances are not thread-safe (scratch buffers are reused); use one instance per thread.
/// </summary>
public class VoyageScorer
{
    private const int GridSize = 3;
    private const int CellCount = GridSize * GridSize;
    private const int MaxConn = 4;
    private const int MaxSlot = MaxConn + 1; // deliver-table slot for "connection count unknown"

    private static readonly (int Dr, int Dc)[] NeighborOffsets = [(1, 0), (-1, 0), (0, -1), (0, 1)];

    /// <summary>A piece's modifiers of one scope (local/global), collapsed to one entry per tag mask.</summary>
    public readonly record struct ModEntry(int MaskIdx, double Weight);

    private readonly int _maskCount;
    private readonly Dictionary<MapPiece, int> _pieceIndex = new();

    // [cell][maskIdx][connectionsOfPieceOnCell 0..4]
    private readonly double[][][] _tileMult;
    private readonly double[][][] _chartMult;

    // [cell][maskIdx] — max over connection counts 1..4
    private readonly double[][] _tileMultMax;

    // Per piece: distinct (mask, summed weight) entries.
    private readonly ModEntry[][] _localEntries;
    private readonly ModEntry[][] _globalEntries;

    // Per piece: value landing in the chart's own area, which no other channel carries.
    private readonly ModEntry[][] _selfEntries;

    // Per piece: upper bound on total global-mod value if placed anywhere.
    private readonly double[] _pieceGlobalUpperBound;

    // [fromCell][toCell][conn 0..4, 5 = unknown]: max over all pieces of the local value an
    // unknown piece at fromCell could deliver to toCell. Null for non-adjacent pairs.
    private readonly double[][][] _deliverMax;

    private readonly double[] _sScratch;
    private readonly int[] _connScratch = new int[CellCount];
    private readonly List<double> _globalsScratch = [];
    private readonly Dictionary<ModifierTag, int> _maskIndex;
    private readonly IReadOnlyList<BorderEffect>[] _bordersByCell = new IReadOnlyList<BorderEffect>[CellCount];

    // A chart's own Item Quantity / Rarity / Pack Size / Sulphur scale everything that drops in the
    // area it opens, including content its neighbours put there — so unlike a border, this
    // multiplier depends on which chart is placed. [piece][maskIdx].
    private readonly double[][] _selfMult;

    // Per piece: the reward stats it projects over the entire voyage (from its voyage-scope
    // implicit). Summed over the nine placed charts, these scale the whole board.
    private readonly ChartRewardStats[] _voyageStats;

    // Max self multiplier over all pieces, per mask — used for admissible upper bounds.
    private readonly double[] _selfMultMax;

    // Voyage-wide multiplier if every available chart were placed: an over-estimate, and therefore
    // an admissible bound for a board that only holds nine of them.
    private readonly double[] _voyageMultMax;

    // Mask-agnostic versions of the two bounds above, for terms already collapsed over masks.
    private readonly double _selfMultBound;
    private readonly double _voyageMultBound;

    private readonly RewardStatWeights _weights;
    private readonly ModifierTag[] _masks;
    private readonly double[][] _selfScratch = new double[CellCount][];
    private readonly double[] _voyageScratch;

    public VoyageScorer(VoyagePuzzle puzzle)
    {
        var pieces = puzzle.AvailablePieces;
        _weights = puzzle.StatWeights ?? RewardStatWeights.Default;

        // Index the distinct tag masks that appear on any modifier.
        var maskIndex = new Dictionary<ModifierTag, int>();
        foreach (var mod in pieces.SelectMany(p => p.Modifiers.Concat(p.SelfModifiers ?? [])))
        {
            if (!maskIndex.ContainsKey(mod.Tags))
                maskIndex[mod.Tags] = maskIndex.Count;
        }

        if (maskIndex.Count == 0)
            maskIndex[ModifierTag.None] = 0;

        _maskIndex = maskIndex;
        _maskCount = maskIndex.Count;
        _sScratch = new double[_maskCount];
        _voyageScratch = new double[_maskCount];
        var masks = new ModifierTag[_maskCount];
        foreach (var (mask, idx) in maskIndex)
            masks[idx] = mask;

        _masks = masks;

        // Multiplier tables per tile.
        _tileMult = new double[CellCount][][];
        _chartMult = new double[CellCount][][];
        _tileMultMax = new double[CellCount][];
        var chartMultMax = new double[CellCount][];
        for (var cell = 0; cell < CellCount; cell++)
        {
            var borders = puzzle.TileBorders?[cell / GridSize, cell % GridSize] ?? [];
            _bordersByCell[cell] = borders;
            _tileMult[cell] = new double[_maskCount][];
            _chartMult[cell] = new double[_maskCount][];
            _tileMultMax[cell] = new double[_maskCount];
            chartMultMax[cell] = new double[_maskCount];
            for (var mi = 0; mi < _maskCount; mi++)
            {
                var tileByConn = new double[MaxConn + 1];
                var chartByConn = new double[MaxConn + 1];
                for (var n = 0; n <= MaxConn; n++)
                {
                    double tile = 1, chart = 1;
                    foreach (var b in borders)
                    {
                        if (!ModifierTagParser.Matches(b.Tags, masks[mi]))
                            continue;

                        var m = b.PerConnection ? Math.Max(0, 1 + (b.Multiplier - 1) * n) : b.Multiplier;
                        if (b.AffectsPlacedChart)
                            chart *= m;
                        else
                            tile *= m;
                    }

                    tileByConn[n] = tile;
                    chartByConn[n] = chart;
                }

                _tileMult[cell][mi] = tileByConn;
                _chartMult[cell][mi] = chartByConn;
                _tileMultMax[cell][mi] = tileByConn.Skip(1).Max();
                chartMultMax[cell][mi] = chartByConn.Skip(1).Max();
            }
        }

        // Static bounds used for pieces/cells that aren't decided yet.
        var sGlobalMax = new double[_maskCount];
        var chartMaxOverCells = new double[_maskCount];
        for (var mi = 0; mi < _maskCount; mi++)
        {
            for (var cell = 0; cell < CellCount; cell++)
            {
                sGlobalMax[mi] += _tileMultMax[cell][mi];
                chartMaxOverCells[mi] = Math.Max(chartMaxOverCells[mi], chartMultMax[cell][mi]);
            }
        }

        // Per-piece modifier entries, grouped by tag mask.
        _localEntries = new ModEntry[pieces.Count][];
        _globalEntries = new ModEntry[pieces.Count][];
        _selfEntries = new ModEntry[pieces.Count][];
        _pieceGlobalUpperBound = new double[pieces.Count];
        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            _pieceIndex[piece] = i;
            _localEntries[i] = BuildEntries(piece.Modifiers, maskIndex, isGlobal: false);
            _globalEntries[i] = BuildEntries(piece.Modifiers, maskIndex, isGlobal: true);
            _selfEntries[i] = BuildEntries(piece.SelfModifiers ?? [], maskIndex, isGlobal: false);
            _pieceGlobalUpperBound[i] = _globalEntries[i]
                .Sum(e => e.Weight * chartMaxOverCells[e.MaskIdx] * sGlobalMax[e.MaskIdx]);
        }

        // Per-piece self multipliers, one per tag mask.
        _selfMult = new double[pieces.Count][];
        _voyageStats = new ChartRewardStats[pieces.Count];
        _selfMultMax = new double[_maskCount];
        for (var i = 0; i < pieces.Count; i++)
        {
            var row = new double[_maskCount];
            for (var mi = 0; mi < _maskCount; mi++)
            {
                row[mi] = StatMultiplier(pieces[i].SelfStats, masks[mi], _weights);
                _selfMultMax[mi] = Math.Max(_selfMultMax[mi], row[mi]);
            }

            _selfMult[i] = row;
            _voyageStats[i] = pieces[i].VoyageStats;
        }

        var allVoyageStats = default(ChartRewardStats);
        foreach (var stats in _voyageStats)
            allVoyageStats += stats;

        _voyageMultMax = new double[_maskCount];
        for (var mi = 0; mi < _maskCount; mi++)
            _voyageMultMax[mi] = StatMultiplier(allVoyageStats, masks[mi], _weights);

        _selfMultBound = _selfMultMax.Length > 0 ? _selfMultMax.Max() : 1;
        _voyageMultBound = _voyageMultMax.Length > 0 ? _voyageMultMax.Max() : 1;

        // The per-piece global bound was built before the multipliers were known; scale it now so
        // it still dominates what a global modifier can actually be worth.
        for (var i = 0; i < _pieceGlobalUpperBound.Length; i++)
            _pieceGlobalUpperBound[i] *= _selfMultBound * _voyageMultBound;

        // Max local delivery from an unknown piece at `from` into `to`.
        _deliverMax = new double[CellCount][][];
        for (var from = 0; from < CellCount; from++)
        {
            _deliverMax[from] = new double[CellCount][];
            var fr = from / GridSize;
            var fc = from % GridSize;
            foreach (var (dr, dc) in NeighborOffsets)
            {
                var tr = fr + dr;
                var tc = fc + dc;
                if (tr < 0 || tr >= GridSize || tc < 0 || tc >= GridSize)
                    continue;

                var to = tr * GridSize + tc;
                var bySlot = new double[MaxSlot + 1];
                for (var slot = 0; slot <= MaxSlot; slot++)
                {
                    double best = 0;
                    for (var i = 0; i < pieces.Count; i++)
                    {
                        double v = 0;
                        foreach (var e in _localEntries[i])
                        {
                            var tileM = slot == MaxSlot
                                ? _tileMultMax[to][e.MaskIdx]
                                : _tileMult[to][e.MaskIdx][slot];
                            v += e.Weight * chartMultMax[from][e.MaskIdx] * tileM;
                        }

                        best = Math.Max(best, v);
                    }

                    bySlot[slot] = best;
                }

                _deliverMax[from][to] = bySlot;
            }
        }
    }

    /// <summary>
    /// Turns a chart's reward stats into a multiplier for one reward category.
    ///
    /// Item Quantity scales every kind of drop, so it always applies. The rest only touch what they
    /// are about: pack size adds monsters, rarity shifts what monsters and chests drop, sulphur is
    /// its own resource, gold its own currency.
    /// </summary>
    private static double StatMultiplier(ChartRewardStats stats, ModifierTag mask, RewardStatWeights weights)
    {
        if (stats.IsZero)
            return 1;

        var multiplier = 1 + weights.Quantity * stats.Quantity / 100.0;

        if ((mask & (ModifierTag.Monsters | ModifierTag.MagicMonsters | ModifierTag.RareMonsters)) != 0)
            multiplier *= 1 + weights.PackSize * stats.PackSize / 100.0;

        if ((mask & (ModifierTag.Rarity | ModifierTag.Uniques | ModifierTag.Equipment)) != 0)
            multiplier *= 1 + weights.Rarity * stats.Rarity / 100.0;

        if ((mask & ModifierTag.Resources) != 0)
            multiplier *= 1 + weights.Sulphur * stats.Sulphur / 100.0;

        if ((mask & ModifierTag.Gold) != 0)
            multiplier *= 1 + weights.Gold * stats.Gold / 100.0;

        return Math.Max(0, multiplier);
    }

    /// <summary>
    /// Board-wide multiplier per tag mask from the voyage-scope stats of every placed chart. These
    /// apply everywhere, so they depend on which charts are used but not on where they sit.
    /// </summary>
    private void VoyageMultipliers(MapPiecePlacement[,] grid, double[] output)
    {
        var total = default(ChartRewardStats);
        for (var cell = 0; cell < CellCount; cell++)
        {
            var placement = grid[cell / GridSize, cell % GridSize];
            if (placement != null)
                total += _voyageStats[_pieceIndex[placement.Piece]];
        }

        for (var mi = 0; mi < _maskCount; mi++)
            output[mi] = StatMultiplier(total, _masks[mi], _weights);
    }

    private static ModEntry[] BuildEntries(IEnumerable<Modifier> mods, Dictionary<ModifierTag, int> maskIndex, bool isGlobal)
    {
        return mods
            .Where(m => m.IsGlobal == isGlobal && m.Weight != 0)
            .GroupBy(m => maskIndex[m.Tags])
            .Select(g => new ModEntry(g.Key, g.Sum(m => m.Weight)))
            .ToArray();
    }

    /// <summary>Exact score of a completely filled grid.</summary>
    public double Score(MapPiecePlacement[,] grid) => ScoreInternal(grid, null);

    /// <summary>
    /// Exact score of a completely filled grid, broken down per tile. Local rewards are attributed
    /// to the tile they land on; global modifiers to the tile carrying them.
    /// </summary>
    public double[,] CellScores(MapPiecePlacement[,] grid)
    {
        var cells = new double[GridSize, GridSize];
        ScoreInternal(grid, cells);
        return cells;
    }

    private double ScoreInternal(MapPiecePlacement[,] grid, double[,] cellsOut)
    {
        // Scoring is the search's inner loop — hundreds of thousands of calls per solve — so the
        // per-call buffers are reused rather than allocated.
        var conn = _connScratch;
        var self = _selfScratch;
        for (var cell = 0; cell < CellCount; cell++)
        {
            var placement = grid[cell / GridSize, cell % GridSize];
            conn[cell] = placement.Connections.CountConnections();
            self[cell] = _selfMult[_pieceIndex[placement.Piece]];
        }

        var voyage = _voyageScratch;
        VoyageMultipliers(grid, voyage);

        // Sum over the board of each tile's multiplier, per mask — the factor for global mods.
        // A tile's factor now includes the chart standing on it, since a chart's own quantity
        // scales whatever lands there.
        var s = _sScratch;
        for (var mi = 0; mi < _maskCount; mi++)
        {
            double sum = 0;
            for (var cell = 0; cell < CellCount; cell++)
                sum += _tileMult[cell][mi][conn[cell]] * self[cell][mi];
            s[mi] = sum;
        }

        double score = 0;
        for (var cell = 0; cell < CellCount; cell++)
        {
            var r = cell / GridSize;
            var c = cell % GridSize;
            double cellScore = 0;

            foreach (var (dr, dc) in NeighborOffsets)
            {
                var nr = r + dr;
                var nc = c + dc;
                if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize)
                    continue;

                var u = nr * GridSize + nc;
                var pi = _pieceIndex[grid[nr, nc].Piece];
                foreach (var e in _localEntries[pi])
                {
                    cellScore += e.Weight * _chartMult[u][e.MaskIdx][conn[u]]
                                 * _tileMult[cell][e.MaskIdx][conn[cell]] * self[cell][e.MaskIdx]
                                 * voyage[e.MaskIdx];
                }
            }

            var piSelf = _pieceIndex[grid[r, c].Piece];
            foreach (var e in _globalEntries[piSelf])
            {
                cellScore += e.Weight * _chartMult[cell][e.MaskIdx][conn[cell]] * s[e.MaskIdx]
                             * voyage[e.MaskIdx];
            }

            // The chart's own area. Border multipliers on this tile and the chart's own quantity
            // both apply, but the chart-effect borders do not: those boost adjacent charts.
            foreach (var e in _selfEntries[piSelf])
            {
                cellScore += e.Weight * _tileMult[cell][e.MaskIdx][conn[cell]] * self[cell][e.MaskIdx]
                             * voyage[e.MaskIdx];
            }

            score += cellScore;
            if (cellsOut != null)
                cellsOut[r, c] = cellScore;
        }

        return score;
    }

    /// <summary>
    /// Admissible upper bound on the score of any completion of a partially filled grid.
    /// </summary>
    public double UpperBound(MapPiecePlacement[,] grid, bool[] pieceUsed, int filledCount)
    {
        var conn = _connScratch;
        for (var cell = 0; cell < CellCount; cell++)
        {
            var placement = grid[cell / GridSize, cell % GridSize];
            conn[cell] = placement?.Connections.CountConnections() ?? -1;
        }

        // Every unknown is replaced by its best possible value, including the self multiplier of a
        // chart not yet placed and the voyage-wide multiplier of charts not yet chosen, so the
        // bound stays admissible now that those depend on the assignment.
        var s = _sScratch;
        for (var mi = 0; mi < _maskCount; mi++)
        {
            double sum = 0;
            for (var cell = 0; cell < CellCount; cell++)
            {
                sum += (conn[cell] >= 0 ? _tileMult[cell][mi][conn[cell]] : _tileMultMax[cell][mi])
                       * _selfMultMax[mi];
            }

            s[mi] = sum * _voyageMultMax[mi];
        }

        double score = 0;
        for (var cell = 0; cell < CellCount; cell++)
        {
            var r = cell / GridSize;
            var c = cell % GridSize;

            foreach (var (dr, dc) in NeighborOffsets)
            {
                var nr = r + dr;
                var nc = c + dc;
                if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize)
                    continue;

                var u = nr * GridSize + nc;
                if (conn[u] >= 0)
                {
                    var pi = _pieceIndex[grid[nr, nc].Piece];
                    foreach (var e in _localEntries[pi])
                    {
                        var tileM = conn[cell] >= 0
                            ? _tileMult[cell][e.MaskIdx][conn[cell]]
                            : _tileMultMax[cell][e.MaskIdx];
                        score += e.Weight * _chartMult[u][e.MaskIdx][conn[u]] * tileM
                                 * _selfMultMax[e.MaskIdx] * _voyageMultMax[e.MaskIdx];
                    }
                }
                else
                {
                    score += _deliverMax[u][cell][conn[cell] >= 0 ? conn[cell] : MaxSlot]
                             * _selfMultBound * _voyageMultBound;
                }
            }

            if (conn[cell] >= 0)
            {
                var pi = _pieceIndex[grid[r, c].Piece];
                foreach (var e in _globalEntries[pi])
                {
                    score += e.Weight * _chartMult[cell][e.MaskIdx][conn[cell]] * s[e.MaskIdx];
                }
            }
        }

        // Globals of the pieces still to be placed: take the best (9 - filled) unused pieces.
        var remaining = CellCount - filledCount;
        if (remaining > 0)
        {
            _globalsScratch.Clear();
            for (var i = 0; i < pieceUsed.Length; i++)
            {
                if (!pieceUsed[i] && _pieceGlobalUpperBound[i] > 0)
                    _globalsScratch.Add(_pieceGlobalUpperBound[i]);
            }

            _globalsScratch.Sort((a, b) => b.CompareTo(a));
            var take = Math.Min(remaining, _globalsScratch.Count);
            for (var i = 0; i < take; i++)
                score += _globalsScratch[i];
        }

        return score;
    }

    /// <summary>Borders touching the given tile (for display).</summary>
    public IReadOnlyList<BorderEffect> BordersAt(int row, int col) => _bordersByCell[row * GridSize + col];

    // --- Accessors used by the exact (assignment-based) planner --------------------------------
    // The planner fixes the board's connection pattern first, which turns every multiplier below
    // into a constant, so it can reuse these tables instead of duplicating the scoring model.

    /// <summary>Number of distinct tag masks appearing on any chart modifier.</summary>
    public int MaskCount => _maskCount;

    /// <summary>Multiplier applied to rewards landing on <paramref name="cell"/> from tag mask <paramref name="maskIdx"/>.</summary>
    public double TileMultiplier(int cell, int maskIdx, int connections) => _tileMult[cell][maskIdx][connections];

    /// <summary>Multiplier applied to the modifiers carried by the chart placed on <paramref name="cell"/>.</summary>
    public double ChartMultiplier(int cell, int maskIdx, int connections) => _chartMult[cell][maskIdx][connections];

    /// <summary>
    /// Whether any border on this tile is per-connection, i.e. whether the tile's multipliers
    /// actually change with the connection count of the chart placed there.
    /// </summary>
    public bool IsConnectionSensitive(int cell)
    {
        foreach (var b in _bordersByCell[cell])
        {
            if (b.PerConnection)
                return true;
        }

        return false;
    }

    /// <summary>The piece's adjacent-scope modifiers, one entry per tag mask.</summary>
    public IReadOnlyList<ModEntry> LocalMods(int pieceIdx) => _localEntries[pieceIdx];

    /// <summary>The piece's voyage-scope (global) modifiers, one entry per tag mask.</summary>
    public IReadOnlyList<ModEntry> GlobalMods(int pieceIdx) => _globalEntries[pieceIdx];

    /// <summary>Value the piece produces in its own area, one entry per tag mask.</summary>
    public IReadOnlyList<ModEntry> SelfMods(int pieceIdx) => _selfEntries[pieceIdx];

    /// <summary>
    /// How much the chart itself scales rewards landing on its own tile, for one tag mask. This is
    /// what makes placement quadratic: it depends on the chart chosen, not just the tile.
    /// </summary>
    public double SelfMultiplier(int pieceIdx, int maskIdx) => _selfMult[pieceIdx][maskIdx];

    /// <summary>Largest self multiplier any available chart could contribute, per mask.</summary>
    public double MaxSelfMultiplier(int maskIdx) => _selfMultMax[maskIdx];

    /// <summary>Voyage-wide multiplier if every available chart were placed — an admissible bound.</summary>
    public double MaxVoyageMultiplier(int maskIdx) => _voyageMultMax[maskIdx];

    /// <summary>Voyage-wide reward stats the given pieces project over the whole board.</summary>
    public double VoyageMultiplierFor(IEnumerable<int> pieceIndices, int maskIdx)
    {
        var total = default(ChartRewardStats);
        foreach (var index in pieceIndices)
            total += _voyageStats[index];

        return StatMultiplier(total, _masks[maskIdx], _weights);
    }

    /// <summary>Index of the piece within this scorer's tables.</summary>
    public int IndexOf(MapPiece piece) => _pieceIndex[piece];

    /// <summary>
    /// Full per-tile justification of a completed grid's score. For each tile, lists every
    /// contribution landing there: the source modifier, its configured weight, the chart-side
    /// multiplier (chart-effect borders on the source tile), the tile-side multiplier (borders
    /// on the receiving tile) with the individual borders that matched, and the final value.
    /// Global modifiers are attributed to the tile carrying them; their tile factor is the sum
    /// of matching tile multipliers over the whole board. The values of each tile's rows sum to
    /// the corresponding <see cref="CellScores"/> entry.
    /// </summary>
    public List<ScoreContribution>[,] Explain(MapPiecePlacement[,] grid)
    {
        var conn = new int[CellCount];
        var self = new double[CellCount][];
        for (var cell = 0; cell < CellCount; cell++)
        {
            var placement = grid[cell / GridSize, cell % GridSize];
            conn[cell] = placement.Connections.CountConnections();
            self[cell] = _selfMult[_pieceIndex[placement.Piece]];
        }

        var voyage = new double[_maskCount];
        VoyageMultipliers(grid, voyage);

        var s = new double[_maskCount];
        for (var mi = 0; mi < _maskCount; mi++)
        {
            for (var cell = 0; cell < CellCount; cell++)
                s[mi] += _tileMult[cell][mi][conn[cell]] * self[cell][mi];
        }

        var result = new List<ScoreContribution>[GridSize, GridSize];
        for (var cell = 0; cell < CellCount; cell++)
        {
            var r = cell / GridSize;
            var c = cell % GridSize;
            var rows = new List<ScoreContribution>();
            double baseValue = 0, baseWeight = 0;

            foreach (var (dr, dc) in NeighborOffsets)
            {
                var nr = r + dr;
                var nc = c + dc;
                if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize)
                    continue;

                var u = nr * GridSize + nc;
                var piece = grid[nr, nc].Piece;
                foreach (var mod in piece.Modifiers)
                {
                    if (mod.IsGlobal || mod.Weight == 0)
                        continue;

                    var mi = _maskIndex[mod.Tags];
                    var chartM = _chartMult[u][mi][conn[u]];
                    // The receiving tile's factor now folds in the chart standing there and the
                    // voyage-wide stats, so these rows still add up to the tile's score.
                    var tileM = _tileMult[cell][mi][conn[cell]] * self[cell][mi] * voyage[mi];
                    var value = mod.Weight * chartM * tileM;
                    if (mod.Name == "Default")
                    {
                        baseValue += value;
                        baseWeight += mod.Weight;
                        continue;
                    }

                    rows.Add(new ScoreContribution(
                        mod.Name, piece.Id, nr, nc, false, mod.Weight,
                        chartM, MatchedBorders(u, mod.Tags, conn[u], chartSide: true),
                        tileM, MatchedBorders(cell, mod.Tags, conn[cell], chartSide: false),
                        value, mod.Tags));
                }
            }

            var selfPiece = grid[r, c].Piece;
            foreach (var mod in selfPiece.Modifiers)
            {
                if (!mod.IsGlobal || mod.Weight == 0)
                    continue;

                var mi = _maskIndex[mod.Tags];
                var chartM = _chartMult[cell][mi][conn[cell]];
                var globalFactor = s[mi] * voyage[mi];
                rows.Add(new ScoreContribution(
                    mod.Name, selfPiece.Id, r, c, true, mod.Weight,
                    chartM, MatchedBorders(cell, mod.Tags, conn[cell], chartSide: true),
                    globalFactor, [], mod.Weight * chartM * globalFactor, mod.Tags));
            }

            foreach (var mod in selfPiece.SelfModifiers ?? [])
            {
                if (mod.Weight == 0)
                    continue;

                var mi = _maskIndex[mod.Tags];
                var tileM = _tileMult[cell][mi][conn[cell]] * self[cell][mi] * voyage[mi];
                rows.Add(new ScoreContribution(
                    mod.Name, selfPiece.Id, r, c, false, mod.Weight,
                    1, [], tileM, MatchedBorders(cell, mod.Tags, conn[cell], chartSide: false),
                    mod.Weight * tileM, mod.Tags));
            }

            rows.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (baseWeight > 0)
            {
                rows.Add(new ScoreContribution(
                    "Base adjacency", -1, -1, -1, false, baseWeight,
                    1, [], baseValue / baseWeight, [], baseValue, ModifierTag.None));
            }

            result[r, c] = rows;
        }

        return result;
    }

    private List<AppliedBorder> MatchedBorders(int cell, ModifierTag tags, int connections, bool chartSide)
    {
        var list = new List<AppliedBorder>();
        foreach (var b in _bordersByCell[cell])
        {
            if (b.AffectsPlacedChart != chartSide)
                continue;
            if (!ModifierTagParser.Matches(b.Tags, tags))
                continue;

            var m = b.PerConnection ? Math.Max(0, 1 + (b.Multiplier - 1) * connections) : b.Multiplier;
            list.Add(new AppliedBorder(b.Name, m));
        }

        return list;
    }
}

/// <summary>A border that matched a contribution, with its effective multiplier for that placement.</summary>
public record AppliedBorder(string Name, double Multiplier);

/// <summary>
/// One line of a tile's score justification: Value = Weight × ChartMultiplier × TileFactor.
/// For local mods, TileFactor is the receiving tile's border multiplier; for global mods it is
/// the sum of matching tile multipliers over the whole board. The synthetic "Base adjacency" row
/// (SourcePieceId = -1) aggregates the per-neighbor base weight.
/// </summary>
public record ScoreContribution(
    string ModName,
    int SourcePieceId,
    int SourceRow,
    int SourceCol,
    bool IsGlobal,
    double Weight,
    double ChartMultiplier,
    IReadOnlyList<AppliedBorder> ChartBorders,
    double TileFactor,
    IReadOnlyList<AppliedBorder> TileBorders,
    double Value,
    ModifierTag Tags);
