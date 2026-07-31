using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DeepwaterEngagementSuiteGGRN.VoyagePlannerData;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using GameOffsets.Native;
using ImGuiNET;
using SharpDX;
using Direction = DeepwaterEngagementSuiteGGRN.VoyagePlannerData.Direction;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuiteGGRN;

public partial class DeepwaterEngagementSuiteGGRN
{
    private VoyageSolutionResult _result;
    private Task _run;
    private SyncTask<bool> _voyagePlaceTask;
    private VoyagePlanner _voyagePlanner;
    private VoyageScorer _uiScorer;
    private int _selectedSolutionIndex = 0;
    private bool _voyageSolving;
    private bool _voyageTimedOut;
    private long _voyageNodesExplored;
    private long _voyageNodesPruned;
    private double _voyageElapsed;
    private System.Diagnostics.Stopwatch _voyageStopwatch;
    private VoyagePlannerExact _voyageExactPlanner;
    private string _voyageDiagnostics;

    public List<NormalInventoryItem> GetAvailableCharts()
    {
        if (GameController.IngameState.IngameUi.VoyageWindow is { IsValid: true, IsVisible: true } voyageWindow)
        {

            var charts = voyageWindow.AvailableCharts;
            if (!charts.Any())
            {
                return [];
            }
            var filters = Settings.VoyageSettings.IgnoredCharts.Content.Where(x => x.Enabled).Select(x => x.Query).ToList();
            if (!filters.Any())
            {
                return charts;
            }

            var chartSize = charts[0].GetClientRectCache.Size;
            var containerRect = voyageWindow.ChartContainer.GetClientRectCache;
            var containerSize = containerRect.Size;
            var inventorySize = new Vector2i(
                (int)Math.Round(containerSize.Width/chartSize.Width),
                (int)Math.Round(containerSize.Height / chartSize.Height)); //TODO: is this gettable somewhere?
            var filtered = charts.Select(x =>
                {
                    var coord = ((x.GetClientRectCache.TopLeft - containerRect.TopLeft).ToVector2Num()
                                 / new Vector2(containerSize.Width, containerSize.Height)
                                 * inventorySize)
                        .RoundToVector2I();
                    return (x, new ChartData(x.Item, GameController, coord));
                })
                .Where(x => !filters.Any(f => f.Matches(x.Item2)))
                .Select(x => x.x)
                .ToList();
            return filtered;
        }

        return [];
    }

    private static bool TileHasChart(VoyageTileElement tile) =>
        tile?.ItemContainer?.Entity?.GetComponent<DeepwaterChart>() != null;

    private static bool BoardIsClear(VoyageWindow tree) =>
        tree.Tiles.All(t => !TileHasChart(t));

    private static async SyncTask<bool> WiggleCursorToFocus(Vector2 screenPos)
    {
        const float delta = 4f;
        Input.SetCursorPos(screenPos + new Vector2(delta, 0));
        await TaskUtils.NextFrame();
        Input.SetCursorPos(screenPos + new Vector2(-delta, 0));
        await TaskUtils.NextFrame();
        Input.SetCursorPos(screenPos + new Vector2(0, delta));
        await TaskUtils.NextFrame();
        Input.SetCursorPos(screenPos);
        await TaskUtils.NextFrame();
        return true;
    }

