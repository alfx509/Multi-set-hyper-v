using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MultiSeat.Input;

/// <summary>
/// Input from our own MultiSeatInput kernel filter (drivers/MultiSeatInput, see public.h for the protocol).
/// Devices that are not captured go to Windows inside the kernel; MultiSeat only receives copies of them
/// (monitor mode, for pairing and the Ctrl+Alt+F12 hotkey) plus the captured devices' input.
/// Closing the handle (exit or crash) gives every device back to Windows.
/// </summary>
internal sealed class DriverInputSource : IInputSource
{
    /// <summary>Device numbers in the config are 100 + driver id, so they never mix with Interception slots (1-20).</summary>
    public const int SlotBase = MultiSeat.Core.InputDevice.DriverSlotBase;

    private const string DevicePath = @"\\.\MultiSeatInput";
    private const uint DeviceType = 0x8A51;
    private const int EventSize = 24;
    private const int InfoSize = 12 + (128 * 2);
    private const int ModePass = 0;
    private const int ModeCapture = 1;
    private const int ErrorIoPending = 997;

    private static readonly uint IoctlVersion = Ctl(0x800, 1);
    private static readonly uint IoctlList = Ctl(0x801, 1);
    private static readonly uint IoctlSetMode = Ctl(0x802, 2);
    private static readonly uint IoctlMonitor = Ctl(0x803, 2);
    private static readonly uint IoctlRead = Ctl(0x804, 1);

    private readonly SafeFileHandle _handle;
    private readonly object _devicesGate = new();
    private readonly Dictionary<int, (bool Keyboard, int Mode, string HardwareId)> _devices = new();
    private readonly Queue<RawInput> _pending = new();

    // The one outstanding read: its buffer and OVERLAPPED stay in unmanaged memory while the driver owns them.
    private readonly IntPtr _readBuffer = Marshal.AllocHGlobal(EventSize * 256);
    private readonly IntPtr _readOverlapped;
    private readonly ManualResetEvent _readDone = new(false);
    private bool _readPending;
    private bool _disposed;

    private DriverInputSource(SafeFileHandle handle)
    {
        _handle = handle;
        _readOverlapped = AllocOverlapped(_readDone);
        Ioctl(IoctlMonitor, BitConverter.GetBytes(1u), 0); // copies of every device's input: pairing + hotkey
        RefreshDevices();
    }

    public string Name => "MultiSeatInput driver";

    public bool RoutesEverything => false;

    /// <summary>Opens the driver if it is installed and running; null (no error) when it is not.</summary>
    public static DriverInputSource? TryCreate(out string? error)
    {
        error = null;
        var handle = Native.CreateFile(DevicePath, 0xC0000000 /* read|write */, 0, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x40000000 /* FILE_FLAG_OVERLAPPED */, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (code is 2 or 3) // not installed / not running
            {
                return null;
            }

            error = code == 5
                ? "The MultiSeatInput driver is already used by another program (only one may open it), or MultiSeat is not running as Administrator."
                : $"Cannot open the MultiSeatInput driver: {new Win32Exception(code).Message}";
            return null;
        }

        try
        {
            var source = new DriverInputSource(handle);
            var version = BitConverter.ToUInt32(source.Ioctl(IoctlVersion, Array.Empty<byte>(), 4));
            if (version != 1)
            {
                source.Dispose();
                error = $"The MultiSeatInput driver has version {version}; this MultiSeat needs version 1. Install the matching build.";
                return null;
            }

            return source;
        }
        catch (Win32Exception ex)
        {
            handle.Dispose();
            error = $"The MultiSeatInput driver does not answer: {ex.Message}";
            return null;
        }
    }

    public bool TryRead(int timeoutMs, out RawInput input)
    {
        if (_pending.Count == 0)
        {
            if (!_readPending)
            {
                StartRead();
            }

            if (_readPending && _readDone.WaitOne(timeoutMs))
            {
                _readPending = false;
                if (Native.GetOverlappedResult(_handle, _readOverlapped, out var bytes, false))
                {
                    Parse(bytes);
                }
            }
        }

        return _pending.TryDequeue(out input);
    }

    public void Pass(in RawInput input)
    {
        // Not possible (and not needed): devices that are not captured already go to Windows in the kernel.
    }

    public void SetCaptured(IReadOnlySet<int> devices)
    {
        RefreshDevices();
        List<(int Id, int Mode)> changes;
        lock (_devicesGate)
        {
            changes = _devices
                .Select(d => (d.Key, Want: devices.Contains(SlotBase + d.Key) ? ModeCapture : ModePass, d.Value.Mode))
                .Where(d => d.Want != d.Mode)
                .Select(d => (d.Key, d.Want))
                .ToList();
        }

        foreach (var (id, mode) in changes)
        {
            var input = new byte[8];
            BitConverter.GetBytes((uint)id).CopyTo(input, 0);
            BitConverter.GetBytes((uint)mode).CopyTo(input, 4);
            try
            {
                Ioctl(IoctlSetMode, input, 0); // switching to CAPTURE also releases keys Windows thinks are held
            }
            catch (Win32Exception)
            {
                // Unplugged meanwhile.
            }
        }

        RefreshDevices();
    }

