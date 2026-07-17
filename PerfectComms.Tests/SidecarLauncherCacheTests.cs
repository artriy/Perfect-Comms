using VoiceChatPlugin;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SidecarLauncherCacheTests
{
    private const string Triple = "x86_64-pc-windows-msvc";
    private const string CacheFixtureResource = "PerfectComms.Tests.cache-test-payload";

    [Fact]
    public void NativeLaunchArgumentsIncludeManagedOwnerPid()
    {
        var arguments = SidecarLauncher.BuildArguments(@"C:\Temp Folder\handshake.json", 4242, wine: false);

        Assert.Equal("--handshake \"C:\\Temp Folder\\handshake.json\" --owner-pid 4242", arguments);
    }

    [Fact]
    public void WineLaunchArgumentsOmitManagedOwnerPid()
    {
        var arguments = SidecarLauncher.BuildArguments("/tmp/handshake.json", 4242, wine: true);

        Assert.Equal("--handshake \"/tmp/handshake.json\"", arguments);
        Assert.DoesNotContain("owner-pid", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedNativePlatformsAndArchitecturesFailClosed()
    {
        Assert.Throws<PlatformNotSupportedException>(() => SidecarLauncher.TargetTripleFor(
            wine: false,
            WineHostOs.Unknown,
            windows: true,
            macOs: false,
            linux: false,
            System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Throws<PlatformNotSupportedException>(() => SidecarLauncher.TargetTripleFor(
            wine: false,
            WineHostOs.Unknown,
            windows: false,
            macOs: false,
            linux: false,
            System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal("x86_64-unknown-linux-gnu", SidecarLauncher.TargetTripleFor(
            wine: false,
            WineHostOs.Unknown,
            windows: false,
            macOs: false,
            linux: true,
            System.Runtime.InteropServices.Architecture.X64));
    }

    [Fact]
    public void WineBootstrapLocksPrivateDirectoryBeforeCreatingToken()
    {
        var root = NewTemporaryDirectory();
        var permissionCalls = 0;
        try
        {
            bool SetPermissions(string program, string arguments)
            {
                Assert.Equal("/bin/chmod", program);
                permissionCalls++;
                var directory = Assert.Single(Directory.EnumerateDirectories(root));
                if (permissionCalls == 1)
                {
                    Assert.StartsWith("700 ", arguments, StringComparison.Ordinal);
                    Assert.Empty(Directory.EnumerateFiles(directory));
                }
                else
                {
                    Assert.StartsWith("600 ", arguments, StringComparison.Ordinal);
                    Assert.True(File.Exists(Path.Combine(directory, "token")));
                }
                return true;
            }

            var paths = SidecarLauncher.CreateTemporaryPaths(
                wine: true,
                static path => path,
                SetPermissions,
                root);
            Assert.NotNull(paths.PrivateDirectory);
            Assert.Equal(Path.Combine(paths.PrivateDirectory!, "handshake.json"), paths.HandshakePath);

            var tokenFile = SidecarLauncher.CreateWineTokenFile(
                paths,
                "test-secret",
                static path => path,
                SetPermissions);
            Assert.Equal("test-secret", File.ReadAllText(tokenFile));
            Assert.Throws<IOException>(() => SidecarLauncher.CreateWineTokenFile(
                paths,
                "replacement",
                static path => path,
                SetPermissions));
            Assert.Equal(2, permissionCalls);

            SidecarLauncher.CleanupTemporaryPaths(
                paths.HandshakePath,
                paths.PrivateDirectory,
                paths.PrivateRoot);
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void WineCleanupRejectsPrefixNamedDirectoryOutsideOwningRoot()
    {
        var parent = NewTemporaryDirectory();
        var owningRoot = Path.Combine(parent, "owning-root");
        var outside = Path.Combine(parent, "perfect-comms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owningRoot);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "handshake.json");
        File.WriteAllText(sentinel, "keep");
        try
        {
            Assert.Throws<InvalidOperationException>(() => SidecarLauncher.CleanupTemporaryPaths(
                sentinel,
                outside,
                owningRoot));
            Assert.True(File.Exists(sentinel));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void WineBootstrapRemovesDirectoryWhenPermissionSetupFails()
    {
        var root = NewTemporaryDirectory();
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => SidecarLauncher.CreateTemporaryPaths(
                wine: true,
                static path => path,
                static (_, _) => false,
                root));
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DeviceEnumerationFileRequiresStructuredInputAndOutputLists()
    {
        var directory = NewTemporaryDirectory();
        var path = Path.Combine(directory, "devices.json");
        try
        {
            File.WriteAllText(path,
                "{\"op\":\"devices\",\"devices\":[{\"id\":\"mic-1\",\"name\":\"Mic\"}]," +
                "\"outputDevices\":[{\"id\":\"spk-1\",\"name\":\"Speaker\"}]}");
            Assert.True(SidecarLauncher.TryReadDevicesFile(path, out var input, out var output));
            Assert.Equal("mic-1", Assert.Single(input).Id);
            Assert.Equal("spk-1", Assert.Single(output).Id);

            File.WriteAllText(path,
                "{\"op\":\"devices\",\"devices\":[],\"outputDevices\":[]}");
            Assert.True(SidecarLauncher.TryReadDevicesFile(path, out input, out output));
            Assert.Empty(input);
            Assert.Empty(output);

            File.WriteAllText(path,
                "{\"op\":\"devices\",\"devices\":[{\"id\":\"mic-1\",\"name\":\"Mic\"}]," +
                "\"outputDevices\":[{\"name\":\"Missing stable ID\"}]}");
            Assert.False(SidecarLauncher.TryReadDevicesFile(path, out _, out _));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

#if WINDOWS
    [Fact]
    public void FailedEnumerationIsDistinctFromAuthoritativeEmptyEnumeration()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var failure = SidecarLauncher.EnumerateDevices(
                Path.Combine(directory, "missing-helper.exe"),
                wine: false,
                static path => path);
            var empty = SidecarDeviceEnumerationResult.Success(
                Array.Empty<VoiceDeviceInfo>(), Array.Empty<VoiceDeviceInfo>());

            Assert.False(failure.IsAuthoritative);
            Assert.True(empty.IsAuthoritative);
            Assert.Empty(empty.Input);
            Assert.Empty(empty.Output);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
#endif

    [Fact]
    public void HelperAndDspMapToFixedNamesInsideSameVersionedBundle()
    {
        var assembly = typeof(SidecarLauncherCacheTests).Assembly;
        var version = NativeLibraryCache.BuildContentVersion(assembly, new[] { CacheFixtureResource });
        var baseDirectory = Path.Combine(Path.GetTempPath(), "Perfect Comms cache mapping");
        var expectedDirectory = Path.Combine(
            baseDirectory,
            "cache",
            "PerfectComms",
            "native",
            Triple,
            version);

        var helper = NativeLibraryCache.ResolveExtractionPath(
            baseDirectory,
            Triple,
            "PerfectCommsAudio.exe",
            version);
        var dsp = NativeLibraryCache.ResolveExtractionPath(
            baseDirectory,
            Triple,
            "webrtc-apm.x64.dll",
            version);

        Assert.StartsWith("bundle-v1-", version, StringComparison.Ordinal);
        Assert.Equal(expectedDirectory, NativeLibraryCache.BundleDirectory(baseDirectory, Triple, version));
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(helper));
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(dsp));
        Assert.Equal("PerfectCommsAudio.exe", Path.GetFileName(helper));
        Assert.Equal("webrtc-apm.x64.dll", Path.GetFileName(dsp));
    }

    [Fact]
    public void BundleVersionIsStableRegardlessOfResourceOrder()
    {
        var assembly = typeof(SidecarLauncherCacheTests).Assembly;

        var first = NativeLibraryCache.BuildContentVersion(
            assembly,
            new[] { CacheFixtureResource, "missing.optional.resource" });
        var second = NativeLibraryCache.BuildContentVersion(
            assembly,
            new[] { "missing.optional.resource", CacheFixtureResource, CacheFixtureResource });

        Assert.Equal(first, second);
    }

    #if WINDOWS
    [Fact]
    public void LockedLegacyTargetDoesNotBlockVersionedExtraction()
    {
        var assembly = typeof(SidecarLauncherCacheTests).Assembly;
        var baseDirectory = NewTemporaryDirectory();
        var legacyTarget = NativeLibraryCache.ResolveExtractionPath(
            baseDirectory,
            Triple,
            "PerfectCommsAudio.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyTarget)!);
        File.WriteAllText(legacyTarget, "old locked helper");

        try
        {
            using (var legacyLock = new FileStream(
                       legacyTarget,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.Read))
            {
                var version = NativeLibraryCache.BuildContentVersion(assembly, new[] { CacheFixtureResource });
                var extracted = NativeLibraryCache.Extract(
                    assembly,
                    CacheFixtureResource,
                    "PerfectCommsAudio.exe",
                    Triple,
                    baseDirectory,
                    version);

                Assert.NotEqual(legacyTarget, extracted);
                Assert.Equal(
                    NativeLibraryCache.BundleDirectory(baseDirectory, Triple, version),
                    Path.GetDirectoryName(extracted));
                Assert.True(File.Exists(extracted));
            }
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }
    #endif

    [Fact]
    public void ExtractionFailureReportsExactStageAndTargetPath()
    {
        var assembly = typeof(SidecarLauncher).Assembly;
        var baseDirectory = NewTemporaryDirectory();
        const string version = "bundle-v1-missing-resource-test";
        var expectedTarget = NativeLibraryCache.ResolveExtractionPath(
            baseDirectory,
            Triple,
            "PerfectCommsAudio.exe",
            version);

        try
        {
            var error = Assert.Throws<NativeCacheExtractionException>(() =>
                NativeLibraryCache.Extract(
                    assembly,
                    "missing.helper.resource",
                    "PerfectCommsAudio.exe",
                    Triple,
                    baseDirectory,
                    version));

            Assert.Equal("open-resource", error.Stage);
            Assert.Equal(expectedTarget, error.TargetPath);
            Assert.Contains("stage=open-resource", error.Message, StringComparison.Ordinal);
            Assert.Contains(NativeLibraryCache.DiagnosticValue(expectedTarget), error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }

    [Fact]
    public void StalePruningNeverDeletesCurrentBundle()
    {
        var baseDirectory = NewTemporaryDirectory();
        const string currentVersion = "bundle-v1-current-test";
        const string staleVersion = "bundle-v1-stale-test";
        var current = NativeLibraryCache.BundleDirectory(baseDirectory, Triple, currentVersion);
        var stale = NativeLibraryCache.BundleDirectory(baseDirectory, Triple, staleVersion);
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "PerfectCommsAudio.exe"), "stale");

        try
        {
            var deleted = NativeLibraryCache.PruneStaleBundles(baseDirectory, Triple, currentVersion);

            Assert.True(Directory.Exists(current));
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(1, deleted);
                Assert.False(Directory.Exists(stale));
            }
            else
            {
                Assert.Equal(0, deleted);
                Assert.True(Directory.Exists(stale));
            }
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }

    [Fact]
    public void StalePruningSkipsBundleWithActiveLease()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var baseDirectory = NewTemporaryDirectory();
        const string currentVersion = "bundle-v1-current-lease-test";
        const string activeVersion = "bundle-v1-active-lease-test";
        var current = NativeLibraryCache.BundleDirectory(baseDirectory, Triple, currentVersion);
        var active = NativeLibraryCache.BundleDirectory(baseDirectory, Triple, activeVersion);
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(active);
        var leasePath = Path.Combine(active, ".in-use");

        try
        {
            using (var activeLease = new FileStream(
                       leasePath,
                       FileMode.OpenOrCreate,
                       FileAccess.ReadWrite,
                       FileShare.ReadWrite))
            {
                var deleted = NativeLibraryCache.PruneStaleBundles(baseDirectory, Triple, currentVersion);

                Assert.Equal(0, deleted);
                Assert.True(Directory.Exists(active));
            }
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }

    [Fact]
    public void MacContentAddressedBundleRepairsCorruptionWithoutTimestampHeuristics()
    {
        var baseDirectory = NewTemporaryDirectory();
        const string triple = "aarch64-apple-darwin";
        const string version = "bundle-v1-mac-cache-test";
        var zip = Path.Combine(baseDirectory, "helper.zip");
        CreateMacHelperZip(zip);

        try
        {
            var inner = SidecarLauncher.ExtractMacApp(zip, triple, baseDirectory, version);
            File.WriteAllText(inner, "cached-sentinel");
            File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddMinutes(5));

            var reused = SidecarLauncher.ExtractMacApp(zip, triple, baseDirectory, version);

            Assert.Equal(inner, reused);
            Assert.Equal("helper-bytes", File.ReadAllText(reused));
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }

    [Fact]
    public async Task ConcurrentMacExtractionPublishesOneCompleteApp()
    {
        var baseDirectory = NewTemporaryDirectory();
        const string triple = "x86_64-apple-darwin";
        const string version = "bundle-v1-mac-concurrency-test";
        var zip = Path.Combine(baseDirectory, "helper.zip");
        CreateMacHelperZip(zip);

        try
        {
            var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
                () => SidecarLauncher.ExtractMacApp(zip, triple, baseDirectory, version))));

            Assert.Single(paths.Distinct(StringComparer.Ordinal));
            Assert.All(paths, path => Assert.Equal("helper-bytes", File.ReadAllText(path)));
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }

    private static void CreateMacHelperZip(string path)
    {
        using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        var entry = archive.CreateEntry("PerfectCommsAudio.app/Contents/MacOS/PerfectCommsAudio");
        entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var writer = new StreamWriter(entry.Open());
        writer.Write("helper-bytes");
    }

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "perfect-comms-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
