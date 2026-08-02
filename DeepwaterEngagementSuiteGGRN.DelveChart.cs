using System;
using System.Collections.Generic;
using System.Linq;
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
    /// The chart, from the interface's own named handle.
    ///
    /// Walking the tree and asking each element whether it was the chart looked reasonable and was
    /// wrong: reinterpreting an element as another type does not fail, it just reads whatever is at
    /// those offsets, so the walk accepted the first element whose garbage happened to look like a
    /// grid and returned twenty-four cells that were all empty. The interface names this window, so
    /// there is nothing to search for.
    /// </summary>
    private SubterraneanChart FindSubterraneanChart()
    {
        try
        {
            var window = GameController.IngameState.IngameUi.DelveWindow;
            return window is { IsValid: true, IsVisible: true } ? window : null;
        }
        catch
        {
            return null;
        }
    }


    /// <summary>
    /// The chart's node elements, found by the shape of the tree rather than by the typed accessor.
    ///
    /// <c>GridElement</c> comes back empty in this build - its offset has moved - but the nodes are
    /// still there and still laid out the way a grid is: a handful of large square tiles, each with
    /// dozens of equally sized small children. Nothing else in the panel looks like that, so the
    /// shape identifies it without depending on an offset that has already drifted once.
    ///
    /// Reading an element as a DelveCell is only meaningful once the right element is in hand, which
    /// is the whole reason this is done by shape first and cast second.
    /// </summary>
    private static List<Element> FindChartNodeElements(Element chart)
    {
        var best = new List<Element>();
        Collect(chart, 0);
        return best;

        void Collect(Element element, int depth)
        {
            if (element == null || depth > 6)
                return;

            IList<Element> children;
            try
            {
                children = element.Children;
            }
            catch
            {
                return;
            }

            if (children == null || children.Count == 0)
                return;

            // A grid tile: square, large, and full of equally sized small children.
            var tiles = new List<Element>();
            foreach (var child in children)
            {
                try
                {
                    var r = child.GetClientRectCache;
                    if (r.Width > 300 && Math.Abs(r.Width - r.Height) < 2 && child.ChildCount >= 16)
                        tiles.Add(child);
                }
                catch
                {
                    // an element can go unreadable while the panel animates
                }
            }

            if (tiles.Count > 0)
            {
                var nodes = new List<Element>();
                foreach (var tile in tiles)
                {
                    try
                    {
                        foreach (var node in tile.Children)
                        {
                            var r = node.GetClientRectCache;
                            if (r.Width > 8 && Math.Abs(r.Width - r.Height) < 2)
                                nodes.Add(node);
                        }
                    }
                    catch
                    {
                        // a tile can be unreadable while the chart scrolls
                    }
                }

                if (nodes.Count > best.Count)
                    best = nodes;

                return;
            }

            foreach (var child in children)
                Collect(child, depth + 1);
        }
    }

    /// <summary>
    /// The chart window's raw child tree, recorded when the typed grid comes back empty.
    ///
    /// The interface dump only keeps elements carrying text and a node is an icon, so the grid is
    /// absent from it entirely — which is why an empty result looked the same as a missing panel.
    /// The nodes are still elements with positions, so their shape gives them away: a row of equally
    /// sized children where the typed accessor found nothing means the accessor's offset has moved,
    /// not that the chart is empty.
    /// </summary>
    private static List<object> ChartSubtreeDump(Element element, int depth)
    {
        var result = new List<object>();
        if (element == null || depth > 6)
            return result;

        IList<Element> children;
        try
        {
            children = element.Children;
        }
        catch
        {
            return result;
        }

        if (children == null)
            return result;

        foreach (var child in children.Take(60))
        {
            try
            {
                var r = child.GetClientRectCache;
                result.Add(new
                {
                    depth,
                    childCount = child.ChildCount,
                    x = (int)r.X,
                    y = (int)r.Y,
                    w = (int)r.Width,
                    h = (int)r.Height,
                    text = SafeText(child),
                    children = ChartSubtreeDump(child, depth + 1),
                });
            }
            catch
            {
                // an element can go unreadable while the panel animates
            }
        }

        return result;
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

        // Shape first: the typed grid accessor is empty in this build.
        foreach (var node in FindChartNodeElements(chart))
        {
            try
            {
                result.Add(DescribeChartCell(node.AsObject<DelveCell>()));
            }
            catch
            {
                // a node can go unreadable mid-walk
            }
        }

        if (result.Count > 0)
            return result;

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

        // An empty grid means the typed accessor missed, not that the chart has no nodes. Record the
        // window's own children so the grid can be found by its shape instead.
        if (result.Count == 0)
            result.Add(new { note = "grid accessor returned nothing - raw subtree follows", subtree = ChartSubtreeDump(chart, 0) });

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
