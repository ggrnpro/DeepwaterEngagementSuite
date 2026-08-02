# The Azurite Mine

What the game itself says about the mine, read out of its own tables rather than off a community
page. Export it again with `Delve Settings` -> `Export the mine's own database`; the snapshot lands
in `config/DeepwaterEngagementSuiteGGRN/debug/snapshot-delve-catalogue-*.json`.

As of the export that produced this file: **10 biomes, 246 room features, 233 league modifiers**.

## Why the game's copy and not the wiki

poewiki.net is behind a bot wall and refuses the fetch. poedb has the biome list but its tables stop
before they say what a biome does. The game ships all of it locally and it matches the installed
patch by construction — which is also how the wall mechanic, the vein ladder and the chest taxonomy
were found, each time contradicting a reasonable guess.

## Biomes

`Mines`, `Fungal Caverns`, `Petrified Forest`, `Abyssal Depths`, `Frozen Hollow`, `Magma Fissure`,
`Sulphur Vents`, `Vaal Outpost`, `Abyssal City`, `Primeval Ruins`.

A biome fixes what its off-path chests contain. The modifier table states this outright rather than
by implication:

```
DelveBiomeOffPathRewardChestsAlwaysAzurite / AlwaysCurrency / AlwaysFossils / AlwaysResonators
DelveBiomeOffPathRewardChestsAzuriteChancePctFinal   (and Currency/Fossil/Resonator siblings)
DelveBiomeNodeTierUpgradePct
DelveBiomeMonsterDropFossilChancePct
DelveBiomeCityChambersCanContainSpecialDelveChest
DelveBiomeContainsDelveBoss
DelveBiomeBossDropsXAdditionalFossils
DelveBiomeBossDropsAdditionalUniqueItem
DelveBiomeSulphiteCostPctFinal
```

## Azurite nodes come in three grades

| Feature id | Name |
| --- | --- |
| `Azurite1_1` … `Azurite1_4`, `Azurite1_Q` | Azurite Cavity |
| `Azurite2_1` … `Azurite2_4` | Azurite Vault |
| `Azurite3_1` … `Azurite3_3` | Azurite Fissure |

Cavity is the small one. Fissure is the one worth crossing the map for. This is the chart-level
ladder and is separate from the in-area vein ladder (`DelveAzuriteVein1_1` renders as "Flawed
Azurite Vein", `1_2` as a plain "Azurite Vein").

## One dedicated fossil room per biome

| Room | Biome |
| --- | --- |
| Haunted Tomb | Fungal Caverns |
| Stonewood Hollow | Petrified Forest |
| Crystal Spire | Abyssal Depths |
| Time-Lost Cavern | Frozen Hollow |
| Molten Cavity | Magma Fissure |
| Humid Fissure | Sulphur Vents |

Plus `Smuggler's Stash` (`ExilesFossils`), which drops fossils in any biome.

## Bosses

| Room | Id |
| --- | --- |
| The Lich's Tomb | `AbyssalBoss` |
| The Grand Architect's Temple | `VaalBoss` |
| The Crystal King's Throne | `ProtoVaalBoss` |

## City chambers, tiered 0-4

`Abyssal Chamber` (`AbyssalChamberTier0..4`), `Ruined Chamber` (`VaalChamberTier0..4`),
`Primeval Chamber` (`ProtoVaalChamberTier0..4`). The tier is in the id, and
`DelveBiomeCityChambersCanContainSpecialDelveChest` is what makes these worth the sulphite.

## Other content that appears as a mine room

`Buried Monolith` (Legion), `Whispering Gallery` (Breach), `Pulsating Grotto` (Beyond),
`Underground Stash` (Harbinger / Strongbox), `Ritual Grounds` (Talisman), `Echoing Lair` (Bestiary),
`Unspeakable Shrine` (Cultists), `Haunted Remains` (Abyss), `Frigid Recess` (Essence).

## Ordinary rooms name their own reward

An encounter room's id ends in what it drops: `...Armour`, `...Weapon`, `...Currency`, `...Gems`,
`...Trinkets`, `...Maps`, `...Minions`, `...Generic`, `...Fossils`. So `MineCampsiteCurrency` is an
Abandoned Camp that pays in currency and `CavernsCampsiteTrinkets` is the same room paying in
jewellery. Ranking a node does not need a hand-written table of room values — the id says it.

## In-area objects

Covered by the classifier in `DeepwaterEngagementSuiteGGRN.Delve.cs`. The short version:

- `Metadata/Chests/DelveChests/Path*` — on the cart's lit route.
- `OffPath*` — off it, in the dark.
- `*Dynamite*` — sealed behind a wall. Not always a prefix: a fossil behind one is
  `DenseFossilChestDynamite`. `DelveMiningSuppliesDynamite` is the crate that hands dynamite out and
  is the one place the word does not mean sealed.
- `*NoDrops` — a decoy that opens onto nothing.
- `Metadata/Terrain/Leagues/Delve/Objects/DelveAzuriteShard` — loose azurite, terrain rather than a
  chest, ten at a time, and no icon points at it.
- `Metadata/Terrain/Leagues/Delve/Objects/DelveWall` — a wall, filed under `EntityType.IngameIcon`.
  Its `MinimapIcon.IsHide` is the whole secret-passage mechanic: the wall object is in the entity
  list the entire time and only its icon is held back, so a route that exists reads as a dead end.

## Still open

The Subterranean Chart reads through `IngameUi.DelveWindow`, but `GridElement.Cells` has come back
empty every time so far. Until that populates, none of the room table above can be applied to node
ranking — it is the one piece between here and "delve into this node".
