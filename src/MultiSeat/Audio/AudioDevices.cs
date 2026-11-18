using System.Runtime.InteropServices;

namespace MultiSeat.Audio;

internal sealed record AudioDevice(string Id, string Name);

/// <summary>Lists active speakers/headsets (render) or microphones (capture) with Windows Core Audio.</summary>
internal static class AudioDevices
{
    private const uint DeviceStateActive = 1;
    private static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public static IReadOnlyList<AudioDevice> List(bool capture)
    {
        var result = new List<AudioDevice>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            Check(enumerator.EnumAudioEndpoints(capture ? 1 : 0, DeviceStateActive, out var collection));
            Check(collection.GetCount(out var count));
            for (uint i = 0; i < count; i++)
            {
                Check(collection.Item(i, out var device));
                Check(device.GetId(out var id));
                var name = id;
                if (device.OpenPropertyStore(0, out var store) == 0)
                {
                    var key = FriendlyName;
                    if (store.GetValue(ref key, out var value) == 0)
                    {
                        if (value.Type == 31 /* VT_LPWSTR */ && value.Pointer != IntPtr.Zero)
                        {
                            name = Marshal.PtrToStringUni(value.Pointer) ?? id;
                        }

                        PropVariantClear(ref value);
                    }

                    Marshal.ReleaseComObject(store);
                }

                result.Add(new AudioDevice(id, name));
                Marshal.ReleaseComObject(device);
            }

            Marshal.ReleaseComObject(collection);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig]
        int OpenPropertyStore(uint access, out IPropertyStore store);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }
}
