using System.Runtime.InteropServices;

namespace MultiSeat.Agent;

/// <summary>SendInput P/Invoke definitions.</summary>
internal static class NativeInput
{
    public const uint InputMouse = 0;
    public const uint InputKeyboard = 1;

    public const uint KeyEventExtendedKey = 0x0001;
    public const uint KeyEventKeyUp = 0x0002;
    public const uint KeyEventScanCode = 0x0008;

    public const uint MouseMove = 0x0001;
    public const uint MouseLeftDown = 0x0002;
    public const uint MouseLeftUp = 0x0004;
    public const uint MouseRightDown = 0x0008;
    public const uint MouseRightUp = 0x0010;
    public const uint MouseMiddleDown = 0x0020;
    public const uint MouseMiddleUp = 0x0040;
    public const uint MouseXDown = 0x0080;
    public const uint MouseXUp = 0x0100;
    public const uint MouseWheel = 0x0800;
    public const uint MouseHWheel = 0x1000;
    public const uint MouseVirtualDesk = 0x4000;
    public const uint MouseAbsolute = 0x8000;

    public const uint XButton1 = 0x0001;
    public const uint XButton2 = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    public static readonly int InputSize = Marshal.SizeOf<Input>();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, Input[] inputs, int size);
}
