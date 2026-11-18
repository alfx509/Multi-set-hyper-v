using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiSeat.Core;

/// <summary>
/// All seats. Saved to C:\ProgramData\MultiSeat\config.json (Administrators only).
/// Seat 0 is always the console seat: the normal Windows desktop of whoever runs MultiSeat.
/// </summary>
public sealed class MultiSeatConfig
{
    public const int FirstAgentPort = 47810;

    /// <summary>Most players supported: main desktop + 7 seats (Interception handles 10 keyboards and 10 mice).</summary>
    public const int MaxSeats = 8;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public List<Seat> Seats { get; set; } = new();

    /// <summary>Keep the console seat's mouse on the console seat's monitor while seats run.</summary>
    public bool ConfineConsoleCursor { get; set; } = true;

    /// <summary>Stream frame rate for seat sessions (Windows caps remote sessions at 60).</summary>
    public int FrameRate { get; set; } = 60;

    /// <summary>Press Start automatically when MultiSeat opens.</summary>
    public bool AutoStartSeats { get; set; }

    /// <summary>If a seat's screen closes while playing, open it again.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>When Windows starts MultiSeat at sign-in, stay hidden in the tray.</summary>
    public bool StartMinimized { get; set; } = true;

    /// <summary>Minimizing hides MultiSeat to the tray (notification area) instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>The window's X button keeps MultiSeat running in the tray. Exit from the tray menu.</summary>
    public bool CloseToTray { get; set; } = true;

    public static MultiSeatConfig CreateDefault() => new()
    {
        Seats = { new Seat { Name = "Seat 1 (this desktop)", IsConsole = true } },
    };

    public static MultiSeatConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return CreateDefault();
        }

        var config = JsonSerializer.Deserialize<MultiSeatConfig>(File.ReadAllText(path), JsonOptions) ?? CreateDefault();
        config.Normalize();
        return config;
    }

    public string ToJson()
    {
        Normalize();
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    /// <summary>Exactly one console seat, first; every other seat has a unique port, loopback address and key.</summary>
    public void Normalize()
    {
        var console = Seats.FirstOrDefault(s => s.IsConsole) ?? new Seat { Name = "Seat 1 (this desktop)", IsConsole = true };
        var others = Seats.Where(s => !ReferenceEquals(s, console)).ToList();
        foreach (var s in others)
        {
            s.IsConsole = false;
        }

        Seats = new List<Seat> { console };
        Seats.AddRange(others);

        // Each seat keeps its number for life (its address and saved password depend on it).
        var used = new HashSet<int>();
        foreach (var seat in others)
        {
            var n = seat.AgentPort - FirstAgentPort;
            if (n is < 1 or > 250 || !used.Add(n))
            {
                seat.AgentPort = 0;
            }
        }

        foreach (var seat in others)
        {
            if (seat.AgentPort == 0)
            {
                var n = Enumerable.Range(1, 250).First(i => !used.Contains(i));
                used.Add(n);
                seat.AgentPort = FirstAgentPort + n;
            }

            seat.LoopbackAddress = $"127.0.0.{seat.AgentPort - FirstAgentPort + 1}";
            if (string.IsNullOrWhiteSpace(seat.AgentKey))
            {
                seat.AgentKey = PacketCodec.NewKey();
            }
        }

        console.AgentKey = null;
        console.AgentPort = 0;
        console.LoopbackAddress = null;
        console.UserName = null;
        FrameRate = Math.Clamp(FrameRate, 30, 60);
    }

    public IEnumerable<string> Validate()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var monitors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var devices = new HashSet<int>();

        if (Seats.Count > MaxSeats)
        {
            yield return $"MultiSeat supports up to {MaxSeats} players; you have {Seats.Count} seats.";
        }

        foreach (var seat in Seats)
        {
            if (string.IsNullOrWhiteSpace(seat.Name) || !names.Add(seat.Name))
            {
                yield return $"Seat name '{seat.Name}' is empty or used twice.";
            }

            if (!seat.IsConsole)
            {
                if (string.IsNullOrWhiteSpace(seat.UserName))
                {
                    yield return $"{seat.Name}: choose a Windows user.";
                }
                else if (!users.Add(seat.UserName))
                {
                    yield return $"{seat.Name}: user '{seat.UserName}' is already used by another seat.";
                }

                if (string.IsNullOrWhiteSpace(seat.Monitor))
                {
                    yield return $"{seat.Name}: choose a monitor.";
                }

                if (seat.Keyboard is null && seat.Mouse is null)
                {
                    yield return $"{seat.Name}: assign a keyboard and a mouse.";
                }
            }

            if (!string.IsNullOrWhiteSpace(seat.Monitor) && !monitors.Add(seat.Monitor))
            {
                yield return $"{seat.Name}: monitor {seat.Monitor} is used by another seat.";
            }

            foreach (var d in new[] { seat.Keyboard, seat.Mouse })
            {
                if (d is not null && !devices.Add(d.Slot))
                {
                    yield return $"{seat.Name}: {d.Description} is assigned to two seats.";
                }
            }
        }
    }
}

public sealed class Seat
{
    public string Name { get; set; } = "";

    /// <summary>The normal desktop (session of the person running MultiSeat). Gets every unassigned device.</summary>
    public bool IsConsole { get; set; }

    /// <summary>Local Windows account that this seat signs in as. Its password is kept in Windows Credential Manager.</summary>
    public string? UserName { get; set; }

    /// <summary>Monitor device name, e.g. \\.\DISPLAY2.</summary>
    public string? Monitor { get; set; }

    public InputDevice? Keyboard { get; set; }

    public InputDevice? Mouse { get; set; }

    /// <summary>Audio endpoint id for this seat's speakers/headset (null = Windows default).</summary>
    public string? AudioOutputId { get; set; }

    public string? AudioOutputName { get; set; }

    /// <summary>Audio endpoint id for this seat's microphone (null = Windows default).</summary>
    public string? AudioInputId { get; set; }

    public string? AudioInputName { get; set; }

    /// <summary>
    /// Experimental: USB device instance paths given to the seat directly (gamepads, other USB devices),
    /// using Remote Desktop USB redirection.
    /// </summary>
    public List<string> UsbDevices { get; set; } = new();

    public int AgentPort { get; set; }

    public string? LoopbackAddress { get; set; }

    public string? AgentKey { get; set; }
}

/// <summary>A keyboard or mouse as seen by the Interception driver: slot 1-10 keyboards, 11-20 mice.</summary>
public sealed record InputDevice(int Slot, string HardwareId)
{
    public bool IsKeyboard => Slot is >= 1 and <= 10;

    public string Description => IsKeyboard ? $"Keyboard {Slot}" : $"Mouse {Slot - 10}";
}

/// <summary>What the agent inside a seat's session needs. One file per Windows user, readable only by that user.</summary>
public sealed class AgentSettings
{
    public const string Directory = @"C:\ProgramData\MultiSeat\agents";

    public string Seat { get; set; } = "";

    public int Port { get; set; }

    public string Key { get; set; } = "";

    public int WatchdogMs { get; set; } = 2000;

    public static string PathFor(string userName) => System.IO.Path.Combine(Directory, $"{userName.ToLowerInvariant()}.json");
}
