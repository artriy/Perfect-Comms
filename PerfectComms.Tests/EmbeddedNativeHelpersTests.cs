using System.Security.Cryptography;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VoiceChatPlugin;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class EmbeddedNativeHelpersTests
{
    [Fact]
    public void EmbeddedDesktopHelpersMatchStagedFiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var helpers = new[]
        {
            new HelperResource(
                "Lib.pc-capture.pc-capture-win-x64.exe",
                Path.Combine(repositoryRoot, "Libs", "pc-capture", "pc-capture-win-x64.exe")),
            new HelperResource(
                "Lib.pc-capture.pc-capture-win-x86.exe",
                Path.Combine(repositoryRoot, "Libs", "pc-capture", "pc-capture-win-x86.exe")),
            new HelperResource(
                "Lib.pc-capture.pc-capture-linux-x64",
                Path.Combine(repositoryRoot, "Libs", "pc-capture", "pc-capture-linux-x64")),
            new HelperResource(
                "Lib.pc-capture.pc-capture-mac.zip",
                Path.Combine(repositoryRoot, "Libs", "pc-capture", "pc-capture-mac.zip")),
        };

        var pluginAssembly = typeof(VoiceLobbyRegistryPublisher).Assembly;
        var embeddedResources = pluginAssembly
            .GetManifestResourceNames()
            .ToHashSet(StringComparer.Ordinal);

        // Keep the test matrix coupled to every target accepted by SidecarLauncher. Both macOS
        // architectures intentionally share the signed universal app archive.
        foreach (string triple in new[]
                 {
                     "x86_64-pc-windows-msvc",
                     "i686-pc-windows-msvc",
                     "x86_64-unknown-linux-gnu",
                     "x86_64-apple-darwin",
                     "aarch64-apple-darwin",
                 })
        {
            string resourceName = SidecarLauncher.ResourceName(triple);
            Assert.Contains(helpers, helper => helper.ResourceName == resourceName);
        }

        // Ordinary managed-only builds intentionally allow Linux/macOS release assets to be
        // absent. Presence must nevertheless agree in both directions: a newly staged helper must
        // be embedded, and a removed helper must not survive in an incremental plugin assembly.
        foreach (var helper in helpers)
            AssertEmbeddedFileParity(pluginAssembly, embeddedResources, helper);

        // Windows release staging is a paired x64/x86 operation. Preserve the stronger existing
        // guard against accidentally testing or packaging a half-updated pair.
        var windowsHelpers = helpers.Take(2).ToArray();
        if (windowsHelpers.Any(helper =>
                File.Exists(helper.StagedPath) || embeddedResources.Contains(helper.ResourceName)))
        {
            Assert.All(windowsHelpers, helper =>
            {
                Assert.True(File.Exists(helper.StagedPath),
                    $"Staged Windows helper pair is incomplete: {helper.StagedPath}");
                Assert.Contains(helper.ResourceName, embeddedResources);
            });
        }
    }

    [Theory]
    [InlineData(false, "install")]
    [InlineData(true, "local-app-data")]
    public async Task EmbeddedWindowsHelperStartsFromSelectedCacheLayout(
        bool deepInstall,
        string expectedRootKind)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.X86))
            return;

        var triple = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? "x86_64-pc-windows-msvc"
            : "i686-pc-windows-msvc";
        var pluginAssembly = typeof(VoiceLobbyRegistryPublisher).Assembly;
        var helperResource = SidecarLauncher.ResourceName(triple);
        var resources = new[] { helperResource }
            .Concat(SidecarLauncher.DspLibsFor(triple).Select(library => library.Resource))
            .Concat(SidecarLauncher.PionLibsFor(triple).Select(library => library.Resource))
            .ToArray();
        foreach (var resource in resources)
        {
            using var embedded = pluginAssembly.GetManifestResourceStream(resource);
            if (embedded == null) return;
        }

        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var workspace = Path.Combine(
            localApplicationData,
            $"pcs-{Guid.NewGuid():N}"[..12]);
        var baseDirectory = deepInstall
            ? Path.Combine(workspace, new string('i', 120))
            : Path.Combine(workspace, "game");
        var localCacheBase = Path.Combine(workspace, "local");
        var version = NativeLibraryCache.BuildContentVersion(pluginAssembly, resources);

        try
        {
            var selection = SidecarLauncher.ResolveNativeCacheRoot(
                baseDirectory,
                localCacheBase,
                triple,
                version,
                windows: true,
                wine: false,
                static () => false);
            Assert.Equal(expectedRootKind, selection.RootKind);

            var helperPath = NativeLibraryCache.Extract(
                pluginAssembly,
                helperResource,
                SidecarLauncher.HelperFileName(triple),
                triple,
                selection.CacheRoot,
                version);
            SidecarLauncher.EnsureDspLibsExtracted(
                pluginAssembly,
                selection.CacheRoot,
                triple,
                helperPath,
                version);
            SidecarLauncher.EnsurePionLibExtracted(
                pluginAssembly,
                selection.CacheRoot,
                triple,
                helperPath,
                version);
            helperPath = SidecarLauncher.ValidateNativeHelperLaunchPath(
                helperPath,
                windows: true,
                wine: false);

            Assert.True(
                helperPath.Length < SidecarLauncher.NativeWindowsLaunchPathBudget);
            Assert.Equal(
                SidecarLauncher.ExpectedNativeHelperBuildInfoJsonForCurrentProcess(),
                await RunBuildInfo(helperPath));
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, true);
        }
    }

    private static void AssertEmbeddedFileParity(
        System.Reflection.Assembly pluginAssembly,
        IReadOnlySet<string> embeddedResources,
        HelperResource helper)
    {
        bool stagedExists = File.Exists(helper.StagedPath);
        bool embeddedExists = embeddedResources.Contains(helper.ResourceName);
        Assert.True(
            stagedExists == embeddedExists,
            $"Native payload presence differs for {helper.ResourceName}: " +
            $"staged={stagedExists} embedded={embeddedExists} path={helper.StagedPath}. " +
            "Rebuild PerfectComms without incremental outputs.");
        if (!stagedExists) return;

        using var embedded = pluginAssembly.GetManifestResourceStream(helper.ResourceName);
        Assert.NotNull(embedded);
        using var staged = File.OpenRead(helper.StagedPath);
        var embeddedHash = Convert.ToHexString(SHA256.HashData(embedded));
        var stagedHash = Convert.ToHexString(SHA256.HashData(staged));

        Assert.True(
            string.Equals(embeddedHash, stagedHash, StringComparison.Ordinal),
            $"Embedded native payload {helper.ResourceName} is stale. " +
            $"embedded={embeddedHash} staged={stagedHash}. " +
            "Rebuild PerfectComms without incremental outputs before packaging.");
    }

    private static async Task<string> RunBuildInfo(string helperPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(helperPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--build-info");
        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        var output = await standardOutput;
        var error = await standardError;
        Assert.Equal(0, process.ExitCode);
        Assert.True(
            string.IsNullOrWhiteSpace(error),
            $"Native helper wrote stderr during --build-info: {error}");
        return output.Trim();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PerfectComms.csproj")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate PerfectComms.csproj above {AppContext.BaseDirectory}.");
    }

    private sealed record HelperResource(string ResourceName, string StagedPath);
}
