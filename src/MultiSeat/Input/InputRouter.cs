using MultiSeat.Core;

namespace MultiSeat.Input;

/// <summary>
/// Owns the Interception driver for as long as MultiSeat is open.
/// <list type="bullet">
/// <item>Devices assigned to a running seat: sent only to that seat's agent, never to the main desktop.</item>
/// <item>All other devices: passed through to the main desktop (seat 1) as normal.</item>
/// <item>Pairing: "press a key on the keyboard for this seat" finds out which device that is.</item>
/// </list>
/// </summary>
internal sealed class InputRouter : IDisposable
{
    private const ushort ScanCtrl = 0x1D;
    private const ushort ScanAlt = 0x38;
    private const ushort ScanF12 = 0x58;
    private const ushort ScanEscape = 0x01;
    private const ushort Extended = 0x100;

    private readonly IntPtr _context;
    private readonly Interception.Predicate _isKeyboard = d => Interception.IsKeyboard(d) ? 1 : 0;
    private readonly Interception.Predicate _isMouse = d => Interception.IsMouse(d) ? 1 : 0;
    private readonly HashSet<ushort>[] _pressed = Enumerable.Range(0, Interception.MaxDevice + 1).Select(_ => new HashSet<ushort>()).ToArray();
    private readonly Thread _thread;
    private readonly object _gate = new();

    private volatile bool _stop;
    private SeatChannel?[] _routes = new SeatChannel?[Interception.MaxDevice + 1];
    private Dictionary<SeatChannel, int> _seatKeyboard = new();
    private List<SeatChannel> _channels = new();
    private TaskCompletionSource<InputDevice?>? _pairing;
    private bool _pairingKeyboard;
    private long _nextHeartbeat;

