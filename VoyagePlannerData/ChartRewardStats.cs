namespace DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

/// <summary>
/// The reward side of a chart's modifiers, in percent.
///
/// Every danger modifier a chart rolls grants exactly one reward stat — that is the trade the
/// league is built on. These are the numbers shown on a chart's tooltip header (Item Quantity,
/// Item Rarity, Monster Pack Size, Dead Man's Sulphur) and they scale everything that drops in the
/// area the chart opens, including content that neighbouring charts put there.
/// </summary>
public readonly record struct ChartRewardStats(
    double Quantity,
    double Rarity,
    double PackSize,
    double Sulphur,
    double Gold)
{
    public static ChartRewardStats operator +(ChartRewardStats a, ChartRewardStats b) => new(
        a.Quantity + b.Quantity,
        a.Rarity + b.Rarity,
        a.PackSize + b.PackSize,
        a.Sulphur + b.Sulphur,
        a.Gold + b.Gold);

    public bool IsZero => Quantity == 0 && Rarity == 0 && PackSize == 0 && Sulphur == 0 && Gold == 0;

    public override string ToString()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (Quantity != 0) parts.Add($"Q{Quantity:F0}%");
        if (Rarity != 0) parts.Add($"R{Rarity:F0}%");
        if (PackSize != 0) parts.Add($"P{PackSize:F0}%");
        if (Sulphur != 0) parts.Add($"S{Sulphur:F0}%");
        if (Gold != 0) parts.Add($"G{Gold:F0}%");
        return parts.Count == 0 ? "-" : string.Join(" ", parts);
    }
}

/// <summary>How far a chart modifier's effect reaches.</summary>
public enum ModScope
{
    /// <summary>Only the area this chart opens.</summary>
    SelfArea,

    /// <summary>The areas orthogonally adjacent to this chart.</summary>
    AdjacentAreas,

    /// <summary>Every area in the voyage.</summary>
    WholeVoyage,
}

/// <summary>
/// Weights turning a chart's reward stats into one multiplier per reward category.
///
/// Item Quantity scales drops directly, so it applies at face value. The others are worth less per
/// point for a currency-focused run — pack size adds monsters rather than drops per monster, and
/// rarity shifts item tiers rather than counts — so each gets a factor the user can tune.
/// </summary>
public readonly record struct RewardStatWeights(
    double Quantity,
    double Rarity,
    double PackSize,
    double Sulphur,
    double Gold)
{
    public static RewardStatWeights Default => new(1.0, 0.5, 0.6, 1.0, 0.2);
}
