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

internal delegate WineHostActionResult WineMacBrokerActionExecutor(
    string operation,
    string hostApplication,
    string requestPath,
    string requestJson,
    string receiptPath,
    string expectedReceipt);

internal delegate int WineUnixProcessExecutor(
    IReadOnlyList<string> arguments,
    bool wait);

// Detects Wine/Proton/CrossOver and provides the host-OS/path/process helpers used to launch and
// clean up the native macOS or Linux audio helper outside the Windows compatibility layer.
internal static class WineEnvironment
{
    // Cold Proton/CrossOver prefixes can take several seconds to dispatch their first host
    // process. Keep the original bounded 15-second allowance while verifying a receipt instead
    // of trusting start.exe's exit status.
    internal const int HostActionTimeoutMs = 15_000;
    private const uint WineUnixCodePage = 65010;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WineUnixSpawnNative(IntPtr arguments, int wait);

    private readonly record struct MacBrokerStartResult(
        bool Started,
        int? ExitCode,
        string FailureKind,
        string Diagnostic);

    private static readonly Lazy<WineUnixSpawnNative?> WineUnixSpawn = new(
        ResolveWineUnixSpawn,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<bool> WineProbe = new(
        DetectWine,
        LazyThreadSafetyMode.ExecutionAndPublication);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = false)]
    private static extern IntPtr GetModuleHandleA(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = false)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int WideCharToMultiByte(
        uint codePage,
        uint flags,
        string source,
        int sourceChars,
        IntPtr destination,
        int destinationBytes,
        IntPtr defaultCharacter,
        IntPtr usedDefaultCharacter);

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

    private static WineUnixSpawnNative? ResolveWineUnixSpawn()
    {
        try
        {
            var ntdll = GetModuleHandleA("ntdll.dll");
            if (ntdll == IntPtr.Zero ||
                GetProcAddress(ntdll, "wine_get_version") == IntPtr.Zero)
                return null;
            var address = GetProcAddress(ntdll, "__wine_unix_spawnvp");
            return address == IntPtr.Zero
                ? null
                : Marshal.GetDelegateForFunctionPointer<WineUnixSpawnNative>(address);
        }
        catch
        {
            return null;
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
            receiptPath,
            expectedReceipt,
            () => BuildWineShellStartInfo(script, hostArguments),
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
            receiptPath,
            expectedReceipt,
            () => BuildWineShellStartInfo(script, hostArguments),
            startProcess,
            timeoutMs);

    internal static WineHostActionResult RunVerifiedMacBrokerAction(
        string operation,
        string hostApplication,
        string requestPath,
        string requestJson,
        string receiptPath,
        string expectedReceipt)
        => RunVerifiedMacBrokerActionCore(
            operation,
            hostApplication,
            requestPath,
            requestJson,
            receiptPath,
            expectedReceipt,
            InvokeWineUnixProcess,
            HostActionTimeoutMs);

    internal static WineHostActionResult RunVerifiedMacBrokerActionForTest(
        string operation,
        string hostApplication,
        string requestPath,
        string requestJson,
        string receiptPath,
        string expectedReceipt,
        WineUnixProcessExecutor runUnixProcess,
        int timeoutMs)
        => RunVerifiedMacBrokerActionCore(
            operation,
            hostApplication,
            requestPath,
            requestJson,
            receiptPath,
            expectedReceipt,
            runUnixProcess,
            timeoutMs);

    private static WineHostActionResult RunVerifiedMacBrokerActionCore(
        string operation,
        string hostApplication,
        string requestPath,
        string requestJson,
        string receiptPath,
        string expectedReceipt,
        WineUnixProcessExecutor runUnixProcess,
        int timeoutMs)
    {
        ThrowIfNullOrWhiteSpace(operation, nameof(operation));
        ThrowIfNullOrWhiteSpace(requestPath, nameof(requestPath));
        ThrowIfNullOrWhiteSpace(requestJson, nameof(requestJson));
        ThrowIfNullOrWhiteSpace(receiptPath, nameof(receiptPath));
        ThrowIfNullOrWhiteSpace(expectedReceipt, nameof(expectedReceipt));
        ArgumentNullException.ThrowIfNull(runUnixProcess);
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));

        var stopwatch = Stopwatch.StartNew();
        var sawInvalidReceipt = false;
        var startResult = default(MacBrokerStartResult);
        try
        {
            TryDeleteFile(receiptPath);
            PublishMacBrokerRequest(requestPath, requestJson);
            startResult = StartMacBrokerApplication(hostApplication, runUnixProcess);
            if (!startResult.Started)
            {
                return LogHostAction(operation, new WineHostActionResult(
                    false,
                    false,
                    false,
                    startResult.ExitCode,
                    startResult.FailureKind,
                    stopwatch.ElapsedMilliseconds,
                    startResult.Diagnostic));
            }

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (TryReadReceipt(receiptPath, out var receipt))
                {
                    if (string.Equals(receipt, expectedReceipt, StringComparison.Ordinal))
                    {
                        return LogHostAction(operation, new WineHostActionResult(
                            true,
                            true,
                            false,
                            startResult.ExitCode,
                            string.Empty,
                            stopwatch.ElapsedMilliseconds,
                            startResult.Diagnostic));
                    }
                    sawInvalidReceipt = true;
                }
                Thread.Sleep(25);
            }

            return LogHostAction(operation, new WineHostActionResult(
                true,
                false,
                true,
                startResult.ExitCode,
                sawInvalidReceipt ? "receipt-invalid" : "receipt-missing",
                stopwatch.ElapsedMilliseconds,
                startResult.Diagnostic));
        }
        catch (Exception ex)
        {
            return LogHostAction(operation, new WineHostActionResult(
                false,
                false,
                false,
                startResult.ExitCode,
                "mac-dispatch-" + ex.GetType().Name,
                stopwatch.ElapsedMilliseconds,
                ex.Message));
        }
        finally
        {
            TryDeleteFile(requestPath);
            TryDeleteFile(receiptPath);
        }
    }

    private static MacBrokerStartResult StartMacBrokerApplication(
        string hostApplication,
        WineUnixProcessExecutor runUnixProcess)
    {
        ThrowIfNullOrWhiteSpace(hostApplication, nameof(hostApplication));
        ArgumentNullException.ThrowIfNull(runUnixProcess);
        var app = hostApplication.TrimEnd('/');
        if (!app.StartsWith("/", StringComparison.Ordinal) ||
            !app.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The macOS broker path must be an absolute application bundle");
        }

        var executable = app + "/Contents/MacOS/PerfectCommsAudio";
        var chmodExit = runUnixProcess(
            new[] { "/bin/chmod", "u+x", executable },
            wait: true);
        if (chmodExit != 0)
        {
            return new MacBrokerStartResult(
                false,
                chmodExit,
                "mac-chmod-failed",
                $"chmodExit={FormatUnixExit(chmodExit)}");
        }

        string xattrDiagnostic;
        try
        {
            var xattrExit = runUnixProcess(
                new[] { "/usr/bin/xattr", "-dr", "com.apple.quarantine", app },
                wait: true);
            xattrDiagnostic = xattrExit == 0
                ? string.Empty
                : $"xattrExit={FormatUnixExit(xattrExit)}";
        }
        catch (Exception ex)
        {
            // A missing quarantine attribute and older xattr implementations are non-fatal.
            // LaunchServices plus the nonce receipt remain authoritative.
            xattrDiagnostic = $"xattrError={ex.GetType().Name}:{ex.Message}";
        }

        var openExit = runUnixProcess(
            new[] { "/usr/bin/open", "-g", "-j", "-n", app },
            wait: false);
        if (openExit != 0)
        {
            return new MacBrokerStartResult(
                false,
                openExit,
                "mac-open-failed",
                string.IsNullOrEmpty(xattrDiagnostic)
                    ? $"openExit={FormatUnixExit(openExit)}"
                    : $"{xattrDiagnostic} openExit={FormatUnixExit(openExit)}");
        }

        return new MacBrokerStartResult(true, openExit, string.Empty, xattrDiagnostic);
    }

    private static int InvokeWineUnixProcess(
        IReadOnlyList<string> arguments,
        bool wait)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0 ||
            string.IsNullOrWhiteSpace(arguments[0]) ||
            !arguments[0].StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A Wine Unix process requires an absolute executable path",
                nameof(arguments));
        }

        var spawn = WineUnixSpawn.Value
            ?? throw new PlatformNotSupportedException(
                "This CrossOver/Wine version does not expose the Unix process bridge");
        var nativeArguments = new IntPtr[arguments.Count];
        var argumentTable = Marshal.AllocHGlobal(checked((arguments.Count + 1) * IntPtr.Size));
        try
        {
            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index]
                    ?? throw new ArgumentException("A Wine Unix process argument cannot be null", nameof(arguments));
                if (argument.IndexOf('\0') >= 0)
                    throw new ArgumentException("A Wine Unix process argument cannot contain NUL", nameof(arguments));
                nativeArguments[index] = AllocateWineUnixString(argument);
                Marshal.WriteIntPtr(argumentTable, index * IntPtr.Size, nativeArguments[index]);
            }
            Marshal.WriteIntPtr(argumentTable, arguments.Count * IntPtr.Size, IntPtr.Zero);
            return spawn(argumentTable, wait ? 1 : 0);
        }
        finally
        {
            foreach (var argument in nativeArguments)
            {
                if (argument != IntPtr.Zero)
                    Marshal.FreeHGlobal(argument);
            }
            Marshal.FreeHGlobal(argumentTable);
        }
    }

    private static IntPtr AllocateWineUnixString(string value)
    {
        var byteCount = WideCharToMultiByte(
            WineUnixCodePage,
            0,
            value,
            -1,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            IntPtr.Zero);
        if (byteCount <= 0)
            throw new InvalidOperationException(
                $"Could not encode a Wine Unix argument (error {Marshal.GetLastWin32Error()})");

        var buffer = Marshal.AllocHGlobal(byteCount);
        var written = WideCharToMultiByte(
            WineUnixCodePage,
            0,
            value,
            -1,
            buffer,
            byteCount,
            IntPtr.Zero,
            IntPtr.Zero);
        if (written != byteCount)
        {
            Marshal.FreeHGlobal(buffer);
            throw new InvalidOperationException(
                $"Could not encode a Wine Unix argument (error {Marshal.GetLastWin32Error()})");
        }
        return buffer;
    }

    private static string FormatUnixExit(int exitCode)
        => exitCode is >= 0 and <= byte.MaxValue
            ? exitCode.ToString()
            : $"0x{unchecked((uint)exitCode):X8}";

    internal static void PublishMacBrokerRequest(string requestPath, string requestJson)
    {
        ThrowIfNullOrWhiteSpace(requestPath, nameof(requestPath));
        ThrowIfNullOrWhiteSpace(requestJson, nameof(requestJson));
        var directory = Path.GetDirectoryName(Path.GetFullPath(requestPath));
        if (string.IsNullOrEmpty(directory))
            throw new InvalidDataException("The macOS broker request has no containing directory");
        Directory.CreateDirectory(directory);

        var temporary = requestPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(
                temporary,
                requestJson,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, requestPath);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static WineHostActionResult RunVerifiedHostActionCore(
        string operation,
        string receiptPath,
        string expectedReceipt,
        Func<ProcessStartInfo> buildStartInfo,
        Func<ProcessStartInfo, Process?> startProcess,
        int timeoutMs)
    {
        ThrowIfNullOrWhiteSpace(operation, nameof(operation));
        ThrowIfNullOrWhiteSpace(receiptPath, nameof(receiptPath));
        ThrowIfNullOrWhiteSpace(expectedReceipt, nameof(expectedReceipt));
        ArgumentNullException.ThrowIfNull(buildStartInfo);
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
            process = startProcess(buildStartInfo());
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
