using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MultiSeat.Protocol;

namespace MultiSeat.Router;

/// <summary>Sends authenticated input packets to one seat's agent over UDP.</summary>
internal sealed class SeatChannel : IDisposable
{
    private readonly Socket _socket;
    private readonly PacketCodec _codec;
    private readonly uint _session = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
    private readonly object _gate = new();
    private ulong _sequence;
    private long _sendErrors;

    public SeatChannel(SeatEndpoint endpoint)
    {
        Name = endpoint.Name;
        _codec = PacketCodec.FromBase64(endpoint.Secret);
        var address = IPAddress.TryParse(endpoint.Address, out var ip)
            ? ip
            : Dns.GetHostAddresses(endpoint.Address).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        EndPoint = new IPEndPoint(address, endpoint.Port);
        _socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.Connect(EndPoint);
    }

    public string Name { get; }

    public IPEndPoint EndPoint { get; }

    /// <summary>When true the seat's devices control the host instead of the VM (toggled with Ctrl+Alt+F12).</summary>
    public bool HostMode { get; set; }

    public long SendErrors => Interlocked.Read(ref _sendErrors);

    public void Send(in InputEvent e)
    {
        Span<byte> buffer = stackalloc byte[PacketCodec.PacketSize];
        lock (_gate)
        {
            _codec.Encode(_session, ++_sequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), e, buffer);
            try
            {
                _socket.Send(buffer);
            }
            catch (SocketException)
            {
                // The VM may be rebooting; heartbeats keep trying.
                Interlocked.Increment(ref _sendErrors);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            Send(InputEvent.ReleaseAll());
        }
        catch (ObjectDisposedException)
        {
        }

        _socket.Dispose();
    }
}
