using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// InputProbe: test tool for the MultiSeatInput kernel driver (drivers/MultiSeatInput).
//   InputProbe list                  – keyboards/mice the driver sees
//   InputProbe watch [seconds]       – show every key/mouse event with its device id (nothing is taken away)
//   InputProbe capture <id> [secs]   – take one device away from Windows for a few seconds and show its input
// Run as Administrator. Closing the tool always gives every device back to Windows.

const uint DeviceType = 0x8A51;
static uint Ctl(uint function, uint access) => (DeviceType << 16) | (access << 14) | (function << 2); // METHOD_BUFFERED = 0
uint ioctlVersion = Ctl(0x800, 1), ioctlList = Ctl(0x801, 1), ioctlSetMode = Ctl(0x802, 2), ioctlMonitor = Ctl(0x803, 2), ioctlRead = Ctl(0x804, 1);

using var handle = Native.CreateFile(@"\\.\MultiSeatInput", 0xC0000000 /* read|write */, 0, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
if (handle.IsInvalid)
{
    Console.Error.WriteLine($"Cannot open \\\\.\\MultiSeatInput: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    Console.Error.WriteLine("Is the driver installed (setup\\dev\\Install-InputDriver.ps1), the PC restarted, and this tool run as Administrator?");
    return 1;
}

var version = new byte[4];
Native.Ioctl(handle, ioctlVersion, Array.Empty<byte>(), version);
Console.WriteLine($"MultiSeatInput driver version {BitConverter.ToUInt32(version)}");

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
switch (command)
{
    case "list":
        foreach (var d in ListDevices())
        {
            Console.WriteLine($"  id {d.Id,3}  {d.Kind,-9} {d.Mode,-8} {d.HardwareId}");
        }

        break;

    case "watch":
        Native.Ioctl(handle, ioctlMonitor, BitConverter.GetBytes(1u), Array.Empty<byte>());
        ReadEvents(TimeSpan.FromSeconds(args.Length > 1 ? int.Parse(args[1]) : 15));
        break;

    case "capture" when args.Length > 1:
        var id = uint.Parse(args[1]);
        var seconds = args.Length > 2 ? int.Parse(args[2]) : 10;
        Console.WriteLine($"Device {id} is now taken away from Windows for {seconds} s. Its input is shown here.");
        SetMode(id, 1);
        try
        {
            ReadEvents(TimeSpan.FromSeconds(seconds));
        }
        finally
        {
            SetMode(id, 0);
            Console.WriteLine($"Device {id} is back on Windows.");
        }

        break;

    // route <id> <agent-settings.json> [seconds]: capture one device and send its input to a seat's SeatAgent
    // (same authenticated UDP packets as the MultiSeat manager). Phase 2, B4.
    case "route" when args.Length > 2:
    {
        var routeId = uint.Parse(args[1]);
        var settings = System.Text.Json.JsonSerializer.Deserialize<MultiSeat.Core.AgentSettings>(
            File.ReadAllText(args[2]), MultiSeat.Core.MultiSeatConfig.JsonOptions) ?? throw new InvalidDataException(args[2]);
        var routeSeconds = args.Length > 3 ? int.Parse(args[3]) : 30;
        using var router = new SeatSender(settings);
        Console.WriteLine($"Device {routeId} -> {settings.Seat} (127.0.0.1:{settings.Port}) for {routeSeconds} s.");
        using var heartbeat = new Timer(_ => router.Send(MultiSeat.Core.InputEvent.Heartbeat()), null, 0, 500);
        SetMode(routeId, 1);
        try
        {
            ReadEvents(TimeSpan.FromSeconds(routeSeconds), router.Forward);
        }
        finally
        {
            SetMode(routeId, 0);
            router.Send(MultiSeat.Core.InputEvent.ReleaseAll());
            Console.WriteLine($"Device {routeId} is back on Windows. Sent {router.Count} event(s).");
        }

        break;
    }

    default:
        Console.WriteLine("Usage: InputProbe list | watch [seconds] | capture <id> [seconds] | route <id> <agent-settings.json> [seconds]");
        return 2;
}

return 0;

List<(uint Id, string Kind, string Mode, string HardwareId)> ListDevices()
{
    const int infoSize = 12 + (128 * 2);
    var buffer = new byte[infoSize * 64];
    var bytes = Native.Ioctl(handle, ioctlList, Array.Empty<byte>(), buffer);
    var result = new List<(uint, string, string, string)>();
    for (var off = 0; off + infoSize <= bytes; off += infoSize)
    {
        var kind = BitConverter.ToUInt32(buffer, off + 4) switch { 1 => "keyboard", 2 => "mouse", _ => "unknown" };
        var mode = BitConverter.ToUInt32(buffer, off + 8) == 1 ? "CAPTURE" : "pass";
        var hwid = System.Text.Encoding.Unicode.GetString(buffer, off + 12, 256).Split('\0')[0];
        result.Add((BitConverter.ToUInt32(buffer, off), kind, mode, hwid));
    }

    return result;
}

void SetMode(uint id, uint mode)
{
    var input = new byte[8];
    BitConverter.GetBytes(id).CopyTo(input, 0);
    BitConverter.GetBytes(mode).CopyTo(input, 4);
    Native.Ioctl(handle, ioctlSetMode, input, Array.Empty<byte>());
}

void ReadEvents(TimeSpan duration, Action<RawEvent>? handler = null)
{
    // Reads block until input arrives; a background thread does the reading, the main thread keeps time.
    using var stop = new CancellationTokenSource(duration);
    var reader = new Thread(() =>
    {
        var buffer = new byte[24 * 256];
        while (!stop.IsCancellationRequested)
        {
            int n;
            try
            {
                n = Native.Ioctl(handle, ioctlRead, Array.Empty<byte>(), buffer);
            }
            catch (Win32Exception)
            {
                return;
            }

            for (var off = 0; off + 24 <= n; off += 24)
            {
                var dev = BitConverter.ToUInt32(buffer, off);
                var kind = BitConverter.ToUInt16(buffer, off + 4);
                var flags = BitConverter.ToUInt16(buffer, off + 6);
                var captured = BitConverter.ToUInt16(buffer, off + 14) == 1 ? "CAPTURED" : "copy";
                handler?.Invoke(new RawEvent(kind == 1, flags, BitConverter.ToUInt16(buffer, off + 8),
                    BitConverter.ToUInt16(buffer, off + 10), BitConverter.ToInt16(buffer, off + 12),
                    BitConverter.ToInt32(buffer, off + 16), BitConverter.ToInt32(buffer, off + 20)));
                if (kind == 1)
                {
                    var code = BitConverter.ToUInt16(buffer, off + 8);
                    Console.WriteLine($"  dev {dev,3} key   scan 0x{code:X2}{((flags & 2) != 0 ? " E0" : "")} {((flags & 1) != 0 ? "up  " : "down")}  {captured}");
                }
                else
                {
                    var buttons = BitConverter.ToUInt16(buffer, off + 10);
                    var wheel = BitConverter.ToInt16(buffer, off + 12);
                    var x = BitConverter.ToInt32(buffer, off + 16);
                    var y = BitConverter.ToInt32(buffer, off + 20);
                    var pos = (flags & 1) != 0 ? $"abs x {x,5}  y {y,5}" : $"rel dx {x,5} dy {y,5}"; // MOUSE_MOVE_ABSOLUTE = 1
                    Console.WriteLine($"  dev {dev,3} mouse {pos} buttons 0x{buttons:X3} wheel {wheel,4}  {captured}");
                }
            }
        }
    })
    { IsBackground = true };
    reader.Start();
    stop.Token.WaitHandle.WaitOne();

    // The handle is synchronous, so a pending read blocks other calls: cancel it and wait first.
    Native.CancelIoEx(handle, IntPtr.Zero);
    reader.Join(2000);
    Native.Ioctl(handle, ioctlMonitor, BitConverter.GetBytes(0u), Array.Empty<byte>());
}

/// <summary>One driver event (see drivers/MultiSeatInput/public.h, MSI_EVENT).</summary>
internal readonly record struct RawEvent(bool Keyboard, ushort Flags, ushort MakeCode, ushort Buttons, short Wheel, int X, int Y);

/// <summary>Sends a seat's input to its SeatAgent like the manager's InputRouter does (HMAC-authenticated UDP).</summary>
internal sealed class SeatSender : IDisposable
{
    private readonly System.Net.Sockets.Socket _socket = new(System.Net.Sockets.AddressFamily.InterNetwork,
        System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
    private readonly MultiSeat.Core.PacketCodec _codec;
    private readonly uint _session = BitConverter.ToUInt32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
    private readonly object _gate = new();
    private ulong _sequence;

    public SeatSender(MultiSeat.Core.AgentSettings settings)
    {
        _codec = MultiSeat.Core.PacketCodec.FromBase64(settings.Key);
        _socket.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, settings.Port));
    }

    public int Count { get; private set; }

    public void Send(in MultiSeat.Core.InputEvent e)
    {
        Span<byte> buffer = stackalloc byte[MultiSeat.Core.PacketCodec.PacketSize];
        lock (_gate)
        {
            _codec.Encode(_session, ++_sequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), e, buffer);
            try
            {
                _socket.Send(buffer);
            }
            catch (System.Net.Sockets.SocketException)
            {
                // agent not listening yet
            }
        }
    }

    // Same mapping as src/MultiSeat/Input/InputRouter.cs.
    public void Forward(RawEvent r)
    {
        Count++;
        if (r.Keyboard)
        {
            Send(MultiSeat.Core.InputEvent.Key(r.MakeCode, (MultiSeat.Core.KeyFlags)(r.Flags & 0x7)));
            return;
        }

        if ((r.Flags & 1) != 0)
        {
            Send(MultiSeat.Core.InputEvent.MoveAbsolute(r.X, r.Y));
        }
        else if (r.X != 0 || r.Y != 0)
        {
            Send(MultiSeat.Core.InputEvent.Move(r.X, r.Y));
        }

        (ushort Down, ushort Up, MultiSeat.Core.MouseButton Button)[] buttons =
        {
            (0x001, 0x002, MultiSeat.Core.MouseButton.Left), (0x004, 0x008, MultiSeat.Core.MouseButton.Right),
            (0x010, 0x020, MultiSeat.Core.MouseButton.Middle), (0x040, 0x080, MultiSeat.Core.MouseButton.X1),
            (0x100, 0x200, MultiSeat.Core.MouseButton.X2),
        };
        foreach (var (down, up, button) in buttons)
        {
            if ((r.Buttons & down) != 0)
            {
                Send(MultiSeat.Core.InputEvent.Button(button, false));
            }

            if ((r.Buttons & up) != 0)
            {
                Send(MultiSeat.Core.InputEvent.Button(button, true));
            }
        }

        if ((r.Buttons & 0x400) != 0)
        {
            Send(MultiSeat.Core.InputEvent.Wheel(r.Wheel));
        }

        if ((r.Buttons & 0x800) != 0)
        {
            Send(MultiSeat.Core.InputEvent.HWheel(r.Wheel));
        }
    }

    public void Dispose() => _socket.Dispose();
}

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize, byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);

    public static int Ioctl(SafeFileHandle h, uint code, byte[] input, byte[] output)
    {
        if (!DeviceIoControl(h, code, input, input.Length, output, output.Length, out var returned, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return returned;
    }
}
