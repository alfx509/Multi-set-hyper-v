using System.Diagnostics;

namespace MultiSeat.Host;

/// <summary>
/// "Start MultiSeat when I sign in to Windows". MultiSeat needs Administrator rights, so it uses a
/// scheduled task with highest privileges (a normal Startup shortcut would show a UAC prompt every time).
/// </summary>
internal static class StartupTask
{
    public const string Name = "MultiSeat";

    /// <summary>Argument passed by the sign-in task, so MultiSeat knows it was started automatically.</summary>
    public const string AutoStartArgument = "--autostart";

    public static bool Exists() => Schtasks($"/Query /TN {Name}") == 0;

    public static bool Enable()
    {
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        return Schtasks($"/Create /F /TN {Name} /SC ONLOGON /RL HIGHEST /IT /RU \"{user}\" /TR \"\\\"{exe}\\\" {AutoStartArgument}\"") == 0;
    }

    public static bool Disable() => !Exists() || Schtasks($"/Delete /F /TN {Name}") == 0;

    private static int Schtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null)
            {
                return -1;
            }

            p.WaitForExit(10000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }
}
