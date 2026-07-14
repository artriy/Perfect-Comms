using System;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;

namespace PerfectComms.Api;

// Public mod-integration surface for Perfect Comms. A third-party role mod references
// PerfectComms.dll as a SOFT dependency, registers rules in its Load(), and ships its own
// DLL. Perfect Comms references nothing of the mod. All callbacks run locally and MUST be
// cheap, allocation-light, and throw-free. Audio-routing callbacks run at snapshot cadence
// (~20x/sec); overlay-privacy callbacks run at most once per rendered frame so concealment
// cannot leave a visual breadcrumb. The registry wraps every call in try/catch. Audio callbacks
// fall back to their neutral result, while identity-bearing overlay callbacks fail private.
//
// See docs/MOD_API_PLAN.md and the GitHub wiki for the full guide.

/// <summary>Game phase a rule is evaluated in.</summary>
public enum VoicePhaseKind
{
    Lobby,
    Tasks,
    Meeting,
    Exile,
}

/// <summary>
/// Audio shape a channel/route applies. Mirrors the internal filter vocabulary so mod
/// results can name a valid shape without referencing internal types.
/// </summary>
public enum VoiceAudioShape
{
    /// <summary>Proximity-shaped audio (still falls off with distance).</summary>
    Proximity,
    /// <summary>Full-volume, no falloff (team radio).</summary>
    Radio,
    /// <summary>Heard but muffled (low-pass).</summary>
    Muffle,
}

/// <summary>Verdict a gate rule returns. Pass = no opinion, defer to other rules.</summary>
public enum VoiceVerdict
{
    Pass,
    Mute,
    Muffle,
}

/// <summary>Whether a listener-origin override replaces or augments the local player's hearing.</summary>
public enum VoiceListenerMode
{
    /// <summary>Hear entirely from the override position (own body silent as a source).</summary>
    Replace,
    /// <summary>Hear from own body AND the override position; per target the louder wins.</summary>
    Additive,
}

/// <summary>Inputs handed to every per-player callback.</summary>
public sealed record VoiceRuleContext(
    PlayerControl Player,
    VoicePhaseKind Phase,
    bool IsLocal,
    bool IsDead)
{
    /// <summary>Host-synced bool option value, keyed by the bare option key registered under this mod's id.</summary>
    public Func<string, bool> GetOption { get; init; } = _ => false;

    /// <summary>Host-synced enum/int option value.</summary>
    public Func<string, int> GetEnumOption { get; init; } = _ => 0;
}

/// <summary>Result of a gate rule.</summary>
public sealed record VoiceRuleResult(VoiceVerdict Verdict, string Reason)
{
    public static readonly VoiceRuleResult Pass = new(VoiceVerdict.Pass, string.Empty);
    public static VoiceRuleResult Mute(string reason) => new(VoiceVerdict.Mute, reason ?? "Muted");
    public static VoiceRuleResult Muffle(string reason) => new(VoiceVerdict.Muffle, reason ?? "Muffled");
}

/// <summary>
/// A voice channel two players share. Same Key on local and target = they hear each other.
/// Keys are namespaced by mod id internally to prevent cross-mod collision.
///
/// By default the channel is positionless (Radio / Muffle apply a flat result, Proximity uses the
/// speaker's own body). Set <see cref="Origin"/> to make the channel SPATIAL from a fixed point:
/// the listener hears the speaker as if the audio came from <see cref="Origin"/>, with normal
/// distance falloff. This is how a Medium seance is heard from the spirit's location rather than
/// as flat radio. Origin is only used when <see cref="Shape"/> is Proximity.
/// </summary>
public sealed record VoiceChannelResult(
    string Key,
    bool TwoWay = true,
    VoiceAudioShape Shape = VoiceAudioShape.Radio,
    float Volume = 1f,
    Vector2? Origin = null);

