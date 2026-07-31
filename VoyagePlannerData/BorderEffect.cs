namespace DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

/// <summary>
/// A single border modifier touching one grid tile.
/// </summary>
/// <param name="Name">Raw border mod id (for display/debugging).</param>
/// <param name="Tags">Which reward categories this border boosts. <see cref="ModifierTag.All"/> matches everything.</param>
/// <param name="Multiplier">
/// Base multiplier. For per-connection borders this is the multiplier per single connection;
/// the effective multiplier is 1 + (Multiplier - 1) * connectionCount.
/// </param>
/// <param name="PerConnection">Scales with the connection count of the piece placed on the affected tile.</param>
/// <param name="AffectsPlacedChart">
/// True for borders that boost the chart placed on the tile (e.g. "increased effect of adjacent
/// Charts", chart refunds) — these multiply all of that chart's own modifiers, wherever their
/// value lands. False for borders that boost rewards materializing on the tile itself.
/// </param>
public record BorderEffect(
    string Name,
    ModifierTag Tags,
    double Multiplier,
    bool PerConnection,
    bool AffectsPlacedChart);
