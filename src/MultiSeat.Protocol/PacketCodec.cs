using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MultiSeat.Protocol;

/// <summary>
/// Encodes and authenticates input packets.
/// </summary>
/// <remarks>
/// Packet layout (little endian, 56 bytes):
/// <code>
///  0  u32  magic "MSR1"
///  4  u32  session id (random per router start)
///  8  u64  sequence number (strictly increasing within a session)
/// 16  u8   packet type
/// 17  u8   reserved (0)
/// 18  u16  code (scan code / mouse button)
/// 20  i32  x
/// 24  i32  y
/// 28  u32  flags
/// 32  i64  sender timestamp, Unix milliseconds
/// 40  [16] HMAC-SHA256(secret, bytes 0..39), truncated to 16 bytes
/// </code>
/// Each seat has its own 32-byte secret, so a packet for seat A can never be replayed on seat B.
/// </remarks>
public sealed class PacketCodec
{
    public const uint Magic = 0x3152534D; // "MSR1"
    public const int PayloadSize = 40;
    public const int TagSize = 16;
    public const int PacketSize = PayloadSize + TagSize;
    public const int MinSecretBytes = 16;

    private readonly byte[] _key;

    public PacketCodec(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length < MinSecretBytes)
        {
            throw new ArgumentException($"Secret must be at least {MinSecretBytes} bytes.", nameof(secret));
        }

        _key = (byte[])secret.Clone();
    }

    public static PacketCodec FromBase64(string secret) => new(ParseSecret(secret));

    public static byte[] ParseSecret(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            throw new ArgumentException("Secret is empty.", nameof(base64));
        }

        return Convert.FromBase64String(base64.Trim());
    }

    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public int Encode(uint session, ulong sequence, long timestampMs, in InputEvent e, Span<byte> destination)
    {
        if (destination.Length < PacketSize)
        {
            throw new ArgumentException($"Destination must be at least {PacketSize} bytes.", nameof(destination));
        }

        var payload = destination[..PayloadSize];
        payload.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(payload, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], session);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[8..], sequence);
        payload[16] = (byte)e.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(payload[18..], e.Code);
        BinaryPrimitives.WriteInt32LittleEndian(payload[20..], e.X);
        BinaryPrimitives.WriteInt32LittleEndian(payload[24..], e.Y);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[28..], e.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(payload[32..], timestampMs);

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_key, payload, mac);
        mac[..TagSize].CopyTo(destination.Slice(PayloadSize, TagSize));
        return PacketSize;
    }

    public bool TryDecode(ReadOnlySpan<byte> packet, out PacketHeader header, out InputEvent e)
    {
        header = default;
        e = default;
        if (packet.Length != PacketSize)
        {
            return false;
        }

        var payload = packet[..PayloadSize];
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload) != Magic)
        {
            return false;
        }

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_key, payload, mac);
        if (!CryptographicOperations.FixedTimeEquals(mac[..TagSize], packet.Slice(PayloadSize, TagSize)))
        {
            return false;
        }

        var type = (PacketType)payload[16];
        if (!Enum.IsDefined(type))
        {
            return false;
        }

        header = new PacketHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(payload[32..]));

        e = new InputEvent(
            type,
            BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[20..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[28..]));
        return true;
    }
}

public readonly record struct PacketHeader(uint Session, ulong Sequence, long TimestampMs);