    private async SyncTask<bool> PlacePieces(VoyageSolution solution)
    {
        try
        {
            var tree = GameController.IngameState.IngameUi.VoyageWindow;
            var winOrigin = GameController.Window.GetWindowRectangleTimeCache.TopLeft.ToVector2Num();
            var needsFocusWiggle = true;

            if (!BoardIsClear(tree))
            {
                var clearPos = winOrigin + tree.ClearButton.GetClientRectCache.Center.ToVector2Num();
                Input.SetCursorPos(clearPos);
                if (needsFocusWiggle)
                {
                    await WiggleCursorToFocus(clearPos);
                    needsFocusWiggle = false;
                }

                await TaskUtils.CheckEveryFrameWithThrow(
                    () => tree.ClearButton.HasShinyHighlight,
                    () => "Clear button never highlighted (board may already be empty?)",
                    TimeSpan.FromSeconds(2));
                Input.LeftDown();
                await TaskUtils.NextFrame();
                Input.LeftUp();
                await TaskUtils.CheckEveryFrameWithThrow(
                    () => BoardIsClear(tree),
                    () => "Board still has charts after Clear",
                    TimeSpan.FromSeconds(3));
            }

            var availableCharts = GetAvailableCharts();
            for (var i = 0; i < 9; i++)
            {
                var tile = tree.Tiles[i];
                var p = solution.Grid[i / 3, i % 3];
                if (p?.Piece == null)
                    continue;
                if (p.Piece.Id < 0 || p.Piece.Id >= availableCharts.Count)
                {
                    DebugWindow.LogError($"Voyage Place: piece id {p.Piece.Id} out of range ({availableCharts.Count} charts)");
                    continue;
                }

                var pieceElem = availableCharts[p.Piece.Id];
                var click1Pos = winOrigin + pieceElem.GetClientRectCache.Center.ToVector2Num();
                var click2Pos = winOrigin + tile.GetClientRectCache.Center.ToVector2Num();
                Input.SetCursorPos(click1Pos);
                if (needsFocusWiggle)
                {
                    await WiggleCursorToFocus(click1Pos);
                    needsFocusWiggle = false;
                }

                await TaskUtils.CheckEveryFrameWithThrow(
                    () => GameController.IngameState.UIHover?.Address.Equals(pieceElem.Address) ?? false,
                    () => $"Hover address was {GameController.IngameState.UIHover?.Address:X} not {pieceElem.Address:X}",
                    TimeSpan.FromSeconds(1));
                Input.LeftDown();
                await TaskUtils.NextFrame();
                Input.LeftUp();
                await TaskUtils.CheckEveryFrameWithThrow(
                    () => GameController.IngameState.IngameUi.Cursor.Action == MouseActionType.HoldItemForSell,
                    TimeSpan.FromSeconds(1));
                Input.SetCursorPos(click2Pos);
                await TaskUtils.CheckEveryFrameWithThrow(
                    () => GameController.IngameState.UIHoverElement?.Address.Equals(tile.Address) ?? false,
                    () => $"Hover address was {GameController.IngameState.UIHoverElement?.Address:X} not {tile.Address:X}",
                    TimeSpan.FromSeconds(1));
                Input.LeftDown();
                await TaskUtils.NextFrame();
                Input.LeftUp();
                await TaskUtils.CheckEveryFrameWithThrow(
                    () => GameController.IngameState.IngameUi.Cursor.Action == MouseActionType.Free &&
                          TileHasChart(tile),
                    TimeSpan.FromSeconds(1));

                while (tile.ItemContainer?.Entity.GetComponent<DeepwaterChart>()?.Rotation is { } rot &&
                       rot != p.Rotation)
                {
                    DebugWindow.LogMsg($"{rot}, {p.Rotation}");
                    var click3Pos = winOrigin + tile.GetClientRectCache.Center.ToVector2Num();
                    Input.SetCursorPos(click3Pos);
                    await TaskUtils.CheckEveryFrameWithThrow(
                        () => GameController.IngameState.UIHover?.Address.Equals(tile.ItemContainer.Address) ?? false,
                        TimeSpan.FromSeconds(1));
                    Input.RightDown();
                    await TaskUtils.NextFrame();
                    Input.RightUp();
                    await TaskUtils.CheckEveryFrameWithThrow(
                        () => tile.ItemContainer?.Entity?.GetComponent<DeepwaterChart>()?.Rotation is { } rot2 &&
                              rot2 != rot,
                        TimeSpan.FromSeconds(1));
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"Voyage Place failed: {ex.Message}");
            return false;
        }
    }

    private void DrawVoyageHighlights()
    {
        var settings = Settings.VoyageSettings;
        if (!settings.EnableVoyageHandling)
            return;

        if (Input.IsKeyDown(Keys.Escape) && _voyagePlaceTask != null)
        {
            _voyagePlaceTask = null;
        }

        VoyageWindow tree;
        try
        {
            tree = GameController?.IngameState?.IngameUi?.VoyageWindow;
        }
        catch (Exception ex)
        {
            _voyagePlaceTask = null;
            DebugWindow.LogError(ex.ToString());
            return;
        }

        if (tree is not { IsValid: true, IsVisible: true })
        {
            _voyagePlaceTask = null;
            return;
        }

        TaskUtils.RunOrRestart(ref _voyagePlaceTask, () => null);

        TrackRerolls(tree);
        TrackVoyageBoard(tree);
        if (Settings.VoyageSettings.DumpSnapshotHotkey.PressedOnce())
            DumpVoyageSnapshot(tree, "manual");

        var modsPerTileIndex = GetTileMods(tree);

        var tiles = tree.Tiles;
        for (var index = 0; index < tiles.Count; index++)
        {
            var tile = tiles[index];
            var mods = modsPerTileIndex.GetValueOrDefault(index) ?? [];
            var tileTopLeft = tile.GetClientRectCache.TopLeft.ToVector2Num();
            Graphics.DrawTextWithBackground($"({index / 3}, {index % 3})", tileTopLeft, Color.Black);
            var tileCenter = tile.GetClientRectCache.Center.ToVector2Num();
            // Chart name above center
            var chart = tile.ItemContainer?.Entity?.GetComponent<DeepwaterChart>();
            if (chart != null)
            {
                var chartMods = tile.ItemContainer.Entity.GetComponent<Mods>()?.ImplicitMods ?? [];
                var chartModOffset = -10f;
                foreach (var im in chartMods)
                {
                    var chartMod = Settings.VoyageSettings.ChartModifiers.Content
                        .FirstOrDefault(cm => cm.Id.Value.Equals(im.RawName, StringComparison.OrdinalIgnoreCase));
                    var displayName = TrimChartPrefix(im.RawName);
                    var prefix = chartMod?.IsGlobal.Value == true ? "[G] " : "";
                    var weight = chartMod?.Weight.Value ?? 0;
                    var chartName = $"{prefix}{displayName}\n({weight:F1})";
                    var textSize = Graphics.MeasureText(chartName);
                    if (!string.IsNullOrEmpty(chartName))
                    {
                        chartModOffset -= textSize.Y;
                        Graphics.DrawTextWithBackground(chartName, tileCenter + new Vector2(0, chartModOffset),
                            chartMod != null && chartMod.Weight.Value > Settings.VoyageSettings.ChartHighlightThreshold.Value
                                ? chartMod.HighlightColor
                                : Color.White, FontAlign.Center, Color.Black);
                    }
                }
            }
            // Border mods below center
            tileCenter = tileCenter + new Vector2(0, 10);
            foreach (var itemMod in mods)
            {
                var matchingSetting = Settings.VoyageSettings.BorderModifiers.Content.FirstOrDefault(c => c.Id.Value.Equals(itemMod.RawName, StringComparison.OrdinalIgnoreCase));
                var text = matchingSetting?.Abbreviation.Value is { Length: > 0 } abbv
                    ? abbv
                    : itemMod.RawName switch
                    {
                        var r when r.StartsWith("DeepwaterBorder", StringComparison.Ordinal) => r["DeepwaterBorder".Length..],
                        var r => r
                    };
                var size = Graphics.DrawTextWithBackground(text, tileCenter,
                    matchingSetting != null && matchingSetting.ValueMultiplier > Settings.VoyageSettings.BorderHighlightThreshold
                        ? matchingSetting.HighlightColor
                        : Color.Orange, FontAlign.Center, Color.Black);
                tileCenter.Y += size.Y;
            }
        }

        DrawRouteOverlay(tiles);

        var charts = GetAvailableCharts();
        for (int i = 0; i < charts.Count; i++) {
            var pos = charts[i].GetClientRectCache.TopLeft.ToVector2Num();
            var size = Graphics.DrawTextWithBackground($"#{i}", pos, Color.Black);
            var chartMods = charts[i].Entity.GetComponent<Mods>()?.ImplicitMods ?? [];
            
            foreach (var chartMod in chartMods) {
                var chartSettings = Settings.VoyageSettings.ChartModifiers.Content
                    .FirstOrDefault(cm => cm.Id.Value.Equals(chartMod.RawName, StringComparison.OrdinalIgnoreCase));
                if (chartSettings != null && !string.IsNullOrEmpty(chartSettings.Label.Value)) {
                    pos.Y += size.Y;
                    Graphics.DrawTextWithBackground(chartSettings.Label.Value, pos, chartSettings.HighlightColor, Color.Black);
                }
            }
        }


        

        if (settings.ShowOptimizerWindow.Value)
        {
            ShowVoyageOptimizerWindow(tree,tiles);
        }
    }

    private static Dictionary<int, List<ItemMod>> GetTileMods(VoyageWindow tree)
    {
        var borderMods = tree.Data.BorderMods;
        Dictionary<int, List<ItemMod>> modsPerTileIndex = [];
        if (borderMods.Count >= 12)
        {
            modsPerTileIndex = new Dictionary<int, List<int>>
            {
                [0] = [0, 11],
                [1] = [1],
                [2] = [2, 3],
                [3] = [10],
                [4] = [],
                [5] = [4],
                [6] = [8, 9],
                [7] = [7],
                [8] = [5, 6],
            }.ToDictionary(
                x => x.Key,
                x => x.Value.Select(v => borderMods[v])
                    .ToList());
        }

        return modsPerTileIndex;
    }

    private void ShowVoyageOptimizerWindow(VoyageWindow tree, List<VoyageTileElement> tiles)
    {
        if (!ImGui.Begin("Voyage Optimizer"))
        {
            ImGui.End();
            return;
        }

        _voyageSolving = _run is { IsCompleted: false };
        
        if (ImGui.Button("Solve"))
        {
            _voyagePlanner?.Cancel();
            _result = null;
            _selectedSolutionIndex = 0;
            _voyageNodesExplored = 0;
            _voyageNodesPruned = 0;
            _voyageElapsed = 0;
            _voyageTimedOut = false;
            _voyageStopwatch = System.Diagnostics.Stopwatch.StartNew();
            _run = Task.Run(() =>
            {
                var i = 0;
                var pieces = new List<MapPiece>();
                foreach (var chart in GetAvailableCharts())
                {
                    if (chart.Item.TryGetComponent(out DeepwaterChart c))
                    {
                        var rotation = ((Direction)c.Room.Path);
                        var mp = new MapPiece(i,
                            int.PopCount((int)rotation) switch
                            {
                                4 => PieceType.Cross,
                                3 => PieceType.Tee,
                                1 => PieceType.Single,
                                2 => rotation.HasFlag(Direction.Left) == rotation.HasFlag(Direction.Right)
                                    ? PieceType.Straight
                                    : PieceType.Corner
                            }, rotation, [
                                new Modifier("Default", 1), ..chart.Item.GetComponent<Mods>()?.ImplicitMods.Select(im =>
                            {
                                var chartMod = Settings.VoyageSettings.ChartModifiers.Content
                                    .FirstOrDefault(cm => cm.Id.Value.Equals(im.RawName, StringComparison.OrdinalIgnoreCase));
                                var configuredWeight = chartMod?.Weight.Value;
                                return new Modifier(im.RawName, configuredWeight ?? 0, chartMod?.IsGlobal.Value ?? false,
                                    ModifierTagParser.Parse(chartMod?.Tags.Value, ModifierTag.None));
                            }) ?? []
                            ]);
                        pieces.Add(mp);
                    }

                    i++;
                }

                var modsPerTileIndex = GetTileMods(tree);
                var tileBorders = new IReadOnlyList<BorderEffect>[3, 3];
                for (var tileIndex = 0; tileIndex < 9; tileIndex++)
                {
                    var borderMods = modsPerTileIndex.GetValueOrDefault(tileIndex) ?? [];
                    tileBorders[tileIndex / 3, tileIndex % 3] = borderMods.Select(m =>
                    {
                        var setting = Settings.VoyageSettings.BorderModifiers.Content
                            .FirstOrDefault(c => c.Id.Value.Equals(m.RawName, StringComparison.OrdinalIgnoreCase));
                        return new BorderEffect(
                            m.RawName,
                            // Untagged borders match everything (legacy behavior for old profiles).
                            ModifierTagParser.Parse(setting?.Tags.Value, ModifierTag.All),
                            setting?.ValueMultiplier.Value ?? 1,
                            setting?.PerConnection.Value ?? false,
                            setting?.AffectsPlacedChart.Value ?? false);
                    }).ToList();
                }

                var puzzle = new VoyagePuzzle(pieces, tileBorders, []);
                _uiScorer = new VoyageScorer(puzzle);
                var timeLimitSetting = Settings.VoyageSettings.SolverTimeLimitSeconds.Value;

                if (Settings.VoyageSettings.UseAssignmentSolver.Value)
                {
                    _voyagePlanner = null;
                    _voyageExactPlanner = new VoyagePlannerExact();
                    var r = _voyageExactPlanner.Solve(puzzle,
                        new VoyagePlannerSettings(TimeLimitSeconds: timeLimitSetting));
                    _result = r;
                    _voyageNodesExplored = r.NodesExplored;
                    _voyageNodesPruned = r.NodesPruned;
                    _voyageDiagnostics = _voyageExactPlanner.Diagnostics;
                }
                else
                {
                    // fast solver ignores per-connection borders for now; still exact for everything else
                    _voyageExactPlanner = null;
                    _voyageDiagnostics = null;
                    IEnumerable<VoyageSolutionResult> results;
                    if (Settings.VoyageSettings.UseFastSolver.Value)
                    {
                        _voyagePlanner = null;
                        results = new VoyagePlannerFast().Solve(puzzle,
                            new VoyagePlannerSettings(TimeLimitSeconds: timeLimitSetting));
                    }
                    else
                    {
                        _voyagePlanner = new VoyagePlanner();
                        results = _voyagePlanner.Solve(puzzle,
                            new VoyagePlannerSettings(TimeLimitSeconds: timeLimitSetting));
                    }

                    foreach (var r in results)
                    {
                        _result = r;
                        _voyageNodesExplored = r.NodesExplored;
                        _voyageNodesPruned = r.NodesPruned;
                    }
                }

                _voyageElapsed = _voyageStopwatch.Elapsed.TotalSeconds;
                if (_voyageElapsed >= timeLimitSetting)
                    _voyageTimedOut = true;

                _voyageSolving = false;
                LogVoyageSolve(puzzle, timeLimitSetting);
            });
        }

        if ((_voyagePlanner != null || _voyageExactPlanner != null) && _voyageSolving)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _voyagePlanner?.Cancel();
                _voyageExactPlanner?.Cancel();
            }
        }

