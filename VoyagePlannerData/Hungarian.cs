using System;

namespace DeepwaterEngagementSuite.VoyagePlannerData;

/// <summary>
/// Rectangular linear assignment (Jonker-Volgenant style shortest augmenting path with potentials).
/// Assigns every one of <c>rows</c> rows to a distinct column so that the total cost is minimal.
/// Runs in O(rows^2 * cols), which for a 9-cell board and a few dozen charts is microseconds.
/// </summary>
public static class Hungarian
{
    /// <summary>Cost used for a forbidden (row, column) pair. Large but finite so the solver stays numerically sane.</summary>
    public const double Forbidden = 1e12;

    private const double Inf = double.MaxValue / 4;

    /// <summary>
    /// Solves the minimisation problem for <paramref name="cost"/> (indexed [row, col]).
    /// </summary>
    /// <param name="cost">Cost matrix; use <see cref="Forbidden"/> for pairs that must not be chosen.</param>
    /// <param name="rows">Number of rows to assign. Must be &lt;= the column count.</param>
    /// <param name="cols">Number of columns available.</param>
    /// <param name="assignment">On return, <c>assignment[row]</c> is the column assigned to that row, or -1.</param>
    /// <returns>Total cost of the assignment, or <see cref="double.PositiveInfinity"/> if no feasible assignment exists.</returns>
    public static double Solve(double[,] cost, int rows, int cols, int[] assignment)
    {
        for (var i = 0; i < rows; i++)
            assignment[i] = -1;

        if (rows == 0)
            return 0;
        if (cols < rows)
            return double.PositiveInfinity;

        // 1-based working arrays, mirroring the classic formulation. Index 0 is the sentinel column.
        var u = new double[rows + 1];
        var v = new double[cols + 1];
        var colRow = new int[cols + 1]; // colRow[j] = row currently matched to column j (0 = none)
        var way = new int[cols + 1];
        var minv = new double[cols + 1];
        var used = new bool[cols + 1];

        for (var i = 1; i <= rows; i++)
        {
            colRow[0] = i;
            var j0 = 0;
            for (var j = 0; j <= cols; j++)
            {
                minv[j] = Inf;
                used[j] = false;
            }

            do
            {
                used[j0] = true;
                var i0 = colRow[j0];
                var delta = Inf;
                var j1 = -1;

                for (var j = 1; j <= cols; j++)
                {
                    if (used[j])
                        continue;

                    var cur = cost[i0 - 1, j - 1] - u[i0] - v[j];
                    if (cur < minv[j])
                    {
                        minv[j] = cur;
                        way[j] = j0;
                    }

                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }

                if (j1 < 0)
                    return double.PositiveInfinity;

                for (var j = 0; j <= cols; j++)
                {
                    if (used[j])
                    {
                        u[colRow[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }

                j0 = j1;
            } while (colRow[j0] != 0);

            do
            {
                var j1 = way[j0];
                colRow[j0] = colRow[j1];
                j0 = j1;
            } while (j0 != 0);
        }

        double total = 0;
        for (var j = 1; j <= cols; j++)
        {
            var row = colRow[j];
            if (row == 0)
                continue;

            var c = cost[row - 1, j - 1];
            if (c >= Forbidden)
                return double.PositiveInfinity;

            assignment[row - 1] = j - 1;
            total += c;
        }

        for (var i = 0; i < rows; i++)
        {
            if (assignment[i] < 0)
                return double.PositiveInfinity;
        }

        return total;
    }
}
