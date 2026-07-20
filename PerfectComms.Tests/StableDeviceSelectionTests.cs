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
    public void MissingStableIdRecoversToUniqueAvailableEndpointWithSameName()
    {
        var available = new[]
        {
            Default,
            new VoiceDeviceInfo("different-id", "Same Display Name", false),
        };

        var published = VoiceChatLocalSettings.WithUnavailableSelection(
            available, "saved-id", "Same Display Name", "Saved microphone");
        int index = VoiceChatLocalSettings.ResolveDeviceIndex(
            "saved-id", "Same Display Name", published, 1,
            authoritativeDeviceList: true,
            recoverMissingStableId: true,
            out var resolved);

        Assert.Equal(3, published.Length);
        Assert.Equal(1, index);
        Assert.Equal("different-id", resolved?.Id);
        Assert.True(resolved?.IsAvailable);
    }

    [Fact]
    public void MissingMicrophoneStableIdDoesNotRetargetByName()
    {
        var devices = new[]
        {
            Default,
            new VoiceDeviceInfo("replacement-id", "USB Audio", false),
            VoiceDeviceInfo.Unavailable("saved-id", "USB Audio", "Saved microphone"),
        };

        int index = VoiceChatLocalSettings.ResolveDeviceIndex(
            "saved-id", "USB Audio", devices, 2,
            authoritativeDeviceList: true,
            recoverMissingStableId: false,
            out var resolved);

        Assert.Equal(2, index);
        Assert.Equal("saved-id", resolved?.Id);
        Assert.False(resolved?.IsAvailable);
    }

