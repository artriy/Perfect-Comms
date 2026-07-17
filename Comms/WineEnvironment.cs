using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace VoiceChatPlugin.VoiceChat;

internal enum WineHostOs
{
    Unknown = 0,
    MacOS = 1,
    Linux = 2,
}

// Detects Wine/Proton/CrossOver and provides the host-OS/path/process helpers used to launch and
// clean up the native macOS or Linux audio helper outside the Windows compatibility layer.
internal static class WineEnvironment
{
    internal const int HostExecTimeoutMs = 15_000;
    private static bool _probed;
    private static bool _isWine;

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = false)]
    private static extern IntPtr GetModuleHandleA(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = false)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    // The canonical Wine check: ntdll exports wine_get_version only under Wine.
    public static bool IsWine
    {
        get
        {
            if (_probed) return _isWine;
            _probed = true;
            try
            {
                var ntdll = GetModuleHandleA("ntdll.dll");
                _isWine = ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
            }
            catch
            {
                _isWine = false;
            }
            return _isWine;
        }
    }

    private static bool _hostOsProbed;
    private static WineHostOs _hostOs;

    public static WineHostOs HostOs
    {
        get
        {
            if (_hostOsProbed) return _hostOs;
            _hostOsProbed = true;
            _hostOs = DetectHostOs();
            return _hostOs;
        }
    }

    private static WineHostOs DetectHostOs()
    {
        if (!IsWine) return WineHostOs.Unknown;
        try
        {
            if (Directory.Exists(@"Z:\System\Library\CoreServices")) return WineHostOs.MacOS;
            if (Directory.Exists(@"Z:\proc")) return WineHostOs.Linux;
        }
        catch { }
        return WineHostOs.Unknown;
    }

    public static void HostExec(string unixProgram, string unixArgs)
        => _ = TryHostExec(unixProgram, unixArgs);

    public static bool TryHostExec(string unixProgram, string unixArgs)
    {
        try
        {
            using var p = Process.Start(BuildHostExecStartInfo(unixProgram, unixArgs));
            if (p == null)
                return false;
            if (!p.WaitForExit(HostExecTimeoutMs))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static ProcessStartInfo BuildHostExecStartInfo(string unixProgram, string unixArgs)
        // Wine start.exe treats the argument immediately after /unix as the program, so every
        // other start option must precede /unix.
        => new("start.exe", $"/wait /unix \"{unixProgram}\" {unixArgs}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

    public static ProcessStartInfo BuildWinePathStartInfo(string windowsPath)
    {
        var psi = new ProcessStartInfo("winepath", $"-u \"{windowsPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        return psi;
    }

    public static string ResolveHostPath(string windowsPath)
    {
        try
        {
            using var p = Process.Start(BuildWinePathStartInfo(windowsPath));
            if (p != null)
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                var host = output.Trim();
                if (!string.IsNullOrEmpty(host))
                    return host;
            }
        }
        catch
        {
        }
        return ManualHostPath(windowsPath);
    }

    private static string ManualHostPath(string windowsPath)
    {
        if (windowsPath.Length >= 2 && (windowsPath[0] == 'Z' || windowsPath[0] == 'z') && windowsPath[1] == ':')
        {
            var rest = windowsPath.Substring(2).Replace('\\', '/');
            if (!rest.StartsWith("/"))
                rest = "/" + rest;
            return rest;
        }
        return windowsPath;
    }
}
