using System.Globalization;
using System.Text;

namespace MultiSeat.Core;

/// <summary>Builds the .rdp file that shows a seat's session full-screen on its monitor.</summary>
public static class RdpFile
{
    public readonly record struct Bounds(int X, int Y, int Width, int Height);

    public static string Build(Seat seat, Bounds monitor)
    {
        if (seat.IsConsole || string.IsNullOrEmpty(seat.LoopbackAddress) || string.IsNullOrEmpty(seat.UserName))
        {
            throw new ArgumentException("Only non-console seats with a user and address have an RDP file.", nameof(seat));
        }

        var lines = new List<string>
        {
            $"full address:s:{seat.LoopbackAddress}",
            $"username:s:.\\{seat.UserName}",

            // Full screen on the seat's monitor: the window starts on that monitor and goes full screen there.
            "screen mode id:i:2",
            "use multimon:i:0",
            "span monitors:i:0",
            $"desktopwidth:i:{monitor.Width}",
            $"desktopheight:i:{monitor.Height}",
            $"winposstr:s:0,1,{monitor.X},{monitor.Y},{monitor.X + monitor.Width},{monitor.Y + monitor.Height}",
            "smart sizing:i:0",
            "dynamic resolution:i:0",
            "displayconnectionbar:i:0",
            "pinconnectionbar:i:0",
            "session bpp:i:32",

            // Local "network": best quality, no throttling.
            "connection type:i:6",
            "networkautodetect:i:0",
            "bandwidthautodetect:i:1",
            "compression:i:0",
            "disable wallpaper:i:0",
            "disable full window drag:i:0",
            "disable menu anims:i:0",
            "disable themes:i:0",
            "allow font smoothing:i:1",
            "allow desktop composition:i:1",
            "bitmapcachepersistenable:i:1",

            // Sound plays through this client, which MultiSeat points at the seat's headset; microphone too.
            "audiomode:i:0",
            "audiocapturemode:i:1",
            "audioqualitymode:i:2",

            // Keyboard/mouse arrive through the MultiSeat agent, not through this window.
            "keyboardhook:i:0",

            // Loopback connection to this same PC: no prompts.
            "authentication level:i:0",
            "prompt for credentials:i:0",
            "promptcredentialonce:i:1",
            "enablecredsspsupport:i:1",
            "autoreconnection enabled:i:1",
            "redirectclipboard:i:1",
            "redirectprinters:i:0",
            "redirectsmartcards:i:0",
            "redirectcomports:i:0",
            "drivestoredirect:s:",
        };

        if (seat.UsbDevices.Count > 0)
        {
            lines.Add("usbdevicestoredirect:s:" + string.Join(";", seat.UsbDevices));
        }

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append(line).Append("\r\n");
        }

        return sb.ToString();
    }

    public static string Name(Seat seat) =>
        string.Create(CultureInfo.InvariantCulture, $"seat-{seat.AgentPort}.rdp");
}
