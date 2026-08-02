using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
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

    /// <summary>
    /// How far to look for objects. The guide only cares about what is close enough to walk to, but
    /// a census wants the whole room — a Delve wall is worth knowing about well before it is in reach.
    /// </summary>
    private const float DumpRadius = 300f;

    /// <summary>Ceiling on the object list so a busy area cannot produce an unreadable file.</summary>
    private const int DumpObjectLimit = 400;

    /// <summary>
    /// Entity kinds that are pure noise in a census: they appear in the hundreds, change every frame
    /// and never carry a decision. Everything else is dumped, because which kind a thing lands in is
    /// exactly what the census is trying to establish.
    /// </summary>
    private static readonly EntityType[] DumpIgnoredTypes =
    [
        EntityType.Effect,
        EntityType.Light,
        EntityType.Daemon,
        EntityType.Player,
        EntityType.Pet,
        EntityType.HideoutDecoration,
    ];

    private void DumpAreaSnapshot()
    {
        var telemetry = Telemetry;
        if (telemetry == null)
            return;

        try
        {
            // Read the player's own position rather than the one the voyage tick caches. That tick
            // returns on its first line when the Deepwater handler is missing, so outside league
            // content the cached position never moves off its starting value and every object in
            // the area measures as too far away — the snapshot came back empty in a mine full of
            // chests.
            var playerPos = PlayerGridPos();

            var payload = new
            {
                area = GameController.Area?.CurrentArea?.DisplayName ?? _currentAreaName,
                player = new { x = playerPos.X, y = playerPos.Y },
                guideCandidates = _lastCandidateCount,
                nearbyObjects = NearbyObjectDump(playerPos),
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

    /// <summary>
    /// Every object around the player, whatever kind the game filed it under.
    ///
    /// This used to reuse the guide's candidate list, which only asks for chests, terrain and ingame
    /// icons because that is all a voyage objective can be. Other content files things elsewhere —
    /// a Delve wall is a TriggerableBlockage, not terrain — so anything the guide does not already
    /// look for was invisible in the very dump meant to discover it.
    /// </summary>
    /// <summary>The player's grid position right now, whatever area this is.</summary>
    private Vector2 PlayerGridPos()
    {
        try
        {
            // Same measure the objects are reported in, so the distances line up by construction.
            if (GameController.Player is { } player)
                return player.GridPosNum;
        }
        catch
        {
            // the player entity is not readable during a load
        }

        return _playerGridPos;
    }

    private List<object> NearbyObjectDump(Vector2 playerPos)
    {
        var result = new List<(float Distance, object Record)>();
        var seen = new HashSet<uint>();

        foreach (var type in Enum.GetValues<EntityType>())
        {
            if (DumpIgnoredTypes.Contains(type) || result.Count >= DumpObjectLimit)
                continue;

            List<Entity> entities;
            try
            {
                entities = GameController.EntityListWrapper.ValidEntitiesByType[type].ToList();
            }
            catch
            {
                // the entity list can be mid-swap, and not every kind is populated
                continue;
            }

            foreach (var entity in entities)
            {
                if (result.Count >= DumpObjectLimit)
                    break;

                try
                {
                    if (!seen.Add(entity.Id))
                        continue;

                    var distance = Vector2.Distance(playerPos, entity.GridPosNum);
                    if (distance > DumpRadius)
                        continue;

                    result.Add((distance, DescribeEntity(entity, type, distance)));
                }
                catch
                {
                    // an entity can go invalid mid-walk; the rest of the dump is still useful
                }
            }
        }

        // Nearest first, so reading the file top-down matches walking towards the objects.
        return result.OrderBy(x => x.Distance).Select(x => x.Record).ToList();
    }

    private object DescribeEntity(Entity entity, EntityType type, float distance)
    {
        // Read through one at a time: a component the entity does not carry throws rather than
        // returning null, and losing the whole record over one missing field defeats the census.
        string renderName = null;
        try
        {
            renderName = entity.GetComponent<Render>()?.Name;
        }
        catch
        {
            // not every object is rendered
        }

        object minimapIcon = null;
        try
        {
            if (entity.TryGetComponent(out MinimapIcon icon))
                minimapIcon = new { name = icon.Name, hidden = icon.IsHide, visible = icon.IsVisible };
        }
        catch
        {
            // the icon component is optional and its strings are not always readable
        }

        return new
        {
            type = type.ToString(),
            path = entity.Path,
            renderName,
            distance = Math.Round(distance, 1),
            entity.IsOpened,
            entity.IsTargetable,
            league = entity.League.ToString(),
            minimapIcon,
            kind = GetChestType(entity.Path).ToString(),
            mods = entity.GetComponent<ObjectMagicProperties>()?.Mods,
            rarity = entity.GetComponent<ObjectMagicProperties>()?.Rarity.ToString(),
            states = entity.TryGetComponent(out StateMachine machine)
                ? machine.States.Select(x => $"{x.Name}={x.Value}").ToList()
                : null,
        };
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
