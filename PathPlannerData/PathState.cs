using System.Collections.Generic;
using GameOffsets.Native;

namespace DeepwaterEngagementSuiteGGRN.PathPlannerData;

public record PathState(List<Vector2i> Points, double Score);