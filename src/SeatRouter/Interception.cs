using System.Runtime.InteropServices;
using System.Text;

namespace MultiSeat.Router;

/// <summary>
/// Minimal P/Invoke surface for the Interception driver (https://github.com/oblitum/Interception).
/// Interception sits below the Windows input stack, tells us which physical device produced every
/// stroke, and lets us decide whether the stroke reaches the host at all.
/// </summary>
internal static class Interception
{
    private const string Dll = "interception.dll";

    public const int MaxKeyboard = 10;
    public const int MaxMouse = 10;
    public const int MaxDevice = MaxKeyboard + MaxMouse;

    public const ushort FilterAll = 0xFFFF;

    // Keyboard key state
    public const ushort KeyUp = 0x01;
    public const ushort KeyE0 = 0x02;
    public const ushort KeyE1 = 0x04;

    // Mouse button state
    public const ushort MouseLeftDown = 0x001;
    public const ushort MouseLeftUp = 0x002;
    public const ushort MouseRightDown = 0x004;
    public const ushort MouseRightUp = 0x008;
    public const ushort MouseMiddleDown = 0x010;
    public const ushort MouseMiddleUp = 0x020;
    public const ushort MouseButton4Down = 0x040;
    public const ushort MouseButton4Up = 0x080;
    public const ushort MouseButton5Down = 0x100;
    public const ushort MouseButton5Up = 0x200;
    public const ushort MouseWheel = 0x400;
    public const ushort MouseHWheel = 0x800;

    // Mouse flags
    public const ushort MouseMoveAbsolute = 0x001;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int Predicate(int device);

    public static bool IsKeyboard(int device) => device >= 1 && device <= MaxKeyboard;

    public static bool IsMouse(int device) => device > MaxKeyboard && device <= MaxDevice;

    [DllImport(Dll, EntryPoint = "interception_create_context", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr CreateContext();

    [DllImport(Dll, EntryPoint = "interception_destroy_context", CallingConvention = CallingConvention.Cdecl)]
    public static extern void DestroyContext(IntPtr context);

    [DllImport(Dll, EntryPoint = "interception_set_filter", CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetFilter(IntPtr context, Predicate predicate, ushort filter);

    [DllImport(Dll, EntryPoint = "interception_wait_with_timeout", CallingConvention = CallingConvention.Cdecl)]
    public static extern int WaitWithTimeout(IntPtr context, uint milliseconds);

    [DllImport(Dll, EntryPoint = "interception_receive", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Receive(IntPtr context, int device, ref Stroke stroke, uint count);

    [DllImport(Dll, EntryPoint = "interception_send", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Send(IntPtr context, int device, ref Stroke stroke, uint count);

    [DllImport(Dll, EntryPoint = "interception_get_hardware_id", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint GetHardwareIdRaw(IntPtr context, int device, byte[] buffer, uint size);

    /// <summary>Returns the first hardware id of a device, or null when no device is attached at that slot.</summary>
    public static string? GetHardwareId(IntPtr context, int device)
    {
        var buffer = new byte[1024];
        var length = GetHardwareIdRaw(context, device, buffer, (uint)buffer.Length);
        if (length == 0 || length > buffer.Length)
        {
            return null;
        }

        // REG_MULTI_SZ, UTF-16: take the first string.
        var text = Encoding.Unicode.GetString(buffer, 0, (int)length);
        var first = text.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }

    public static string DeviceLabel(int device) =>
        IsKeyboard(device) ? $"keyboard #{device}" : $"mouse #{device - MaxKeyboard}";
}

/// <summary>
/// InterceptionStroke: a 20-byte union of InterceptionKeyStroke and InterceptionMouseStroke.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 20)]
internal struct Stroke
{
    // InterceptionKeyStroke { ushort code; ushort state; uint information; }
    [FieldOffset(0)] public ushort KeyCode;
    [FieldOffset(2)] public ushort KeyState;
    [FieldOffset(4)] public uint KeyInformation;

    // InterceptionMouseStroke { ushort state; ushort flags; short rolling; int x; int y; uint information; }
    [FieldOffset(0)] public ushort MouseState;
    [FieldOffset(2)] public ushort MouseFlags;
    [FieldOffset(4)] public short Rolling;
    [FieldOffset(8)] public int X;
    [FieldOffset(12)] public int Y;
    [FieldOffset(16)] public uint MouseInformation;
}
