using Microsoft.Win32;

namespace MultiSeat.Host;

/// <summary>Frame rate of the players' screens (Windows setting for all sessions; applies to newly opened screens).</summary>
internal static class FrameRate
{
    private const string Key = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations";
    private const string Value = "DWMFRAMEINTERVAL";

    public static int Get()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Key);
        return key?.GetValue(Value) is int interval && interval > 0 && interval <= 20 ? 60 : 30;
    }

    public static void Set(int fps)
    {
        using var key = Registry.LocalMachine.CreateSubKey(Key);
        key.SetValue(Value, fps >= 60 ? 15 : 33, RegistryValueKind.DWord);
    }
}
