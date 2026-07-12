#if ANDROID
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

// Loads libpc_mobile.so so the PcMobileNative DllImports resolve. The .so is embedded as a
// resource and extracted to the app-private cache, then loaded by full path; if it is not
// embedded we fall back to the system loader (e.g. a .so placed in the APK's lib/<abi>/).
// ponytail: arm64-v8a only (modern Among Us is 64-bit); add other ABIs here if 32-bit returns.
internal static class PcMobileLoader
{
    // ABI 3 adds set-input, runtime synthetic control, and batched peer-level telemetry.
    private const int ExpectedAbi = SidecarProtocol.MobileAbi;
    private const uint ReadExecuteMode = 0x16D; // Unix 0555: executable but never writable.
    private const string ResourceName = "Lib.pc-mobile.libpc_mobile.so";
    private const string FilePrefix = "libpc_mobile.";

    private static bool _loaded;
    private static IntPtr _nativeHandle;
    private static readonly object _gate = new();

    static PcMobileLoader()
    {
        try { NativeLibrary.SetDllImportResolver(typeof(PcMobileLoader).Assembly, ResolveImport); }
        catch (Exception)
        {
            // Another resolver may already own the assembly. A successful dlopen still lets the
            // runtime resolve the library by its ELF soname, and runtimes without resolver support
            // retain the system-loader fallback.
        }
    }

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
                    try { _nativeHandle = NativeLibrary.Load(path); }
                    catch (Exception ex) { VoiceDiagnostics.DebugWarning($"[VC] pc-mobile load by path failed ({ex.Message}); trying system loader"); }
                }
                int abi = PcMobileNative.pc_abi_version();
                _loaded = abi == ExpectedAbi;
                if (_loaded) VoiceDiagnostics.DebugInfo($"[VC] pc-mobile loaded (ABI {ExpectedAbi})");
                else VoiceDiagnostics.DebugWarning($"[VC] pc-mobile ABI mismatch: got {abi}, expected {ExpectedAbi}");
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

        byte[] expectedHash;
        using (var sha = SHA256.Create()) expectedHash = sha.ComputeHash(src);
        var hashText = BitConverter.ToString(expectedHash, 0, 12).Replace("-", string.Empty).ToLowerInvariant();
        var directory = Path.Combine(Application.temporaryCachePath, "PerfectComms", "native", "arm64-v8a");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FilePrefix + hashText + ".so");

        if (File.Exists(path))
        {
            try
            {
                using var existing = File.OpenRead(path);
                using var sha = SHA256.Create();
                if (CryptographicOperations.FixedTimeEquals(expectedHash, sha.ComputeHash(existing)))
                {
                    MakeReadOnly(path);
                    return path;
                }
            }
            catch { }
        }

        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var copySource = asm.GetManifestResourceStream(ResourceName)
                ?? throw new FileNotFoundException("embedded pc-mobile resource disappeared", ResourceName);
            using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                copySource.CopyTo(dst);
                dst.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            MakeReadOnly(path);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
        return path;
    }

    private static IntPtr ResolveImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        _ = assembly;
        _ = searchPath;
        return string.Equals(libraryName, "pc_mobile", StringComparison.Ordinal) ? _nativeHandle : IntPtr.Zero;
    }

    private static void MakeReadOnly(string path)
    {
        // Android 17 requires native files passed to System.load/dlopen to be read-only, and the
        // same rule closes the modification window on earlier releases. Fail closed if chmod does
        // not take effect instead of loading writable executable code.
        if (chmod(path, ReadExecuteMode) != 0)
            throw new IOException($"could not mark pc-mobile read-only (errno {Marshal.GetLastWin32Error()})");
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int chmod(string path, uint mode);
}
#endif
