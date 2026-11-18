using System.Runtime.InteropServices;

namespace MultiSeat.Sessions;

/// <summary>Finds and signs out Windows sessions by user name.</summary>
internal static class WtsSessions
{
    private const int WtsUserName = 5;

    public static IReadOnlyList<(int SessionId, string User, int State)> List()
    {
        var result = new List<(int, string, int)>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
        {
            return result;
        }

        try
        {
            var size = Marshal.SizeOf<SessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<SessionInfo>(buffer + (i * size));
                if (WTSQuerySessionInformation(IntPtr.Zero, info.SessionId, WtsUserName, out var userPtr, out _))
                {
                    var user = Marshal.PtrToStringUni(userPtr) ?? "";
                    WTSFreeMemory(userPtr);
                    if (user.Length > 0)
                    {
                        result.Add((info.SessionId, user, info.State));
                    }
                }
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return result;
    }

    public static int SignOut(string userName)
    {
        var n = 0;
        foreach (var s in List().Where(s => string.Equals(s.User, userName, StringComparison.OrdinalIgnoreCase)))
        {
            if (WTSLogoffSession(IntPtr.Zero, s.SessionId, false))
            {
                n++;
            }
        }

        return n;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr server, int sessionId, bool wait);
}
