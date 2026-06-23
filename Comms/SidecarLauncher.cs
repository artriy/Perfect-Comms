using System;
using System.Runtime.InteropServices;

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

    public static string BuildArguments(string handshakePath)
        => $"--handshake \"{handshakePath}\"";
}
