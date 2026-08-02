using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Exports the mine's own vocabulary: every biome, every room feature, and every modifier the league
/// can apply.
///
/// Ranking a node on the chart means knowing what its biome and its rooms are worth, and that is
/// three tables the game ships locally. Community pages carry part of it, out of date and with the
/// interesting half cut off; the game's copy matches the installed patch by construction.
///
/// The biome and feature tables give the names. The modifier table gives what those names do, and it
/// is unusually explicit here - stats like DelveBiomeOffPathRewardChestsAlwaysFossils say outright
/// what a biome puts in the chests off the cart's route, which is the whole question when choosing
/// where to delve.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    /// <summary>Modifier name prefixes that belong to the mine rather than to other content.</summary>
    private static readonly string[] DelveCataloguePrefixes =
    [
        "DelveBiome",
        "Delve",
        "MapDelve",
        "LocalDelve",
    ];

    private void ExportDelveCatalogue()
    {
        var telemetry = Telemetry;
        if (telemetry == null)
            return;

        try
        {
            var biomes = ReadTable(() => GameController.Files.DelveBiomes.EntriesList
                .Select(x => (object)new { x.Id, x.Name })
                .ToList());

            var features = ReadTable(() => GameController.Files.DelveFeatures.EntriesList
                .Select(x => (object)new { x.Id, x.Name, x.Image })
                .ToList());

            var mods = ReadTable(CollectDelveMods);

            var path = telemetry.WriteSnapshot("delve-catalogue", new
            {
                biomeCount = biomes.Count,
                featureCount = features.Count,
                modCount = mods.Count,
                biomes,
                features,
                mods,
            });

            DebugWindow.LogMsg(
                $"DWS: {biomes.Count} biomes, {features.Count} features, {mods.Count} modifiers exported to {path}");
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"DWS: delve catalogue export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Every league modifier, with the stats that say what it actually does.
    ///
    /// The names alone are suggestive but not decisive - "AlwaysFossils" is readable, a tier upgrade
    /// percentage is not - so the stats and their ranges come too.
    /// </summary>
    private List<object> CollectDelveMods()
    {
        var result = new List<object>();

        foreach (var (key, record) in GameController.Files.Mods.records)
        {
            if (key == null || !DelveCataloguePrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
                continue;

            result.Add(DescribeCatalogueEntry(key, record));
        }

        return result;
    }

    /// <summary>A table that cannot be read is reported as empty rather than losing the whole export.</summary>
    private static List<object> ReadTable(Func<List<object>> read)
    {
        try
        {
            return read() ?? [];
        }
        catch (Exception ex)
        {
            return [new { error = ex.GetBaseException().Message }];
        }
    }
}
