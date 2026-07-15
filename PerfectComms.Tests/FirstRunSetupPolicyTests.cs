using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class FirstRunSetupPolicyTests
{
    [Fact]
    public void CurrentSetupRevisionIsTheV4OnboardingRevision()
    {
        Assert.Equal(1, FirstRunSetupPolicy.CurrentRevision);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    public void MissingOrOlderStateNeedsAutomaticSetup(int completedRevision)
    {
        Assert.True(FirstRunSetupPolicy.NeedsAutomaticSetup(completedRevision));

        var state = FirstRunSetupPolicy.Evaluate(completedRevision);
        Assert.True(state.ShouldShow);
        Assert.True(state.IsAutomatic);
        Assert.False(state.IsManual);
        Assert.Equal(FirstRunSetupTriggerReason.AutomaticRevisionRequired, state.TriggerReason);
        Assert.Equal(FirstRunSetupPolicy.CurrentRevision, state.TargetRevision);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void CompletedOrNewerStateNeverRerunsAutomatically(int completedRevision)
    {
        Assert.False(FirstRunSetupPolicy.NeedsAutomaticSetup(completedRevision));

        var state = FirstRunSetupPolicy.Evaluate(completedRevision);
        Assert.False(state.ShouldShow);
        Assert.False(state.IsAutomatic);
        Assert.False(state.IsManual);
        Assert.Equal(FirstRunSetupTriggerReason.None, state.TriggerReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public void ManualRequestBypassesAutomaticRevisionGate(int completedRevision)
    {
        var state = FirstRunSetupPolicy.Evaluate(completedRevision, manualRequested: true);

        Assert.True(state.ShouldShow);
        Assert.False(state.IsAutomatic);
        Assert.True(state.IsManual);
        Assert.Equal(FirstRunSetupTriggerReason.Manual, state.TriggerReason);
        Assert.Equal(completedRevision, state.CompletedRevision);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void CompletionAdvancesOldStateWithoutDowngradingNewerState(
        int completedRevision,
        int expectedRevision)
    {
        var state = FirstRunSetupPolicy.Evaluate(completedRevision, manualRequested: true);

        Assert.Equal(expectedRevision, state.RevisionToStoreOnCompletion);
        Assert.Equal(
            expectedRevision,
            FirstRunSetupPolicy.RevisionToStoreOnCompletion(completedRevision));
    }
}
