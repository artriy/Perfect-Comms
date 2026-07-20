#if ANDROID
using System;
using System.Runtime.InteropServices;

namespace VoiceChatPlugin.VoiceChat;

// P/Invoke surface for libpc_mobile.so (the shared Rust voice core, native/pc-mobile).
// The engine speaks the same JSON control/signal protocol as the desktop sidecar, so the
// callers reuse SidecarProtocol for everything except the audio PCM (push_mic/pull_playback).
internal static class PcMobileNative
{
    private const string Lib = "pc_mobile";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pc_abi_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pc_engine_new_with_transport")]
    private static extern IntPtr pc_engine_new_with_transport(byte[] transportPathNullTerminated);

    // Keep the existing managed call surface, but always use ABI 4's atomic path+construction
    // entry point. EnsureLoaded prepares a rooted UTF-8 path before MobileVoiceClient gets here.
    public static IntPtr pc_engine_new()
    {
        var transportPath = PcMobileLoader.PionTransportPath;
        return transportPath == null
            ? IntPtr.Zero
            : pc_engine_new_with_transport(transportPath);
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pc_engine_free(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pc_control(IntPtr handle, byte[] jsonNullTerminated);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern float pc_push_mic(IntPtr handle, float[] samples, int len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern float pc_push_mic_with_gap(
        IntPtr handle,
        float[] samples,
        int len,
        ulong skippedBeforeCurrent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pc_pull_playback(IntPtr handle, float[] outBuf, int cap);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern float pc_mic_level(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pc_poll_signal(IntPtr handle, byte[] outBuf, int cap);
}
#endif