/// <summary>Relocates where the LOCAL player hears from this frame.</summary>
public sealed record VoiceListenerResult(
    Vector2 Origin,
    float LightRadius,
    VoiceListenerMode Mode);

/// <summary>
/// Muffles ALL incoming audio for the local player this frame (a low-pass on what you hear, not on
/// what you say). Use for blinded / flashed / hypnotised hearing effects. This affects the local
/// listener only; it does not change how others hear the player.
/// </summary>
public sealed record VoiceListenerFilterResult(bool Muffle);

/// <summary>Viewer-wide privacy verdict for identity-bearing voice overlays.</summary>
public enum VoiceOverlayViewerVerdict
{
    /// <summary>No opinion; allow other rules or the normal overlay policy to decide.</summary>
    Pass,
    /// <summary>Keep the overlay visible, but remove/dim identity-bearing presentation.</summary>
    DimAll,
    /// <summary>Hide every identity-bearing voice indicator for this viewer.</summary>
    HideAll,
}

/// <summary>Result of a viewer-wide overlay privacy rule.</summary>
public readonly record struct VoiceOverlayViewerResult(VoiceOverlayViewerVerdict Verdict)
{
    public static readonly VoiceOverlayViewerResult Pass = new(VoiceOverlayViewerVerdict.Pass);
    public static readonly VoiceOverlayViewerResult DimAll = new(VoiceOverlayViewerVerdict.DimAll);
    public static readonly VoiceOverlayViewerResult HideAll = new(VoiceOverlayViewerVerdict.HideAll);
}

/// <summary>Source-specific privacy verdict for an identity-bearing voice indicator.</summary>
public enum VoiceOverlaySpeakerVerdict
{
    /// <summary>No opinion; present the transport speaker normally.</summary>
    Pass,
    /// <summary>Attribute activity to another player instead of the transport source.</summary>
    Alias,
    /// <summary>Hide this transport source's identity-bearing indicator.</summary>
    HideSource,
    /// <summary>Hide every identity-bearing voice indicator for this viewer.</summary>
    HideAll,
}

/// <summary>Result of a source-specific overlay privacy rule.</summary>
public readonly record struct VoiceOverlaySpeakerResult(
    VoiceOverlaySpeakerVerdict Verdict,
    byte? AliasPlayerId = null)
{
    public static readonly VoiceOverlaySpeakerResult Pass = new(VoiceOverlaySpeakerVerdict.Pass);
    public static readonly VoiceOverlaySpeakerResult HideSource = new(VoiceOverlaySpeakerVerdict.HideSource);
    public static readonly VoiceOverlaySpeakerResult HideAll = new(VoiceOverlaySpeakerVerdict.HideAll);

    /// <summary>Attribute the source's activity to <paramref name="targetPlayerId"/>.</summary>
    public static VoiceOverlaySpeakerResult Alias(byte targetPlayerId)
        => new(VoiceOverlaySpeakerVerdict.Alias, targetPlayerId);
}

/// <summary>Inputs handed to a viewer-wide overlay privacy callback.</summary>
public sealed record VoiceOverlayViewerContext(
    PlayerControl Viewer,
    VoicePhaseKind Phase,
    bool IsDead)
{
    public Func<string, bool> GetOption { get; init; } = _ => false;
    public Func<string, int> GetEnumOption { get; init; } = _ => 0;
}

/// <summary>Inputs handed to a source-specific overlay privacy callback.</summary>
public sealed record VoiceOverlaySpeakerContext(
    PlayerControl Viewer,
    PlayerControl Speaker,
    VoicePhaseKind Phase,
    bool ViewerIsDead,
    bool SpeakerIsDead)
{
    public Func<string, bool> GetOption { get; init; } = _ => false;
    public Func<string, int> GetEnumOption { get; init; } = _ => 0;
}

/// <summary>Declarative host toggle. Stored/synced as "modId.Key".</summary>
public sealed record VoiceHostOption(string Key, string Label, bool Default);

