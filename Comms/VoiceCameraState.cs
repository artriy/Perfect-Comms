namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceCameraState
{
    private static Minigame? _activeCameraMinigame;
    private static int _activeCameraInstanceId;
    private static int _closedCameraInstanceId = int.MinValue;

    public static void Open(Minigame? minigame)
    {
        if (!IsCameraMinigame(minigame)) return;

        _activeCameraMinigame = minigame;
        _activeCameraInstanceId = minigame!.GetInstanceID();
        _closedCameraInstanceId = int.MinValue;
    }

    public static void Close(Minigame? minigame)
    {
        if (minigame == null)
        {
            Clear();
            return;
        }

        int instanceId = minigame.GetInstanceID();
        if (instanceId == _activeCameraInstanceId || IsCameraMinigame(minigame))
        {
            Clear();
            _closedCameraInstanceId = instanceId;
        }
    }

    public static void Clear()
    {
        _activeCameraMinigame = null;
        _activeCameraInstanceId = 0;
    }

    public static bool TryGetActiveMinigame(out Minigame minigame)
    {
        var current = Minigame.Instance;
        if (IsUsableCameraMinigame(current) && current!.GetInstanceID() != _closedCameraInstanceId)
        {
            Open(current);
            minigame = current;
            return true;
        }

        if (IsCameraMinigame(current))
        {
            Close(current);
            minigame = null!;
            return false;
        }

        if (IsCurrentCameraMinigame(_activeCameraMinigame))
        {
            minigame = _activeCameraMinigame!;
            return true;
        }

        Clear();
        minigame = null!;
        return false;
    }

    private static bool IsCameraMinigame(Minigame? minigame)
        => minigame is SurveillanceMinigame or PlanetSurveillanceMinigame or FungleSurveillanceMinigame;

    private static bool IsUsableCameraMinigame(Minigame? minigame)
    {
        if (!IsCameraMinigame(minigame)) return false;

        try
        {
            return minigame!.gameObject != null && minigame.gameObject.activeInHierarchy;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCurrentCameraMinigame(Minigame? minigame)
    {
        if (!IsUsableCameraMinigame(minigame)) return false;

        try
        {
            return Minigame.Instance != null && Minigame.Instance == minigame;
        }
        catch
        {
            return false;
        }
    }
}
