using System;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using InnerNet;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

// Reactor-style version gate. The host rejects any client that does not announce
// the SAME Perfect Comms version. A client with no mod never sends a handshake,
// so it is treated as a mismatch and removed before it can play.
//
// ponytail: version is a plaintext string, no HMAC. A client-only mod cannot keep
// a real secret (the key ships in the binary), so the old HMAC was enforcement
// theater. This gates honest version mismatches, which is all it can ever do.
internal static class VoiceJoinGuard
{
    private const uint Magic = 0x50435631;
    private const byte MagicB0 = 0x31;
    private const byte MagicB1 = 0x56;
    private const byte MagicB2 = 0x43;
    private const byte MagicB3 = 0x50;
    private const byte SubmessageTag = byte.MaxValue;
    private const byte FlagHandshake = 1;
    private const byte FlagKickReason = 2;

    // No-handshake = no mod. Keep this just long enough for one round-trip so a
    // legit client's early handshake lands first; a modless client is gone fast.
    private const float GraceSeconds = 3f;
    private const float SendInterval = 0.5f;
    private const int MaxSends = 8;
    private const int MaxKicks = 3;
    private const float RekickInterval = 3f;

    private static readonly Dictionary<int, float> Pending = new();
    private static readonly Dictionary<int, string> PendingKick = new();
    private static readonly HashSet<int> Cleared = new();
    private static readonly Dictionary<int, float> KickTime = new();
    private static readonly Dictionary<int, int> KickCount = new();
    private static readonly List<int> Scratch = new();

    private static int _sentCount;
    private static float _lastSend = -999f;
    private static string? _pendingKickReason;
    private static bool _loggedActive;
    private static float _lastHeartbeat = -999f;

    private static void Dbg(string msg) => VoiceChatPluginMain.Logger.LogMessage("[JoinGuard] " + msg);

    public static void Reset()
    {
        Pending.Clear();
        PendingKick.Clear();
        Cleared.Clear();
        KickTime.Clear();
        KickCount.Clear();
        _sentCount = 0;
        _lastSend = -999f;
        _pendingKickReason = null;
        Dbg("reset");
    }

    public static void Tick()
    {
        var client = AmongUsClient.Instance;
        if (client == null) return;

        if (!_loggedActive)
        {
            _loggedActive = true;
            Dbg($"active ver={VoiceChatPluginMain.Version}");
        }

        if (!client.AmHost) { ClientTick(client); return; }
        HostTick(client);
    }

    private static void ClientTick(InnerNetClient client)
    {
        if (Time.time - _lastHeartbeat > 3f)
        {
            _lastHeartbeat = Time.time;
            Dbg($"client heartbeat sent={_sentCount} clientId={client.ClientId} hostId={client.HostId}");
        }

        if (_sentCount >= MaxSends) return;
        if (Time.time - _lastSend < SendInterval) return;
        if (SendHandshake(client)) { _lastSend = Time.time; _sentCount++; }
    }

    private static void HostTick(InnerNetClient client)
    {
        try
        {
            foreach (var c in client.allClients)
            {
                if (c == null) continue;
                int id = c.Id;
                if (id == client.ClientId || Cleared.Contains(id) || PendingKick.ContainsKey(id)) continue;
                if (KickCount.TryGetValue(id, out var kc) && kc >= MaxKicks) continue;
                if (!Pending.ContainsKey(id)) { Pending[id] = Time.time; Dbg($"track id={id}"); }
            }
        }
        catch (Exception e) { Dbg("allClients scan error: " + e.Message); }

        if (Time.time - _lastHeartbeat > 3f)
        {
            _lastHeartbeat = Time.time;
            Dbg($"host heartbeat pending={Pending.Count} kick={PendingKick.Count} cleared={Cleared.Count}");
        }

        Scratch.Clear();
        Scratch.AddRange(Pending.Keys);
        foreach (var id in Scratch)
        {
            if (Cleared.Contains(id)) { Pending.Remove(id); continue; }
            if (client.FindClientById(id) == null) { Forget(id); continue; }
            if (Time.time - Pending[id] >= GraceSeconds)
            {
                Pending.Remove(id);
                if (!PendingKick.ContainsKey(id)) { PendingKick[id] = MissingMessage(); Dbg($"queue-kick id={id} (no handshake)"); }
            }
        }

        Scratch.Clear();
        Scratch.AddRange(PendingKick.Keys);
        foreach (var id in Scratch)
        {
            var cd = client.FindClientById(id);
            if (cd == null) { Forget(id); continue; }
            TryKick(client, id, PendingKick[id]);
            if (KickCount.TryGetValue(id, out var kc) && kc >= MaxKicks) PendingKick.Remove(id);
        }
    }

    private static bool SendHandshake(InnerNetClient client)
    {
        if (client.ClientId < 0 || client.HostId < 0) return false;
        try
        {
            string version = VoiceChatPluginMain.Version;

            var writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage((byte) Tags.GameDataTo);
            writer.Write(client.GameId);
            writer.WritePacked(client.HostId);
            writer.StartMessage(SubmessageTag);
            writer.Write(Magic);
            writer.Write(FlagHandshake);
            writer.Write(version);
            writer.WritePacked(client.ClientId);
            writer.EndMessage();
            writer.EndMessage();
            client.SendOrDisconnect(writer);
            writer.Recycle();

            Dbg($"sent handshake ver={version} clientId={client.ClientId} hostId={client.HostId}");
            return true;
        }
        catch (Exception ex)
        {
            Dbg("send error: " + ex.Message);
            return false;
        }
    }

