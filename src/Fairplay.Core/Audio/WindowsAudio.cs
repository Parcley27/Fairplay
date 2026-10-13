using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Fairplay.Audio;

/// <summary>Helpers for Windows audio endpoints that NAudio doesn't cover.</summary>
public static class WindowsAudio
{
    /// <summary>
    /// Make <paramref name="deviceId"/> the default playback device for all roles, the same way the
    /// Sound control panel does (undocumented but stable IPolicyConfig interface; no admin needed).
    /// </summary>
    public static void SetDefaultOutput(string deviceId)
    {
        var config = (IPolicyConfig)new CPolicyConfigClient();
        try
        {
            for (int role = 0; role < 3; role++) // console, multimedia, communications
                Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, role));
        }
        finally
        {
            Marshal.ReleaseComObject(config);
        }
    }

    public static string? DefaultOutputId()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return d.ID;
        }
        catch { return null; }
    }

    /// <summary>Set the Windows volume slider (0..1) of a playback device (null = default).</summary>
    public static void SetEndpointVolume(string? deviceId, float scalar, bool? mute = null)
    {
        using var en = new MMDeviceEnumerator();
        using var d = deviceId == null ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : en.GetDevice(deviceId);
        d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(scalar, 0f, 1f);
        if (mute.HasValue) d.AudioEndpointVolume.Mute = mute.Value;
    }

    static readonly Guid DeviceDescFmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0"); // PKEY_Device_DeviceDesc, pid 2

    /// <summary>The endpoint's own name (the "Speakers" part of "Speakers (Realtek Audio)").</summary>
    public static string? GetEndpointName(string deviceId)
    {
        using var en = new MMDeviceEnumerator();
        using var d = en.GetDevice(deviceId);
        var key = new NAudio.CoreAudioApi.PropertyKey(DeviceDescFmtid, 2);
        return d.Properties.Contains(key) ? d.Properties[key].Value as string : null;
    }

    /// <summary>
    /// Rename a playback endpoint as shown in Windows' output pickers — what Sound settings' "Rename" does.
    /// Handy for giving a virtual cable the speaker's name, e.g. "Bedroom (AirPlay)".
    /// </summary>
    public static void RenameEndpoint(string deviceId, string name)
    {
        var config = (IPolicyConfig)new CPolicyConfigClient();
        var key = new PropKey { Fmtid = DeviceDescFmtid, Pid = 2 };
        var value = new PropVariantString { Vt = 31 /* VT_LPWSTR */, Ptr = Marshal.StringToCoTaskMemUni(name) };
        try
        {
            Marshal.ThrowExceptionForHR(config.SetPropertyValue(deviceId, 0, ref key, ref value));
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.Ptr);
            Marshal.ReleaseComObject(config);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropKey
    {
        public Guid Fmtid;
        public int Pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariantString
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public IntPtr Ptr;
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class CPolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, int useDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, int useDefault, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, int fxStore, ref PropKey key, ref PropVariantString value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string device, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string device, int visible);
    }
}