#if WINDOWS
    [Fact]
    public void LegacyDesktopMicrophoneIdPrefersExactRawCubebEndpoint()
    {
        var unique = new[]
        {
            Default,
            new VoiceDeviceInfo("cubeb-v2:776173617069:656e64706f696e742d3432", "USB Microphone", false),
            new VoiceDeviceInfo("cubeb-v2:776173617069:6f74686572", "USB Microphone", false),
            VoiceDeviceInfo.Unavailable("wasapi:endpoint-42", "USB Microphone", "Saved microphone"),
        };

        Assert.True(VoiceChatLocalSettings.ShouldRecoverLegacyDesktopDeviceId("wasapi:endpoint-42"));
        Assert.True(VoiceChatLocalSettings.ShouldRecoverLegacyDesktopDeviceId("cubeb-v1:0102"));
        Assert.False(VoiceChatLocalSettings.ShouldRecoverLegacyDesktopDeviceId("legacy-backend-id"));
        Assert.False(VoiceChatLocalSettings.ShouldRecoverLegacyDesktopDeviceId(string.Empty));
        Assert.True(VoiceChatLocalSettings.LegacyDesktopDeviceIdMatchesCubeb(
            "wasapi:endpoint-42", "cubeb-v2:776173617069:656e64706f696e742d3432"));
        Assert.True(VoiceChatLocalSettings.LegacyDesktopDeviceIdMatchesCubeb(
            "cubeb-v1:656e64706f696e742d3432",
            "cubeb-v2:776173617069:656e64706f696e742d3432"));
        Assert.False(VoiceChatLocalSettings.LegacyDesktopDeviceIdMatchesCubeb(
            "wasapi:endpoint-42", "cubeb-v1:zz"));

        int index = VoiceChatLocalSettings.ResolveDeviceIndex(
            "wasapi:endpoint-42", "USB Microphone", unique, 3,
            authoritativeDeviceList: true,
            recoverMissingStableId: VoiceChatLocalSettings.ShouldRecoverLegacyDesktopDeviceId(
                "wasapi:endpoint-42"),
            out var resolved);

        Assert.Equal(1, index);
        Assert.Equal("cubeb-v2:776173617069:656e64706f696e742d3432", resolved?.Id);
        Assert.True(resolved?.IsAvailable);
    }

    [Theory]
    [InlineData("wasapi", "wasapi")]
    [InlineData("coreaudio", "coreaudio")]
    [InlineData("pulseaudio", "pulse")]
    [InlineData("alsa", "alsa")]
    public void LegacyCpalIdsMigrateOnlyToCompatibleCubebBackendFamily(
        string legacyHost,
        string cubebFamily)
    {
        const string rawId = "device:with:colons";
        string savedId = legacyHost + ":" + rawId;
        string compatible = CubebV2(cubebFamily, rawId);
        string incompatible = CubebV2(
            string.Equals(cubebFamily, "alsa", StringComparison.Ordinal) ? "pulse" : "alsa",
            rawId);

        Assert.True(VoiceChatLocalSettings.LegacyDesktopDeviceIdMatchesCubeb(
            savedId, compatible));
        Assert.False(VoiceChatLocalSettings.LegacyDesktopDeviceIdMatchesCubeb(
            savedId, incompatible));
    }

    [Fact]
    public void LegacyLinuxIdCannotRetargetAcrossPulseAndAlsaWithSameRawId()
    {
        const string rawId = "default";
        var devices = new[]
        {
            Default,
            new VoiceDeviceInfo(CubebV2("pulse", rawId), "Linux Audio", true),
            new VoiceDeviceInfo(CubebV2("alsa", rawId), "Linux Audio", false),
            VoiceDeviceInfo.Unavailable("alsa:default", "Linux Audio", "Saved speaker"),
        };

        int index = VoiceChatLocalSettings.ResolveDeviceIndex(
            "alsa:default", "Linux Audio", devices, 3,
            authoritativeDeviceList: true,
            recoverMissingStableId: true,
            out var resolved);

        Assert.Equal(2, index);
        Assert.Equal(CubebV2("alsa", rawId), resolved?.Id);
    }

    [Fact]
    public void CubebV1MigratesByRawIdButV2RemainsBackendScoped()
    {
        const string rawId = "shared-id";
        string v1 = CubebV1(rawId);
        string pulse = CubebV2("pulse", rawId);
        string alsa = CubebV2("alsa", rawId);

        Assert.True(VoiceChatLocalSettings.LegacyDesktopDeviceIdMatchesCubeb(v1, pulse));
        Assert.True(VoiceChatLocalSettings.LegacyDesktopDeviceIdMatchesCubeb(v1, alsa));
        Assert.False(VoiceChatLocalSettings.LegacyDesktopDeviceIdMatchesCubeb(pulse, alsa));
    }

    [Fact]
    public void MalformedOrOversizedLegacyIdsAreNotMigrationCandidates()
    {
        Assert.False(VoiceChatLocalSettings.ShouldRecoverLegacyDesktopDeviceId("cubeb-v1:"));
        Assert.False(VoiceChatLocalSettings.ShouldRecoverLegacyDesktopDeviceId("cubeb-v1:zz"));
        Assert.False(VoiceChatLocalSettings.ShouldRecoverLegacyDesktopDeviceId(
            "wasapi:" + new string('x', 4_097)));
        Assert.False(VoiceChatLocalSettings.LegacyDesktopDeviceIdMatchesCubeb(
            "cubeb-v2::00", CubebV2("wasapi", "\0")));
    }
#endif

#if ANDROID
    [Fact]
    public void AndroidDoesNotTreatDesktopDeviceIdsAsMigrationCandidates()
    {
        Assert.False(VoiceChatLocalSettings.ShouldRecoverLegacyDesktopDeviceId(
            "wasapi:endpoint"));
        Assert.False(VoiceChatLocalSettings.ShouldRecoverLegacyDesktopDeviceId(
            CubebV1("endpoint")));
    }