    private InputRouter(IntPtr context)
    {
        _context = context;
        Interception.SetFilter(_context, _isKeyboard, Interception.FilterAll);
        Interception.SetFilter(_context, _isMouse, Interception.FilterAll);
        _thread = new Thread(Loop) { IsBackground = true, Name = "MultiSeat input", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>Raised on the input thread.</summary>
    public event Action<string>? Message;

    public static InputRouter? TryCreate(out string? error)
    {
        try
        {
            var context = Interception.CreateContext();
            if (context == IntPtr.Zero)
            {
                error = "The Interception input driver is not running. Run MultiSeat-Setup.exe again and restart the PC.";
                return null;
            }

            error = null;
            return new InputRouter(context);
        }
        catch (DllNotFoundException)
        {
            error = "interception.dll is missing next to MultiSeat.exe. Run MultiSeat-Setup.exe again.";
            return null;
        }
    }

    public string? HardwareId(int slot) => Interception.GetHardwareId(_context, slot);

    /// <summary>Route the given seats' devices to their agents. Replaces any previous routing.</summary>
    public void SetRoutes(IEnumerable<SeatChannel> channels)
    {
        var routes = new SeatChannel?[Interception.MaxDevice + 1];
        var keyboards = new Dictionary<SeatChannel, int>();
        var list = channels.ToList();
        foreach (var c in list)
        {
            if (c.Seat.Keyboard is { } kb)
            {
                routes[kb.Slot] = c;
                keyboards[c] = kb.Slot;
            }

            if (c.Seat.Mouse is { } m)
            {
                routes[m.Slot] = c;
            }
        }

        lock (_gate)
        {
            _routes = routes;
            _seatKeyboard = keyboards;
            _channels = list;
        }
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

            var device = Interception.WaitWithTimeout(_context, 200);
            if (device <= 0)
            {
                continue;
            }

            var stroke = default(Stroke);
            if (Interception.Receive(_context, device, ref stroke, 1) <= 0)
            {
                continue;
            }

            CheckPairing(device, stroke);

            SeatChannel? seat;
            lock (_gate)
            {
                seat = _routes[device];
            }

            if (Interception.IsKeyboard(device))
            {
                HandleKeyboard(device, seat, ref stroke);
            }
            else if (seat is { HostMode: false })
            {
                ForwardMouse(seat, stroke);
            }
            else
            {
                Interception.Send(_context, device, ref stroke, 1);
            }
        }
    }

    private void CheckPairing(int device, in Stroke stroke)
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

        var keyUp = Interception.IsKeyboard(device) && (stroke.KeyState & Interception.KeyUp) != 0;
        InputDevice? result = null;
        var done = false;

        if (keyUp && stroke.KeyCode == ScanEscape && (stroke.KeyState & Interception.KeyE0) == 0)
        {
            done = true; // cancelled
        }
        else if (wantKeyboard && keyUp)
        {
            done = true;
            result = new InputDevice(device, HardwareId(device) ?? "unknown");
        }
        else if (!wantKeyboard && Interception.IsMouse(device) && (stroke.MouseState & Interception.MouseLeftUp) != 0)
        {
            done = true;
            result = new InputDevice(device, HardwareId(device) ?? "unknown");
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

    private void HandleKeyboard(int device, SeatChannel? seat, ref Stroke stroke)
    {
        var up = (stroke.KeyState & Interception.KeyUp) != 0;
        var id = (ushort)(stroke.KeyCode | ((stroke.KeyState & Interception.KeyE0) != 0 ? Extended : 0));
        var pressed = _pressed[device];
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
            ToggleHostMode(seat, device);
            return;
        }

        if (seat is null || seat.HostMode)
        {
            Interception.Send(_context, device, ref stroke, 1);
            return;
        }

        seat.Send(InputEvent.Key(stroke.KeyCode, (KeyFlags)(stroke.KeyState & 0x7)));
    }

    private void ToggleHostMode(SeatChannel seat, int keyboard)
    {
        seat.HostMode = !seat.HostMode;
        if (seat.HostMode)
        {
            seat.Send(InputEvent.ReleaseAll());
            Message?.Invoke($"{seat.Seat.Name}: keyboard and mouse now control the main desktop (Ctrl+Alt+F12 to go back).");
            return;
        }

        // Ctrl/Alt went to the main desktop while in host mode: release them there.
        foreach (var id in _pressed[keyboard])
        {
            var release = new Stroke
            {
                KeyCode = (ushort)(id & 0xFF),
                KeyState = (ushort)(Interception.KeyUp | ((id & Extended) != 0 ? Interception.KeyE0 : 0)),
            };
            Interception.Send(_context, keyboard, ref release, 1);
        }

        Message?.Invoke($"{seat.Seat.Name}: keyboard and mouse are back on the seat.");
    }

    private static void ForwardMouse(SeatChannel seat, in Stroke s)
    {
        if ((s.MouseFlags & Interception.MouseMoveAbsolute) != 0)
        {
            seat.Send(InputEvent.MoveAbsolute(s.X, s.Y));
        }
        else if (s.X != 0 || s.Y != 0)
        {
            seat.Send(InputEvent.Move(s.X, s.Y));
        }

        var st = s.MouseState;
        Button(seat, st, Interception.MouseLeftDown, Interception.MouseLeftUp, MouseButton.Left);
        Button(seat, st, Interception.MouseRightDown, Interception.MouseRightUp, MouseButton.Right);
        Button(seat, st, Interception.MouseMiddleDown, Interception.MouseMiddleUp, MouseButton.Middle);
        Button(seat, st, Interception.MouseButton4Down, Interception.MouseButton4Up, MouseButton.X1);
        Button(seat, st, Interception.MouseButton5Down, Interception.MouseButton5Up, MouseButton.X2);
        if ((st & Interception.MouseWheel) != 0)
        {
            seat.Send(InputEvent.Wheel(s.Rolling));
        }

        if ((st & Interception.MouseHWheel) != 0)
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
        Interception.DestroyContext(_context); // every device goes back to Windows
    }
}
