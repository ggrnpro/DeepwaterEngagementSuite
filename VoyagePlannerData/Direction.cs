using System;

namespace DeepwaterEngagementSuiteGGRN.VoyagePlannerData;

[Flags]
public enum Direction
{
    None = 0,
    Left = 1,
    Up = 2,
    Right = 4,
    Down = 8,
    All = Up | Down | Left | Right
}

public static class DirectionExtensions
{
    public static Direction RotateCcw(this Direction d, int times)
    {
        var t = (4 - times % 4) % 4;
        while (t > 0)
        {
            d = d.Rotate90Cw();
            t--;
        }

        return d;
    }

    public static Direction Rotate90Cw(this Direction d)
    {
        return (d.HasFlag(Direction.Up) ? Direction.Right : 0) |
               (d.HasFlag(Direction.Right) ? Direction.Down : 0) |
               (d.HasFlag(Direction.Down) ? Direction.Left : 0) |
               (d.HasFlag(Direction.Left) ? Direction.Up : 0);
    }

    public static Direction Opposite(this Direction d)
    {
        return d switch
        {
            Direction.Up => Direction.Down,
            Direction.Down => Direction.Up,
            Direction.Left => Direction.Right,
            Direction.Right => Direction.Left,
            _ => d
        };
    }

    public static int CountConnections(this Direction d)
    {
        var c = 0;
        if (d.HasFlag(Direction.Up)) c++;
        if (d.HasFlag(Direction.Down)) c++;
        if (d.HasFlag(Direction.Left)) c++;
        if (d.HasFlag(Direction.Right)) c++;
        return c;
    }

    public static (int Dr, int Dc) CellNeighbor(this Direction d)
    {
        return d switch
        {
            Direction.Up => (-1, 0),
            Direction.Down => (1, 0),
            Direction.Left => (0, -1),
            Direction.Right => (0, 1),
            _ => (0, 0)
        };
    }
}
