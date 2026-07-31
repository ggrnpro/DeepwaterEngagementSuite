namespace DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

/// <param name="TimeLimitSeconds">
/// Hard backstop so a solve cannot run forever. It is not the intended stopping point.
/// </param>
/// <param name="PatienceSeconds">
/// Stop once this long has passed without finding a better board. Placement is quadratic and the
/// last stage of the search is randomised, so there is no moment where the answer is provably
/// final — but a search that has stopped improving for a long stretch has converged in practice.
/// Null falls back to stopping on <paramref name="TimeLimitSeconds"/> alone.
/// </param>
public record VoyagePlannerSettings(
    int TopN = 10,
    bool YieldIntermediate = true,
    double? TimeLimitSeconds = 30.0,
    double? PatienceSeconds = null);
