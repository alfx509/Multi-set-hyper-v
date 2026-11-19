using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MultiSeat.Input;

/// <summary>
/// Keeps the main desktop's mouse pointer on the main desktop's monitor while players are playing.
/// </summary>
/// <remarks>
/// ClipCursor alone is not enough: Windows resets it whenever another window becomes active, so the
/// pointer could slip onto a player's monitor between two re-applies (reported in testing). A low-level
/// mouse hook sees every movement before Windows applies it and stops any move that would leave the
/// monitor. ClipCursor is still set as a second layer.
/// </remarks>
internal sealed class CursorFence : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;

    private readonly LowLevelMouseProc _proc;
    private IntPtr _hook;
    private Rectangle _area;
    private volatile bool _active;

    public CursorFence()
    {
        _proc = HookProc; // keep the delegate alive while the hook exists
    }

    /// <summary>Starts keeping the pointer inside <paramref name="area"/>. Must be called on the UI thread.</summary>
    public void Enable(Rectangle area)
    {
        _area = area;
        if (_hook == IntPtr.Zero)
        {
            _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the mouse fence.");
            }
        }

        _active = true;
        if (GetCursorPos(out var p) && !_area.Contains(p.X, p.Y))
        {
            var (x, y) = Clamp(p.X, p.Y);
            SetCursorPos(x, y);
        }

        Reclip();
    }

    /// <summary>Re-applies ClipCursor (Windows drops it on focus changes). Cheap; call on a timer.</summary>
    public void Reclip()
    {
        if (!_active)
        {
            return;
        }

        var r = new NativeRect { Left = _area.Left, Top = _area.Top, Right = _area.Right, Bottom = _area.Bottom };
        ClipCursor(ref r);
    }

    public void Disable()
    {
        _active = false;
        ClipCursor(IntPtr.Zero);
    }

    public void Dispose()
    {
        Disable();
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private (int X, int Y) Clamp(int x, int y) =>
        (Math.Clamp(x, _area.Left, _area.Right - 1), Math.Clamp(y, _area.Top, _area.Bottom - 1));

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _active && wParam == WmMouseMove)
        {
            var info = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            if (!_area.Contains(info.X, info.Y))
            {
                var (x, y) = Clamp(info.X, info.Y);
                SetCursorPos(x, y);
                return 1; // swallow the move that would leave the monitor
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(IntPtr rect);
}