        if (_voyageSolving)
        {
            if (_voyageStopwatch != null)
                _voyageElapsed = _voyageStopwatch.Elapsed.TotalSeconds;
            ImGui.SameLine();
            var timeLimitSetting = Settings.VoyageSettings.SolverTimeLimitSeconds.Value;
            var progress = timeLimitSetting > 0 ? Math.Min(1f, (float)(_voyageElapsed / timeLimitSetting)) : 0.5f;
            ImGui.ProgressBar(progress, default, $"{_voyageElapsed:F1}s");
        }

        if (_result != null && _result.Solutions.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Place"))
            {
                var ranked = RankedSolutions();
                if (_selectedSolutionIndex >= ranked.Count)
                    _selectedSolutionIndex = 0;
                _voyagePlaceTask = PlacePieces(ranked[_selectedSolutionIndex]);
            }
        }

        ImGui.Spacing();

        if (_voyageSolving || _result != null)
        {
            ImGui.Text($"Nodes: {_voyageNodesExplored:N0} explored, {_voyageNodesPruned:N0} pruned");
        }

        if (_result == null || _result.Solutions.Count == 0)
        {
            if (_voyageSolving)
            {
                ImGui.TextColored(Color.Yellow.ToImguiVec4(), "Searching...");
            }
            else if (_voyageTimedOut)
            {
                ImGui.TextColored(Color.Orange.ToImguiVec4(), "Time limit reached - no valid solution found.");
            }
            else
            {
                ImGui.TextColored(Color.Gray.ToImguiVec4(), "No solutions yet. Press Solve.");
            }

            if (!string.IsNullOrEmpty(_voyageDiagnostics))
            {
                ImGui.TextWrapped(_voyageDiagnostics);
            }

            ImGui.End();
            return;
        }

