namespace DeepwaterEngagementSuiteGGRN.PathPlannerData;

public class Chest : IChest
{
    public Chest(IconPickerIndex type)
    {
        Type = type;
    }

    public IconPickerIndex Type { get; }
}