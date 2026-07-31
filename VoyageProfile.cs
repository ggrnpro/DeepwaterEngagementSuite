using System.Collections.Generic;

namespace DeepwaterEngagementSuiteGGRN;

public class VoyageProfile
{
    public List<VoyageBorderModifier> BorderModifiers { get; set; } = [];
    public List<VoyageChartModifier> ChartModifiers { get; set; } = [];
}
