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
    public bool RequiresNativeOperationAcknowledgements => true;
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

    public bool RemovePeer(int clientId, int generation) =>
        _voice()?.RemovePeer(PeerId(clientId), generation) == true;

    public bool RestartIce(int clientId, int generation, bool createOffer) =>
        _voice()?.RestartIce(PeerId(clientId), generation, createOffer) == true;

    public bool SetRemoteSdp(int clientId, int generation, string sdpType, string sdp) =>
        _voice()?.SetRemoteSdp(PeerId(clientId), generation, sdpType, sdp) == true;

    public bool AddIceCandidate(int clientId, int generation, string candidate) =>
        _voice()?.AddIceCandidate(PeerId(clientId), generation, candidate) == true;
}
#endif

#if ANDROID
internal sealed class MobileVoiceTransport : IVoiceTransport
{
    public bool RequiresNativeOperationAcknowledgements => false;
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

    public bool RemovePeer(int clientId, int generation) =>
        _voice()?.RemovePeer(PeerId(clientId), generation) == true;

    public bool RestartIce(int clientId, int generation, bool createOffer) =>
        _voice()?.RestartIce(PeerId(clientId), generation, createOffer) == true;

    public bool SetRemoteSdp(int clientId, int generation, string sdpType, string sdp) =>
        _voice()?.SetRemoteSdp(PeerId(clientId), generation, sdpType, sdp) == true;

    public bool AddIceCandidate(int clientId, int generation, string candidate) =>
        _voice()?.AddIceCandidate(PeerId(clientId), generation, candidate) == true;
}
#endif