    public bool IsKeyboard(int device) => Lookup(device)?.Keyboard ?? false;

    public string? HardwareId(int device) => Lookup(device)?.HardwareId;

    private (bool Keyboard, int Mode, string HardwareId)? Lookup(int device)
    {
        var id = device - SlotBase;
        lock (_devicesGate)
        {
            if (_devices.TryGetValue(id, out var d))
            {
                return d;
            }
        }

        RefreshDevices(); // plugged in after the last refresh?
        lock (_devicesGate)
        {
            return _devices.TryGetValue(id, out var d) ? d : null;
        }
    }

    private void RefreshDevices()
    {
        byte[] buffer;
        try
        {
            buffer = Ioctl(IoctlList, Array.Empty<byte>(), InfoSize * 128);
        }
        catch (Win32Exception)
        {
            return;
        }

        lock (_devicesGate)
        {
            _devices.Clear();
            for (var off = 0; off + InfoSize <= buffer.Length; off += InfoSize)
            {
                var kind = BitConverter.ToUInt32(buffer, off + 4);
                if (kind is not (1 or 2))
                {
                    continue; // not connected to Windows yet
                }

                var hwid = Encoding.Unicode.GetString(buffer, off + 12, 256).Split('\0')[0];
                _devices[(int)BitConverter.ToUInt32(buffer, off)] = (kind == 1, (int)BitConverter.ToUInt32(buffer, off + 8), hwid);
            }
        }
    }

    private void StartRead()
    {
        if (Native.DeviceIoControl(_handle, IoctlRead, IntPtr.Zero, 0, _readBuffer, EventSize * 256, out var bytes, _readOverlapped))
        {
            Parse(bytes); // completed at once
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorIoPending)
        {
            _readPending = true;
            return;
        }

        throw new Win32Exception(error);
    }

    private void Parse(int bytes)
    {
        var data = new byte[bytes];
        Marshal.Copy(_readBuffer, data, 0, bytes);
        for (var off = 0; off + EventSize <= bytes; off += EventSize)
        {
            var device = SlotBase + (int)BitConverter.ToUInt32(data, off);
            var keyboard = BitConverter.ToUInt16(data, off + 4) == 1;
            var flags = BitConverter.ToUInt16(data, off + 6);
            var captured = BitConverter.ToUInt16(data, off + 14) == 1;
            _pending.Enqueue(keyboard
                ? new RawInput(device, true, captured, BitConverter.ToUInt16(data, off + 8), flags, 0, 0, 0, 0)
                : new RawInput(device, false, captured, 0, BitConverter.ToUInt16(data, off + 10), flags,
                    BitConverter.ToInt16(data, off + 12), BitConverter.ToInt32(data, off + 16), BitConverter.ToInt32(data, off + 20)));
        }
    }

    /// <summary>A blocking DeviceIoControl on the overlapped handle (never waits behind the pending read).</summary>
    private byte[] Ioctl(uint code, byte[] input, int outputSize)
    {
        using var done = new ManualResetEvent(false);
        var overlapped = AllocOverlapped(done);
        var inBuf = Marshal.AllocHGlobal(Math.Max(input.Length, 1));
        var outBuf = Marshal.AllocHGlobal(Math.Max(outputSize, 1));
        try
        {
            Marshal.Copy(input, 0, inBuf, input.Length);
            if (!Native.DeviceIoControl(_handle, code, inBuf, input.Length, outBuf, outputSize, out var bytes, overlapped))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorIoPending || !Native.GetOverlappedResult(_handle, overlapped, out bytes, true))
                {
                    throw new Win32Exception(error == ErrorIoPending ? Marshal.GetLastWin32Error() : error);
                }
            }

            var output = new byte[bytes];
            Marshal.Copy(outBuf, output, 0, bytes);
            return output;
        }
        finally
        {
            Marshal.FreeHGlobal(overlapped);
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    private static IntPtr AllocOverlapped(WaitHandle done)
    {
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        Marshal.StructureToPtr(new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() }, ptr, false);
        return ptr;
    }

    private static uint Ctl(uint function, uint access) => (DeviceType << 16) | (access << 14) | (function << 2); // METHOD_BUFFERED

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_readPending)
        {
            // The driver must be done with the buffer before it is freed.
            Native.CancelIoEx(_handle, _readOverlapped);
            Native.GetOverlappedResult(_handle, _readOverlapped, out _, true);
        }

        _handle.Dispose(); // the driver puts every device back on PASS
        Marshal.FreeHGlobal(_readOverlapped);
        Marshal.FreeHGlobal(_readBuffer);
        _readDone.Dispose();
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out int transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
    }
}
