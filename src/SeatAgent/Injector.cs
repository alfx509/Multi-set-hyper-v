using MultiSeat.Protocol;
using static MultiSeat.Agent.NativeInput;

namespace MultiSeat.Agent;

/// <summary>
/// Turns protocol events into SendInput calls and remembers what is held down so that
/// everything can be released if the router disconnects (no stuck keys).
/// </summary>
internal sealed class Injector
{
    private readonly HashSet<(ushort Code, bool Extended)> _keysDown = new();
    private readonly HashSet<MouseButton> _buttonsDown = new();
    private readonly Input[] _one = new Input[1];

    public bool HasPressed => _keysDown.Count > 0 || _buttonsDown.Count > 0;

    public void Apply(in InputEvent e)
    {
        switch (e.Type)
        {
            case PacketType.Key:
                Key(e.Code, (KeyFlags)e.Flags);
                break;
            case PacketType.MouseMove:
                Mouse(MouseMove, e.X, e.Y);
                break;
            case PacketType.MouseMoveAbsolute:
                Mouse(MouseMove | MouseAbsolute | MouseVirtualDesk, Clamp(e.X), Clamp(e.Y));
                break;
            case PacketType.MouseButton:
                Button((MouseButton)e.Code, released: (e.Flags & 1) != 0);
                break;
            case PacketType.Wheel:
                Mouse(MouseWheel, 0, 0, unchecked((uint)e.X));
                break;
            case PacketType.HWheel:
                Mouse(MouseHWheel, 0, 0, unchecked((uint)e.X));
                break;
            case PacketType.ReleaseAll:
                ReleaseAll();
                break;
            case PacketType.Heartbeat:
            default:
                break;
        }
    }

    public void ReleaseAll()
    {
        foreach (var (code, extended) in _keysDown.ToArray())
        {
            Key(code, KeyFlags.Up | (extended ? KeyFlags.Extended0 : KeyFlags.None));
        }

        foreach (var button in _buttonsDown.ToArray())
        {
            Button(button, released: true);
        }
    }

    private void Key(ushort scanCode, KeyFlags flags)
    {
        var up = flags.HasFlag(KeyFlags.Up);
        var extended = flags.HasFlag(KeyFlags.Extended0);
        if (up)
        {
            _keysDown.Remove((scanCode, extended));
        }
        else
        {
            _keysDown.Add((scanCode, extended));
        }

        var native = KeyEventScanCode;
        if (up)
        {
            native |= KeyEventKeyUp;
        }

        if (extended)
        {
            native |= KeyEventExtendedKey;
        }

        _one[0] = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = scanCode, Flags = native } },
        };
        SendInput(1, _one, InputSize);
    }

    private void Button(MouseButton button, bool released)
    {
        if (released)
        {
            _buttonsDown.Remove(button);
        }
        else
        {
            _buttonsDown.Add(button);
        }

        var (flags, data) = (button, released) switch
        {
            (MouseButton.Left, false) => (MouseLeftDown, 0u),
            (MouseButton.Left, true) => (MouseLeftUp, 0u),
            (MouseButton.Right, false) => (MouseRightDown, 0u),
            (MouseButton.Right, true) => (MouseRightUp, 0u),
            (MouseButton.Middle, false) => (MouseMiddleDown, 0u),
            (MouseButton.Middle, true) => (MouseMiddleUp, 0u),
            (MouseButton.X1, false) => (MouseXDown, XButton1),
            (MouseButton.X1, true) => (MouseXUp, XButton1),
            (MouseButton.X2, false) => (MouseXDown, XButton2),
            (MouseButton.X2, true) => (MouseXUp, XButton2),
            _ => (0u, 0u),
        };

        if (flags != 0)
        {
            Mouse(flags, 0, 0, data);
        }
    }

    private void Mouse(uint flags, int dx, int dy, uint data = 0)
    {
        _one[0] = new Input
        {
            Type = InputMouse,
            Data = new InputUnion { Mouse = new MouseInput { Dx = dx, Dy = dy, MouseData = data, Flags = flags } },
        };
        SendInput(1, _one, InputSize);
    }

    private static int Clamp(int v) => Math.Clamp(v, 0, 65535);
}
