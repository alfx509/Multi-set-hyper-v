using MultiSeat.Protocol;

namespace MultiSeat.Router;

/// <summary>
/// Captures every keyboard and mouse through Interception and sends each device's input to the seat
/// it is paired with. Devices that are not paired keep working on the host as normal.
/// </summary>
internal sealed class InputRouter : IDisposable
{
    // Scan codes for the Ctrl+Alt+F12 "give this seat's keyboard/mouse back to the host" hotkey.
    private const ushort ScanCtrl = 0x1D;
    private const ushort ScanAlt = 0x38;
    private const ushort ScanF12 = 0x58;
    private const ushort ScanEscape = 0x01;
    private const ushort ExtendedBit = 0x100;

    private readonly IntPtr _context;

    // Keep the delegates alive: the native driver library holds on to the function pointers.
    private readonly Interception.Predicate _isKeyboard = d => Interception.IsKeyboard(d) ? 1 : 0;
    private readonly Interception.Predicate _isMouse = d => Interception.IsMouse(d) ? 1 : 0;

    private readonly SeatChannel?[] _deviceSeat = new SeatChannel?[Interception.MaxDevice + 1];
    private readonly HashSet<ushort>[] _pressed = new HashSet<ushort>[Interception.MaxDevice + 1];
    private readonly Dictionary<SeatChannel, int> _seatKeyboard = new();

    public InputRouter()
    {
        try
        {
            _context = Interception.CreateContext();
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "interception.dll was not found next to SeatRouter.exe. Run scripts/host/Install-Interception.ps1.", ex);
        }

        if (_context == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Could not open the Interception driver. Install it (scripts/host/Install-Interception.ps1) and reboot.");
        }

        for (var i = 0; i < _pressed.Length; i++)
        {
            _pressed[i] = new HashSet<ushort>();
        }

