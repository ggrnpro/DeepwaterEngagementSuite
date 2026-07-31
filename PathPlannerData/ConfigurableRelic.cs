namespace DeepwaterEngagementSuiteGGRN.PathPlannerData;

public class ConfigurableRelic : IExpeditionRelic
{
    private readonly double _multiplier;
    private readonly double _increase;
    private readonly bool _isMonsterRelic;

    public ConfigurableRelic(double multiplier, double increase, bool isMonsterRelic)
    {
        _multiplier = multiplier;
        _increase = increase;
        _isMonsterRelic = isMonsterRelic;
    }

    public (double, double) GetScoreMultiplier(IExpeditionLoot loot)
    {
        return (_isMonsterRelic, loot) switch
        {
            (true, IMonster) or (false, IChest) => (_multiplier, _increase),
            _ => (1, 0),
        };
    }
}