        if (!string.IsNullOrEmpty(_voyageDiagnostics))
        {
            ImGui.TextColored(Color.Orange.ToImguiVec4(), _voyageDiagnostics);
        }

        if (_voyageTimedOut)
        {
            ImGui.TextColored(Color.Orange.ToImguiVec4(), $"Time limit reached - showing best solutions found so far (may not be optimal).");
        }

        var solutions = RankedSolutions();
        _selectedSolutionIndex = Math.Clamp(_selectedSolutionIndex, 0, solutions.Count - 1);
        var currentSolution = solutions[_selectedSolutionIndex];
        RefreshRouteSteps();

        var asciiArt = BuildAsciiGrid(currentSolution.Grid, tiles);

        using (ImGuiHelpers.UseStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0)))
            foreach (var line in asciiArt)
            {
                ImGui.TextUnformatted(line);
            }

        ImGui.Spacing();

        ImGui.Text($"Score: {currentSolution.TotalScore:F2}");
        ImGui.Text($"Valid: {(currentSolution.IsValid ? "Yes" : "No")}");

        DrawRoutePanel();
        RecordBoardScore(currentSolution.TotalScore);
        DrawRerollPanel(currentSolution.TotalScore);

        if (solutions.Count > 0)
        {
            ImGui.Spacing();
            if (ImGui.BeginTable("SolutionsList", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("#");
                ImGui.TableSetupColumn("Score");
                ImGui.TableSetupColumn("Routed");
                ImGui.TableSetupColumn("Valid");
                ImGui.TableSetupColumn("Select");
                ImGui.TableHeadersRow();

                for (int i = 0; i < solutions.Count; i++)
                {
                    var sol = solutions[i];
                    ImGui.TableNextRow();
                    ImGui.PushID(i);
                    ImGui.TableNextColumn();
                    ImGui.Text($"{i + 1}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{sol.TotalScore:F2}");
                    ImGui.TableNextColumn();
                    ImGui.Text(RoutedValueAt(i) is { } routed ? $"{routed:F2}" : "-");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{sol.IsValid}");
                    ImGui.TableNextColumn();
                    var isSelected = i == _selectedSolutionIndex;
                    if (isSelected)
                        ImGui.PushStyleColor(ImGuiCol.Button, Color.Green.ToImguiVec4());
                    if (ImGui.Button(isSelected ? "Selected" : "Select"))
                    {
                        _selectedSolutionIndex = i;
                    }

                    if (isSelected)
                        ImGui.PopStyleColor();
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }
        }

        var cellScores = _uiScorer?.CellScores(currentSolution.Grid);

        if (ImGui.BeginTable("ScoreBreakdown", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn("Tile", ImGuiTableColumnFlags.WidthFixed, 25);
            ImGui.TableSetupColumn("Piece", ImGuiTableColumnFlags.WidthFixed, 20);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Score", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Mods");
            ImGui.TableHeadersRow();

            for (int i = 0; i < 9; i++)
            {
                var r = i / 3;
                var c = i % 3;
                var placement = currentSolution.Grid[r, c];

                ImGui.TableNextRow();
                ImGui.PushID($"tile{i}");
                ImGui.TableNextColumn();
                ImGui.Text($"{r},{c}");
                ImGui.TableNextColumn();
                ImGui.Text($"#{placement.Piece.Id}");
                ImGui.TableNextColumn();
                ImGui.Text($"{placement.Piece.Type}");
                ImGui.TableNextColumn();
                ImGui.Text(cellScores != null ? $"{cellScores[r, c]:F1}" : "-");
                ImGui.TableNextColumn();
                var modText = string.Join(", ", placement.Piece.Modifiers.Where(m => m.Name != "Default").Select(m =>
                {
                    var displayName = TrimChartPrefix(m.Name);
                    var prefix = m.IsGlobal ? "[Global] " : "";
                    return $"{prefix}{displayName}({m.Weight:F1})";
                }));
                ImGui.Text(string.IsNullOrEmpty(modText) ? "-" : modText);
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        DrawScoreDetails(currentSolution);

        ImGui.End();
    }

    /// <summary>
    /// Per-tile justification of the selected solution's score: every contribution landing on a
    /// tile, where it came from, and which border multipliers applied to it.
    /// </summary>
    private void DrawScoreDetails(VoyageSolution solution)
    {
        if (_uiScorer == null)
            return;

        ImGui.Spacing();
        if (!ImGui.TreeNode("Score details"))
            return;

        var explanation = _uiScorer.Explain(solution.Grid);
        for (int i = 0; i < 9; i++)
        {
            var r = i / 3;
            var c = i % 3;
            var placement = solution.Grid[r, c];
            var rows = explanation[r, c];
            var total = rows.Sum(x => x.Value);

            ImGui.PushID($"detail{i}");
            var open = ImGui.TreeNode("node", $"({r},{c}) #{placement.Piece.Id} {placement.Piece.Type} — {total:F1}");
            if (open)
            {
                var borders = _uiScorer.BordersAt(r, c);
                ImGui.TextDisabled(borders.Count > 0
                    ? "Borders: " + string.Join(",  ", borders.Select(FormatBorderEffect))
                    : "No borders touch this tile");

                if (rows.Count == 0)
                {
                    ImGui.TextDisabled("No score contributions");
                }
                else if (ImGui.BeginTable("details", 6,
                             ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Mod");
                    ImGui.TableSetupColumn("From", ImGuiTableColumnFlags.WidthFixed, 75);
                    ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 60);
                    ImGui.TableSetupColumn("Mult", ImGuiTableColumnFlags.WidthFixed, 130);
                    ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 65);
                    ImGui.TableSetupColumn("Applied borders");
                    ImGui.TableHeadersRow();

                    foreach (var row in rows)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.Text($"{(row.IsGlobal ? "[G] " : "")}{TrimChartPrefix(row.ModName)}");
                        ImGui.TableNextColumn();
                        ImGui.Text(row.SourcePieceId < 0
                            ? "-"
                            : row.IsGlobal
                                ? "self"
                                : $"#{row.SourcePieceId} ({row.SourceRow},{row.SourceCol})");
                        ImGui.TableNextColumn();
                        ImGui.Text($"{row.Weight:F1}");
                        ImGui.TableNextColumn();
                        // For locals: chart-side x tile-side multipliers. For globals the tile
                        // factor is the sum of matching multipliers over all 9 tiles.
                        ImGui.Text(row.SourcePieceId < 0
                            ? $"x{row.TileFactor:F2}"
                            : row.IsGlobal
                                ? $"x{row.ChartMultiplier:F2} sum{row.TileFactor:F2}"
                                : $"x{row.ChartMultiplier:F2} x{row.TileFactor:F2}");
                        ImGui.TableNextColumn();
                        ImGui.Text($"{row.Value:F1}");
                        ImGui.TableNextColumn();
                        var applied = row.TileBorders
                            .Select(b => $"{TrimBorderPrefix(b.Name)} x{b.Multiplier:0.##}")
                            .Concat(row.ChartBorders
                                .Select(b => $"{TrimBorderPrefix(b.Name)} x{b.Multiplier:0.##} (boosts chart at ({row.SourceRow},{row.SourceCol}))"))
                            .ToList();
                        ImGui.Text(applied.Count > 0 ? string.Join(", ", applied) : "-");
                    }

                    ImGui.EndTable();
                }

                ImGui.TreePop();
            }

            ImGui.PopID();
        }

        ImGui.TreePop();
    }

    private string FormatBorderEffect(BorderEffect border)
    {
        return $"{TrimBorderPrefix(border.Name)} x{border.Multiplier:0.##}{(border.PerConnection ? "/conn" : "")}" +
               $"{(border.AffectsPlacedChart ? " (boosts this tile's chart, value lands where its mods point)" : "")} [{border.Tags}]";
    }

    private static string TrimBorderPrefix(string name)
    {
        return name.StartsWith("DeepwaterBorder", StringComparison.Ordinal)
            ? name["DeepwaterBorder".Length..]
            : name;
    }

    private static string[] BuildAsciiGrid(MapPiecePlacement[,] grid, List<VoyageTileElement> tiles)
    {
        const int H = 5;
        const int W = 7;
        const int GH = H * 3 + 2;
        const int GW = W * 3 + 2;

        var buf = new char[GH, GW];
        for (int y = 0; y < GH; y++)
        for (int x = 0; x < GW; x++)
            buf[y, x] = ' ';

        FillBox(buf, '+', '+', '+', '+', '-', '|', 0, 0, GH - 1, GW - 1);

        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                var left = c * W + 1;
                var right = left + W - 1;
                var top = r * H + 1;
                var bot = top + H - 1;
                var cx = left + W / 2;
                var cy = top + H / 2;

                var p = grid[2 - r, c];
                var conn = p.Connections;

                for (int y = top; y <= bot; y++)
                for (int x = left; x <= right; x++)
                    buf[y, x] = ' ';

                if (conn.HasFlag(Direction.Up))
                    for (int y = top; y < cy; y++)
                        buf[y, cx] = '|';
                if (conn.HasFlag(Direction.Down))
                    for (int y = cy + 1; y <= bot; y++)
                        buf[y, cx] = '|';
                if (conn.HasFlag(Direction.Left))
                    for (int x = left; x < cx; x++)
                        buf[cy, x] = '-';
                if (conn.HasFlag(Direction.Right))
                    for (int x = cx + 1; x <= right; x++)
                        buf[cy, x] = '-';

                buf[cy, cx] = conn switch
                {
                    Direction.Up | Direction.Down => '|',
                    Direction.Left | Direction.Right => '-',
                    Direction.All => '+',
                    _ => '.',
                };

                // Match indicator
                var tileIdx = (2 - r) * 3 + c;
                bool matches = false;
                if (tileIdx < tiles.Count)
                {
                    var t = tiles[tileIdx];
                    if (t.ItemContainer?.Address != null)
                    {
                        var placed = t.ItemContainer.Entity.GetComponent<DeepwaterChart>();
                        if (placed != null)
                        {
                            var actualRot = ((Direction)placed.Room.Path).RotateCcw(placed.Rotation);
                            var expectedRot = p.Connections;
                            matches = actualRot == expectedRot;
                        }
                    }
                }

                buf[cy + 1, cx + 2] = matches ? 'O' : 'X';
            }
        }

        var lines = new string[GH];
        for (int y = 0; y < GH; y++)
        {
            var row = new char[GW];
            for (int x = 0; x < GW; x++)
                row[x] = buf[y, x];
            lines[y] = new string(row);
        }

        return lines;
    }

    private static void FillBox(char[,] buf, char tl, char tr, char bl, char br, char h, char v, int y1, int x1, int y2, int x2)
    {
        buf[y1, x1] = tl;
        buf[y1, x2] = tr;
        buf[y2, x1] = bl;
        buf[y2, x2] = br;
        for (int x = x1 + 1; x < x2; x++)
        {
            buf[y1, x] = h;
            buf[y2, x] = h;
        }

        for (int y = y1 + 1; y < y2; y++)
        {
            buf[y, x1] = v;
            buf[y, x2] = v;
        }
    }

    private static string TrimChartPrefix(string name)
    {
        if (name.StartsWith("MapDeepwaterChartVoyage", StringComparison.Ordinal))
            return name["MapDeepwaterChartVoyage".Length..];
        if (name.StartsWith("MapDeepwaterChartAdjacent", StringComparison.Ordinal))
            return name["MapDeepwaterChartAdjacent".Length..];
        return name;
    }
}