        Interception.SetFilter(_context, _isKeyboard, Interception.FilterAll);
        Interception.SetFilter(_context, _isMouse, Interception.FilterAll);
    }

    public IReadOnlyList<(int Device, string HardwareId)> ListDevices()
    {
        var list = new List<(int, string)>();
        for (var device = 1; device <= Interception.MaxDevice; device++)
        {
            var id = Interception.GetHardwareId(_context, device);
            if (id is not null)
            {
                list.Add((device, id));
            }
        }

        return list;
    }

    /// <summary>True when every saved device is still present at the same slot with the same hardware id.</summary>
    public bool IsPairingValid(IEnumerable<SeatPairing> pairings)
    {
        foreach (var p in pairings)
        {
            if (p.Keyboard > 0 && Interception.GetHardwareId(_context, p.Keyboard) != p.KeyboardHardwareId)
            {
                return false;
            }

            if (p.Mouse > 0 && Interception.GetHardwareId(_context, p.Mouse) != p.MouseHardwareId)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Interactive pairing. For each seat the player presses a key on their keyboard and clicks their mouse.
    /// All input still reaches the host while pairing, so nothing gets stuck.
    /// </summary>
    public List<SeatPairing> Pair(IReadOnlyList<string> seatNames, CancellationToken cancel)
    {
        var taken = new HashSet<int>();
        var result = new List<SeatPairing>();

        foreach (var seat in seatNames)
        {
            var pairing = new SeatPairing { Seat = seat };

            Console.WriteLine();
            Console.WriteLine($"[{seat}] Press any key on the keyboard for this seat (Esc = no keyboard).");
            var kb = WaitForRelease(d => Interception.IsKeyboard(d) && !taken.Contains(d), keyboard: true, cancel);
            if (kb.Device > 0 && !kb.Escape)
            {
                taken.Add(kb.Device);
                pairing.Keyboard = kb.Device;
                pairing.KeyboardHardwareId = Interception.GetHardwareId(_context, kb.Device);
                Console.WriteLine($"  -> {Interception.DeviceLabel(kb.Device)}  {pairing.KeyboardHardwareId}");
            }

            Console.WriteLine($"[{seat}] Click the left button on the mouse for this seat (Esc = no mouse).");
            var mouse = WaitForRelease(d => Interception.IsMouse(d) && !taken.Contains(d), keyboard: false, cancel);
            if (mouse.Device > 0 && !mouse.Escape)
            {
                taken.Add(mouse.Device);
                pairing.Mouse = mouse.Device;
                pairing.MouseHardwareId = Interception.GetHardwareId(_context, mouse.Device);
                Console.WriteLine($"  -> {Interception.DeviceLabel(mouse.Device)}  {pairing.MouseHardwareId}");
            }

            result.Add(pairing);
        }

        return result;
    }

    private (int Device, bool Escape) WaitForRelease(Func<int, bool> wanted, bool keyboard, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var device = Interception.WaitWithTimeout(_context, 250);
            if (device <= 0)
            {
                continue;
            }

            var stroke = default(Stroke);
            if (Interception.Receive(_context, device, ref stroke, 1) <= 0)
            {
                continue;
            }

            // Everything still goes to the host during pairing.
            Interception.Send(_context, device, ref stroke, 1);

            var isKeyUp = Interception.IsKeyboard(device) && (stroke.KeyState & Interception.KeyUp) != 0;
            if (isKeyUp && stroke.KeyCode == ScanEscape && (stroke.KeyState & Interception.KeyE0) == 0)
            {
                return (device, true);
            }

            if (!wanted(device))
            {
                continue;
            }

            if (keyboard && isKeyUp)
            {
                return (device, false);
            }

            if (!keyboard && (stroke.MouseState & Interception.MouseLeftUp) != 0)
            {
                return (device, false);
            }
        }

        return (0, false);
    }

    public void Apply(IEnumerable<SeatPairing> pairings, IReadOnlyDictionary<string, SeatChannel> seats)
    {
        Array.Clear(_deviceSeat);
        _seatKeyboard.Clear();
        foreach (var p in pairings)
        {
            if (!seats.TryGetValue(p.Seat, out var channel))
            {
                continue;
            }

            if (p.Keyboard > 0)
            {
                _deviceSeat[p.Keyboard] = channel;
                _seatKeyboard[channel] = p.Keyboard;
            }

            if (p.Mouse > 0)
            {
                _deviceSeat[p.Mouse] = channel;
            }
        }
    }

    public void Run(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var device = Interception.WaitWithTimeout(_context, 250);
            if (device <= 0)
            {
                continue;
            }

            var stroke = default(Stroke);
            if (Interception.Receive(_context, device, ref stroke, 1) <= 0)
            {
                continue;
            }

            var seat = _deviceSeat[device];
            if (Interception.IsKeyboard(device))
            {
                HandleKeyboard(device, seat, ref stroke);
            }
            else if (Interception.IsMouse(device) && seat is { HostMode: false })
            {
                ForwardMouse(seat, stroke);
            }
            else
            {
                Interception.Send(_context, device, ref stroke, 1);
            }
        }
    }

    private void HandleKeyboard(int device, SeatChannel? seat, ref Stroke stroke)
    {
        var up = (stroke.KeyState & Interception.KeyUp) != 0;
        var id = (ushort)(stroke.KeyCode | ((stroke.KeyState & Interception.KeyE0) != 0 ? ExtendedBit : 0));
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
            && (pressed.Contains(ScanCtrl) || pressed.Contains(ScanCtrl | ExtendedBit))
            && (pressed.Contains(ScanAlt) || pressed.Contains(ScanAlt | ExtendedBit)))
        {
            ToggleHostMode(seat);
            return;
        }

        if (seat is null || seat.HostMode)
        {
            Interception.Send(_context, device, ref stroke, 1);
            return;
        }

        seat.Send(InputEvent.Key(stroke.KeyCode, (KeyFlags)(stroke.KeyState & 0x7)));
    }

    private void ToggleHostMode(SeatChannel seat)
    {
        seat.HostMode = !seat.HostMode;
        if (seat.HostMode)
        {
            seat.Send(InputEvent.ReleaseAll());
            Log($"{seat.Name}: keyboard/mouse now control the HOST (Ctrl+Alt+F12 to return).");
            return;
        }

        // Keys held while in host mode (Ctrl, Alt) went to the host; release them there.
        if (_seatKeyboard.TryGetValue(seat, out var keyboard))
        {
            foreach (var id in _pressed[keyboard])
            {
                var release = new Stroke
                {
                    KeyCode = (ushort)(id & 0xFF),
                    KeyState = (ushort)(Interception.KeyUp | ((id & ExtendedBit) != 0 ? Interception.KeyE0 : 0)),
                };
                Interception.Send(_context, keyboard, ref release, 1);
            }
        }

        Log($"{seat.Name}: keyboard/mouse returned to the seat.");
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
            seat.Send(InputEvent.Button(button, released: false));
        }

        if ((state & up) != 0)
        {
            seat.Send(InputEvent.Button(button, released: true));
        }
    }

    public static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");

    public void Dispose()
    {
        // Destroying the context hands every device back to Windows.
        if (_context != IntPtr.Zero)
        {
            Interception.DestroyContext(_context);
        }
    }
}
