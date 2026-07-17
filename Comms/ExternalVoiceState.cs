using System;
using PerfectComms.Api;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

// Resolved third-party state is stored as plain values on the snapshot. No mod callback runs in
// the backend/audio loop. A player may hold several namespaced channel memberships at once.
internal readonly record struct ExternalVoiceChannelState(
    string Key,
    bool CanTransmit,
    int Shape,
    float Volume,
    bool HasOrigin,
    Vector2 Origin);

internal readonly record struct ExternalVoicePairState(
    VoicePairVerdict Verdict,
    string Reason,
    bool Muffled,
    int Shape,
    float Volume,
    bool HasSpeakerOrigin,
    Vector2 SpeakerOrigin,
    bool HasListenerOrigin,
    Vector2 ListenerOrigin)
{
    internal static readonly ExternalVoicePairState None = new(
        VoicePairVerdict.Pass,
        string.Empty,
        false,
        (int)VoicePairRouteShape.Proximity,
        1f,
        false,
        default,
        false,
        default);
}

internal readonly record struct ExternalVoiceState(
    // Speaker-wide gate state.
    bool Muted,
    bool Muffled,
    string Reason,
    // Every channel membership returned by this player's registered resolvers.
    ExternalVoiceChannelState[]? Channels,
    // Local-player listener-origin state.
    bool ListenerActive,
    Vector2 ListenerOrigin,
    float ListenerLightRadius,
    bool ListenerReplace,
    // Local-listener/speaker pair state. This is resolved separately for each client snapshot.
    ExternalVoicePairState Pair)
{
    public static readonly ExternalVoiceState None = new(
        false,
        false,
        string.Empty,
        null,
        false,
        default,
        -1f,
        true,
        ExternalVoicePairState.None);

    public bool HasReason => !string.IsNullOrEmpty(Reason);
    public ReadOnlySpan<ExternalVoiceChannelState> ChannelSpan => Channels;
}
