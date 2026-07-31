using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace DeepwaterEngagementSuiteGGRN;

public partial class DeepwaterEngagementSuiteGGRN
{
    private readonly Dictionary<uint, TrailCacheItem> _trailMarkers = new();
    private readonly List<Vector2> _trailPointerTargets = [];
    private readonly List<Vector2> _completedTrailTargets = [];

    private record TrailCacheItem(
        IconPickerIndex Type,
        Vector2 GridPos,
        int MissingTicks = 0);

    private void ResetTrailTracking()
    {
        _trailMarkers.Clear();
        _trailPointerTargets.Clear();
        _completedTrailTargets.Clear();
    }

    private void TrackTrailEntity(Entity entity)
    {
        if (!Settings.TrailSettings.Enabled ||
            GetEntityType(entity.Path) == ExpeditionEntityType.None)
        {
            return;
        }

        var type = GetChestType(entity.Path);
        var gridPos = entity.PosNum.WorldToGrid();
        if (IsEntityCompleted(entity, type))
        {
            _trailMarkers.Remove(entity.Id);
            RememberCompletedTrailTarget(gridPos);
            return;
        }

        var completedAtPosition = _completedTrailTargets
            .Where(x => Vector2.Distance(x, gridPos) <= 5)
            .ToList();
        foreach (var completed in completedAtPosition)
        {
            _completedTrailTargets.Remove(completed);
        }

        _trailMarkers[entity.Id] = new TrailCacheItem(type, gridPos);
    }

    private void UpdateTrailTracking()
    {
        if (!Settings.TrailSettings.Enabled)
        {
            ResetTrailTracking();
            return;
        }

        var rawPointerTargets = ReadRawPointerTargets();
        var seenIds = new HashSet<uint>();

        foreach (var entity in new[] { EntityType.Chest, EntityType.Terrain, EntityType.IngameIcon }
                     .SelectMany(x => GameController.EntityListWrapper.ValidEntitiesByType[x]))
        {
            if (GetEntityType(entity.Path) == ExpeditionEntityType.None)
            {
                continue;
            }

            seenIds.Add(entity.Id);
            TrackTrailEntity(entity);
        }

        foreach (var missing in _trailMarkers
                     .Where(x => !seenIds.Contains(x.Key))
                     .ToList())
        {
            if (!DisappearsWhenConsumed(missing.Value.Type))
            {
                if (missing.Value.MissingTicks != 0)
                {
                    _trailMarkers[missing.Key] = missing.Value with { MissingTicks = 0 };
                }

                continue;
            }

            // A surviving pointer means the encounter was merely unloaded
            // because the player moved away. Preserve the known type and place.
            if (rawPointerTargets.Any(x => Vector2.Distance(x, missing.Value.GridPos) <= 20))
            {
                if (missing.Value.MissingTicks != 0)
                {
                    _trailMarkers[missing.Key] = missing.Value with { MissingTicks = 0 };
                }

                continue;
            }

            var missingTicks = missing.Value.MissingTicks + 1;
            if (missingTicks < 5)
            {
                _trailMarkers[missing.Key] = missing.Value with { MissingTicks = missingTicks };
                continue;
            }

            RememberCompletedTrailTarget(missing.Value.GridPos);
            _trailMarkers.Remove(missing.Key);
        }

        _trailPointerTargets.Clear();
        foreach (var target in rawPointerTargets)
        {
            if (_completedTrailTargets.Any(x => Vector2.Distance(x, target) <= 5) ||
                _trailMarkers.Values.Any(x => Vector2.Distance(x.GridPos, target) <= 20) ||
                _trailPointerTargets.Any(x => Vector2.Distance(x, target) <= 1))
            {
                continue;
            }

            _trailPointerTargets.Add(target);
        }
    }

