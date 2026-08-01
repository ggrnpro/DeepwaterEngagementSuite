using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Exports every modifier the league can roll, from the game's own table.
///
/// Everything the planner knows so far was learned from modifiers that happened to appear in front
/// of the player, which leaves two gaps: modifiers never seen are missing entirely, and how likely
/// each one is to appear is unknown — so reroll advice could only compare a board against the
/// handful of boards already recorded rather than against what a reroll can actually produce.
///
/// The game ships the whole table locally and it matches the installed patch exactly, which no
/// external database can promise. Enumerating it closes both gaps at once: the full vocabulary, the
/// magnitudes, and the spawn weights that make expected value computable.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    /// <summary>Prefixes worth exporting. Everything else in the table belongs to other content.</summary>
    private static readonly string[] CataloguePrefixes =
    [
        "MapDeepwaterChart",
        "DeepwaterBorder",
        "Chest",
        "MapDeepwater",
    ];

    private void ExportModCatalogue()
    {
        var telemetry = Telemetry;
        if (telemetry == null)
            return;

        try
        {
            var records = GameController.Files.Mods.records;
            var exported = new List<object>();

            foreach (var (key, record) in records)
            {
                if (key == null || !CataloguePrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
                    continue;

                exported.Add(DescribeCatalogueEntry(key, record));
            }

            var path = telemetry.WriteSnapshot("mod-catalogue", new
            {
                total = records.Count,
                exported = exported.Count,
                mods = exported,
            });

            DebugWindow.LogMsg($"DWS: {exported.Count} modifiers exported to {path}");
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"DWS: modifier export failed: {ex.Message}");
        }
    }

    private static object DescribeCatalogueEntry(string key, dynamic record)
    {
        try
        {
            var statNames = new List<string>();
            var ranges = new List<object>();
            try
            {
                foreach (var stat in record.StatNames)
                    statNames.Add(stat?.MatchingStat.ToString());

                foreach (var range in record.StatRange)
                    ranges.Add(new { min = range.Min, max = range.Max });
            }
            catch
            {
                // a record can be missing either list
            }

            // Spawn weights: a modifier is only offered where its tag weight is above zero, and the
            // weight decides how often. This is what turns "is this board bad?" into an expected
            // value rather than a comparison against the few boards already seen.
            var weights = new List<object>();
            try
            {
                foreach (var tag in record.TagChances)
                    weights.Add(new { tag = tag.Key, weight = tag.Value });
            }
            catch
            {
                // not every record carries tag chances
            }

            return new
            {
                id = key,
                name = (string)record.UserFriendlyName,
                affix = record.AffixType.ToString(),
                group = (string)record.Group,
                minLevel = record.MinLevel,
                stats = statNames,
                ranges,
                spawnWeights = weights,
            };
        }
        catch (Exception ex)
        {
            return new { id = key, error = ex.GetBaseException().Message };
        }
    }
}
