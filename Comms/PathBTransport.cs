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

    public void AddPeer(int clientId) => _voice()?.AddPeer(PeerId(clientId));

    public void RemovePeer(int clientId) => _voice()?.RemovePeer(PeerId(clientId));

    public void SetRemoteSdp(int clientId, string sdpType, string sdp) => _voice()?.SetRemoteSdp(PeerId(clientId), sdpType, sdp);

    public void AddIceCandidate(int clientId, string candidate) => _voice()?.AddIceCandidate(PeerId(clientId), candidate);
}
#endif

#if ANDROID
internal sealed class SipsorceryVoiceTransport : IVoiceTransport
{
    private readonly BetterCrewLinkVoiceBackend _backend;

    public SipsorceryVoiceTransport(BetterCrewLinkVoiceBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public static string PeerId(int clientId) => clientId.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseClientId(string peerId, out int clientId)
        => int.TryParse(peerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out clientId);

    public void AddPeer(int clientId) => _backend.RpcAddPeer(PeerId(clientId));

    public void RemovePeer(int clientId) => _backend.RpcClosePeer(PeerId(clientId));

    public void SetRemoteSdp(int clientId, string sdpType, string sdp) => _backend.RpcApplyRemoteSdp(PeerId(clientId), sdpType, sdp);

    public void AddIceCandidate(int clientId, string candidate) => _backend.RpcApplyRemoteCandidate(PeerId(clientId), candidate);
}
#endif
