using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

internal static class SidecarLauncher
{
    private static readonly object BundleVersionGate = new();
    private static readonly Dictionary<string, string> BundleVersions = new(StringComparer.Ordinal);

    public static string TargetTriple()
    {
        if (WineEnvironment.IsWine)
        {
            if (WineEnvironment.HostOs == WineHostOs.MacOS) return "x86_64-apple-darwin";
            if (WineEnvironment.HostOs == WineHostOs.Linux) return "x86_64-unknown-linux-gnu";
            throw new PlatformNotSupportedException(
                "pc-capture: running under Wine but host OS is undetectable (Z: not mapped to host root); cannot select a native helper");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return RuntimeInformation.OSArchitecture == Architecture.X86
                ? "i686-pc-windows-msvc"
                : "x86_64-pc-windows-msvc";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "aarch64-apple-darwin"
                : "x86_64-apple-darwin";
        return "x86_64-unknown-linux-gnu";
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
            "x86_64-pc-windows-msvc" => new[] { ("Lib.dsp.webrtc-apm.x64.dll", "webrtc-apm.x64.dll"), ("Lib.dsp.df.x64.dll", "df.x64.dll") },
            "i686-pc-windows-msvc" => new[] { ("Lib.dsp.webrtc-apm.x86.dll", "webrtc-apm.x86.dll"), ("Lib.dsp.df.x86.dll", "df.x86.dll") },
            "x86_64-unknown-linux-gnu" => new[] { ("Lib.dsp.libwebrtc-apm.so", "libwebrtc-apm.so"), ("Lib.dsp.libdf.so", "libdf.so") },
            "x86_64-apple-darwin" => new[] { ("Lib.dsp.libwebrtc-apm.dylib", "libwebrtc-apm.dylib"), ("Lib.dsp.libdf.dylib", "libdf.dylib") },
            "aarch64-apple-darwin" => new[] { ("Lib.dsp.libwebrtc-apm.dylib", "libwebrtc-apm.dylib"), ("Lib.dsp.libdf.dylib", "libdf.dylib") },
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
                {
                    if (!File.Exists(beside))
                        File.Copy(extracted, beside, false);
                }
            }
            catch (Exception ex)
            {
                var target = Path.Combine(helperDir, file);
                VoiceDiagnostics.Log(
                    "sidecar.dsp",
                    $"event=extract-failed stage=extract-or-place path={NativeLibraryCache.DiagnosticValue(target)} " +
                    $"resource={NativeLibraryCache.DiagnosticValue(resource)} error={ExceptionDiagnostic(ex)}");
            }
        }
    }

    public static string ExtractMacApp(string zipPath, string triple, string baseDirectory, string bundleVersion)
    {
        var bundleDirectory = NativeLibraryCache.BundleDirectory(baseDirectory, triple, bundleVersion);
        var appDir = Path.Combine(bundleDirectory, "PerfectCommsAudio.app");
        var inner = Path.Combine(appDir, "Contents", "MacOS", "PerfectCommsAudio");
        // The parent bundle is content-addressed from the zip bytes. Archive entry timestamps are
        // normally older than the freshly extracted zip file, so comparing mtimes would delete and
        // recreate this app on every call. Existence is sufficient for an immutable bundle.
        if (File.Exists(inner))
            return inner;

        Directory.CreateDirectory(bundleDirectory);
        var lockPath = Path.Combine(bundleDirectory, ".mac-extract.lock");
        using var extractionLock = AcquireExclusiveFileLock(lockPath, TimeSpan.FromSeconds(15));
        if (File.Exists(inner))
            return inner;

        // Only the lock owner can observe/repair a partial extraction left by a crashed process.
        if (Directory.Exists(appDir))
            Directory.Delete(appDir, true);
        ZipFile.ExtractToDirectory(zipPath, bundleDirectory, true);
        if (!File.Exists(inner))
            throw new InvalidDataException(
                $"Mac helper archive is missing PerfectCommsAudio.app/Contents/MacOS/PerfectCommsAudio: {zipPath}");
        return inner;
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
    {
        var dir = WineEnvironment.IsWine ? @"Z:\tmp" : Path.GetTempPath();
        return Path.Combine(dir, "pc-capture-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public static (List<string> Input, List<string> Output) EnumerateDevices()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            if (!IsHelperAvailable(assembly))
                return (new List<string>(), new List<string>());
            var helperPath = EnsureHelperExtracted(assembly, AppContext.BaseDirectory, force: false);
            return EnumerateDevices(helperPath, WineEnvironment.IsWine, WineEnvironment.ResolveHostPath);
        }
        catch
        {
            return (new List<string>(), new List<string>());
        }
    }

    public static (List<string> Input, List<string> Output) EnumerateDevices(string helperPath, bool wine, Func<string, string> resolveWineHostPath)
    {
        var outPath = NewHandshakePath();
        Process? process = null;
        SidecarProcessDiagnostics? diagnostics = null;
        var sw = Stopwatch.StartNew();
        VoiceDiagnostics.Log(
            "sidecar.launch",
            $"event=enumerate-begin wine={wine} helper=\"{SafeDiagnosticField(helperPath, 512)}\" handshake=\"{SafeDiagnosticField(outPath, 320)}\"");
        try
        {
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
                return (new List<string>(), new List<string>());
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
                    return (input, output);
                }
                Thread.Sleep(25);
            }
            VoiceDiagnostics.Log(
                "sidecar.launch",
                $"event=enumerate-timeout pid={SafeProcessId(process)} elapsedMs={pollSw.ElapsedMilliseconds}");
            return (new List<string>(), new List<string>());
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log(
                "sidecar.launch",
                $"event=enumerate-failed elapsedMs={sw.ElapsedMilliseconds} error={ExceptionDiagnostic(ex)}");
            return (new List<string>(), new List<string>());
        }
        finally
        {
            if (process != null && !wine)
                KillQuietly(process);
            diagnostics?.Complete("enumerate-finished");
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
        }
    }

    private static bool TryReadDevicesFile(string path, out List<string> input, out List<string> output)
    {
        input = new List<string>();
        output = new List<string>();
        try
        {
            if (!File.Exists(path))
                return false;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var ok = SidecarProtocol.TryReadDevices(text, out input);
            SidecarProtocol.TryReadOutputDevices(text, out output);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public static SidecarLaunchResult Launch(string helperPath, string token, int handshakeTimeoutMs, bool wine, Func<string, string> resolveWineHostPath)
    {
        var result = new SidecarLaunchResult { HandshakePath = NewHandshakePath() };
        Process? process = null;
        SidecarProcessDiagnostics? diagnostics = null;
        string? tokenFile = null;
        var sw = Stopwatch.StartNew();
        VoiceDiagnostics.Log(
            "sidecar.launch",
            $"event=begin wine={wine} timeoutMs={handshakeTimeoutMs} helper=\"{SafeDiagnosticField(helperPath, 512)}\" handshake=\"{SafeDiagnosticField(result.HandshakePath, 320)}\"");
        try
        {
            if (wine)
            {
                tokenFile = result.HandshakePath + ".token";
                File.WriteAllText(tokenFile, token);
                var hostToken = resolveWineHostPath(tokenFile);
                WineEnvironment.HostExec("/bin/chmod", $"600 \"{hostToken}\"");
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
