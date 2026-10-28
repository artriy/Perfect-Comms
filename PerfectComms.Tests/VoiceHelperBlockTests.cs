using System.ComponentModel;
using System.Reflection;
using VoiceChatPlugin;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceHelperBlockClassifierTests
{
    [Theory]
    [InlineData(nameof(VoiceHelperFailureStage.Start), 225)]
    [InlineData(nameof(VoiceHelperFailureStage.Start), 226)]
    [InlineData(nameof(VoiceHelperFailureStage.ProbeStart), 225)]
    [InlineData(nameof(VoiceHelperFailureStage.InspectHelper), 225)]
    [InlineData(nameof(VoiceHelperFailureStage.InspectHelper), 226)]
    [InlineData(nameof(VoiceHelperFailureStage.Extract), 225)]
    [InlineData(nameof(VoiceHelperFailureStage.Start), 5)]
    [InlineData(nameof(VoiceHelperFailureStage.ProbeStart), 5)]
    [InlineData(nameof(VoiceHelperFailureStage.InspectHelper), 5)]
    [InlineData(nameof(VoiceHelperFailureStage.Start), 1260)]
    [InlineData(nameof(VoiceHelperFailureStage.InspectHelper), 1260)]
    [InlineData(nameof(VoiceHelperFailureStage.Start), 4551)]
    [InlineData(nameof(VoiceHelperFailureStage.InspectHelper), 4551)]
    [InlineData(nameof(VoiceHelperFailureStage.ProbeStart), 4556)]
    [InlineData(nameof(VoiceHelperFailureStage.HelperMissing), 0)]
    [InlineData(nameof(VoiceHelperFailureStage.HelperChanged), 0)]
    public void ConfirmedSignalsOnNativeWindows(string stage, int code)
    {
        Assert.Equal(
            VoiceHelperBlockKind.Confirmed,
            VoiceHelperBlockClassifier.Classify(Failure(stage, code), 0, nativeWindows: true));
    }

    [Theory]
    [InlineData("write-temp")]
    [InlineData("publish")]
    public void AccessDeniedWhileWritingTheHelperIsSuspected(string extractionStage)
    {
        var failure = new VoiceHelperLaunchFailure(VoiceHelperFailureStage.Extract, 5, extractionStage);
        Assert.Equal(VoiceHelperBlockKind.Suspected, VoiceHelperBlockClassifier.Classify(failure, 0, true));
    }

    [Theory]
    [InlineData(nameof(VoiceHelperFailureStage.Start), 2, "")]
    [InlineData(nameof(VoiceHelperFailureStage.Start), 0, "")]
    [InlineData(nameof(VoiceHelperFailureStage.InspectHelper), 32, "")]
    [InlineData(nameof(VoiceHelperFailureStage.Extract), 5, "create-directory")]
    [InlineData(nameof(VoiceHelperFailureStage.Extract), 206, "")]
    [InlineData(nameof(VoiceHelperFailureStage.Extract), 0, "")]
    [InlineData(nameof(VoiceHelperFailureStage.ExitedBeforeHandshake), 0, "")]
    [InlineData(nameof(VoiceHelperFailureStage.None), 225, "")]
    public void OrdinaryFailuresAreNotClassified(string stage, int code, string extractionStage)
    {
        var failure = Failure(stage, code) with { ExtractionStage = extractionStage };
        Assert.Equal(VoiceHelperBlockKind.None, VoiceHelperBlockClassifier.Classify(failure, 1, true));
    }

    [Theory]
    [InlineData(nameof(VoiceHelperFailureStage.Start), 225)]
    [InlineData(nameof(VoiceHelperFailureStage.HelperMissing), 0)]
    [InlineData(nameof(VoiceHelperFailureStage.ExitedBeforeHandshake), 0)]
    public void WineAndOtherPlatformsAreNeverClassified(string stage, int code)
    {
        Assert.Equal(
            VoiceHelperBlockKind.None,
            VoiceHelperBlockClassifier.Classify(Failure(stage, code), 5, nativeWindows: false));
    }

    private static VoiceHelperLaunchFailure Failure(string stage, int code)
        => new(Enum.Parse<VoiceHelperFailureStage>(stage), code);

    [Fact]
    public void ErrorCodesComeFromWin32ExceptionsAndWin32HResults()
    {
        Assert.Equal(225, VoiceHelperBlockClassifier.NativeErrorCode(new Win32Exception(225)));
        Assert.Equal(226, VoiceHelperBlockClassifier.NativeErrorCode(
            new InvalidOperationException("wrapped", new IOException("virus", unchecked((int)0x800700E2)))));
        Assert.Equal(5, VoiceHelperBlockClassifier.NativeErrorCode(new UnauthorizedAccessException()));
        Assert.Equal(0, VoiceHelperBlockClassifier.NativeErrorCode(new InvalidOperationException("elevated")));

        Assert.Equal(VoiceHelperBlockKind.Confirmed, VoiceHelperBlockClassifier.Classify(
            VoiceHelperLaunchFailure.FromException(
                VoiceHelperFailureStage.InspectHelper,
                new IOException("virus", unchecked((int)0x800700E1))),
            0,
            true));
    }

    [Fact]
    public void ExtractionFailuresUseTheNativeCacheStage()
    {
        var controlledFolderAccess = new InvalidOperationException(
            "Sidecar extraction failed",
            new NativeCacheExtractionException(
                "write-temp", "helper.exe", "resource", new UnauthorizedAccessException()));
        var failure = VoiceHelperLaunchFailure.FromExtraction(controlledFolderAccess);
        Assert.Equal("write-temp", failure.ExtractionStage);
        Assert.Equal(VoiceHelperBlockKind.Suspected, VoiceHelperBlockClassifier.Classify(failure, 0, true));

        var pathTooLong = new InvalidOperationException(
            "Sidecar extraction failed stage=validate-helper-launch-path",
            new PathTooLongException("The native Windows helper path is too long to launch"));
        Assert.Equal(VoiceHelperBlockKind.None, VoiceHelperBlockClassifier.Classify(
            VoiceHelperLaunchFailure.FromExtraction(pathTooLong), 0, true));

        var elevated = new InvalidOperationException(
            "Sidecar extraction failed stage=select-cache-root",
            new InvalidOperationException("Perfect Comms cannot relocate its native helper while the Among Us process is elevated."));
        Assert.Equal(VoiceHelperBlockKind.None, VoiceHelperBlockClassifier.Classify(
            VoiceHelperLaunchFailure.FromExtraction(elevated), 0, true));
    }

    [Fact]
    public void RepeatedExitsBeforeHandshakeBecomeSuspected()
    {
        var tracker = new VoiceHelperBlockTracker();
        var exited = new VoiceHelperLaunchFailure(VoiceHelperFailureStage.ExitedBeforeHandshake);
        var timedOut = new VoiceHelperLaunchFailure(VoiceHelperFailureStage.HandshakeTimeout);

        Assert.Equal(VoiceHelperBlockKind.None, tracker.Observe(exited, true));
        Assert.Equal(VoiceHelperBlockKind.None, tracker.Observe(timedOut, true));
        Assert.Equal(VoiceHelperBlockKind.Suspected, tracker.Observe(exited, true));

        Assert.Equal(VoiceHelperBlockKind.None, tracker.Observe(default, true));
        Assert.Equal(VoiceHelperBlockKind.None, tracker.Observe(exited, true));
        Assert.Equal(VoiceHelperBlockKind.None, tracker.Observe(exited, true));
        tracker.Reset();
        Assert.Equal(VoiceHelperBlockKind.None, tracker.Observe(exited, true));
        Assert.Equal(VoiceHelperBlockKind.None, tracker.Observe(exited, false));
    }
}

