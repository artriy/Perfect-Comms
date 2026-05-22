using System;
using System.Runtime.InteropServices;

namespace VoiceChatPlugin.Audio;

internal static class WinMmOutputDevices
{
    public static int DeviceCount
    {
        get
        {
            try { return checked((int)waveOutGetNumDevs()); }
            catch { return 0; }
        }
    }

    public static string GetProductName(int deviceNumber)
    {
        if (deviceNumber < 0) return "mapper";
        return TryGetCaps(deviceNumber, out var caps) ? caps.ProductName ?? string.Empty : string.Empty;
    }

    private static bool TryGetCaps(int deviceNumber, out WaveOutCaps caps)
        => waveOutGetDevCaps(new IntPtr(deviceNumber), out caps, (uint)Marshal.SizeOf<WaveOutCaps>()) == 0;

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int waveOutGetDevCaps(IntPtr deviceId, out WaveOutCaps caps, uint capsSize);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WaveOutCaps
    {
        private ushort _manufacturerId;
        private ushort _productId;
        private uint _driverVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ProductName;

        private uint _formats;
        private ushort _channels;
        private ushort _reserved;
        private uint _support;
    }
}