    private List<Vector2> ReadRawPointerTargets()
    {
        var result = new List<Vector2>();
        foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Terrain]
                     .Where(x => x.Path == "Metadata/Terrain/Leagues/Deepwater/Objects/Pointer"))
        {
            if (!entity.TryGetComponent(out Pointer pointer))
            {
                continue;
            }

            foreach (var target in pointer.Targets)
            {
                if (!result.Any(x => Vector2.Distance(x, target) <= 1))
                {
                    result.Add(target);
                }
            }
        }

        return result;
    }

    private static bool DisappearsWhenConsumed(IconPickerIndex type) => type is
        IconPickerIndex.IzaroObject or
        IconPickerIndex.AltarCrab or
        IconPickerIndex.AltarOctopus or
        IconPickerIndex.TormentedSpiritEncounter or
        IconPickerIndex.LanternReplenishEncounter or
        IconPickerIndex.GoldenLanternEncounter or
        IconPickerIndex.InfusedCoralEncounter;

    private void RememberCompletedTrailTarget(Vector2 gridPos)
    {
        if (!_completedTrailTargets.Any(x => Vector2.Distance(x, gridPos) <= 5))
        {
            _completedTrailTargets.Add(gridPos);
        }
    }

    private void RenderTrailOverlay(bool largePanelsOpen)
    {
        if (!Settings.TrailSettings.Enabled ||
            largePanelsOpen)
        {
            return;
        }

        var largeMapVisible = _largeMapOpen;
        var drawMap = Settings.TrailSettings.DrawOnLargeMap && largeMapVisible;
        var drawWorld = Settings.TrailSettings.DrawInWorld && !largeMapVisible;
        if (!drawMap && !drawWorld)
        {
            return;
        }

        var maxDistance = Settings.TrailSettings.MaxDistance.Value;
        var maxDistanceSquared = maxDistance * maxDistance;
        var markers = _trailMarkers.Values
            .Select(x => (Type: x.Type, GridPos: x.GridPos))
            .Concat(_trailPointerTargets.Select(x => (Type: IconPickerIndex.PointerTarget, GridPos: x)));

        foreach (var marker in markers)
        {
            if (!IsTrailTypeEnabled(marker.Type))
            {
                continue;
            }

            var delta = marker.GridPos - _playerGridPos;
            if (delta.LengthSquared() > maxDistanceSquared)
            {
                continue;
            }

            if (Settings.TrailSettings.OnlyUnreachable &&
                IsEntityInBubble(marker.GridPos))
            {
                continue;
            }

            var isPointer = marker.Type == IconPickerIndex.PointerTarget;
            var mapColor = isPointer
                ? Settings.TrailSettings.UndiscoveredColor.Value
                : GetTrailColor(marker.Type, Settings.TrailSettings.DefaultMapColor.Value);
            var worldColor = isPointer
                ? Settings.TrailSettings.UndiscoveredColor.Value
                : GetTrailColor(marker.Type, Settings.TrailSettings.DefaultWorldColor.Value);
            var label = GetEntityDisplayName(marker.Type);

            if (drawMap)
            {
                var from = Graphics.GridToMap(_playerGridPos, _playerGridPos);
                var to = Graphics.GridToMap(marker.GridPos, _playerGridPos);
                Graphics.DrawLine(from, to, Settings.TrailSettings.MapLineWidth, mapColor);
                if (Settings.TrailSettings.ShowLabels)
                {
                    Graphics.DrawTextWithBackground(label, (from + to) * 0.5f, mapColor, FontAlign.Center, Color.Black);
                }
            }

            if (drawWorld)
            {
                var from = Camera.WorldToScreen(ExpandWithTerrainHeight(_playerGridPos));
                var to = Camera.WorldToScreen(ExpandWithTerrainHeight(marker.GridPos));
                Graphics.DrawLine(from, to, Settings.TrailSettings.WorldLineWidth, worldColor);
                if (Settings.TrailSettings.ShowLabels)
                {
                    Graphics.DrawTextWithBackground(label, (from + to) * 0.5f, worldColor, FontAlign.Center, Color.Black);
                }
            }
        }
    }

    private Color GetTrailColor(IconPickerIndex type, Color fallback)
    {
        return Settings.TrailSettings.Colors.Get(type, fallback);
    }

    private bool IsTrailTypeEnabled(IconPickerIndex type)
    {
        return type == IconPickerIndex.PointerTarget
            ? Settings.TrailSettings.ShowUndiscoveredTargets.Value
            : Settings.TrailSettings.Colors.IsEnabled(type);
    }
}
