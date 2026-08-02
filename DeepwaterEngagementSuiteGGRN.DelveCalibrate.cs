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
                // Three ways the name could sit here: written into the node itself, behind one
                // pointer, or behind two. The first scan only tried the middle one and came back
                // with nothing at all, which rules out a filter that is merely too strict.
                Record(offset, "direct", ReadText(address + offset));
                Record(offset, "ptr", ReadTextThrough(address + offset));
                Record(offset, "ptr2", ReadTextThroughTwice(address + offset));
            }

            void Record(int offset, string via, string text)
            {
                if (text == null)
                    return;

                var key = offset * 4 + via switch { "direct" => 0, "ptr" => 1, _ => 2 };
                if (!hits.TryGetValue(key, out var list))
                    hits[key] = list = [];

                if (list.Count < 8 && !list.Contains(text))
                    list.Add(text);
            }
        }

        // An offset that lands on names the game knows is the one being looked for; the rest are
        // words that happened to point at something readable.
        return hits
            .Select(x => new
            {
                offset = $"0x{x.Key / 4:X}",
                via = (x.Key % 4) switch { 0 => "direct", 1 => "ptr", _ => "ptr2" },
                matchesKnownNames = x.Value.Count(v => known.Contains(v)),
                samples = x.Value,
            })
            // One reading is worth reporting now. Requiring two threw away the only evidence there
            // was when the first scan came back empty.
            .Where(x => x.samples.Count > 0)
            .OrderByDescending(x => x.matchesKnownNames)
            .ThenByDescending(x => x.samples.Count)
            .Take(40)
            .Cast<object>()
            .ToList();
    }

    /// <summary>Text written at the address itself.</summary>
    private string ReadText(long at)
    {
        try
        {
            return Sane(GameController.Memory.ReadStringU(at));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Text behind the pointer stored at the address.</summary>
    private string ReadTextThrough(long at) => ReadText(Dereference(at));

    /// <summary>Text two pointers along, for a name held in a wrapper rather than inline.</summary>
    private string ReadTextThroughTwice(long at) => ReadText(Dereference(Dereference(at)));

    private long Dereference(long at)
    {
        if (at == 0)
            return 0;

        try
        {
            var pointer = GameController.Memory.Read<long>(at);
            return pointer is < 0x10000 or > 0x7FFFFFFFFFFF ? 0 : pointer;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Whether this reads like a name rather than like memory that happens to decode. A real name is
    /// words: printable, and containing letters.
    /// </summary>
    private static string Sane(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length is < 3 or > 48)
            return null;

        return text.All(c => c is >= ' ' and <= '~') && text.Any(char.IsLetter) ? text : null;
    }
}
