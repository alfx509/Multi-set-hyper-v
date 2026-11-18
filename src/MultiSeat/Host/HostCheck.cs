using Microsoft.Win32;
using MultiSeat.Core;

namespace MultiSeat.Host;

internal enum CheckLevel
{
    Ok,
    Warning,
    Error,
}

internal sealed record CheckResult(CheckLevel Level, string Message);

/// <summary>Checks that this PC is set up for multiple seats. Fixes: run MultiSeat-Setup.exe again.</summary>
internal static class HostCheck
{
    private const string TsPolicy = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";

    public static List<CheckResult> Run(MultiSeatConfig config)
    {
        var results = new List<CheckResult>();
        var sessionSeats = config.Seats.Count(s => !s.IsConsole);

        var productType = ReadString(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\ProductOptions", "ProductType");
        var isServer = productType is "ServerNT" or "LanmanNT";
        if (isServer)
        {
            // TSAppCompat = 1 when the Remote Desktop Session Host role is installed.
            var sessionHost = ReadInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "TSAppCompat") == 1;
            if (sessionHost)
            {
                results.Add(new(CheckLevel.Ok,
                    $"Windows Server with Remote Desktop Session Host: up to {MultiSeatConfig.MaxSeats} players. " +
                    "(Without RDS licenses Windows allows 120 days; then add licenses.)"));
            }
            else if (sessionSeats > 2)
            {
                results.Add(new(CheckLevel.Error,
                    $"You have {sessionSeats + 1} players, but Windows Server allows only 3 (main desktop + 2) without the " +
                    "Remote Desktop Session Host role. Run MultiSeat-Setup.exe again and tick \"Allow 4 to 8 players\"."));
            }
            else
            {
                results.Add(new(CheckLevel.Ok,
                    "Windows Server: up to 3 players. For 4-8 players run MultiSeat-Setup.exe again and tick \"Allow 4 to 8 players\"."));
            }
        }
        else if (sessionSeats > 0)
        {
            results.Add(new(CheckLevel.Warning,
                "This is Windows 10/11. As shipped, it allows only ONE signed-in person at a time, so starting a seat " +
                "will normally sign out the main desktop. Windows Server supports several players."));
        }

        var monitors = Screen.AllScreens.Length;
        var wanted = config.Seats.Count(s => !string.IsNullOrEmpty(s.Monitor));
        if (config.Seats.Count > monitors)
        {
            results.Add(new(CheckLevel.Warning,
                $"{config.Seats.Count} players but only {monitors} monitor(s) connected. Every player needs a monitor."));
        }
        else
        {
            results.Add(new(CheckLevel.Ok, $"{monitors} monitor(s) connected, {wanted} assigned."));
        }

        if (ReadInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections") != 0)
        {
            results.Add(new(CheckLevel.Error, "Remote Desktop is turned off. Run MultiSeat-Setup.exe again."));
        }

        if (ReadInt(Registry.LocalMachine, TsPolicy, "bEnumerateHWBeforeSW") != 1)
        {
            results.Add(new(CheckLevel.Warning, "Seats will not use the graphics card (GPU policy not set). Run MultiSeat-Setup.exe again."));
        }
        else
        {
            results.Add(new(CheckLevel.Ok, "Seats use the graphics card for games."));
        }

        if (!File.Exists(Path.Combine(Environment.SystemDirectory, "drivers", "keyboard.sys")))
        {
            results.Add(new(CheckLevel.Error, "Interception input driver not installed. Run MultiSeat-Setup.exe again and restart."));
        }

        if (!File.Exists(AppPaths.AgentExe))
        {
            results.Add(new(CheckLevel.Error, $"Seat agent not installed ({AppPaths.AgentExe}). Run MultiSeat-Setup.exe again."));
        }

        foreach (var seat in config.Seats.Where(s => !s.IsConsole && !string.IsNullOrWhiteSpace(s.UserName)))
        {
            if (!SecureFile.UserExists(seat.UserName!))
            {
                results.Add(new(CheckLevel.Error, $"{seat.Name}: Windows user '{seat.UserName}' does not exist."));
            }
            else if (!Sessions.Credentials.Exists(seat.LoopbackAddress!))
            {
                results.Add(new(CheckLevel.Error, $"{seat.Name}: no password saved. Edit the seat and type the password."));
            }
        }

        return results;
    }

    private static string? ReadString(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    private static int? ReadInt(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) is int i ? i : null;
    }
}
