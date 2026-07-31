namespace DeepwaterEngagementSuiteGGRN.PathPlannerData;

public record DoubledMonstersRelic : IExpeditionRelic
{
    public (double, double) GetScoreMultiplier(IExpeditionLoot loot)
    {
        if (loot is RunicMonster)
        {
            return (2, 0);
        }

        return (1, 0);
    }
}