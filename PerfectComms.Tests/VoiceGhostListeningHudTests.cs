using VoiceChatPlugin.VoiceChat;
using Xunit;
using Audience = VoiceChatPlugin.VoiceChat.VoiceChatHudState.GhostMicrophoneAudience;

public sealed class VoiceGhostListeningHudTests
{
    [Theory]
    [InlineData((int)VoiceGamePhase.Lobby, true, true)]
    [InlineData((int)VoiceGamePhase.Lobby, false, false)]
    [InlineData((int)VoiceGamePhase.Tasks, true, false)]
    [InlineData((int)VoiceGamePhase.Meeting, true, false)]
    [InlineData((int)VoiceGamePhase.Unknown, true, false)]
    public void NoticeRequiresActualLobbyAndSyncedSettings(int phaseValue, bool synced, bool visible)
    {
        var phase = (VoiceGamePhase)phaseValue;
        var settings = VoiceRoomSettingsSnapshot.Defaults with
        {
            OnlyMeetingOrLobby = true,
            GhostsHearLivingDuringTasks = true,
        };
        Assert.Equal(visible, VoiceHudWarnings.ShouldShowGhostListeningNotice(settings, phase, synced));
        Assert.False(VoiceHudWarnings.ShouldShowGhostListeningNotice(
            settings with { GhostsHearLivingDuringTasks = false }, phase, synced));
        Assert.False(VoiceHudWarnings.ShouldShowGhostListeningNotice(
            settings with { OnlyMeetingOrLobbyAffectsGhosts = true }, phase, synced));
    }

    [Theory]
    [InlineData(false, false, false, false, false, false, (int)Audience.None)]
    [InlineData(true, false, false, false, false, false, (int)Audience.GhostsOnly)]
    [InlineData(true, true, false, false, false, false, (int)Audience.None)]
    [InlineData(true, false, true, false, false, false, (int)Audience.Muted)]
    [InlineData(true, false, false, true, false, false, (int)Audience.Deafened)]
    [InlineData(true, false, true, true, true, false, (int)Audience.Deafened)]
    [InlineData(true, false, false, false, true, false, (int)Audience.PushToTalk)]
    [InlineData(true, false, false, false, false, true, (int)Audience.None)]
    public void AudienceNeverClaimsTransmissionOverMuteOrRadio(
        bool ghostOnly, bool blocked, bool muted, bool deafened, bool pttBlocked, bool radio, int expected)
    {
        Assert.Equal((Audience)expected, VoiceChatHudState.ResolveGhostMicrophoneAudience(
            ghostOnly, blocked, muted, deafened, pttBlocked, radio));
    }

    [Fact]
    public void NoticeUsesSafeBottomRightAndMovesAboveIntersectingControls()
    {
        var safe = new VoiceHudRect(0.06f, 0.04f, 0.88f, 0.92f);
        var control = new VoiceHudRect(0.83f, 0.06f, 0.08f, 0.15f);
        Assert.True(VoiceChatHudState.TryPlaceGhostListeningNotice(
            safe, 0.34f, 0.08f, default, default, out var anchored));
        Assert.True(VoiceChatHudState.TryPlaceGhostListeningNotice(
            safe, 0.34f, 0.08f, control, default, out var shifted));
        Assert.Equal(safe.XMax - 0.015f, anchored.X + 0.34f, 5);
        Assert.Equal(safe.YMin + 0.015f, anchored.Y, 5);
        Assert.True(shifted.Y > control.YMax);
        Assert.True(shifted.Y + 0.08f < safe.YMax);
        Assert.Equal(anchored.X, shifted.X);
    }

    [Fact]
    public void VerticalControlStackAndStatusAreClearedTogether()
    {
        var safe = new VoiceHudRect(0f, 0f, 1f, 1f);
        var controls = new VoiceHudRect(0.85f, 0.02f, 0.13f, 0.32f);
        var status = new VoiceHudRect(0.61f, 0.36f, 0.37f, 0.12f);
        Assert.True(VoiceChatHudState.TryPlaceGhostListeningNotice(
            safe, 0.38f, 0.1f, controls, status, out var position));
        Assert.True(position.Y >= status.YMax + 0.014f);
        AssertPlacement(position, 0.38f, 0.1f, safe, controls, status);
    }

    [Fact]
    public void NearSafeTopStackMovesLeftInsteadOfClampingBackOntoControls()
    {
        var safe = new VoiceHudRect(0.08f, 0.06f, 0.84f, 0.80f);
        var controls = new VoiceHudRect(0.75f, 0.07f, 0.15f, 0.64f);
        var status = new VoiceHudRect(0.57f, 0.72f, 0.33f, 0.12f);
        Assert.True(VoiceChatHudState.TryPlaceGhostListeningNotice(
            safe, 0.34f, 0.12f, controls, status, out var position));
        Assert.True(position.X + 0.34f < controls.XMin);
        AssertPlacement(position, 0.34f, 0.12f, safe, controls, status);
    }

    [Fact]
    public void FullHeightObstructionNeverReturnsAnOverlappingPlacement()
    {
        Assert.False(VoiceChatHudState.TryPlaceGhostListeningNotice(
            new VoiceHudRect(0f, 0f, 1f, 1f), 0.38f, 0.1f,
            new VoiceHudRect(0f, 0f, 1f, 1f), default, out _));
    }

    private static void AssertPlacement(
        VoiceHudPoint position, float width, float height, VoiceHudRect safe,
        VoiceHudRect controls, VoiceHudRect status)
    {
        const float tolerance = 0.00001f;
        Assert.True(position.X >= safe.XMin + 0.015f - tolerance);
        Assert.True(position.Y >= safe.YMin + 0.015f - tolerance);
        Assert.True(position.X + width <= safe.XMax - 0.015f + tolerance);
        Assert.True(position.Y + height <= safe.YMax - 0.015f + tolerance);
        AssertClear(controls);
        AssertClear(status);

        void AssertClear(VoiceHudRect obstruction)
            => Assert.True(position.X + width <= obstruction.XMin - 0.015f + tolerance ||
                           position.X >= obstruction.XMax + 0.015f - tolerance ||
                           position.Y + height <= obstruction.YMin - 0.015f + tolerance ||
                           position.Y >= obstruction.YMax + 0.015f - tolerance);
    }
}
