using System;
using System.Collections.Generic;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Reads the Subterranean Chart - the map of nodes you choose between before a delve starts.
///
/// Which node to delve into decides what the run can even contain: the game's own stats say a
/// node's biome fixes what its off-path chests hold, down to "always fossils" or "always azurite".
/// That is a larger decision than any routing inside the area, where the loot has already been
/// rolled and all that is left is picking it up.
///
/// On screen the chart only ever names the node under the cursor, so reading it by text means
/// hovering every node one at a time. The core exposes the grid itself, where every cell carries its
/// own type and modifiers whether or not it is hovered.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    /// <summary>
    /// Depth of the walk looking for the chart. It sits a few levels inside the interface root and
    /// the walk stops at the first element that turns out to be one.
    /// </summary>
    private const int ChartSearchDepth = 8;

    /// <summary>
    /// Finds the chart by asking elements whether they are one, rather than by matching the title
    /// text or a fixed position. Both of those move between leagues; the shape of the object does
    /// not, and an element that yields a populated grid is the chart by construction.
    /// </summary>
    private SubterraneanChart FindSubterraneanChart()
    {
        try
        {
            return ProbeForChart(GameController.IngameState.UIRoot, 0);
        }
        catch
        {
            return null;
        }
    }

    private static SubterraneanChart ProbeForChart(Element element, int depth)
    {
        if (element == null || depth > ChartSearchDepth)
            return null;

        try
        {
            if (!element.IsVisible)
                return null;
        }
        catch
        {
            return null;
        }

        try
        {
            var candidate = element.AsObject<SubterraneanChart>();
            if (candidate?.GridElement?.Cells is { Count: > 0 })
                return candidate;
        }
        catch
        {
            // casting an unrelated element is expected to fail; that is the test
        }

        IList<Element> children;
        try
        {
            children = element.Children;
        }
        catch
        {
            return null;
        }

        if (children == null)
            return null;

        foreach (var child in children)
        {
            if (ProbeForChart(child, depth + 1) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>
    /// Every cell on the chart with everything it carries.
    ///
    /// The individual string fields are not named by the core beyond a number, so all of them are
    /// recorded rather than guessed at - which one holds the biome and which the reward is a
    /// question for real chart data, not for a hunch.
    /// </summary>
    private List<object> SubterraneanChartDump()
    {
        var chart = FindSubterraneanChart();
        if (chart == null)
            return null;

        var result = new List<object>();

        try
        {
            foreach (var bigCell in chart.GridElement.Cells)
            {
                if (bigCell == null)
                    continue;

                var cells = new List<object>();
                try
                {
                    foreach (var cell in bigCell.Cells)
                    {
                        if (cell == null)
                            continue;

                        cells.Add(DescribeChartCell(cell));
                    }
                }
                catch
                {
                    // a row can be unreadable while the chart is scrolling
                }

                object rect = null;
                try
                {
                    var r = bigCell.GetClientRectCache;
                    rect = new { x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height };
                }
                catch
                {
                    // position is not always readable
                }

                result.Add(new { text = SafeText(bigCell), rect, cells });
            }
        }
        catch (Exception ex)
        {
            result.Add(new { error = ex.GetBaseException().Message });
        }

        return result;
    }

    private static object DescribeChartCell(DelveCell cell)
    {
        object info = null;
        try
        {
            if (cell.Info is { } strings)
            {
                info = new
                {
                    strings.Interesting,
                    s0 = strings.TestString,
                    good = strings.TestStringGood,
                    s2 = strings.TestString2,
                    s3 = strings.TestString3,
                    s4 = strings.TestString4,
                    s5 = strings.TestString5,
                };
            }
        }
        catch
        {
            // the info block is not populated for every cell
        }

        object rect = null;
        try
        {
            var r = cell.GetClientRectCache;
            rect = new { x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height };
        }
        catch
        {
            // position is not always readable
        }

        return new
        {
            type = SafeRead(() => cell.Type),
            typeHuman = SafeRead(() => cell.TypeHuman),
            mods = SafeRead(() => cell.Mods),
            mines = SafeRead(() => cell.MinesText),
            text = SafeText(cell),
            info,
            rect,
        };
    }

    private static string SafeText(Element element)
    {
        try
        {
            return element.Text;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeRead(Func<string> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }
}
