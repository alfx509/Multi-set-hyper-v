using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MultiSeat.Agent;
using MultiSeat.Core;

// Runs in every user session (HKLM Run key). If this user is a MultiSeat seat, receive that seat's
// keyboard/mouse from the MultiSeat manager and type/click them into this session.

var settingsPath = AgentSettings.PathFor(Environment.UserName);
if (!File.Exists(settingsPath))
{
    return 0; // not a seat user (for example the main desktop user)
}

using var single = new Mutex(true, @"Local\MultiSeatAgent", out var first);
if (!first)
{
    return 0;
}

var logPath = Path.Combine(Path.GetTempPath(), "MultiSeatAgent.log");
using var log = new StreamWriter(logPath, append: true) { AutoFlush = true };
void Log(string m) => log.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}");

AgentSettings settings;
try
{
    settings = JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(settingsPath), MultiSeatConfig.JsonOptions)
        ?? throw new InvalidDataException("empty");
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
{
    Log($"Cannot read {settingsPath}: {ex.Message}");
    return 1;
}

if (!Pointer.UseSoftwarePointer())
{
    Log("Could not switch on the software mouse pointer; it applies after the next sign-in.");
}

var codec = PacketCodec.FromBase64(settings.Key);
var guard = new ReplayGuard();
var injector = new Injector();

using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
socket.Bind(new IPEndPoint(IPAddress.Loopback, settings.Port));
socket.ReceiveTimeout = 250;
Log($"{settings.Seat}: listening on 127.0.0.1:{settings.Port}");

var buffer = new byte[256];
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
        if (injector.HasPressed && now - lastPacket > settings.WatchdogMs)
        {
            injector.ReleaseAll(); // manager stopped or crashed: never leave keys held down
        }

        continue;
    }

    if (!codec.TryDecode(buffer.AsSpan(0, length), out var header, out var e)
        || !guard.Accept(header, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
    {
        if (++rejected % 100 == 1)
        {
            Log($"Rejected {rejected} packet(s) (wrong key or replay).");
        }

        continue;
    }

    lastPacket = now;
    if (guard.SessionChanged)
    {
        injector.ReleaseAll();
    }

    injector.Apply(e);
}
