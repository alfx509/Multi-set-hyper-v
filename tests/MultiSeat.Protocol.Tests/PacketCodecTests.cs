using MultiSeat.Protocol;
using Xunit;

namespace MultiSeat.Protocol.Tests;

public class PacketCodecTests
{
    private static readonly string Secret = PacketCodec.NewSecret();

    [Theory]
    [InlineData(PacketType.Key, (ushort)0x1E, 0, 0, 1u)]
    [InlineData(PacketType.MouseMove, (ushort)0, -12, 7, 0u)]
    [InlineData(PacketType.MouseMoveAbsolute, (ushort)0, 65535, 0, 0u)]
    [InlineData(PacketType.Wheel, (ushort)0, -120, 0, 0u)]
    public void RoundTrips(PacketType type, ushort code, int x, int y, uint flags)
    {
        var codec = PacketCodec.FromBase64(Secret);
        var sent = new InputEvent(type, code, x, y, flags);
        var buffer = new byte[PacketCodec.PacketSize];

        codec.Encode(0xCAFE, 42, 1_700_000_000_000, sent, buffer);

        Assert.True(codec.TryDecode(buffer, out var header, out var received));
        Assert.Equal(sent, received);
        Assert.Equal(0xCAFEu, header.Session);
        Assert.Equal(42ul, header.Sequence);
        Assert.Equal(1_700_000_000_000, header.TimestampMs);
    }

    [Fact]
    public void RejectsTamperedPacket()
    {
        var codec = PacketCodec.FromBase64(Secret);
        var buffer = new byte[PacketCodec.PacketSize];
        codec.Encode(1, 1, 0, InputEvent.Key(0x1E, KeyFlags.None), buffer);

        buffer[18] ^= 0xFF; // change the scan code

        Assert.False(codec.TryDecode(buffer, out _, out _));
    }

    [Fact]
    public void RejectsOtherSeatsSecret()
    {
        var buffer = new byte[PacketCodec.PacketSize];
        PacketCodec.FromBase64(Secret).Encode(1, 1, 0, InputEvent.Heartbeat(), buffer);

        Assert.False(PacketCodec.FromBase64(PacketCodec.NewSecret()).TryDecode(buffer, out _, out _));
    }

    [Fact]
    public void RejectsShortSecret()
    {
        Assert.Throws<ArgumentException>(() => new PacketCodec(new byte[8]));
    }
}

public class ReplayGuardTests
{
    private const long Now = 1_700_000_000_000;

    [Fact]
    public void AcceptsIncreasingSequenceOnly()
    {
        var guard = new ReplayGuard();
        Assert.True(guard.Accept(new PacketHeader(7, 1, Now), Now));
        Assert.True(guard.SessionChanged);
        Assert.True(guard.Accept(new PacketHeader(7, 2, Now), Now));
        Assert.False(guard.SessionChanged);
        Assert.False(guard.Accept(new PacketHeader(7, 2, Now), Now));
        Assert.False(guard.Accept(new PacketHeader(7, 1, Now), Now));
    }

    [Fact]
    public void RejectsStaleTimestamp()
    {
        var guard = new ReplayGuard(TimeSpan.FromSeconds(5));
        Assert.False(guard.Accept(new PacketHeader(7, 1, Now - 6_000), Now));
    }

    [Fact]
    public void NewSessionMustNotBeOlderThanCurrent()
    {
        var guard = new ReplayGuard();
        Assert.True(guard.Accept(new PacketHeader(1, 10, Now), Now));

        // Replayed packet from an earlier router session.
        Assert.False(guard.Accept(new PacketHeader(2, 99, Now - 1_000), Now));

        // Router restarted: new session with a fresh timestamp.
        Assert.True(guard.Accept(new PacketHeader(3, 1, Now + 10), Now + 10));
        Assert.True(guard.SessionChanged);
    }
}
