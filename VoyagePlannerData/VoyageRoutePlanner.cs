using System;
using System.Collections.Generic;

namespace DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

/// <summary>The order to clear a solved board in, and what that ordering is worth.</summary>
/// <param name="Order">Cell indices in visit order, starting at the entry tile.</param>
/// <param name="RoutedValue">Total value once Golden Lantern bonuses picked up earlier are applied.</param>
/// <param name="UnroutedValue">Total value with no lantern bonus at all — the floor.</param>
/// <param name="WorstRoutedValue">Value of the worst legal order, for showing what the routing is saving.</param>
public record VoyageRoute(
    IReadOnlyList<int> Order,
    double RoutedValue,
    double UnroutedValue,
    double WorstRoutedValue);

/// <summary>
/// Orders the nine rooms of a solved board.
///
/// The board is the voyage map: rooms are joined by the connections the charts were matched on, and
/// you swim between them. Golden Lanterns are picked up, not consumed on the spot — once collected
/// they raise item quantity and rarity for the rest of the voyage. So a room's loot is worth more
/// the more lantern rooms were cleared before it, and the ordering problem is real.
///
/// Cleared rooms stay open, so the only constraint on a visit order is that each newly entered room
/// touches one already visited. The bonus applied to a room therefore depends only on the *set* of
/// rooms cleared before it, never on the order within that set — which makes an exact subset DP
/// possible: 512 masks x 9 rooms, solved instantly.
/// </summary>
public static class VoyageRoutePlanner
{
    private const int GridSize = 3;
    private const int CellCount = GridSize * GridSize;

    // Same convention as the solvers: Up = row+1, Down = row-1. Grid row 0 is the bottom of the board.
    private static readonly (Direction Dir, int Dr, int Dc)[] Dirs =
    [
        (Direction.Up, 1, 0),
        (Direction.Down, -1, 0),
        (Direction.Left, 0, -1),
        (Direction.Right, 0, 1)
    ];

    /// <summary>The voyage always starts in the bottom-left room, which is grid[0, 0].</summary>
    public const int EntryCell = 0;

    /// <summary>
    /// Plans the clearing order.
    /// </summary>
    /// <param name="grid">The solved board.</param>
    /// <param name="tileValue">Per-cell loot value (row-major, cell = row * 3 + col).</param>
    /// <param name="tileLanternValue">
    /// Per-cell Golden Lantern value: the part of that room's score carrying the Lanterns tag.
    /// </param>
    /// <param name="bonusPerLanternValue">
    /// How much one point of lantern value raises everything found afterwards. This is the one
    /// number the model cannot derive, so it is a setting.
    /// </param>
    /// <param name="entryCell">Room the voyage starts in.</param>
    public static VoyageRoute Plan(
        MapPiecePlacement[,] grid,
        double[] tileValue,
        double[] tileLanternValue,
        double bonusPerLanternValue,
        int entryCell = EntryCell)
    {
        var adjacency = BuildAdjacency(grid);

        // Lantern value accumulated by each visited set — the bonus depends on the set alone.
        var lanternByMask = new double[1 << CellCount];
        for (var mask = 1; mask < lanternByMask.Length; mask++)
        {
            var lowest = int.TrailingZeroCount(mask);
            lanternByMask[mask] = lanternByMask[mask & (mask - 1)] + tileLanternValue[lowest];
        }

        var best = Optimise(adjacency, tileValue, lanternByMask, bonusPerLanternValue, entryCell, maximise: true);
        var worst = Optimise(adjacency, tileValue, lanternByMask, bonusPerLanternValue, entryCell, maximise: false);

        double unrouted = 0;
        foreach (var v in tileValue)
            unrouted += v;

        return new VoyageRoute(best.Order, best.Value, unrouted, worst.Value);
    }

    private static (IReadOnlyList<int> Order, double Value) Optimise(
        int[] adjacency,
        double[] tileValue,
        double[] lanternByMask,
        double bonusPerLanternValue,
        int entryCell,
        bool maximise)
    {
        var size = 1 << CellCount;
        var dp = new double[size];
        var from = new int[size];
        var added = new int[size];
        Array.Fill(dp, maximise ? double.NegativeInfinity : double.PositiveInfinity);
        Array.Fill(from, -1);

        // The entry room is cleared before any lantern has been collected, so it gets no bonus.
        var startMask = 1 << entryCell;
        dp[startMask] = tileValue[entryCell];
        added[startMask] = entryCell;

        for (var mask = 0; mask < size; mask++)
        {
            if (double.IsInfinity(dp[mask]))
                continue;

            var bonus = 1 + bonusPerLanternValue * lanternByMask[mask];
            for (var next = 0; next < CellCount; next++)
            {
                var bit = 1 << next;
                if ((mask & bit) != 0)
                    continue;
                if ((adjacency[next] & mask) == 0)
                    continue; // unreachable: no cleared room touches it

                var candidate = dp[mask] + tileValue[next] * bonus;
                var nextMask = mask | bit;
                var better = maximise ? candidate > dp[nextMask] : candidate < dp[nextMask];
                if (!better)
                    continue;

                dp[nextMask] = candidate;
                from[nextMask] = mask;
                added[nextMask] = next;
            }
        }

        var full = size - 1;
        if (double.IsInfinity(dp[full]))
        {
            // Disconnected board: no order reaches every room. Report what is reachable.
            return ([entryCell], tileValue[entryCell]);
        }

        var order = new List<int>(CellCount);
        for (var mask = full; mask != startMask; mask = from[mask])
            order.Add(added[mask]);

        order.Add(entryCell);
        order.Reverse();
        return (order, dp[full]);
    }

    /// <summary>Bitmask of rooms each room is joined to by matched connections.</summary>
    private static int[] BuildAdjacency(MapPiecePlacement[,] grid)
    {
        var adjacency = new int[CellCount];
        for (var cell = 0; cell < CellCount; cell++)
        {
            var r = cell / GridSize;
            var c = cell % GridSize;
            var placement = grid[r, c];
            if (placement == null)
                continue;

            foreach (var (dir, dr, dc) in Dirs)
            {
                if (!placement.Connections.HasFlag(dir))
                    continue;

                var nr = r + dr;
                var nc = c + dc;
                if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize)
                    continue;

                var neighbour = grid[nr, nc];
                if (neighbour == null || !neighbour.Connections.HasFlag(dir.Opposite()))
                    continue;

                adjacency[cell] |= 1 << (nr * GridSize + nc);
            }
        }

        return adjacency;
    }
}
