using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
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

    private sealed record DelveTarget(
        uint Id,
        Vector2 GridPos,
        float Distance,
        DelveKind Kind,
        int Amount,
        string Label,
        double Score);

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

        if (!path.StartsWith(DelveChestPrefix, StringComparison.Ordinal))
            return new DelveKind(DelveCategory.Unknown, false, false, false, 0);

        var tail = path[DelveChestPrefix.Length..];
        var offPath = tail.StartsWith("OffPath", StringComparison.Ordinal);
        var behindWall = tail.StartsWith("Dynamite", StringComparison.Ordinal);
        var empty = tail.EndsWith("NoDrops", StringComparison.OrdinalIgnoreCase)
                    || (renderName?.Contains("NoDrops", StringComparison.OrdinalIgnoreCase) ?? false);

        // Trailing digits are the tier: the mine numbers a category's chests upwards as they get
        // richer, which is why a Resonator3 is worth crossing the room for and a Resonator1 is not.
        var tier = 0;
        for (var i = tail.Length - 1; i >= 0 && char.IsDigit(tail[i]); i--)
            tier = tail[i] - '0';

        var category = DelveCategory.Unknown;
        if (tail.Contains("AzuriteVein", StringComparison.Ordinal))
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

        if (empty)
            category = DelveCategory.Empty;

        return new DelveKind(category, offPath, behindWall, empty, tier);
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
        var azurite = Settings.DelveSettings.Azurite;

        double value = kind.Category switch
        {
            DelveCategory.Azurite => amount >= azurite.HugeThreshold.Value ? 260
                : amount >= azurite.BigThreshold.Value ? 150
                : 70,
            DelveCategory.Resonator => 60 + 40 * kind.Tier,
            DelveCategory.Fossil => 140,
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

    private static string DelveLabel(DelveKind kind, int amount)
    {
        var name = kind.Category switch
        {
            DelveCategory.Azurite => amount > 0 ? $"Azurite {amount}" : "Azurite",
            DelveCategory.Resonator => kind.Tier > 0 ? $"Resonator T{kind.Tier}" : "Resonator",
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

        foreach (var type in new[]
                 {
                     EntityType.Chest, EntityType.Terrain, EntityType.IngameIcon,
                     EntityType.TriggerableBlockage, EntityType.MiscellaneousObjects, EntityType.None,
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

                    var distance = Vector2.Distance(playerPos, entity.GridPosNum);
                    if (distance > settings.Range.Value)
                        continue;

                    var amount = kind.Category is DelveCategory.Azurite or DelveCategory.Dynamite or DelveCategory.Flares
                        ? ParseDelveAmount(renderName)
                        : 0;

                    if (kind.Category == DelveCategory.Azurite)
                    {
                        if (!settings.Azurite.Enabled.Value)
                            continue;
                        if (amount > 0 && amount < settings.Azurite.IgnoreBelow.Value)
                            continue;
                    }

                    var value = kind.Category == DelveCategory.Wall ? 0 : DelveBaseValue(kind, amount);

                    results.Add(new DelveTarget(
                        entity.Id,
                        entity.GridPosNum,
                        distance,
                        kind,
                        amount,
                        DelveLabel(kind, amount),
                        value / Math.Max(30f, distance)));
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

            var label = target.Kind.Category == DelveCategory.Wall
                ? DelveWallContents(target, targets, settings.Walls.ContentsRadius.Value) is { } contents
                    ? $"WALL -> {contents}"
                    : "WALL"
                : target.Label;

            Graphics.DrawTextWithBackground(label, screen, color, FontAlign.Center, Color.Black);
        }
    }

    /// <summary>The ranked list, so the next thing to walk to is readable without opening the map.</summary>
    private void DrawDelveList(List<DelveTarget> targets, List<DelveTarget> walls, DelveSettings settings)
    {
        var ranked = targets
            .Where(x => x.Kind.Category != DelveCategory.Wall)
            .OrderByDescending(x => x.Score)
            .Take(settings.ListLength.Value)
            .ToList();

        if (ranked.Count == 0)
            return;

        using (Graphics.SetTextScale(settings.FontScale.Value))
        {
            var y = 260f;
            Graphics.DrawTextWithBackground("Mine", new System.Numerics.Vector2(20, y), Color.White, Color.Black);
            y += 24 * settings.FontScale.Value;

            foreach (var target in ranked)
            {
                Graphics.DrawTextWithBackground(
                    $"{target.Label}  {target.Distance:F0}",
                    new System.Numerics.Vector2(20, y),
                    target.Kind.Color,
                    Color.Black);
                y += 22 * settings.FontScale.Value;
            }
        }
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
        if (contents == null && settings.Walls.OnlyWhenLootBehind.Value)
            return;

        var text = contents == null
            ? $"DYNAMITE  {nearest.Distance:F0}"
            : $"DYNAMITE -> {contents}  {nearest.Distance:F0}";

        using (Graphics.SetTextScale(settings.FontScale.Value * 1.3f))
        {
            var width = GameController.Window.GetWindowRectangle().Width;
            Graphics.DrawTextWithBackground(
                text,
                new System.Numerics.Vector2(width / 2f, 190),
                Color.Magenta,
                FontAlign.Center,
                Color.Black);
        }
    }
}
