using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceSceneState
{
    private const float EndGameProbeInterval = 0.25f;

    private static int _lastFrame = -1;
    private static float _nextEndGameProbeTime;
    private static bool _endGameActive;
    private static bool _endGameSceneHint;
    private static VoiceGamePhase _lastConfirmedPhase = VoiceGamePhase.Unknown;

    public static bool IsEndGameActive
    {
        get
        {
            if (_endGameSceneHint)
                return true;

            int frame = Time.frameCount;
            if (_lastFrame == frame)
                return _endGameActive;

            _lastFrame = frame;
            if (_endGameActive || Time.time >= _nextEndGameProbeTime)
            {
                _endGameActive = Object.FindObjectOfType<EndGameManager>() != null;
                _nextEndGameProbeTime = Time.time + EndGameProbeInterval;
            }

            return _endGameActive;
        }
    }

    public static VoiceGamePhase ResolvePhase()
    {
        var observed = ResolveObservedPhase();
        var resolved = StabilizeUnknownPhase(observed, _lastConfirmedPhase);
        if (observed != VoiceGamePhase.Unknown)
            _lastConfirmedPhase = observed;
        return resolved;
    }

    internal static VoiceGamePhase StabilizeUnknownPhase(
        VoiceGamePhase observed,
        VoiceGamePhase lastConfirmed)
        => observed == VoiceGamePhase.Unknown && lastConfirmed != VoiceGamePhase.Unknown
            ? lastConfirmed
            : observed;

    internal static void SetEndGameSceneHint(bool active)
    {
        _endGameSceneHint = active;
        _lastFrame = -1;
        _nextEndGameProbeTime = 0f;
        if (active)
        {
            _endGameActive = true;
            _lastConfirmedPhase = VoiceGamePhase.EndGame;
        }
    }

    private static VoiceGamePhase ResolveObservedPhase()
    {
        if (IsEndGameActive) return VoiceGamePhase.EndGame;
        if (ExileController.Instance != null) return VoiceGamePhase.Exile;
        if (MeetingHud.Instance != null) return VoiceGamePhase.Meeting;
        if (IntroCutscene.Instance != null) return VoiceGamePhase.Intro;
        if (LobbyBehaviour.Instance != null) return VoiceGamePhase.Lobby;
        if (ShipStatus.Instance != null) return VoiceGamePhase.Tasks;
        if (AmongUsClient.Instance == null) return VoiceGamePhase.Menu;
        return VoiceGamePhase.Unknown;
    }

    public static bool IsLobbyVoicePhase(VoiceGamePhase phase)
        => phase is VoiceGamePhase.Menu
            or VoiceGamePhase.Lobby
            or VoiceGamePhase.Intro
            or VoiceGamePhase.EndGame;

    public static bool IsMeetingVoicePhase(VoiceGamePhase phase)
        => phase is VoiceGamePhase.Meeting or VoiceGamePhase.Exile;

    public static bool IsTaskVoicePhase(VoiceGamePhase phase)
        => phase == VoiceGamePhase.Tasks;

    public static void Reset()
    {
        _lastFrame = -1;
        _nextEndGameProbeTime = 0f;
        _endGameActive = false;
        _endGameSceneHint = false;
        _lastConfirmedPhase = VoiceGamePhase.Unknown;
    }
}
