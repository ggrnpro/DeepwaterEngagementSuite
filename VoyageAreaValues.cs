using System;
using System.Collections.Generic;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// What a chart's destination area is worth on its own.
///
/// Most charts open a plain biome room — Abyssal Plain, Undersea Groves, Seafloor Ridges — and are
/// worth nothing beyond the content their neighbours push into them. A handful open a named area
/// with its own encounter, and those are a large part of why one chart is better than another. The
/// planner had no channel for this at all, so every chart of a given shape looked alike.
///
/// The numbers below are starting estimates on the same scale as the chart modifier weights, not
/// measurements. They are meant to be edited: the values that matter are the ones that come out of
/// running the areas and seeing what they actually drop.
/// </summary>
public static class VoyageAreaValues
{
    public readonly record struct AreaValue(string Area, double Weight, string Tags, string Note);

    public static IReadOnlyList<AreaValue> Defaults { get; } =
    [
        // Plain biome rooms: no encounter of their own.
        new("Abyssal Plain", 0, "", "Sandy Seabed filler"),
        new("Undersea Groves", 0, "", "Coral Forest filler"),
        new("Seafloor Ridges", 0, "", "Coral Reef filler"),

        // Sandy Seabed
        new("Kishara's Rest", 400, "", "Boss encounter; reported as the most valuable chart in the league"),
        new("Anchorfield", 220, "Currency", "Sunken loot"),
        new("Infested Spheres", 120, "Monsters", "Extra monsters"),
        new("Hazardous Depths", 150, "", "Rotmother's Ducat"),

        // Coral Forest
        new("Sea Pillars", 150, "", "Starfish"),
        new("Brine King", 180, "RareMonsters", "Pantheon-touched rares"),
        new("Clam Shelf", 60, "Gold", "Gold"),
        new("Diving Shoals", 120, "", "Exclusive mercenary"),
        new("Sunken Totems", 150, "", "Katakohi Ducat"),
        new("Eldritch Depths", 180, "", "Ukatoa's"),
        new("Pelagic Abyss", 200, "", "Abyss pit"),
        new("Lost Ruins", 200, "", "Vaal vessels"),
    ];

    /// <summary>Case-insensitive lookup of the default weight for an area name.</summary>
    public static bool TryGetDefault(string area, out AreaValue value)
    {
        foreach (var entry in Defaults)
        {
            if (string.Equals(entry.Area, area, StringComparison.OrdinalIgnoreCase))
            {
                value = entry;
                return true;
            }
        }

        value = default;
        return false;
    }
}
