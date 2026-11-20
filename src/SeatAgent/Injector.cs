using MultiSeat.Core;
using static MultiSeat.Agent.NativeInput;

namespace MultiSeat.Agent;

/// <summary>Turns packets into SendInput calls in this session; remembers held keys so nothing gets stuck.</summary>
internal sealed class Injector
{
    private readonly HashSet<(ushort Code, bool Extended)> _keys = new();
    private readonly HashSet<MouseButton> _buttons = new();
    private readonly Input[] _one = new Input[1];

    public bool HasPressed => _keys.Count > 0 || _buttons.Count > 0;

    /// <summary>Events Windows accepted / refused since the last <see cref="TakeCounts"/> (counts only, never keys).</summary>
    public (long Sent, long Failed, int LastError) TakeCounts()
    {
        var r = (_sent, _failed, _lastError);
        _sent = _failed = 0;
        return r;
    }

    private long _sent;
    private long _failed;
    private int _lastError;

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
                Mouse(MouseMove | MouseAbsolute | MouseVirtualDesk, Math.Clamp(e.X, 0, 65535), Math.Clamp(e.Y, 0, 65535));
                break;
            case PacketType.MouseButton:
                Button((MouseButton)e.Code, (e.Flags & 1) != 0);
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
        }
    }

    public void ReleaseAll()
    {
        foreach (var (code, extended) in _keys.ToArray())
        {
            Key(code, KeyFlags.Up | (extended ? KeyFlags.Extended0 : KeyFlags.None));
        }

        foreach (var b in _buttons.ToArray())
        {
            Button(b, true);
        }
    }

    private void Key(ushort scanCode, KeyFlags flags)
    {
        var up = flags.HasFlag(KeyFlags.Up);
        var extended = flags.HasFlag(KeyFlags.Extended0);
        if (up)
        {
            _keys.Remove((scanCode, extended));
        }
        else
        {
            _keys.Add((scanCode, extended));
        }

        var native = KeyScanCode | (up ? KeyUp : 0) | (extended ? KeyExtended : 0);
        _one[0] = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = scanCode, Flags = native } },
        };
        Send();
    }

    /// <summary>
    /// Counts as input for Windows (1 pixel right and back), but changes nothing for the player. Windows closes a
    /// remote session's sign-in screen after about 30 s without input; a seat must keep showing it.
    /// Not included in the injected counts.
    /// </summary>
    public bool KeepAlive()
    {
        var ok = true;
        foreach (var dx in new[] { 1, -1 })
        {
            _one[0] = new Input
            {
                Type = InputMouse,
                Data = new InputUnion { Mouse = new MouseInput { Dx = dx, Dy = 0, Flags = MouseMove } },
            };
            ok &= SendInput(1, _one, InputSize) == 1;
        }

        return ok;
    }

    private void Send()
    {
        if (SendInput(1, _one, InputSize) == 1)
        {
            _sent++;
        }
        else
        {
            _failed++;
            _lastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        }
    }

    private void Button(MouseButton button, bool released)
    {
        if (released)
        {
            _buttons.Remove(button);
        }
        else
        {
            _buttons.Add(button);
        }

        var (flags, data) = button switch
        {
            MouseButton.Left => (released ? MouseLeftUp : MouseLeftDown, 0u),
            MouseButton.Right => (released ? MouseRightUp : MouseRightDown, 0u),
            MouseButton.Middle => (released ? MouseMiddleUp : MouseMiddleDown, 0u),
            MouseButton.X1 => (released ? MouseXUp : MouseXDown, 1u),
            MouseButton.X2 => (released ? MouseXUp : MouseXDown, 2u),
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
        Send();
    }
}
