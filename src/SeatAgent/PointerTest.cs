using System.Runtime.InteropServices;

namespace MultiSeat.Agent;

/// <summary>Test only (--pointer-test): moves the pointer in a circle so snapshots show the drawn pointer.</summary>
internal static class PointerTest
{
    public static void Circle(int seconds)
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4));
        var desktop = OpenInputDesktop(0, false, 0x10000000);
        if (desktop != IntPtr.Zero)
        {
            SetThreadDesktop(desktop);
        }

        var cx = GetSystemMetrics(0) / 2;
        var cy = GetSystemMetrics(1) / 2;
        var end = Environment.TickCount64 + (seconds * 1000L);
        for (var i = 0; Environment.TickCount64 < end; i++)
        {
            var a = i * 0.05;
            SetCursorPos(cx + (int)(150 * Math.Cos(a)), cy + (int)(150 * Math.Sin(a)));
            Thread.Sleep(16);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