    private static void TryKick(InnerNetClient client, int targetId, string reason)
    {
        if (KickCount.TryGetValue(targetId, out var count) && count >= MaxKicks) { Pending.Remove(targetId); return; }
        if (KickTime.TryGetValue(targetId, out var last) && Time.time - last < RekickInterval) return;

        KickTime[targetId] = Time.time;
        KickCount[targetId] = (KickCount.TryGetValue(targetId, out var c) ? c : 0) + 1;
        Pending.Remove(targetId);

        try
        {
            var writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage((byte) Tags.GameDataTo);
            writer.Write(client.GameId);
            writer.WritePacked(targetId);
            writer.StartMessage(SubmessageTag);
            writer.Write(Magic);
            writer.Write(FlagKickReason);
            writer.WritePacked(targetId);
            writer.Write(reason);
            writer.EndMessage();
            writer.EndMessage();
            client.SendOrDisconnect(writer);
            writer.Recycle();

            Dbg($"KICK id={targetId} attempt={KickCount[targetId]}");
            client.KickPlayer(targetId, false);
        }
        catch (Exception ex)
        {
            Dbg("kick error: " + ex.Message);
        }
    }

    private static void Forget(int id)
    {
        Pending.Remove(id);
        PendingKick.Remove(id);
        Cleared.Remove(id);
        KickTime.Remove(id);
        KickCount.Remove(id);
    }

    private static void Scan(InnerNetClient inc, MessageReader reader, string src)
    {
        var buf = reader.Buffer;
        if (buf == null) return;
        int start = reader.Offset;
        int end = reader.Offset + reader.Length;
        if (end > buf.Length) end = buf.Length;

        for (int i = start; i + 5 <= end; i++)
        {
            if (buf[i] != SubmessageTag) continue;
            if (buf[i + 1] != MagicB0 || buf[i + 2] != MagicB1 || buf[i + 3] != MagicB2 || buf[i + 4] != MagicB3) continue;

            var r = MessageReader.Get(buf);
            try
            {
                r.Offset = 0;
                r.Length = buf.Length;
                r.Position = i + 5;
                byte flag = r.ReadByte();
                if (flag == FlagHandshake) OnHandshake(inc, r);
                else if (flag == FlagKickReason) OnKickReason(inc, r);
            }
            catch (Exception e) { Dbg($"{src} parse error: " + e.Message); }
            finally { r.Recycle(); }
            return;
        }
    }

    private static void OnHandshake(InnerNetClient client, MessageReader r)
    {
        if (!client.AmHost) return;

        string version = r.ReadString();
        int clientId = r.ReadPackedInt32();
        if (clientId == client.ClientId) return;

        if (string.Equals(version, VoiceChatPluginMain.Version, StringComparison.Ordinal))
        {
            Cleared.Add(clientId);
            Pending.Remove(clientId);
            Dbg($"cleared id={clientId} ver={version}");
        }
        else
        {
            // Reactor-style: a wrong version is rejected immediately, no grace.
            Dbg($"mismatch id={clientId} theirs={version} ours={VoiceChatPluginMain.Version}");
            Pending.Remove(clientId);
            if (!PendingKick.ContainsKey(clientId)) PendingKick[clientId] = MismatchMessage(version);
        }
    }

    private static void OnKickReason(InnerNetClient client, MessageReader r)
    {
        if (client.AmHost) return;

        int targetId = r.ReadPackedInt32();
        string reason = r.ReadString();
        if (targetId != client.ClientId) return;
        _pendingKickReason = reason;
        Dbg("kickreason stored");
    }

    private static string MismatchMessage(string clientVersion) =>
        "Perfect Comms version mismatch.\n\n" +
        $"This lobby is running Perfect Comms {VoiceChatPluginMain.Version}.\n" +
        $"You have {clientVersion}.\n\n" +
        "Update Perfect Comms to join this lobby.";

    private static string MissingMessage() =>
        $"This lobby requires Perfect Comms {VoiceChatPluginMain.Version}.\n\n" +
        "Install or enable Perfect Comms to join this lobby.";

    [HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
    private static class LobbyStartPatch
    {
        public static void Postfix() => Reset();
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
    private static class GameJoinedPatch
    {
        public static void Postfix(AmongUsClient __instance)
        {
            if (__instance == null || __instance.AmHost) return;
            _sentCount = 0;
            _lastSend = -999f;
            Dbg("OnGameJoined -> early handshake");
            if (SendHandshake(__instance)) { _lastSend = Time.time; _sentCount = 1; }
        }
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.OnPlayerJoined))]
    private static class PlayerJoinedPatch
    {
        public static void Postfix(InnerNetClient __instance, ClientData client)
        {
            if (__instance == null || client == null || !__instance.AmHost) return;
            int id = client.Id;
            if (id == __instance.ClientId || Cleared.Contains(id)) return;
            if (KickCount.TryGetValue(id, out var kc) && kc >= MaxKicks) return;
            if (!Pending.ContainsKey(id)) Pending[id] = Time.time;
            Dbg($"OnPlayerJoined id={id}");
        }
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.HandleMessage))]
    private static class HandleMessagePatch
    {
        public static void Prefix(InnerNetClient __instance, [HarmonyArgument(0)] MessageReader reader)
        {
            if (__instance == null || reader == null) return;
            try { Scan(__instance, reader, "hmsg"); } catch (Exception e) { Dbg("hmsg error: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.DisconnectInternal))]
    private static class DisconnectInternalPatch
    {
        public static void Prefix(InnerNetClient __instance, ref DisconnectReasons reason)
        {
            if (reason == DisconnectReasons.Kicked && _pendingKickReason != null)
            {
                Dbg("disconnect swap -> custom");
                reason = DisconnectReasons.Custom;
                __instance.LastCustomDisconnect = _pendingKickReason;
                _pendingKickReason = null;
            }
        }
    }
}
