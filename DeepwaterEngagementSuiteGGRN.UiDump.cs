using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Snapshots what is on screen while playing, as opposed to what is on the voyage board.
///
/// The board snapshot only fires while the voyage window is open, so pressing the key inside an
/// area did nothing — which is why questions about in-game panels kept going unanswered. This one
/// works anywhere and dumps the visible interface along with the nearby objects, so a panel that
/// needs identifying can be found in the data instead of described from memory.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    /// <summary>How deep to walk the interface tree. Deeper finds more but the file grows fast.</summary>
    private const int UiDumpDepth = 6;

    private void DumpAreaSnapshot()
    {
        var telemetry = Telemetry;
        if (telemetry == null)
            return;

        try
        {
            var payload = new
            {
                area = _currentAreaName,
                player = new { x = _playerGridPos.X, y = _playerGridPos.Y },
                guideCandidates = _lastCandidateCount,
                nearbyObjects = NearbyObjectDump(),
                visibleUi = VisibleUiDump(),
            };

            var path = telemetry.WriteSnapshot("area", payload);
            DebugWindow.LogMsg($"DWS: area snapshot written to {path}");
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"DWS: area snapshot failed: {ex.Message}");
        }
    }

    private List<object> NearbyObjectDump()
    {
        var result = new List<object>();
        foreach (var entity in CandidateEntities())
        {
            try
            {
                var distance = Vector2.Distance(_playerGridPos, entity.GridPosNum);
                if (distance > 150)
                    continue;

                result.Add(new
                {
                    path = entity.Path,
                    distance = Math.Round(distance, 1),
                    entity.IsOpened,
                    entity.IsTargetable,
                    kind = GetChestType(entity.Path).ToString(),
                    mods = entity.GetComponent<ObjectMagicProperties>()?.Mods,
                    rarity = entity.GetComponent<ObjectMagicProperties>()?.Rarity.ToString(),
                    states = entity.TryGetComponent(out StateMachine machine)
                        ? machine.States.Select(x => $"{x.Name}={x.Value}").ToList()
                        : null,
                });
            }
            catch
            {
                // an entity can go invalid mid-walk; the rest of the dump is still useful
            }
        }

        return result;
    }

    /// <summary>Visible interface elements carrying text, which is what identifies a panel.</summary>
    private List<object> VisibleUiDump()
    {
        var result = new List<object>();
        try
        {
            Walk(GameController.IngameState.UIRoot, 0, result);
        }
        catch (Exception ex)
        {
            result.Add($"<error: {ex.GetBaseException().Message}>");
        }

        return result;
    }

    private static void Walk(Element element, int depth, List<object> into)
    {
        if (element == null || depth > UiDumpDepth || into.Count > 400)
            return;

        bool visible;
        try
        {
            visible = element.IsVisible;
        }
        catch
        {
            return;
        }

        if (!visible)
            return;

        try
        {
            var text = element.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                var rect = element.GetClientRectCache;
                into.Add(new
                {
                    depth,
                    text,
                    x = (int)rect.X,
                    y = (int)rect.Y,
                    w = (int)rect.Width,
                    h = (int)rect.Height,
                    address = element.Address.ToString("X"),
                });
            }
        }
        catch
        {
            // text is not readable on every element
        }

        IList<Element> children;
        try
        {
            children = element.Children;
        }
        catch
        {
            return;
        }

        if (children == null)
            return;

        foreach (var child in children)
            Walk(child, depth + 1, into);
    }
}
