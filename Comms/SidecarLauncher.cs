using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

internal readonly record struct SidecarDeviceEnumerationResult(
    bool IsAuthoritative,
    IReadOnlyList<VoiceDeviceInfo> Input,
    IReadOnlyList<VoiceDeviceInfo> Output)
{
    internal static SidecarDeviceEnumerationResult Failure =>
        new(false, Array.Empty<VoiceDeviceInfo>(), Array.Empty<VoiceDeviceInfo>());

    internal static SidecarDeviceEnumerationResult Success(
        IReadOnlyList<VoiceDeviceInfo> input,
        IReadOnlyList<VoiceDeviceInfo> output)
        => new(true, input, output);
}

internal readonly record struct SidecarTemporaryPaths(
    string HandshakePath,
    string? PrivateDirectory,
    string? PrivateRoot);

internal static class SidecarLauncher
{
    private const string WineTemporaryDirectoryPrefix = "perfect-comms-";
    private static readonly object BundleVersionGate = new();
    private static readonly Dictionary<string, string> BundleVersions = new(StringComparer.Ordinal);

    public static string TargetTriple()
        => TargetTripleFor(
            WineEnvironment.IsWine,
            WineEnvironment.HostOs,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
            RuntimeInformation.OSArchitecture);

    internal static string TargetTripleFor(
        bool wine,
        WineHostOs wineHostOs,
        bool windows,
        bool macOs,
        bool linux,
        Architecture architecture)
    {
        if (wine)
        {
            // The macOS resource is a universal app even though its resource key uses x86_64.
            if (wineHostOs == WineHostOs.MacOS) return "x86_64-apple-darwin";
            if (wineHostOs == WineHostOs.Linux) return "x86_64-unknown-linux-gnu";
            throw new PlatformNotSupportedException(
                "pc-capture: running under Wine but host OS is undetectable (Z: not mapped to host root); cannot select a native helper");
        }
        if (windows)
            return architecture switch
            {
                Architecture.X86 => "i686-pc-windows-msvc",
                Architecture.X64 => "x86_64-pc-windows-msvc",
                _ => throw new PlatformNotSupportedException(
                    $"pc-capture: unsupported Windows architecture {architecture}"),
            };
        if (macOs)
            return architecture switch
            {
                Architecture.X64 => "x86_64-apple-darwin",
                Architecture.Arm64 => "aarch64-apple-darwin",
                _ => throw new PlatformNotSupportedException(
                    $"pc-capture: unsupported macOS architecture {architecture}"),
            };
        if (linux)
            return architecture == Architecture.X64
                ? "x86_64-unknown-linux-gnu"
                : throw new PlatformNotSupportedException(
                    $"pc-capture: unsupported Linux architecture {architecture}");
        throw new PlatformNotSupportedException("pc-capture: unsupported operating system");
    }

    public static string HelperFileName(string triple)
        => triple.Contains("windows") ? "PerfectCommsAudio.exe" : "PerfectCommsAudio";

    public static bool IsHelperAvailable()
        => IsHelperAvailable(Assembly.GetExecutingAssembly());

