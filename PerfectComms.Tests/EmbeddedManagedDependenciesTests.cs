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
}
