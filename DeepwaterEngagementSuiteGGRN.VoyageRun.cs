using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Captures what happens inside a voyage, as opposed to what was planned on the board.
///
/// The open question the planner cannot answer on its own is how Golden Lanterns actually pay out:
/// whether picking one up raises a stat for the rest of the run (so lantern rooms should be visited
/// first) or only buffs monsters near it. Player stats answer that directly, so every change to a
/// deepwater/lantern/reward stat is logged with the area it happened in.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    private static readonly string[] TrackedStatFragments =
    [
        "Deepwater", "Lantern", "Chart", "Quantity", "Rarity", "PackSize", "Resource", "Sulphur",
    ];

    private string _lastVoyageStatSignature;
    private DateTime _lastVoyageStatLog = DateTime.MinValue;
    private string _currentAreaName;

    /// <summary>Logs the area transition and the player's reward-related stats on entry.</summary>
    private void LogVoyageAreaChange(AreaInstance area)
    {
        if (!Settings.VoyageSettings.EnableDebugDump)
            return;

        _lastVoyageStatSignature = null;

        try
        {
            _currentAreaName = area?.Area?.Name;
            Telemetry?.Log("area_entered", new
            {
                name = _currentAreaName,
                rawName = area?.Area?.RawName,
                level = area?.RealLevel,
                hash = area?.Hash,
                isHideout = area?.IsHideout,
                stats = CollectTrackedStats(),
                buffs = CollectBuffs(),
            });
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"DWS debug: area capture failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Called every frame while in game. Emits an event whenever a tracked stat changes, which is
    /// what reveals the size and lifetime of the Golden Lantern bonus.
    /// </summary>
    private void TrackVoyageRun()
    {
        if (!Settings.VoyageSettings.EnableDebugDump)
            return;

        var now = DateTime.UtcNow;
        if (now - _lastVoyageStatLog < TimeSpan.FromMilliseconds(400))
            return;

        _lastVoyageStatLog = now;

        try
        {
            var stats = CollectTrackedStats();
            if (stats == null || stats.Count == 0)
                return;

            var buffSignature = string.Join(",", (CollectBuffs() ?? [])
                .Select(x => x.ToString())
                .OrderBy(x => x, StringComparer.Ordinal));
            var signature = string.Join(";", stats.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}")) + "|" + buffSignature;
            if (signature == _lastVoyageStatSignature)
                return;

            _lastVoyageStatSignature = signature;
            Telemetry?.Log("voyage_stats", new
            {
                area = _currentAreaName,
                stats,
                buffs = CollectBuffs(),
            });
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"DWS debug: stat capture failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The player's buffs. Golden Lantern pickups do not show up in the stats sampled above, so the
    /// bonus they grant is most likely carried as a buff — quite possibly a stacking one.
    /// </summary>
    private List<object> CollectBuffs()
    {
        try
        {
            var buffs = GameController?.Player?.GetComponent<Buffs>()?.BuffsList;
            if (buffs == null)
                return null;

            var result = new List<object>();
            foreach (var buff in buffs)
                result.Add(new { buff.Name, buff.Charges, timer = buff.Timer });

            return result;
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, int> CollectTrackedStats()
    {
        try
        {
            var dictionary = GameController?.Player?.GetComponent<Stats>()?.StatDictionary;
            if (dictionary == null)
                return null;

            var result = new Dictionary<string, int>();
            foreach (var kv in dictionary)
            {
                var name = kv.Key.ToString();
                foreach (var fragment in TrackedStatFragments)
                {
                    if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    {
                        result[name] = kv.Value;
                        break;
                    }
                }
            }

            return result;
        }
        catch
        {
            return null;
        }
    }
}
