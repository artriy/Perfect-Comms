using System;
using System.Collections.Generic;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal interface IVoiceBackend : IDisposable
{
    string RoomCode { get; }
    string Region { get; }
    bool UsingMicrophone { get; }
    bool UsingSpeaker { get; }
    bool Mute { get; }
    float LocalLevel { get; }
    bool LocalSpeaking { get; }
    int PeerCount { get; }
    IEnumerable<VoiceRemoteOverlayState> RemoteOverlayStates { get; }

    // Allocation-free variant for the per-frame overlay path: append remote overlay states into the
    // caller-owned buffer instead of allocating a fresh List/array (and LINQ) on every access.
    void AppendRemoteOverlayStates(List<VoiceRemoteOverlayState> buffer);

    void SetMicrophonePolicy(bool mute, bool keepCaptureWarm);
    void SetLoopBack(bool loopBack, bool delayed, float gain);
    void SetMasterVolume(float volume);
    void SetMicVolume(float volume);
    void SetNoiseGate(float noiseGateThreshold, float vadThreshold);
    void SetCaptureRuntimeOptions(VoiceCaptureRuntimeOptions options);
    void SetMicrophone(string deviceName, float volume, bool forceRestart = false);
    void SetSpeaker(string deviceName);
    void UpdateProfile(byte playerId, string playerName);
    void ApplyRemoteRadioState(byte playerId, VoiceTeamRadioChannel channel);
    void Rejoin();
    // Rebuild any pre-built ICE/peer-connection pool after a custom TURN / relay-policy setting change, so the next
    // peer-join uses the new policy without generating a DTLS certificate on the main thread. Backends with
    // Implementations without a prewarmed ICE pool may implement this as a no-op.
    void RebuildIceConnectionPool();
    void Update(
        VoiceGameStateSnapshot? snapshot,
        IReadOnlyList<VoiceChatRoom.SpeakerCache> speakerCache,
        IReadOnlyList<IVoiceComponent> virtualMicrophones,
        bool localInVent,
        bool commsSabActive);
    bool TrySetRemoteVolume(byte playerId, string playerName, float volume);
    int ResetPeerMappingsNoMute();
    int CountMappedRemotePeers(VoiceGameStateSnapshot snapshot);
    int CountPeersWithOpenChannel(VoiceGameStateSnapshot snapshot);

    // Peers whose underlying data channel is physically OPEN, regardless of whether their clientId is currently
    // mapped to a live snapshot player. After a round boundary a surviving peer can be briefly unmapped (the
    // local roster hasn't re-listed the remote yet) while its channel is perfectly healthy and audio is flowing;
    // the room uses this so it does not misread that self-healing window as a mesh collapse and fire a
    // destructive global rebuild. Backends that cannot observe a per-peer channel return their live peer count.
    int CountOpenDataChannels();

    // Targeted, non-destructive recovery of ONLY the specific remote clients that are expected but not
    // currently backed by a live/open peer — re-mapping / re-requesting an offer for each missing client
    // while leaving every already-open peer's data channel INTACT. This is the per-peer alternative to the
    // global Rejoin()/ClearPeers() teardown, so a single permanently-unmappable remote cannot drive a
    // mesh-wide rebuild storm. Returns the number of missing clients the backend was able to act on
    // (request/recreate) this call; returns -1 when the backend has no targeted path and the caller should
    // fall back to its existing global rebuild.
    int TryRecoverMissingClients(VoiceGameStateSnapshot snapshot);
}

internal readonly record struct VoiceCaptureRuntimeOptions(
    bool SyntheticMicToneEnabled,
    bool MicCalibrationDiagnostics,
    bool NoiseSuppressionEnabled,
    bool StrongerNoiseSuppressionEnabled,
    bool EchoCancellationEnabled,
    float MicSensitivity);
