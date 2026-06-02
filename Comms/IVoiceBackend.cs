using System;
using System.Collections.Generic;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal interface IVoiceBackend : IDisposable
{
    event Action<VoiceBackendCustomMessage>? CustomMessageReceived;

    string RoomCode { get; }
    string Region { get; }
    string ServerUrl { get; }
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

    void SetMute(bool mute);
    void ToggleMute();
    void SetLoopBack(bool loopBack);
    void SetMasterVolume(float volume);
    void SetMicVolume(float volume);
    void SetNoiseGate(float noiseGateThreshold, float vadThreshold);
    void SetCaptureRuntimeOptions(VoiceCaptureRuntimeOptions options);
    void SetMicrophone(string deviceName, float volume);
    void SetSpeaker(string deviceName);
    void UpdateProfile(byte playerId, string playerName);
    void SendRadioState(byte playerId, VoiceTeamRadioChannel channel);
    void ApplyRemoteRadioState(byte playerId, VoiceTeamRadioChannel channel);
    void SendCustomMessage(byte[] payload);
    void Rejoin();
    void Update(
        VoiceGameStateSnapshot? snapshot,
        IReadOnlyList<VoiceChatRoom.SpeakerCache> speakerCache,
        IReadOnlyList<IVoiceComponent> virtualMicrophones,
        bool localInVent,
        bool commsSabActive);
    bool TrySetRemoteVolume(byte playerId, string playerName, float volume);
    int ResetPeerMappingsNoMute();
    int CountMappedRemotePeers(VoiceGameStateSnapshot snapshot);
}

internal readonly record struct VoiceCaptureRuntimeOptions(
    bool SyntheticMicToneEnabled,
    bool MicCalibrationDiagnostics,
    bool NoiseSuppressionEnabled,
    float MicSensitivity);

internal readonly record struct VoiceBackendCustomMessage(
    byte[] Payload,
    int SenderClientId,
    byte SenderPlayerId,
    string SenderPeerId)
{
    public const int UnknownClientId = -1;
    public const byte UnknownPlayerId = byte.MaxValue;

    public static VoiceBackendCustomMessage Unknown(byte[] payload, string senderPeerId)
        => new(payload, UnknownClientId, UnknownPlayerId, senderPeerId);

    public VoiceBackendCustomMessage CopyPayload()
    {
        var copy = new byte[Payload.Length];
        Array.Copy(Payload, copy, Payload.Length);
        return new VoiceBackendCustomMessage(copy, SenderClientId, SenderPlayerId, SenderPeerId);
    }
}
