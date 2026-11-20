using MultiSeat.Core;

namespace MultiSeat.Input;

/// <summary>
/// Owns the input driver for as long as MultiSeat is open.
/// <list type="bullet">
/// <item>Devices assigned to a running seat: sent only to that seat's agent, never to the main desktop.</item>
/// <item>All other devices: go to the main desktop (seat 1) as normal.</item>
/// <item>Pairing: "press a key on the keyboard for this seat" finds out which device that is.</item>
/// </list>
/// Uses our MultiSeatInput driver when it is installed, otherwise Interception.
/// </summary>
internal sealed class InputRouter : IDisposable
{
    private const ushort ScanCtrl = 0x1D;
    private const ushort ScanAlt = 0x38;
    private const ushort ScanF12 = 0x58;
    private const ushort ScanEscape = 0x01;
    private const ushort Extended = 0x100;

    private const ushort KeyUp = 0x01;
    private const ushort KeyE0 = 0x02;
    private const ushort MouseMoveAbsolute = 0x001;
    private const ushort MouseLeftUp = 0x002;
    private const ushort MouseWheel = 0x400;
    private const ushort MouseHWheel = 0x800;

    private readonly IInputSource _source;
    private readonly Dictionary<int, HashSet<ushort>> _pressed = new();
    private readonly Thread _thread;
    private readonly object _gate = new();
    private readonly object _captureGate = new();

    private volatile bool _stop;
    private Dictionary<int, SeatChannel> _routes = new();
    private List<SeatChannel> _channels = new();
    private TaskCompletionSource<InputDevice?>? _pairing;
    private bool _pairingKeyboard;
    private long _nextHeartbeat;

