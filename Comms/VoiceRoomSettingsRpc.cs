using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Hazel;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceRoomSettingsRpc
{
    private const byte RpcId = 203;
    internal const byte LegacySnapshotKind = 1;
    private const byte RequestKind = 2;
    internal const byte SnapshotKind = 3;
    internal const byte SnapshotSchema = 1;
    private const int MaxSyncedModOptions = 256;

    // Schema 1 is a self-contained, exact binary layout. The old kind-1 envelope had no
    // schema marker and was extended in-place repeatedly; its remaining-byte heuristics can
    // misread later fields at earlier offsets, so it must never enter the current decoder.
    private const int SnapshotHeaderBytes = 3; // schema:byte + bodyLength:ushort
    private const int FixedSettingsBytes = 56;
    private const int ModOptionBytes = 9; // keyHash:int + isEnum:byte + value:int
    private const int MaxBackendServerUrlBytes = 512;
    internal const int MaxSnapshotPayloadBytes = SnapshotHeaderBytes + FixedSettingsBytes + 2
        + MaxBackendServerUrlBytes + 2 + MaxSyncedModOptions * ModOptionBytes;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal readonly record struct SyncedModOptionValue(int Hash, bool IsEnum, int Value);

    public static bool TrySendSnapshot(VoiceRoomSettingsSnapshot settings, int targetClientId = -1)
    {
        if (!TryStartWriter(targetClientId, "snapshot", out var writer)) return false;
        try
        {
            writer.Write(SnapshotKind);
            writer.Write(EncodeSnapshotPayload(settings, CollectModOptions()));
            return TryFinishWriter(writer, targetClientId, "snapshot");
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log(
                "settings.rpc.send_failed",
                $"op=snapshot target={targetClientId} errorType={ex.GetType().Name} error=\"{Safe(ex.Message)}\"");
            return false;
        }
    }

    public static bool TrySendRequest(int targetClientId = -1)
    {
        if (!TryStartWriter(targetClientId, "request", out var writer)) return false;
        try
        {
            writer.Write(RequestKind);
            return TryFinishWriter(writer, targetClientId, "request");
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log(
                "settings.rpc.send_failed",
                $"op=request target={targetClientId} errorType={ex.GetType().Name} error=\"{Safe(ex.Message)}\"");
            return false;
        }
    }

    private static bool TryStartWriter(int targetClientId, string operation, out MessageWriter writer)
    {
        writer = null!;
        if (targetClientId < -1)
        {
            VoiceDiagnostics.Log("settings.rpc.send_deferred", $"op={operation} target={targetClientId} reason=invalid-target");
            return false;
        }

        var client = AmongUsClient.Instance;
        if (client == null)
        {
            VoiceDiagnostics.Log("settings.rpc.send_deferred", $"op={operation} target={targetClientId} reason=client-unavailable");
            return false;
        }

        var localPlayer = PlayerControl.LocalPlayer;
        if (localPlayer == null)
        {
            VoiceDiagnostics.Log("settings.rpc.send_deferred", $"op={operation} target={targetClientId} reason=local-player-unavailable");
            return false;
        }

        try
        {
            writer = client.StartRpcImmediately(
                localPlayer.NetId,
                RpcId,
                SendOption.Reliable,
                targetClientId);
            return writer != null;
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log(
                "settings.rpc.send_failed",
                $"op={operation} target={targetClientId} stage=start errorType={ex.GetType().Name} error=\"{Safe(ex.Message)}\"");
            return false;
        }
    }

    private static bool TryFinishWriter(MessageWriter writer, int targetClientId, string operation)
    {
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null)
            {
                VoiceDiagnostics.Log("settings.rpc.send_deferred", $"op={operation} target={targetClientId} reason=client-lost-before-finish");
                return false;
            }
            client.FinishRpcImmediately(writer);
            return true;
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log(
                "settings.rpc.send_failed",
                $"op={operation} target={targetClientId} stage=finish errorType={ex.GetType().Name} error=\"{Safe(ex.Message)}\"");
            return false;
        }
    }

    private static string Safe(string? value)
        => (value ?? string.Empty).Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');

    private static List<SyncedModOptionValue> CollectModOptions()
    {
        var result = new List<SyncedModOptionValue>();
        foreach (var entry in VoiceModRegistry.SyncedValues())
        {
            if (result.Count >= MaxSyncedModOptions)
                throw new InvalidOperationException($"too many synced mod options (max={MaxSyncedModOptions})");
            result.Add(new SyncedModOptionValue(entry.Hash, entry.IsEnum, entry.Value));
        }
        return result;
    }

    internal static byte[] EncodeSnapshotPayload(
        VoiceRoomSettingsSnapshot settings,
        IReadOnlyList<SyncedModOptionValue> modOptions)
    {
        if (modOptions == null) throw new ArgumentNullException(nameof(modOptions));
        if (modOptions.Count > MaxSyncedModOptions)
            throw new ArgumentOutOfRangeException(nameof(modOptions), $"at most {MaxSyncedModOptions} values are allowed");

        settings = NormalizeForWire(settings);
        var serverUrlBytes = StrictUtf8.GetBytes(settings.BackendServerUrl ?? string.Empty);
        if (serverUrlBytes.Length > MaxBackendServerUrlBytes)
            throw new ArgumentOutOfRangeException(nameof(settings), $"backend URL exceeds {MaxBackendServerUrlBytes} UTF-8 bytes");

        int payloadLength = SnapshotHeaderBytes + FixedSettingsBytes + 2 + serverUrlBytes.Length
            + 2 + modOptions.Count * ModOptionBytes;
        var payload = new byte[payloadLength];
        payload[0] = SnapshotSchema;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), checked((ushort)(payloadLength - SnapshotHeaderBytes)));

        int offset = SnapshotHeaderBytes;
        WriteInt32(payload, ref offset, settings.Backend);
        WriteSingle(payload, ref offset, settings.MaxChatDistance);
        WriteInt32(payload, ref offset, settings.FalloffMode);
        WriteInt32(payload, ref offset, settings.OcclusionMode);
        WriteBoolean(payload, ref offset, settings.WallsBlockSound);
        WriteBoolean(payload, ref offset, settings.OnlyHearInSight);
        WriteBoolean(payload, ref offset, settings.ImpostorHearGhosts);
        WriteBoolean(payload, ref offset, settings.HearInVent);
        WriteBoolean(payload, ref offset, settings.VentPrivateChat);
        WriteBoolean(payload, ref offset, settings.CommsSabDisables);
        WriteBoolean(payload, ref offset, settings.CameraCanHear);
        WriteBoolean(payload, ref offset, settings.TeamRadio);
        WriteBoolean(payload, ref offset, settings.TeamRadioImpostors);
        WriteBoolean(payload, ref offset, settings.TeamRadioVampires);
        WriteBoolean(payload, ref offset, settings.TeamRadioLovers);
        WriteBoolean(payload, ref offset, settings.OnlyGhostsCanTalk);
        WriteBoolean(payload, ref offset, settings.OnlyMeetingOrLobby);
        WriteBoolean(payload, ref offset, settings.OnlyMeetingOrLobbyAffectsGhosts);
        WriteBoolean(payload, ref offset, settings.MuteBlackmailedInMeetings);
        WriteBoolean(payload, ref offset, settings.MuteBlackmailedNextRound);
        WriteBoolean(payload, ref offset, settings.MuteJailedInMeetings);
        WriteBoolean(payload, ref offset, settings.JailorCanUnmuteJailed);
        WriteBoolean(payload, ref offset, settings.MuteParasiteControlled);
        WriteBoolean(payload, ref offset, settings.MutePuppeteerControlled);
        WriteBoolean(payload, ref offset, settings.CrewpostorUsesImpostorVoice);
        WriteBoolean(payload, ref offset, settings.MuteSwooperWhileSwooped);
        WriteInt32(payload, ref offset, settings.MediumGhostVoice);
        WriteBoolean(payload, ref offset, settings.MuteGlitchHacked);
        WriteBoolean(payload, ref offset, settings.MuffleBlindedOrFlashedHearing);
        WriteBoolean(payload, ref offset, settings.MuffleHypnotizedDuringHysteria);
        WriteBoolean(payload, ref offset, settings.TeamRadioInMeetings);
        WriteBoolean(payload, ref offset, settings.PuppeteerHearFromVictim);
        WriteBoolean(payload, ref offset, settings.ParasiteHearFromVictim);
        WriteBoolean(payload, ref offset, settings.TeamRadioInTasks);
        WriteBoolean(payload, ref offset, settings.GhostsHearEachOtherUnlimited);
        WriteBoolean(payload, ref offset, settings.JailPersistsAfterJailorDeath);
        WriteBoolean(payload, ref offset, settings.GracePeriodEnabled);
        WriteSingle(payload, ref offset, settings.GracePeriodSeconds);

        if (offset != SnapshotHeaderBytes + FixedSettingsBytes)
            throw new InvalidOperationException("settings schema size invariant failed");

        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), checked((ushort)serverUrlBytes.Length));
        offset += 2;
        serverUrlBytes.CopyTo(payload.AsSpan(offset));
        offset += serverUrlBytes.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), checked((ushort)modOptions.Count));
        offset += 2;

        for (int i = 0; i < modOptions.Count; i++)
        {
            var value = modOptions[i];
            WriteInt32(payload, ref offset, value.Hash);
            WriteBoolean(payload, ref offset, value.IsEnum);
            WriteInt32(payload, ref offset, value.Value);
        }

        if (offset != payload.Length)
            throw new InvalidOperationException("snapshot payload size invariant failed");
        return payload;
    }

    internal static bool TryDecodeSnapshotPayload(
        byte kind,
        ReadOnlySpan<byte> payload,
        out VoiceRoomSettingsSnapshot settings,
        out List<SyncedModOptionValue> modOptions,
        out string reason)
    {
        settings = default;
        modOptions = new List<SyncedModOptionValue>();
        reason = string.Empty;

        if (kind == LegacySnapshotKind)
        {
            reason = "legacy-unversioned";
            return false;
        }
        if (kind != SnapshotKind)
        {
            reason = "unknown-kind";
            return false;
        }
        if (payload.Length > MaxSnapshotPayloadBytes)
        {
            reason = "payload-oversized";
            return false;
        }
        if (payload.Length < SnapshotHeaderBytes)
        {
            reason = "header-truncated";
            return false;
        }
        if (payload[0] != SnapshotSchema)
        {
            reason = "unsupported-schema";
            return false;
        }
        int declaredBodyLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
        if (declaredBodyLength != payload.Length - SnapshotHeaderBytes)
        {
            reason = "body-length-mismatch";
            return false;
        }
        if (payload.Length < SnapshotHeaderBytes + FixedSettingsBytes + 4)
        {
            reason = "settings-truncated";
            return false;
        }

        int offset = SnapshotHeaderBytes;
        int backend = ReadInt32(payload, ref offset);
        float maxChatDistance = ReadSingle(payload, ref offset);
        int falloffMode = ReadInt32(payload, ref offset);
        int occlusionMode = ReadInt32(payload, ref offset);
        if (!TryReadBoolean(payload, ref offset, out bool wallsBlockSound)
            || !TryReadBoolean(payload, ref offset, out bool onlyHearInSight)
            || !TryReadBoolean(payload, ref offset, out bool impostorHearGhosts)
            || !TryReadBoolean(payload, ref offset, out bool hearInVent)
            || !TryReadBoolean(payload, ref offset, out bool ventPrivateChat)
            || !TryReadBoolean(payload, ref offset, out bool commsSabDisables)
            || !TryReadBoolean(payload, ref offset, out bool cameraCanHear)
            || !TryReadBoolean(payload, ref offset, out bool teamRadio)
            || !TryReadBoolean(payload, ref offset, out bool teamRadioImpostors)
            || !TryReadBoolean(payload, ref offset, out bool teamRadioVampires)
            || !TryReadBoolean(payload, ref offset, out bool teamRadioLovers)
            || !TryReadBoolean(payload, ref offset, out bool onlyGhostsCanTalk)
            || !TryReadBoolean(payload, ref offset, out bool onlyMeetingOrLobby)
            || !TryReadBoolean(payload, ref offset, out bool onlyMeetingOrLobbyAffectsGhosts)
            || !TryReadBoolean(payload, ref offset, out bool muteBlackmailedInMeetings)
            || !TryReadBoolean(payload, ref offset, out bool muteBlackmailedNextRound)
            || !TryReadBoolean(payload, ref offset, out bool muteJailedInMeetings)
            || !TryReadBoolean(payload, ref offset, out bool jailorCanUnmuteJailed)
            || !TryReadBoolean(payload, ref offset, out bool muteParasiteControlled)
            || !TryReadBoolean(payload, ref offset, out bool mutePuppeteerControlled)
            || !TryReadBoolean(payload, ref offset, out bool crewpostorUsesImpostorVoice)
            || !TryReadBoolean(payload, ref offset, out bool muteSwooperWhileSwooped))
        {
            reason = "invalid-boolean";
            return false;
        }

        int mediumGhostVoice = ReadInt32(payload, ref offset);
        if (!TryReadBoolean(payload, ref offset, out bool muteGlitchHacked)
            || !TryReadBoolean(payload, ref offset, out bool muffleBlindedOrFlashedHearing)
            || !TryReadBoolean(payload, ref offset, out bool muffleHypnotizedDuringHysteria)
            || !TryReadBoolean(payload, ref offset, out bool teamRadioInMeetings)
            || !TryReadBoolean(payload, ref offset, out bool puppeteerHearFromVictim)
            || !TryReadBoolean(payload, ref offset, out bool parasiteHearFromVictim)
            || !TryReadBoolean(payload, ref offset, out bool teamRadioInTasks)
            || !TryReadBoolean(payload, ref offset, out bool ghostsHearEachOtherUnlimited)
            || !TryReadBoolean(payload, ref offset, out bool jailPersistsAfterJailorDeath)
            || !TryReadBoolean(payload, ref offset, out bool gracePeriodEnabled))
        {
            reason = "invalid-boolean";
            return false;
        }
        float gracePeriodSeconds = ReadSingle(payload, ref offset);
        if (!float.IsFinite(maxChatDistance) || !float.IsFinite(gracePeriodSeconds))
        {
            reason = "non-finite-number";
            return false;
        }
        if (offset != SnapshotHeaderBytes + FixedSettingsBytes)
        {
            reason = "settings-size-mismatch";
            return false;
        }

        int serverUrlLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
        offset += 2;
        if (serverUrlLength > MaxBackendServerUrlBytes || payload.Length - offset < serverUrlLength + 2)
        {
            reason = "invalid-backend-url-length";
            return false;
        }

        string backendServerUrl;
        try
        {
            backendServerUrl = StrictUtf8.GetString(payload.Slice(offset, serverUrlLength));
        }
        catch (DecoderFallbackException)
        {
            reason = "invalid-backend-url-utf8";
            return false;
        }
        offset += serverUrlLength;

        int modOptionCount = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
        offset += 2;
        if (modOptionCount > MaxSyncedModOptions
            || payload.Length - offset != modOptionCount * ModOptionBytes)
        {
            reason = "invalid-mod-option-block";
            return false;
        }

        var parsedOptions = new List<SyncedModOptionValue>(modOptionCount);
        for (int i = 0; i < modOptionCount; i++)
        {
            int hash = ReadInt32(payload, ref offset);
            if (!TryReadBoolean(payload, ref offset, out bool isEnum))
            {
                reason = "invalid-mod-option-kind";
                return false;
            }
            int value = ReadInt32(payload, ref offset);
            parsedOptions.Add(new SyncedModOptionValue(hash, isEnum, value));
        }
        if (offset != payload.Length)
        {
            reason = "trailing-data";
            return false;
        }

        settings = new VoiceRoomSettingsSnapshot(
            backend,
            backendServerUrl,
            maxChatDistance,
            falloffMode,
            occlusionMode,
            wallsBlockSound,
            onlyHearInSight,
            impostorHearGhosts,
            hearInVent,
            ventPrivateChat,
            commsSabDisables,
            cameraCanHear,
            teamRadio,
            teamRadioImpostors,
            teamRadioVampires,
            teamRadioLovers,
            onlyGhostsCanTalk,
            onlyMeetingOrLobby,
            onlyMeetingOrLobbyAffectsGhosts,
            muteBlackmailedInMeetings,
            muteBlackmailedNextRound,
            muteJailedInMeetings,
            jailorCanUnmuteJailed,
            muteParasiteControlled,
            mutePuppeteerControlled,
            crewpostorUsesImpostorVoice,
            muteSwooperWhileSwooped,
            mediumGhostVoice,
            muteGlitchHacked,
            muffleBlindedOrFlashedHearing,
            muffleHypnotizedDuringHysteria,
            teamRadioInMeetings,
            puppeteerHearFromVictim,
            parasiteHearFromVictim,
            teamRadioInTasks,
            ghostsHearEachOtherUnlimited,
            jailPersistsAfterJailorDeath,
            gracePeriodEnabled,
            gracePeriodSeconds).Clamp();
        modOptions = parsedOptions;
        return true;
    }

    internal static bool ApplyDecodedModOptions(
        bool trusted,
        System.Collections.Generic.IReadOnlyList<SyncedModOptionValue> values,
        System.Action<int, bool, int> apply)
    {
        if (!trusted) return false;
        for (int i = 0; i < values.Count; i++)
        {
            var value = values[i];
            apply(value.Hash, value.IsEnum, value.Value);
        }
        return true;
    }

    private static VoiceRoomSettingsSnapshot NormalizeForWire(VoiceRoomSettingsSnapshot settings)
    {
        var normalized = settings.Clamp();
        var defaults = VoiceRoomSettingsSnapshot.Defaults;
        if (!float.IsFinite(normalized.MaxChatDistance))
            normalized = normalized with { MaxChatDistance = defaults.MaxChatDistance };
        if (!float.IsFinite(normalized.GracePeriodSeconds))
            normalized = normalized with { GracePeriodSeconds = defaults.GracePeriodSeconds };
        return normalized;
    }

    private static void WriteInt32(byte[] buffer, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), value);
        offset += 4;
    }

    private static int ReadInt32(ReadOnlySpan<byte> buffer, ref int offset)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
        offset += 4;
        return value;
    }

    private static void WriteSingle(byte[] buffer, ref int offset, float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset), value);
        offset += 4;
    }

    private static float ReadSingle(ReadOnlySpan<byte> buffer, ref int offset)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(buffer[offset..]);
        offset += 4;
        return value;
    }

    private static void WriteBoolean(byte[] buffer, ref int offset, bool value)
        => buffer[offset++] = value ? (byte)1 : (byte)0;

    private static bool TryReadBoolean(ReadOnlySpan<byte> buffer, ref int offset, out bool value)
    {
        byte raw = buffer[offset++];
        value = raw != 0;
        return raw <= 1;
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
    private static class PlayerControlHandleRpcPatch
    {
        public static void Postfix(PlayerControl __instance, byte callId, MessageReader reader)
        {
            if (callId != RpcId) return;

            try
            {
                var kind = reader.ReadByte();
                if (kind == SnapshotKind || kind == LegacySnapshotKind)
                {
                    if (AmongUsClient.Instance?.AmHost == true) return;
                    if (!VoiceHostAuthority.IsTrustedHostSender(__instance,
                            VoiceChatRoom.Current?.CurrentSnapshot,
                            "rpc",
                            out var sender,
                            out var authReason,
                            out var hostClientId,
                            out var hostPlayerId))
                    {
                        VoiceDiagnostics.Log("settings.snapshot.rejected",
                            $"{sender.ToDiagnosticFields()} reason={authReason} hostClient={hostClientId} hostPlayer={hostPlayerId}");
                        // Stale host id (e.g. post-migration): re-request a fresh snapshot.
                        VoiceChatRoom.NoteHostSettingsSnapshotRejected();
                        return;
                    }

                    // The unversioned kind-1 payload was extended in-place across many releases.
                    // It is impossible to distinguish all of those layouts safely, so a trusted
                    // legacy host is asked for a compatible snapshot instead of applying shifted
                    // settings. Current payloads are bounded before allocating their byte buffer.
                    if (kind == LegacySnapshotKind || reader.BytesRemaining > MaxSnapshotPayloadBytes)
                    {
                        string wireReason = kind == LegacySnapshotKind ? "legacy-unversioned" : "payload-oversized";
                        VoiceDiagnostics.Log("settings.snapshot.rejected",
                            $"{sender.ToDiagnosticFields()} reason={wireReason} kind={kind} hostClient={hostClientId} hostPlayer={hostPlayerId}");
                        VoiceChatRoom.NoteHostSettingsSnapshotRejected();
                        return;
                    }

                    var payload = reader.ReadBytes(reader.BytesRemaining);
                    if (!TryDecodeSnapshotPayload(kind, payload, out var settings, out var modOptions, out var decodeReason))
                    {
                        VoiceDiagnostics.Log("settings.snapshot.rejected",
                            $"{sender.ToDiagnosticFields()} reason={decodeReason} kind={kind} hostClient={hostClientId} hostPlayer={hostPlayerId}");
                        VoiceChatRoom.NoteHostSettingsSnapshotRejected();
                        return;
                    }

                    // Everything is still inert here. Mutate host-synced state only after both
                    // sender authorization and complete, exact schema validation have succeeded.
                    VoiceRoomSettingsState.ApplyRemote(settings, AmongUsClient.Instance?.GameId ?? 0);
                    VoiceModRegistry.BeginRemoteSync();
                    ApplyDecodedModOptions(trusted: true, modOptions, VoiceModRegistry.ApplySyncedValue);
                    VoiceChatRoom.NoteHostSettingsSnapshotApplied("rpc", hostClientId, hostPlayerId);
                    VoiceDiagnostics.Log("settings.snapshot.applied",
                        $"{sender.ToDiagnosticFields()} kind=host-snapshot schema={SnapshotSchema} hostClient={hostClientId} hostPlayer={hostPlayerId}");
                    return;
                }

                if (kind == RequestKind && AmongUsClient.Instance?.AmHost == true)
                    VoiceChatRoom.RespondToHostSettingsRequest(VoiceHostAuthority.FromPlayer(__instance, "rpc"));
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("settings.rpc.error", $"error=\"{ex.Message}\"");
            }
        }
    }
}
