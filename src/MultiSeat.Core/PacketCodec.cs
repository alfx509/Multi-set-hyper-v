using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MultiSeat.Core;

/// <summary>
/// 56-byte authenticated packet:
/// magic(4) session(4) sequence(8) type(1) reserved(1) code(2) x(4) y(4) flags(4) timestampMs(8) | HMAC-SHA256 tag(16).
/// Every seat has its own key, so only that seat's agent accepts its packets.
/// </summary>
public sealed class PacketCodec
{
    public const uint Magic = 0x3253534D; // "MSS2"
    public const int PayloadSize = 40;
    public const int TagSize = 16;
    public const int PacketSize = PayloadSize + TagSize;

    private readonly byte[] _key;

    public PacketCodec(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 16)
        {
            throw new ArgumentException("Key must be at least 16 bytes.", nameof(key));
        }

        _key = (byte[])key.Clone();
    }

    public static PacketCodec FromBase64(string key) => new(Convert.FromBase64String(key.Trim()));

    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public void Encode(uint session, ulong sequence, long timestampMs, in InputEvent e, Span<byte> destination)
    {
        if (destination.Length < PacketSize)
        {
            throw new ArgumentException("Destination too small.", nameof(destination));
        }

        var p = destination[..PayloadSize];
        p.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(p, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], session);
        BinaryPrimitives.WriteUInt64LittleEndian(p[8..], sequence);
        p[16] = (byte)e.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(p[18..], e.Code);
        BinaryPrimitives.WriteInt32LittleEndian(p[20..], e.X);
        BinaryPrimitives.WriteInt32LittleEndian(p[24..], e.Y);
        BinaryPrimitives.WriteUInt32LittleEndian(p[28..], e.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(p[32..], timestampMs);

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_key, p, mac);
        mac[..TagSize].CopyTo(destination.Slice(PayloadSize, TagSize));
    }

    public bool TryDecode(ReadOnlySpan<byte> packet, out PacketHeader header, out InputEvent e)
    {
        header = default;
        e = default;
        if (packet.Length != PacketSize)
        {
            return false;
        }

        var p = packet[..PayloadSize];
        if (BinaryPrimitives.ReadUInt32LittleEndian(p) != Magic)
        {
            return false;
        }

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_key, p, mac);
        if (!CryptographicOperations.FixedTimeEquals(mac[..TagSize], packet.Slice(PayloadSize, TagSize)))
        {
            return false;
        }

        var type = (PacketType)p[16];
        if (!Enum.IsDefined(type))
        {
            return false;
        }

        header = new PacketHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadUInt64LittleEndian(p[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(p[32..]));
        e = new InputEvent(
            type,
            BinaryPrimitives.ReadUInt16LittleEndian(p[18..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[20..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[28..]));
        return true;
    }
}

public readonly record struct PacketHeader(uint Session, ulong Sequence, long TimestampMs);

/// <summary>Rejects stale, repeated or replayed packets.</summary>
public sealed class ReplayGuard
{
    private readonly long _maxSkewMs;
    private bool _hasSession;
    private uint _session;
    private ulong _lastSequence;
    private long _lastTimestampMs = long.MinValue;

    public ReplayGuard(TimeSpan? maxClockSkew = null)
    {
        _maxSkewMs = (long)(maxClockSkew ?? TimeSpan.FromSeconds(10)).TotalMilliseconds;
    }

    /// <summary>True when the last accepted packet came from a new manager session.</summary>
    public bool SessionChanged { get; private set; }

    public bool Accept(in PacketHeader h, long nowMs)
    {
        SessionChanged = false;
        if (Math.Abs(nowMs - h.TimestampMs) > _maxSkewMs)
        {
            return false;
        }

        if (!_hasSession || h.Session != _session)
        {
            if (h.TimestampMs < _lastTimestampMs)
            {
                return false;
            }

            _hasSession = true;
            _session = h.Session;
            _lastSequence = h.Sequence;
            _lastTimestampMs = h.TimestampMs;
            SessionChanged = true;
            return true;
        }

        if (h.Sequence <= _lastSequence)
        {
            return false;
        }

        _lastSequence = h.Sequence;
        _lastTimestampMs = Math.Max(_lastTimestampMs, h.TimestampMs);
        return true;
    }
}
