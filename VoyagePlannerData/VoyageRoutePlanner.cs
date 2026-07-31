using System;
using System.Collections.Generic;

namespace DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

/// <summary>The order to clear a solved board in, and what that ordering is worth.</summary>
/// <param name="Order">Cell indices in first-visit order, starting at the entry tile.</param>
/// <param name="Walk">
/// The rooms actually swum through, including the ones re-entered to reach a later room. The visit
/// order alone reads as teleporting between unconnected rooms.
/// </param>
/// <param name="RoutedValue">Total value once Golden Lantern bonuses picked up earlier are applied.</param>
/// <param name="UnroutedValue">Total value with no lantern bonus at all — the floor.</param>
/// <param name="WorstRoutedValue">Value of the worst legal order, for showing what the routing is saving.</param>
public record VoyageRoute(
    IReadOnlyList<int> Order,
    IReadOnlyList<int> Walk,
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
    /// <param name="tileLanternCount">
    /// How many Golden Lantern groups stand in each room. This has to be a count, not a share of
    /// the room's score: score already carries every border and quantity multiplier on the board,
    /// so feeding it back in makes the bonus grow with the board's own richness and compound into
    /// numbers the game never produces.
    /// </param>
    /// <param name="bonusPerLantern">
    /// How much everything found later is raised per lantern group collected. The game does not
    /// expose this, so it is a setting.
    /// </param>
    /// <param name="entryCell">Room the voyage starts in.</param>
    public static VoyageRoute Plan(
        MapPiecePlacement[,] grid,
        double[] tileValue,
        double[] tileLanternCount,
        double bonusPerLantern,
        int entryCell = EntryCell)
    {
        var adjacency = BuildAdjacency(grid);

        // Lantern value accumulated by each visited set — the bonus depends on the set alone.
        var lanternByMask = new double[1 << CellCount];
        for (var mask = 1; mask < lanternByMask.Length; mask++)
        {
            var lowest = int.TrailingZeroCount(mask);
            lanternByMask[mask] = lanternByMask[mask & (mask - 1)] + tileLanternCount[lowest];
        }

        var best = Optimise(adjacency, tileValue, lanternByMask, bonusPerLantern, entryCell, maximise: true);
        var worst = Optimise(adjacency, tileValue, lanternByMask, bonusPerLantern, entryCell, maximise: false);

        double unrouted = 0;
        foreach (var v in tileValue)
            unrouted += v;

        return new VoyageRoute(best.Order, ExpandToWalk(adjacency, best.Order), best.Value, unrouted, worst.Value);
    }

    /// <summary>
    /// Turns a first-visit order into the rooms actually swum through. A room is entered from
    /// whichever cleared room is nearest it, so reaching one that does not touch the previous room
    /// means backtracking, and the walk shows that instead of implying a jump.
    /// </summary>
    private static IReadOnlyList<int> ExpandToWalk(int[] adjacency, IReadOnlyList<int> order)
    {
        var walk = new List<int> { order[0] };
        var visited = 1 << order[0];

        for (var step = 1; step < order.Count; step++)
        {
            var target = order[step];
            var path = ShortestPath(adjacency, walk[^1], target, visited | (1 << target));
            for (var i = 1; i < path.Count; i++)
                walk.Add(path[i]);

            visited |= 1 << target;
        }

        return walk;
    }

    /// <summary>Fewest rooms from one room to another, moving only through rooms in <paramref name="allowed"/>.</summary>
    private static List<int> ShortestPath(int[] adjacency, int from, int to, int allowed)
    {
        var previous = new int[CellCount];
        Array.Fill(previous, -1);
        previous[from] = from;

        var queue = new Queue<int>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            if (cell == to)
                break;

            for (var next = 0; next < CellCount; next++)
            {
                if ((adjacency[cell] & (1 << next)) == 0 || (allowed & (1 << next)) == 0 || previous[next] >= 0)
                    continue;

                previous[next] = cell;
                queue.Enqueue(next);
            }
        }

        if (previous[to] < 0)
            return [from, to];

        var path = new List<int>();
        for (var cell = to; cell != from; cell = previous[cell])
            path.Add(cell);

        path.Add(from);
        path.Reverse();
        return path;
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
