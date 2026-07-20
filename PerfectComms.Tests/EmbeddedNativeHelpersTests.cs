using System.Security.Cryptography;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class EmbeddedNativeHelpersTests
{
    [Fact]
    public void EmbeddedDesktopHelpersMatchStagedFiles()
    {
#if ANDROID
        // Android embeds the in-process pc-mobile engine instead of desktop sidecars.
        return;
#else
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

        var pluginAssembly = typeof(BetterCrewLinkLobbyPublisher).Assembly;
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
#endif
    }

    [Fact]
    public void EmbeddedAndroidNativePayloadsMatchStagedFiles()
    {
#if ANDROID
        var repositoryRoot = FindRepositoryRoot();
        var payloads = new[]
        {
            new HelperResource(
                "Lib.pc-mobile.libpc_mobile.so",
                Path.Combine(repositoryRoot, "Libs", "pc-mobile", "libpc_mobile.so")),
            new HelperResource(
                "Lib.pc-pion.libpc-pion.android-arm64.so",
                Path.Combine(repositoryRoot, "Libs", "pion", "libpc-pion.android-arm64.so")),
        };
        var pluginAssembly = typeof(BetterCrewLinkLobbyPublisher).Assembly;
        var embeddedResources = pluginAssembly
            .GetManifestResourceNames()
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(
            embeddedResources,
            resource => resource.StartsWith("Lib.pc-capture.", StringComparison.Ordinal));
        foreach (var payload in payloads)
            AssertEmbeddedFileParity(pluginAssembly, embeddedResources, payload);
#endif
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
