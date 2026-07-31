namespace DeepwaterEngagementSuiteGGRN.PathPlannerData;

public record WarningRelic : IExpeditionRelic
{
    public (double, double) GetScoreMultiplier(IExpeditionLoot loot)
    {
        return (0, 0);
    }
}