    public static bool IsHelperAvailable(Assembly assembly)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(ResourceName(TargetTriple()));
            return stream != null;
        }
        catch
        {
            return false;
        }
    }

    public static string ResourceName(string triple)
        => triple switch
        {
            "x86_64-pc-windows-msvc" => "Lib.pc-capture.pc-capture-win-x64.exe",
            "i686-pc-windows-msvc" => "Lib.pc-capture.pc-capture-win-x86.exe",
            "x86_64-unknown-linux-gnu" => "Lib.pc-capture.pc-capture-linux-x64",
            "x86_64-apple-darwin" => "Lib.pc-capture.pc-capture-mac.zip",
            "aarch64-apple-darwin" => "Lib.pc-capture.pc-capture-mac.zip",
            _ => throw new PlatformNotSupportedException($"No pc-capture helper for target {triple}"),
        };

    public static string EnsureHelperExtracted(Assembly assembly, string baseDirectory, bool force)
    {
        var triple = TargetTriple();
        var resourceName = ResourceName(triple);
        var isMac = resourceName.EndsWith(".zip", StringComparison.Ordinal);
        var cacheFileName = isMac ? "pc-capture.zip" : HelperFileName(triple);
        var stage = "bundle-version";
        var diagnosticPath = Path.Combine(baseDirectory, "cache", "PerfectComms", "native", triple);

        try
        {
            var bundleVersion = BundleVersionFor(assembly, triple, resourceName);
            if (force)
                bundleVersion += $"-refresh-{Environment.ProcessId}-{Guid.NewGuid():N}";

            var bundleDirectory = NativeLibraryCache.BundleDirectory(baseDirectory, triple, bundleVersion);
            diagnosticPath = bundleDirectory;
            VoiceDiagnostics.Log(
                "sidecar.extract",
                $"event=begin stage=acquire-bundle-lease triple={triple} bundle={bundleVersion} path={NativeLibraryCache.DiagnosticValue(bundleDirectory)} force={force.ToString().ToLowerInvariant()}");

            stage = "acquire-bundle-lease";
            NativeLibraryCache.HoldBundleLease(bundleDirectory);

            stage = "extract-helper";
            var extracted = NativeLibraryCache.Extract(
                assembly,
                resourceName,
                cacheFileName,
                triple,
                baseDirectory,
                bundleVersion);
            diagnosticPath = extracted;

            string helperPath;
            if (isMac)
            {
                stage = "extract-mac-app";
                helperPath = ExtractMacApp(extracted, triple, baseDirectory, bundleVersion);
                diagnosticPath = helperPath;
                stage = "make-executable";
                MakeExecutable(helperPath);
                stage = "strip-quarantine";
                StripQuarantine(helperPath);
            }
            else
            {
                helperPath = extracted;
                if (WineEnvironment.IsWine || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    stage = "make-executable";
                    MakeExecutable(helperPath);
                }
            }

            stage = "extract-dsp";
            EnsureDspLibsExtracted(assembly, baseDirectory, triple, helperPath, bundleVersion);

            stage = "prune-stale-bundles";
            var pruned = NativeLibraryCache.PruneStaleBundles(baseDirectory, triple, bundleVersion);
            VoiceDiagnostics.Log(
                "sidecar.extract",
                $"event=complete triple={triple} bundle={bundleVersion} helper={NativeLibraryCache.DiagnosticValue(helperPath)} pruned={pruned}");
            return helperPath;
        }
        catch (Exception ex)
        {
            var detail = ex is NativeCacheExtractionException
                ? ex.Message
                : $"Sidecar extraction failed stage={stage} path={NativeLibraryCache.DiagnosticValue(diagnosticPath)} " +
                  $"error={ExceptionDiagnostic(ex)}";
            VoiceDiagnostics.Log("sidecar.extract", $"event=failed {detail}");
            throw new InvalidOperationException(detail, ex);
        }
    }

    private static string BundleVersionFor(Assembly assembly, string triple, string helperResourceName)
    {
        var key = $"{assembly.ManifestModule.ModuleVersionId:N}:{triple}";
        lock (BundleVersionGate)
        {
            if (BundleVersions.TryGetValue(key, out var existing))
                return existing;

            var resources = new List<string> { helperResourceName };
            foreach (var (resource, _) in DspLibsFor(triple))
                resources.Add(resource);
            var created = NativeLibraryCache.BuildContentVersion(assembly, resources);
            BundleVersions[key] = created;
            return created;
        }
    }

    public static (string Resource, string File)[] DspLibsFor(string triple)
        => triple switch
        {
            "x86_64-pc-windows-msvc" => new[] { ("Lib.dsp.webrtc-apm.x64.dll", "webrtc-apm.x64.dll") },
            "i686-pc-windows-msvc" => new[] { ("Lib.dsp.webrtc-apm.x86.dll", "webrtc-apm.x86.dll") },
            "x86_64-unknown-linux-gnu" => new[] { ("Lib.dsp.libwebrtc-apm.so", "libwebrtc-apm.so") },
            "x86_64-apple-darwin" => new[] { ("Lib.dsp.libwebrtc-apm.dylib", "libwebrtc-apm.dylib") },
            "aarch64-apple-darwin" => new[] { ("Lib.dsp.libwebrtc-apm.dylib", "libwebrtc-apm.dylib") },
            _ => Array.Empty<(string, string)>(),
        };

    public static void EnsureDspLibsExtracted(
        Assembly assembly,
        string baseDirectory,
        string triple,
        string helperPath,
        string bundleVersion)
    {
        var helperDir = Path.GetDirectoryName(helperPath);
        if (string.IsNullOrEmpty(helperDir)) return;
        foreach (var (resource, file) in DspLibsFor(triple))
        {
            try
            {
                using (var probe = assembly.GetManifestResourceStream(resource))
                    if (probe == null) continue;
                var extracted = NativeLibraryCache.Extract(
                    assembly,
                    resource,
                    file,
                    triple,
                    baseDirectory,
                    bundleVersion);
                var beside = Path.Combine(helperDir, file);
                if (!string.Equals(Path.GetFullPath(extracted), Path.GetFullPath(beside), StringComparison.OrdinalIgnoreCase))
                    PublishFileAtomically(extracted, beside);
            }
            catch (Exception ex)
            {
                var target = Path.Combine(helperDir, file);
                VoiceDiagnostics.Log(
                    "sidecar.dsp",
                    $"event=extract-failed stage=extract-or-place path={NativeLibraryCache.DiagnosticValue(target)} " +
                    $"resource={NativeLibraryCache.DiagnosticValue(resource)} error={ExceptionDiagnostic(ex)}");
                throw;
            }
        }
    }

    public static string ExtractMacApp(string zipPath, string triple, string baseDirectory, string bundleVersion)
    {
        var bundleDirectory = NativeLibraryCache.BundleDirectory(baseDirectory, triple, bundleVersion);
        var appDir = Path.Combine(bundleDirectory, "PerfectCommsAudio.app");
        var inner = Path.Combine(appDir, "Contents", "MacOS", "PerfectCommsAudio");
        Directory.CreateDirectory(bundleDirectory);
        var lockPath = Path.Combine(bundleDirectory, ".mac-extract.lock");
        using var extractionLock = AcquireExclusiveFileLock(lockPath, TimeSpan.FromSeconds(15));
        if (MacAppMatchesArchive(zipPath, appDir))
            return inner;

        var stagingRoot = Path.Combine(
            bundleDirectory,
            $".mac-stage-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var stagedApp = Path.Combine(stagingRoot, "PerfectCommsAudio.app");
        var backupApp = Path.Combine(
            bundleDirectory,
            $".mac-old-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var previousMoved = false;
        var published = false;
        try
        {
            Directory.CreateDirectory(stagingRoot);
            ZipFile.ExtractToDirectory(zipPath, stagingRoot, false);
            if (!MacAppMatchesArchive(zipPath, stagedApp))
                throw new InvalidDataException(
                    $"Mac helper archive is missing or failed integrity validation: {zipPath}");

            if (Directory.Exists(appDir))
            {
                Directory.Move(appDir, backupApp);
                previousMoved = true;
            }

            Directory.Move(stagedApp, appDir);
            published = true;
            TryDeleteDirectory(backupApp);
            return inner;
        }
        catch
        {
            if (!published && previousMoved && !Directory.Exists(appDir) && Directory.Exists(backupApp))
            {
                try { Directory.Move(backupApp, appDir); } catch { }
            }
            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
            if (published)
                TryDeleteDirectory(backupApp);
        }
    }

    private static bool MacAppMatchesArchive(string zipPath, string appDirectory)
    {
        if (!Directory.Exists(appDirectory))
            return false;

        const string appPrefix = "PerfectCommsAudio.app/";
        const string helperEntry = "PerfectCommsAudio.app/Contents/MacOS/PerfectCommsAudio";
        var appRoot = Path.GetFullPath(appDirectory);
        var appRootPrefix = Path.TrimEndingDirectorySeparator(appRoot) + Path.DirectorySeparatorChar;
        var foundHelper = false;
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.StartsWith(appPrefix, StringComparison.Ordinal) ||
                    entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    continue;

                var relative = entry.FullName.Substring(appPrefix.Length)
                    .Replace('/', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(appRoot, relative));
                if (!target.StartsWith(appRootPrefix, StringComparison.Ordinal) ||
                    !File.Exists(target) ||
                    new FileInfo(target).Length != entry.Length)
                    return false;

                using var expectedStream = entry.Open();
                using var actualStream = new FileStream(
                    target,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (!StreamHashesEqual(expectedStream, actualStream))
                    return false;
                if (string.Equals(entry.FullName, helperEntry, StringComparison.Ordinal))
                    foundHelper = true;
            }
            return foundHelper;
        }
        catch
        {
            return false;
        }
    }

    private static void PublishFileAtomically(string source, string target)
    {
        using (var sourceProbe = new FileStream(
                   source,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            if (File.Exists(target))
            {
                using var targetProbe = new FileStream(
                    target,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (sourceProbe.Length == targetProbe.Length && StreamHashesEqual(sourceProbe, targetProbe))
                    return;
            }
        }

        var temp = $"{target}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var input = new FileStream(
                       source,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            File.Move(temp, target, true);
            temp = string.Empty;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temp))
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }

    private static bool StreamHashesEqual(Stream left, Stream right)
    {
        using var leftSha = SHA256.Create();
        using var rightSha = SHA256.Create();
        return CryptographicOperations.FixedTimeEquals(
            leftSha.ComputeHash(left),
            rightSha.ComputeHash(right));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch { }
    }

    private static FileStream AcquireExclusiveFileLock(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    public static void MakeExecutable(string path)
    {
        if (WineEnvironment.IsWine)
        {
            WineEnvironment.HostExec("/bin/chmod", $"+x \"{WineEnvironment.ResolveHostPath(path)}\"");
            return;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("chmod", $"+x \"{path}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(2000);
        }
        catch { }
    }

    public static void StripQuarantine(string innerPath)
    {
        var appDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(innerPath)));
        var target = appDir ?? innerPath;
        if (WineEnvironment.IsWine)
        {
            WineEnvironment.HostExec("/usr/bin/xattr", $"-dr com.apple.quarantine \"{WineEnvironment.ResolveHostPath(target)}\"");
            return;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("xattr", $"-dr com.apple.quarantine \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(2000);
        }
        catch { }
    }

    public static string BuildArguments(string handshakePath, int ownerPid, bool wine)
    {
        var arguments = $"--handshake \"{handshakePath}\"";
        if (wine)
            return arguments;
        if (ownerPid <= 0)
            throw new ArgumentOutOfRangeException(nameof(ownerPid), "A native helper owner PID must be positive");
        return $"{arguments} --owner-pid {ownerPid}";
    }

    public static bool TryReadHandshake(string handshakePath, out int port, out int pid)
    {
        port = 0;
        pid = 0;
        try
        {
            if (!File.Exists(handshakePath))
                return false;
            var text = File.ReadAllText(handshakePath);
            if (string.IsNullOrWhiteSpace(text))
                return false;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("port", out var p) || !root.TryGetProperty("pid", out var i))
                return false;
            port = p.GetInt32();
            pid = i.GetInt32();
            return port > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool PollHandshake(string handshakePath, int timeoutMs, Func<bool> childAlive, out int port, out int pid)
    {
        port = 0;
        pid = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!childAlive())
                return false;
            if (TryReadHandshake(handshakePath, out port, out pid))
                return true;
            Thread.Sleep(25);
        }
        return false;
    }

    public static string NewHandshakePath()
        => Path.Combine(Path.GetTempPath(), "pc-capture-" + Guid.NewGuid().ToString("N") + ".json");

    internal static SidecarTemporaryPaths CreateTemporaryPaths(
        bool wine,
        Func<string, string> resolveWineHostPath,
        Func<string, string, bool> hostExec,
        string? wineTemporaryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(resolveWineHostPath);
        ArgumentNullException.ThrowIfNull(hostExec);
        if (!wine)
            return new SidecarTemporaryPaths(NewHandshakePath(), null, null);

        var root = Path.GetFullPath(wineTemporaryRoot ?? @"Z:\tmp");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Wine host temporary directory is unavailable: {root}");

        string? privateDirectory = null;
        try
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var candidate = Path.Combine(
                    root,
                    WineTemporaryDirectoryPrefix + Guid.NewGuid().ToString("N"));
                if (Directory.Exists(candidate))
                    continue;
                Directory.CreateDirectory(candidate);
                privateDirectory = candidate;
                break;
            }
            if (privateDirectory == null)
                throw new IOException("Could not allocate a private Wine helper directory");

            var hostDirectory = resolveWineHostPath(privateDirectory);
            if (string.IsNullOrWhiteSpace(hostDirectory) ||
                !hostExec("/bin/chmod", $"700 \"{hostDirectory}\""))
            {
                throw new UnauthorizedAccessException(
                    "Could not restrict the private Wine helper directory to mode 0700");
            }

            return new SidecarTemporaryPaths(
                Path.Combine(privateDirectory, "handshake.json"),
                privateDirectory,
                root);
        }
        catch
        {
            if (privateDirectory != null)
                TryDeleteDirectory(privateDirectory);
            throw;
        }
    }

    internal static string CreateWineTokenFile(
        SidecarTemporaryPaths paths,
        string token,
        Func<string, string> resolveWineHostPath,
        Func<string, string, bool> hostExec)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (string.IsNullOrEmpty(paths.PrivateDirectory))
            throw new InvalidOperationException("A Wine token requires a private temporary directory");

        var tokenFile = Path.Combine(paths.PrivateDirectory, "token");
        var tokenBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(token);
        using (var stream = new FileStream(tokenFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(tokenBytes, 0, tokenBytes.Length);
            stream.Flush(true);
        }

        var hostToken = resolveWineHostPath(tokenFile);
        if (string.IsNullOrWhiteSpace(hostToken) ||
            !hostExec("/bin/chmod", $"600 \"{hostToken}\""))
        {
            try { File.Delete(tokenFile); } catch { }
            throw new UnauthorizedAccessException("Could not restrict the Wine helper token to mode 0600");
        }
        return tokenFile;
    }

    internal static void CleanupTemporaryPaths(
        string? handshakePath,
        string? privateDirectory,
        string? privateRoot)
    {
        if (string.IsNullOrEmpty(privateDirectory))
        {
            if (!string.IsNullOrEmpty(handshakePath) && File.Exists(handshakePath))
                File.Delete(handshakePath);
            return;
        }
        if (string.IsNullOrEmpty(privateRoot))
            throw new InvalidOperationException("Refusing to remove a Wine helper directory without its owning root");

        var fullDirectory = Path.GetFullPath(privateDirectory);
        var fullRoot = Path.GetFullPath(privateRoot);
        var parent = Path.GetDirectoryName(fullDirectory);
        var directoryName = Path.GetFileName(fullDirectory);
        var directorySuffix = directoryName.StartsWith(WineTemporaryDirectoryPrefix, StringComparison.Ordinal)
            ? directoryName.Substring(WineTemporaryDirectoryPrefix.Length)
            : string.Empty;
        if (!Guid.TryParseExact(directorySuffix, "N", out _))
            throw new InvalidOperationException("Refusing to remove an unrecognized Wine helper directory");
        if (string.IsNullOrEmpty(parent) || !PathsEqual(parent, fullRoot))
            throw new InvalidOperationException("Refusing to remove a Wine helper directory outside its owning root");
        if (!string.IsNullOrEmpty(handshakePath))
        {
            var handshakeParent = Path.GetDirectoryName(Path.GetFullPath(handshakePath));
            if (string.IsNullOrEmpty(handshakeParent) || !PathsEqual(handshakeParent, fullDirectory))
                throw new InvalidOperationException("Refusing to clean a handshake outside its Wine helper directory");
        }
        if (Directory.Exists(fullDirectory))
            Directory.Delete(fullDirectory, true);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static SidecarDeviceEnumerationResult EnumerateDevices()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            if (!IsHelperAvailable(assembly))
                return SidecarDeviceEnumerationResult.Failure;
            var helperPath = EnsureHelperExtracted(assembly, AppContext.BaseDirectory, force: false);
            return EnumerateDevices(helperPath, WineEnvironment.IsWine, WineEnvironment.ResolveHostPath);
        }
        catch
        {
            return SidecarDeviceEnumerationResult.Failure;
        }
    }

    public static SidecarDeviceEnumerationResult EnumerateDevices(
        string helperPath,
        bool wine,
        Func<string, string> resolveWineHostPath,
        Func<string, string, bool>? hostExec = null)
    {
        var paths = default(SidecarTemporaryPaths);
        var outPath = string.Empty;
        Process? process = null;
        SidecarProcessDiagnostics? diagnostics = null;
        var sw = Stopwatch.StartNew();
        try
        {
            hostExec ??= WineEnvironment.TryHostExec;
            paths = CreateTemporaryPaths(wine, resolveWineHostPath, hostExec);
            outPath = paths.HandshakePath;
            VoiceDiagnostics.Log(
                "sidecar.launch",
                $"event=enumerate-begin wine={wine} helper=\"{SafeDiagnosticField(helperPath, 512)}\" handshake=\"{SafeDiagnosticField(outPath, 320)}\"");
            ProcessStartInfo psi;
            if (wine)
            {
                var hostHelper = resolveWineHostPath(helperPath);
                var hostOut = resolveWineHostPath(outPath);
                var args = BuildArguments(hostOut, Environment.ProcessId, wine: true);
                psi = new ProcessStartInfo("start.exe", $"/unix \"{hostHelper}\" --enumerate {args}");
            }
            else
            {
                var args = BuildArguments(outPath, Environment.ProcessId, wine: false);
                psi = new ProcessStartInfo(helperPath, $"--enumerate {args}");
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;

            process = Process.Start(psi);
            if (process == null)
            {
                VoiceDiagnostics.Log("sidecar.launch", "event=enumerate-start-failed reason=process-start-null");
                return SidecarDeviceEnumerationResult.Failure;
            }
            diagnostics = AttachProcessDiagnostics(process, token: string.Empty, purpose: "enumerate");
            VoiceDiagnostics.Log("sidecar.launch", $"event=enumerate-process-start pid={SafeProcessId(process)}");
            var pollSw = Stopwatch.StartNew();
            while (pollSw.ElapsedMilliseconds < 3000)
            {
                if (TryReadDevicesFile(outPath, out var input, out var output))
                {
                    VoiceDiagnostics.Log(
                        "sidecar.launch",
                        $"event=enumerate-complete pid={SafeProcessId(process)} inputs={input.Count} outputs={output.Count} elapsedMs={pollSw.ElapsedMilliseconds}");
                    return SidecarDeviceEnumerationResult.Success(input, output);
                }
                Thread.Sleep(25);
            }
            VoiceDiagnostics.Log(
                "sidecar.launch",
                $"event=enumerate-timeout pid={SafeProcessId(process)} elapsedMs={pollSw.ElapsedMilliseconds}");
            return SidecarDeviceEnumerationResult.Failure;
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log(
                "sidecar.launch",
                $"event=enumerate-failed elapsedMs={sw.ElapsedMilliseconds} error={ExceptionDiagnostic(ex)}");
            return SidecarDeviceEnumerationResult.Failure;
        }
        finally
        {
            if (process != null && !wine)
                KillQuietly(process);
            diagnostics?.Complete("enumerate-finished");
            try { CleanupTemporaryPaths(outPath, paths.PrivateDirectory, paths.PrivateRoot); } catch { }
        }
    }

    internal static bool TryReadDevicesFile(string path, out List<VoiceDeviceInfo> input, out List<VoiceDeviceInfo> output)
    {
        input = new List<VoiceDeviceInfo>();
        output = new List<VoiceDeviceInfo>();
        try
        {
            if (!File.Exists(path))
                return false;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return SidecarProtocol.TryReadDevices(text, out input) &&
                   SidecarProtocol.TryReadOutputDevices(text, out output);
        }
        catch
        {
            return false;
        }
    }

    public static SidecarLaunchResult Launch(
        string helperPath,
        string token,
        int handshakeTimeoutMs,
        bool wine,
        Func<string, string> resolveWineHostPath,
        Func<string, string, bool>? hostExec = null)
    {
        var result = new SidecarLaunchResult();
        var paths = default(SidecarTemporaryPaths);
        Process? process = null;
        SidecarProcessDiagnostics? diagnostics = null;
        string? tokenFile = null;
        var sw = Stopwatch.StartNew();
        try
        {
            hostExec ??= WineEnvironment.TryHostExec;
            paths = CreateTemporaryPaths(wine, resolveWineHostPath, hostExec);
            result.HandshakePath = paths.HandshakePath;
            result.TemporaryDirectory = paths.PrivateDirectory;
            result.TemporaryRoot = paths.PrivateRoot;
            VoiceDiagnostics.Log(
                "sidecar.launch",
                $"event=begin wine={wine} timeoutMs={handshakeTimeoutMs} helper=\"{SafeDiagnosticField(helperPath, 512)}\" handshake=\"{SafeDiagnosticField(result.HandshakePath, 320)}\"");
            if (wine)
            {
                tokenFile = CreateWineTokenFile(paths, token, resolveWineHostPath, hostExec);
                var hostToken = resolveWineHostPath(tokenFile);
                var hostHelper = resolveWineHostPath(helperPath);
                var hostHandshake = resolveWineHostPath(result.HandshakePath);
                var wineArguments = BuildArguments(hostHandshake, Environment.ProcessId, wine: true);
                var wpsi = new ProcessStartInfo("start.exe",
                    $"/unix \"{hostHelper}\" {wineArguments} --token-file \"{hostToken}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                process = Process.Start(wpsi);
                if (process == null)
                {
                    result.FailureReason = "Process.Start returned null (start.exe missing)";
                    return result;
                }
                result.Process = process;
                diagnostics = AttachProcessDiagnostics(process, token, "voice-wine");
                result.Diagnostics = diagnostics;
                VoiceDiagnostics.Log("sidecar.launch", $"event=process-start pid={SafeProcessId(process)} wine=true");

                if (!PollHandshake(result.HandshakePath, handshakeTimeoutMs, () => true, out var wport, out var wpid))
                {
                    result.FailureReason = "handshake timeout (wine host-exec: verify Z: maps to host root, winepath, and host mic permission)";
                    return result;
                }

                result.Port = wport;
                result.Pid = wpid;
                result.Success = true;
                VoiceDiagnostics.Log(
                    "sidecar.launch",
                    $"event=handshake-ready launcherPid={SafeProcessId(process)} helperPid={wpid} port={wport} wine=true elapsedMs={sw.ElapsedMilliseconds}");
                return result;
            }

            var nativeArguments = BuildArguments(result.HandshakePath, Environment.ProcessId, wine: false);
            var psi = new ProcessStartInfo(helperPath, nativeArguments)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            process = Process.Start(psi);
            if (process == null)
            {
                result.FailureReason = "Process.Start returned null";
                return result;
            }
            result.Process = process;
            diagnostics = AttachProcessDiagnostics(process, token, "voice");
            result.Diagnostics = diagnostics;
            VoiceDiagnostics.Log("sidecar.launch", $"event=process-start pid={SafeProcessId(process)} wine=false");

            try
            {
                process.StandardInput.WriteLine(token);
                process.StandardInput.Flush();
                process.StandardInput.Close();
            }
            catch (Exception ex)
            {
                result.FailureReason = "token write failed: " + ex.Message;
                KillQuietly(process);
                return result;
            }

            if (!PollHandshake(result.HandshakePath, handshakeTimeoutMs, () => !ProcessExited(process), out var port, out var pid))
            {
                result.FailureReason = ProcessExited(process)
                    ? "helper exited before handshake (host-exec blocked or crash)"
                    : "handshake timeout";
                KillQuietly(process);
                return result;
            }

            result.Port = port;
            result.Pid = pid;
            result.Success = true;
            VoiceDiagnostics.Log(
                "sidecar.launch",
                $"event=handshake-ready launcherPid={SafeProcessId(process)} helperPid={pid} port={port} wine=false elapsedMs={sw.ElapsedMilliseconds}");
            return result;
        }
        catch (Exception ex)
        {
            result.FailureReason = "launch failed: " + ex.Message;
            if (process != null)
                KillQuietly(process);
            return result;
        }
        finally
        {
            if (!result.Success)
            {
                VoiceDiagnostics.Log(
                    "sidecar.launch",
                    $"event=failed elapsedMs={sw.ElapsedMilliseconds} reason=\"{SafeDiagnosticField(result.FailureReason, 320)}\"");
                diagnostics?.Complete("launch-failed");
            }
            if (tokenFile != null)
            {
                try { File.Delete(tokenFile); } catch { }
            }
            if (!result.Success)
            {
                try { CleanupTemporaryPaths(result.HandshakePath, result.TemporaryDirectory, result.TemporaryRoot); } catch { }
            }
        }
    }

    private static SidecarProcessDiagnostics AttachProcessDiagnostics(Process process, string token, string purpose)
    {
        var diagnostics = new SidecarProcessDiagnostics(process, token, purpose);
        diagnostics.Attach();
        return diagnostics;
    }

    internal static string SanitizeStderrForDiagnostics(string? line, string token)
    {
        if (string.IsNullOrEmpty(line)) return string.Empty;

        var normalized = new StringBuilder(Math.Min(line.Length, SidecarProcessDiagnostics.MaxLineChars));
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            normalized.Append(char.IsControl(c) || c == '"' ? ' ' : c);
        }
        var safe = normalized.ToString();
        if (!string.IsNullOrEmpty(token))
            safe = safe.Replace(token, "[redacted-token]", StringComparison.Ordinal);

        safe = RedactNamedValue(safe, "candidate");
        safe = RedactNamedValue(safe, "sdp");
        safe = RedactNamedValue(safe, "credential");
        safe = RedactNamedValue(safe, "authorization");
        safe = RedactNamedValue(safe, "token");

        if (safe.Length > SidecarProcessDiagnostics.MaxLineChars)
            safe = safe.Substring(0, SidecarProcessDiagnostics.MaxLineChars) + "...";
        return safe;
    }

    private static string RedactNamedValue(string value, string name)
    {
        var searchFrom = 0;
        while (searchFrom < value.Length)
        {
            var nameIndex = value.IndexOf(name, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (nameIndex < 0) return value;
            var suffixStart = nameIndex + name.Length;
            if (suffixStart + 6 <= value.Length &&
                value.AsSpan(suffixStart, 6).Equals("_bytes".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                searchFrom = suffixStart + 6;
                continue;
            }
            var separatorLimit = Math.Min(value.Length, nameIndex + name.Length + 8);
            for (var i = nameIndex + name.Length; i < separatorLimit; i++)
            {
                if (value[i] != '=' && value[i] != ':') continue;
                return value.Substring(0, i + 1) + "[redacted]";
            }
            searchFrom = nameIndex + name.Length;
        }
        return value;
    }

    private static string SafeDiagnosticField(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var length = Math.Min(value.Length, maxChars);
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var c = value[i];
            builder.Append(char.IsControl(c) || c == '"' ? ' ' : c);
        }
        if (value.Length > maxChars) builder.Append("...");
        return builder.ToString();
    }

    private static int SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch { return -1; }
    }

    private static string ExceptionDiagnostic(Exception ex)
        => $"{SafeDiagnosticField(ex.GetType().Name, 80)}:\"{SafeDiagnosticField(ex.Message, 240)}\"";

    private static bool ProcessExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static void KillQuietly(Process process)
    {
        try { if (!process.HasExited) process.Kill(); } catch { }
    }
}

