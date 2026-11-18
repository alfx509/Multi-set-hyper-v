using System.Runtime.InteropServices;
using System.Text;

namespace MultiSeat.Input;

/// <summary>
/// P/Invoke for the Interception driver (https://github.com/oblitum/Interception).
/// It tells us WHICH keyboard or mouse produced each key/click, and lets us keep it away from the main desktop.
/// Slots 1-10 are keyboards, 11-20 are mice.
/// </summary>
internal static class Interception
{
    private const string Dll = "interception.dll";

    public const int MaxKeyboard = 10;
    public const int MaxDevice = 20;
    public const ushort FilterAll = 0xFFFF;

    public const ushort KeyUp = 0x01;
    public const ushort KeyE0 = 0x02;

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
    public const ushort MouseMoveAbsolute = 0x001;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int Predicate(int device);

    public static bool IsKeyboard(int d) => d >= 1 && d <= MaxKeyboard;

    public static bool IsMouse(int d) => d > MaxKeyboard && d <= MaxDevice;

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

    public static string? GetHardwareId(IntPtr context, int device)
    {
        var buffer = new byte[1024];
        var length = GetHardwareIdRaw(context, device, buffer, (uint)buffer.Length);
        if (length == 0 || length > buffer.Length)
        {
            return null;
        }

        var first = Encoding.Unicode.GetString(buffer, 0, (int)length)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }
}

/// <summary>InterceptionStroke: 20-byte union of the key stroke and the mouse stroke.</summary>
[StructLayout(LayoutKind.Explicit, Size = 20)]
internal struct Stroke
{
    [FieldOffset(0)] public ushort KeyCode;
    [FieldOffset(2)] public ushort KeyState;
    [FieldOffset(4)] public uint KeyInformation;

    [FieldOffset(0)] public ushort MouseState;
    [FieldOffset(2)] public ushort MouseFlags;
    [FieldOffset(4)] public short Rolling;
    [FieldOffset(8)] public int X;
    [FieldOffset(12)] public int Y;
    [FieldOffset(16)] public uint MouseInformation;
}
