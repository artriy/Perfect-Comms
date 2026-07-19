#if ANDROID
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

// Loads libpc_mobile.so and prepares the Pion transport library used by its Rust voice core.
// Both embedded libraries are extracted read-only into the app-private cache. pc-mobile can still
// use the APK system loader, while Pion fails closed unless its signed resource can be extracted
// and passed to Rust by an exact absolute path.
// ponytail: arm64-v8a only (modern Among Us is 64-bit); add other ABIs here if 32-bit returns.
internal static class PcMobileLoader
{
    // ABI 4 adds the constructor that configures Pion before RtcEngine construction.
    private const int ExpectedAbi = SidecarProtocol.MobileAbi;
    private const uint ReadExecuteMode = 0x16D; // Unix 0555: executable but never writable.
    private const string MobileResourceName = "Lib.pc-mobile.libpc_mobile.so";
    private const string MobileFileName = "libpc_mobile.so";
    private const string PionResourceName = "Lib.pc-pion.libpc-pion.android-arm64.so";
    private const string PionFileName = "libpc-pion.so";

    private static volatile bool _loaded;
    private static IntPtr _nativeHandle;
    private static byte[]? _pionTransportPath;
    private static readonly object _gate = new();

    internal static byte[]? PionTransportPath => _loaded ? _pionTransportPath : null;

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
                var pionPath = ExtractIfNeeded(PionResourceName, PionFileName, "Pion transport");
                if (pionPath == null || !Path.IsPathRooted(pionPath))
                    throw new FileNotFoundException("Pion transport library is unavailable", PionFileName);
                _pionTransportPath = Encoding.UTF8.GetBytes(Path.GetFullPath(pionPath) + "\0");

                var mobilePath = ExtractIfNeeded(MobileResourceName, MobileFileName, "pc-mobile");
                if (mobilePath != null)
                {
                    try { _nativeHandle = NativeLibrary.Load(mobilePath); }
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
                _pionTransportPath = null;
            }
            return _loaded;
        }
    }

    private static string? ExtractIfNeeded(string resourceName, string fileName, string label)
    {
        var asm = typeof(PcMobileLoader).Assembly;
        using var src = asm.GetManifestResourceStream(resourceName);
        if (src == null)
        {
            VoiceDiagnostics.DebugWarning($"[VC] {label} resource '{resourceName}' not embedded; checking APK lib dir");
            return null;
        }

        byte[] expectedHash;
        using (var sha = SHA256.Create()) expectedHash = sha.ComputeHash(src);
        var hashText = BitConverter.ToString(expectedHash, 0, 12).Replace("-", string.Empty).ToLowerInvariant();
        var directory = Path.Combine(Application.temporaryCachePath, "PerfectComms", "native", "arm64-v8a", hashText);
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, fileName));

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
            using var copySource = asm.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException($"embedded {label} resource disappeared", resourceName);
            using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // Keep the trusted descriptor open, but remove path-based write access before
                // copying any executable bytes. This follows Android's safe DCL ordering and
                // prevents another process from replacing content through the extraction path.
                MakeReadOnly(tmp);
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
        // Android 17 requires native files passed to System.load/dlopen to be read-only. Applying
        // the same safe-DCL ordering while the trusted writer is already open also closes the
        // modification window on earlier releases. Fail closed instead of loading writable code.
        if (chmod(path, ReadExecuteMode) != 0)
            throw new IOException($"could not mark native library read-only (errno {Marshal.GetLastWin32Error()})");
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int chmod(string path, uint mode);
}
#endif
