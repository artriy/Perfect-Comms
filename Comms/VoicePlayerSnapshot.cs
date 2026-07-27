using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>How the local player's registered listener origin is applied this frame.</summary>
internal enum VoiceControlHearingMode
{
    None = 0,
    ExternalReplace = 1,
    ExternalAdditive = 2,
}

internal readonly record struct VoicePlayerSnapshot(
    byte PlayerId,
    int ClientId,
    string PlayerName,
    Vector2 Position,
    bool IsLocal,
    bool IsDead,
    bool IsSpectator,
    bool IsImpostor,
    bool InVent,
    bool Disconnected,
    bool IsDummy,
    bool IsVisible,
    // Local-player-only listener-origin fields (default None/zero for everyone else).
    VoiceControlHearingMode ControlHearingMode,
    Vector2 ControlledVictimPosition,
    float ControlledVictimLightRadius,
    // Registered mod voice state, resolved once per player in the snapshot builder.
    ExternalVoiceState External = default);
