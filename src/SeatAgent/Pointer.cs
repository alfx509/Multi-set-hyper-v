using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MultiSeat.Agent;

/// <summary>
/// In a remote-style session Windows normally lets the viewer window draw the mouse pointer, which only
/// follows the viewer's own mouse. MultiSeat moves the pointer from inside the session, so we ask Windows
/// to draw the pointer into the picture itself ("MouseTrails = -1": software pointer, no visible trails).
/// </summary>
internal static class Pointer
{
    private const uint SpiSetMouseTrails = 0x005D;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    public static bool UseSoftwarePointer()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse");
            key.SetValue("MouseTrails", "-1", RegistryValueKind.String);
        }
        catch (UnauthorizedAccessException)
        {
        }

        return SystemParametersInfo(SpiSetMouseTrails, unchecked((uint)-1), IntPtr.Zero, SpifUpdateIniFile | SpifSendChange);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
}
