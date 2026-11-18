using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MultiSeat.Host;

/// <summary>Creates the local Windows accounts that players sign in with.</summary>
internal static class LocalUsers
{
    private const int UserPrivUser = 1;
    private const int UfScript = 0x0001;
    private const int UfDontExpirePasswd = 0x10000;
    private const int NerrSuccess = 0;
    private const int ErrorMemberInAlias = 1378;

    /// <summary>Creates the user (if missing) and lets it sign in to its player session.</summary>
    public static void Create(string userName, string password)
    {
        if (!SecureFile.UserExists(userName))
        {
            var info = new UserInfo1
            {
                Name = userName,
                Password = password,
                Priv = UserPrivUser,
                Flags = UfScript | UfDontExpirePasswd,
                Comment = "MultiSeat player",
            };
            var rc = NetUserAdd(null, 1, ref info, out _);
            if (rc != NerrSuccess)
            {
                throw new Win32Exception(rc, $"Could not create user '{userName}' ({new Win32Exception(rc).Message}).");
            }
        }

        AddToRemoteDesktopUsers(userName);
    }

    /// <summary>Players need "Remote Desktop Users" membership to open their session. Group name depends on Windows language.</summary>
    private static void AddToRemoteDesktopUsers(string userName)
    {
        var group = new SecurityIdentifier("S-1-5-32-555").Translate(typeof(NTAccount)).Value;
        var groupName = group.Contains('\\') ? group[(group.IndexOf('\\') + 1)..] : group;
        var member = new LocalGroupMembersInfo3 { DomainAndName = $"{Environment.MachineName}\\{userName}" };
        var rc = NetLocalGroupAddMembers(null, groupName, 3, ref member, 1);
        if (rc != NerrSuccess && rc != ErrorMemberInAlias)
        {
            throw new Win32Exception(rc, $"Could not add '{userName}' to {groupName}.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1
    {
        public string Name;
        public string Password;
        public int PasswordAge;
        public int Priv;
        public string? HomeDir;
        public string? Comment;
        public int Flags;
        public string? ScriptPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupMembersInfo3
    {
        public string DomainAndName;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserAdd(string? server, int level, ref UserInfo1 buffer, out int parmError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAddMembers(string? server, string groupName, int level, ref LocalGroupMembersInfo3 buffer, int totalEntries);
}
