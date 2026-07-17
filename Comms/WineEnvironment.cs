using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

internal enum WineHostOs
{
    Unknown = 0,
    MacOS = 1,
    Linux = 2,
}

internal readonly record struct WineHostActionResult(
    bool Started,
    bool ReceiptVerified,
    bool TimedOut,
    int? WrapperExitCode,
    string FailureKind,
    long ElapsedMilliseconds,
    string ProcessOutput)
{
    internal bool Succeeded => Started && ReceiptVerified && !TimedOut;

    internal static WineHostActionResult Verified(int? wrapperExitCode = null)
        => new(true, true, false, wrapperExitCode, string.Empty, 0, string.Empty);

    internal static WineHostActionResult Failed(string failureKind)
        => new(false, false, false, null, failureKind, 0, string.Empty);

    internal string DiagnosticSummary
        => $"result={(Succeeded ? "verified" : "failed")} " +
           $"failure={SafeValue(string.IsNullOrEmpty(FailureKind) ? "none" : FailureKind, 80)} " +
           $"wrapperExit={(WrapperExitCode?.ToString() ?? "unknown")} " +
           $"receipt={ReceiptVerified.ToString().ToLowerInvariant()} " +
           $"timedOut={TimedOut.ToString().ToLowerInvariant()} elapsedMs={ElapsedMilliseconds}" +
           (string.IsNullOrEmpty(ProcessOutput) ? string.Empty : $" output=\"{SafeValue(ProcessOutput, 320)}\"");

    private static string SafeValue(string value, int maxChars)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maxChars));
        for (var i = 0; i < value.Length && builder.Length < maxChars; i++)
        {
            var c = value[i];
            builder.Append(char.IsControl(c) || c == '"' ? ' ' : c);
        }
        if (value.Length > maxChars) builder.Append("...");
        return builder.ToString();
    }
}

internal delegate WineHostActionResult WineHostActionExecutor(
    string operation,
    string script,
    IReadOnlyList<string> hostArguments,
    string receiptPath,
    string expectedReceipt);

