using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MultiSeat.Agent;

/// <summary>
/// Earlier versions set "MouseTrails = -1" so Windows would draw the pointer into the seat's picture. That does not
/// work in Remote Desktop seats on current Windows (the viewer draws the pointer at its own mouse position), so the
/// agent now draws the pointer itself (<see cref="PointerOverlay"/>). This undoes the old setting, so players never get
/// two pointers or pointer trails.
/// </summary>
internal static class Pointer
{
    private const uint SpiSetMouseTrails = 0x005D;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    public static void UndoSoftwarePointerSetting()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse");
            if (key.GetValue("MouseTrails") as string == "-1")
            {
                key.SetValue("MouseTrails", "0", RegistryValueKind.String);
                SystemParametersInfo(SpiSetMouseTrails, 0, IntPtr.Zero, SpifUpdateIniFile | SpifSendChange);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
}
