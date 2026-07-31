using System.Collections.Generic;

namespace DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

public record VoyageSolution(
    MapPiecePlacement[,] Grid,
    double TotalScore,
    bool IsValid);

public record VoyageSolutionResult(
    List<VoyageSolution> Solutions,
    long NodesExplored,
    long NodesPruned);
