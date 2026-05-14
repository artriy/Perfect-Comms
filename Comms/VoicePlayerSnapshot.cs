using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal readonly record struct VoicePlayerSnapshot(
    byte PlayerId,
    int ClientId,
    string PlayerName,
    Vector2 Position,
    bool IsLocal,
    bool IsDead,
    bool IsImpostor,
    bool IsVampire,
    bool InVent,
    bool Disconnected,
    bool IsDummy,
    bool IsVisible,
    bool IsBlackmailed,
    bool IsBlackmailedNextRound,
    bool IsJailed,
    byte JailorId,
    bool IsMediumSpiritual,
    bool IsParasiteVictim);