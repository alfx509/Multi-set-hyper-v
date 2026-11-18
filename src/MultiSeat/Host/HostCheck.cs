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

/// <summary>Checks that this PC is set up for multiple seats. Fixes are in setup\Install-MultiSeat.ps1.</summary>
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
            var rdsInstalled = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TermServLicensing") is not null
                || ReadInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server\RCM\Licensing Core", "LicensingMode") is 2 or 4;
            if (sessionSeats > 2 && !rdsInstalled)
            {
                results.Add(new(CheckLevel.Warning,
                    $"Windows Server without the Remote Desktop Session Host role allows 2 extra seats; you have {sessionSeats}. " +
                    "Install the RDS Session Host role and licenses for more."));
            }
            else
            {
                results.Add(new(CheckLevel.Ok, "Windows Server: several people can be signed in at the same time."));
            }
        }
        else if (sessionSeats > 0)
        {
            results.Add(new(CheckLevel.Error,
                "This is Windows 10/11 (client). It allows only ONE signed-in person at a time, so extra seats " +
                "would sign out the main desktop. Use Windows Server (the free evaluation works) for extra seats."));
        }

        if (ReadInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections") != 0)
        {
            results.Add(new(CheckLevel.Error, "Remote Desktop is turned off. Run setup\\Install-MultiSeat.ps1."));
        }

        if (ReadInt(Registry.LocalMachine, TsPolicy, "bEnumerateHWBeforeSW") != 1)
        {
            results.Add(new(CheckLevel.Warning, "Seats will not use the graphics card (GPU policy not set). Run setup\\Install-MultiSeat.ps1."));
        }
        else
        {
            results.Add(new(CheckLevel.Ok, "Seats use the graphics card for games."));
        }

        if (!File.Exists(Path.Combine(Environment.SystemDirectory, "drivers", "keyboard.sys")))
        {
            results.Add(new(CheckLevel.Error, "Interception input driver not installed. Run setup\\Install-MultiSeat.ps1 and restart."));
        }

        if (!File.Exists(AppPaths.AgentExe))
        {
            results.Add(new(CheckLevel.Error, $"Seat agent not installed ({AppPaths.AgentExe}). Run setup\\Install-MultiSeat.ps1."));
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
