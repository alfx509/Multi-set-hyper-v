using System.Net;
using System.Net.Sockets;
using MultiSeat.Agent;
using MultiSeat.Protocol;

// SeatAgent runs inside each seat VM, in the logged-on user's session (scheduled task at logon),
// and injects the keyboard/mouse input that SeatRouter sends from the host.

var configPath = AgentConfig.DefaultPath;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--config" && i + 1 < args.Length)
    {
        configPath = args[++i];
    }
}

var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MultiSeat");
Directory.CreateDirectory(logDir);
using var log = new StreamWriter(Path.Combine(logDir, "agent.log"), append: true) { AutoFlush = true };
void Log(string message) => log.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");

AgentConfig config;
try
{
    config = AgentConfig.Load(configPath);
}
catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
{
    Log($"Cannot load {configPath}: {ex.Message}");
    return 1;
}

var codec = PacketCodec.FromBase64(config.Secret);
var guard = new ReplayGuard();
var injector = new Injector();

using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
socket.Bind(new IPEndPoint(IPAddress.Parse(config.BindAddress), config.Port));
socket.ReceiveTimeout = 250;
Log($"{config.Name}: listening on udp/{config.BindAddress}:{config.Port}");

var buffer = new byte[2048];
var lastPacket = Environment.TickCount64;
long rejected = 0;

while (true)
{
    int length;
    try
    {
        length = socket.Receive(buffer);
    }
    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.ConnectionReset)
    {
        length = 0;
    }

    var now = Environment.TickCount64;
    if (length == 0)
    {
        // Router gone (crashed, host mode, network hiccup): never leave keys stuck down.
        if (injector.HasPressed && now - lastPacket > config.WatchdogMs)
        {
            injector.ReleaseAll();
            Log("Watchdog: no packets, released all keys and buttons.");
        }

        continue;
    }

    if (!codec.TryDecode(buffer.AsSpan(0, length), out var header, out var e)
        || !guard.Accept(header, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
    {
        if (++rejected % 100 == 1)
        {
            Log($"Rejected packet(s): {rejected} total (wrong secret, clock skew or replay).");
        }

        continue;
    }

    lastPacket = now;
    if (guard.SessionChanged)
    {
        injector.ReleaseAll();
        Log($"Router session {header.Session:x8} connected.");
    }

    injector.Apply(e);
}
