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
    /// <summary>
    /// Close enough to count as having been at a wall. Generous on purpose: passing the turning that
    /// leads to one is as good as having decided about it, and a line that comes back after you have
    /// already walked on is worse than no line.
    /// </summary>
    private const float DelveWallReachedDistance = 55f;

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
    /// Walls already walked up to.
    ///
    /// A wall has no opened state to read, and a hidden one keeps its hidden flag after it is blown,
    /// so nothing about the wall itself ever says "done with this". Standing next to it does: the
    /// line kept pointing back at a wall whose chests were all emptied because the only thing it was
    /// checking was that the game still called the wall hidden.
    /// </summary>
    private readonly HashSet<uint> _delveVisitedWalls = [];

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

        /// <summary>Worth, on the three steps a decision has. See <see cref="DelveTier"/>.</summary>
        public int Tier { get; init; }
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
            return ClassifyOrdinaryChest(path);

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
    /// A chest outside the mine, read the same way.
    ///
    /// Every league names its chests after what they hold, so the words that decide a mine chest
    /// decide a dungeon chest too - which means a side area is worth walking into or it is not
    /// before the walk, without a table per league.
    /// </summary>
    private static DelveKind ClassifyOrdinaryChest(string path)
    {
        if (!path.StartsWith("Metadata/Chests/", StringComparison.Ordinal))
            return new DelveKind(DelveCategory.Unknown, false, false, false, 0);

        var tail = path["Metadata/Chests/".Length..];

        var tier = 0;
        for (var i = tail.Length - 1; i >= 0 && char.IsDigit(tail[i]); i--)
            tier = tail[i] - '0';

        var category = DelveCategory.Unknown;
        if (tail.Contains("Resonator", StringComparison.OrdinalIgnoreCase))
            category = DelveCategory.Resonator;
        else if (tail.Contains("Fossil", StringComparison.OrdinalIgnoreCase))
            category = DelveCategory.Fossil;
        else if (tail.Contains("Divination", StringComparison.OrdinalIgnoreCase))
            category = DelveCategory.Divination;
        else if (tail.Contains("Currency", StringComparison.OrdinalIgnoreCase))
            category = DelveCategory.Currency;
        else if (tail.Contains("StrongBoxes", StringComparison.OrdinalIgnoreCase))
            category = DelveCategory.Special;
        else if (tail.Contains("Unique", StringComparison.OrdinalIgnoreCase))
            category = DelveCategory.Special;
        else if (tail.Contains("Essence", StringComparison.OrdinalIgnoreCase))
            category = DelveCategory.Divination;
        else if (tail.Contains("Trinket", StringComparison.OrdinalIgnoreCase))
            category = DelveCategory.Trinkets;
        else if (tail.Contains("Gem", StringComparison.OrdinalIgnoreCase))
            category = DelveCategory.Gem;
        else if (tail.Contains("Map", StringComparison.OrdinalIgnoreCase))
            category = DelveCategory.Map;

        // Everything else is a barrel, a vase or a crate. Naming them would be listing scenery.
        return new DelveKind(category, false, false, false, tier);
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

    /// <summary>
    /// How badly this is worth the walk, on the three steps a decision actually has: take it if you
    /// pass it, leave the route for it, or drop what you are doing.
    ///
    /// Colouring by category told you what a thing was, which its own name already says. What is
    /// missing while the cart keeps moving is whether it is worth anything, so the colour says that.
    /// </summary>
    private static int DelveTier(double baseValue) => baseValue >= 150 ? 3 : baseValue >= 90 ? 2 : 1;

    private static Color DelveTierColor(int tier) => tier switch
    {
        3 => Color.Magenta,
        2 => Color.Gold,
        _ => Color.Silver,
    };

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

                    // Walls reach further than anything else. A chest never walked near costs
                    // nothing, but a passage missed for sitting outside the radius is a whole room
                    // never entered - and the game lists it regardless of what the player can see.
                    var distance = Vector2.Distance(playerPos, entity.GridPosNum);
                    var reach = kind.Category == DelveCategory.Wall
                        ? settings.Walls.Range.Value
                        : settings.Range.Value;

                    if (distance > reach)
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
                    var tier = DelveTier(value);

                    results.Add(new DelveTarget(
                        entity.Id,
                        entity.GridPosNum,
                        distance,
                        kind,
                        amount,
                        DelveLabel(kind, amount, renderName),
                        value / Math.Max(30f, distance)) { IconHidden = iconHidden, Tier = tier });
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
    /// Whether a wall is worth a stick of dynamite, and why.
    ///
    /// The mine files a wall and the chest it seals as unrelated objects, but a sealed chest only
    /// ever sits just past its own wall, so proximity recovers the link. The verdict is then the best
    /// thing behind it - one word, because standing in front of a wall the question is not what is
    /// back there, it is whether to stop.
    ///
    /// Nothing found is reported as nothing found rather than as "not worth it". The mine does place
    /// walls with nothing behind them, but a chest can also be out of range or not yet loaded, and
    /// telling those apart matters more than sounding certain.
    /// </summary>
    private static (int Tier, string What) DelveWallVerdict(DelveTarget wall, List<DelveTarget> all, float radius)
    {
        var behind = all
            .Where(x => x.Kind.BehindWall && Vector2.Distance(x.GridPos, wall.GridPos) <= radius)
            .ToList();

        if (behind.Count == 0)
            return (0, null);

        var best = behind.OrderByDescending(x => x.Tier).ThenByDescending(x => x.Kind.Category).First();
        var names = behind
            .OrderByDescending(x => x.Tier)
            .Select(x => DelveLabel(x.Kind, x.Amount).Replace("[wall] ", ""))
            .Distinct()
            .Take(2);

        return (best.Tier, string.Join(", ", names));
    }

    /// <summary>
    /// The one word that answers "do I stop here". Two outcomes, because there are two: you either
    /// spend the dynamite and the time or you keep walking, and a middle grade would only be a
    /// decision handed back.
    /// </summary>
    private static string DelveWallWord(int tier) => tier >= 2 ? "GO" : "skip";

    private static Color DelveWallColor(int tier) => tier switch
    {
        3 => Color.Magenta,
        2 => Color.Gold,
        1 => Color.Silver,
        _ => Color.DimGray,
    };

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
        if (!settings.Enabled.Value)
            return;

        if (!InAzuriteMine())
        {
            // Outside the mine this is a second opinion, not the main one. Where the voyage guide is
            // already ranking the same objects with a profile built for them, two panels disagreeing
            // about the same chest is worse than one.
            if (!settings.Everywhere.Value || Handler != null)
                return;
        }

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

        // MinimapIcons already writes a label on every one of these, and two plugins writing the
        // same word on the same spot is less readable than one.
        if (largeMapOpen && settings.DrawMapLabels.Value)
        {
            if (settings.GroupIntoRooms.Value)
                DrawDelveRooms(playerPos, targets, settings);

            // Always the full list: what a wall is worth is read off the sealed chests around it, so
            // handing this only the walls left every verdict as "nothing found".
            DrawDelveMap(playerPos, targets, walls, settings, settings.GroupIntoRooms.Value);
        }

        DrawDelveSecretGuide(playerPos, walls, targets, settings, largeMapOpen);
        DrawDelveList(targets, walls, settings);
        DrawDelveWallPrompt(walls, targets, settings);
    }



    /// <summary>
    /// Leads to the way through.
    ///
    /// A wall the game does not draw is a route the map is denying exists, so being told the room has
    /// one is only half of it - the other half is which way to walk, and that half is needed while
    /// running rather than while standing still with the map open. So the line is drawn in the world
    /// as well, and it picks the nearest way through rather than the richest: the point of a passage
    /// is that it is the way, and a further one is not a better way.
    /// </summary>
    private void DrawDelveSecretGuide(Vector2 playerPos, List<DelveTarget> walls, List<DelveTarget> all, DelveSettings settings, bool largeMapOpen)
    {
        if (!settings.Walls.GuideToSecret.Value)
            return;

        // Anything this close has been reached, and a wall does not need pointing at twice.
        foreach (var wall in walls.Where(w => w.Distance <= DelveWallReachedDistance))
            _delveVisitedWalls.Add(wall.Id);

        // Only what is still sealed and still worth it. Being a passage the game hides is no longer
        // enough on its own: an emptied one is still hidden, and the line went on pointing at it.
        var target = walls
            .Where(w => !_delveVisitedWalls.Contains(w.Id))
            .Where(w => DelveWallVerdict(w, all, settings.Walls.ContentsRadius.Value).Tier >= settings.Walls.MinimumTier.Value)
            .OrderBy(w => w.Distance)
            .FirstOrDefault();

        if (target == null)
            return;

        // One colour. The line means one thing - there is something worth having through there - and
        // a second colour would be a distinction without a decision behind it.
        var color = Color.Lime;

        if (largeMapOpen)
        {
            Graphics.DrawLine(
                Graphics.GridToMap(playerPos, playerPos),
                Graphics.GridToMap(target.GridPos, playerPos),
                3,
                color);
            return;
        }

        if (!settings.Walls.GuideInWorld.Value)
            return;

        try
        {
            Graphics.DrawLine(
                GetWorldScreenPosition(playerPos),
                GetWorldScreenPosition(target.GridPos),
                3,
                color);
        }
        catch
        {
            // the world projection is not readable during a load
        }
    }

    /// <summary>A cluster of chests close enough together to be one room.</summary>
    private sealed record DelveRoom(Vector2 Centre, int Count, int BestTier, double Value, string Best);

    /// <summary>
    /// Groups chests that stand together into rooms.
    ///
    /// The buried towns are a corridor with two or three rooms off it and a handful of chests in
    /// each. Marking every chest there answers a question nobody has - the loot filter already names
    /// them - while the question actually being asked, standing in the corridor, is whether this room
    /// is worth walking into at all. One mark per room answers that one.
    ///
    /// Plain single-link clustering: chests within reach of each other are the same room. The rooms
    /// are small and far apart, so nothing cleverer earns its keep.
    /// </summary>
    private static List<DelveRoom> GroupDelveRooms(List<DelveTarget> targets, float radius)
    {
        var rooms = new List<DelveRoom>();
        var taken = new bool[targets.Count];

        for (var i = 0; i < targets.Count; i++)
        {
            if (taken[i] || targets[i].Kind.Category == DelveCategory.Wall)
                continue;

            var members = new List<DelveTarget> { targets[i] };
            taken[i] = true;

            // Grow the room until nothing else is within reach of anything already in it.
            for (var grew = true; grew;)
            {
                grew = false;
                for (var j = 0; j < targets.Count; j++)
                {
                    if (taken[j] || targets[j].Kind.Category == DelveCategory.Wall)
                        continue;

                    if (!members.Any(m => Vector2.Distance(m.GridPos, targets[j].GridPos) <= radius))
                        continue;

                    members.Add(targets[j]);
                    taken[j] = true;
                    grew = true;
                }
            }

            var centre = new Vector2(members.Average(m => m.GridPos.X), members.Average(m => m.GridPos.Y));
            var best = members.OrderByDescending(m => m.Tier).First();
            rooms.Add(new DelveRoom(
                centre,
                members.Count,
                members.Max(m => m.Tier),
                members.Sum(m => m.Tier),
                best.Label));
        }

        return rooms;
    }

    /// <summary>
    /// One mark per room: green to walk in, grey to walk past.
    ///
    /// Two colours because there are two answers. The count and the best thing in the room follow, so
    /// the mark can be argued with rather than only obeyed.
    /// </summary>
    private void DrawDelveRooms(Vector2 playerPos, List<DelveTarget> targets, DelveSettings settings)
    {
        foreach (var room in GroupDelveRooms(targets, settings.RoomRadius.Value))
        {
            // Red rather than nothing. A room left unmarked reads as a room not yet found, and the
            // walk in to check costs the same as the walk in to loot it - so a room known to be
            // rubbish is worth saying so about.
            var worth = room.BestTier >= settings.MinimumTier.Value;
            var color = worth ? Color.Lime : Color.Red;
            var screen = Graphics.GridToMap(room.Centre, playerPos);
            var size = worth ? 14f : 10f;

            Graphics.DrawFrame(
                new RectangleF(screen.X - size, screen.Y - size, size * 2, size * 2),
                color,
                worth ? 4 : 2);

            Graphics.DrawTextWithBackground(
                worth ? $"GO  {room.Count}  {room.Best}" : $"NO  {room.Count}",
                new System.Numerics.Vector2(screen.X, screen.Y + size + 2),
                color,
                FontAlign.Center,
                Color.Black);
        }
    }

    private void DrawDelveMap(Vector2 playerPos, List<DelveTarget> targets, List<DelveTarget> walls, DelveSettings settings, bool wallsOnly = false)
    {
        foreach (var target in targets)
        {
            if (wallsOnly && target.Kind.Category != DelveCategory.Wall)
                continue;

            var screen = Graphics.GridToMap(target.GridPos, playerPos);
            var color = target.Kind.Color;

            if (target.Kind.Category == DelveCategory.Wall)
            {
                var (tier, _) = DelveWallVerdict(target, targets, settings.Walls.ContentsRadius.Value);

                // Drawn or not drawn, and nothing in between. A wall that is not worth the dynamite
                // is not worth a label saying so - the answer to "do I stop here" is the absence of
                // a mark. Walls the game itself hides stay marked whatever is behind them, because
                // there the passage is the reward rather than the chest.
                if (tier < settings.Walls.MinimumTier.Value && !target.IconHidden)
                    continue;

                var wallColor = target.IconHidden ? Color.Lime : DelveWallColor(tier);
                Graphics.DrawFrame(
                    new RectangleF(screen.X - 13, screen.Y - 13, 26, 26), wallColor, 3);
                continue;
            }

            if (target.Tier < settings.MinimumTier.Value)
                continue;

            // A mark rather than the name: MinimapIcons writes the name already, and what it cannot
            // say is whether the thing is worth the walk. The ring is the answer to that.
            var tierColor = DelveTierColor(target.Tier);
            var size = 6f + 4f * target.Tier;
            Graphics.DrawFrame(
                new SharpDX.RectangleF(screen.X - size, screen.Y - size, size * 2, size * 2),
                tierColor,
                target.Tier);
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
            .Where(x => x.Tier >= settings.MinimumTier.Value)
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
                ImGui.TextColored(DelveTierColor(target.Tier).ToImguiVec4(), target.Kind.Category.ToString());
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
            var (tier, what) = DelveWallVerdict(wall, targets, settings.Walls.ContentsRadius.Value);
            if (tier < settings.Walls.MinimumTier.Value && !wall.IconHidden)
                continue;

            var head = wall.IconHidden ? "SECRET " : "";
            ImGui.TextColored(
                DelveWallColor(tier).ToImguiVec4(),
                what == null
                    ? $"{head}wall - ?   {wall.Distance:F0}"
                    : $"{head}{DelveWallWord(tier)} - {what}   {wall.Distance:F0}");
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

        var (tier, what) = DelveWallVerdict(nearest, all, settings.Walls.ContentsRadius.Value);

        // A wall the game has not drawn is worth a prompt whether or not its contents can be worked
        // out: the passage itself is the reward.
        if (what == null && settings.Walls.OnlyWhenLootBehind.Value && !nearest.IconHidden)
            return;

        var text = what == null
            ? (nearest.IconHidden ? "SECRET PASSAGE - unknown" : "WALL - nothing found")
            : $"{DelveWallWord(tier)}  {what}";

        if (nearest.IconHidden && what != null)
            text = "SECRET  " + text;

        using (Graphics.SetTextScale(settings.FontScale.Value * 1.3f))
        {
            var width = GameController.Window.GetWindowRectangle().Width;
            Graphics.DrawTextWithBackground(
                text,
                new System.Numerics.Vector2(width / 2f, 190),
                nearest.IconHidden ? Color.Lime : DelveWallColor(tier),
                FontAlign.Center,
                Color.Black);
        }
    }
}
