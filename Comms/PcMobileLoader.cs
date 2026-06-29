#if ANDROID
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

// Loads libpc_mobile.so so the PcMobileNative DllImports resolve. The .so is embedded as a
// resource and extracted to the app's writable dir, then loaded by full path; if it is not
// embedded we fall back to the system loader (e.g. a .so placed in the APK's lib/<abi>/).
// ponytail: arm64-v8a only (modern Among Us is 64-bit); add other ABIs here if 32-bit returns.
internal static class PcMobileLoader
{
    private const string ResourceName = "Lib.pc-mobile.libpc_mobile.so";
    private const string FileName = "libpc_mobile.so";

    private static bool _loaded;
    private static readonly object _gate = new();

    public static bool EnsureLoaded()
    {
        if (_loaded) return true;
        lock (_gate)
        {
            if (_loaded) return true;
            try
            {
                var path = ExtractIfNeeded();
                if (path != null)
                {
                    try { NativeLibrary.Load(path); }
                    catch (Exception ex) { VoiceDiagnostics.DebugWarning($"[VC] pc-mobile load by path failed ({ex.Message}); trying system loader"); }
                }
                int abi = PcMobileNative.pc_abi_version();
                _loaded = abi == 1;
                if (_loaded) VoiceDiagnostics.DebugInfo("[VC] pc-mobile loaded (ABI 1)");
                else VoiceDiagnostics.DebugWarning($"[VC] pc-mobile ABI mismatch: got {abi}, expected 1");
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.DebugWarning($"[VC] pc-mobile load failed: {ex}");
                _loaded = false;
            }
            return _loaded;
        }
    }

    private static string? ExtractIfNeeded()
    {
        var asm = typeof(PcMobileLoader).Assembly;
        using var src = asm.GetManifestResourceStream(ResourceName);
        if (src == null)
        {
            VoiceDiagnostics.DebugWarning($"[VC] pc-mobile resource '{ResourceName}' not embedded; relying on APK lib dir");
            return null;
        }
        var path = Path.Combine(Application.persistentDataPath, FileName);
        bool needWrite = true;
        if (File.Exists(path))
        {
            try { needWrite = new FileInfo(path).Length != src.Length; } catch { needWrite = true; }
        }
        if (needWrite)
        {
            var tmp = path + ".tmp";
            using (var dst = File.Create(tmp)) src.CopyTo(dst);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        return path;
    }
}
#endif