// Detects Wine/Proton/CrossOver and provides the host-OS/path/process helpers used to launch and
// clean up the native macOS or Linux audio helper outside the Windows compatibility layer.
internal static class WineEnvironment
{
    // Cold Proton/CrossOver prefixes can take several seconds to dispatch their first host
    // process. Keep the original bounded 15-second allowance while verifying a receipt instead
    // of trusting start.exe's exit status.
    internal const int HostActionTimeoutMs = 15_000;
    private static readonly Lazy<bool> WineProbe = new(
        DetectWine,
        LazyThreadSafetyMode.ExecutionAndPublication);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = false)]
    private static extern IntPtr GetModuleHandleA(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = false)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    // The canonical Wine check: ntdll exports wine_get_version only under Wine.
    public static bool IsWine => WineProbe.Value;

    private static bool DetectWine()
    {
        try
        {
            var ntdll = GetModuleHandleA("ntdll.dll");
            return ntdll != IntPtr.Zero &&
                   GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static readonly Lazy<WineHostOs> HostOsProbe = new(
        DetectHostOs,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static WineHostOs HostOs => HostOsProbe.Value;

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

    public static void HostExec(string unixProgram, params string[] unixArgs)
        => _ = TryHostExec(unixProgram, unixArgs);

    // Fire-and-forget host operations are used only for best-effort cleanup and extraction
    // preparation. Security-sensitive operations use RunVerifiedHostAction below because several
    // Wine/Proton builds return a non-zero start.exe status even after /unix successfully starts
    // the host process.
    public static bool TryHostExec(string unixProgram, params string[] unixArgs)
    {
        try
        {
            using var p = Process.Start(BuildHostExecStartInfo(unixProgram, unixArgs));
            return p != null;
        }
        catch
        {
            return false;
        }
    }

    internal static ProcessStartInfo BuildHostExecStartInfo(
        string unixProgram,
        IReadOnlyList<string> unixArgs)
    {
        ThrowIfNullOrWhiteSpace(unixProgram, nameof(unixProgram));
        ArgumentNullException.ThrowIfNull(unixArgs);
        var psi = NewHostStartInfo(redirectOutput: false);
        psi.ArgumentList.Add("/unix");
        psi.ArgumentList.Add(unixProgram);
        foreach (var argument in unixArgs)
            psi.ArgumentList.Add(argument);
        return psi;
    }

    internal static ProcessStartInfo BuildWineShellStartInfo(
        string script,
        IReadOnlyList<string> hostArguments)
    {
        ThrowIfNullOrWhiteSpace(script, nameof(script));
        ArgumentNullException.ThrowIfNull(hostArguments);
        var psi = NewHostStartInfo(redirectOutput: true);
        psi.ArgumentList.Add("/unix");
        psi.ArgumentList.Add("/bin/sh");
        psi.ArgumentList.Add("-c");
        // Raw multiline literals follow the source checkout's line endings. Normalize here so a
        // Windows CRLF checkout cannot feed stray carriage returns to the host POSIX shell.
        psi.ArgumentList.Add(script.Replace("\r\n", "\n").Replace('\r', '\n'));
        // With sh -c, the first value after the script becomes $0. Supplying a fixed value keeps
        // every filesystem path in a positional parameter instead of interpolating it as shell.
        psi.ArgumentList.Add("perfect-comms-bootstrap");
        foreach (var argument in hostArguments)
            psi.ArgumentList.Add(argument);
        return psi;
    }

    internal static WineHostActionResult RunVerifiedHostAction(
        string operation,
        string script,
        IReadOnlyList<string> hostArguments,
        string receiptPath,
        string expectedReceipt)
        => RunVerifiedHostActionCore(
            operation,
            script,
            hostArguments,
            receiptPath,
            expectedReceipt,
            static startInfo => Process.Start(startInfo),
            HostActionTimeoutMs);

    internal static WineHostActionResult RunVerifiedHostActionForTest(
        string operation,
        string script,
        IReadOnlyList<string> hostArguments,
        string receiptPath,
        string expectedReceipt,
        Func<ProcessStartInfo, Process?> startProcess,
        int timeoutMs)
        => RunVerifiedHostActionCore(
            operation,
            script,
            hostArguments,
            receiptPath,
            expectedReceipt,
            startProcess,
            timeoutMs);

    private static WineHostActionResult RunVerifiedHostActionCore(
        string operation,
        string script,
        IReadOnlyList<string> hostArguments,
        string receiptPath,
        string expectedReceipt,
        Func<ProcessStartInfo, Process?> startProcess,
        int timeoutMs)
    {
        ThrowIfNullOrWhiteSpace(operation, nameof(operation));
        ThrowIfNullOrWhiteSpace(receiptPath, nameof(receiptPath));
        ThrowIfNullOrWhiteSpace(expectedReceipt, nameof(expectedReceipt));
        ArgumentNullException.ThrowIfNull(startProcess);
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        TryDeleteFile(receiptPath);

        var stopwatch = Stopwatch.StartNew();
        Process? process = null;
        var processOutput = new StringBuilder();
        var outputGate = new object();
        var sawInvalidReceipt = false;
        try
        {
            process = startProcess(BuildWineShellStartInfo(script, hostArguments));
            if (process == null)
                return LogHostAction(operation, new WineHostActionResult(
                    false, false, false, null, "wrapper-start-null", stopwatch.ElapsedMilliseconds, string.Empty));

            try
            {
                process.OutputDataReceived += (_, args) =>
                    AppendProcessOutput(processOutput, outputGate, "stdout", args.Data);
                process.ErrorDataReceived += (_, args) =>
                    AppendProcessOutput(processOutput, outputGate, "stderr", args.Data);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch
            {
                // Receipt verification is authoritative; wrapper output is diagnostic only.
            }

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (TryReadReceipt(receiptPath, out var receipt))
                {
                    if (string.Equals(receipt, expectedReceipt, StringComparison.Ordinal))
                    {
                        var verified = new WineHostActionResult(
                            true,
                            true,
                            false,
                            TryGetExitCode(process),
                            string.Empty,
                            stopwatch.ElapsedMilliseconds,
                            SnapshotOutput(processOutput, outputGate));
                        return LogHostAction(operation, verified);
                    }
                    sawInvalidReceipt = true;
                }
                Thread.Sleep(25);
            }

            TryKill(process);
            var timedOut = new WineHostActionResult(
                true,
                false,
                true,
                TryGetExitCode(process),
                sawInvalidReceipt ? "receipt-invalid" : "receipt-missing",
                stopwatch.ElapsedMilliseconds,
                SnapshotOutput(processOutput, outputGate));
            return LogHostAction(operation, timedOut);
        }
        catch (Exception ex)
        {
            var failed = new WineHostActionResult(
                false,
                false,
                false,
                null,
                "wrapper-start-" + ex.GetType().Name,
                stopwatch.ElapsedMilliseconds,
                ex.Message);
            return LogHostAction(operation, failed);
        }
        finally
        {
            TryDeleteFile(receiptPath);
            // Once the nonce receipt is present the host action is detached and complete; the
            // Wine start.exe proxy is no longer part of its lifetime. Terminate a wedged proxy
            // before releasing the handle so repeated retries cannot accumulate wrappers.
            try { if (process != null) TryKill(process); } catch { }
            try { process?.Dispose(); } catch { }
        }
    }

    private static ProcessStartInfo NewHostStartInfo(bool redirectOutput)
        => new("start.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            CreateNoWindow = true,
        };

    private static void ThrowIfNullOrWhiteSpace(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be null or whitespace.", parameterName);
    }

    private static WineHostActionResult LogHostAction(string operation, WineHostActionResult result)
    {
        VoiceDiagnostics.Log("wine.host-action", $"operation={operation} {result.DiagnosticSummary}");
        return result;
    }

    private static bool TryReadReceipt(string path, out string value)
    {
        value = string.Empty;
        try
        {
            if (!File.Exists(path)) return false;
            value = File.ReadAllText(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AppendProcessOutput(
        StringBuilder output,
        object gate,
        string channel,
        string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (gate)
        {
            if (output.Length >= 512) return;
            if (output.Length > 0) output.Append(" | ");
            output.Append(channel).Append(':');
            var remaining = 512 - output.Length;
            output.Append(value, 0, Math.Min(value.Length, Math.Max(0, remaining)));
            if (output.Length > 512) output.Length = 512;
        }
    }

    private static string SnapshotOutput(StringBuilder output, object gate)
    {
        lock (gate) return output.ToString();
    }

    private static int? TryGetExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch { return null; }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(); } catch { }
        try { process.WaitForExit(500); } catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static ProcessStartInfo BuildWinePathStartInfo(string windowsPath)
    {
        var psi = new ProcessStartInfo("winepath")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(windowsPath);
        return psi;
    }

    public static string ResolveHostPath(string windowsPath)
    {
        try
        {
            using var p = Process.Start(BuildWinePathStartInfo(windowsPath));
            if (p != null)
            {
                var outputTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(2000))
                    TryKill(p);
                else if (outputTask.Wait(500))
                {
                    var host = outputTask.Result.Trim();
                    if (!string.IsNullOrEmpty(host))
                        return host;
                }
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
