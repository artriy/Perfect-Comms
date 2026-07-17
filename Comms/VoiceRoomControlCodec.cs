using System;
using System.Buffers.Binary;

namespace VoiceChatPlugin.VoiceChat;

public enum VoiceRoomControlMessageKind : byte
{
    HostSettingsSnapshot = 1,
    HostSettingsRequest = 2,
}

public readonly record struct VoiceRoomControlMessage(
    VoiceRoomControlMessageKind Kind,
    VoiceRoomSettingsSnapshot Settings);

// NOTE: This codec is NOT the live wire path. Host voice settings + requests flow over the
// host-object-routed custom RPC (VoiceRoomSettingsRpc); the voice-transport side-channel that previously
// used this codec was de-authorized for security (a self-asserted sender could forge host settings).
// Object-owner checks on the live path are compatibility validation, not packet-source authentication.
// It is retained only for legacy side-channel fixtures and their versioned decode tests. It is not the
// schema for the live RPC: current host snapshots use the exact kind-3/schema-1 payload implemented by
// VoiceRoomSettingsRpc.EncodeSnapshotPayload/TryDecodeSnapshotPayload. Do not add new runtime callers.
public static class VoiceRoomControlCodec
{
    private const byte Magic0 = (byte)'P';
    private const byte Magic1 = (byte)'C';
    private const byte Version = 13;
    private const byte LegacyVersion12 = 12;
    private const byte LegacyVersion11 = 11;
    private const byte LegacyVersion10 = 10;
    private const byte LegacyVersion9 = 9;
    private const byte LegacyVersion8 = 8;
    private const byte LegacyVersion7 = 7;
    private const byte LegacyVersion6 = 6;
    private const byte LegacyVersion5 = 5;
    private const byte LegacyVersion4 = 4;
    private const int HeaderBytes = 4;
    private const int LegacyFixedSettingsBytesV4 = 4 + 4 + 4 + 4 + 17;
    private const int LegacyFixedSettingsBytesV5 = 4 + 4 + 4 + 4 + 18;
    private const int LegacyFixedSettingsBytesV6 = 4 + 4 + 4 + 4 + 21;
    private const int LegacyFixedSettingsBytesV7 = 4 + 4 + 4 + 4 + 21 + 4;
    private const int LegacyFixedSettingsBytesV8 = 4 + 4 + 4 + 4 + 21 + 4 + 1;
    private const int LegacyFixedSettingsBytesV9 = 4 + 4 + 4 + 4 + 21 + 4 + 3;
    private const int LegacyFixedSettingsBytesV10 = 4 + 4 + 4 + 4 + 21 + 4 + 4;
    private const int LegacyFixedSettingsBytesV11 = 4 + 4 + 4 + 4 + 21 + 4 + 5;
    private const int LegacyFixedSettingsBytesV12 = 4 + 4 + 4 + 4 + 21 + 4 + 7;
    private const int FixedSettingsBytes = 4 + 4 + 4 + 4 + 21 + 4 + 10;
    private const int MaxServerUrlBytes = 512;

    public static byte[] EncodeHostSettingsSnapshot(VoiceRoomSettingsSnapshot settings)
    {
        var serverUrlBytes = System.Text.Encoding.UTF8.GetBytes(settings.BackendServerUrl ?? string.Empty);
        if (serverUrlBytes.Length > MaxServerUrlBytes)
            serverUrlBytes = serverUrlBytes.AsSpan(0, MaxServerUrlBytes).ToArray();

        var buffer = new byte[HeaderBytes + FixedSettingsBytes + 2 + serverUrlBytes.Length];
        WriteHeader(buffer, VoiceRoomControlMessageKind.HostSettingsSnapshot);
        WriteSettings(buffer.AsSpan(HeaderBytes), settings.Clamp(), serverUrlBytes);
        return buffer;
    }

    public static byte[] EncodeHostSettingsRequest()
    {
        var buffer = new byte[HeaderBytes];
        WriteHeader(buffer, VoiceRoomControlMessageKind.HostSettingsRequest);
        return buffer;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out VoiceRoomControlMessage message)
    {
        message = default;
        if (payload.Length < HeaderBytes) return false;
        var version = payload[2];
        if (payload[0] != Magic0 || payload[1] != Magic1 || !IsSupportedVersion(version)) return false;

        var kind = (VoiceRoomControlMessageKind)payload[3];
        if (kind == VoiceRoomControlMessageKind.HostSettingsRequest)
        {
            if (payload.Length != HeaderBytes) return false;
            message = new VoiceRoomControlMessage(kind, default);
            return true;
        }

        if (kind == VoiceRoomControlMessageKind.HostSettingsSnapshot)
        {
            if (!TryReadSettings(payload[HeaderBytes..], version, out var settings)) return false;
            message = new VoiceRoomControlMessage(kind, settings);
            return true;
        }

        return false;
    }

