using System;
using System.Globalization;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class RpcSignalingSender : ISignalingSender
{
    public bool Send(int targetClientId, SignalMsgType type, byte[] payload)
        => AmongUsRpcSignaling.Send(targetClientId, type, payload);
}

#if WINDOWS
internal sealed class SidecarVoiceTransport : IVoiceTransport
{
    private readonly Func<SidecarVoiceLease?> _voice;

    public SidecarVoiceTransport(Func<SidecarVoiceLease?> voice)
    {
        _voice = voice ?? throw new ArgumentNullException(nameof(voice));
    }

    public static string PeerId(int clientId) => clientId.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseClientId(string peerId, out int clientId)
        => int.TryParse(peerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out clientId);

    public bool AddPeer(int clientId, bool isOfferer, int generation)
        => _voice()?.AddPeer(PeerId(clientId), isOfferer, generation) == true;

    public bool RemovePeer(int clientId) => _voice()?.RemovePeer(PeerId(clientId)) == true;

    public bool RestartIce(int clientId, bool createOffer) =>
        _voice()?.RestartIce(PeerId(clientId), createOffer) == true;

    public bool SetRemoteSdp(int clientId, string sdpType, string sdp) => _voice()?.SetRemoteSdp(PeerId(clientId), sdpType, sdp) == true;

    public bool AddIceCandidate(int clientId, string candidate) => _voice()?.AddIceCandidate(PeerId(clientId), candidate) == true;
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

    public bool AddPeer(int clientId, bool isOfferer, int generation)
        => _voice()?.AddPeer(PeerId(clientId), isOfferer, generation) == true;

    public bool RemovePeer(int clientId) => _voice()?.RemovePeer(PeerId(clientId)) == true;

    public bool RestartIce(int clientId, bool createOffer) =>
        _voice()?.RestartIce(PeerId(clientId), createOffer) == true;

    public bool SetRemoteSdp(int clientId, string sdpType, string sdp) => _voice()?.SetRemoteSdp(PeerId(clientId), sdpType, sdp) == true;

    public bool AddIceCandidate(int clientId, string candidate) => _voice()?.AddIceCandidate(PeerId(clientId), candidate) == true;
}
#endif