#endif

    [Fact]
    public void OpaqueStableIdsAreCaseSensitive()
    {
        var devices = new[]
        {
            Default,
            new VoiceDeviceInfo("case-sensitive", "Mic", false),
        };

        int index = VoiceChatLocalSettings.ResolveDeviceIndex(
            "CASE-SENSITIVE", "Different legacy name", devices, 1, out var resolved);

        Assert.Equal(0, index);
        Assert.Null(resolved);
    }

    [Fact]
    public void FirstAuthoritativeDeviceListEstablishesBaselineOnlyOnce()
    {
        bool established = false;
        Assert.False(VoiceChatLocalSettings.MarkAndDetectFirstAuthoritativeList(
            authoritative: false, ref established));
        Assert.False(established);

        Assert.True(VoiceChatLocalSettings.MarkAndDetectFirstAuthoritativeList(
            authoritative: true, ref established));
        Assert.True(established);
        Assert.False(VoiceChatLocalSettings.MarkAndDetectFirstAuthoritativeList(
            authoritative: true, ref established));
    }

    [Fact]
    public void FirstAuthoritativeListAppliesIdentityChangingLegacyMigration()
    {
        var migrated = new VoiceDeviceInfo(
            CubebV2("wasapi", "endpoint-42"), "USB Microphone", false);

        Assert.True(VoiceChatLocalSettings.ShouldReapplyResolvedDeviceAfterListPublication(
            firstAuthoritativeList: true,
            previousId: "wasapi:endpoint-42",
            previousAvailable: false,
            resolvedDevice: migrated));
        Assert.False(VoiceChatLocalSettings.ShouldReapplyResolvedDeviceAfterListPublication(
            firstAuthoritativeList: true,
            previousId: migrated.Id,
            previousAvailable: false,
            resolvedDevice: migrated));
        Assert.True(VoiceChatLocalSettings.ShouldReapplyResolvedDeviceAfterListPublication(
            firstAuthoritativeList: false,
            previousId: migrated.Id,
            previousAvailable: false,
            resolvedDevice: migrated));
    }

    [Fact]
    public void FirstAuthoritativeListUsesPersistedIdentityWhenRuntimeBaselineIsEmpty()
    {
        var current = new VoiceDeviceInfo(
            CubebV2("wasapi", "endpoint-42"), "USB Microphone", false);

        Assert.False(VoiceChatLocalSettings.ShouldReapplyResolvedDeviceAfterListPublication(
            firstAuthoritativeList: true,
            previousId: string.Empty,
            previousAvailable: false,
            persistedSavedId: current.Id,
            resolvedDevice: current));
        Assert.True(VoiceChatLocalSettings.ShouldReapplyResolvedDeviceAfterListPublication(
            firstAuthoritativeList: true,
            previousId: string.Empty,
            previousAvailable: false,
            persistedSavedId: "wasapi:endpoint-42",
            resolvedDevice: current));
    }

    [Fact]
    public void AmbiguousNameRecoveryPreservesUnavailablePreference()
    {
        var devices = new[]
        {
            Default,
            new VoiceDeviceInfo("new-id-a", "USB Audio", false),
            new VoiceDeviceInfo("new-id-b", "USB Audio", true),
            VoiceDeviceInfo.Unavailable("old-id", "USB Audio", "Saved speaker"),
        };

        int index = VoiceChatLocalSettings.ResolveDeviceIndex(
            "old-id", "USB Audio", devices, 3,
            authoritativeDeviceList: true,
            recoverMissingStableId: true,
            out var resolved);

        Assert.Equal(3, index);
        Assert.Equal("old-id", resolved?.Id);
        Assert.False(resolved?.IsAvailable);
    }

    [Fact]
    public void NonAuthoritativeStartupListPreservesPlaceholderUntilProbeCompletes()
    {
        var devices = new[]
        {
            Default,
            VoiceDeviceInfo.Unavailable("saved-id", "USB Headset", "Saved speaker"),
        };

        int index = VoiceChatLocalSettings.ResolveDeviceIndex(
            "saved-id", "USB Headset", devices, 1,
            authoritativeDeviceList: false,
            recoverMissingStableId: true,
            out var resolved);

        Assert.Equal(1, index);
        Assert.Equal("saved-id", resolved?.Id);
        Assert.False(resolved?.IsAvailable);
    }

    [Fact]
    public void SetupCannotSelectUnavailableEndpoint()
    {
        var devices = new[]
        {
            Default,
            new VoiceDeviceInfo("available", "Speakers", false),
            VoiceDeviceInfo.Unavailable("missing", "Headphones", "Saved speaker"),
        };

        Assert.True(FirstRunSetupDraft.CanSelectDeviceIndex(0, devices));
        Assert.True(FirstRunSetupDraft.CanSelectDeviceIndex(1, devices));
        Assert.False(FirstRunSetupDraft.CanSelectDeviceIndex(2, devices));
        Assert.False(FirstRunSetupDraft.CanSelectDeviceIndex(3, devices));
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

    private static string CubebV1(string rawId)
        => "cubeb-v1:" + Hex(rawId);

    private static string CubebV2(string family, string rawId)
        => "cubeb-v2:" + Hex(family) + ":" + Hex(rawId);

    private static string Hex(string value)
        => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(value)).ToLowerInvariant();
}
