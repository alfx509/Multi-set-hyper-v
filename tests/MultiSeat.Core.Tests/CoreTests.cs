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

    [Fact]
    public void SupportsEightPlayersButNotNine()
    {
        var config = MultiSeatConfig.CreateDefault();
        config.Seats[0].Monitor = "\\\\.\\DISPLAY1";
        for (var i = 2; i <= MultiSeatConfig.MaxSeats; i++)
        {
            config.Seats.Add(new Seat
            {
                Name = $"Player {i}",
                UserName = $"player{i}",
                Monitor = $"\\\\.\\DISPLAY{i}",
                Keyboard = new InputDevice(i, $"kb{i}"),
                Mouse = new InputDevice(10 + i, $"m{i}"),
            });
        }

        config.Normalize();
        Assert.Empty(config.Validate());
        Assert.Equal(8, config.Seats.Select(s => s.LoopbackAddress).Distinct().Count());

        config.Seats.Add(new Seat { Name = "Player 9", UserName = "player9", Monitor = "x", Keyboard = new InputDevice(1, "k"), Mouse = new InputDevice(11, "m") });
        config.Normalize();
        Assert.Contains(config.Validate(), p => p.Contains("up to 8"));
    }
}

public class InputDeviceTests
{
    [Fact]
    public void InterceptionSlotsKeepTheirMeaning()
    {
        Assert.True(new InputDevice(3, "k").IsKeyboard);
        Assert.Equal("Keyboard 3", new InputDevice(3, "k").Description);
        Assert.False(new InputDevice(12, "m").IsKeyboard);
        Assert.Equal("Mouse 2", new InputDevice(12, "m").Description);
    }

    [Fact]
    public void DriverDevicesUseTheirKind()
    {
        var mouse = new InputDevice(InputDevice.DriverSlotBase + 2, "HID-x", Keyboard: false);
        var keyboard = new InputDevice(InputDevice.DriverSlotBase + 7, "HID-y", Keyboard: true);
        Assert.False(mouse.IsKeyboard);
        Assert.Equal("Mouse 2", mouse.Description);
        Assert.True(keyboard.IsKeyboard);
        Assert.Equal("Keyboard 7", keyboard.Description);
    }

    [Fact]
    public void OldConfigWithoutKindStillLoads()
    {
        const string json = """{ "seats": [ { "name": "P2", "keyboard": { "slot": 2, "hardwareId": "HID-VID_1" } } ] }""";
        var config = System.Text.Json.JsonSerializer.Deserialize<MultiSeatConfig>(json, MultiSeatConfig.JsonOptions)!;
        Assert.Equal(new InputDevice(2, "HID-VID_1"), config.Seats[0].Keyboard);
        Assert.True(config.Seats[0].Keyboard!.IsKeyboard);
    }

    [Fact]
    public void KindSurvivesSaveAndLoad()
    {
        var config = new MultiSeatConfig();
        config.Seats.Add(new Seat { Name = "P2", Mouse = new InputDevice(105, "HID-m", Keyboard: false) });
        var back = System.Text.Json.JsonSerializer.Deserialize<MultiSeatConfig>(config.ToJson(), MultiSeatConfig.JsonOptions)!;
        Assert.Equal(new InputDevice(105, "HID-m", Keyboard: false), back.Seats.Single(s => s.Name == "P2").Mouse);
    }
}
