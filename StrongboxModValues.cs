using System;
using System.Collections.Generic;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// What each strongbox modifier is worth to a run chasing currency.
///
/// The ids here were read off real boxes rather than guessed. The earlier attempt tried to total the
/// item quantity and rarity stats a modifier grants and produced zero on every box, because that is
/// not where a strongbox's value sits: a Diviner's box is worth opening for the cards it adds, and a
/// Scarab box for the scarabs, not for a quantity roll.
///
/// Values are on one scale where a plain item-quantity roll is 10. They rank modifiers against each
/// other; they are not currency amounts.
/// </summary>
public static class StrongboxModValues
{
    private static readonly Dictionary<string, double> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // The reason to open a box of that type at all.
        ["ChestDropsAddedCardsThatGiveCurrency"] = 40,
        ["ChestDropsAddedCardsThatGiveUniques"] = 35,
        ["ChestDropsAddedCardsThatGivesCorruptedItems"] = 20,
        ["ChestDropsAdditionalEssenceScarabs"] = 30,
        ["ChestDropsAdditionalFragments"] = 20,
        ["ChestAllScarabsSameType"] = 10,

        // Broad multipliers on whatever the box holds.
        ["ChestItemQuantity"] = 10,
        ["ChestExtraRareItems"] = 12,
        ["ChestItemRarity"] = 6,
        ["ChestExtraNormalItems"] = 3,
        ["ChestItemQuality"] = 2,
        ["ChestItemLevel"] = 2,

        // Danger and flavour. They cost time, not currency, and a build that clears anything does
        // not care - so they score zero rather than negative.
        ["ChestExplodeCorpses"] = 0,
        ["ChestSummonSkeletons"] = 0,
        ["ChestSummonMagics"] = 0,
        ["ChestSummonRares"] = 0,
        ["ChestSpawnRogueExile"] = 0,
        ["ChestLightningStorm"] = 0,
        ["ChestIceNova"] = 0,
        ["ChestIgnite"] = 0,
        ["ChestFreeze"] = 0,
        ["ChestPoisonCloud"] = 0,
        ["ChestReviveMonsters"] = 0,
    };

    /// <summary>
    /// Value of a modifier, falling back to what its name says when the id is not in the table.
    /// New league modifiers appear without warning, and a box full of unknown ids should not read as
    /// worthless.
    /// </summary>
    public static double ValueOf(string id, out bool known)
    {
        known = false;
        if (string.IsNullOrEmpty(id))
            return 0;

        if (Known.TryGetValue(id, out var value))
        {
            known = true;
            return value;
        }

        // Anything that adds drops is worth something even unrecognised; anything that only makes
        // the fight harder is not.
        if (id.Contains("DropsAdded", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("DropsAdditional", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Extra", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Quantity", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Rarity", StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }

        return 0;
    }

    /// <summary>A short human-readable name, since the ids read well enough once split up.</summary>
    public static string Describe(string id)
    {
        if (string.IsNullOrEmpty(id))
            return "";

        var trimmed = id.StartsWith("Chest", StringComparison.Ordinal) ? id["Chest".Length..] : id;
        var text = new System.Text.StringBuilder(trimmed.Length + 8);
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (i > 0 && char.IsUpper(trimmed[i]) && !char.IsUpper(trimmed[i - 1]))
                text.Append(' ');

            text.Append(trimmed[i]);
        }

        return text.ToString();
    }
}
