using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiSeat.Sessions;

/// <summary>Stores a seat user's password in Windows Credential Manager so the seat signs in without typing it.</summary>
internal static class Credentials
{
    private const int CredTypeDomainPassword = 2;
    private const int CredPersistLocalMachine = 2;

    public static string Target(string loopbackAddress) => $"TERMSRV/{loopbackAddress}";

    public static void Save(string loopbackAddress, string userName, string password)
    {
        var blob = Encoding.Unicode.GetBytes(password);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new Credential
            {
                Type = CredTypeDomainPassword,
                TargetName = Target(loopbackAddress),
                UserName = $"{Environment.MachineName}\\{userName}",
                CredentialBlob = blobPtr,
                CredentialBlobSize = blob.Length,
                Persist = CredPersistLocalMachine,
            };
            if (!CredWrite(ref cred, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, blobPtr, blob.Length);
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public static bool Exists(string loopbackAddress)
    {
        if (CredRead(Target(loopbackAddress), CredTypeDomainPassword, 0, out var ptr))
        {
            CredFree(ptr);
            return true;
        }

        return false;
    }

    public static void Delete(string loopbackAddress) => CredDelete(Target(loopbackAddress), CredTypeDomainPassword, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
