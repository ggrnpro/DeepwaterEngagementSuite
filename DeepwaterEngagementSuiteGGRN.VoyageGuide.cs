using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using SharpDX;
using Color = SharpDX.Color;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Tells you where to go next inside a voyage.
///
/// Two rules decide the order. Golden Lanterns come first regardless of distance, because they
/// raise quantity and rarity for the rest of the run and are therefore worth more the earlier they
/// are taken — a lantern grabbed at the end buffs nothing. Everything else is ranked by value per
/// unit of walking, so a rich chest across the room beats a poor one underfoot but not by so much
/// that you cross the map for scraps.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    private readonly Dictionary<uint, GuideObjective> _objectives = new();

    private sealed record GuideObjective(uint Id, Entity Entity, string Name, double Value, bool IsMultiplier)
    {
        public Vector2 GridPos => Entity.GridPosNum;
    }

    private void TrackObjective(Entity entity)
    {
        if (entity == null || !Settings.VoyageSettings.ShowObjectiveGuide.Value)
            return;

        foreach (var candidate in VoyageObjectiveCatalog.Defaults)
        {
            if (!entity.Path.Contains(candidate.PathFragment, StringComparison.OrdinalIgnoreCase))
                continue;

            _objectives[entity.Id] = new GuideObjective(
                entity.Id, entity, candidate.Name, candidate.Value, candidate.IsMultiplier);

            // The state names differ per object type and are not documented, so record them the
            // first time each kind is seen rather than assuming the list above is complete.
            if (Settings.VoyageSettings.EnableDebugDump)
                Telemetry?.NoteModRecord("entity-states:" + entity.Path, () => DescribeObjectiveStates(entity));

            return;
        }
    }

    /// <summary>
    /// Whether an objective has already been taken.
    ///
    /// Chests report it on their Chest component, but a good half of what is worth walking to in a
    /// voyage — anchors, encounter spawners, ducat drops — are terrain objects with no Chest at
    /// all, and checking only the component left the guide pointing at things already looted. The
    /// entity's own flag and its state machine cover those.
    /// </summary>
    private static bool IsObjectiveTaken(Entity entity)
    {
        if (entity.IsOpened)
            return true;

        if (entity.TryGetComponent(out Chest chest) && chest.IsOpened)
            return true;

        if (entity.TryGetComponent(out StateMachine stateMachine))
        {
            foreach (var state in stateMachine.States)
            {
                if (state.Value != 1)
                    continue;

                if (state.Name is "activated" or "opened" or "used" or "collected" or "finished" or "complete")
                    return true;
            }
        }

        return false;
    }

    private void ForgetObjective(Entity entity)
    {
        if (entity != null)
            _objectives.Remove(entity.Id);
    }

    private static object DescribeObjectiveStates(Entity entity)
    {
        try
        {
            return new
            {
                path = entity.Path,
                isOpened = entity.IsOpened,
                hasChest = entity.TryGetComponent<Chest>(out _),
                states = entity.TryGetComponent(out StateMachine sm)
                    ? sm.States.Select(x => $"{x.Name}={x.Value}").ToList()
                    : null,
            };
        }
        catch (Exception ex)
        {
            return $"<error: {ex.GetBaseException().Message}>";
        }
    }

    /// <summary>Objectives still worth visiting, dropping anything opened or gone.</summary>
    private List<GuideObjective> LiveObjectives()
    {
        var live = new List<GuideObjective>();
        var stale = new List<uint>();

        foreach (var objective in _objectives.Values)
        {
            bool gone;
            try
            {
                gone = objective.Entity is not { IsValid: true } || IsObjectiveTaken(objective.Entity);
            }
            catch
            {
                gone = true;
            }

            if (gone)
                stale.Add(objective.Id);
            else
                live.Add(objective);
        }

        foreach (var id in stale)
            _objectives.Remove(id);

        return live;
    }

    /// <summary>
    /// Ranks objectives: every lantern first in shortest-route order, then the rest by value per
    /// unit of distance.
    /// </summary>
    private List<GuideObjective> RankObjectives(List<GuideObjective> live)
    {
        var maxDistance = Settings.VoyageSettings.GuideMaxDistance.Value;
        var inRange = maxDistance <= 0
            ? live
            : live.Where(x => Vector2.Distance(_playerGridPos, x.GridPos) <= maxDistance).ToList();

        var multipliers = inRange.Where(x => x.IsMultiplier).ToList();
        var rest = inRange.Where(x => !x.IsMultiplier).ToList();

        var ranked = new List<GuideObjective>();
        if (multipliers.Count > 0)
        {
            var order = PlanPickupOrder(_playerGridPos, multipliers.Select(x => x.GridPos).ToList());
            ranked.AddRange(order.Select(i => multipliers[i]));
        }

        ranked.AddRange(rest
            .OrderByDescending(x => x.Value / Math.Max(20f, Vector2.Distance(_playerGridPos, x.GridPos))));

        return ranked;
    }

    private void DrawObjectiveGuide()
    {
        var settings = Settings.VoyageSettings;
        if (!settings.ShowObjectiveGuide.Value)
            return;

        var ranked = RankObjectives(LiveObjectives());
        if (ranked.Count == 0)
            return;

        var next = ranked[0];
        var from = GetWorldScreenPosition(_playerGridPos);
        var to = GetWorldScreenPosition(next.GridPos);
        var color = next.IsMultiplier
            ? settings.LanternRouteColor.Value
            : settings.GuideColor.Value;

        Graphics.DrawLine(from, to, settings.LanternRouteWidth.Value + 1, color);
        Graphics.DrawTextWithBackground(
            $"{next.Name}  {Vector2.Distance(_playerGridPos, next.GridPos):F0}",
            to, color, FontAlign.Center, Color.Black);

        if (!settings.ShowObjectiveList.Value)
            return;

        if (!ImGui.Begin("Voyage Guide", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var lanternsLeft = ranked.Count(x => x.IsMultiplier);
        if (lanternsLeft > 0)
        {
            ImGui.TextColored(settings.LanternRouteColor.Value.ToImguiVec4(),
                $"{lanternsLeft} Golden Lantern(s) still out — take these first, they buff the rest of the run.");
        }

        if (ImGui.BeginTable("guide", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 24);
            ImGui.TableSetupColumn("Target");
            ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            foreach (var (objective, index) in ranked.Take(settings.GuideListLength.Value).Select((x, i) => (x, i)))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text($"{index + 1}");
                ImGui.TableNextColumn();
                if (objective.IsMultiplier)
                    ImGui.TextColored(settings.LanternRouteColor.Value.ToImguiVec4(), objective.Name);
                else
                    ImGui.Text(objective.Name);
                ImGui.TableNextColumn();
                ImGui.Text($"{Vector2.Distance(_playerGridPos, objective.GridPos):F0}");
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }
}
