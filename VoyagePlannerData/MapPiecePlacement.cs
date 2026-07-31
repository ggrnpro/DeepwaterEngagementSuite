namespace DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

public record MapPiecePlacement(
    MapPiece Piece,
    int Rotation,
    Direction Connections);
