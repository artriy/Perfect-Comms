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
// See docs/MOD_INTEGRATION.md and the GitHub wiki for the full guide.

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
    /// <summary>
    /// Proximity-shaped audio. The speaker's resolved body position is the default source;
    /// <see cref="VoiceChannelResult.Origin"/> can replace it with a fixed spatial source.
    /// </summary>
    Proximity,
    /// <summary>Full-volume, no falloff (team radio).</summary>
    Radio,
    /// <summary>Heard through a working low-pass channel filter.</summary>
    Muffle,
}

/// <summary>Verdict a gate rule returns. Pass = no opinion, defer to other rules.</summary>
public enum VoiceVerdict
{
    Pass,
    Mute,
    /// <summary>Keep the speaker audible through the listener-muffle low-pass filter.</summary>
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

/// <summary>Runtime-discoverable API 1.1 capabilities.</summary>
[Flags]
public enum VoiceApiCapability
{
    None = 0,
    PerSpeakerMuffle = 1 << 0,
    GlobalReceiveGate = 1 << 1,
    DirectionalChannels = 1 << 2,
    MultipleChannels = 1 << 3,
    ContextualListeners = 1 << 4,
    PairRouting = 1 << 5,
    PlayerTraits = 1 << 6,
    PhaseObservers = 1 << 7,
    ConditionalHostOptions = 1 << 8,
    NumericHostOptions = 1 << 9,
    OverlayPrivacy = 1 << 10,
}

/// <summary>Additional voice classifications a role mod can contribute for a player.</summary>
[Flags]
public enum VoicePlayerTraits
{
    None = 0,
    /// <summary>Use the same vent, ghost-hearing, radio, and viewer-privacy classification as an impostor.</summary>
    ImpostorVoice = 1 << 0,
    /// <summary>Treat the player as dead for voice even if base game data has not marked them dead.</summary>
    VoiceDead = 1 << 1,
    /// <summary>Treat the voice-dead player as a spectator rather than an ordinary ghost.</summary>
    Spectator = 1 << 2,
}

/// <summary>Inputs handed to every per-player callback.</summary>
public sealed record VoiceRuleContext(
    PlayerControl Player,
    VoicePhaseKind Phase,
    bool IsLocal,
    bool IsDead)
{
    /// <summary>The local listener on this client, if the scene has resolved one.</summary>
    public PlayerControl? LocalPlayer { get; init; }

    /// <summary>Voice-dead state of <see cref="LocalPlayer"/> after registered player traits are applied.</summary>
    public bool LocalIsDead { get; init; }

    /// <summary>Host-synced bool option value, keyed by the bare option key registered under this mod's id.</summary>
    public Func<string, bool> GetOption { get; init; } = _ => false;

    /// <summary>Host-synced enum/int option value.</summary>
    public Func<string, int> GetEnumOption { get; init; } = _ => 0;

    /// <summary>Host-synced numeric option value.</summary>
    public Func<string, float> GetNumberOption { get; init; } = _ => 0f;
}

/// <summary>Result of a gate rule.</summary>
public sealed record VoiceRuleResult(VoiceVerdict Verdict, string Reason)
{
    public static readonly VoiceRuleResult Pass = new(VoiceVerdict.Pass, string.Empty);
    /// <summary>Mute the speaker in every voice phase, including dead-player routes.</summary>
    public static VoiceRuleResult Mute(string reason) => new(VoiceVerdict.Mute, reason ?? "Muted");
    /// <summary>Muffle this speaker without affecting other incoming audio.</summary>
    public static VoiceRuleResult Muffle(string reason) => new(VoiceVerdict.Muffle, reason ?? "Muffled");
}

/// <summary>Inputs for option-aware global gates.</summary>
public sealed record VoiceGlobalGateContext(
    PlayerControl? LocalPlayer,
    VoicePhaseKind Phase,
    bool LocalIsDead)
{
    public Func<string, bool> GetOption { get; init; } = _ => false;
    public Func<string, int> GetEnumOption { get; init; } = _ => 0;
    public Func<string, float> GetNumberOption { get; init; } = _ => 0f;
}

/// <summary>Inputs for option-aware listener-origin and listener-effect callbacks.</summary>
public sealed record VoiceListenerContext(
    PlayerControl Listener,
    VoicePhaseKind Phase,
    bool IsDead)
{
    public Func<string, bool> GetOption { get; init; } = _ => false;
    public Func<string, int> GetEnumOption { get; init; } = _ => 0;
    public Func<string, float> GetNumberOption { get; init; } = _ => 0f;
}

/// <summary>How a listener-specific speaker rule composes with normal routing.</summary>
public enum VoicePairVerdict
{
    Pass,
    Mute,
    Muffle,
    Route,
}

/// <summary>Audio output selected by an explicit listener-speaker route.</summary>
public enum VoicePairRouteShape
{
    Proximity,
    Radio,
    Ghost,
}

/// <summary>Inputs for a listener-specific speaker decision. The listener is always the local player.</summary>
public sealed record VoicePairContext(
    PlayerControl Listener,
    PlayerControl Speaker,
    VoicePhaseKind Phase,
    bool ListenerIsDead,
    bool SpeakerIsDead)
{
    public Func<string, bool> GetOption { get; init; } = _ => false;
    public Func<string, int> GetEnumOption { get; init; } = _ => 0;
    public Func<string, float> GetNumberOption { get; init; } = _ => 0f;
}

/// <summary>
/// Listener-specific routing result. Mute wins over every other pair result. Muffle applies to the
/// otherwise selected route. Route replaces ordinary routing for this pair and can provide either
/// endpoint's spatial origin; omitted origins use the players' resolved positions.
/// </summary>
public sealed record VoicePairResult(VoicePairVerdict Verdict, string Reason)
{
    public VoicePairRouteShape Shape { get; init; } = VoicePairRouteShape.Proximity;
    public float Volume { get; init; } = 1f;
    public Vector2? SpeakerOrigin { get; init; }
    public Vector2? ListenerOrigin { get; init; }

