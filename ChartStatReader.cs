using System;
using System.Collections.Generic;
using System.Linq;
using DeepwaterEngagementSuiteGGRN.VoyagePlannerData;
using ExileCore.PoEMemory.MemoryObjects;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Reads a chart's reward stats straight out of the game's modifier data.
///
/// A chart's Item Quantity, Item Rarity, Pack Size and Dead Man's Sulphur are shown on its tooltip
/// header but its own stat block is empty, so they have to be recovered from the modifiers that
/// grant them. Every modifier record carries its stat names and rolled values, which makes this
/// exact rather than a table that has to be maintained by hand as the league is patched.
///
/// Verified against three charts: their derived Quantity/Rarity/Pack/Sulphur match the tooltip to
/// the point.
/// </summary>
public static class ChartStatReader
{
    private const string QuantityStat = "MapItemDropQuantityPct";
    private const string RarityStat = "MapItemDropRarityPct";
    private const string PackSizeStat = "MapPackSizePct";
    private const string SulphurStat = "MapDeepwaterLeagueResourceFoundPct";
    private const string GoldStat = "MapGoldFoundPct";
    private const string AdjacentScopeStat = "LocalDeepwaterModAppliesToAdjacentCharts";
    private const string GlobalScopeStat = "LocalDeepwaterModAppliesGlobally";

    /// <summary>
    /// Reward stats a single modifier grants, and how far they reach. Returns null when the
    /// modifier's data cannot be read.
    /// </summary>
    public static (ModScope Scope, ChartRewardStats Stats)? Read(ItemMod mod)
    {
        List<string> statNames;
        List<int> values;
        try
        {
            var record = mod?.ModRecord;
            if (record?.StatNames == null)
                return null;

            statNames = record.StatNames.Select(x => x?.MatchingStat.ToString()).ToList();
            values = ReadValues(mod, record, statNames.Count);
        }
        catch
        {
            return null;
        }

        double quantity = 0, rarity = 0, packSize = 0, sulphur = 0, gold = 0;
        var scope = ModScope.SelfArea;

        for (var i = 0; i < statNames.Count; i++)
        {
            var name = statNames[i];
            if (name == null)
                continue;

            var value = i < values.Count ? values[i] : 0;
            switch (name)
            {
                case QuantityStat: quantity += value; break;
                case RarityStat: rarity += value; break;
                case PackSizeStat: packSize += value; break;
                case SulphurStat: sulphur += value; break;
                case GoldStat: gold += value; break;
                case AdjacentScopeStat: scope = ModScope.AdjacentAreas; break;
                case GlobalScopeStat: scope = ModScope.WholeVoyage; break;
            }
        }

        return (scope, new ChartRewardStats(quantity, rarity, packSize, sulphur, gold));
    }

    /// <summary>
    /// Total reward stats that apply to the area a chart opens: everything the chart rolled whose
    /// effect does not reach past its own area.
    /// </summary>
    public static ChartRewardStats SelfAreaStats(IEnumerable<ItemMod> mods)
    {
        var total = default(ChartRewardStats);
        foreach (var mod in mods ?? [])
        {
            if (Read(mod) is { Scope: ModScope.SelfArea } entry)
                total += entry.Stats;
        }

        return total;
    }

    /// <summary>Reward stats a chart projects onto every area of the voyage.</summary>
    public static ChartRewardStats VoyageWideStats(IEnumerable<ItemMod> mods)
    {
        var total = default(ChartRewardStats);
        foreach (var mod in mods ?? [])
        {
            if (Read(mod) is { Scope: ModScope.WholeVoyage } entry)
                total += entry.Stats;
        }

        return total;
    }

    /// <summary>Reward stats a chart projects onto each orthogonally adjacent area.</summary>
    public static ChartRewardStats AdjacentStats(IEnumerable<ItemMod> mods)
    {
        var total = default(ChartRewardStats);
        foreach (var mod in mods ?? [])
        {
            if (Read(mod) is { Scope: ModScope.AdjacentAreas } entry)
                total += entry.Stats;
        }

        return total;
    }

    private static List<int> ReadValues(ItemMod mod, dynamic record, int statCount)
    {
        // The rolled values are authoritative; the record's ranges are the fallback for the frames
        // where an item's values are not readable. Reward stats roll a fixed value either way.
        List<int> rolled = null;
        try
        {
            rolled = mod.Values?.ToList();
        }
        catch
        {
            // fall through to ranges
        }

        if (rolled != null && rolled.Count >= statCount)
            return rolled;

        var result = new List<int>(statCount);
        try
        {
            foreach (var range in record.StatRange)
                result.Add((int)range.Min);
        }
        catch
        {
            return rolled ?? [];
        }

        return result;
    }
}
