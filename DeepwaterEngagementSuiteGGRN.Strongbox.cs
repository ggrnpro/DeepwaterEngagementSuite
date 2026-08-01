using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;
using Color = SharpDX.Color;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Tells you whether to open a strongbox or reroll it, and with what.
///
/// A strongbox is an item: it has a rarity and modifiers, and currency applies to it the same way
/// it applies to gear. What those modifiers are worth is not a matter of opinion — each one grants
/// stats the game publishes, so the box's total item quantity and rarity can be read rather than
/// guessed. Comparing that total against every box seen so far turns "is this good?" into a
/// question with an answer.
///
/// Which orb to use follows from the rarity, since that is what each orb acts on. The judgement of
/// whether a reroll is worth making is what the measurement supplies.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    private sealed class StrongboxRecord
    {
        public string Timestamp { get; set; }
        public string Kind { get; set; }
        public string Rarity { get; set; }
        public double Quantity { get; set; }
        public double Rarity_ { get; set; }
    }

    private List<StrongboxRecord> _strongboxHistory;
    private string _strongboxHistoryPath;

    private string StrongboxHistoryPath =>
        _strongboxHistoryPath ??= Path.Combine(ConfigDirectory, "strongboxes.json");

    private List<StrongboxRecord> StrongboxHistory
    {
        get
        {
            if (_strongboxHistory != null)
                return _strongboxHistory;

            try
            {
                _strongboxHistory = File.Exists(StrongboxHistoryPath)
                    ? JsonConvert.DeserializeObject<List<StrongboxRecord>>(File.ReadAllText(StrongboxHistoryPath)) ?? []
                    : [];
            }
            catch
            {
                _strongboxHistory = [];
            }

            return _strongboxHistory;
        }
    }

    private readonly HashSet<long> _strongboxesRecorded = [];

    private readonly record struct StrongboxReading(
        string Kind,
        MonsterRarity Rarity,
        double Score,
        int UnknownMods,
        List<string> Mods,
        float Distance);

    private static bool IsStrongbox(IconPickerIndex type) => type is
        IconPickerIndex.StrongboxArcanist or
        IconPickerIndex.StrongboxDivination or
        IconPickerIndex.StrongboxScarab or
        IconPickerIndex.OtherChests;

    /// <summary>
    /// Reads what a strongbox's modifiers actually grant. The mod names come off the entity, the
    /// stats each one carries come from the game's own modifier table, so the totals here are the
    /// same ones the tooltip is built from.
    /// </summary>
    private StrongboxReading? ReadStrongbox(Entity entity)
    {
        try
        {
            if (entity is not { IsValid: true } || entity.IsOpened)
                return null;

            if (entity.Path is not { } path || !path.Contains("StrongBoxes/", StringComparison.OrdinalIgnoreCase))
                return null;

            var magic = entity.GetComponent<ObjectMagicProperties>();
            if (magic == null)
                return null;

            // The modifier ids are what carry the meaning. Reading stat names off them was tried
            // and produced zero on every box, because a Diviner box is worth opening for the cards
            // it adds and not for a quantity roll. Duplicates appear in the list as a box is
            // rerolled, so only distinct ids count.
            var mods = (magic.Mods ?? []).Where(x => x != null).Distinct().ToList();
            double score = 0;
            var unknown = 0;
            foreach (var mod in mods)
            {
                score += StrongboxModValues.ValueOf(mod, out var known);
                if (!known)
                {
                    unknown++;
                    if (Settings.VoyageSettings.EnableDebugDump)
                        Telemetry?.NoteUnknown("strongboxMod", mod);
                }
            }

            return new StrongboxReading(
                GetEntityDisplayName(GetChestType(entity.Path)),
                magic.Rarity,
                score,
                unknown,
                mods,
                Vector2.Distance(_playerGridPos, entity.GridPosNum));
        }
        catch
        {
            return null;
        }
    }

    private static string TrimStrongboxPrefix(string mod) =>
        mod?.StartsWith("Chest", StringComparison.Ordinal) == true ? mod["Chest".Length..] : mod;

    private StrongboxReading? NearestStrongbox()
    {
        StrongboxReading? best = null;
        foreach (var entity in CandidateEntities())
        {
            if (ReadStrongbox(entity) is not { } reading)
                continue;

            if (reading.Distance > Settings.VoyageSettings.StrongboxRange.Value)
                continue;

            if (best is null || reading.Distance < best.Value.Distance)
                best = reading;
        }

        return best;
    }

    private void RecordStrongbox(StrongboxReading reading, long key)
    {
        if (!_strongboxesRecorded.Add(key))
            return;

        StrongboxHistory.Add(new StrongboxRecord
        {
            Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Kind = reading.Kind,
            Rarity = reading.Rarity.ToString(),
            Quantity = reading.Score,
            Rarity_ = 0,
        });

        if (StrongboxHistory.Count > 1000)
            StrongboxHistory.RemoveRange(0, StrongboxHistory.Count - 1000);

        try
        {
            File.WriteAllText(StrongboxHistoryPath, JsonConvert.SerializeObject(StrongboxHistory, Formatting.Indented));
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"DWS: could not save strongbox history: {ex.Message}");
        }
    }

    private void DrawStrongboxAdvice()
    {
        if (!Settings.VoyageSettings.ShowStrongboxAdvice.Value)
            return;

        if (NearestStrongbox() is not { } box)
            return;

        RecordStrongbox(box, (long)box.Score * 1000 + box.Mods.Count * 7 + box.Kind.GetHashCode());

        if (!ImGui.Begin("Strongbox", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        ImGui.SetWindowFontScale(Settings.VoyageSettings.OverlayFontScale.Value);

        ImGui.Text($"{box.Kind}  ({box.Rarity})   {box.Distance:F0} away   score {box.Score:F0}");

        foreach (var mod in box.Mods)
        {
            var value = StrongboxModValues.ValueOf(mod, out _);
            if (value > 0)
                ImGui.TextColored(Color.Lime.ToImguiVec4(), $"  +{value:F0}  {StrongboxModValues.Describe(mod)}");
            else
                ImGui.TextDisabled($"       {StrongboxModValues.Describe(mod)}");
        }

        if (box.UnknownMods > 0)
            ImGui.TextDisabled($"{box.UnknownMods} modifier(s) not in the value table yet - recorded.");

        var comparable = StrongboxHistory
            .Where(x => x.Kind == box.Kind)
            .Select(x => x.Quantity)
            .ToList();

        double? percentile = null;
        if (comparable.Count >= 5)
        {
            percentile = 100.0 * comparable.Count(x => x < box.Score) / comparable.Count;
            ImGui.Text($"Better than {percentile:F0}% of the {comparable.Count} boxes of this type you have seen.");
        }
        else
        {
            ImGui.TextDisabled($"Only {comparable.Count} boxes of this type recorded - open a few more before " +
                               "this can be compared against anything.");
        }

        ImGui.Separator();
        var (verdict, colour) = StrongboxVerdict(box, percentile);
        ImGui.TextColored(colour.ToImguiVec4(), verdict);

        ImGui.End();
    }

    /// <summary>
    /// What to do with the box. Which orb applies is decided by its rarity, since that is what each
    /// orb acts on; whether a reroll is worth it is decided by how this box compares to the others.
    /// </summary>
    private (string Verdict, Color Colour) StrongboxVerdict(StrongboxReading box, double? percentile)
    {
        var threshold = Settings.VoyageSettings.StrongboxRerollBelowPercentile.Value;

        switch (box.Rarity)
        {
            case MonsterRarity.White:
                return ("ALCHEMY - it is still Normal, so an Orb of Alchemy makes it Rare with a full set of modifiers.",
                    Color.Lime);

            case MonsterRarity.Magic:
                return ("REGAL - Magic caps at two modifiers. A Regal Orb makes it Rare and keeps what it has; " +
                        "an Alteration Orb rerolls it instead if these two are poor.", Color.Lime);

            case MonsterRarity.Rare:
                if (percentile is null)
                    return ("Open it. Not enough boxes recorded yet to say whether this roll is worth a Chaos Orb.",
                        Color.Gray);

                return percentile < threshold
                    ? ($"CHAOS - this roll is in the bottom {threshold:F0}% of the boxes of this type you have seen.", Color.Lime)
                    : ($"KEEP - this roll beats {percentile:F0}% of them. Open it.", Color.Orange);

            default:
                return ("Unique box - currency will not improve it. Open it.", Color.Orange);
        }
    }
}
