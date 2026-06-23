using System;
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
        => triple.Contains("windows") ? "pc-capture.exe" : "pc-capture";

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
            return inner;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            MakeExecutable(extracted);
        return extracted;
    }

    public static string ExtractMacApp(string zipPath, string triple, string baseDirectory)
    {
        var appDir = Path.Combine(baseDirectory, "cache", "PerfectComms", "native", triple, "pc-capture.app");
        var inner = Path.Combine(appDir, "Contents", "MacOS", "pc-capture");
        if (File.Exists(inner))
            return inner;
        if (Directory.Exists(appDir))
            Directory.Delete(appDir, true);
        ZipFile.ExtractToDirectory(zipPath, Path.GetDirectoryName(appDir)!, true);
        return inner;
    }

    public static void MakeExecutable(string path)
    {
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
}
