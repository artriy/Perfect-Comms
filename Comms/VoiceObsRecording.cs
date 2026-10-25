using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

public enum VoiceObsRecordingMode
{
    PerPlayer = 0,
    AliveDead = 1,
}

internal readonly record struct SidecarObsRecordingSettings(bool Enabled, VoiceObsRecordingMode Mode);

internal readonly record struct VoiceObsRecordingIdentity(uint Id, string Name, byte Group)
{
    internal static VoiceObsRecordingIdentity Resolve(
        VoiceIdentityPrivacyResolution resolution,
        bool hiddenViewer,
        VoicePlayerSnapshot? presentation,
        uint presentationToken)
    {
        if (hiddenViewer || presentationToken == 0 || !resolution.HasConcretePresentation ||
            presentation is not { } player || player.PlayerId != resolution.PresentationPlayerId ||
            VoiceProximityCalculator.IsUnavailableTarget(player))
            return new(0, string.Empty, 0);

        return new(presentationToken, PlainName(player.PlayerName), (byte)(player.IsDead ? 2 : 1));
    }

    private static string PlainName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Player";
        Span<char> plain = stackalloc char[192];
        var chars = 0;
        var bytes = 0;
        var inTag = false;
        var changed = false;
        foreach (var rune in name.EnumerateRunes())
        {
            if (rune.Value == '<') { inTag = true; changed = true; continue; }
            if (rune.Value == '>') { inTag = false; changed = true; continue; }
            if (inTag || Rune.IsControl(rune) || rune.Value == 0xFFFD) { changed = true; continue; }
            if (bytes + rune.Utf8SequenceLength > 192) { changed = true; break; }
            chars += rune.EncodeToUtf16(plain[chars..]);
            bytes += rune.Utf8SequenceLength;
        }
        return chars == 0 ? "Player" : changed ? new string(plain[..chars]) : name;
    }
}

internal sealed class VoiceObsRecordingProjection
{
    private readonly record struct Participant(int ClientId, string Name, uint Token);

    private readonly Dictionary<byte, Participant> _participants = new();
    private readonly HashSet<byte> _roster = new();
    private readonly List<byte> _departed = new();
    private readonly Dictionary<byte, VoiceIdentityPrivacyResolution> _resolutions = new();
    private int? _sessionId;
    private uint _nextToken;

    internal void UpdateParticipants(
        int sessionId, IReadOnlyList<VoicePlayerSnapshot>? players, bool rosterComplete)
    {
        if (players == null) return;
        if (_sessionId != sessionId)
        {
            _participants.Clear();
            _sessionId = sessionId;
        }
        _roster.Clear();
        foreach (var player in players)
        {
            if (player.PlayerId == byte.MaxValue) continue;
            _roster.Add(player.PlayerId);
            if (player.Disconnected)
            {
                if (rosterComplete) _participants.Remove(player.PlayerId);
                continue;
            }
            if (player.ClientId < 0 || player.IsDummy) continue;
            if (_participants.TryGetValue(player.PlayerId, out var previous) &&
                previous.ClientId == player.ClientId && previous.Name == player.PlayerName)
                continue;
            // Never recycle an export token, including after a player-slot reuse or rename.
            if (_nextToken == uint.MaxValue)
            {
                _participants.Remove(player.PlayerId);
                continue;
            }
            _participants[player.PlayerId] = new Participant(player.ClientId, player.PlayerName, ++_nextToken);
        }
        if (!rosterComplete) return;
        _departed.Clear();
        foreach (var playerId in _participants.Keys)
            if (!_roster.Contains(playerId)) _departed.Add(playerId);
        foreach (var playerId in _departed) _participants.Remove(playerId);
    }

    internal void Apply(
        List<SidecarProtocol.GameStatePeerInput> routes,
        IReadOnlyDictionary<string, VoicePlayerSnapshot> sources,
        VoiceGameStateSnapshot snapshot,
        bool hiddenViewer,
        Func<byte, VoiceGamePhase, VoiceIdentityPrivacyResolution> resolve)
    {
        _resolutions.Clear();
        foreach (var route in routes)
        {
            if (!sources.TryGetValue(route.Id, out var source) || _resolutions.ContainsKey(source.PlayerId))
                continue;
            var resolution = resolve(source.PlayerId, snapshot.Phase);
            _resolutions.Add(source.PlayerId, resolution);
            hiddenViewer |= resolution.Decision is VoiceIdentityPrivacyDecision.HideAllForViewer
                or VoiceIdentityPrivacyDecision.DimAll;
        }
        for (int i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            var identity = default(VoiceObsRecordingIdentity);
            if (sources.TryGetValue(route.Id, out var source) &&
                _resolutions.TryGetValue(source.PlayerId, out var resolution))
            {
                VoicePlayerSnapshot? presentation =
                    resolution.PresentationPlayerId is { } id && snapshot.TryGetPlayer(id, out var player)
                        ? player
                        : null;
                uint token = presentation is { } target &&
                    _participants.TryGetValue(target.PlayerId, out var participant) &&
                    participant.ClientId == target.ClientId && participant.Name == target.PlayerName
                        ? participant.Token
                        : 0;
                identity = VoiceObsRecordingIdentity.Resolve(resolution, hiddenViewer, presentation, token);
            }
            routes[i] = new SidecarProtocol.GameStatePeerInput(
                route.Id, route.Gain, route.Pan, route.Mode, route.Muffled,
                identity.Id, identity.Name, identity.Group);
        }
    }
}

internal sealed record VoiceObsRecordingStatus(string State, string Message)
{
    internal string Label => State switch
    {
        "disabled" => "Off",
        "ready" => "Connected",
        "recording" => "Recording",
        "paused" => "Paused",
        "error" => "Recording error",
        _ => "Waiting for OBS",
    };
}

internal static class VoiceObsRecording
{
    private static VoiceObsRecordingStatus _status = new("disabled", string.Empty);
    internal static VoiceObsRecordingStatus Status => Volatile.Read(ref _status);

    internal static VoiceObsRecordingMode NormalizeMode(VoiceObsRecordingMode mode)
        => mode is VoiceObsRecordingMode.PerPlayer or VoiceObsRecordingMode.AliveDead
            ? mode
            : VoiceObsRecordingMode.PerPlayer;

    internal static void HelperUnavailable(bool enabled)
        => Publish(enabled ? "waiting" : "disabled",
            enabled ? "Join voice, then add the Perfect Comms source in OBS." : string.Empty);

    internal static void SettingChanged(bool enabled)
    {
        if (!enabled)
            Publish("disabled", string.Empty);
        else if (Status.State == "disabled")
            Publish("waiting", "Join voice, then add the Perfect Comms source in OBS.");
    }

    internal static bool Publish(string state, string? message)
    {
        if (state is not ("disabled" or "waiting" or "ready" or "recording" or "paused" or "error"))
            return false;
        if (message is { Length: > 1024 }) return false;
        Volatile.Write(ref _status, new VoiceObsRecordingStatus(state, message ?? string.Empty));
        return true;
    }
}
