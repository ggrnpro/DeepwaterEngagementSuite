using System.Collections.Generic;
using System.Linq;

namespace DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

public enum PieceType
{
    Cross,
    Straight,
    Corner,
    Tee,
    Single,
}

/// <param name="SelfStats">
/// Reward stats that apply to the area this chart opens — its own Item Quantity, Rarity, Pack Size
/// and Dead Man's Sulphur. They scale everything that drops there, including content neighbouring
/// charts put in the area.
/// </param>
/// <param name="SelfModifiers">
/// Value that materialises in this chart's own area rather than being delivered to neighbours —
/// the area it opens. A plain "Abyssal Plain" is worth little; "Kishara's Rest" is a boss encounter
/// and worth a great deal, and nothing else in the model captures that difference.
/// </param>
/// <param name="VoyageStats">
/// Reward stats this chart projects over every area of the voyage, from its voyage-scope implicit.
/// Unlike <paramref name="SelfStats"/> these do not depend on where the chart is placed.
/// </param>
public record MapPiece(
    int Id,
    PieceType Type,
    Direction BaseConnections,
    List<Modifier> Modifiers,
    ChartRewardStats SelfStats = default,
    ChartRewardStats VoyageStats = default,
    List<Modifier> SelfModifiers = null)
{
    public readonly double GlobalModifier = Modifiers.Where(x => x.IsGlobal).Sum(x => x.Weight);
    public readonly double LocalModifier = Modifiers.Where(x => !x.IsGlobal).Sum(x => x.Weight);
    public int DistinctRotations => Type switch
    {
        PieceType.Cross => 1,
        PieceType.Straight => 2,
        PieceType.Corner => 4,
        PieceType.Tee => 4,
        PieceType.Single => 4,
        _ => 4
    };

    public Direction GetConnections(int rotation)
    {
        return BaseConnections.RotateCcw(rotation);
    }
}
