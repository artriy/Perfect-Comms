using System;
using System.Threading.Tasks;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceLobbyRegistryPublisherTests
{
    [Fact]
    public async Task ClearDuringPendingPublishRunsFinalDeleteAfterPublish()
    {
        var queue = new VoiceLobbyRegistryOperationQueue();
        var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(queue.TryStart(() => publish.Task));
        var cleanup = queue.QueueAfterCurrent(() =>
        {
            deleteStarted.SetResult();
            return releaseDelete.Task;
        });

        Assert.True(queue.IsBusy);
        Assert.False(deleteStarted.Task.IsCompleted);

        publish.SetResult();
        await deleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(queue.IsBusy);
        Assert.False(cleanup.IsCompleted);

        releaseDelete.SetResult();
        await cleanup;

        Assert.False(queue.IsBusy);
    }

    [Fact]
    public async Task ReplacementPublishCannotStartUntilFinalDeleteCompletes()
    {
        var queue = new VoiceLobbyRegistryOperationQueue();
        var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementStarted = false;

        Assert.True(queue.TryStart(() => publish.Task));
        var cleanup = queue.QueueAfterCurrent(() =>
        {
            deleteStarted.SetResult();
            return releaseDelete.Task;
        });

        Assert.False(queue.TryStart(() =>
        {
            replacementStarted = true;
            return Task.CompletedTask;
        }));
        Assert.False(replacementStarted);

        publish.SetResult();
        await deleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(queue.TryStart(() =>
        {
            replacementStarted = true;
            return Task.CompletedTask;
        }));
        Assert.False(replacementStarted);

        releaseDelete.SetResult();
        await cleanup;

        Assert.True(queue.TryStart(() =>
        {
            replacementStarted = true;
            return Task.CompletedTask;
        }));
        Assert.True(replacementStarted);
    }

    [Fact]
    public async Task FinalDeleteStillRunsAfterFailedPublish()
    {
        var queue = new VoiceLobbyRegistryOperationQueue();
        var deleteRan = false;

        Assert.True(queue.TryStart(
            () => Task.FromException(new InvalidOperationException("publish failed"))));

        await queue.QueueAfterCurrent(() =>
        {
            deleteRan = true;
            return Task.CompletedTask;
        });

        Assert.True(deleteRan);
        Assert.False(queue.IsBusy);
    }
}
