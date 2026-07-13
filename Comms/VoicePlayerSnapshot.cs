using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

// How the LOCAL player's proximity-hearing origin is relocated this frame.
//   None             - normal: hear from your own body.
//   PuppeteerSwap    - TOU Puppeteer drives the victim (own body frozen): hear ENTIRELY from the
//                      victim's surroundings. Gated on the built-in PuppeteerHearFromVictim host toggle.
//   ParasiteAdditive - TOU Parasite hears its own body AND the victim's surroundings (union, louder
//                      wins). Gated on the built-in ParasiteHearFromVictim host toggle.
//   ExternalReplace  - PerfectComms.Api listener-origin override (Replace). Hear ENTIRELY from the
//                      override position. NOT gated on any built-in/TOU toggle - works standalone.
//   ExternalAdditive - PerfectComms.Api listener-origin override (Additive). Hear from own body AND
//                      the override position. NOT gated on any built-in/TOU toggle.
internal enum VoiceControlHearingMode
{
    None = 0,
    PuppeteerSwap = 1,
    ParasiteAdditive = 2,
    ExternalReplace = 3,
    ExternalAdditive = 4,
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
    float ControlledVictimLightRadius,
    // Third-party mod voice state (PerfectComms.Api), resolved once per player in the snapshot
    // builder. One bundled field keeps the API isolated from the core snapshot shape.
    ExternalVoiceState External = default,
    // Needed by HUD-independent transition policy: when LocalPlayer/Data is being rebuilt, the
    // last authenticated snapshot must preserve this role mute instead of briefly unmuting.
    bool IsGlitchHacked = false);
