using System.Numerics;
using ExileCore.Shared.Helpers;
using GameOffsets.Native;

namespace DeepwaterEngagementSuiteGGRN;

public static class Extensions
{
    public static bool DistanceLessThanOrEqual(this Vector2 v, Vector2 other, float distance)
    {
        return v.DistanceSquared(other) < distance * distance;
    }

    public static bool DistanceLessThanOrEqual(this Vector2i v, Vector2i other, float distance)
    {
        return v.DistanceSqr(other) < distance * distance;
    }
}