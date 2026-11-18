using System.Runtime.InteropServices;

namespace MultiSeat.Audio;

/// <summary>
/// Sends one program's sound (and microphone) to a chosen device, like
/// Settings → Sound → Volume mixer → "Output device" per app.
/// </summary>
/// <remarks>
/// Uses Windows' internal AudioPolicyConfig factory (the same one the Settings app and EarTrumpet use).
/// It is not a public API, so every call is best effort: on failure MultiSeat tells the user to pick the
/// device by hand in the Volume mixer. Windows remembers the choice per program path, which is why each
/// seat gets its own copy of the Remote Desktop client.
/// </remarks>
internal static class AppAudioRouting
{
    private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";
    private const string RenderInterface = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string CaptureInterface = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";
    private const string DevicePrefix = @"\\?\SWD#MMDEVAPI#";
    private const int SetPersistedDefaultAudioEndpointSlot = 25;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPersistedDefaultAudioEndpointFn(IntPtr self, uint processId, int flow, int role, IntPtr deviceId);

    /// <summary>Returns null on success, otherwise a reason.</summary>
    public static string? Route(int processId, string? outputId, string? inputId)
    {
        try
        {
            var factory = GetFactory();
            try
            {
                var vtable = Marshal.ReadIntPtr(factory);
                var fn = Marshal.GetDelegateForFunctionPointer<SetPersistedDefaultAudioEndpointFn>(
                    Marshal.ReadIntPtr(vtable, SetPersistedDefaultAudioEndpointSlot * IntPtr.Size));

                if (!string.IsNullOrEmpty(outputId))
                {
                    Set(fn, factory, processId, 0, DevicePrefix + outputId + RenderInterface);
                }

                if (!string.IsNullOrEmpty(inputId))
                {
                    Set(fn, factory, processId, 1, DevicePrefix + inputId + CaptureInterface);
                }
            }
            finally
            {
                Marshal.Release(factory);
            }

            return null;
        }
        catch (Exception ex) when (ex is COMException or EntryPointNotFoundException or DllNotFoundException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    private static void Set(SetPersistedDefaultAudioEndpointFn fn, IntPtr factory, int processId, int flow, string deviceId)
    {
        Check(WindowsCreateString(deviceId, deviceId.Length, out var hstring));
        try
        {
            // Roles: 0 console, 1 multimedia, 2 communications (voice chat).
            for (var role = 0; role <= 2; role++)
            {
                Check(fn(factory, (uint)processId, flow, role, hstring));
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static IntPtr GetFactory()
    {
        // The interface id changed in Windows 10 21H2 (build 21390).
        var iid = Environment.OSVersion.Version.Build >= 21390
            ? new Guid("ab3d4648-e242-459f-b02f-541c70306324")
            : new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258");

        Check(WindowsCreateString(ClassName, ClassName.Length, out var name));
        try
        {
            Check(RoGetActivationFactory(name, ref iid, out var factory));
            return factory;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
}
