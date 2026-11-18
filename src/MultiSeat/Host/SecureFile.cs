using System.Security.AccessControl;
using System.Security.Principal;
using MultiSeat.Core;

namespace MultiSeat.Host;

/// <summary>Writes files that hold seat keys, readable only by Administrators, SYSTEM and (optionally) one user.</summary>
internal static class SecureFile
{
    public static void Write(string path, string content, string? readerUser = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        if (readerUser is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new NTAccount(Environment.MachineName, readerUser), FileSystemRights.Read, AccessControlType.Allow));
        }

        new FileInfo(path).SetAccessControl(security);
    }

    /// <summary>Writes the settings the seat's agent reads when its user signs in.</summary>
    public static void WriteAgentSettings(Seat seat)
    {
        var settings = new AgentSettings { Seat = seat.Name, Port = seat.AgentPort, Key = seat.AgentKey! };
        Write(
            AgentSettings.PathFor(seat.UserName!),
            System.Text.Json.JsonSerializer.Serialize(settings, MultiSeatConfig.JsonOptions),
            seat.UserName);
    }

    public static bool UserExists(string userName)
    {
        try
        {
            new NTAccount(Environment.MachineName, userName).Translate(typeof(SecurityIdentifier));
            return true;
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
    }
}
