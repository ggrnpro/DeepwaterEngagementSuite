using System;
using System.Collections.Generic;
using System.Linq;
using DeepwaterEngagementSuiteGGRN.VoyagePlannerData;
using ExileCore.PoEMemory.Elements;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Clearing-order planning on top of a solved board.
///
/// The board is the voyage map, so a solved board still leaves the question of what order to clear
/// it in. Golden Lanterns are collected, and once collected they raise item quantity and rarity for
/// the rest of the run, so rooms holding them are worth reaching early — and a board that puts them
/// near the bottom-left entry is worth more than an equally-scoring board that buries them.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    private VoyageSolutionResult _routeCacheResult;
    private double _routeCacheBonus = double.NaN;
    private bool _routeCacheRankByRoute;
    private List<VoyageSolution> _rankedSolutions;
    private List<VoyageRoute> _rankedRoutes;
    private int[] _routeStepByTile;

    private double GoldenLanternBonusPerPoint =>
        Settings.VoyageSettings.GoldenLanternBonusPer100.Value / 100.0 / 100.0;

    /// <summary>
    /// Solutions in the order they should be offered, cached until the result or the routing
    /// settings change.
    /// </summary>
    private List<VoyageSolution> RankedSolutions()
    {
        if (_result == null || _uiScorer == null)
            return _result?.Solutions ?? [];

        var bonus = GoldenLanternBonusPerPoint;
        var rank = Settings.VoyageSettings.RankByRoute.Value;
        if (ReferenceEquals(_routeCacheResult, _result) &&
            _routeCacheBonus.Equals(bonus) &&
            _routeCacheRankByRoute == rank &&
            _rankedSolutions != null)
        {
            return _rankedSolutions;
        }

        var pairs = new List<(VoyageSolution Solution, VoyageRoute Route)>();
        foreach (var solution in _result.Solutions)
        {
            try
            {
                pairs.Add((solution, PlanRoute(solution, bonus)));
            }
            catch
            {
                pairs.Add((solution, null));
            }
        }

        if (rank)
            pairs = pairs.OrderByDescending(p => p.Route?.RoutedValue ?? p.Solution.TotalScore).ToList();

        _rankedSolutions = pairs.Select(p => p.Solution).ToList();
        _rankedRoutes = pairs.Select(p => p.Route).ToList();
        _routeCacheResult = _result;
        _routeCacheBonus = bonus;
        _routeCacheRankByRoute = rank;
        return _rankedSolutions;
    }

    private double? RoutedValueAt(int index)
    {
        if (_rankedRoutes == null || index < 0 || index >= _rankedRoutes.Count)
            return null;

        return _rankedRoutes[index]?.RoutedValue;
    }

    private VoyageRoute RouteForSelected()
    {
        RankedSolutions();
        if (_rankedRoutes == null || _selectedSolutionIndex < 0 || _selectedSolutionIndex >= _rankedRoutes.Count)
            return null;

        return _rankedRoutes[_selectedSolutionIndex];
    }

    private VoyageRoute PlanRoute(VoyageSolution solution, double bonusPerPoint)
    {
        var cellScores = _uiScorer.CellScores(solution.Grid);
        var tileValue = new double[9];
        for (var i = 0; i < 9; i++)
            tileValue[i] = cellScores[i / 3, i % 3];

        return VoyageRoutePlanner.Plan(solution.Grid, tileValue, LanternValues(solution), bonusPerPoint);
    }

    /// <summary>
    /// Per-tile Golden Lantern value: the part of a room's score that carries the Lanterns tag, i.e.
    /// the lanterns that will actually be standing in that room.
    /// </summary>
    private double[] LanternValues(VoyageSolution solution)
    {
        var explanation = _uiScorer.Explain(solution.Grid);
        var result = new double[9];
        for (var i = 0; i < 9; i++)
        {
            double sum = 0;
            foreach (var row in explanation[i / 3, i % 3])
            {
                if ((row.Tags & ModifierTag.Lanterns) != 0)
                    sum += row.Value;
            }

            result[i] = sum;
        }

        return result;
    }

    private void RefreshRouteSteps()
    {
        var route = RouteForSelected();
        if (route == null)
        {
            _routeStepByTile = null;
            return;
        }

        var steps = new int[9];
        for (var i = 0; i < route.Order.Count; i++)
            steps[route.Order[i]] = i + 1;

        _routeStepByTile = steps;
    }

    /// <summary>Draws the clearing order on the board itself, so it is readable without the window.</summary>
    private void DrawRouteOverlay(List<VoyageTileElement> tiles)
    {
        if (!Settings.VoyageSettings.ShowRoute.Value || _routeStepByTile == null)
            return;

        for (var index = 0; index < tiles.Count && index < 9; index++)
        {
            var step = _routeStepByTile[index];
            if (step <= 0)
                continue;

            var rect = tiles[index].GetClientRectCache;
            var pos = new Vector2(rect.Right - 18, rect.Top + 4);
            Graphics.DrawTextWithBackground($"#{step}", pos,
                step == 1 ? Color.Lime : Color.Cyan, FontAlign.Center, Color.Black);
        }
    }

    private void DrawRoutePanel()
    {
        var route = RouteForSelected();
        if (route == null)
            return;

        ImGui.Spacing();
        var order = string.Join(" -> ", route.Order.Select(c => $"({c / 3},{c % 3})"));
        ImGui.Text($"Clear order: {order}");

        var gainOverWorst = route.RoutedValue - route.WorstRoutedValue;
        ImGui.Text($"Routed value: {route.RoutedValue:F2}  (no lanterns {route.UnroutedValue:F2}, worst order {route.WorstRoutedValue:F2})");
        if (gainOverWorst > 0.005)
        {
            ImGui.TextColored(Color.Lime.ToImguiVec4(),
                $"Clearing in this order is worth +{gainOverWorst:F2} over the worst legal order.");
        }
        else
        {
            ImGui.TextDisabled("No Golden Lantern value on this board — any legal order pays the same.");
        }

        ImGui.TextDisabled("Entry is the bottom-left room (0,0). Lantern bonus is an estimate; tune it in settings.");
    }
}
