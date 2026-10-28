namespace VoiceChatPlugin.VoiceChat;

internal enum VoiceHelperBlockKind
{
    None,
    Suspected,
    Confirmed,
}

#if WINDOWS
internal enum VoiceHelperFailureStage
{
    None,
    Extract,
    InspectHelper,
    HelperMissing,
    HelperChanged,
    ProbeStart,
    Start,
    ExitedBeforeHandshake,
    HandshakeTimeout,
}

/// <summary>
/// Where a native helper launch failed and the Win32 error code observed there, if any.
/// <see cref="ExtractionStage"/> is the native cache write stage that failed during extraction.
/// </summary>
internal readonly record struct VoiceHelperLaunchFailure(
    VoiceHelperFailureStage Stage,
    int NativeErrorCode = 0,
    string ExtractionStage = "")
{
    internal static VoiceHelperLaunchFailure FromException(VoiceHelperFailureStage stage, System.Exception ex)
        => new(stage, VoiceHelperBlockClassifier.NativeErrorCode(ex));

    internal static VoiceHelperLaunchFailure FromExtraction(System.Exception ex)
    {
        var extractionStage = string.Empty;
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is NativeCacheExtractionException cacheFailure)
            {
                extractionStage = cacheFailure.Stage;
                break;
            }
        }
        return new VoiceHelperLaunchFailure(
            VoiceHelperFailureStage.Extract,
            VoiceHelperBlockClassifier.NativeErrorCode(ex),
            extractionStage);
    }
}

internal static class VoiceHelperBlockClassifier
{
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorVirusInfected = 225;
    internal const int ErrorVirusDeleted = 226;
    internal const int ErrorAccessDisabledByPolicy = 1260;
    internal const int ErrorSystemIntegrityPolicyViolation = 4551;
    internal const int ErrorSystemIntegrityTooManyPolicies = 4556;
    internal const int SuspectedHandshakeFailureStreak = 3;

    /// <summary>
    /// Maps one native Windows helper launch failure to how sure we are that antivirus or
    /// application control is blocking the helper. Path, elevation and Wine failures never qualify.
    /// </summary>
    internal static VoiceHelperBlockKind Classify(
        VoiceHelperLaunchFailure failure,
        int consecutiveHandshakeFailures,
        bool nativeWindows)
    {
        if (!nativeWindows || failure.Stage == VoiceHelperFailureStage.None)
            return VoiceHelperBlockKind.None;

        var code = failure.NativeErrorCode;
        if (code is ErrorVirusInfected or ErrorVirusDeleted)
            return VoiceHelperBlockKind.Confirmed;

        switch (failure.Stage)
        {
            case VoiceHelperFailureStage.HelperMissing:
            case VoiceHelperFailureStage.HelperChanged:
                return VoiceHelperBlockKind.Confirmed;
            case VoiceHelperFailureStage.InspectHelper:
            case VoiceHelperFailureStage.ProbeStart:
            case VoiceHelperFailureStage.Start:
                return code is ErrorAccessDenied
                    or ErrorAccessDisabledByPolicy
                    or ErrorSystemIntegrityPolicyViolation
                    or ErrorSystemIntegrityTooManyPolicies
                    ? VoiceHelperBlockKind.Confirmed
                    : VoiceHelperBlockKind.None;
            case VoiceHelperFailureStage.Extract:
                return code == ErrorAccessDenied &&
                       failure.ExtractionStage is "write-temp" or "publish"
                    ? VoiceHelperBlockKind.Suspected
                    : VoiceHelperBlockKind.None;
            case VoiceHelperFailureStage.ExitedBeforeHandshake:
            case VoiceHelperFailureStage.HandshakeTimeout:
                return consecutiveHandshakeFailures >= SuspectedHandshakeFailureStreak
                    ? VoiceHelperBlockKind.Suspected
                    : VoiceHelperBlockKind.None;
            default:
                return VoiceHelperBlockKind.None;
        }
    }

    /// <summary>
    /// First Win32 error code in the exception chain, from <c>Win32Exception.NativeErrorCode</c>
    /// or a <c>0x8007xxxx</c> HRESULT (how .NET surfaces Win32 file errors).
    /// </summary>
    internal static int NativeErrorCode(System.Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is System.ComponentModel.Win32Exception win32 && win32.NativeErrorCode != 0)
                return win32.NativeErrorCode;
            var hresult = current.HResult;
            if ((hresult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000))
                return hresult & 0xFFFF;
        }
        return 0;
    }
}

/// <summary>
/// Counts consecutive helper exits before handshake across launch attempts. One instance is
/// shared by every production helper launch.
/// </summary>
internal sealed class VoiceHelperBlockTracker
{
    private readonly object _gate = new();
    private int _handshakeFailureStreak;

    internal VoiceHelperBlockKind Observe(VoiceHelperLaunchFailure failure, bool nativeWindows)
    {
        lock (_gate)
        {
            _handshakeFailureStreak =
                failure.Stage is VoiceHelperFailureStage.ExitedBeforeHandshake
                    or VoiceHelperFailureStage.HandshakeTimeout
                    ? _handshakeFailureStreak + 1
                    : 0;
            return VoiceHelperBlockClassifier.Classify(failure, _handshakeFailureStreak, nativeWindows);
        }
    }

    internal void Reset()
    {
        lock (_gate)
            _handshakeFailureStreak = 0;
    }
}

internal enum HelperRetryOutcome
{
    /// <summary>A local voice refresh started; close the notice.</summary>
    RefreshStarted,
    /// <summary>The refresh cooldown is running; keep the notice open with Retry disabled.</summary>
    CoolingDown,
    /// <summary>No voice room exists; forget the block so the next helper start re-detects it.</summary>
    ClearedWithoutRoom,
}

internal static class HelperRetryPolicy
{
    internal static HelperRetryOutcome Decide(bool hasRoom, bool refreshStarted)
    {
        if (!hasRoom)
            return HelperRetryOutcome.ClearedWithoutRoom;
        return refreshStarted ? HelperRetryOutcome.RefreshStarted : HelperRetryOutcome.CoolingDown;
    }
}

/// <summary>
/// Current antivirus block for the native helper. Written from helper launch threads, read by
/// the UI on the main thread. An episode starts when a block is first reported after a clear.
/// </summary>
internal static class VoiceHelperBlockState
{
    private static readonly object Gate = new();
    private static VoiceHelperBlockKind _kind;
    private static string _cacheRoot = string.Empty;
    private static int _episode;

    internal static VoiceHelperBlockKind Kind
    {
        get { lock (Gate) return _kind; }
    }

    internal static string CacheRoot
    {
        get { lock (Gate) return _cacheRoot; }
    }

    internal static int Episode
    {
        get { lock (Gate) return _episode; }
    }

    internal static bool IsBlocked => Kind != VoiceHelperBlockKind.None;

    internal static void Report(VoiceHelperBlockKind kind, string cacheRoot)
    {
        if (kind == VoiceHelperBlockKind.None)
            return;
        lock (Gate)
        {
            if (_kind == VoiceHelperBlockKind.None)
                _episode++;
            // A weaker signal later in the same episode must not soften confirmed evidence.
            if (kind > _kind)
                _kind = kind;
            if (!string.IsNullOrWhiteSpace(cacheRoot))
                _cacheRoot = cacheRoot;
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            _kind = VoiceHelperBlockKind.None;
        }
    }
}
#endif
