using System;
using System.Globalization;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class RpcSignalingSender : ISignalingSender
{
    public void Send(int targetClientId, SignalMsgType type, byte[] payload)
        => AmongUsRpcSignaling.Send(targetClientId, type, payload);
}

#if WINDOWS
internal sealed class SidecarVoiceTransport : IVoiceTransport
{
    private readonly Func<SidecarVoiceClient?> _voice;

    public SidecarVoiceTransport(Func<SidecarVoiceClient?> voice)
    {
        _voice = voice ?? throw new ArgumentNullException(nameof(voice));
    }

    public static string PeerId(int clientId) => clientId.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseClientId(string peerId, out int clientId)
        => int.TryParse(peerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out clientId);

    public void AddPeer(int clientId, bool isOfferer, bool relayOnly, int generation)
        => _voice()?.AddPeer(PeerId(clientId), isOfferer, relayOnly, generation);

    public void RemovePeer(int clientId) => _voice()?.RemovePeer(PeerId(clientId));

    public void SetRemoteSdp(int clientId, string sdpType, string sdp) => _voice()?.SetRemoteSdp(PeerId(clientId), sdpType, sdp);

    public void AddIceCandidate(int clientId, string candidate) => _voice()?.AddIceCandidate(PeerId(clientId), candidate);
}
#endif

#if ANDROID
internal sealed class MobileVoiceTransport : IVoiceTransport
{
    private readonly Func<MobileVoiceClient?> _voice;

    public MobileVoiceTransport(Func<MobileVoiceClient?> voice)
    {
        _voice = voice ?? throw new ArgumentNullException(nameof(voice));
    }

    public static string PeerId(int clientId) => clientId.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseClientId(string peerId, out int clientId)
        => int.TryParse(peerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out clientId);

    public void AddPeer(int clientId, bool isOfferer, bool relayOnly, int generation)
        => _voice()?.AddPeer(PeerId(clientId), isOfferer, relayOnly, generation);

    public void RemovePeer(int clientId) => _voice()?.RemovePeer(PeerId(clientId));

    public void SetRemoteSdp(int clientId, string sdpType, string sdp) => _voice()?.SetRemoteSdp(PeerId(clientId), sdpType, sdp);

    public void AddIceCandidate(int clientId, string candidate) => _voice()?.AddIceCandidate(PeerId(clientId), candidate);
}
#endif