    public static readonly VoicePairResult Pass = new(VoicePairVerdict.Pass, string.Empty);
    public static VoicePairResult Mute(string reason) => new(VoicePairVerdict.Mute, reason ?? "Muted");
    public static VoicePairResult Muffle(string reason) => new(VoicePairVerdict.Muffle, reason ?? "Muffled");
    public static VoicePairResult Route(
        VoicePairRouteShape shape,
        float volume = 1f,
        Vector2? speakerOrigin = null,
        Vector2? listenerOrigin = null,
        string reason = "Mod Route")
        => new(VoicePairVerdict.Route, reason ?? "Mod Route")
        {
            Shape = shape,
            Volume = volume,
            SpeakerOrigin = speakerOrigin,
            ListenerOrigin = listenerOrigin,
        };
}

/// <summary>
/// A voice channel two players share. Same Key on local and target = they hear each other.
/// Keys are namespaced by mod id internally to prevent cross-mod collision.
///
/// A Proximity channel uses the speaker's resolved body position by default. Set <see cref="Origin"/>
/// to route it from a fixed point instead:
/// the listener hears the speaker as if the audio came from <see cref="Origin"/>, with normal
/// distance falloff. This is how a Medium seance is heard from the spirit's location rather than
/// as flat radio. Origin is only used when <see cref="Shape"/> is Proximity. Set
/// <see cref="TwoWay"/> to false for a receive-only member: it can hear transmitting members with
/// the same key, but it is not heard back through that channel.
/// </summary>
public sealed record VoiceChannelResult(
    string Key,
    bool TwoWay = true,
    VoiceAudioShape Shape = VoiceAudioShape.Radio,
    float Volume = 1f,
    Vector2? Origin = null);

/// <summary>
/// Relocates where the LOCAL player hears from during tasks. LightRadius = -1 inherits the local
/// player's resolved light radius; zero disables vision-radius limiting for the override.
/// </summary>
public sealed record VoiceListenerResult(
    Vector2 Origin,
    float LightRadius,
    VoiceListenerMode Mode);

/// <summary>
/// Result returned by RegisterContextualListenerFilter. True muffles all incoming audio for the
/// local listener without changing how other players hear them.
/// </summary>
public sealed record VoiceListenerFilterResult(bool Muffle);

/// <summary>Inputs delivered once when Perfect Comms observes a voice-phase transition.</summary>
public sealed record VoicePhaseChangedContext(
    VoicePhaseKind PreviousPhase,
    VoicePhaseKind Phase,
    PlayerControl? LocalPlayer)
{
    public Func<string, bool> GetOption { get; init; } = _ => false;
    public Func<string, int> GetEnumOption { get; init; } = _ => 0;
    public Func<string, float> GetNumberOption { get; init; } = _ => 0f;
}

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
    public Func<string, float> GetNumberOption { get; init; } = _ => 0f;
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
    public Func<string, float> GetNumberOption { get; init; } = _ => 0f;
}

