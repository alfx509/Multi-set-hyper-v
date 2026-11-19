using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

// SeatProbe: run it inside any session (main desktop or a player session) to see what that session really gets.
// It answers the feasibility questions of the ASTER-style driver work (docs/driver-plan.md):
//   * Is this the console session or a remote/other session? What protocol?
//   * Which graphics adapters and monitor outputs does THIS session see?
//   * How fast does the desktop compositor (DWM) actually run here? (RDP sessions: 30/60 fps)
//   * Does screen capture (Desktop Duplication) work here, and how fast?
// Usage: SeatProbe.exe [seconds]   → prints a report and saves SeatProbe-session<N>.txt next to the exe.

var seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? Math.Clamp(s, 2, 60) : 5;
var report = new StringBuilder();
void Line(string text = "")
{
    Console.WriteLine(text);
    report.AppendLine(text);
}

Line($"SeatProbe – {DateTime.Now:yyyy-MM-dd HH:mm:ss} – {Environment.OSVersion.VersionString} ({Environment.MachineName})");
Line(new string('=', 78));

// ------------------------------------------------------------------ session
Native.ProcessIdToSessionId((uint)Environment.ProcessId, out var sessionId);
var consoleSession = Native.WTSGetActiveConsoleSessionId();
var protocol = Native.QueryShort(sessionId, 16 /* WTSClientProtocolType */);
var station = Native.QueryString(sessionId, 6 /* WTSWinStationName */);
Line("SESSION");
Line($"  User                : {Environment.UserDomainName}\\{Environment.UserName}");
Line($"  Session id          : {sessionId}   (console session id: {consoleSession})");
Line($"  Is console session  : {(sessionId == consoleSession ? "YES" : "no")}");
Line($"  Station name        : {station}");
Line($"  Protocol            : {protocol switch { 0 => "0 = console (local)", 1 => "1 = ICA", 2 => "2 = RDP", null => "?", _ => protocol.ToString() }}");
Line();

// ------------------------------------------------------------------ registry knobs
Line("SETTINGS");
Line($"  DWMFRAMEINTERVAL    : {Reg(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations", "DWMFRAMEINTERVAL")} (15 = 60 fps cap for remote sessions, empty = 30 fps)");
Line($"  GPU for sessions    : {Reg(@"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services", "bEnumerateHWBeforeSW")} (1 = use the real GPU)");
Line($"  Test signing        : {(BcdTestSigning() ? "ON" : "off / unknown")}");
Line();

// ------------------------------------------------------------------ GDI view of displays
Line("DISPLAYS (as this session sees them)");
for (uint i = 0; ; i++)
{
    var dev = new Native.DisplayDevice { cb = Marshal.SizeOf<Native.DisplayDevice>() };
    if (!Native.EnumDisplayDevices(null, i, ref dev, 0))
    {
        break;
    }

    var mode = new Native.DevMode { dmSize = (short)Marshal.SizeOf<Native.DevMode>() };
    var hasMode = Native.EnumDisplaySettings(dev.DeviceName, -1, ref mode);
    var attached = (dev.StateFlags & 0x1) != 0;
    Line($"  {dev.DeviceName,-14} {dev.DeviceString,-40} {(attached ? "attached" : "not attached")}" +
         (hasMode && attached ? $"  {mode.dmPelsWidth}x{mode.dmPelsHeight} @ {mode.dmDisplayFrequency} Hz" : ""));
}

Line();

// ------------------------------------------------------------------ DXGI view
Line("GRAPHICS ADAPTERS AND OUTPUTS (DXGI)");
using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
var outputs = new List<(IDXGIAdapter1 Adapter, IDXGIOutput Output, string Name)>();
for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
{
    var d = adapter.Description1;
    Line($"  Adapter {a}: {d.Description}  (vendor 0x{d.VendorId:X4}, VRAM {d.DedicatedVideoMemory / (1024 * 1024)} MB{((d.Flags & AdapterFlags.Software) != 0 ? ", SOFTWARE" : "")})");
    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
    {
        var od = output.Description;
        var r = od.DesktopCoordinates;
        Line($"     Output {o}: {od.DeviceName}  {r.Right - r.Left}x{r.Bottom - r.Top} at ({r.Left},{r.Top})  attached={od.AttachedToDesktop}");
        outputs.Add((adapter, output, od.DeviceName));
    }
}

if (outputs.Count == 0)
{
    Line("  (no outputs visible to this session)");
}

Line();

