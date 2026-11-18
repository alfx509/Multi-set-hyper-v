using System.Text.Json;
using MultiSeat.Protocol;
using MultiSeat.Router;

const string Usage = """
SeatRouter - send each physical keyboard and mouse to its own Hyper-V gaming seat.

Usage:
  SeatRouter --config <router.json> [--pair]
  SeatRouter --list
  SeatRouter --new-secret

Options:
  --config <path>  Runtime config (written by scripts/host/Start-MultiSeat.ps1).
  --pair           Ignore saved pairing and pair keyboards/mice again.
  --list           List the keyboards and mice Interception can see, then exit.
  --new-secret     Print a new random seat secret, then exit.

While running:
  Ctrl+Alt+F12 on a seat keyboard  toggles that seat's keyboard+mouse between the VM and the host.
  Ctrl+C in this window            stops routing; every device returns to the host.
""";

string? configPath = null;
var forcePair = false;
var list = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        case "--pair":
            forcePair = true;
            break;
        case "--list":
            list = true;
            break;
        case "--new-secret":
            Console.WriteLine(PacketCodec.NewSecret());
            return 0;
        default:
            Console.WriteLine(Usage);
            return args[i] is "-h" or "--help" or "/?" ? 0 : 2;
    }
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("SeatRouter only runs on Windows (Hyper-V host).");
    return 1;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    using var router = new InputRouter();

    if (list)
    {
        foreach (var (device, hardwareId) in router.ListDevices())
        {
            Console.WriteLine($"{Interception.DeviceLabel(device),-14} {hardwareId}");
        }

        return 0;
    }

    if (configPath is null)
    {
        Console.WriteLine(Usage);
        return 2;
    }

    var config = RouterConfig.Load(configPath);
    var channels = config.Seats.ToDictionary(s => s.Name, s => new SeatChannel(s), StringComparer.OrdinalIgnoreCase);
    try
    {
        var pairings = LoadPairing(config.PairingFile!);
        var seatNames = config.Seats.Select(s => s.Name).ToList();
        var complete = pairings is not null
            && seatNames.All(n => pairings.Any(p => string.Equals(p.Seat, n, StringComparison.OrdinalIgnoreCase)))
            && router.IsPairingValid(pairings);

        if (forcePair || !complete)
        {
            Console.WriteLine("Pairing keyboards and mice with seats. Each player uses their own devices.");
            pairings = router.Pair(seatNames, cancel.Token);
            if (cancel.IsCancellationRequested)
            {
                return 1;
            }

            File.WriteAllText(config.PairingFile!, JsonSerializer.Serialize(pairings, RouterConfig.JsonOptions));
            Console.WriteLine($"Pairing saved to {config.PairingFile}");
        }

        router.Apply(pairings!, channels);

        Console.WriteLine();
        foreach (var p in pairings!)
        {
            var target = channels.TryGetValue(p.Seat, out var c) ? c.EndPoint.ToString() : "?";
            var kb = p.Keyboard > 0 ? Interception.DeviceLabel(p.Keyboard) : "-";
            var mouse = p.Mouse > 0 ? Interception.DeviceLabel(p.Mouse) : "-";
            Console.WriteLine($"  {p.Seat,-10} {kb,-14} {mouse,-12} -> {target}");
        }

        Console.WriteLine();
        InputRouter.Log("Routing. Ctrl+Alt+F12 on a seat keyboard = host mode. Ctrl+C here = stop.");

        using var heartbeat = new Timer(
            _ =>
            {
                foreach (var channel in channels.Values)
                {
                    channel.Send(InputEvent.Heartbeat());
                }
            },
            null,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(500));

        router.Run(cancel.Token);
        InputRouter.Log("Stopped. All devices are back on the host.");
        return 0;
    }
    finally
    {
        foreach (var channel in channels.Values)
        {
            channel.Dispose();
        }
    }
}
catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or JsonException or FormatException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static List<SeatPairing>? LoadPairing(string path)
{
    if (!File.Exists(path))
    {
        return null;
    }

    try
    {
        return JsonSerializer.Deserialize<List<SeatPairing>>(File.ReadAllText(path), RouterConfig.JsonOptions);
    }
    catch (JsonException)
    {
        return null;
    }
}