/// <summary>Option lookup supplied to conditional host-option visibility callbacks.</summary>
public sealed record VoiceHostOptionContext
{
    public Func<string, bool> GetOption { get; init; } = _ => false;
    public Func<string, int> GetEnumOption { get; init; } = _ => 0;
    public Func<string, float> GetNumberOption { get; init; } = _ => 0f;
}

/// <summary>Declarative host toggle. Stored/synced as "modId.Key".</summary>
public sealed record VoiceHostOption(string Key, string Label, bool Default)
{
    public string Description { get; init; } = "";
    public Func<VoiceHostOptionContext, bool>? Visible { get; init; }
}

/// <summary>Declarative host enum/stepper option.</summary>
public sealed record VoiceHostEnumOption(string Key, string Label, int Default, string[] Choices)
{
    public string Description { get; init; } = "";
    public Func<VoiceHostOptionContext, bool>? Visible { get; init; }
}

/// <summary>Declarative host numeric/slider option.</summary>
public sealed record VoiceHostNumberOption(
    string Key,
    string Label,
    float Default,
    float Min,
    float Max,
    float Step,
    string Format = "0.0")
{
    public string Description { get; init; } = "";
    public Func<VoiceHostOptionContext, bool>? Visible { get; init; }
}

public static class PerfectCommsApi
{
    /// <summary>
    /// Compile-time API identifier. Because const values are embedded into consuming assemblies,
    /// this is not a runtime capability probe.
    /// </summary>
    public const string ApiVersion = "1.1";
    /// <summary>Stable BepInEx plugin id used for the soft-dependency presence check.</summary>
    public const string PluginId = "com.edgetel.perfectcomms";

    /// <summary>Runtime API identifier for reflection-based capability probes.</summary>
    public static string RuntimeApiVersion => ApiVersion;

    /// <summary>Capabilities implemented by this runtime's API 1.1 surface.</summary>
    public static VoiceApiCapability Capabilities =>
        VoiceApiCapability.PerSpeakerMuffle |
        VoiceApiCapability.GlobalReceiveGate |
        VoiceApiCapability.DirectionalChannels |
        VoiceApiCapability.MultipleChannels |
        VoiceApiCapability.ContextualListeners |
        VoiceApiCapability.PairRouting |
        VoiceApiCapability.PlayerTraits |
        VoiceApiCapability.PhaseObservers |
        VoiceApiCapability.ConditionalHostOptions |
        VoiceApiCapability.NumericHostOptions |
        VoiceApiCapability.OverlayPrivacy;

    public static bool Supports(VoiceApiCapability capability)
        => capability != VoiceApiCapability.None && (Capabilities & capability) == capability;

    // ---- Primitive 1: Gate / traits / listener-speaker pair policy ----

    /// <summary>
    /// Register a per-player gate. Mute wins over Muffle, and both apply in every voice phase.
    /// </summary>
    public static void RegisterVoiceRule(string modId, Func<VoiceRuleContext, VoiceRuleResult> rule)
        => VoiceModRegistry.AddRule(modId, rule);