// ------------------------------------------------------------------ DWM composition rate
Line($"DESKTOP COMPOSITOR SPEED (DwmFlush for {seconds} s – keep something moving on screen)");
var timing = new Native.DwmTimingInfo { cbSize = (uint)Marshal.SizeOf<Native.DwmTimingInfo>() };
if (Native.DwmGetCompositionTimingInfo(IntPtr.Zero, ref timing) == 0 && timing.rateRefresh.uiDenominator != 0)
{
    Line($"  Reported refresh    : {(double)timing.rateRefresh.uiNumerator / timing.rateRefresh.uiDenominator:F1} Hz");
}

var flushes = 0;
var sw = Stopwatch.StartNew();
while (sw.Elapsed.TotalSeconds < seconds)
{
    if (Native.DwmFlush() != 0)
    {
        break;
    }

    flushes++;
}

Line($"  Measured            : {flushes / sw.Elapsed.TotalSeconds:F1} compositions per second");
Line();

// ------------------------------------------------------------------ Desktop Duplication
Line($"SCREEN CAPTURE (Desktop Duplication, {seconds} s per output)");
foreach (var (adapter, output, name) in outputs)
{
    try
    {
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 }, out ID3D11Device? device).CheckError();
        using (device)
        using (var output1 = output.QueryInterface<IDXGIOutput1>())
        using (var dup = output1.DuplicateOutput(device))
        {
            var frames = 0;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                var result = dup.AcquireNextFrame(100, out var info, out var resource);
                if (result.Success)
                {
                    if (info.AccumulatedFrames > 0)
                    {
                        frames++;
                    }

                    resource?.Dispose();
                    dup.ReleaseFrame();
                }
            }

            Line($"  {name,-14} OK   {frames / watch.Elapsed.TotalSeconds:F1} new frames per second");
        }
    }
    catch (SharpGen.Runtime.SharpGenException ex)
    {
        Line($"  {name,-14} FAILED  0x{ex.HResult:X8} {ex.Message.Split('\n')[0]}");
    }
}

Line();
Line("HOW TO READ THIS");
Line("  * A player session should ideally say: Is console = no, Protocol = 0 (local), real GPU adapter,");
Line("    its own monitor output attached, and compositions per second = the monitor's refresh rate.");
Line("  * RDP sessions show Protocol = 2, an 'RDP'/'Remote Display' adapter and about 30 or 60 compositions per second.");

var file = Path.Combine(AppContext.BaseDirectory, $"SeatProbe-session{sessionId}.txt");
try
{
    File.WriteAllText(file, report.ToString());
    Console.WriteLine();
    Console.WriteLine($"Saved: {file}");
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.WriteLine($"Could not save the report: {ex.Message}");
}

return 0;

static string Reg(string path, string name)
{
    using var key = Registry.LocalMachine.OpenSubKey(path);
    return key?.GetValue(name)?.ToString() ?? "(not set)";
}

static bool BcdTestSigning()
{
    try
    {
        var psi = new ProcessStartInfo("bcdedit.exe", "/enum {current}") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        var text = p?.StandardOutput.ReadToEnd() ?? "";
        return text.Contains("testsigning", StringComparison.OrdinalIgnoreCase) && text.Contains("Yes", StringComparison.OrdinalIgnoreCase);
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return false;
    }
}

internal static class Native
{
    [DllImport("kernel32.dll")]
    public static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll")]
    public static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW")]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    public static string? QueryString(uint session, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out var buf, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buf);
        }
        finally
        {
            WTSFreeMemory(buf);
        }
    }

    public static short? QueryShort(uint session, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out var buf, out var bytes))
        {
            return null;
        }

        try
        {
            return bytes >= 2 ? Marshal.ReadInt16(buf) : null;
        }
        finally
        {
            WTSFreeMemory(buf);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential)]
    public struct UnsignedRatio
    {
        public uint uiNumerator;
        public uint uiDenominator;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DwmTimingInfo
    {
        public uint cbSize;
        public UnsignedRatio rateRefresh;
        public ulong qpcRefreshPeriod;
        public UnsignedRatio rateCompose;
        public ulong qpcVBlank;
        public ulong cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose;
        public ulong cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame;
        public ulong cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed;
        public ulong qpcFrameDisplayed;
        public ulong cRefreshFrameDisplayed;
        public ulong cFrameComplete;
        public ulong qpcFrameComplete;
        public ulong cFramePending;
        public ulong qpcFramePending;
        public ulong cFramesDisplayed;
        public ulong cFramesComplete;
        public ulong cFramesPending;
        public ulong cFramesAvailable;
        public ulong cFramesDropped;
        public ulong cFramesMissed;
        public ulong cRefreshNextDisplayed;
        public ulong cRefreshNextPresented;
        public ulong cRefreshesDisplayed;
        public ulong cRefreshesPresented;
        public ulong cRefreshStarted;
        public ulong cPixelsReceived;
        public ulong cPixelsDrawn;
        public ulong cBuffersEmpty;
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DwmTimingInfo info);

    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();
}
