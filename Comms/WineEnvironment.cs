using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VoiceChatPlugin.VoiceChat;

internal enum WineHostOs
{
    Unknown = 0,
    MacOS = 1,
    Linux = 2,
}

// Detects whether we're running under Wine/Proton (Linux) and provides a Wine-safe way to
// learn our real local IPv4. Both matter for WebRTC: under Wine, SIPSorcery's ICE host-candidate
// gathering (which leans on the Windows network-interface APIs) is unreliable and often yields
// no candidates, so peers never connect. See docs/wine-nat-fix-plan.md.
internal static class WineEnvironment
{
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
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("start.exe", $"/unix {unixProgram} {unixArgs}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(2000);
        }
        catch { }
    }

    // Get our real outbound local IPv4 WITHOUT NetworkInterface.GetAllNetworkInterfaces() (which Wine
    // mis-reports). Opening a UDP socket toward a public address and reading LocalEndPoint resolves the
    // routable local address even on Wine; no packets are actually sent (UDP connect just sets the route).
    public static IPAddress? GetLocalIPv4()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 53);
            if (s.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address))
                return ep.Address;
        }
        catch
        {
            // fall through
        }
        return null;
    }

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