    /// <summary>Register a phase-scoped global gate that mutes everyone while <paramref name="isActive"/> is true.</summary>
    public static void RegisterGlobalGate(string modId, VoicePhaseKind phase, Func<bool> isActive, string reason)
        => VoiceModRegistry.AddGlobalGate(modId, phase, isActive, reason);

    /// <summary>Register an option-aware phase-scoped global gate.</summary>
    public static void RegisterContextualGlobalGate(
        string modId,
        VoicePhaseKind phase,
        Func<VoiceGlobalGateContext, bool> isActive,
        string reason)
        => VoiceModRegistry.AddContextualGlobalGate(modId, phase, isActive, reason);

    /// <summary>Contribute additive voice classifications such as impostor-equivalent voice.</summary>
    public static void RegisterVoicePlayerTraits(
        string modId,
        Func<VoiceRuleContext, VoicePlayerTraits> traits)
        => VoiceModRegistry.AddPlayerTraits(modId, traits);

    /// <summary>Register a listener-specific speaker rule for private, directional, or spatial role voice.</summary>
    public static void RegisterVoicePairRule(
        string modId,
        Func<VoicePairContext, VoicePairResult> rule)
        => VoiceModRegistry.AddPairRule(modId, rule);

    // ---- Primitive 2: Channel ----

    /// <summary>
    /// Register one channel-membership resolver. Every non-empty membership is retained, so a player
    /// can join multiple channels by matching multiple registered callbacks.
    /// </summary>
    public static void RegisterVoiceChannel(string modId, Func<VoiceRuleContext, VoiceChannelResult?> channel)
        => VoiceModRegistry.AddChannel(modId, channel);

    // ---- Primitive 3: Listener-origin ----

    /// <summary>
    /// Register a task-phase local-player listener-origin override. Return null for normal hearing.
    /// The first non-null result wins after built-in control-hearing behavior.
    /// </summary>
    public static void RegisterListenerOrigin(string modId, Func<PlayerControl, VoiceListenerResult?> origin)
        => VoiceModRegistry.AddListenerOrigin(modId, origin);

    /// <summary>Register an option- and phase-aware listener-origin override.</summary>
    public static void RegisterContextualListenerOrigin(
        string modId,
        Func<VoiceListenerContext, VoiceListenerResult?> origin)
        => VoiceModRegistry.AddContextualListenerOrigin(modId, origin);

    /// <summary>
    /// Register a local-player listener FILTER: while the predicate returns true, all incoming audio
    /// is muffled for the local player. For blinded / flashed / hypnotised hearing. No netcode.
    /// </summary>
    public static void RegisterListenerFilter(string modId, Func<PlayerControl, bool> shouldMuffle)
        => VoiceModRegistry.AddListenerFilter(modId, shouldMuffle);

    /// <summary>Register an option- and phase-aware listener muffle rule.</summary>
    public static void RegisterContextualListenerFilter(
        string modId,
        Func<VoiceListenerContext, VoiceListenerFilterResult> filter)
        => VoiceModRegistry.AddContextualListenerFilter(modId, filter);

    /// <summary>Observe phase transitions once, before the new phase's player callbacks are resolved.</summary>
    public static void RegisterVoicePhaseObserver(
        string modId,
        Action<VoicePhaseChangedContext> observer)
        => VoiceModRegistry.AddPhaseObserver(modId, observer);

    // ---- Primitive 4: Host options ----

    public static void RegisterHostOption(string modId, VoiceHostOption option)
        => VoiceModRegistry.AddHostOption(modId, option);

    public static void RegisterHostEnumOption(string modId, VoiceHostEnumOption option)
        => VoiceModRegistry.AddHostEnumOption(modId, option);

    public static void RegisterHostNumberOption(string modId, VoiceHostNumberOption option)
        => VoiceModRegistry.AddHostNumberOption(modId, option);

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