public sealed class VoiceHelperContractBlockTests
{
    [Fact]
    public void MissingHelperIsReportedAsMissing()
    {
        var helper = Path.Combine(Path.GetTempPath(), "pc-block-" + Guid.NewGuid().ToString("N"), "helper.exe");

        Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
            helper, (_, _) => throw new InvalidOperationException("probe must not run"), out var reason, out var block));

        Assert.Contains("helper file is unavailable", reason, StringComparison.Ordinal);
        Assert.Equal(VoiceHelperFailureStage.HelperMissing, block.Stage);
    }

    [Fact]
    public void ProbeThatCannotStartCarriesTheNativeErrorCode()
    {
        var directory = NewTemporaryDirectory();
        var helper = Path.Combine(directory, "helper.exe");
        File.WriteAllText(helper, "helper");
        try
        {
            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
                helper, (_, _) => throw new Win32Exception(225), out _, out var block));

            Assert.Equal(new VoiceHelperLaunchFailure(VoiceHelperFailureStage.ProbeStart, 225), block);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnreadableHelperAfterProbeKeepsItsOwnReadFailure()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Sharing violations are enforced on Windows only.");
        var directory = NewTemporaryDirectory();
        var helper = Path.Combine(directory, "helper.exe");
        File.WriteAllText(helper, "helper");
        FileStream? exclusive = null;
        try
        {
            NativeHelperContractProbeResult Probe(string path, int _)
            {
                exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return new NativeHelperContractProbeResult(
                    true, false, 0, SidecarLauncher.ExpectedNativeHelperBuildInfoJsonForCurrentProcess(), false, string.Empty);
            }

            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(helper, Probe, out _, out var block));

            Assert.Equal(new VoiceHelperLaunchFailure(VoiceHelperFailureStage.InspectHelper, 32), block);
            Assert.Equal(VoiceHelperBlockKind.None, VoiceHelperBlockClassifier.Classify(block, 0, true));
        }
        finally
        {
            exclusive?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HelperChangedDuringProbeIsReportedAsChanged()
    {
        var directory = NewTemporaryDirectory();
        var helper = Path.Combine(directory, "helper.exe");
        File.WriteAllText(helper, "first");
        try
        {
            NativeHelperContractProbeResult Probe(string path, int _)
            {
                File.WriteAllText(path, "replaced by antivirus");
                return new NativeHelperContractProbeResult(
                    true, false, 0, SidecarLauncher.ExpectedNativeHelperBuildInfoJsonForCurrentProcess(), false, string.Empty);
            }

            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(helper, Probe, out _, out var block));
            Assert.Equal(VoiceHelperFailureStage.HelperChanged, block.Stage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HelperChangedAfterExtractionIsRejectedBeforeProbing()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var helper = NativeLibraryCache.Extract(
                Assembly.GetExecutingAssembly(),
                "PerfectComms.Tests.cache-test-payload",
                "PerfectCommsAudio.exe",
                "x86_64-pc-windows-msvc",
                directory);
            File.WriteAllText(helper, "modified after extraction");

            Assert.False(SidecarLauncher.TryValidateNativeHelperContract(
                helper, (_, _) => throw new InvalidOperationException("probe must not run"), out var reason, out var block));

            Assert.Contains("helper changed after extraction", reason, StringComparison.Ordinal);
            Assert.Equal(VoiceHelperFailureStage.HelperChanged, block.Stage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string NewTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pc-block-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

public sealed class HelperRetryPolicyTests
{
    [Fact]
    public void RetryClosesOnlyWhenARefreshStartsOrThereIsNoRoom()
    {
        Assert.Equal(HelperRetryOutcome.RefreshStarted, HelperRetryPolicy.Decide(hasRoom: true, refreshStarted: true));
        Assert.Equal(HelperRetryOutcome.CoolingDown, HelperRetryPolicy.Decide(hasRoom: true, refreshStarted: false));
        Assert.Equal(HelperRetryOutcome.ClearedWithoutRoom, HelperRetryPolicy.Decide(hasRoom: false, refreshStarted: false));
    }
}

public sealed class VoiceHelperBlockStateTests
{
    [Fact]
    public void ReportStartsOneEpisodeAndKeepsTheStrongestEvidence()
    {
        VoiceHelperBlockState.Clear();
        int before = VoiceHelperBlockState.Episode;

        VoiceHelperBlockState.Report(VoiceHelperBlockKind.None, @"C:\ignored");
        Assert.Equal(VoiceHelperBlockKind.None, VoiceHelperBlockState.Kind);
        Assert.Equal(before, VoiceHelperBlockState.Episode);

        VoiceHelperBlockState.Report(VoiceHelperBlockKind.Suspected, @"C:\Games\Among Us\cache\PerfectComms\native");
        Assert.Equal(VoiceHelperBlockKind.Suspected, VoiceHelperBlockState.Kind);
        Assert.Equal(before + 1, VoiceHelperBlockState.Episode);
        Assert.Equal(@"C:\Games\Among Us\cache\PerfectComms\native", VoiceHelperBlockState.CacheRoot);

        VoiceHelperBlockState.Report(VoiceHelperBlockKind.Confirmed, "");
        Assert.Equal(VoiceHelperBlockKind.Confirmed, VoiceHelperBlockState.Kind);
        Assert.Equal(before + 1, VoiceHelperBlockState.Episode);
        Assert.Equal(@"C:\Games\Among Us\cache\PerfectComms\native", VoiceHelperBlockState.CacheRoot);

        VoiceHelperBlockState.Report(VoiceHelperBlockKind.Suspected, "");
        Assert.Equal(VoiceHelperBlockKind.Confirmed, VoiceHelperBlockState.Kind);
        Assert.True(VoiceHelperBlockState.IsBlocked);

        VoiceHelperBlockState.Clear();
        Assert.Equal(VoiceHelperBlockKind.None, VoiceHelperBlockState.Kind);
        Assert.False(VoiceHelperBlockState.IsBlocked);
        Assert.Equal(before + 1, VoiceHelperBlockState.Episode);

        VoiceHelperBlockState.Report(VoiceHelperBlockKind.Suspected, @"D:\Local\PerfectComms\native");
        Assert.Equal(before + 2, VoiceHelperBlockState.Episode);
        Assert.Equal(@"D:\Local\PerfectComms\native", VoiceHelperBlockState.CacheRoot);
        VoiceHelperBlockState.Clear();
    }

    [Fact]
    public async Task ConcurrentReportsStartASingleEpisode()
    {
        VoiceHelperBlockState.Clear();
        int before = VoiceHelperBlockState.Episode;

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            VoiceHelperBlockState.Report(VoiceHelperBlockKind.Confirmed, @"C:\cache\PerfectComms\native"))));

        Assert.Equal(before + 1, VoiceHelperBlockState.Episode);
        VoiceHelperBlockState.Clear();
    }
}

public sealed class VoiceHelperBlockStatusTextTests
{
    private static readonly VoiceConnectionProgress Retrying = new(VoiceConnectionStage.RetryingAudio, 0, 0);

    [Fact]
    public void ConfirmedBlockReplacesTheRetryLineWithTitleAndHint()
    {
        var text = VoiceConnectionStatusPolicy.BuildText(Retrying, 0, VoiceHelperBlockKind.Confirmed, "F10");

        Assert.Equal(
            "<color=#E65860>Antivirus blocked voice chat</color>\n" +
            "<size=85%><color=#A6ACC3>Press <noparse>F10</noparse> to fix</color></size>",
            text);
    }

    [Fact]
    public void SuspectedBlockUsesSofterTitleAndTheConfiguredKey()
    {
        var text = VoiceConnectionStatusPolicy.BuildText(Retrying, 0, VoiceHelperBlockKind.Suspected, "LeftControl+F9");

        Assert.Contains("Antivirus may be blocking voice", text, StringComparison.Ordinal);
        Assert.Contains("Press <noparse>LeftControl+F9</noparse> to fix", text, StringComparison.Ordinal);
        Assert.DoesNotContain("retrying audio", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnboundVoiceSettingsKeyPointsAtVoiceSettings()
    {
        var text = VoiceConnectionStatusPolicy.BuildText(Retrying, 0, VoiceHelperBlockKind.Confirmed, "None");

        Assert.Contains("Open Voice Settings to fix", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutABlockTheNormalStatusTextIsUnchanged()
    {
        Assert.Equal(
            "Voice unavailable - retrying audio...",
            VoiceConnectionStatusPolicy.BuildText(Retrying, 0, VoiceHelperBlockKind.None, "F10"));
        Assert.Equal(
            "Connecting voice... starting audio",
            VoiceConnectionStatusPolicy.BuildText(
                VoiceConnectionProgress.Starting, 0, VoiceHelperBlockKind.Confirmed, "F10"));
    }
}
