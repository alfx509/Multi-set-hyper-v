namespace MultiSeat.Protocol;

/// <summary>Kind of event carried in one packet.</summary>
public enum PacketType : byte
{
    /// <summary>Keep-alive. The agent releases everything if heartbeats stop.</summary>
    Heartbeat = 1,

    /// <summary>Keyboard scan code. <see cref="InputEvent.Code"/> = scan code, flags = <see cref="KeyFlags"/>.</summary>
    Key = 2,

    /// <summary>Relative mouse movement in mickeys (X, Y).</summary>
    MouseMove = 3,

    /// <summary>Absolute mouse position, normalised to 0..65535 on both axes.</summary>
    MouseMoveAbsolute = 4,

    /// <summary>Mouse button. Code = <see cref="MouseButton"/>, flags bit 0 = released.</summary>
    MouseButton = 5,

    /// <summary>Vertical wheel. X = delta (120 per notch).</summary>
    Wheel = 6,

    /// <summary>Horizontal wheel. X = delta (120 per notch).</summary>
    HWheel = 7,

    /// <summary>Release every key and button currently held on the seat.</summary>
    ReleaseAll = 8,
}

/// <summary>Flags for <see cref="PacketType.Key"/>. Bit layout matches Interception's key state.</summary>
[Flags]
public enum KeyFlags : uint
{
    None = 0,
    Up = 0x1,
    Extended0 = 0x2,
    Extended1 = 0x4,
}

/// <summary>Mouse button identifiers used in <see cref="PacketType.MouseButton"/>.</summary>
public enum MouseButton : ushort
{
    Left = 1,
    Right = 2,
    Middle = 3,
    X1 = 4,
    X2 = 5,
}

/// <summary>One input event routed from the host to a seat.</summary>
public readonly record struct InputEvent(PacketType Type, ushort Code = 0, int X = 0, int Y = 0, uint Flags = 0)
{
    public static InputEvent Heartbeat() => new(PacketType.Heartbeat);

    public static InputEvent ReleaseAll() => new(PacketType.ReleaseAll);

    public static InputEvent Key(ushort scanCode, KeyFlags flags) => new(PacketType.Key, scanCode, Flags: (uint)flags);

    public static InputEvent Move(int dx, int dy) => new(PacketType.MouseMove, X: dx, Y: dy);

    public static InputEvent MoveAbsolute(int x, int y) => new(PacketType.MouseMoveAbsolute, X: x, Y: y);

    public static InputEvent Button(MouseButton button, bool released) =>
        new(PacketType.MouseButton, (ushort)button, Flags: released ? 1u : 0u);

    public static InputEvent Wheel(int delta) => new(PacketType.Wheel, X: delta);

    public static InputEvent HWheel(int delta) => new(PacketType.HWheel, X: delta);
}
