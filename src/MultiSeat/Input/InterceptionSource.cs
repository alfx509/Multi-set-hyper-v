namespace MultiSeat.Input;

/// <summary>Input from the Interception driver: every key/mouse event passes through MultiSeat.</summary>
internal sealed class InterceptionSource : IInputSource
{
    private readonly IntPtr _context;
    private readonly Interception.Predicate _isKeyboard = d => Interception.IsKeyboard(d) ? 1 : 0;
    private readonly Interception.Predicate _isMouse = d => Interception.IsMouse(d) ? 1 : 0;

    private InterceptionSource(IntPtr context)
    {
        _context = context;
        Interception.SetFilter(_context, _isKeyboard, Interception.FilterAll);
        Interception.SetFilter(_context, _isMouse, Interception.FilterAll);
    }

    public string Name => "Interception driver";

    public bool RoutesEverything => true;

    public static InterceptionSource? TryCreate(out string? error)
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
            return new InterceptionSource(context);
        }
        catch (DllNotFoundException)
        {
            error = "interception.dll is missing next to MultiSeat.exe. Run MultiSeat-Setup.exe again.";
            return null;
        }
    }

    public bool TryRead(int timeoutMs, out RawInput input)
    {
        input = default;
        var device = Interception.WaitWithTimeout(_context, (uint)timeoutMs);
        if (device <= 0)
        {
            return false;
        }

        var s = default(Stroke);
        if (Interception.Receive(_context, device, ref s, 1) <= 0)
        {
            return false;
        }

        input = Interception.IsKeyboard(device)
            ? new RawInput(device, true, true, s.KeyCode, s.KeyState, 0, 0, 0, 0)
            : new RawInput(device, false, true, 0, s.MouseState, s.MouseFlags, s.Rolling, s.X, s.Y);
        return true;
    }

    public void Pass(in RawInput input)
    {
        var s = default(Stroke);
        if (input.Keyboard)
        {
            s.KeyCode = input.Code;
            s.KeyState = input.State;
        }
        else
        {
            s.MouseState = input.State;
            s.MouseFlags = input.Flags;
            s.Rolling = input.Rolling;
            s.X = input.X;
            s.Y = input.Y;
        }

        Interception.Send(_context, input.Device, ref s, 1);
    }

    public void SetCaptured(IReadOnlySet<int> devices)
    {
        // Interception captures everything; the router decides per event.
    }

    public bool IsKeyboard(int device) => Interception.IsKeyboard(device);

    public string? HardwareId(int device) => Interception.GetHardwareId(_context, device);

    /// <summary>Interception: slots 1-10 keyboards, 11-20 mice; a slot with a hardware id has a device.</summary>
    public IReadOnlyList<MultiSeat.Core.InputDevice> Devices() =>
        Enumerable.Range(1, 20)
            .Select(slot => (slot, id: HardwareId(slot)))
            .Where(x => !string.IsNullOrEmpty(x.id))
            .Select(x => new MultiSeat.Core.InputDevice(x.slot, x.id!))
            .ToList();

    public void Dispose() => Interception.DestroyContext(_context); // every device goes back to Windows
}