    private InputRouter(IInputSource source)
    {
        _source = source;
        _thread = new Thread(Loop) { IsBackground = true, Name = "MultiSeat input", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>Raised on the input thread.</summary>
    public event Action<string>? Message;

    /// <summary>Which driver is in use, for the log.</summary>
    public string SourceName => _source.Name;

    public static InputRouter? TryCreate(out string? error)
    {
        // Our own driver first; if it is installed but cannot be opened, say so instead of silently using Interception.
        IInputSource? source = DriverInputSource.TryCreate(out error);
        if (source is null && error is null)
        {
            source = InterceptionSource.TryCreate(out error);
        }

        return source is null ? null : new InputRouter(source);
    }

    public string? HardwareId(int slot) => _source.HardwareId(slot);

    public IReadOnlyList<InputDevice> Devices() => _source.Devices();

    /// <summary>Route the given seats' devices to their agents. Replaces any previous routing.</summary>
    public void SetRoutes(IEnumerable<SeatChannel> channels)
    {
        var routes = new Dictionary<int, SeatChannel>();
        var list = channels.ToList();
        foreach (var c in list)
        {
            if (c.Seat.Keyboard is { } kb)
            {
                routes[kb.Slot] = c;
            }

            if (c.Seat.Mouse is { } m)
            {
                routes[m.Slot] = c;
            }
        }

        lock (_gate)
        {
            _routes = routes;
            _channels = list;
        }

        ApplyCapture();
    }

    public void ClearRoutes() => SetRoutes(Array.Empty<SeatChannel>());

    /// <summary>Waits until the user presses+releases a key (keyboard) or clicks (mouse). Esc cancels (returns null).</summary>
    public Task<InputDevice?> PairAsync(bool keyboard, CancellationToken cancel)
    {
        var tcs = new TaskCompletionSource<InputDevice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pairing?.TrySetResult(null);
            _pairing = tcs;
            _pairingKeyboard = keyboard;
        }

        cancel.Register(() => tcs.TrySetResult(null));
        return tcs.Task;
    }

    /// <summary>Devices of running seats that are not lent to the main desktop must not reach Windows.</summary>
    private void ApplyCapture()
    {
        HashSet<int> captured;
        lock (_gate)
        {
            captured = _routes.Where(r => !r.Value.HostMode).Select(r => r.Key).ToHashSet();
        }

        lock (_captureGate)
        {
            _source.SetCaptured(captured);
        }
    }

    private void Loop()
    {
        while (!_stop)
        {
            var now = Environment.TickCount64;
            if (now >= _nextHeartbeat)
            {
                _nextHeartbeat = now + 500;
                List<SeatChannel> channels;
                lock (_gate)
                {
                    channels = _channels;
                }

                foreach (var c in channels)
                {
                    c.Send(InputEvent.Heartbeat());
                }
            }

            if (!_source.TryRead(200, out var input))
            {
                continue;
            }

            CheckPairing(input);

            SeatChannel? seat;
            lock (_gate)
            {
                _routes.TryGetValue(input.Device, out seat);
            }

            if (input.Keyboard)
            {
                HandleKeyboard(input, seat);
            }
            else if (seat is { HostMode: false })
            {
                if (input.Captured)
                {
                    ForwardMouse(seat, input);
                }
            }
            else
            {
                PassToDesktop(input);
            }
        }
    }

    /// <summary>Only Interception needs this; with MultiSeatInput the kernel already gave Windows every uncaptured event.</summary>
    private void PassToDesktop(in RawInput input)
    {
        if (input.Captured && _source.RoutesEverything)
        {
            _source.Pass(input);
        }
    }

    private void CheckPairing(in RawInput input)
    {
        TaskCompletionSource<InputDevice?>? pairing;
        bool wantKeyboard;
        lock (_gate)
        {
            pairing = _pairing;
            wantKeyboard = _pairingKeyboard;
        }

        if (pairing is null)
        {
            return;
        }

        var keyUp = input.Keyboard && (input.State & KeyUp) != 0;
        InputDevice? result = null;
        var done = false;

        if (keyUp && input.Code == ScanEscape && (input.State & KeyE0) == 0)
        {
            done = true; // cancelled
        }
        else if (wantKeyboard && keyUp)
        {
            done = true;
            result = new InputDevice(input.Device, HardwareId(input.Device) ?? "unknown", Keyboard: true);
        }
        else if (!wantKeyboard && !input.Keyboard && (input.State & MouseLeftUp) != 0)
        {
            done = true;
            result = new InputDevice(input.Device, HardwareId(input.Device) ?? "unknown", Keyboard: false);
        }

        if (done)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pairing, pairing))
                {
                    _pairing = null;
                }
            }

            pairing.TrySetResult(result);
        }
    }

    private void HandleKeyboard(in RawInput input, SeatChannel? seat)
    {
        var up = (input.State & KeyUp) != 0;
        var id = (ushort)(input.Code | ((input.State & KeyE0) != 0 ? Extended : 0));
        if (!_pressed.TryGetValue(input.Device, out var pressed))
        {
            _pressed[input.Device] = pressed = new HashSet<ushort>();
        }

        if (up)
        {
            pressed.Remove(id);
        }
        else
        {
            pressed.Add(id);
        }

        if (seat is not null && !up && id == ScanF12
            && (pressed.Contains(ScanCtrl) || pressed.Contains(ScanCtrl | Extended))
            && (pressed.Contains(ScanAlt) || pressed.Contains(ScanAlt | Extended)))
        {
            ToggleHostMode(seat, input.Device, pressed);
            return;
        }

        if (seat is null || seat.HostMode)
        {
            PassToDesktop(input);
            return;
        }

        if (input.Captured)
        {
            seat.Send(InputEvent.Key(input.Code, (KeyFlags)(input.State & 0x7)));
        }
    }

    private void ToggleHostMode(SeatChannel seat, int keyboard, HashSet<ushort> pressed)
    {
        seat.HostMode = !seat.HostMode;
        ApplyCapture();
        if (seat.HostMode)
        {
            seat.Send(InputEvent.ReleaseAll());
            Message?.Invoke($"{seat.Seat.Name}: keyboard and mouse now control the main desktop (Ctrl+Alt+F12 to go back).");
            return;
        }

        // Ctrl/Alt went to the main desktop while in host mode: release them there.
        // (MultiSeatInput does this itself when the device is captured again.)
        if (_source.RoutesEverything)
        {
            foreach (var id in pressed)
            {
                var state = (ushort)(KeyUp | ((id & Extended) != 0 ? KeyE0 : 0));
                _source.Pass(new RawInput(keyboard, true, true, (ushort)(id & 0xFF), state, 0, 0, 0, 0));
            }
        }

        Message?.Invoke($"{seat.Seat.Name}: keyboard and mouse are back on the seat.");
    }

    private static void ForwardMouse(SeatChannel seat, in RawInput s)
    {
        if ((s.Flags & MouseMoveAbsolute) != 0)
        {
            seat.Send(InputEvent.MoveAbsolute(s.X, s.Y));
        }
        else if (s.X != 0 || s.Y != 0)
        {
            seat.Send(InputEvent.Move(s.X, s.Y));
        }

        var st = s.State;
        Button(seat, st, 0x001, 0x002, MouseButton.Left);
        Button(seat, st, 0x004, 0x008, MouseButton.Right);
        Button(seat, st, 0x010, 0x020, MouseButton.Middle);
        Button(seat, st, 0x040, 0x080, MouseButton.X1);
        Button(seat, st, 0x100, 0x200, MouseButton.X2);
        if ((st & MouseWheel) != 0)
        {
            seat.Send(InputEvent.Wheel(s.Rolling));
        }

        if ((st & MouseHWheel) != 0)
        {
            seat.Send(InputEvent.HWheel(s.Rolling));
        }
    }

    private static void Button(SeatChannel seat, ushort state, ushort down, ushort up, MouseButton button)
    {
        if ((state & down) != 0)
        {
            seat.Send(InputEvent.Button(button, false));
        }

        if ((state & up) != 0)
        {
            seat.Send(InputEvent.Button(button, true));
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
        _source.Dispose(); // every device goes back to Windows
    }
}
