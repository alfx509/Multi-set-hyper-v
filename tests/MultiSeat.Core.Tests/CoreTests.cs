using MultiSeat.Core;
using Xunit;

namespace MultiSeat.Core.Tests;

public class PacketTests
{
    private static readonly string Key = PacketCodec.NewKey();

    [Fact]
    public void RoundTrip()
    {
        var codec = PacketCodec.FromBase64(Key);
        var buffer = new byte[PacketCodec.PacketSize];
        var sent = InputEvent.Key(0x1E, KeyFlags.Up | KeyFlags.Extended0);
        codec.Encode(5, 9, 123, sent, buffer);
        Assert.True(codec.TryDecode(buffer, out var header, out var got));
        Assert.Equal(sent, got);
        Assert.Equal(new PacketHeader(5, 9, 123), header);
    }

    [Fact]
    public void RejectsTamperingAndOtherSeatsKey()
    {
        var buffer = new byte[PacketCodec.PacketSize];
        PacketCodec.FromBase64(Key).Encode(1, 1, 0, InputEvent.Move(3, 4), buffer);
        Assert.False(PacketCodec.FromBase64(PacketCodec.NewKey()).TryDecode(buffer, out _, out _));
        buffer[20] ^= 1;
        Assert.False(PacketCodec.FromBase64(Key).TryDecode(buffer, out _, out _));
    }

    [Fact]
    public void ReplayGuard()
    {
        const long now = 1_700_000_000_000;
        var g = new ReplayGuard();
        Assert.True(g.Accept(new PacketHeader(1, 1, now), now));
        Assert.False(g.Accept(new PacketHeader(1, 1, now), now));
        Assert.False(g.Accept(new PacketHeader(2, 5, now - 500), now));
        Assert.False(g.Accept(new PacketHeader(3, 1, now - 60_000), now));
        Assert.True(g.Accept(new PacketHeader(4, 1, now + 1), now + 1));
        Assert.True(g.SessionChanged);
    }
}

public class ConfigTests
{
    [Fact]
    public void ConsoleSeatIsFirstAndSeatsGetStableNumbers()
    {
        var config = new MultiSeatConfig
        {
            Seats =
            {
                new Seat { Name = "B", UserName = "b" },
                new Seat { Name = "Main", IsConsole = true },
                new Seat { Name = "C", UserName = "c" },
            },
        };
        config.Normalize();

        Assert.True(config.Seats[0].IsConsole);
        Assert.Equal("127.0.0.2", config.Seats[1].LoopbackAddress);
        Assert.Equal("127.0.0.3", config.Seats[2].LoopbackAddress);

        // Removing seat B must not change seat C's address (its saved password depends on it).
        config.Seats.RemoveAt(1);
        config.Normalize();
        Assert.Equal("127.0.0.3", config.Seats[1].LoopbackAddress);

        // A new seat takes the free number.
        config.Seats.Add(new Seat { Name = "D", UserName = "d" });
        config.Normalize();
        Assert.Equal("127.0.0.2", config.Seats[2].LoopbackAddress);
        Assert.NotEqual(config.Seats[1].AgentKey, config.Seats[2].AgentKey);
    }

    [Fact]
    public void ValidationFindsSharedDevices()
    {
        var kb = new InputDevice(2, "HID\\VID_1");
        var config = new MultiSeatConfig
        {
            Seats =
            {
                new Seat { Name = "Main", IsConsole = true, Monitor = "\\\\.\\DISPLAY1" },
                new Seat { Name = "P2", UserName = "p2", Monitor = "\\\\.\\DISPLAY2", Keyboard = kb },
                new Seat { Name = "P3", UserName = "p3", Monitor = "\\\\.\\DISPLAY3", Keyboard = kb },
            },
        };
        config.Normalize();
        Assert.Contains(config.Validate(), p => p.Contains("two seats"));
    }

    [Fact]
    public void RdpFileTargetsSeatMonitor()
    {
        var config = new MultiSeatConfig { Seats = { new Seat { IsConsole = true, Name = "Main" }, new Seat { Name = "P2", UserName = "p2" } } };
        config.Normalize();
        var rdp = RdpFile.Build(config.Seats[1], new RdpFile.Bounds(1920, 0, 2560, 1440));
        Assert.Contains("full address:s:127.0.0.2", rdp);
        Assert.Contains("winposstr:s:0,1,1920,0,4480,1440", rdp);
        Assert.Contains("desktopwidth:i:2560", rdp);
        Assert.Contains("audiocapturemode:i:1", rdp);
    }
}
