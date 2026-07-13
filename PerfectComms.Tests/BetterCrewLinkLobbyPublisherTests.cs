using System;
using System.Threading.Tasks;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class BetterCrewLinkLobbyPublisherTests
{
    [Fact]
    public void SocketCallbackRequiresMatchingInstanceAndGeneration()
    {
        var current = new object();
        var replacement = new object();

        Assert.True(BetterCrewLinkLobbyPublisher.MatchesSocketGeneration(
            current, currentGeneration: 7, current, candidateGeneration: 7));
        Assert.False(BetterCrewLinkLobbyPublisher.MatchesSocketGeneration(
            current, currentGeneration: 8, current, candidateGeneration: 7));
        Assert.False(BetterCrewLinkLobbyPublisher.MatchesSocketGeneration(
            replacement, currentGeneration: 7, current, candidateGeneration: 7));
    }

    [Fact]
    public async Task SocketCleanupWaitsForPendingPublish()
    {
        var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupRan = false;

        var cleanup = BetterCrewLinkLobbyPublisher.RunAfterPendingAsync(
            publish.Task,
            () =>
            {
                cleanupRan = true;
                return Task.CompletedTask;
            });

        Assert.False(cleanup.IsCompleted);
        Assert.False(cleanupRan);

        publish.SetResult();
        await cleanup;

        Assert.True(cleanupRan);
    }

    [Fact]
    public async Task SocketCleanupStillRunsAfterFailedPublish()
    {
        var cleanupRan = false;

        await BetterCrewLinkLobbyPublisher.RunAfterPendingAsync(
            Task.FromException(new InvalidOperationException("publish failed")),
            () =>
            {
                cleanupRan = true;
                return Task.CompletedTask;
            });

        Assert.True(cleanupRan);
    }
}
