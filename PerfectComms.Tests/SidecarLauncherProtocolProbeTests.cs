using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SidecarLauncherContractProbeTests
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
            NativeHelperContractProbeResult Probe(string path, int timeoutMs)
            {
                calls++;
                Assert.Equal(Path.GetFullPath(helper), path);
                Assert.Equal(SidecarLauncher.NativeHelperContractProbeTimeoutMs, timeoutMs);
                return Success(
                    SidecarLauncher.ExpectedNativeHelperBuildInfoJsonForCurrentProcess() +
                    Environment.NewLine);
            }

            Assert.True(SidecarLauncher.TryValidateNativeHelperContract(
                helper, Probe, out var firstFailure), firstFailure);
            Assert.True(SidecarLauncher.TryValidateNativeHelperContract(
                helper, Probe, out var cachedFailure), cachedFailure);
            Assert.Equal(1, calls);

            File.AppendAllText(helper, "-changed");
            Assert.True(SidecarLauncher.TryValidateNativeHelperContract(
                helper, Probe, out var changedFailure), changedFailure);
            Assert.Equal(2, calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CachedContractIsInvalidatedWhenContentChangesWithSameLengthAndTimestamp()
    {
        var directory = NewTemporaryDirectory();
        var helper = Path.Combine(directory, "helper.bin");
        File.WriteAllText(helper, "first");
        var originalWriteTime = File.GetLastWriteTimeUtc(helper);
        var calls = 0;
        try
        {
            NativeHelperContractProbeResult Probe(string _, int __)
            {
                calls++;
                return Success(SidecarLauncher.ExpectedNativeHelperBuildInfoJsonForCurrentProcess());
            }

            Assert.True(SidecarLauncher.TryValidateNativeHelperContract(
                helper, Probe, out var firstFailure), firstFailure);
            File.WriteAllText(helper, "other");
            File.SetLastWriteTimeUtc(helper, originalWriteTime);
            Assert.True(SidecarLauncher.TryValidateNativeHelperContract(
                helper, Probe, out var changedFailure), changedFailure);
            Assert.Equal(2, calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HelperChangedDuringProbeIsRejectedAndMustBeProbedAgain()
    {
        var directory = NewTemporaryDirectory();
        var helper = Path.Combine(directory, "helper.bin");
        File.WriteAllText(helper, "first");
        var calls = 0;
        try
        {
            NativeHelperContractProbeResult Probe(string path, int _)
            {
                calls++;
                if (calls == 1)
                    File.WriteAllText(path, "other");
                return Success(SidecarLauncher.ExpectedNativeHelperBuildInfoJsonForCurrentProcess());
            }

            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
                helper, Probe, out var changedFailure));
            Assert.Contains("helper changed while it was being validated", changedFailure,
                StringComparison.Ordinal);

            Assert.True(SidecarLauncher.TryValidateNativeHelperContract(
                helper, Probe, out var retryFailure), retryFailure);
            Assert.Equal(2, calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StaleProtocolOrNonCubebEngineFailsExplicitlyAndIsNotCached()
    {
        var directory = NewTemporaryDirectory();
        var helper = Path.Combine(directory, "helper.bin");
        File.WriteAllText(helper, "stale");
        var calls = 0;
        try
        {
            var expected = SidecarLauncher.ExpectedNativeHelperBuildInfoJsonForCurrentProcess();
            NativeHelperContractProbeResult Probe(string _, int __)
            {
                calls++;
                return Success(expected.Replace(
                    $"\"protocol\":{SidecarVoiceClient.Proto}",
                    $"\"protocol\":{SidecarVoiceClient.Proto - 1}",
                    StringComparison.Ordinal));
            }

            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
                helper, Probe, out var firstFailure));
            Assert.Contains(
                $"protocol is not {SidecarVoiceClient.Proto}",
                firstFailure,
                StringComparison.Ordinal);
            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
                helper, Probe, out var secondFailure));
            Assert.Equal(firstFailure, secondFailure);
            Assert.Equal(2, calls);

            var staleCpal = Success(expected.Replace(
                "\"audio_engine\":\"cubeb\"",
                "\"audio_engine\":\"cpal\"",
                StringComparison.Ordinal));
            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
                helper, (_, _) => staleCpal, out var engineFailure));
            Assert.Contains("audio engine is not cubeb", engineFailure, StringComparison.Ordinal);

            var wrongBackends = Success(expected.Replace(
                expected[expected.IndexOf("\"compiled_backends\":", StringComparison.Ordinal)..
                    expected.IndexOf(",\"contract\":", StringComparison.Ordinal)],
                "\"compiled_backends\":[\"wrong-backend\"]",
                StringComparison.Ordinal));
            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
                helper, (_, _) => wrongBackends, out var backendFailure));
            Assert.Contains(
                "compiled backend inventory does not match this platform",
                backendFailure,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CubebContractRequiresVersionMarkerAndUnambiguousBackendInventory()
    {
        var directory = NewTemporaryDirectory();
        var helper = Path.Combine(directory, "helper.bin");
        File.WriteAllText(helper, "contract");
        try
        {
            var expected = SidecarLauncher.ExpectedNativeHelperBuildInfoJsonForCurrentProcess();

            var wrongVersion = Success(expected.Replace(
                "\"cubeb_version\":\"0.36.0\"",
                "\"cubeb_version\":\"0.35.0\"",
                StringComparison.Ordinal));
            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
                helper, (_, _) => wrongVersion, out var versionFailure));
            Assert.Contains("Cubeb version is not 0.36.0", versionFailure,
                StringComparison.Ordinal);

            var wrongMarker = Success(expected.Replace(
                "ENGINE=CUBEB",
                "ENGINE=CPAL",
                StringComparison.Ordinal));
            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
                helper, (_, _) => wrongMarker, out var markerFailure));
            Assert.Contains("audio contract marker is invalid", markerFailure,
                StringComparison.Ordinal);

            var backendStart = expected.IndexOf("\"compiled_backends\":", StringComparison.Ordinal);
            var backendEnd = expected.IndexOf(",\"contract\":", StringComparison.Ordinal);
            var backendField = expected[backendStart..backendEnd];
            var firstBackendStart = backendField.IndexOf("[\"", StringComparison.Ordinal) + 2;
            var firstBackendEnd = backendField.IndexOf('"', firstBackendStart);
            var firstBackend = backendField[firstBackendStart..firstBackendEnd];
            var duplicateBackends = Success(expected.Replace(
                backendField,
                $"\"compiled_backends\":[\"{firstBackend}\",\"{firstBackend}\"]",
                StringComparison.Ordinal));
            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
                helper, (_, _) => duplicateBackends, out var duplicateFailure));
            Assert.Contains("invalid or duplicate entry", duplicateFailure,
                StringComparison.Ordinal);

            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
                helper, (_, _) => Success("{not-json"), out var malformedFailure));
            Assert.Contains("Cubeb contract mismatch", malformedFailure,
                StringComparison.Ordinal);
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
            var timedOut = new NativeHelperContractProbeResult(
                true, true, -1, string.Empty, false, "ignored");
            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
                helper, (_, _) => timedOut, out var timeoutFailure));
            Assert.Contains("timed out after 2000 ms", timeoutFailure, StringComparison.Ordinal);

            var oversized = new NativeHelperContractProbeResult(
                true, false, 0,
                SidecarLauncher.ExpectedNativeHelperBuildInfoJsonForCurrentProcess(),
                true, string.Empty);
            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
                helper, (_, _) => oversized, out var oversizedFailure));
            Assert.Contains(
                "--build-info output exceeded",
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
            var failed = new NativeHelperContractProbeResult(
                false,
                false,
                -1,
                string.Empty,
                false,
                "launch failed\ncredential=do-not-log");

            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
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

    private static NativeHelperContractProbeResult Success(string output)
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
