using System;
using System.Collections.Generic;
using System.Linq;
using DeepwaterEngagementSuiteGGRN.VoyagePlannerData;
using ExileCore.PoEMemory.Components;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Ties a chart to the area it opens.
///
/// Most charts lead to a plain biome room worth nothing on its own; a few lead to a named area with
/// its own encounter, and that is often the main reason to prefer one chart over another. The
/// planner reads the area off the chart and turns its configured value into a modifier that lands
/// in that chart's own room.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    /// <summary>Fills the area table with the known areas the first time the plugin runs.</summary>
    private void SeedAreaValues()
    {
        var content = Settings.VoyageSettings.AreaValues.Content;
        if (content.Count > 0)
            return;

        foreach (var entry in VoyageAreaValues.Defaults)
        {
            content.Add(new VoyageAreaSetting
            {
                Area = new ExileCore.Shared.Nodes.TextNode(entry.Area),
                Weight = new ExileCore.Shared.Nodes.RangeNode<float>((float)entry.Weight, 0, 2000),
                Tags = new ExileCore.Shared.Nodes.TextNode(entry.Tags),
            });
        }
    }

    private static string SafeRoomName(DeepwaterChart chart)
    {
        try
        {
            return chart?.Room?.Name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The area's own value, as a modifier landing in the chart's own room.</summary>
    private List<Modifier> AreaModifiers(string areaName)
    {
        if (string.IsNullOrWhiteSpace(areaName))
            return [];

        var setting = Settings.VoyageSettings.AreaValues.Content
            .FirstOrDefault(x => x.Area.Value.Equals(areaName, StringComparison.OrdinalIgnoreCase));

        if (setting == null)
        {
            // An area nobody has configured: record it so it can be priced rather than ignored.
            Telemetry?.NoteUnknown("area", areaName);
            return [];
        }

        if (setting.Weight.Value <= 0)
            return [];

        return [new Modifier(
            $"Area: {areaName}",
            setting.Weight.Value,
            IsGlobal: false,
            ModifierTagParser.Parse(setting.Tags.Value, ModifierTag.None))];
    }
}