    private static void WriteHeader(Span<byte> buffer, VoiceRoomControlMessageKind kind)
    {
        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Version;
        buffer[3] = (byte)kind;
    }

    private static void WriteSettings(Span<byte> buffer, VoiceRoomSettingsSnapshot settings, ReadOnlySpan<byte> serverUrlBytes)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, settings.Backend);
        BinaryPrimitives.WriteSingleLittleEndian(buffer[4..], settings.MaxChatDistance);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[8..], settings.FalloffMode);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[12..], settings.OcclusionMode);
        buffer[16] = ToByte(settings.WallsBlockSound);
        buffer[17] = ToByte(settings.OnlyHearInSight);
        buffer[18] = ToByte(settings.ImpostorHearGhosts);
        buffer[19] = ToByte(settings.HearInVent);
        buffer[20] = ToByte(settings.VentPrivateChat);
        buffer[21] = ToByte(settings.CommsSabDisables);
        buffer[22] = ToByte(settings.CameraCanHear);
        buffer[23] = ToByte(settings.TeamRadio);
        buffer[24] = ToByte(settings.TeamRadioImpostors);
        buffer[25] = ToByte(settings.TeamRadioVampires);
        buffer[26] = ToByte(settings.TeamRadioLovers);
        buffer[27] = ToByte(settings.OnlyGhostsCanTalk);
        buffer[28] = ToByte(settings.OnlyMeetingOrLobby);
        buffer[29] = ToByte(settings.MuteBlackmailedInMeetings);
        buffer[30] = ToByte(settings.MuteBlackmailedNextRound);
        buffer[31] = ToByte(settings.MuteJailedInMeetings);
        buffer[32] = ToByte(settings.JailorCanUnmuteJailed);
        buffer[33] = ToByte(settings.MuteParasiteControlled);
        buffer[34] = ToByte(settings.MutePuppeteerControlled);
        buffer[35] = ToByte(settings.CrewpostorUsesImpostorVoice);
        buffer[36] = ToByte(settings.MuteSwooperWhileSwooped);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[37..], settings.MediumGhostVoice);
        buffer[41] = ToByte(settings.MuteGlitchHacked);
        buffer[42] = ToByte(settings.MuffleBlindedOrFlashedHearing);
        buffer[43] = ToByte(settings.MuffleHypnotizedDuringHysteria);
        buffer[44] = ToByte(settings.OnlyMeetingOrLobbyAffectsGhosts);
        buffer[45] = ToByte(settings.TeamRadioInMeetings);
        buffer[46] = ToByte(settings.PuppeteerHearFromVictim);
        buffer[47] = ToByte(settings.ParasiteHearFromVictim);
        buffer[48] = ToByte(settings.TeamRadioInTasks);
        buffer[49] = ToByte(settings.GhostsHearEachOtherUnlimited);
        buffer[50] = ToByte(settings.JailPersistsAfterJailorDeath);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[FixedSettingsBytes..], checked((ushort)serverUrlBytes.Length));
        serverUrlBytes.CopyTo(buffer[(FixedSettingsBytes + 2)..]);
    }

    private static bool TryReadSettings(ReadOnlySpan<byte> buffer, byte version, out VoiceRoomSettingsSnapshot settings)
    {
        settings = default;
        var fixedSettingsBytes = FixedSettingsBytesForVersion(version);
        if (fixedSettingsBytes < 0) return false; // fail closed: unknown/unmapped version
        if (buffer.Length < fixedSettingsBytes + 2) return false;
        var serverUrlLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer[fixedSettingsBytes..]);
        if (serverUrlLength > MaxServerUrlBytes || buffer.Length != fixedSettingsBytes + 2 + serverUrlLength) return false;
        var serverUrl = System.Text.Encoding.UTF8.GetString(buffer.Slice(fixedSettingsBytes + 2, serverUrlLength));
        // Version 10 shares V11's exact byte layout minus the final TeamRadioInMeetings byte, so it
        // carries the team-radio sub-settings, MediumGhostVoice, MuteGlitchHacked and listener-muffle
        // bytes (tailOffset 27). The earlier flags omitted V10 from all of these, decoding it with the
        // pre-subsettings offset (24) and 3-byte field shift; that mis-aligned every trailing setting.
        bool hasTeamRadioSubSettings = version == Version || version == LegacyVersion12 || version == LegacyVersion11 || version == LegacyVersion10 || version == LegacyVersion9 || version == LegacyVersion8 || version == LegacyVersion7 || version == LegacyVersion6;
        int tailOffset = hasTeamRadioSubSettings ? 27 : 24;
        bool hasMediumGhostVoice = version == Version || version == LegacyVersion12 || version == LegacyVersion11 || version == LegacyVersion10 || version == LegacyVersion9 || version == LegacyVersion8 || version == LegacyVersion7;
        bool hasMuteGlitchHacked = version == Version || version == LegacyVersion12 || version == LegacyVersion11 || version == LegacyVersion10 || version == LegacyVersion9 || version == LegacyVersion8;
        bool hasListenerMuffleSettings = version == Version || version == LegacyVersion12 || version == LegacyVersion11 || version == LegacyVersion10 || version == LegacyVersion9;
        bool hasMeetingLobbyGhostSetting = version == Version || version == LegacyVersion12 || version == LegacyVersion11 || version == LegacyVersion10;
        bool hasTeamRadioInMeetings = version == Version || version == LegacyVersion12 || version == LegacyVersion11;
        bool hasPuppeteerHearFromVictim = version == Version || version == LegacyVersion12;
        bool hasParasiteHearFromVictim = version == Version || version == LegacyVersion12;
        bool hasV13Settings = version == Version;
        settings = new VoiceRoomSettingsSnapshot(
            BinaryPrimitives.ReadInt32LittleEndian(buffer),
            serverUrl,
            BinaryPrimitives.ReadSingleLittleEndian(buffer[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(buffer[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(buffer[12..]),
            buffer[16] != 0,
            buffer[17] != 0,
            buffer[18] != 0,
            buffer[19] != 0,
            buffer[20] != 0,
            buffer[21] != 0,
            buffer[22] != 0,
            buffer[23] != 0,
            !hasTeamRadioSubSettings || buffer[24] != 0,
            hasTeamRadioSubSettings && buffer[25] != 0,
            hasTeamRadioSubSettings && buffer[26] != 0,
            buffer[tailOffset] != 0,
            buffer[tailOffset + 1] != 0,
            hasMeetingLobbyGhostSetting ? buffer[tailOffset + 17] != 0 : true,
            buffer[tailOffset + 2] != 0,
            buffer[tailOffset + 3] != 0,
            buffer[tailOffset + 4] != 0,
            buffer[tailOffset + 5] != 0,
            buffer[tailOffset + 6] != 0,
            buffer[tailOffset + 7] != 0,
            buffer[tailOffset + 8] != 0,
            version == LegacyVersion4 || buffer[tailOffset + 9] != 0,
            hasMediumGhostVoice
                ? BinaryPrimitives.ReadInt32LittleEndian(buffer[(tailOffset + 10)..])
                : (int)MediumGhostVoiceMode.None,
            hasMuteGlitchHacked ? buffer[tailOffset + 14] != 0 : true,
            hasListenerMuffleSettings ? buffer[tailOffset + 15] != 0 : true,
            hasListenerMuffleSettings ? buffer[tailOffset + 16] != 0 : true,
            hasTeamRadioInMeetings && buffer[tailOffset + 18] != 0,
            hasPuppeteerHearFromVictim && buffer[tailOffset + 19] != 0,
            hasParasiteHearFromVictim && buffer[tailOffset + 20] != 0,
            !hasV13Settings || buffer[tailOffset + 21] != 0,
            hasV13Settings && buffer[tailOffset + 22] != 0,
            hasV13Settings && buffer[tailOffset + 23] != 0,
            false,
            5f).Clamp();
        return true;
    }

    private static bool IsSupportedVersion(byte version)
        => version is Version or LegacyVersion12 or LegacyVersion11 or LegacyVersion10 or LegacyVersion9 or LegacyVersion8 or LegacyVersion7 or LegacyVersion6 or LegacyVersion5 or LegacyVersion4;

    private static int FixedSettingsBytesForVersion(byte version)
        => version switch
        {
            LegacyVersion4 => LegacyFixedSettingsBytesV4,
            LegacyVersion5 => LegacyFixedSettingsBytesV5,
            LegacyVersion6 => LegacyFixedSettingsBytesV6,
            LegacyVersion7 => LegacyFixedSettingsBytesV7,
            LegacyVersion8 => LegacyFixedSettingsBytesV8,
            LegacyVersion9 => LegacyFixedSettingsBytesV9,
            LegacyVersion10 => LegacyFixedSettingsBytesV10,
            LegacyVersion11 => LegacyFixedSettingsBytesV11,
            LegacyVersion12 => LegacyFixedSettingsBytesV12,
            Version => FixedSettingsBytes,
            _ => -1, // fail closed: reject unknown versions
        };

    private static byte ToByte(bool value) => value ? (byte)1 : (byte)0;
}
