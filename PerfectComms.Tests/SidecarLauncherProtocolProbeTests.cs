using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SidecarLauncherProtocolProbeTests
{
    [Fact]
    public void SuccessfulProbeIsCachedUntilHelperMetadataChanges()
    {
        var directory = NewTemporaryDirectory();
        var helper = Path.Combine(directory, "helper.bin");
        File.WriteAllText(helper, "first");
        var calls = 0;
        try
        {
            NativeHelperProtocolProbeResult Probe(string path, int timeoutMs)
            {
                calls++;
                Assert.Equal(Path.GetFullPath(helper), path);
                Assert.Equal(SidecarLauncher.NativeHelperProtocolProbeTimeoutMs, timeoutMs);
                return Success(SidecarVoiceClient.Proto + Environment.NewLine);
            }

            Assert.True(SidecarLauncher.TryValidateNativeHelperProtocol(
                helper, Probe, out var firstFailure), firstFailure);
            Assert.True(SidecarLauncher.TryValidateNativeHelperProtocol(
                helper, Probe, out var cachedFailure), cachedFailure);
            Assert.Equal(1, calls);

            File.AppendAllText(helper, "-changed");
            Assert.True(SidecarLauncher.TryValidateNativeHelperProtocol(
                helper, Probe, out var changedFailure), changedFailure);
            Assert.Equal(2, calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MismatchedProtocolFailsExplicitlyAndIsNotCached()
    {
        var directory = NewTemporaryDirectory();
        var helper = Path.Combine(directory, "helper.bin");
        File.WriteAllText(helper, "stale");
        var calls = 0;
        try
        {
            NativeHelperProtocolProbeResult Probe(string _, int __)
            {
                calls++;
                return Success((SidecarVoiceClient.Proto - 1).ToString());
            }

            Assert.False(SidecarLauncher.TryValidateNativeHelperProtocol(
                helper, Probe, out var firstFailure));
            Assert.Contains(
                $"native helper protocol mismatch: expected {SidecarVoiceClient.Proto}, got {SidecarVoiceClient.Proto - 1}",
                firstFailure,
                StringComparison.Ordinal);
            Assert.False(SidecarLauncher.TryValidateNativeHelperProtocol(
                helper, Probe, out var secondFailure));
            Assert.Equal(firstFailure, secondFailure);
            Assert.Equal(2, calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProbeTimeoutAndOversizedOutputFailClosed()
    {
        var directory = NewTemporaryDirectory();
        var helper = Path.Combine(directory, "helper.bin");
        File.WriteAllText(helper, "invalid");
        try
        {
            var timedOut = new NativeHelperProtocolProbeResult(
                true, true, -1, string.Empty, false, "ignored");
            Assert.False(SidecarLauncher.TryValidateNativeHelperProtocol(
                helper, (_, _) => timedOut, out var timeoutFailure));
            Assert.Contains("timed out after 2000 ms", timeoutFailure, StringComparison.Ordinal);

            var oversized = new NativeHelperProtocolProbeResult(
                true, false, 0, SidecarVoiceClient.Proto.ToString(), true, string.Empty);
            Assert.False(SidecarLauncher.TryValidateNativeHelperProtocol(
                helper, (_, _) => oversized, out var oversizedFailure));
            Assert.Contains(
                "--protocol-version output exceeded",
                oversizedFailure,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProbeStartDiagnosticIsBoundedAndSanitized()
    {
        var directory = NewTemporaryDirectory();
        var helper = Path.Combine(directory, "helper.bin");
        File.WriteAllText(helper, "invalid");
        try
        {
            var failed = new NativeHelperProtocolProbeResult(
                false,
                false,
                -1,
                string.Empty,
                false,
                "launch failed\ncredential=do-not-log");

            Assert.False(SidecarLauncher.TryValidateNativeHelperProtocol(
                helper, (_, _) => failed, out var failure));
            Assert.Contains("failed to start", failure, StringComparison.Ordinal);
            Assert.DoesNotContain("do-not-log", failure, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', failure);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static NativeHelperProtocolProbeResult Success(string output)
        => new(true, false, 0, output, false, string.Empty);

    private static string NewTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PerfectComms-protocol-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
