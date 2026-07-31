using System;
using System.Collections.Generic;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// What is worth walking to inside a voyage, and how much.
///
/// The entity vocabulary here was taken from a census of a real run rather than guessed, so the
/// paths match what the game actually spawns. Values are on one scale where a plain currency chest
/// is 100; they are starting estimates meant to be edited once runs show what each thing really
/// drops.
///
/// Golden Lanterns are deliberately not ranked by value. They raise item quantity and rarity for
/// the rest of the run, so their worth depends on how much voyage is left — the guide always sends
/// you to them first rather than pricing them against a chest.
/// </summary>
public static class VoyageObjectiveCatalog
{
    public readonly record struct Objective(string PathFragment, string Name, double Value, bool IsMultiplier);

    public static IReadOnlyList<Objective> Defaults { get; } =
    [
        // Collect these before anything else: they scale everything found afterwards.
        new("Objects/DeepwaterGoldenLantern", "Golden Lantern", 0, true),

        // Currency.
        new("LeagueDeepwater/CurrencyTreasureChest", "Currency Chest", 100, false),
        new("LeagueDeepwater/CurrencyGemcuttersChest", "Gemcutter's Chest", 90, false),
        new("LeagueDeepwater/DeepwaterChestStackedDecks", "Stacked Decks Chest", 85, false),
        new("LeagueDeepwater/DeepwaterChestScarabs", "Scarab Chest", 80, false),
        new("StrongBoxes/Arcanist", "Arcanist's Strongbox", 95, false),
        new("StrongBoxes/StrongboxDivination", "Diviner's Strongbox", 85, false),
        new("StrongBoxes/StrongboxScarab", "Scarab Strongbox", 75, false),

        // Named encounters with their own reward.
        new("LeagueDeepwater/CursedTreasureChestEncounter", "Cursed Treasure", 90, false),
        new("LeagueDeepwater/BrinerotStoresChestEncounter", "Brinerot Stores", 70, false),
        new("LeagueDeepwater/GiantCoralChest", "Giant Coral Chest", 70, false),
        new("Objects/DeepwaterAnchorEndgameChests", "Treasure Anchor", 75, false),
        new("Objects/DeepwaterAnchorUniques", "Anchor (Uniques)", 55, false),
        new("Objects/DeepwaterAnchorHigh", "Anchor", 50, false),
        new("Objects/DeepwaterCursedDucatDrop", "Cursed Ducats", 45, false),

        // Dead Man's Sulphur. Half the stated objective, and it pays for rerolls.
        new("Objects/ResourceChestHuge", "Sulphur (Huge)", 70, false),
        new("Objects/ResourceChestLarge", "Sulphur (Large)", 45, false),
        new("Objects/ResourceChestBase", "Sulphur", 20, false),
        new("Objects/ResourceChestSmall", "Sulphur (Small)", 10, false),

        // Lower value, still worth grabbing when close.
        new("LeagueDeepwater/DeepwaterChestAllflameEmbers", "Allflame Embers", 40, false),
        new("LeagueDeepwater/DeepwaterChestMaps", "Maps Chest", 30, false),
        new("LeagueDeepwater/DeepwaterAnchorUniqueArmour", "Unique Armour Chest", 35, false),
        new("LeagueDeepwater/ClamTreasureChest", "Clam Chest", 25, false),
        new("LeagueDeepwater/GoldTreasureChest", "Gold Chest", 15, false),

        // Banking. Not loot, but the run is lost without it.
        new("Objects/CollectionChest", "Allflame Capsule (bank loot)", 5, false),
    ];
}
