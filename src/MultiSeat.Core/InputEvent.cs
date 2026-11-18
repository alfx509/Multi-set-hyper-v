namespace MultiSeat.Core;

public enum PacketType : byte
{
    Heartbeat = 1,
    Key = 2,
    MouseMove = 3,
    MouseMoveAbsolute = 4,
    MouseButton = 5,
    Wheel = 6,
    HWheel = 7,
    ReleaseAll = 8,
}

/// <summary>Key flags. Same bits as the Interception driver's key state.</summary>
[Flags]
public enum KeyFlags : uint
{
    None = 0,
    Up = 0x1,
    Extended0 = 0x2,
    Extended1 = 0x4,
}

public enum MouseButton : ushort
{
    Left = 1,
    Right = 2,
    Middle = 3,
    X1 = 4,
    X2 = 5,
}

/// <summary>One keyboard/mouse event sent from the manager to a seat's agent.</summary>
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
