using System;
using System.Runtime.InteropServices;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Raw pointer access. ExileCore only remembers where it last put the cursor, which is not where the
/// player's hand left it, and the whole point of borrowing the cursor is putting it back exactly.
/// </summary>
internal static class NativeCursor
{
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const int MK_LBUTTON = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public static Vector2? TryGetPosition()
    {
        return GetCursorPos(out var point) ? new Vector2(point.X, point.Y) : null;
    }

    /// <summary>
    /// Tells the window the pointer is over a spot without moving the real one. The coordinates are
    /// client space, the way the game itself packs them.
    /// </summary>
    public static bool PostMouseMove(IntPtr window, Vector2 clientPosition)
    {
        return window != IntPtr.Zero &&
               PostMessage(window, WM_MOUSEMOVE, IntPtr.Zero, PackPosition(clientPosition));
    }

    /// <summary>Delivers a click to the window without touching the real pointer.</summary>
    public static bool PostLeftClick(IntPtr window, Vector2 clientPosition)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        var position = PackPosition(clientPosition);
        return PostMessage(window, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), position)
               && PostMessage(window, WM_LBUTTONUP, IntPtr.Zero, position);
    }

    private static IntPtr PackPosition(Vector2 position)
    {
        var x = (int)position.X & 0xFFFF;
        var y = (int)position.Y & 0xFFFF;
        return new IntPtr((y << 16) | x);
    }
}