internal sealed class SidecarLaunchResult
{
    public bool Success;
    public int Port;
    public int Pid;
    public Process? Process;
    public SidecarProcessDiagnostics? Diagnostics;
    public string HandshakePath = "";
    public string? TemporaryDirectory;
    public string? TemporaryRoot;
    public string FailureReason = "";
}

internal sealed class SidecarProcessDiagnostics
{
    internal const int MaxLineChars = 512;
    private const int MaxLoggedLines = 200;
    private const int MaxLinesPerWindow = 20;
    private const int WindowMs = 1000;

    private readonly object _gate = new();
    private readonly Process _process;
    private readonly string _token;
    private readonly string _purpose;
    private long _windowStartTick;
    private int _windowLines;
    private int _logged;
    private int _dropped;
    private int _summaryLogged;

    public SidecarProcessDiagnostics(Process process, string token, string purpose)
    {
        _process = process;
        _token = token ?? string.Empty;
        _purpose = purpose;
        _windowStartTick = Environment.TickCount64;
    }

    public void Attach()
    {
        try
        {
            _process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    Complete("stderr-eof");
                    return;
                }
                LogLine(args.Data);
            };
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                var exitCode = -1;
                try { exitCode = _process.ExitCode; } catch { }
                VoiceDiagnostics.Log(
                    "sidecar.launch",
                    $"event=process-exit purpose={_purpose} pid={SafePid()} exitCode={exitCode}");
            };
            _process.BeginErrorReadLine();
            VoiceDiagnostics.Log(
                "sidecar.stderr",
                $"event=capture-start purpose={_purpose} pid={SafePid()} perSecondLimit={MaxLinesPerWindow} totalLimit={MaxLoggedLines} maxLineChars={MaxLineChars}");
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log(
                "sidecar.stderr",
                $"event=capture-failed purpose={_purpose} pid={SafePid()} error={ex.GetType().Name}:\"{SidecarLauncher.SanitizeStderrForDiagnostics(ex.Message, _token)}\"");
        }
    }

    private void LogLine(string line)
    {
        string? safe = null;
        var sequence = 0;
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (now - _windowStartTick >= WindowMs)
            {
                _windowStartTick = now;
                _windowLines = 0;
            }
            if (_logged >= MaxLoggedLines || _windowLines >= MaxLinesPerWindow)
            {
                _dropped++;
                return;
            }
            _windowLines++;
            sequence = ++_logged;
            safe = SidecarLauncher.SanitizeStderrForDiagnostics(line, _token);
        }

        VoiceDiagnostics.Log(
            "sidecar.stderr",
            $"purpose={_purpose} pid={SafePid()} seq={sequence} text=\"{safe}\"");
    }

    public void Complete(string reason)
    {
        if (Interlocked.Exchange(ref _summaryLogged, 1) != 0) return;
        int logged;
        int dropped;
        lock (_gate)
        {
            logged = _logged;
            dropped = _dropped;
        }
        VoiceDiagnostics.Log(
            "sidecar.stderr",
            $"event=capture-stop purpose={_purpose} pid={SafePid()} reason={reason} logged={logged} dropped={dropped}");
    }

    private int SafePid()
    {
        try { return _process.Id; }
        catch { return -1; }
    }
}
