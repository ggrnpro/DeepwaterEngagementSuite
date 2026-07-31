using System.Collections.Generic;

namespace DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

/// <param name="StatWeights">
/// How much each of a chart's reward stats is worth. Null uses <see cref="RewardStatWeights.Default"/>.
/// </param>
public record VoyagePuzzle(
    List<MapPiece> AvailablePieces,
    IReadOnlyList<BorderEffect>[,] TileBorders,
    List<LockedPlacement> LockedPlacements,
    RewardStatWeights? StatWeights = null);