/// <summary>Declarative host enum/stepper option.</summary>
public sealed record VoiceHostEnumOption(string Key, string Label, int Default, string[] Choices);

public static class PerfectCommsApi
{
    public const string ApiVersion = "1.1";
    public const string PluginId = "com.edgetel.perfectcomms";

    // ---- Primitive 1: Gate (mute / muffle) ----

    /// <summary>Register a per-player gate. First non-Pass result across all mods wins.</summary>
    public static void RegisterVoiceRule(string modId, Func<VoiceRuleContext, VoiceRuleResult> rule)
        => VoiceModRegistry.AddRule(modId, rule);

    /// <summary>Register a phase-scoped global gate that mutes everyone while <paramref name="isActive"/> is true.</summary>
    public static void RegisterGlobalGate(string modId, VoicePhaseKind phase, Func<bool> isActive, string reason)
        => VoiceModRegistry.AddGlobalGate(modId, phase, isActive, reason);

    // ---- Primitive 2: Channel ----

    /// <summary>Register a channel resolver. Return the channel a player belongs to this frame, or null.</summary>
    public static void RegisterVoiceChannel(string modId, Func<VoiceRuleContext, VoiceChannelResult?> channel)
        => VoiceModRegistry.AddChannel(modId, channel);

    // ---- Primitive 3: Listener-origin ----

    /// <summary>Register a local-player listener-origin override. Return null for normal hearing.</summary>
    public static void RegisterListenerOrigin(string modId, Func<PlayerControl, VoiceListenerResult?> origin)
        => VoiceModRegistry.AddListenerOrigin(modId, origin);

    /// <summary>
    /// Register a local-player listener FILTER: while the predicate returns true, all incoming audio
    /// is muffled for the local player. For blinded / flashed / hypnotised hearing. No netcode.
    /// </summary>
    public static void RegisterListenerFilter(string modId, Func<PlayerControl, bool> shouldMuffle)
        => VoiceModRegistry.AddListenerFilter(modId, shouldMuffle);

    // ---- Primitive 4: Host options ----

    public static void RegisterHostOption(string modId, VoiceHostOption option)
        => VoiceModRegistry.AddHostOption(modId, option);

    public static void RegisterHostEnumOption(string modId, VoiceHostEnumOption option)
        => VoiceModRegistry.AddHostEnumOption(modId, option);

    // ---- Primitive 5: Mod tab ----

    /// <summary>Register a host-panel tab for this mod. Options registered under the same id render inside it.</summary>
    public static void RegisterModTab(string modId, string tabLabel)
        => VoiceModRegistry.AddTab(modId, tabLabel);

    // ---- Primitive 6: Identity-bearing overlay privacy ----

    /// <summary>
    /// Register a viewer-wide overlay privacy rule. Results compose restrictively across all
    /// registrations: HideAll wins over DimAll, which wins over Pass. A throw fails to HideAll.
    /// </summary>
    public static void RegisterOverlayViewerRule(
        string modId,
        Func<VoiceOverlayViewerContext, VoiceOverlayViewerResult> rule)
        => VoiceModRegistry.AddOverlayViewerRule(modId, rule);

    /// <summary>
    /// Register a per-speaker overlay privacy rule. Results compose restrictively across all
    /// registrations: HideAll, HideSource, Alias, then Pass. Conflicting aliases and throws fail
    /// to HideSource.
    /// </summary>
    public static void RegisterOverlaySpeakerRule(
        string modId,
        Func<VoiceOverlaySpeakerContext, VoiceOverlaySpeakerResult> rule)
        => VoiceModRegistry.AddOverlaySpeakerRule(modId, rule);

    // ---- Cleanup ----

    /// <summary>Remove every registration for this mod id (call on unload).</summary>
    public static void Unregister(string modId)
        => VoiceModRegistry.RemoveAll(modId);
}
