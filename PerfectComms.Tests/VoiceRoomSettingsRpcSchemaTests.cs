using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceRoomSettingsRpcSchemaTests
{
    [Fact]
    public void CurrentSchemaRoundTripsEverySettingAndModOptionExactly()
    {
        var defaults = VoiceRoomSettingsSnapshot.Defaults;
        var source = defaults with
        {
            Backend = 17,
            BackendServerUrl = "reserved.example",
            MaxChatDistance = 12.25f,
            FalloffMode = (int)VoiceFalloffMode.VoiceFocused,
            OcclusionMode = (int)VoiceOcclusionMode.HardBlock,
            WallsBlockSound = !defaults.WallsBlockSound,
            OnlyHearInSight = !defaults.OnlyHearInSight,
            ImpostorHearGhosts = !defaults.ImpostorHearGhosts,
            HearInVent = !defaults.HearInVent,
            VentPrivateChat = !defaults.VentPrivateChat,
            CommsSabDisables = !defaults.CommsSabDisables,
            CameraCanHear = !defaults.CameraCanHear,
            TeamRadio = !defaults.TeamRadio,
            TeamRadioImpostors = !defaults.TeamRadioImpostors,
            TeamRadioVampires = !defaults.TeamRadioVampires,
            TeamRadioLovers = !defaults.TeamRadioLovers,
            OnlyGhostsCanTalk = !defaults.OnlyGhostsCanTalk,
            OnlyMeetingOrLobby = !defaults.OnlyMeetingOrLobby,
            OnlyMeetingOrLobbyAffectsGhosts = !defaults.OnlyMeetingOrLobbyAffectsGhosts,
            MuteBlackmailedInMeetings = !defaults.MuteBlackmailedInMeetings,
            MuteBlackmailedNextRound = !defaults.MuteBlackmailedNextRound,
            MuteJailedInMeetings = !defaults.MuteJailedInMeetings,
            JailorCanUnmuteJailed = !defaults.JailorCanUnmuteJailed,
            MuteParasiteControlled = !defaults.MuteParasiteControlled,
            MutePuppeteerControlled = !defaults.MutePuppeteerControlled,
            CrewpostorUsesImpostorVoice = !defaults.CrewpostorUsesImpostorVoice,
            MuteSwooperWhileSwooped = !defaults.MuteSwooperWhileSwooped,
            MediumGhostVoice = (int)MediumGhostVoiceMode.Both,
            MuteGlitchHacked = !defaults.MuteGlitchHacked,
            MuffleBlindedOrFlashedHearing = !defaults.MuffleBlindedOrFlashedHearing,
            MuffleHypnotizedDuringHysteria = !defaults.MuffleHypnotizedDuringHysteria,
            TeamRadioInMeetings = !defaults.TeamRadioInMeetings,
            PuppeteerHearFromVictim = !defaults.PuppeteerHearFromVictim,
            ParasiteHearFromVictim = !defaults.ParasiteHearFromVictim,
            TeamRadioInTasks = !defaults.TeamRadioInTasks,
            GhostsHearEachOtherUnlimited = !defaults.GhostsHearEachOtherUnlimited,
            JailPersistsAfterJailorDeath = !defaults.JailPersistsAfterJailorDeath,
            GracePeriodEnabled = !defaults.GracePeriodEnabled,
            GracePeriodSeconds = 11.5f,
        };
        var options = new[]
        {
            new VoiceRoomSettingsRpc.SyncedModOptionValue(101, IsEnum: false, Value: 1),
            new VoiceRoomSettingsRpc.SyncedModOptionValue(-202, IsEnum: true, Value: 7),
        };

        var payload = VoiceRoomSettingsRpc.EncodeSnapshotPayload(source, options);

        Assert.True(VoiceRoomSettingsRpc.TryDecodeSnapshotPayload(
            VoiceRoomSettingsRpc.SnapshotKind,
            payload,
            out var decoded,
            out var decodedOptions,
            out var reason), reason);
        Assert.Equal(source.Clamp(), decoded);
        Assert.Equal(options, decodedOptions);
        Assert.Equal(VoiceRoomSettingsRpc.SnapshotSchema, payload[0]);
        Assert.Equal(payload.Length - 3, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(1)));
    }

    [Fact]
    public void LegacyKindOneIsRejectedWithoutInterpretingItsAmbiguousPayload()
    {
        var payload = VoiceRoomSettingsRpc.EncodeSnapshotPayload(
            VoiceRoomSettingsSnapshot.Defaults,
            Array.Empty<VoiceRoomSettingsRpc.SyncedModOptionValue>());

        Assert.False(VoiceRoomSettingsRpc.TryDecodeSnapshotPayload(
            VoiceRoomSettingsRpc.LegacySnapshotKind,
            payload,
            out var decoded,
            out var options,
            out var reason));
        Assert.Equal("legacy-unversioned", reason);
        Assert.Equal(default, decoded);
        Assert.Empty(options);
    }

    [Fact]
    public void UnsupportedSchemaInvalidBooleanAndTrailingDataFailClosed()
    {
        var payload = VoiceRoomSettingsRpc.EncodeSnapshotPayload(
            VoiceRoomSettingsSnapshot.Defaults,
            Array.Empty<VoiceRoomSettingsRpc.SyncedModOptionValue>());

        var unsupported = (byte[])payload.Clone();
        unsupported[0] = checked((byte)(VoiceRoomSettingsRpc.SnapshotSchema + 1));
        AssertRejected(unsupported, "unsupported-schema");

        var invalidBoolean = (byte[])payload.Clone();
        invalidBoolean[3 + 16] = 2; // first boolean after four 32-bit fixed fields
        AssertRejected(invalidBoolean, "invalid-boolean");

        var trailing = new byte[payload.Length + 1];
        payload.CopyTo(trailing, 0);
        trailing[^1] = 0x5A;
        BinaryPrimitives.WriteUInt16LittleEndian(trailing.AsSpan(1), checked((ushort)(trailing.Length - 3)));
        AssertRejected(trailing, "invalid-mod-option-block");
    }

    [Fact]
    public void TruncatedOptionBlockReturnsNoPartialDecodedValues()
    {
        var options = new[]
        {
            new VoiceRoomSettingsRpc.SyncedModOptionValue(1, false, 1),
            new VoiceRoomSettingsRpc.SyncedModOptionValue(2, true, 2),
        };
        var valid = VoiceRoomSettingsRpc.EncodeSnapshotPayload(VoiceRoomSettingsSnapshot.Defaults, options);
        var truncated = valid.AsSpan(0, valid.Length - 1).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(truncated.AsSpan(1), checked((ushort)(truncated.Length - 3)));

        Assert.False(VoiceRoomSettingsRpc.TryDecodeSnapshotPayload(
            VoiceRoomSettingsRpc.SnapshotKind,
            truncated,
            out var decoded,
            out var decodedOptions,
            out var reason));
        Assert.Equal("invalid-mod-option-block", reason);
        Assert.Equal(default, decoded);
        Assert.Empty(decodedOptions);
    }

    [Fact]
    public void EncoderAndDecoderEnforcePayloadAndOptionBounds()
    {
        var tooMany = new List<VoiceRoomSettingsRpc.SyncedModOptionValue>();
        for (int i = 0; i < 257; i++)
            tooMany.Add(new VoiceRoomSettingsRpc.SyncedModOptionValue(i, false, i));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VoiceRoomSettingsRpc.EncodeSnapshotPayload(VoiceRoomSettingsSnapshot.Defaults, tooMany));

        var oversized = new byte[VoiceRoomSettingsRpc.MaxSnapshotPayloadBytes + 1];
        Assert.False(VoiceRoomSettingsRpc.TryDecodeSnapshotPayload(
            VoiceRoomSettingsRpc.SnapshotKind,
            oversized,
            out _,
            out var decodedOptions,
            out var reason));
        Assert.Equal("payload-oversized", reason);
        Assert.Empty(decodedOptions);
    }

    private static void AssertRejected(byte[] payload, string expectedReason)
    {
        Assert.False(VoiceRoomSettingsRpc.TryDecodeSnapshotPayload(
            VoiceRoomSettingsRpc.SnapshotKind,
            payload,
            out var decoded,
            out var options,
            out var reason));
        Assert.Equal(expectedReason, reason);
        Assert.Equal(default, decoded);
        Assert.Empty(options);
    }
}
