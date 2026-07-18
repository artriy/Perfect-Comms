using System.Diagnostics;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class EmbeddedManagedDependenciesTests
{
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
