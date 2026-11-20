using System.Runtime.InteropServices;

namespace MultiSeat.Agent;

/// <summary>
/// Seat mode (MultiSeat Local sessions): the agent runs as SYSTEM inside the seat's session from before sign-in.
/// The active desktop changes (Winlogon sign-in screen, the user's Default desktop, UAC/secure desktop), and
/// SendInput only reaches the desktop the calling thread is on. This keeps the thread on the input desktop.
/// The injecting thread must own no windows, otherwise SetThreadDesktop fails.
/// </summary>
internal sealed class DesktopFollower
{
    private const uint GenericAll = 0x10000000;
    private const int UoiName = 2;

    private IntPtr _current;
    private string _name = "";
    private long _nextCheck;

    public string Name => _name;

    /// <summary>Re-checks at most every 200 ms; returns true when the thread moved to another desktop.</summary>
    public bool Follow(Action<string> log)
    {
        var now = Environment.TickCount64;
        if (now < _nextCheck)
        {
            return false;
        }

        _nextCheck = now + 200;
        var desktop = OpenInputDesktop(0, false, GenericAll);
        if (desktop == IntPtr.Zero)
        {
            return false; // e.g. while the desktop switches
        }

        var name = NameOf(desktop);
        if (name == _name)
        {
            CloseDesktop(desktop);
            return false;
        }

        if (!SetThreadDesktop(desktop))
        {
            log($"SetThreadDesktop({name}) failed: {Marshal.GetLastWin32Error()}");
            CloseDesktop(desktop);
            return false;
        }

        if (_current != IntPtr.Zero)
        {
            CloseDesktop(_current);
        }

        _current = desktop;
        log($"Input desktop: {name}");
        _name = name;
        return true;
    }

    private static string NameOf(IntPtr desktop)
    {
        var buffer = new char[256];
        return GetUserObjectInformationW(desktop, UoiName, buffer, buffer.Length * 2, out _)
            ? new string(buffer).TrimEnd('\0')
            : "?";
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(IntPtr obj, int index, [Out] char[] info, int length, out int needed);
}
