using System;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceCameraState
{
    private static Minigame? _activeCameraMinigame;
    private static int _activeCameraInstanceId;
    private static int _closedCameraInstanceId = int.MinValue;
    private static DateTime _lastUpdateDiagnosticUtc;
    private static int _lastLoggedOpenInstanceId = int.MinValue;
    private static int _lastLoggedUpdateInstanceId = int.MinValue;

    public static void Open(Minigame? minigame)
    {
        if (!IsCameraMinigame(minigame)) return;

        _activeCameraMinigame = minigame;
        _activeCameraInstanceId = minigame!.GetInstanceID();
        _closedCameraInstanceId = int.MinValue;
        if (_lastLoggedOpenInstanceId != _activeCameraInstanceId)
        {
            _lastLoggedOpenInstanceId = _activeCameraInstanceId;
            VoiceDiagnostics.Log("camera.state", DescribeMinigame("open", minigame));
        }
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
            VoiceDiagnostics.Log("camera.state", DescribeMinigame("close", minigame));
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

    public static void NoteUpdate(Minigame? minigame)
    {
        if (!IsCameraMinigame(minigame)) return;

        var now = DateTime.UtcNow;
        int instanceId = SafeInstanceId(minigame!);
        if (instanceId == _lastLoggedUpdateInstanceId &&
            (now - _lastUpdateDiagnosticUtc).TotalSeconds < 1.0)
            return;

        _lastLoggedUpdateInstanceId = instanceId;
        _lastUpdateDiagnosticUtc = now;
        VoiceDiagnostics.Log("camera.update", DescribeMinigame("update", minigame));
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

    private static string DescribeMinigame(string action, Minigame? minigame)
    {
        if (minigame == null)
            return $"action={action} minigame=none current={DescribeCurrentMinigame()}";

        return
            $"action={action} type={minigame.GetType().Name} instance={SafeInstanceId(minigame)} " +
            $"active={SafeActive(minigame)} current={DescribeCurrentMinigame()}";
    }

    private static string DescribeCurrentMinigame()
    {
        try
        {
            var current = Minigame.Instance;
            return current == null ? "none" : $"{current.GetType().Name}:{SafeInstanceId(current)}";
        }
        catch (Exception ex)
        {
            return $"error:{ex.GetType().Name}";
        }
    }

    private static int SafeInstanceId(Minigame minigame)
    {
        try { return minigame.GetInstanceID(); }
        catch { return 0; }
    }

    private static string SafeActive(Minigame minigame)
    {
        try { return minigame.gameObject != null && minigame.gameObject.activeInHierarchy ? "true" : "false"; }
        catch (Exception ex) { return $"error:{ex.GetType().Name}"; }
    }
}
