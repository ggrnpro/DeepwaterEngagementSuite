using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeepwaterEngagementSuiteGGRN.VoyagePlannerData;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Models;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Debug capture for the voyage planner: dumps what the game actually exposes so the scoring model
/// can be built against real data instead of guesses.
///
/// The board signature is watched every frame, so every reroll is recorded automatically — that log
/// doubles as the sample set for estimating how often each border modifier appears.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    private VoyageTelemetry _telemetry;
    private string _lastBoardSignature;
    private string _lastSnapshotPath;
    private int _boardsSeen;

    private VoyageTelemetry Telemetry
    {
        get
        {
            if (_telemetry == null && Settings.VoyageSettings.EnableDebugDump)
                _telemetry = new VoyageTelemetry(Path.Combine(ConfigDirectory, "debug"));

            return _telemetry;
        }
    }

    /// <summary>
    /// Watches the voyage window and appends an event whenever the board changes (a reroll, a new
    /// voyage, or a different set of charts in the tray).
    /// </summary>
    private void TrackVoyageBoard(VoyageWindow tree)
    {
        if (!Settings.VoyageSettings.EnableDebugDump)
            return;

        string signature;
        try
        {
            signature = BuildBoardSignature(tree);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"DWS debug: signature failed: {ex.Message}");
            return;
        }

        if (signature == _lastBoardSignature)
            return;

        var previous = _lastBoardSignature;
        _lastBoardSignature = signature;
        _boardsSeen++;

        try
        {
            var board = BuildBoardInfo(tree, describeFirstChart: _boardsSeen == 1);
            Telemetry?.Log(previous == null ? "board_seen" : "board_changed", board);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"DWS debug: board capture failed: {ex.Message}");
        }
    }

    private static string BuildBoardSignature(VoyageWindow tree)
    {
        var borders = string.Join(",", tree.Data.BorderMods.Select(m => m.RawName));
        var charts = string.Join(",", tree.AvailableCharts.Select(c => c.Address.ToString("X")));
        var tiles = string.Join(",", tree.Tiles.Select(t =>
            t?.ItemContainer?.Entity?.GetComponent<DeepwaterChart>() is { } chart
                ? $"{t.ItemContainer.Entity.Address:X}:{chart.Rotation}"
                : "-"));
        return $"{borders}|{charts}|{tiles}";
    }

    /// <summary>Full description of the current voyage window: borders, tray charts, placed charts.</summary>
    private object BuildBoardInfo(VoyageWindow tree, bool describeFirstChart)
    {
        var borderMods = tree.Data.BorderMods;
        var tileMods = GetTileMods(tree);
        var charts = GetAvailableCharts();

        foreach (var mod in borderMods)
        {
            var known = Settings.VoyageSettings.BorderModifiers.Content
                .Any(c => c.Id.Value.Equals(mod.RawName, StringComparison.OrdinalIgnoreCase));
            if (!known)
                Telemetry?.NoteUnknown("border", mod.RawName, SafeDisplayName(mod));
        }

        return new
        {
            profile = Settings.VoyageSettings.ProfileSelector.Value,
            borderModCount = borderMods.Count,
            borderMods = borderMods.Select((m, i) => new
            {
                slot = i,
                id = m.RawName,
                display = SafeDisplayName(m),
                values = SafeValues(m),
            }).ToList(),
            tileBorders = tileMods.ToDictionary(
                kv => kv.Key.ToString(),
                kv => kv.Value.Select(m => m.RawName).ToList()),
            trayCharts = charts.Select((c, i) => BuildChartInfo(c, i, describeFirstChart && i == 0)).ToList(),
            placedCharts = tree.Tiles.Select((t, i) => new
            {
                tile = i,
                row = i / 3,
                col = i % 3,
                chart = t?.ItemContainer?.Entity is { } e && e.GetComponent<DeepwaterChart>() != null
                    ? BuildChartInfo(e, -1, describe: false)
                    : null,
            }).ToList(),
        };
    }

    private object BuildChartInfo(NormalInventoryItem element, int index, bool describe)
    {
        var info = BuildChartInfo(element?.Item, index, describe);
        return info;
    }

    private object BuildChartInfo(Entity entity, int index, bool describe)
    {
        if (entity == null)
            return null;

        var mods = entity.GetComponent<Mods>();
        var chart = entity.GetComponent<DeepwaterChart>();
        var baseComponent = entity.GetComponent<Base>();

        foreach (var mod in mods?.ImplicitMods ?? [])
        {
            var known = Settings.VoyageSettings.ChartModifiers.Content
                .Any(c => c.Id.Value.Equals(mod.RawName, StringComparison.OrdinalIgnoreCase));
            if (!known)
                Telemetry?.NoteUnknown("chartImplicit", mod.RawName, SafeDisplayName(mod));
        }

        // Explicit mods are the map-mod-like rolls (monster danger plus sulphur riders). They are
        // not modelled at all yet, so collect the full vocabulary while playing.
        foreach (var mod in mods?.ExplicitMods ?? [])
            Telemetry?.NoteUnknown("chartExplicit", mod.RawName, SafeDisplayName(mod));

        Direction connections = 0;
        int? rotation = null;
        try
        {
            if (chart != null)
            {
                connections = (Direction)chart.Room.Path;
                rotation = chart.Rotation;
            }
        }
        catch
        {
            // Room/Rotation can be unreadable for a frame while the item is moving; not fatal.
        }

        return new
        {
            index,
            address = entity.Address.ToString("X"),
            path = entity.Path,
            baseName = SafeBaseName(baseComponent),
            rarity = mods?.ItemRarity.ToString(),
            identified = mods?.Identified,
            corrupted = SafeCorrupted(baseComponent),
            connections = connections.ToString(),
            connectionCount = connections.CountConnections(),
            rotation,
            stats = SafeStats(mods),
            implicitMods = (mods?.ImplicitMods ?? []).Select(DescribeMod).ToList(),
            explicitMods = (mods?.ExplicitMods ?? []).Select(DescribeMod).ToList(),
            // One-off reflection dump so the real API shape (area name, biome, room data) is visible
            // in the log rather than guessed at from the outside.
            chartComponent = describe ? VoyageTelemetry.Describe(chart, depth: 2) : null,
            modsComponent = describe ? VoyageTelemetry.Describe(mods, depth: 1) : null,
        };
    }

    private object DescribeMod(ItemMod mod)
    {
        // The item's own stat block comes back empty for charts, so the mod records are the only
        // way to learn which of a mod's values is quantity, rarity, pack size or sulphur.
        Telemetry?.NoteModRecord(mod.RawName, () => VoyageTelemetry.Describe(SafeModRecord(mod), depth: 2));

        return new
        {
            id = mod.RawName,
            display = SafeDisplayName(mod),
            values = SafeValues(mod),
        };
    }

    private static object SafeModRecord(ItemMod mod)
    {
        try
        {
            return mod.ModRecord;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeDisplayName(ItemMod mod)
    {
        try
        {
            return mod.DisplayName;
        }
        catch
        {
            return null;
        }
    }

    private static List<int> SafeValues(ItemMod mod)
    {
        try
        {
            return mod.Values?.ToList();
        }
        catch
        {
            return null;
        }
    }

    private static string SafeBaseName(Base baseComponent)
    {
        try
        {
            return baseComponent?.Name;
        }
        catch
        {
            return null;
        }
    }

    private static bool? SafeCorrupted(Base baseComponent)
    {
        try
        {
            return baseComponent?.isCorrupted;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The chart's own Item Quantity / Rarity / Pack Size / Dead Man's Sulphur rolls. ExileCore's
    /// ItemStats shape is not part of the plugin's compile-time surface, so this walks it
    /// reflectively — the dump then shows exactly where those numbers live.
    /// </summary>
    private static object SafeStats(Mods mods)
    {
        try
        {
            return mods?.ItemStats == null ? null : VoyageTelemetry.Describe(mods.ItemStats, depth: 2);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes a full snapshot of the current window plus the latest solver output.</summary>
    private void DumpVoyageSnapshot(VoyageWindow tree, string label)
    {
        var telemetry = Telemetry;
        if (telemetry == null)
            return;

        try
        {
            var payload = new
            {
                board = BuildBoardInfo(tree, describeFirstChart: true),
                solver = BuildSolverInfo(),
                profileName = Settings.VoyageSettings.ProfileSelector.Value,
                borderProfile = Settings.VoyageSettings.BorderModifiers.Content.Select(b => new
                {
                    id = b.Id.Value,
                    multiplier = b.ValueMultiplier.Value,
                    tags = b.Tags.Value,
                    perConnection = b.PerConnection.Value,
                    affectsPlacedChart = b.AffectsPlacedChart.Value,
                }).ToList(),
                chartProfile = Settings.VoyageSettings.ChartModifiers.Content.Select(c => new
                {
                    id = c.Id.Value,
                    weight = c.Weight.Value,
                    isGlobal = c.IsGlobal.Value,
                    tags = c.Tags.Value,
                }).ToList(),
            };

            _lastSnapshotPath = telemetry.WriteSnapshot(label, payload);
            telemetry.FlushUnknown();
            DebugWindow.LogMsg($"DWS: snapshot written to {_lastSnapshotPath}");
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"DWS debug: snapshot failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Records one solver run: the exact puzzle it was given and what it produced. Replaying these
    /// offline is how scoring and solver changes get tested without going back into the game.
    /// </summary>
    private void LogVoyageSolve(VoyagePuzzle puzzle, int timeLimitSeconds)
    {
        if (!Settings.VoyageSettings.EnableDebugDump)
            return;

        try
        {
            Telemetry?.Log("solve", new
            {
                solver = Settings.VoyageSettings.UseAssignmentSolver.Value ? "assignment"
                    : Settings.VoyageSettings.UseFastSolver.Value ? "fast"
                    : "backtracking",
                timeLimitSeconds,
                puzzle = new
                {
                    pieces = puzzle.AvailablePieces.Select(p => new
                    {
                        p.Id,
                        type = p.Type.ToString(),
                        connections = p.BaseConnections.ToString(),
                        mods = p.Modifiers
                            .Where(m => m.Name != "Default")
                            .Select(m => new { m.Name, m.Weight, m.IsGlobal, tags = m.Tags.ToString() })
                            .ToList(),
                    }).ToList(),
                    tileBorders = Enumerable.Range(0, 9).Select(i => new
                    {
                        tile = i,
                        borders = (puzzle.TileBorders?[i / 3, i % 3] ?? []).Select(b => new
                        {
                            b.Name,
                            b.Multiplier,
                            b.PerConnection,
                            b.AffectsPlacedChart,
                            tags = b.Tags.ToString(),
                        }).ToList(),
                    }).ToList(),
                },
                result = BuildSolverInfo(),
            });
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"DWS debug: solve capture failed: {ex.Message}");
        }
    }

    private object BuildSolverInfo()
    {
        if (_result == null)
            return null;

        return new
        {
            solutionCount = _result.Solutions.Count,
            nodesExplored = _result.NodesExplored,
            nodesPruned = _result.NodesPruned,
            elapsedSeconds = _voyageElapsed,
            timedOut = _voyageTimedOut,
            diagnostics = _voyageDiagnostics,
            solutions = _result.Solutions.Take(5).Select(s => new
            {
                score = s.TotalScore,
                valid = s.IsValid,
                grid = Enumerable.Range(0, 9).Select(i =>
                {
                    var p = s.Grid[i / 3, i % 3];
                    return new
                    {
                        tile = i,
                        pieceId = p.Piece.Id,
                        type = p.Piece.Type.ToString(),
                        rotation = p.Rotation,
                        connections = p.Connections.ToString(),
                        mods = p.Piece.Modifiers
                            .Where(m => m.Name != "Default")
                            .Select(m => new { m.Name, m.Weight, m.IsGlobal, tags = m.Tags.ToString() })
                            .ToList(),
                    };
                }).ToList(),
            }).ToList(),
        };
    }
}
