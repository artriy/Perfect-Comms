using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceVersionGuard
{
    private const byte RpcId = 207;
    private const byte KindChallenge = 1;
    private const byte KindAnswer = 2;

    private const float AnswerInterval = 1f;
    private const float ChallengeInterval = 1f;
    private const float EnforceInterval = 0.5f;
    private const float GraceSeconds = 2f;
    private const float WrongVersionGrace = 4f;
    private const int NonceLength = 16;
    private const int MacLength = 32;
    private const int ChallengeDomainId = int.MinValue;

    private static readonly byte[] AttestKey =
    {
        0x9c, 0x4e, 0x1a, 0xf7, 0x83, 0x21, 0xbd, 0x56,
        0x0e, 0xd2, 0x77, 0x48, 0xab, 0x39, 0xc1, 0x6f,
        0x52, 0x8d, 0x14, 0xe0, 0xb6, 0x9a, 0x3c, 0x71,
        0x2f, 0xd5, 0x88, 0x4b, 0xe7, 0x10, 0x63, 0xca,
    };

    private static byte[]? _hostNonce;
    private static readonly HashSet<int> Cleared = new();
    private static readonly HashSet<int> WrongVersion = new();
    private static readonly Dictionary<int, float> FirstSeen = new();
    private static readonly HashSet<int> Present = new();
    private static readonly List<int> Absent = new();
    private static float _lastEnforce = -999f;
    private static float _lastChallenge = -999f;
    private static bool _wasHost;

    private static byte[]? _receivedNonce;
    private static float _lastAnswer = -999f;
    private static string _shownReasonFor = "";

    private static bool _attestProbed;
    private static bool _attestHealthy;

    public static void Reset()
    {
        _hostNonce = null;
        Cleared.Clear();
        WrongVersion.Clear();
        FirstSeen.Clear();
        _receivedNonce = null;
        _shownReasonFor = "";
        _lastEnforce = -999f;
        _lastChallenge = -999f;
        _lastAnswer = -999f;
        _wasHost = false;
    }

    public static void Tick()
    {
        var client = AmongUsClient.Instance;
        if (client == null || PlayerControl.LocalPlayer == null) return;
        if (!AttestationHealthy()) return;

        bool amHost = client.AmHost;
        if (amHost && !_wasHost) OnBecameHost();
        _wasHost = amHost;

        if (!amHost)
        {
            if (_receivedNonce != null && Time.time - _lastAnswer >= AnswerInterval)
            {
                _lastAnswer = Time.time;
                SendAnswer(client, _receivedNonce);
            }
            return;
        }

        if (_hostNonce == null) _hostNonce = NewNonce();

        if (Time.time - _lastChallenge >= ChallengeInterval)
        {
            _lastChallenge = Time.time;
            BroadcastChallenge(client, _hostNonce);
        }

        if (Time.time - _lastEnforce >= EnforceInterval)
        {
            _lastEnforce = Time.time;
            Enforce(client);
        }
    }

    private static void OnBecameHost()
    {
        Cleared.Clear();
        WrongVersion.Clear();
        FirstSeen.Clear();
        _hostNonce = NewNonce();
        _lastEnforce = Time.time;
        _lastChallenge = -999f;
    }

    private static void Enforce(AmongUsClient client)
    {
        int hostId = client.ClientId;
        bool sawNewcomer = false;

        Present.Clear();
        foreach (var other in client.allClients)
        {
            if (other == null) continue;
            int id = other.Id;
            if (id == hostId) continue;
            Present.Add(id);

            if (Cleared.Contains(id))
            {
                FirstSeen.Remove(id);
                continue;
            }

            if (other.Character == null) continue;

            if (!FirstSeen.TryGetValue(id, out var seen))
            {
                FirstSeen[id] = Time.time;
                sawNewcomer = true;
                continue;
            }

            float grace = WrongVersion.Contains(id) ? WrongVersionGrace : GraceSeconds;
            if (Time.time - seen >= grace)
                Kick(client, id);
        }

        PruneAbsent();

        if (sawNewcomer && _hostNonce != null)
        {
            _lastChallenge = Time.time;
            BroadcastChallenge(client, _hostNonce);
        }
    }

    private static void PruneAbsent()
    {
        Absent.Clear();
        foreach (var id in FirstSeen.Keys)
            if (!Present.Contains(id)) Absent.Add(id);
        foreach (var id in Absent) FirstSeen.Remove(id);

        Absent.Clear();
        foreach (var id in Cleared)
            if (!Present.Contains(id)) Absent.Add(id);
        foreach (var id in Absent) Cleared.Remove(id);

        Absent.Clear();
        foreach (var id in WrongVersion)
            if (!Present.Contains(id)) Absent.Add(id);
        foreach (var id in Absent) WrongVersion.Remove(id);
    }

    private static void Kick(AmongUsClient client, int clientId)
    {
        try
        {
            VoiceDiagnostics.Log("version.guard.kick",
                $"client={clientId} reason=\"no valid PerfectComms {VoiceChatPluginMain.Version} attestation\"");
            client.KickPlayer(clientId, false);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("version.guard.kick.error", $"error=\"{ex.Message}\"");
        }

        Cleared.Remove(clientId);
        WrongVersion.Remove(clientId);
        FirstSeen.Remove(clientId);
    }

    private static void BroadcastChallenge(AmongUsClient client, byte[] nonce)
    {
        try
        {
            var writer = client.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            writer.Write(KindChallenge);
            for (int i = 0; i < NonceLength; i++) writer.Write(nonce[i]);
            writer.Write(VoiceChatPluginMain.Version);
            byte[] challengeMac = ComputeMac(nonce, ChallengeDomainId, VoiceChatPluginMain.Version);
            for (int i = 0; i < MacLength; i++) writer.Write(challengeMac[i]);
            client.FinishRpcImmediately(writer);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("version.guard.challenge.error", $"error=\"{ex.Message}\"");
        }
    }

    private static void SendAnswer(AmongUsClient client, byte[] nonce)
    {
        try
        {
            byte[] mac = ComputeMac(nonce, client.ClientId, VoiceChatPluginMain.Version);
            var writer = client.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            writer.Write(KindAnswer);
            writer.Write(VoiceChatPluginMain.Version);
            for (int i = 0; i < NonceLength; i++) writer.Write(nonce[i]);
            for (int i = 0; i < MacLength; i++) writer.Write(mac[i]);
            client.FinishRpcImmediately(writer);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("version.guard.answer.error", $"error=\"{ex.Message}\"");
        }
    }

    private static void ShowVersionReason(string hostVersion)
    {
        if (string.Equals(_shownReasonFor, hostVersion, StringComparison.Ordinal)) return;
        _shownReasonFor = hostVersion;
        try
        {
            VoiceChatHudState.ShowToast(
                $"This lobby runs PerfectComms {hostVersion}. You have {VoiceChatPluginMain.Version}. Update to join.");
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("version.guard.reason.error", $"error=\"{ex.Message}\"");
        }
    }

    private static bool AttestationHealthy()
    {
        if (_attestProbed) return _attestHealthy;
        _attestProbed = true;
        try
        {
            var nonce = NewNonce();
            var a = ComputeMac(nonce, 1, VoiceChatPluginMain.Version);
            var b = ComputeMac(nonce, 1, VoiceChatPluginMain.Version);
            _attestHealthy = a.Length == MacLength && ConstantTimeEquals(a, b);
            if (!_attestHealthy)
                VoiceDiagnostics.Log("version.guard.disabled", "reason=\"attestation self-test mismatch\"");
        }
        catch (Exception ex)
        {
            _attestHealthy = false;
            VoiceDiagnostics.Log("version.guard.disabled", $"reason=\"crypto unavailable: {ex.Message}\"");
        }

        return _attestHealthy;
    }

    private static byte[] NewNonce()
    {
        var nonce = new byte[NonceLength];
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }

    private static byte[] ComputeMac(byte[] nonce, int clientId, string version)
    {
        using var hmac = new HMACSHA256(AttestKey);
        var idBytes = BitConverter.GetBytes(clientId);
        var versionBytes = Encoding.UTF8.GetBytes(version);
        var message = new byte[nonce.Length + idBytes.Length + versionBytes.Length];
        Buffer.BlockCopy(nonce, 0, message, 0, nonce.Length);
        Buffer.BlockCopy(idBytes, 0, message, nonce.Length, idBytes.Length);
        Buffer.BlockCopy(versionBytes, 0, message, nonce.Length + idBytes.Length, versionBytes.Length);
        return hmac.ComputeHash(message);
    }

    private static bool ConstantTimeEquals(byte[] a, byte[] b)
        => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);

    [HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
    private static class LobbyStartPatch
    {
        public static void Postfix() => Reset();
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
    private static class PlayerControlHandleRpcPatch
    {
        public static void Postfix(PlayerControl __instance, byte callId, MessageReader reader)
        {
            if (callId != RpcId) return;

            try
            {
                byte kind = reader.ReadByte();
                if (kind == KindChallenge) HandleChallenge(__instance, reader);
                else if (kind == KindAnswer) HandleAnswer(__instance, reader);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("version.guard.rpc.error", $"error=\"{ex.Message}\"");
            }
        }
    }

    private static void HandleChallenge(PlayerControl sender, MessageReader reader)
    {
        var nonce = new byte[NonceLength];
        for (int i = 0; i < NonceLength; i++) nonce[i] = reader.ReadByte();
        string hostVersion = reader.ReadString();
        var challengeMac = new byte[MacLength];
        for (int i = 0; i < MacLength; i++) challengeMac[i] = reader.ReadByte();

        var client = AmongUsClient.Instance;
        if (client == null || client.AmHost) return;

        if (!ConstantTimeEquals(challengeMac, ComputeMac(nonce, ChallengeDomainId, hostVersion)))
            return;

        if (!VoiceHostAuthority.IsTrustedHostSender(sender, VoiceChatRoom.Current?.CurrentSnapshot, "rpc",
                out _, out _, out _, out _))
            return;

        if (!string.Equals(hostVersion, VoiceChatPluginMain.Version, StringComparison.Ordinal))
            ShowVersionReason(hostVersion);

        _receivedNonce = nonce;
        _lastAnswer = -999f;
    }

    private static void HandleAnswer(PlayerControl sender, MessageReader reader)
    {
        string version = reader.ReadString();
        var nonce = new byte[NonceLength];
        for (int i = 0; i < NonceLength; i++) nonce[i] = reader.ReadByte();
        var mac = new byte[MacLength];
        for (int i = 0; i < MacLength; i++) mac[i] = reader.ReadByte();

        var client = AmongUsClient.Instance;
        if (client == null || !client.AmHost || _hostNonce == null || sender == null) return;

        int senderId = sender.OwnerId;
        if (senderId == client.ClientId) return;

        if (!ConstantTimeEquals(nonce, _hostNonce)) return;
        if (!ConstantTimeEquals(mac, ComputeMac(_hostNonce, senderId, version))) return;

        if (string.Equals(version, VoiceChatPluginMain.Version, StringComparison.Ordinal))
        {
            Cleared.Add(senderId);
            WrongVersion.Remove(senderId);
            FirstSeen.Remove(senderId);
        }
        else if (!Cleared.Contains(senderId))
        {
            WrongVersion.Add(senderId);
        }
    }
}
