using System.Collections.Generic;

namespace DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

public record VoyagePuzzle(
    List<MapPiece> AvailablePieces,
    IReadOnlyList<BorderEffect>[,] TileBorders,
    List<LockedPlacement> LockedPlacements);
