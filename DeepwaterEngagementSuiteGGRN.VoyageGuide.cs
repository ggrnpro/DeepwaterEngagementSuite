using System;
using System.Collections.Generic;
using System.Linq;
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
/// The objects come from the server's own list of the voyage's static entities, not from the client
/// entity cache. The cache only holds what is loaded around the player and only retires what it can
/// still see, so it both forgot to drop looted objects and knew nothing about the far side of the
/// map. The server list has neither problem: it covers the whole voyage and reports each object's
/// opened state directly, which is what makes it possible to point at a room worth crossing to
/// rather than only at what happens to be underfoot.
///
/// Two rules decide the order. Golden Lanterns come first regardless of distance: they raise
/// quantity and rarity for the rest of the run, so one taken early buffs everything after it and
/// one taken last buffs nothing. Everything else ranks by value per unit of walking.
///
/// The chosen target then sticks until it is reached or something is clearly better, because value
/// over distance reorders on almost every step while moving, and a marker that jumps cannot be
/// followed.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    /// <summary>How much better a rival must be before the guide abandons its current target.</summary>
    private const double TargetSwitchMargin = 1.35;

    /// <summary>Within this distance the target counts as reached and the guide moves on.</summary>
    private const float TargetArrivalDistance = 25f;

    private uint? _currentTargetId;

    private void ResetGuide() => _currentTargetId = null;

    /// <summary>
    /// Value of an object type on a scale where a plain currency chest is 100. Zero means the guide
    /// does not send you there — the Allflame Capsule is the obvious case: you know where it is and
    /// a line to it is just noise.
    /// </summary>
    private static double ObjectiveValue(IconPickerIndex type) => type switch
    {
        // Ranked ahead of everything while the lantern-first rule is on. The value below only
        // matters when it is off, and is a guess at what one is worth against a chest.
        IconPickerIndex.GoldenLanternEncounter => 90,
        IconPickerIndex.CurrencyTreasureChestOpulent => 200,
        IconPickerIndex.CurrencyTreasureChest => 100,
        IconPickerIndex.StrongboxArcanist => 95,
        IconPickerIndex.CurrencyGemcuttersChest => 90,
        IconPickerIndex.StackedDecksChest => 85,
        IconPickerIndex.StrongboxDivination => 85,
        IconPickerIndex.ScarabChest => 80,
        IconPickerIndex.StrongboxScarab => 75,
        IconPickerIndex.InfusedCoralEncounter => 70,
        IconPickerIndex.UniqueWeaponChest => 55,
        IconPickerIndex.UniqueArmourChest => 55,
        IconPickerIndex.CursedDucatDrop => 45,
        IconPickerIndex.RandomDucatChest => 45,
        IconPickerIndex.IzaroObject => 45,
        IconPickerIndex.AllflameEmbersChest => 40,
        IconPickerIndex.MapsChest => 30,
        IconPickerIndex.AltarCrab => 30,
        IconPickerIndex.AltarOctopus => 30,
        IconPickerIndex.ClamTreasureChest => 25,
        IconPickerIndex.TormentedSpiritEncounter => 25,
        IconPickerIndex.BottledItemChest => 20,
        IconPickerIndex.GoldTreasureChest => 15,
        _ => 0,
    };

    private readonly record struct GuideTarget(uint Id, IconPickerIndex Type, Vector2 GridPos, double Value)
    {
        public bool IsLantern => Type == IconPickerIndex.GoldenLanternEncounter;
    }

    /// <summary>Everything in the voyage still worth visiting, according to the server.</summary>
    private List<GuideTarget> GuideTargets()
    {
        var maxDistance = Settings.VoyageSettings.GuideMaxDistance.Value;
        var targets = new List<GuideTarget>();

        List<Entity> statics;
        try
        {
            statics = Handler?.StaticEntities;
        }
        catch
        {
            return targets;
        }

        if (statics == null)
            return targets;

        foreach (var entity in statics)
        {
            bool skip;
            try
            {
                skip = entity is not { IsValid: true } || entity.IsOpened;
            }
            catch
            {
                continue;
            }

            if (skip)
                continue;

            var type = GetChestType(entity.Path);
            var value = ObjectiveValue(type);
            if (value <= 0)
                continue;

            Vector2 pos;
            try
            {
                pos = entity.GridPosNum;
            }
            catch
            {
                continue;
            }

            if (maxDistance > 0 && Vector2.Distance(_playerGridPos, pos) > maxDistance)
                continue;

            targets.Add(new GuideTarget(entity.Id, type, pos, value));
        }

        return targets;
    }

    /// <summary>Lanterns first in shortest-route order, then everything else by value per step.</summary>
    private List<GuideTarget> RankTargets(List<GuideTarget> targets)
    {
        if (!Settings.VoyageSettings.PrioritiseLanterns.Value)
            return targets.OrderByDescending(Density).ToList();

        var lanterns = targets.Where(x => x.IsLantern).ToList();
        var rest = targets.Where(x => !x.IsLantern).ToList();

        var ranked = new List<GuideTarget>(targets.Count);
        if (lanterns.Count > 0)
        {
            var order = PlanPickupOrder(_playerGridPos, lanterns.Select(x => x.GridPos).ToList());
            ranked.AddRange(order.Select(i => lanterns[i]));
        }

        ranked.AddRange(rest.OrderByDescending(Density));
        return ranked;
    }

    private double Density(GuideTarget target) =>
        target.Value / Math.Max(TargetArrivalDistance, Vector2.Distance(_playerGridPos, target.GridPos));

    /// <summary>
    /// Picks what to walk to, preferring to stay on the current target. Ranking by value over
    /// distance reorders constantly while moving, so without this the line flicks between objects
    /// several times a second.
    /// </summary>
    private GuideTarget? ChooseTarget(List<GuideTarget> ranked)
    {
        if (ranked.Count == 0)
        {
            _currentTargetId = null;
            return null;
        }

        var leader = ranked[0];

        if (_currentTargetId is { } currentId && ranked.Any(x => x.Id == currentId))
        {
            var current = ranked.First(x => x.Id == currentId);
            var arrived = Vector2.Distance(_playerGridPos, current.GridPos) <= TargetArrivalDistance;

            // A lantern outranks anything that is not one, so never hold a chest in front of one.
            var leaderOutclasses = leader.IsLantern && !current.IsLantern;

            if (!arrived && !leaderOutclasses && Density(leader) < Density(current) * TargetSwitchMargin)
                return current;
        }

        _currentTargetId = leader.Id;
        return leader;
    }

    private void DrawObjectiveGuide()
    {
        var settings = Settings.VoyageSettings;
        if (!settings.ShowObjectiveGuide.Value)
            return;

        var ranked = RankTargets(GuideTargets());
        if (ChooseTarget(ranked) is not { } next)
            return;

        var color = next.IsLantern ? settings.LanternRouteColor.Value : settings.GuideColor.Value;
        var from = GetWorldScreenPosition(_playerGridPos);
        var to = GetWorldScreenPosition(next.GridPos);

        Graphics.DrawLine(from, to, settings.LanternRouteWidth.Value + 1, color);

        var distance = Vector2.Distance(_playerGridPos, next.GridPos);
        Graphics.DrawTextWithBackground(
            $"{GetEntityDisplayName(next.Type)}  {distance:F0}", to, color, FontAlign.Center, Color.Black);

        // Off-screen targets get a label pinned near the player, since the end of the line is
        // somewhere past the edge of the window and the line alone gives no idea how far.
        if (distance > 120)
        {
            var direction = to - from;
            if (direction.LengthSquared() > 1)
            {
                direction /= direction.Length();
                Graphics.DrawTextWithBackground(
                    $"{GetEntityDisplayName(next.Type)} {distance:F0} away",
                    from + direction * 90f, color, FontAlign.Center, Color.Black);
            }
        }

        if (!settings.ShowObjectiveList.Value)
            return;

        if (!ImGui.Begin("Voyage Guide", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var lanternsLeft = ranked.Count(x => x.IsLantern);
        if (lanternsLeft > 0)
        {
            ImGui.TextColored(settings.LanternRouteColor.Value.ToImguiVec4(),
                $"{lanternsLeft} Golden Lantern(s) still out - take these first, they buff the rest of the run.");
        }

        if (ImGui.BeginTable("guide", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 24);
            ImGui.TableSetupColumn("Target");
            ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            foreach (var (target, index) in ranked.Take(settings.GuideListLength.Value).Select((x, i) => (x, i)))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(target.Id == _currentTargetId ? ">" : $"{index + 1}");
                ImGui.TableNextColumn();
                if (target.IsLantern)
                    ImGui.TextColored(settings.LanternRouteColor.Value.ToImguiVec4(), GetEntityDisplayName(target.Type));
                else
                    ImGui.Text(GetEntityDisplayName(target.Type));
                ImGui.TableNextColumn();
                ImGui.Text($"{Vector2.Distance(_playerGridPos, target.GridPos):F0}");
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }
}
