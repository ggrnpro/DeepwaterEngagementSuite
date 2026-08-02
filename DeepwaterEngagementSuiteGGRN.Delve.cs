using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Ranks what is worth taking in the Azurite Mine.
///
/// The mine hands out its answers in the object path: a chest is on the cart's lit route, off it in
/// the dark, or sealed behind a wall, and its category is spelled out in the same string. Existing
/// plugins draw an icon per chest and stop there, which leaves the actual question — is this one
/// worth leaving the light for, is this wall worth a stick of dynamite — to be answered by eye
/// while the cart keeps moving.
///
/// Azurite is graded rather than merely marked, because a vein's size is the difference between a
/// detour that pays and one that does not, and the size is written on the vein itself.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    private const string DelveChestPrefix = "Metadata/Chests/DelveChests/";

    /// <summary>Matched loosely: the wall's own path has moved between leagues, its place in it has not.</summary>
    private const string DelveWallMarker = "Delve/Objects/DelveWall";

    /// <summary>
    /// Pulls the number out of a vein's rendered name. The mine writes it as displayed text wrapped
    /// in colour markup, so the digits are read out rather than taken from a stat.
    /// </summary>
    private static readonly Regex DelveAmountPattern = new(@"(\d[\d,\s]*)", RegexOptions.Compiled);

    private readonly Dictionary<uint, string> _delveNameCache = new();

    /// <summary>
    /// Every distinct vein and fossil the mine has shown, kept so the grades can be checked against
    /// what actually turns up rather than against a table written from memory. The wiki is behind a
    /// bot wall and the grade is a word, not a number, so this is the only honest source for it.
    /// </summary>
    private readonly Dictionary<string, string> _delveSeenNames = new(StringComparer.Ordinal);

    /// <summary>Set when a name is seen for the first time; the next snapshot carries the list.</summary>
    private bool _delveNamesDirty;

    /// <summary>
    /// How rich a fossil is, on a scale the mine does not provide. The type is written into the
    /// chest's path, and which types are worth stopping for is a market question rather than a game
    /// one, so this is a starting table meant to be corrected against real drops - anything not
    /// listed lands in the middle and gets recorded.
    /// </summary>
    private static readonly Dictionary<string, int> FossilGrades = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Faceted"] = 3,
        ["Bloodstained"] = 3,
        ["Sanctified"] = 3,
        ["Fractured"] = 3,
        ["Glyphic"] = 3,
        ["Tangled"] = 3,
        ["Hollow"] = 3,
        ["Shuddering"] = 2,
        ["Enchanted"] = 2,
        ["Encrusted"] = 2,
        ["Prismatic"] = 2,
        ["Gilded"] = 2,
        ["Bound"] = 2,
        ["Perfect"] = 2,
        ["Deft"] = 1,
        ["Dense"] = 1,
        ["Aetheric"] = 1,
        ["Serrated"] = 1,
        ["Metallic"] = 1,
        ["Jagged"] = 1,
        ["Corroded"] = 1,
        ["Pristine"] = 1,
        ["Lucent"] = 1,
        ["Scorched"] = 1,
        ["Frigid"] = 1,
        ["Aberrant"] = 1,
    };

    private sealed record DelveTarget(
        uint Id,
        Vector2 GridPos,
        float Distance,
        DelveKind Kind,
        int Amount,
        string Label,
        double Score)
    {
        /// <summary>
        /// The game is holding this object's own map icon back.
        ///
        /// For a wall that is the whole point: the mine seals some passages behind walls it does not
        /// draw until you are almost touching them, which is why a route that exists reads as a dead
        /// end. Those are the ones worth being told about from across the room.
        /// </summary>
        public bool IconHidden { get; init; }
    }

    /// <summary>
    /// The mine names itself "Azurite Mine" and appends the monster level, so the name is matched by
    /// its start. Nothing narrower is used: an area id or act number is one league rename away from
    /// switching the whole feature off silently.
    /// </summary>
    private bool InAzuriteMine()
    {
        try
        {
            return GameController.Area?.CurrentArea?.DisplayName?.StartsWith("Azurite Mine", StringComparison.Ordinal) ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads an object's path into what it is and how it is reached.
    ///
    /// "Path" and "OffPath" are the mine's own words for on and off the cart's lit route, "Dynamite"
    /// marks what is sealed behind a wall, and a name ending in NoDrops is a decoy that opens onto
    /// nothing. All three change the decision, so all three are kept rather than flattened into a
    /// single category.
    /// </summary>
    private static DelveKind ClassifyDelveObject(string path, string renderName)
    {
        if (string.IsNullOrEmpty(path))
            return new DelveKind(DelveCategory.Unknown, false, false, false, 0);

        if (path.Contains(DelveWallMarker, StringComparison.Ordinal))
            return new DelveKind(DelveCategory.Wall, false, false, false, 0);

        // Loose azurite lying on the floor is terrain rather than a chest, and it turns up in
        // clusters of ten, so it is free azurite that no icon points at.
        if (path.Contains("DelveAzuriteShard", StringComparison.Ordinal))
            return new DelveKind(DelveCategory.AzuriteShard, false, false, false, 1);

        if (!path.StartsWith(DelveChestPrefix, StringComparison.Ordinal))
            return new DelveKind(DelveCategory.Unknown, false, false, false, 0);

        var tail = path[DelveChestPrefix.Length..];
        var offPath = tail.StartsWith("OffPath", StringComparison.Ordinal);

        // "Dynamite" marks what is sealed behind a wall, and it is not always the start of the name:
        // a fossil behind a wall is a DenseFossilChestDynamite, so matching only the prefix left the
        // richest sealed chests looking like ordinary ones sitting in the open. The supply crate that
        // hands out dynamite carries the same word and is the one thing that is not sealed by it.
        var supplies = tail.Contains("MiningSupplies", StringComparison.Ordinal);
        var behindWall = !supplies && tail.Contains("Dynamite", StringComparison.Ordinal);
        var empty = tail.EndsWith("NoDrops", StringComparison.OrdinalIgnoreCase)
                    || (renderName?.Contains("NoDrops", StringComparison.OrdinalIgnoreCase) ?? false);

        // Trailing digits are the tier: the mine numbers a category's chests upwards as they get
        // richer, which is why a Resonator3 is worth crossing the room for and a Resonator1 is not.
        var tier = 0;
        for (var i = tail.Length - 1; i >= 0 && char.IsDigit(tail[i]); i--)
            tier = tail[i] - '0';

        var category = DelveCategory.Unknown;
        // The chambers the mine calls "Special" are the reward rooms of a Vaal outpost or an abyssal
        // city, and their chests name their contents the same way an ordinary chest does - so
        // matching the contents first filed the richest chest in the mine as plain armour.
        if (tail.Contains("DelveChestSpecial", StringComparison.Ordinal))
            category = DelveCategory.Special;
        else if (tail.Contains("AzuriteVein", StringComparison.Ordinal))
            category = DelveCategory.Azurite;
        else if (tail.Contains("Resonator", StringComparison.Ordinal))
            category = DelveCategory.Resonator;
        else if (tail.Contains("Fossil", StringComparison.Ordinal))
            category = DelveCategory.Fossil;
        else if (tail.Contains("Divination", StringComparison.Ordinal))
            category = DelveCategory.Divination;
        else if (tail.Contains("Currency", StringComparison.Ordinal))
            category = DelveCategory.Currency;
        else if (tail.Contains("SuppliesDynamite", StringComparison.Ordinal))
            category = DelveCategory.Dynamite;
        else if (tail.Contains("Flares", StringComparison.Ordinal))
            category = DelveCategory.Flares;
        else if (tail.Contains("Trinkets", StringComparison.Ordinal))
            category = DelveCategory.Trinkets;
        else if (tail.Contains("Gem", StringComparison.Ordinal))
            category = DelveCategory.Gem;
        else if (tail.Contains("Map", StringComparison.Ordinal))
            category = DelveCategory.Map;
        else if (tail.Contains("Weapon", StringComparison.Ordinal))
            category = DelveCategory.Weapon;
        else if (tail.Contains("Armour", StringComparison.Ordinal))
            category = DelveCategory.Armour;
        else if (tail.Contains("Generic", StringComparison.Ordinal))
            category = DelveCategory.Generic;

        // A vein grades on the last number in its path, not the first: DelveAzuriteVein1_1 renders as
        // "Flawed Azurite Vein" and 1_2 as a plain "Azurite Vein", so reading the number straight
        // after the name gave both of them the same grade and the richer one never stood out.
        if (category == DelveCategory.Azurite)
            tier = Math.Max(1, tier);
        else if (category == DelveCategory.Fossil)
            tier = FossilGrade(tail);

        if (empty)
            category = DelveCategory.Empty;

        return new DelveKind(category, offPath, behindWall, empty, tier);
    }


    /// <summary>
    /// Grades a fossil chest from the fossil named in its path. Unknown types land in the middle
    /// rather than at either end, so a fossil added by a new league is neither hidden nor promoted
    /// over one that is actually worth stopping for.
    /// </summary>
    private static int FossilGrade(string tail)
    {
        var at = tail.IndexOf("Fossil", StringComparison.Ordinal);
        if (at <= 0)
            return 2;

        var name = tail[..at];
        return FossilGrades.TryGetValue(name, out var grade) ? grade : 2;
    }

    /// <summary>
    /// The number written on a vein or a supply crate, or zero when it carries none.
    ///
    /// The mine wraps it in colour markup — "&lt;rgb(175,238,238)&gt;{350}" — and thousands are
    /// separated, so the digits are pulled out and the separators dropped rather than parsing the
    /// string whole.
    /// </summary>
    private static int ParseDelveAmount(string renderName)
    {
        if (string.IsNullOrEmpty(renderName))
            return 0;

        var best = 0;
        foreach (Match match in DelveAmountPattern.Matches(renderName))
        {
            var digits = new string(match.Value.Where(char.IsDigit).ToArray());
            if (digits.Length > 0
                && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && value > best)
            {
                best = value;
            }
        }

        return best;
    }

    /// <summary>
    /// How badly this is worth walking to, before distance is considered.
    ///
    /// Azurite is scored off its actual size rather than a flat number for the category, because the
    /// whole point of grading veins is that a small one and a large one are not the same decision.
    /// </summary>
    private double DelveBaseValue(DelveKind kind, int amount)
    {
        double value = kind.Category switch
        {
            // A vein's grade is the whole decision, so it drives the value directly. The amount is
            // still added when the mine happens to print one, which it does not for veins.
            DelveCategory.Azurite => 60 + 90 * Math.Max(0, kind.Tier - 1) + amount * 0.1,
            DelveCategory.Special => 220,
            DelveCategory.AzuriteShard => 45,
            DelveCategory.Resonator => 60 + 40 * kind.Tier,
            DelveCategory.Fossil => 60 + 60 * Math.Max(0, kind.Tier),
            DelveCategory.Divination => 120,
            DelveCategory.Currency => 110,
            DelveCategory.Dynamite => 80,
            DelveCategory.Flares => 60,
            DelveCategory.Map => 45,
            DelveCategory.Trinkets => 40,
            DelveCategory.Gem => 35,
            DelveCategory.Weapon => 25,
            DelveCategory.Armour => 25,
            DelveCategory.Generic => 20,
            DelveCategory.Empty => 0,
            _ => 10,
        };

        // A chest numbered upwards is a richer version of the same chest.
        if (kind.Category != DelveCategory.Azurite && kind.Category != DelveCategory.Resonator && kind.Tier > 1)
            value += 10 * (kind.Tier - 1);

        // Off the lit route costs darkness, and behind a wall costs a stick of dynamite. Neither
        // rules the object out; both make it a worse deal than the same thing sitting on the path.
        if (kind.OffPath)
            value *= 0.75;
        if (kind.BehindWall)
            value *= 0.85;

        return value;
    }

    private static string DelveLabel(DelveKind kind, int amount, string renderName = null)
    {
        // A vein or a fossil already names its own grade better than any category word could, so its
        // own name is used where the mine gives one.
        if (kind.Category is DelveCategory.Azurite or DelveCategory.Fossil && !string.IsNullOrEmpty(renderName))
        {
            var own = $"{renderName} [{kind.Tier}]";
            return kind.BehindWall ? "[wall] " + own : kind.OffPath ? "[dark] " + own : own;
        }

        var name = kind.Category switch
        {
            DelveCategory.Azurite => amount > 0 ? $"Azurite {amount}" : "Azurite",
            DelveCategory.AzuriteShard => "Azurite shard",
            DelveCategory.Resonator => kind.Tier > 0 ? $"Resonator T{kind.Tier}" : "Resonator",
            DelveCategory.Special => "SPECIAL",
            DelveCategory.Dynamite => "Dynamite",
            DelveCategory.Flares => "Flares",
            DelveCategory.Empty => "empty",
            DelveCategory.Wall => "WALL",
            _ => kind.Category.ToString(),
        };

        if (kind.BehindWall)
            name = "[wall] " + name;
        else if (kind.OffPath)
            name = "[dark] " + name;

        return name;
    }

    private string DelveRenderName(Entity entity)
    {
        if (_delveNameCache.TryGetValue(entity.Id, out var cached))
            return cached;

        string name = null;
        try
        {
            name = entity.GetComponent<Render>()?.Name;
        }
        catch
        {
            // not every object exposes a readable name
        }

        // The mine writes names as markup; the braces and the colour tag are not part of the answer.
        if (!string.IsNullOrEmpty(name))
            name = Regex.Replace(name, @"<[^>]*>|[{}]", "").Trim();

        _delveNameCache[entity.Id] = name ?? "";
        return _delveNameCache[entity.Id];
    }

    private List<DelveTarget> CollectDelveTargets(Vector2 playerPos)
    {
        var settings = Settings.DelveSettings;
        var results = new List<DelveTarget>();
        var seen = new HashSet<uint>();

        // Only the kinds the mine actually files its loot and its walls under. The census showed
        // MiscellaneousObjects alone running to thirteen hundred entries in a room, and classifying
        // every one of them each frame buys nothing: chests are Chest, walls are IngameIcon.
        foreach (var type in new[]
                 {
                     EntityType.Chest, EntityType.IngameIcon, EntityType.Terrain,
                     EntityType.TriggerableBlockage,
                 })
        {
            List<Entity> entities;
            try
            {
                entities = GameController.EntityListWrapper.ValidEntitiesByType[type].ToList();
            }
            catch
            {
                continue;
            }

            foreach (var entity in entities)
            {
                try
                {
                    if (!seen.Add(entity.Id) || !entity.IsValid)
                        continue;

                    var path = entity.Path;
                    if (string.IsNullOrEmpty(path))
                        continue;

                    // Classify from the path before touching the name. The sweep sees every
                    // projectile in the room, and reading a render component plus running a regex
                    // over each of them, every frame, costs far more than the two string compares
                    // that rule them out.
                    var kind = ClassifyDelveObject(path, null);
                    if (kind.Category == DelveCategory.Unknown)
                        continue;

                    var renderName = DelveRenderName(entity);
                    if (!kind.Empty && renderName.Contains("NoDrops", StringComparison.OrdinalIgnoreCase))
                        kind = kind with { Category = DelveCategory.Empty, Empty = true };

                    // An opened chest is spent. A wall stays interesting until it is gone from the
                    // entity list entirely, because it has no opened state to read.
                    if (kind.Category != DelveCategory.Wall && entity.IsOpened)
                        continue;

                    if (kind.Category == DelveCategory.Wall && !settings.Walls.Enabled.Value)
                        continue;

                    if (kind.Empty && settings.HideEmpty.Value)
                        continue;

                    if (kind.Category == DelveCategory.Generic && settings.HideGeneric.Value)
                        continue;

                    var distance = Vector2.Distance(playerPos, entity.GridPosNum);
                    if (distance > settings.Range.Value)
                        continue;

                    var amount = kind.Category is DelveCategory.Azurite or DelveCategory.Dynamite or DelveCategory.Flares
                        ? ParseDelveAmount(renderName)
                        : 0;

                    // Record what the mine calls each vein and fossil. The grade is a word, and the
                    // only trustworthy list of those words is the one the mine actually shows.
                    if (kind.Category is DelveCategory.Azurite or DelveCategory.Fossil
                        && renderName.Length > 0
                        && !_delveSeenNames.ContainsKey(renderName))
                    {
                        _delveSeenNames[renderName] = $"{path} tier={kind.Tier}";
                        _delveNamesDirty = true;
                    }

                    if (kind.Category == DelveCategory.Azurite)
                    {
                        if (!settings.Azurite.Enabled.Value || kind.Tier < settings.Azurite.MinimumGrade.Value)
                            continue;
                    }
                    else if (kind.Category == DelveCategory.Fossil
                             && kind.Tier < settings.Azurite.MinimumFossilGrade.Value)
                    {
                        continue;
                    }

                    // A wall the game has not drawn yet is a passage you would walk past. Reading the
                    // icon's own hidden flag is what turns those from invisible into the first thing
                    // on the list; MinimapIcons force-shows this same icon for the same reason.
                    var iconHidden = false;
                    if (kind.Category == DelveCategory.Wall)
                    {
                        try
                        {
                            if (entity.TryGetComponent(out MinimapIcon icon))
                                iconHidden = icon.IsHide;
                        }
                        catch
                        {
                            // the icon component is optional and not always readable
                        }

                        if (iconHidden && !settings.Walls.ShowUndiscovered.Value)
                            continue;
                    }

                    var value = kind.Category == DelveCategory.Wall ? 0 : DelveBaseValue(kind, amount);

                    results.Add(new DelveTarget(
                        entity.Id,
                        entity.GridPosNum,
                        distance,
                        kind,
                        amount,
                        DelveLabel(kind, amount, renderName),
                        value / Math.Max(30f, distance)) { IconHidden = iconHidden });
                }
                catch
                {
                    // an entity can go invalid mid-walk; the rest of the sweep is still useful
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Names what a wall is hiding, by looking for sealed chests close to it.
    ///
    /// The mine files a wall and the chest behind it as separate objects with no link between them,
    /// so the only thing tying them together is that a "Dynamite" chest sits just past its own wall
    /// and nowhere else. Where that fails the wall is still drawn, just without a promise.
    /// </summary>
    private static string DelveWallContents(DelveTarget wall, List<DelveTarget> all, float radius)
    {
        var behind = all
            .Where(x => x.Kind.BehindWall && Vector2.Distance(x.GridPos, wall.GridPos) <= radius)
            .OrderByDescending(x => x.Kind.Category)
            .ToList();

        if (behind.Count == 0)
            return null;

        var names = behind.Select(x => DelveLabel(x.Kind, x.Amount).Replace("[wall] ", "")).Distinct().Take(3);
        return string.Join(", ", names);
    }

    private void DrawDelveOverlay()
    {
        var settings = Settings.DelveSettings;
        if (!settings.Enabled.Value || !InAzuriteMine())
            return;

        // The chart is a full-screen panel over the same area, and the overlay's own labels were
        // being painted on top of it - a chest called "Generic" behind the panel read as a node type
        // on it. Nothing in the area is worth marking while the map of where to go next is open.
        if (FindSubterraneanChart() != null)
            return;

        bool largeMapOpen;
        try
        {
            largeMapOpen = GameController.Game.IngameState.IngameUi.Map.LargeMap.AsObject<SubMap>().IsVisible;
        }
        catch
        {
            largeMapOpen = false;
        }

        var playerPos = PlayerGridPos();
        var targets = CollectDelveTargets(playerPos);
        if (targets.Count == 0)
            return;

        var walls = targets.Where(x => x.Kind.Category == DelveCategory.Wall).ToList();

        if (largeMapOpen)
            DrawDelveMap(playerPos, targets, walls, settings);

        DrawDelveList(targets, walls, settings);
        DrawDelveWallPrompt(walls, targets, settings);
    }

    private void DrawDelveMap(Vector2 playerPos, List<DelveTarget> targets, List<DelveTarget> walls, DelveSettings settings)
    {
        foreach (var target in targets)
        {
            var screen = Graphics.GridToMap(target.GridPos, playerPos);
            var color = target.Kind.Color;

            string label;
            if (target.Kind.Category == DelveCategory.Wall)
            {
                var contents = DelveWallContents(target, targets, settings.Walls.ContentsRadius.Value);
                var head = target.IconHidden ? "SECRET WALL" : "WALL";
                label = contents == null ? head : $"{head} -> {contents}";

                // An undiscovered wall is the one the map is lying about, so it does not get to look
                // like the walls the game already drew.
                if (target.IconHidden)
                    color = Color.Lime;
            }
            else
            {
                label = target.Label;
            }

            Graphics.DrawTextWithBackground(label, screen, color, FontAlign.Center, Color.Black);
        }
    }

    /// <summary>
    /// The ranked list, so the next thing to walk to is readable without opening the map.
    ///
    /// Drawn as its own window rather than as text painted at a fixed corner: the fixed version
    /// landed underneath the voyage panel and could not be moved, resized or found.
    /// </summary>
    private void DrawDelveList(List<DelveTarget> targets, List<DelveTarget> walls, DelveSettings settings)
    {
        var ranked = targets
            .Where(x => x.Kind.Category != DelveCategory.Wall)
            .OrderByDescending(x => x.Score)
            .Take(settings.ListLength.Value)
            .ToList();

        if (ranked.Count == 0 && walls.Count == 0)
            return;

        ImGui.SetNextWindowBgAlpha(0.85f);
        if (!ImGui.Begin("Mine", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        ImGui.SetWindowFontScale(settings.FontScale.Value);

        if (ImGui.BeginTable("mine", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            foreach (var target in ranked)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(target.Kind.Color.ToImguiVec4(), target.Kind.Category.ToString());
                ImGui.TableNextColumn();
                ImGui.Text(target.Label);
                ImGui.TableNextColumn();
                ImGui.Text($"{target.Distance:F0}");
            }

            ImGui.EndTable();
        }

        // Undiscovered walls first: they are the ones that change where you would have walked.
        foreach (var wall in walls.OrderByDescending(x => x.IconHidden).ThenBy(x => x.Distance))
        {
            var contents = DelveWallContents(wall, targets, settings.Walls.ContentsRadius.Value);
            var head = wall.IconHidden ? "SECRET wall" : "wall";
            ImGui.TextColored(
                (wall.IconHidden ? Color.Lime : Color.Magenta).ToImguiVec4(),
                contents == null
                    ? $"{head}  {wall.Distance:F0}"
                    : $"{head} -> {contents}   {wall.Distance:F0}");
        }

        ImGui.SetWindowFontScale(1f);
        ImGui.End();
    }

    /// <summary>
    /// The reminder to throw, shown only once a wall is close enough that throwing is the next thing
    /// you would do. Naming what is behind it turns the reminder into a decision rather than a nag.
    /// </summary>
    private void DrawDelveWallPrompt(List<DelveTarget> walls, List<DelveTarget> all, DelveSettings settings)
    {
        if (!settings.Walls.Enabled.Value || walls.Count == 0)
            return;

        var nearest = walls.OrderBy(x => x.Distance).First();
        if (nearest.Distance > settings.Walls.PromptDistance.Value)
            return;

        var contents = DelveWallContents(nearest, all, settings.Walls.ContentsRadius.Value);

        // A wall the game has not drawn is worth a prompt whether or not its contents can be worked
        // out: the passage itself is the reward.
        if (contents == null && settings.Walls.OnlyWhenLootBehind.Value && !nearest.IconHidden)
            return;

        var head = nearest.IconHidden ? "DYNAMITE - SECRET PASSAGE" : "DYNAMITE";
        var text = contents == null
            ? $"{head}  {nearest.Distance:F0}"
            : $"{head} -> {contents}  {nearest.Distance:F0}";

        using (Graphics.SetTextScale(settings.FontScale.Value * 1.3f))
        {
            var width = GameController.Window.GetWindowRectangle().Width;
            Graphics.DrawTextWithBackground(
                text,
                new System.Numerics.Vector2(width / 2f, 190),
                nearest.IconHidden ? Color.Lime : Color.Magenta,
                FontAlign.Center,
                Color.Black);
        }
    }
}
