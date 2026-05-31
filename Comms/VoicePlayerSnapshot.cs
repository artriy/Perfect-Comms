using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

// How the LOCAL player's proximity-hearing origin is affected by a Town of Us control ability they are using.
//   None             — normal: hear from your own body.
//   PuppeteerSwap    — Puppeteer drives the victim (own body frozen): hear ENTIRELY from the victim's surroundings.
//   ParasiteAdditive — Parasite plays normally but ALSO hears the infected victim's surroundings (union, louder wins).
internal enum VoiceControlHearingMode
{
    None = 0,
    PuppeteerSwap = 1,
    ParasiteAdditive = 2,
}

internal readonly record struct VoicePlayerSnapshot(
    byte PlayerId,
    int ClientId,
    string PlayerName,
    Vector2 Position,
    bool IsLocal,
    bool IsDead,
    bool IsImpostor,
    bool IsVampire,
    bool IsLover,
    byte LoverPartnerId,
    bool InVent,
    bool Disconnected,
    bool IsDummy,
    bool IsVisible,
    bool IsBlackmailed,
    bool IsJailed,
    byte JailorId,
    bool IsParasiteControlled,
    bool IsPuppeteerControlled,
    bool IsBlackmailedNextRound,
    bool IsSwooped,
    bool IsMedium,
    bool HasMediumSpirit,
    Vector2 MediumSpiritPosition,
    bool IsMediatedGhost,
    byte MediatingMediumId,
    // Local-player-only control-hearing fields (default None/zero for everyone else).
    VoiceControlHearingMode ControlHearingMode,
    Vector2 ControlledVictimPosition,
    float ControlledVictimLightRadius);
