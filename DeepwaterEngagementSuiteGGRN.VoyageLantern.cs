using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore.Shared.Enums;
using Color = SharpDX.Color;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Plans the order Golden Lanterns get picked up in.
///
/// A Golden Lantern raises item quantity and rarity for the rest of the voyage once collected, so
/// the first one taken is worth more than the last and every lantern is worth more the earlier it
/// is reached. The board planner already prefers layouts that put lantern rooms near the entry;
/// this is the other half, inside the area.
///
/// Trails already draw a line to every marker, which answers "where is one" but not "which first".
/// With rarely more than a dozen lanterns in play the shortest route through all of them can be
/// solved exactly, so the answer is a numbered chain rather than a nearest-thing arrow.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    /// <summary>Above this many lanterns the exact route is replaced by nearest-neighbour.</summary>
    private const int ExactRouteLimit = 12;

    private void DrawLanternRoute()
    {
        var settings = Settings.VoyageSettings;
        if (!settings.ShowLanternRoute.Value)
            return;

        var lanterns = _cachedEntities.Values
            .Where(x => !x.IsOpened && GetChestType(x.Path) == IconPickerIndex.GoldenLanternEncounter)
            .Select(x => x.GridPos)
            .ToList();

        if (lanterns.Count == 0)
            return;

        var maxDistance = settings.LanternRouteMaxDistance.Value;
        if (maxDistance > 0)
        {
            lanterns = lanterns
                .Where(x => Vector2.Distance(x, _playerGridPos) <= maxDistance)
                .ToList();
            if (lanterns.Count == 0)
                return;
        }

        var order = PlanPickupOrder(_playerGridPos, lanterns);
        var color = settings.LanternRouteColor.Value;
        var width = settings.LanternRouteWidth.Value;
        var previous = _playerGridPos;
        for (var step = 0; step < order.Count; step++)
        {
            var target = lanterns[order[step]];

            // Fade later legs: the first lantern is the one that matters, the rest are context.
            var legColor = step == 0
                ? color
                : new Color(color.R, color.G, color.B, (byte)Math.Max(60, color.A - step * 45));

            var label = step == 0
                ? $"1  Golden Lantern {Vector2.Distance(_playerGridPos, target):F0}"
                : $"{step + 1}";
            DrawGuideLine(previous, target, legColor, width, label);

            previous = target;
        }
    }

    /// <summary>
    /// Shortest route visiting every lantern once, starting at the player and free to end anywhere.
    /// Exact by Held-Karp while the count is small, nearest-neighbour beyond that.
    /// </summary>
    private static List<int> PlanPickupOrder(Vector2 start, List<Vector2> targets)
    {
        var count = targets.Count;
        if (count == 1)
            return [0];

        var fromStart = new double[count];
        var between = new double[count, count];
        for (var i = 0; i < count; i++)
        {
            fromStart[i] = Vector2.Distance(start, targets[i]);
            for (var j = 0; j < count; j++)
                between[i, j] = Vector2.Distance(targets[i], targets[j]);
        }

        return count <= ExactRouteLimit
            ? ExactRoute(count, fromStart, between)
            : NearestNeighbourRoute(count, fromStart, between);
    }

    private static List<int> ExactRoute(int count, double[] fromStart, double[,] between)
    {
        var size = 1 << count;
        var cost = new double[size, count];
        var previous = new int[size, count];
        for (var mask = 0; mask < size; mask++)
        for (var last = 0; last < count; last++)
        {
            cost[mask, last] = double.PositiveInfinity;
            previous[mask, last] = -1;
        }

        for (var i = 0; i < count; i++)
            cost[1 << i, i] = fromStart[i];

        for (var mask = 1; mask < size; mask++)
        {
            for (var last = 0; last < count; last++)
            {
                if ((mask & (1 << last)) == 0 || double.IsInfinity(cost[mask, last]))
                    continue;

                for (var next = 0; next < count; next++)
                {
                    var bit = 1 << next;
                    if ((mask & bit) != 0)
                        continue;

                    var candidate = cost[mask, last] + between[last, next];
                    if (candidate >= cost[mask | bit, next])
                        continue;

                    cost[mask | bit, next] = candidate;
                    previous[mask | bit, next] = last;
                }
            }
        }

        var full = size - 1;
        var best = 0;
        for (var last = 1; last < count; last++)
        {
            if (cost[full, last] < cost[full, best])
                best = last;
        }

        var order = new List<int>(count);
        var current = best;
        var currentMask = full;
        while (current >= 0)
        {
            order.Add(current);
            var step = previous[currentMask, current];
            currentMask &= ~(1 << current);
            current = step;
        }

        order.Reverse();
        return order;
    }

    private static List<int> NearestNeighbourRoute(int count, double[] fromStart, double[,] between)
    {
        var visited = new bool[count];
        var order = new List<int>(count);
        var current = -1;

        for (var step = 0; step < count; step++)
        {
            var best = -1;
            var bestDistance = double.PositiveInfinity;
            for (var i = 0; i < count; i++)
            {
                if (visited[i])
                    continue;

                var distance = current < 0 ? fromStart[i] : between[current, i];
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            if (best < 0)
                break;

            visited[best] = true;
            order.Add(best);
            current = best;
        }

        return order;
    }
}
