using System.Diagnostics;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class WinePathResolveTests
{
    [Fact]
    public void BuildsWinePathUnixArguments()
    {
        var psi = WineEnvironment.BuildWinePathStartInfo(@"C:\Users\x\.wine\drive_c\pc-capture.exe");
        Xunit.Assert.Equal("winepath", psi.FileName);
        Xunit.Assert.Equal(
            new[] { "-u", @"C:\Users\x\.wine\drive_c\pc-capture.exe" },
            psi.ArgumentList);
        Xunit.Assert.True(psi.RedirectStandardOutput);
        Xunit.Assert.False(psi.UseShellExecute);
        Xunit.Assert.True(psi.CreateNoWindow);
    }

    [Fact]
    public void HostDispatchOmitsBrokenWineWaitAndSeparatesArguments()
    {
        var path = "/tmp/Perfect Comms/odd ' $() ; & path";
        var psi = WineEnvironment.BuildHostExecStartInfo("/bin/chmod", new[] { "700", path });

        Assert.Equal("start.exe", psi.FileName);
        Assert.Equal(new[] { "/unix", "/bin/chmod", "700", path }, psi.ArgumentList);
        Assert.DoesNotContain("/wait", psi.ArgumentList);
        Assert.Equal(15_000, WineEnvironment.HostActionTimeoutMs);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
    }

    [Fact]
    public void HostShellKeepsScriptConstantAndPathsPositional()
    {
        var oddPath = "/tmp/Perfect Comms/odd ' $() ; & path";
        var psi = WineEnvironment.BuildWineShellStartInfo(
            SidecarLauncher.WinePrivateDirectoryScript,
            new[]
            {
                oddPath,
                oddPath + "/token",
                oddPath + "/receipt",
                "nonce",
                oddPath + "/.launch-owned",
            });

        Assert.Equal("start.exe", psi.FileName);
        Assert.Equal("/unix", psi.ArgumentList[0]);
        Assert.Equal("/bin/sh", psi.ArgumentList[1]);
        Assert.Equal("-c", psi.ArgumentList[2]);
        Assert.Equal(SidecarLauncher.WinePrivateDirectoryScript, psi.ArgumentList[3]);
        Assert.Equal("perfect-comms-bootstrap", psi.ArgumentList[4]);
        Assert.Equal(oddPath, psi.ArgumentList[5]);
        Assert.DoesNotContain(oddPath, psi.ArgumentList[3], StringComparison.Ordinal);
        Assert.DoesNotContain("/wait", psi.ArgumentList);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
    }

    [Fact]
    public void HostShellNormalizesWindowsLineEndingsBeforePosixDispatch()
    {
        var psi = WineEnvironment.BuildWineShellStartInfo(
            "first\r\nsecond\rthird",
            Array.Empty<string>());

        Assert.Equal("first\nsecond\nthird", psi.ArgumentList[3]);
        Assert.DoesNotContain('\r', psi.ArgumentList[3]);
    }

    [Fact]
    public void VerifiedHostActionAcceptsReceiptAfterNonzeroWrapperExit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "perfect-comms-host-action-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var receipt = Path.Combine(directory, "receipt");
        const string expected = "perfect-comms-host-action-v1:test-nonce";
        ProcessStartInfo? requestedStart = null;
        using var wrapperExited = new ManualResetEventSlim(false);
        var writer = new Thread(() =>
        {
            if (!wrapperExited.Wait(2000)) return;
            File.WriteAllText(receipt, expected);
        }) { IsBackground = true };
        try
        {
            writer.Start();
            var result = WineEnvironment.RunVerifiedHostActionForTest(
                "test-nonzero-wrapper",
                "printf ignored",
                Array.Empty<string>(),
                receipt,
                expected,
                startInfo =>
                {
                    requestedStart = startInfo;
                    var wrapper = StartWrapper(exitCode: 1, output: string.Empty);
                    wrapperExited.Set();
                    return wrapper;
                },
                timeoutMs: 2000);

            Assert.True(result.Succeeded, result.DiagnosticSummary);
            Assert.True(result.ReceiptVerified);
            Assert.Equal(1, result.WrapperExitCode);
            Assert.NotNull(requestedStart);
            Assert.DoesNotContain("/wait", requestedStart!.ArgumentList);
        }
        finally
        {
            writer.Join(2000);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void VerifiedHostActionRejectsWrongReceiptAndCapturesWrapperStdout()
    {
        var directory = Path.Combine(Path.GetTempPath(), "perfect-comms-host-action-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var receipt = Path.Combine(directory, "receipt");
        const string expected = "perfect-comms-host-action-v1:expected";
        using var wrapperStarted = new ManualResetEventSlim(false);
        var writer = new Thread(() =>
        {
            if (!wrapperStarted.Wait(2000)) return;
            File.WriteAllText(receipt, "perfect-comms-host-action-v1:wrong");
        }) { IsBackground = true };
        try
        {
            writer.Start();
            var result = WineEnvironment.RunVerifiedHostActionForTest(
                "test-invalid-receipt",
                "printf ignored",
                Array.Empty<string>(),
                receipt,
                expected,
                _ =>
                {
                    var wrapper = StartWrapper(exitCode: 0, output: "fatal-wrapper-output");
                    wrapperStarted.Set();
                    return wrapper;
                },
                timeoutMs: 1000);

            Assert.False(result.Succeeded);
            Assert.True(result.TimedOut);
            Assert.Equal("receipt-invalid", result.FailureKind);
            Assert.Contains("stdout:fatal-wrapper-output", result.ProcessOutput, StringComparison.Ordinal);
            Assert.Contains("fatal-wrapper-output", result.DiagnosticSummary, StringComparison.Ordinal);
        }
        finally
        {
            writer.Join(2000);
            Directory.Delete(directory, true);
        }
    }

    private static Process StartWrapper(int exitCode, string output)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("cmd.exe");
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add($"echo {output} & exit /b {exitCode}");
        }
        else
        {
            psi = new ProcessStartInfo("/bin/sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"printf '%s\\n' '{output}'; exit {exitCode}");
        }
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        var process = Process.Start(psi) ?? throw new InvalidOperationException("test wrapper did not start");
        Assert.True(process.WaitForExit(2000));
        return process;
    }
}
