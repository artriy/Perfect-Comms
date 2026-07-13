using System;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceModRemoteOptionStateTests : IDisposable
{
    private const string Prefix = "tests.remote-options.";
    private const string BoolKey = Prefix + "enabled";
    private const string EnumKey = Prefix + "mode";

    public VoiceModRemoteOptionStateTests()
    {
        VoiceModRemoteOptionState.RemovePrefix(Prefix);
        VoiceModRemoteOptionState.Clear();
        VoiceModRemoteOptionState.RegisterBool(BoolKey, defaultValue: true);
        VoiceModRemoteOptionState.RegisterEnum(EnumKey, defaultValue: 2);
    }

    public void Dispose()
    {
        VoiceModRemoteOptionState.Clear();
        VoiceModRemoteOptionState.RemovePrefix(Prefix);
    }

    [Fact]
    public void CompleteRemoteSyncStartsFromRegisteredDefaults()
    {
        VoiceModRemoteOptionState.BeginSync();

        Assert.True(VoiceModRemoteOptionState.IsActive);
        Assert.True(VoiceModRemoteOptionState.GetBool(BoolKey));
        Assert.Equal(2, VoiceModRemoteOptionState.GetEnum(EnumKey));
    }

    [Fact]
    public void NewSnapshotReplacesRatherThanMergesOldHostValues()
    {
        VoiceModRemoteOptionState.BeginSync();
        VoiceModRemoteOptionState.SetBool(BoolKey, false);
        VoiceModRemoteOptionState.SetEnum(EnumKey, 7);

        VoiceModRemoteOptionState.BeginSync();

        Assert.True(VoiceModRemoteOptionState.GetBool(BoolKey));
        Assert.Equal(2, VoiceModRemoteOptionState.GetEnum(EnumKey));
    }

    [Fact]
    public void SessionClearRemovesRemoteAuthority()
    {
        VoiceModRemoteOptionState.BeginSync();
        VoiceModRemoteOptionState.SetBool(BoolKey, false);

        VoiceModRemoteOptionState.Clear();

        Assert.False(VoiceModRemoteOptionState.IsActive);
    }
}
