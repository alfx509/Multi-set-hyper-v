using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MultiSeat.Audio;
using MultiSeat.Core;
using MultiSeat.Host;

namespace MultiSeat.Sessions;

/// <summary>A running seat: its user's session shown full screen on its monitor.</summary>
internal sealed class SeatSession
{
    public SeatSession(Seat seat, Process client)
    {
        Seat = seat;
        Client = client;
    }

    public Seat Seat { get; }

    public Process Client { get; }

    public bool IsRunning
    {
        get
        {
            try
            {
                return !Client.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Closes the window. The seat's session stays signed in (disconnected), so games keep their state.</summary>
    public void Close()
    {
        try
        {
            if (!Client.HasExited)
            {
                Client.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}

/// <summary>
/// Starts a seat: signs its Windows user in to its own session on this PC and shows that session
/// full screen on the seat's monitor, with the seat's headset and microphone.
/// </summary>
internal static class SessionLauncher
{
    private const string ShellWindowClass = "TscShellContainerClass";

    public static async Task<SeatSession> LaunchAsync(Seat seat, Screen screen, Action<string> log)
    {
        if (seat.IsConsole || seat.UserName is null || seat.LoopbackAddress is null)
        {
            throw new ArgumentException("Not a session seat.", nameof(seat));
        }

        Directory.CreateDirectory(AppPaths.RdpDirectory);
        var rdpPath = Path.Combine(AppPaths.RdpDirectory, RdpFile.Name(seat));
        var b = screen.Bounds;
        await File.WriteAllTextAsync(rdpPath, RdpFile.Build(seat, new RdpFile.Bounds(b.X, b.Y, b.Width, b.Height)), Encoding.Unicode);

        // Each seat gets its own copy of the Remote Desktop client, because Windows remembers
        // the audio device per program path: copy 2 → headset 2, copy 3 → headset 3.
        var client = PrepareClientCopy(seat, log);
        var process = Start(client, rdpPath);
        if (await ExitedWithin(process, TimeSpan.FromSeconds(4)) && client != SystemClient)
        {
            log($"{seat.Name}: the client copy did not start; using the normal Remote Desktop client (shared sound device).");
            process = Start(SystemClient, rdpPath);
        }

        if (seat.AudioOutputId is not null || seat.AudioInputId is not null)
        {
            var error = AppAudioRouting.Route(process.Id, seat.AudioOutputId, seat.AudioInputId);
            log(error is null
                ? $"{seat.Name}: sound → {seat.AudioOutputName ?? "default"}, microphone ← {seat.AudioInputName ?? "default"}"
                : $"{seat.Name}: could not set the headset automatically ({error}). Set it in Settings → Sound → Volume mixer for {client}.");
        }

        _ = Task.Run(() => PlaceWindow(process, screen.Bounds, seat, log));
        return new SeatSession(seat, process);
    }

    private static string SystemClient => Path.Combine(Environment.SystemDirectory, "mstsc.exe");

    private static Process Start(string exe, string rdpPath) =>
        Process.Start(new ProcessStartInfo(exe, $"\"{rdpPath}\"") { UseShellExecute = false })
        ?? throw new InvalidOperationException($"Could not start {exe}.");

    private static async Task<bool> ExitedWithin(Process p, TimeSpan time)
    {
        using var cts = new CancellationTokenSource(time);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string PrepareClientCopy(Seat seat, Action<string> log)
    {
        try
        {
            var dir = Path.Combine(AppPaths.ClientsDirectory, $"seat{seat.AgentPort}");
            var exe = Path.Combine(dir, "mstsc.exe");
            var source = new FileInfo(SystemClient);
            if (!File.Exists(exe) || new FileInfo(exe).LastWriteTimeUtc != source.LastWriteTimeUtc)
            {
                Directory.CreateDirectory(dir);
                source.CopyTo(exe, overwrite: true);

                // Language resources: <lang>\mstsc.exe.mui must sit next to the copy.
                foreach (var langDir in Directory.EnumerateDirectories(Environment.SystemDirectory))
                {
                    var mui = Path.Combine(langDir, "mstsc.exe.mui");
                    if (File.Exists(mui))
                    {
                        var target = Path.Combine(dir, Path.GetFileName(langDir));
                        Directory.CreateDirectory(target);
                        File.Copy(mui, Path.Combine(target, "mstsc.exe.mui"), overwrite: true);
                    }
                }
            }

            return exe;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"{seat.Name}: could not copy the Remote Desktop client ({ex.Message}).");
            return SystemClient;
        }
    }

    /// <summary>Makes sure the seat's window covers exactly its monitor and stays out of Alt+Tab on the main desktop.</summary>
    private static async Task PlaceWindow(Process process, Rectangle bounds, Seat seat, Action<string> log)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            await Task.Delay(500);
            if (process.HasExited)
            {
                return;
            }

            var window = FindWindow(process.Id, ShellWindowClass);
            if (window == IntPtr.Zero)
            {
                continue;
            }

            if (GetWindowRect(window, out var r) && r.Left == bounds.X && r.Top == bounds.Y
                && r.Right - r.Left == bounds.Width && r.Bottom - r.Top == bounds.Height)
            {
                HideFromAltTab(window);
                return;
            }

            SetWindowPos(window, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpNoZOrder | SwpShowWindow | SwpNoActivate);
        }

        log($"{seat.Name}: could not place the seat window on its monitor. Drag it there and press Ctrl+Alt+Break.");
    }

    private static void HideFromAltTab(IntPtr window)
    {
        var style = GetWindowLongPtr(window, GwlExStyle).ToInt64();
        SetWindowLongPtr(window, GwlExStyle, new IntPtr(style | WsExToolWindow));
    }

    private static IntPtr FindWindow(int processId, string className)
    {
        var found = IntPtr.Zero;
        var name = new StringBuilder(256);
        EnumWindows(
            (hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == processId && IsWindowVisible(hwnd))
                {
                    name.Clear();
                    GetClassName(hwnd, name, name.Capacity);
                    if (name.ToString() == className)
                    {
                        found = hwnd;
                        return false;
                    }
                }

                return true;
            },
            IntPtr.Zero);
        return found;
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
