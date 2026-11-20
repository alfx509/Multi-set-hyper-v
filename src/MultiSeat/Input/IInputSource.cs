namespace MultiSeat.Input;

/// <summary>
/// One key or mouse event from a specific physical device.
/// Keyboard: <see cref="Code"/> = scan code, <see cref="State"/> = 1 up / 2 E0 / 4 E1.
/// Mouse: <see cref="State"/> = button flags (Windows MOUSE_*_BUTTON_* / wheel bits),
/// <see cref="Flags"/> = move flags (1 = absolute), <see cref="Rolling"/> = wheel delta.
/// <see cref="Captured"/>: true when Windows did NOT get this event (MultiSeat must deliver it somewhere).
/// </summary>
internal readonly record struct RawInput(
    int Device, bool Keyboard, bool Captured, ushort Code, ushort State, ushort Flags, short Rolling, int X, int Y);

/// <summary>A driver that tells us which keyboard/mouse produced each event and can keep devices away from Windows.</summary>
internal interface IInputSource : IDisposable
{
    /// <summary>Shown in the log, e.g. "MultiSeatInput driver".</summary>
    string Name { get; }

    /// <summary>
    /// True when every event passes through MultiSeat (Interception): events for the main desktop must be
    /// sent back with <see cref="Pass"/>, and keys held on the desktop must be released by MultiSeat.
    /// False (MultiSeatInput): devices that are not captured go straight to Windows in the kernel.
    /// </summary>
    bool RoutesEverything { get; }

    /// <summary>Waits up to <paramref name="timeoutMs"/> for the next event.</summary>
    bool TryRead(int timeoutMs, out RawInput input);

    /// <summary>Gives a captured event to the main desktop (only meaningful when <see cref="RoutesEverything"/>).</summary>
    void Pass(in RawInput input);

    /// <summary>Exactly these devices must be kept away from the main desktop; all others go to Windows.</summary>
    void SetCaptured(IReadOnlySet<int> devices);

    bool IsKeyboard(int device);

    string? HardwareId(int device);

    /// <summary>Keyboards and mice connected now (for the Workplaces view's spares).</summary>
    IReadOnlyList<MultiSeat.Core.InputDevice> Devices();
}
