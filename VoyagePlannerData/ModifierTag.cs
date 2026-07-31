using System;
using System.Collections.Concurrent;

namespace DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

/// <summary>
/// Reward categories used to decide which border multipliers actually apply to which chart
/// modifiers. A border only multiplies a chart modifier's weight when they share at least one
/// tag (or the border is tagged <see cref="All"/>).
/// </summary>
[Flags]
public enum ModifierTag
{
    None = 0,
    Monsters = 1 << 0,
    MagicMonsters = 1 << 1,
    RareMonsters = 1 << 2,
    Essences = 1 << 3,
    Strongboxes = 1 << 4,
    Uniques = 1 << 5,
    Currency = 1 << 6,
    Scarabs = 1 << 7,
    Gold = 1 << 8,
    Equipment = 1 << 9,
    Experience = 1 << 10,
    Resources = 1 << 11,
    Lanterns = 1 << 12,
    Rarity = 1 << 13,

    All = Monsters | MagicMonsters | RareMonsters | Essences | Strongboxes | Uniques | Currency |
          Scarabs | Gold | Equipment | Experience | Resources | Lanterns | Rarity,
}

public static class ModifierTagParser
{
    private static readonly ConcurrentDictionary<string, ModifierTag?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a comma-separated tag list ("Monsters, RareMonsters"). Unknown tokens are ignored.
    /// Returns <paramref name="emptyFallback"/> when the text is empty or contains no valid token,
    /// so old profiles without tags keep their legacy behavior (borders: All, chart mods: None).
    /// </summary>
    public static ModifierTag Parse(string text, ModifierTag emptyFallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return emptyFallback;

        return Cache.GetOrAdd(text, ParseUncached) ?? emptyFallback;
    }

    private static ModifierTag? ParseUncached(string text)
    {
        ModifierTag result = default;
        var anyParsed = false;
        foreach (var rawToken in text.Split(','))
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
                continue;

            if (Enum.TryParse<ModifierTag>(token, true, out var parsed))
            {
                result |= parsed;
                anyParsed = true;
            }
        }

        return anyParsed ? result : null;
    }

    /// <summary>
    /// Whether a border with <paramref name="borderTags"/> applies to a chart modifier with
    /// <paramref name="modifierTags"/>. An All-tagged border matches everything, including
    /// untagged (None) modifiers.
    /// </summary>
    public static bool Matches(ModifierTag borderTags, ModifierTag modifierTags)
    {
        return borderTags == ModifierTag.All || (borderTags & modifierTags) != ModifierTag.None;
    }
}
