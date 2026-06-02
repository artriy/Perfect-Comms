using System.Collections.Generic;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal readonly record struct VoiceLocalOverlayState(
    bool IsMuted,
    bool IsDeafened,
    float Level,
    bool IsSpeaking,
    bool MicrophoneAvailable,
    bool SpeakerAvailable);

internal readonly record struct VoiceRemoteOverlayState(
    byte PlayerId,
    string PlayerName,
    float Level,
    bool IsSpeaking,
    bool IsAudible,
    VoiceProximityReason Reason);

internal sealed class VoiceOverlayState
{
    public static readonly VoiceOverlayState Empty = new(
        new VoiceLocalOverlayState(false, false, 0f, false, false, false),
        []);

    public VoiceLocalOverlayState Local { get; }
    // NOTE: for a non-Empty state this aliases a shared static scratch buffer that the next cache-miss
    // Build() clears and refills. It is valid ONLY within the current frame — copy the values you need into
    // your own storage before calling VoiceOverlayState.Current()/Build() again. Do not retain it across
    // frames. (Today all consumers iterate it synchronously within the frame and copy out.)
    public IReadOnlyList<VoiceRemoteOverlayState> RemotePlayers { get; }
    private static int _cachedFrame = -1;
    private static VoiceChatRoom? _cachedRoom;
    private static VoiceOverlayState _cachedState = Empty;
    // Scratch reused per frame. Build runs at most once per frame (frame-cached by Current) and is
    // always invoked on the Unity main thread. The cached state exposes this buffer directly as its
    // RemotePlayers (no per-frame array copy); this is safe because (a) consumers iterate RemotePlayers
    // synchronously within the same frame and never retain it, and (b) the next cache-miss frame clears
    // and refills the buffer before any new consumer reads it.
    private static readonly List<VoiceRemoteOverlayState> _remoteBuffer = new(16);

    private VoiceOverlayState(VoiceLocalOverlayState local, IReadOnlyList<VoiceRemoteOverlayState> remotePlayers)
    {
        Local = local;
        RemotePlayers = remotePlayers;
    }

    public static VoiceOverlayState Current(VoiceChatRoom? room)
    {
        int frame = Time.frameCount;
        if (_cachedFrame == frame && ReferenceEquals(_cachedRoom, room))
            return _cachedState;

        _cachedFrame = frame;
        _cachedRoom = room;
        _cachedState = Build(room);
        return _cachedState;
    }

    internal static void InvalidateCache()
    {
        _cachedFrame = -1;
        _cachedRoom = null;
        _cachedState = Empty;
    }

    private static VoiceOverlayState Build(VoiceChatRoom? room)
    {
        if (room == null)
            return Empty;

        var snapshot = room.CurrentSnapshot;
        _remoteBuffer.Clear();
        // Fill directly into the reused buffer (no per-frame List/array allocation in the backend getter).
        room.AppendRemoteOverlayStates(_remoteBuffer);

        // Only suppress positively not-live remotes; a briefly-null snapshot (one tick after
        // Rejoin/transport switch) keeps speakers rather than dropping all for a frame. Filter in place.
        if (snapshot != null)
        {
            for (int i = _remoteBuffer.Count - 1; i >= 0; i--)
            {
                if (!IsLiveRemoteSpeaker(_remoteBuffer[i].PlayerId, snapshot))
                    _remoteBuffer.RemoveAt(i);
            }
        }

        var local = new VoiceLocalOverlayState(
            VoiceChatHudState.IsMuted,
            VoiceChatHudState.IsSpeakerMuted,
            room.LocalMicLevel,
            room.LocalMicSpeaking && !VoiceChatHudState.IsMuted && !VoiceChatHudState.IsLocalTransmitBlocked,
            room.UsingMicrophone,
            room.UsingSpeaker);

        return new VoiceOverlayState(local, _remoteBuffer);
    }

    private static bool IsLiveRemoteSpeaker(byte playerId, VoiceGameStateSnapshot? snapshot)
    {
        if (playerId == byte.MaxValue)
            return false;

        if (snapshot == null)
            return true;

        // Absent from a partial snapshot: keep showing; only suppress players it classifies not-live.
        if (!snapshot.TryGetPlayer(playerId, out var player))
            return true;

        return !player.IsLocal
               && !player.Disconnected
               && !player.IsDummy
               && player.ClientId >= 0;
    }
}
