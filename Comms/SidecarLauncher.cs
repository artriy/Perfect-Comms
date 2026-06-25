using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

internal static class SidecarLauncher
{
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

        if (force)
        {
            try
            {
                var cacheDir = Path.Combine(baseDirectory, "cache", "PerfectComms", "native", triple);
                if (Directory.Exists(cacheDir))
                    Directory.Delete(cacheDir, true);
            }
            catch { }
        }

        var extracted = VoiceChatPlugin.NativeLibraryCache.Extract(assembly, resourceName, cacheFileName, triple, baseDirectory);

        if (isMac)
        {
            var inner = ExtractMacApp(extracted, triple, baseDirectory);
            MakeExecutable(inner);
            StripQuarantine(inner);
            return inner;
        }

        if (WineEnvironment.IsWine || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            MakeExecutable(extracted);
        return extracted;
    }

    public static string ExtractMacApp(string zipPath, string triple, string baseDirectory)
    {
        var appDir = Path.Combine(baseDirectory, "cache", "PerfectComms", "native", triple, "PerfectCommsAudio.app");
        var inner = Path.Combine(appDir, "Contents", "MacOS", "PerfectCommsAudio");
        if (File.Exists(inner) &&
            File.GetLastWriteTimeUtc(inner) >= File.GetLastWriteTimeUtc(zipPath))
            return inner;
        if (Directory.Exists(appDir))
            Directory.Delete(appDir, true);
        ZipFile.ExtractToDirectory(zipPath, Path.GetDirectoryName(appDir)!, true);
        return inner;
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

    public static string BuildArguments(string handshakePath)
        => $"--handshake \"{handshakePath}\"";

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
        try
        {
            ProcessStartInfo psi;
            if (wine)
            {
                var hostHelper = resolveWineHostPath(helperPath);
                var hostOut = resolveWineHostPath(outPath);
                psi = new ProcessStartInfo("start.exe", $"/unix \"{hostHelper}\" --enumerate --handshake \"{hostOut}\"");
            }
            else
            {
                psi = new ProcessStartInfo(helperPath, $"--enumerate --handshake \"{outPath}\"");
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            process = Process.Start(psi);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                if (TryReadDevicesFile(outPath, out var input, out var output))
                    return (input, output);
                Thread.Sleep(25);
            }
            return (new List<string>(), new List<string>());
        }
        catch
        {
            return (new List<string>(), new List<string>());
        }
        finally
        {
            if (process != null && !wine)
                KillQuietly(process);
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
        string? tokenFile = null;
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
                var wpsi = new ProcessStartInfo("start.exe",
                    $"/unix \"{hostHelper}\" --handshake \"{hostHandshake}\" --token-file \"{hostToken}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                };

                process = Process.Start(wpsi);
                if (process == null)
                {
                    result.FailureReason = "Process.Start returned null (start.exe missing)";
                    return result;
                }
                result.Process = process;

                if (!PollHandshake(result.HandshakePath, handshakeTimeoutMs, () => true, out var wport, out var wpid))
                {
                    result.FailureReason = "handshake timeout (wine host-exec: verify Z: maps to host root, winepath, and host mic permission)";
                    return result;
                }

                result.Port = wport;
                result.Pid = wpid;
                result.Success = true;
                return result;
            }

            var args = BuildArguments(result.HandshakePath);
            var psi = new ProcessStartInfo(helperPath, args)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };

            process = Process.Start(psi);
            if (process == null)
            {
                result.FailureReason = "Process.Start returned null";
                return result;
            }
            result.Process = process;

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
            if (tokenFile != null)
            {
                try { File.Delete(tokenFile); } catch { }
            }
        }
    }

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
    public string HandshakePath = "";
    public string FailureReason = "";
}
