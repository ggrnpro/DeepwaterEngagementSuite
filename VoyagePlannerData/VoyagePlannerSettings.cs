namespace DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

public record VoyagePlannerSettings(
    int TopN = 10,
    bool YieldIntermediate = true,
    double? TimeLimitSeconds = 30.0);
