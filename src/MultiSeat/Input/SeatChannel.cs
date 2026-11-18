using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MultiSeat.Core;

namespace MultiSeat.Input;

/// <summary>Sends authenticated input packets to one seat's agent on 127.0.0.1.</summary>
internal sealed class SeatChannel : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly PacketCodec _codec;
    private readonly uint _session = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
    private readonly object _gate = new();
    private ulong _sequence;

    public SeatChannel(Seat seat)
    {
        Seat = seat;
        _codec = PacketCodec.FromBase64(seat.AgentKey ?? throw new ArgumentException("Seat has no key."));
        _socket.Connect(new IPEndPoint(IPAddress.Loopback, seat.AgentPort));
    }

    public Seat Seat { get; }

    /// <summary>Ctrl+Alt+F12: the seat's keyboard/mouse temporarily control the main desktop.</summary>
    public volatile bool HostMode;

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
                // Agent not running yet (seat still signing in). Heartbeats keep trying.
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        Send(InputEvent.ReleaseAll());
        lock (_gate)
        {
            _socket.Dispose();
        }
    }
}
