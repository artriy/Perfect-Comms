using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class StableDeviceSelectionTests
{
    private static readonly VoiceDeviceInfo Default =
        new(string.Empty, "Default", true);

    [Fact]
    public void LegacyDisplayNameResolvesOnceToStructuredDevice()
    {
        var devices = new[]
        {
            Default,
            new VoiceDeviceInfo("stable-mic-id", "Legacy Mic Name", false),
        };

        int index = VoiceChatLocalSettings.ResolveDeviceIndex(
            string.Empty, "legacy mic name", devices, 0, out var resolved);

        Assert.Equal(1, index);
        Assert.Equal("stable-mic-id", resolved?.Id);
        Assert.Equal("Legacy Mic Name", resolved?.Name);
    }

    [Fact]
    public void StableIdSelectsCorrectEndpointWhenNamesAreDuplicated()
    {
        var devices = new[]
        {
            Default,
            new VoiceDeviceInfo("mic-a", "USB Audio", false),
            new VoiceDeviceInfo("mic-b", "USB Audio", true),
        };

        int index = VoiceChatLocalSettings.ResolveDeviceIndex(
            "mic-b", "USB Audio", devices, 0, out var resolved);

        Assert.Equal(2, index);
        Assert.Equal("mic-b", resolved?.Id);
    }

    [Fact]
    public void StableIdStillDistinguishesEndpointsWhenFriendlyNamesAreDuplicated()
    {
        var devices = new[]
        {
            Default,
            new VoiceDeviceInfo("wasapi-endpoint-a", "HyperX Cloud III", false),
            new VoiceDeviceInfo("wasapi-endpoint-b", "HyperX Cloud III", true),
        };

        int index = VoiceChatLocalSettings.ResolveDeviceIndex(
            "wasapi-endpoint-b", "Microphone", devices, 0, out var resolved);

        Assert.Equal(2, index);
        Assert.Equal("wasapi-endpoint-b", resolved?.Id);
        Assert.Equal("HyperX Cloud III", resolved?.Name);
    }

    [Fact]
    public void ResolvedStableIdProducesFriendlyPersistedSelectionInsteadOfStaleGenericName()
    {
        var devices = new[]
        {
            Default,
            new VoiceDeviceInfo("wasapi-stable-id", "Razer Seiren Mini", true),
        };

        int index = VoiceChatLocalSettings.ResolveDeviceIndex(
            "wasapi-stable-id", "Microphone", devices, 0, out var resolved);
        Assert.True(resolved.HasValue);
        var persisted = VoiceChatLocalSettings.PersistedSelectionForDevice(
            index, resolved.Value);

        Assert.Equal(1, index);
        Assert.Equal("wasapi-stable-id", persisted.Id);
        Assert.Equal("Razer Seiren Mini", persisted.Name);
    }

    [Fact]
    public void MissingStableIdBecomesExplicitUnavailableSelectionInsteadOfStaleIndex()
    {
        var available = new[]
        {
            Default,
            new VoiceDeviceInfo("different-id", "Same Display Name", false),
        };

        var published = VoiceChatLocalSettings.WithUnavailableSelection(
            available, "saved-id", "Same Display Name", "Saved microphone");
        int index = VoiceChatLocalSettings.ResolveDeviceIndex(
            "saved-id", "Same Display Name", published, 1, out var resolved);

        Assert.Equal(3, published.Length);
        Assert.Equal(2, index);
        Assert.Equal("saved-id", resolved?.Id);
        Assert.False(resolved?.IsAvailable);
        Assert.Equal("different-id", published[1].Id);
    }

    [Fact]
    public void OpaqueStableIdsAreCaseSensitive()
    {
        var devices = new[]
        {
            Default,
            new VoiceDeviceInfo("case-sensitive", "Mic", false),
        };

        int index = VoiceChatLocalSettings.ResolveDeviceIndex(
            "CASE-SENSITIVE", "Mic", devices, 1, out var resolved);

        Assert.Equal(0, index);
        Assert.Null(resolved);
    }

    [Fact]
    public void SetupSelectionChangeUsesOpaqueIdSemantics()
    {
        var draft = new FirstRunSetupDraft
        {
            MicrophoneDevice = "case-sensitive",
            OriginalMicrophoneDevice = "CASE-SENSITIVE",
        };

        Assert.True(draft.MicrophoneSelectionChanged);
    }

    [Fact]
    public void ReappearingDeviceAtSameIndexIsReapplied()
    {
        var missing = VoiceDeviceInfo.Unavailable(
            "stable-mic-id", "USB Microphone", "Saved microphone");
        var returned = new VoiceDeviceInfo(
            "stable-mic-id", "USB Microphone", false);

        Assert.False(VoiceChatLocalSettings.ShouldReapplyResolvedDevice(
            "stable-mic-id", true, missing));
        Assert.True(VoiceChatLocalSettings.ShouldReapplyResolvedDevice(
            "stable-mic-id", false, returned));
    }

    [Fact]
    public void UnrelatedDeviceListChangeDoesNotReapplySelectedDevice()
    {
        var stillSelected = new VoiceDeviceInfo(
            "stable-mic-id", "USB Microphone", false);

        Assert.False(VoiceChatLocalSettings.ShouldReapplyResolvedDevice(
            "stable-mic-id", true, stillSelected));
    }

    [Fact]
    public void StableIdReorderCorrectsIndexWithoutRuntimeReselect()
    {
        var sameEndpointAtNewIndex = new VoiceDeviceInfo(
            "stable-device-id", "USB Audio", false);

        Assert.False(VoiceChatLocalSettings.ShouldProcessDeviceSelectionDispatch(
            internalIndexCorrection: true));
        Assert.False(VoiceChatLocalSettings.ShouldReapplyResolvedDevice(
            "stable-device-id", true, sameEndpointAtNewIndex));
    }

    [Fact]
    public void UserChoiceAndReturnedEndpointStillApply()
    {
        var returnedEndpoint = new VoiceDeviceInfo(
            "stable-device-id", "USB Audio", false);

        Assert.True(VoiceChatLocalSettings.ShouldProcessDeviceSelectionDispatch(
            internalIndexCorrection: false));
        Assert.True(VoiceChatLocalSettings.ShouldReapplyResolvedDevice(
            "stable-device-id", false, returnedEndpoint));
    }

    [Fact]
    public void ChangedSystemDefaultReappliesOnlyWhenDefaultIsSelected()
    {
        Assert.True(VoiceChatLocalSettings.ShouldReapplyDefaultDevice(
            true, "old-default", "new-default"));
        Assert.False(VoiceChatLocalSettings.ShouldReapplyDefaultDevice(
            false, "old-default", "new-default"));
        Assert.False(VoiceChatLocalSettings.ShouldReapplyDefaultDevice(
            true, "same-default", "same-default"));
        Assert.False(VoiceChatLocalSettings.ShouldReapplyDefaultDevice(
            true, "old-default", string.Empty));
    }

    [Fact]
    public void DefaultEndpointIdentityIgnoresSyntheticAndUnavailableEntries()
    {
        var devices = new[]
        {
            Default,
            new VoiceDeviceInfo("unavailable", "Disconnected", true, false),
            new VoiceDeviceInfo("active-default", "Headset", true),
            new VoiceDeviceInfo("other", "Speakers", false),
        };

        Assert.Equal(
            "active-default",
            VoiceChatLocalSettings.DefaultAvailableDeviceId(devices));
    }

    [Fact]
    public void ForcedDefaultMicRefreshRestartsRepeatedEmptySelection()
    {
        Assert.False(PerfectCommsVoiceBackend.ShouldRestartMicrophoneSelection(
            string.Empty, string.Empty, forceRestart: false));
        Assert.True(PerfectCommsVoiceBackend.ShouldRestartMicrophoneSelection(
            string.Empty, string.Empty, forceRestart: true));
    }

    [Fact]
    public void ActiveRoomDeviceProbeUsesAConservativeInterval()
    {
        Assert.True(
            VoiceChatLocalSettings.ActiveDeviceProbeInterval >= TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void LegacyDefaultLiteralCanonicalizesOnlyForSyntheticDefaultSelection()
    {
        var synthetic = VoiceChatLocalSettings.CanonicalizeLegacyPersistedSelection(
            0, string.Empty, "Default");
        var realByIndex = VoiceChatLocalSettings.CanonicalizeLegacyPersistedSelection(
            1, string.Empty, "Default");
        var realByStableId = VoiceChatLocalSettings.CanonicalizeLegacyPersistedSelection(
            0, "real-default-id", "Default");

        Assert.Equal((string.Empty, string.Empty), synthetic);
        Assert.Equal((string.Empty, "Default"), realByIndex);
        Assert.Equal(("real-default-id", "Default"), realByStableId);
    }

    [Fact]
    public void MigratedLegacyDefaultDoesNotCreateAnUnavailablePickerEntry()
    {
        var canonical = VoiceChatLocalSettings.CanonicalizeLegacyPersistedSelection(
            0, string.Empty, "Default");
        var published = VoiceChatLocalSettings.WithUnavailableSelection(
            new[] { Default }, canonical.Id, canonical.Name, "Saved microphone");

        Assert.Single(published);
        Assert.Equal(Default, published[0]);
    }

    [Fact]
    public void PersistingSyntheticDefaultNeverStoresItsDisplayLabel()
    {
        var synthetic = VoiceChatLocalSettings.PersistedSelectionForDevice(0, Default);
        var realNamedDefault = VoiceChatLocalSettings.PersistedSelectionForDevice(
            1, new VoiceDeviceInfo("real-default-id", "Default", true));

        Assert.Equal((string.Empty, string.Empty), synthetic);
        Assert.Equal(("real-default-id", "Default"), realNamedDefault);
    }

    [Fact]
    public void DefaultTrackingWorksAfterLegacyMigrationAndDefaultReselection()
    {
        var migrated = VoiceChatLocalSettings.CanonicalizeLegacyPersistedSelection(
            0, string.Empty, "Default");
        bool migratedDefault = VoiceChatLocalSettings.IsPersistedDefaultSelection(
            0, migrated.Id, migrated.Name);
        var reselected = VoiceChatLocalSettings.PersistedSelectionForDevice(0, Default);
        bool reselectedDefault = VoiceChatLocalSettings.IsPersistedDefaultSelection(
            0, reselected.Id, reselected.Name);

        Assert.True(migratedDefault);
        Assert.True(reselectedDefault);
        Assert.True(VoiceChatLocalSettings.ShouldReapplyDefaultDevice(
            migratedDefault, "old-default", "new-default"));
        Assert.True(VoiceChatLocalSettings.ShouldReapplyDefaultDevice(
            reselectedDefault, "old-default", "new-default"));
    }
}
