using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Finds where a chart node keeps its name, by looking rather than by being told.
///
/// The typed accessors read the node's strings from offsets that have moved since this build of the
/// core was made, so they come back as pointers rendered as text. The node is still there and the
/// strings are still in it - only the map to them is stale.
///
/// Rather than reverse the structure or fork the core over five fields, this walks the node's own
/// memory, treats every aligned word as a possible pointer, and reads what it points at. The right
/// offset gives itself away: the game ships a table of all 246 room names, so an offset that yields
/// one of those names is the offset, and no hovering or guessing is involved.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    /// <summary>How far into the node to look. Comfortably past where the strings could sit.</summary>
    private const int CalibrationSpan = 0x400;

    /// <summary>Enough nodes to tell a real field from one word that happened to read as text.</summary>
    private const int CalibrationSampleSize = 60;

    private List<object> CalibrateChartCells()
    {
        var chart = FindSubterraneanChart();
        if (chart == null)
            return null;

        var nodes = FindChartNodeElements(chart);
        if (nodes.Count == 0)
            return null;

        // Names the game itself uses, so a candidate can be recognised rather than eyeballed.
        var known = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var feature in GameController.Files.DelveFeatures.EntriesList)
            {
                if (!string.IsNullOrWhiteSpace(feature.Name))
                    known.Add(feature.Name);
            }

            foreach (var biome in GameController.Files.DelveBiomes.EntriesList)
            {
                if (!string.IsNullOrWhiteSpace(biome.Name))
                    known.Add(biome.Name);
            }
        }
        catch
        {
            // without the tables this still records what it found, just without the verdict
        }

        var hits = new Dictionary<int, List<string>>();

        foreach (var node in nodes.Take(CalibrationSampleSize))
        {
            long address;
            try
            {
                address = node.Address;
            }
            catch
            {
                continue;
            }

            if (address == 0)
                continue;

            for (var offset = 0; offset < CalibrationSpan; offset += 8)
            {
                var text = ReadTextThrough(address + offset);
                if (text == null)
                    continue;

                if (!hits.TryGetValue(offset, out var list))
                    hits[offset] = list = [];

                if (list.Count < 8 && !list.Contains(text))
                    list.Add(text);
            }
        }

        // An offset that lands on names the game knows is the one being looked for; the rest are
        // words that happened to point at something readable.
        return hits
            .Select(x => new
            {
                offset = $"0x{x.Key:X}",
                matchesKnownNames = x.Value.Count(v => known.Contains(v)),
                samples = x.Value,
            })
            .Where(x => x.samples.Count > 1)
            .OrderByDescending(x => x.matchesKnownNames)
            .ThenByDescending(x => x.samples.Count)
            .Take(40)
            .Cast<object>()
            .ToList();
    }

    /// <summary>
    /// Reads the word at <paramref name="at"/> as a pointer and returns the text it leads to, when
    /// that text looks like a name rather than like memory being read as one.
    /// </summary>
    private string ReadTextThrough(long at)
    {
        try
        {
            var pointer = GameController.Memory.Read<long>(at);
            if (pointer < 0x10000 || pointer > 0x7FFFFFFFFFFF)
                return null;

            var text = GameController.Memory.ReadStringU(pointer);
            if (string.IsNullOrWhiteSpace(text) || text.Length is < 3 or > 48)
                return null;

            // A real name is words. Anything with a control character or a symbol outside plain
            // punctuation is memory that happens to decode.
            return text.All(c => c is >= ' ' and <= '~') && text.Any(char.IsLetter) ? text : null;
        }
        catch
        {
            return null;
        }
    }
}
