using System.Diagnostics;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class EmbeddedManagedDependenciesTests
{
    [Fact]
    public void StandalonePluginCarriesNativeDependencyNotices()
    {
        var resources = typeof(BetterCrewLinkLobbyPublisher).Assembly.GetManifestResourceNames();
        var expected = new[]
        {
            "Licenses.THIRD_PARTY_NOTICES.md",
            "Licenses.SocketIOClient-MIT.txt",
            "Licenses.System.Text.Encodings.Web-THIRD-PARTY-NOTICES.txt",
            "Licenses.System.Text.Json-THIRD-PARTY-NOTICES.txt",
            "Licenses.WebRTC-BSD-3-Clause.txt",
            "Licenses.WebRTC-fft-BSD-3-Clause.txt",
            "Licenses.WebRTC-ooura-BSD.txt",
            "Licenses.WebRTC-pffft-BSD-3-Clause.txt",
            "Licenses.WebRTC-rnnoise-BSD-3-Clause.txt",
            "Licenses.WebRTC-spl-sqrt-floor-BSD-3-Clause.txt",
            "Licenses.cubeb-coreaudio-rust-dependencies.html",
            "Licenses.cubeb-rs-ISC.txt",
            "Licenses.cubeb-speex-resampler-BSD-3-Clause.txt",
            "Licenses.dotnet-runtime-MIT.txt",
            "Licenses.libcubeb-ISC.txt",
            "Licenses.libopus-BSD-3-Clause.txt",
            "Licenses.native-rust-dependencies.html",
            "Licenses.opusic-c-BSD-3-Clause.txt",
            "Licenses.pion-go-dependencies.txt",
            "Licenses.webrtc-audio-processing-BSD-3-Clause.txt",
        };

        foreach (var resource in expected)
            Assert.Contains(resource, resources);
    }

    [Fact]
    public void SocketIoV4RuntimeClosureIsEmbedded()
    {
        var resources = typeof(BetterCrewLinkLobbyPublisher).Assembly.GetManifestResourceNames();
        var expected = new[]
        {
            "Lib.Microsoft.Bcl.AsyncInterfaces.dll",
            "Lib.Microsoft.Extensions.DependencyInjection.Abstractions.dll",
            "Lib.Microsoft.Extensions.DependencyInjection.dll",
            "Lib.Microsoft.Extensions.Logging.Abstractions.dll",
            "Lib.Microsoft.Extensions.Logging.dll",
            "Lib.Microsoft.Extensions.Options.dll",
            "Lib.Microsoft.Extensions.Primitives.dll",
            "Lib.SocketIOClient.Common.dll",
            "Lib.SocketIOClient.Serializer.dll",
            "Lib.SocketIOClient.dll",
            "Lib.System.Diagnostics.DiagnosticSource.dll",
            "Lib.System.IO.Pipelines.dll",
            "Lib.System.Text.Encodings.Web.dll",
            "Lib.System.Text.Json.dll",
        };

        foreach (var resource in expected)
            Assert.Contains(resource, resources);

        Assert.DoesNotContain("Lib.SocketIO.Core.dll", resources);
        Assert.DoesNotContain("Lib.SocketIO.Serializer.Core.dll", resources);
        Assert.DoesNotContain("Lib.SocketIO.Serializer.SystemTextJson.dll", resources);
    }

    [Fact]
    public void EmbeddedDotNetRuntimeDependenciesUsePinnedServicingVersion()
    {
        var assembly = typeof(BetterCrewLinkLobbyPublisher).Assembly;
        var runtimeResources = new[]
        {
            "Lib.Microsoft.Extensions.DependencyInjection.Abstractions.dll",
            "Lib.Microsoft.Extensions.DependencyInjection.dll",
            "Lib.Microsoft.Extensions.Logging.Abstractions.dll",
            "Lib.Microsoft.Extensions.Logging.dll",
            "Lib.Microsoft.Extensions.Options.dll",
            "Lib.Microsoft.Extensions.Primitives.dll",
            "Lib.System.Diagnostics.DiagnosticSource.dll",
        };

        foreach (var resourceName in runtimeResources)
        {
            using var resource = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(resource);

            var extractedPath = Path.Combine(
                Path.GetTempPath(),
                $"perfectcomms-embedded-{Guid.NewGuid():N}.dll");

            try
            {
                using (var extracted = File.Create(extractedPath))
                    resource.CopyTo(extracted);

                var productVersion = FileVersionInfo.GetVersionInfo(extractedPath).ProductVersion;
                Assert.NotNull(productVersion);
                Assert.StartsWith("10.0.10+", productVersion, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(extractedPath);
            }
        }
    }
}
