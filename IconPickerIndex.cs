using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DeepwaterEngagementSuiteGGRN;

[JsonConverter(typeof(StringEnumConverter))]
public enum IconPickerIndex
{
    OtherChests,
    BottledItemChest,
    GoldTreasureChest,
    ClamTreasureChest,
    CurrencyTreasureChest,
    CurrencyTreasureChestOpulent,
    /// <summary>Gemcutter's Prism currency chest — Metadata/.../CurrencyGemcuttersChest1.</summary>
    CurrencyGemcuttersChest,
    UniqueWeaponChest,
    UniqueArmourChest,
    ScarabChest,
    StackedDecksChest,
    MapsChest,
    AllflameEmbersChest,
    CursedDucatDrop,
    RandomDucatChest,
    IzaroObject,
    AltarCrab,
    AltarOctopus,
    TormentedSpiritEncounter,
    LanternReplenishEncounter,
    GoldenLanternEncounter,
    InfusedCoralEncounter,
    /// <summary>Dead Man's Sulphur resource piles, by size. Sulphur is half of what a run is for.</summary>
    SulphurChestSmall,
    SulphurChest,
    SulphurChestLarge,
    SulphurChestHuge,
    TreasureAnchor,
    StrongboxDivination,
    StrongboxScarab,
    StrongboxArcanist,
    PointerTarget,
}
