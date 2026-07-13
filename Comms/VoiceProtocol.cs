using System;

namespace VoiceChatPlugin.VoiceChat;

[Flags]
internal enum VoiceFrameFlags : byte
{
    None = 0,
    ImpostorRadio = 1 << 0,
    BundledAudio = 1 << 7,
}

internal static class VoiceProtocol
{
    // Protocol 4 is the native WebRTC RTP + Among Us RPC signaling transport. Protocol 3 used
    // Socket.IO signaling and custom Opus data-channel frames; the two transports cannot form a
    // voice session and must never be advertised as mutually compatible in public lobby metadata.
    public const int ProtocolVersion = 4;
    public const int MinCompatibleVersion = 4;

    public const int MaxEncodedAudioBytes = 4096;
    public const int AudioSequenceBytes = 4;
    public const int AudioFlagsBytes = 1;
    public const int AudioHeaderBytes = AudioSequenceBytes + AudioFlagsBytes;
    public const int AudioBundleCountBytes = 1;
    public const int AudioBundleLengthBytes = 2;
    public const int MaxBundledAudioFrames = 2;
    public const double MaxAudioBundleWaitSeconds = 0.02;
    public const int MaxAudioPayloadBytes = AudioHeaderBytes + MaxEncodedAudioBytes;
    public const int MaxProfileNameChars = 64;
    public const int MaxQueuedSendFrames = 12;
    public const int MaxQueuedReceivePackets = 160;
    public const int JitterBufferDelayFrames = 2;
    public const int MaxJitterBufferFrames = 24;
    public const int MaxJitterFramesPerUpdate = 8;
    public const int MaxDecodedAudioFramesPerUpdate = 64;
    public const double MaxJitterBufferDelayMilliseconds = 60;
    public const int MaxSenderPacketsPerSecond = 80;
    public const int MaxSenderBytesPerSecond = 320_000;
    public const double MaxQueuedFrameAgeSeconds = 0.50;
    public const double MaxHttpQueuedFrameAgeSeconds = 0.50;
    public static readonly TimeSpan StatsLogInterval = TimeSpan.FromSeconds(5);

    public const VoiceFeatureFlags CurrentFeatures =
        VoiceFeatureFlags.ReliableAudio |
        VoiceFeatureFlags.UnreliableAudio |
        VoiceFeatureFlags.BundledAudio |
        VoiceFeatureFlags.ProfilePackets |
        VoiceFeatureFlags.AudioFrameFlags;

    internal const VoiceFrameFlags AllowedFrameFlags =
        VoiceFrameFlags.ImpostorRadio |
        VoiceFrameFlags.BundledAudio;

    public static bool IsCompatible(int remoteProtocolVersion, int remoteMinCompatibleVersion)
        => remoteProtocolVersion >= MinCompatibleVersion &&
           ProtocolVersion >= remoteMinCompatibleVersion;

    public static bool IsValidAudioPayloadLength(int length)
        => length > AudioHeaderBytes && length <= MaxAudioPayloadBytes;

}

internal static class VoiceSenderGuard
{
    public static bool IsLocalSender(int senderId, int localClientId)
        => localClientId >= 0 && senderId == localClientId;
}
