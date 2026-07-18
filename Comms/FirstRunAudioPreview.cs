using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

#if ANDROID
using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#endif

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Local-only setup probe. Desktop capture owns a peerless sidecar lease and never adds ICE or
/// peers; Android reads Unity's microphone directly. Output tests use the exact selected sidecar
/// output on desktop and Unity's current platform route on Android.
/// </summary>
internal sealed class FirstRunAudioPreview : IDisposable
{
    private enum MicrophoneRouteResolution
    {
        Ready,
        FellBackToDefault,
        WaitingForDeviceList,
    }

#if WINDOWS
    private enum DesktopFailureChannel
    {
        Microphone = 1,
        Output = 2,
    }
#endif

    private volatile float _level;
    private volatile bool _speaking;
    private readonly object _microphoneStateGate = new();
    private int _microphoneTestActive;
    private int _listening;
    private int _playingTone;
    private volatile string _microphoneStatus = "Mic check is off";
    private volatile string _outputStatus = "Test sound has not been played";
    private long _lastLevelTick;
    private long _lastSignalTick;
    private CancellationTokenSource? _toneCancellation;
    private bool _disposed;
    private long _microphonePriorityStatusUntilTick;
    private long _legacyOutputStatusUntilTick;
    private int _toneGeneration;
    private int _uiRefreshPending;
    private int _microphoneSignalDetected;
    private int _outputTestCompleted;
    private bool _micPausedForTone;

#if WINDOWS
    private SidecarVoiceLease? _lease;
    private int _desktopFailurePending;
    private volatile string _desktopFailureMessage = "The audio helper stopped";
    private int _desktopFailureChannel;
    private int _desktopFailureAffectedOutput;
    private int _desktopLeaseGeneration;
    private readonly ConcurrentQueue<SidecarPlaybackState> _playbackStates = new();
    private FirstRunSetupDraft? _pendingSpeakerDraft;
    private string _pendingOutputDevice = string.Empty;
    private ulong _pendingPlaybackGeneration;
    private long _outputReadyDeadlineTick;
    private int _waitingForOutput;
    private bool _outputSelectionAccepted;
    private ulong _activeTonePlaybackGeneration;
#endif
#if ANDROID
    private AndroidMicrophone? _androidMicrophone;
    private GameObject? _toneObject;
    private AudioSource? _toneSource;
    private AudioClip? _toneClip;
    private int _permissionGeneration;
#endif

    internal float Level => float.IsFinite(_level) ? Math.Clamp(_level, 0f, 1f) : 0f;
    internal float LiveLevel
    {
        get
        {
            if (!IsListening) return 0f;
            long age = Environment.TickCount64 - Volatile.Read(ref _lastLevelTick);
            return age >= 0 && age <= 300 ? Level : 0f;
        }
    }
    internal bool IsMicrophoneTestActive => Volatile.Read(ref _microphoneTestActive) != 0;
    internal bool IsListening => Volatile.Read(ref _listening) != 0;
    internal bool IsPlayingTone => Volatile.Read(ref _playingTone) != 0;
    internal bool IsPreparingTone =>
#if WINDOWS
        Volatile.Read(ref _waitingForOutput) != 0;
#else
        false;
#endif
    internal bool IsSpeakerTestBusy => IsPlayingTone || IsPreparingTone;
    internal bool ConsumeUiRefresh() => Interlocked.Exchange(ref _uiRefreshPending, 0) != 0;
    internal bool MicrophoneSignalDetected => Volatile.Read(ref _microphoneSignalDetected) != 0;
    internal bool OutputTestCompleted => Volatile.Read(ref _outputTestCompleted) != 0;
    internal string OutputStatus => _outputStatus;

    internal string MicrophoneStatus
    {
        get
        {
            long now = Environment.TickCount64;
            if (!IsListening || now < Volatile.Read(ref _microphonePriorityStatusUntilTick))
                return _microphoneStatus;
            long signalSilentFor = now - Volatile.Read(ref _lastSignalTick);
            long callbackSilentFor = now - Volatile.Read(ref _lastLevelTick);
            if (signalSilentFor > 2500 || callbackSilentFor > 2500)
                return "No microphone signal detected";
            float level = Level;
            if (level >= 0.90f) return "Very loud - lower Mic Volume";
            if (level >= 0.035f || _speaking) return "Great - your microphone is working";
            return "Listening - speak normally";
        }
    }

    /// <summary>
    /// Compatibility projection for the existing single-status setup UI. New UI should bind to
    /// MicrophoneStatus and OutputStatus separately.
    /// </summary>
    internal string Status =>
        IsSpeakerTestBusy || Environment.TickCount64 < Volatile.Read(ref _legacyOutputStatusUntilTick)
            ? OutputStatus
            : MicrophoneStatus;

    internal void InvalidateMicrophoneVerification()
    {
        lock (_microphoneStateGate)
        {
            Interlocked.Exchange(ref _microphoneSignalDetected, 0);
            _level = 0f;
            _speaking = false;
            SetMicrophoneStatus(IsMicrophoneTestActive
                ? "Restarting mic check for the selected input..."
                : "Mic check needed for the selected input");
        }
    }

    internal void InvalidateOutputVerification()
    {
        Interlocked.Exchange(ref _outputTestCompleted, 0);
        SetOutputStatus("Test the selected output to verify it");
    }

    internal void Tick()
    {
#if WINDOWS
        if (Interlocked.Exchange(ref _desktopFailurePending, 0) != 0)
        {
            int channel = Volatile.Read(ref _desktopFailureChannel);
            bool affectedOutput = Volatile.Read(ref _desktopFailureAffectedOutput) != 0;
            if (channel == (int)DesktopFailureChannel.Output)
                FailOutput(_desktopFailureMessage);
            else
            {
                FailMicrophone(_desktopFailureMessage);
                if (affectedOutput)
                    FailOutput("The audio helper stopped during the speaker test");
            }
            StopDesktopLease();
        }
        TickDesktopOutputReadiness();
#endif
#if ANDROID
        _androidMicrophone?.Tick();
        if (_toneSource != null && !_toneSource.isPlaying && IsPlayingTone)
        {
            Volatile.Write(ref _playingTone, 0);
            CleanupAndroidTone();
            CompleteOutputTest();
        }
#endif
    }

    internal void StartMicrophone(FirstRunSetupDraft draft)
    {
        if (_disposed) return;
#if WINDOWS
        var microphoneRoute = ResolveMicrophoneRoute(draft);
        if (microphoneRoute == MicrophoneRouteResolution.WaitingForDeviceList)
        {
            SetMicrophonePriorityStatus(
                "Still checking the saved microphone - try Mic Check again in a moment", 3500);
            return;
        }
#endif
        StopMicrophone();
        lock (_microphoneStateGate)
        {
            Volatile.Write(ref _microphoneTestActive, 1);
            Interlocked.Exchange(ref _microphoneSignalDetected, 0);
            _micPausedForTone = false;
            SetMicrophoneStatus("Starting microphone check...");
        }

#if WINDOWS
        int generation = Interlocked.Increment(ref _desktopLeaseGeneration);
        var callbacks = new SidecarVoiceCallbacks(
            (_, _) => { },
            reason => FailDesktop(
                "Microphone helper stopped: " + reason, generation, DesktopFailureChannel.Microphone),
            (_, message) => FailDesktop(
                "Microphone unavailable: " + message, generation, DesktopFailureChannel.Microphone),
            (_, _, _, _) => { },
            (_, _, _) => { },
            (_, _, _) => { },
            OnLevel,
            _ => { },
            OnPlaybackState);
        _lease = SidecarVoiceHost.TryAcquire(callbacks, out string failure);
        if (_lease == null)
        {
            FailMicrophone(failure.StartsWith("lease-active", StringComparison.Ordinal)
                ? "Mic check is available from the main menu"
                : "Could not start microphone check");
            return;
        }

        if (!_lease.EnsureStarted(draft.MicrophoneDevice, draft.SpeakerDevice))
        {
            FailMicrophone("Could not start the Perfect Comms audio helper");
            StopDesktopLease();
            return;
        }
        _lease.SetDsp(
            aec: draft.EchoCancellation,
            agc: false,
            ns: draft.NoiseSuppression,
            nsVeryHigh: draft.StrongerNoiseSuppression,
            hpf: true);
        _lease.SetInput(draft.MicVolume, EffectiveVadThreshold(draft), EffectiveNoiseGateThreshold(draft));
        _lease.SelectMicDevice(draft.MicrophoneDevice);
        _lease.SetMicActive(true);
        MarkListening();
        if (microphoneRoute == MicrophoneRouteResolution.FellBackToDefault)
            SetMicrophonePriorityStatus(
                "The saved microphone is unavailable - Mic Check is using Default", 6500);
#elif ANDROID
        if (Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            StartAndroidCaptureAfterPermission(draft);
            return;
        }

        int generation = ++_permissionGeneration;
        FirstRunSetupPermissionRequester.Request(granted =>
        {
            if (_disposed || generation != _permissionGeneration) return;
            if (!granted)
            {
                FailMicrophone("Microphone permission denied - receive-only still works");
                return;
            }
            StartAndroidCaptureAfterPermission(draft);
        });
#else
        FailMicrophone("Microphone check is unavailable on this platform build");
#endif
    }

    internal void RefreshMicrophoneSettings(FirstRunSetupDraft draft)
    {
        if (!IsListening || IsPlayingTone) return;
#if WINDOWS
        _lease?.SetDsp(
            aec: draft.EchoCancellation,
            agc: false,
            ns: draft.NoiseSuppression,
            nsVeryHigh: draft.StrongerNoiseSuppression,
            hpf: true);
        _lease?.SetInput(draft.MicVolume, EffectiveVadThreshold(draft), EffectiveNoiseGateThreshold(draft));
#elif ANDROID
        _androidMicrophone?.SetVolume(draft.MicVolume);
#endif
    }

    internal void RestartForDeviceChange(FirstRunSetupDraft draft)
    {
        if (IsMicrophoneTestActive && !IsPlayingTone) StartMicrophone(draft);
    }

    internal void StopMicrophone()
    {
        lock (_microphoneStateGate)
        {
            Volatile.Write(ref _microphoneTestActive, 0);
            Volatile.Write(ref _listening, 0);
            _level = 0f;
            _speaking = false;
            SetMicrophoneStatus("Mic check is off");
        }
#if WINDOWS
        StopDesktopLease();
#elif ANDROID
        _permissionGeneration++;
        if (_androidMicrophone != null)
        {
            _androidMicrophone.Dispose();
            _androidMicrophone = null;
        }
#endif
    }

    internal void PlayTestSound(FirstRunSetupDraft draft)
    {
        if (_disposed || IsSpeakerTestBusy) return;
        if (draft.MasterVolume < 0.099f)
        {
            FailOutput("Speaker Volume is below the supported 10% minimum", 4000);
            return;
        }

#if WINDOWS
        if (_lease == null)
        {
            // A speaker-only test still uses the same peerless helper lease.
            int generation = Interlocked.Increment(ref _desktopLeaseGeneration);
            var callbacks = new SidecarVoiceCallbacks(
                (_, _) => { },
                reason => FailDesktop(
                    "Audio helper stopped: " + reason, generation, DesktopFailureChannel.Output),
                (_, message) => FailDesktop(
                    "Speaker unavailable: " + message, generation, DesktopFailureChannel.Output),
                (_, _, _, _) => { },
                (_, _, _) => { },
                (_, _, _) => { },
                OnLevel,
                _ => { },
                OnPlaybackState);
            _lease = SidecarVoiceHost.TryAcquire(callbacks, out string failure);
            if (_lease == null || !_lease.EnsureStarted(draft.MicrophoneDevice, draft.SpeakerDevice))
            {
                FailOutput(failure.StartsWith("lease-active", StringComparison.Ordinal)
                    ? "Speaker test is available from the main menu"
                    : "Could not start speaker test");
                StopDesktopLease();
                return;
            }
        }

        BeginDesktopOutputTest(draft);
#elif ANDROID
        PauseMicrophoneForTone();
        StartAndroidTone(draft.MasterVolume);
#else
        FailOutput("Speaker test is unavailable on this platform build");
#endif
    }

    private void OnLevel(float peak, bool speaking)
    {
        lock (_microphoneStateGate)
        {
            if (!IsListening) return;
            _level = float.IsFinite(peak) ? Math.Clamp(peak, 0f, 1f) : 0f;
            _speaking = speaking;
            long now = Environment.TickCount64;
            Volatile.Write(ref _lastLevelTick, now);
            if (speaking || peak >= 0.035f)
            {
                Volatile.Write(ref _lastSignalTick, now);
                Interlocked.Exchange(ref _microphoneSignalDetected, 1);
            }
        }
    }

    private void MarkListening()
    {
        lock (_microphoneStateGate)
        {
            long now = Environment.TickCount64;
            Volatile.Write(ref _lastLevelTick, now);
            Volatile.Write(ref _lastSignalTick, now);
            Volatile.Write(ref _microphoneTestActive, 1);
            Volatile.Write(ref _listening, 1);
            SetMicrophoneStatus("Listening - speak normally");
        }
    }

    private void FailMicrophone(string message)
    {
        lock (_microphoneStateGate)
        {
            Volatile.Write(ref _microphoneTestActive, 0);
            Volatile.Write(ref _listening, 0);
            Interlocked.Exchange(ref _microphoneSignalDetected, 0);
            SetMicrophoneStatus(message);
            _level = 0f;
            _speaking = false;
        }
    }

    private void SetMicrophoneStatus(string message)
    {
        _microphoneStatus = message;
        Volatile.Write(ref _microphonePriorityStatusUntilTick, 0);
    }

    private void SetMicrophonePriorityStatus(string message, int milliseconds)
    {
        _microphoneStatus = message;
        Volatile.Write(
            ref _microphonePriorityStatusUntilTick,
            Environment.TickCount64 + Math.Max(0, milliseconds));
    }

    private void SetOutputStatus(string message, int legacyMilliseconds = 0)
    {
        _outputStatus = message;
        Volatile.Write(
            ref _legacyOutputStatusUntilTick,
            legacyMilliseconds > 0 ? Environment.TickCount64 + legacyMilliseconds : 0);
    }

    private void FailOutput(string message, int legacyMilliseconds = 6000)
    {
        Interlocked.Exchange(ref _outputTestCompleted, 0);
        SetOutputStatus(message, legacyMilliseconds);
    }

    private void CompleteOutputTest()
    {
        Interlocked.Exchange(ref _outputTestCompleted, 1);
        SetOutputStatus("Test sound completed - did you hear it?", 6000);
    }

    private void PauseMicrophoneForTone()
    {
        _micPausedForTone = IsMicrophoneTestActive;
        if (!_micPausedForTone) return;
#if WINDOWS
        try { _lease?.SetMicActive(false); } catch { }
#elif ANDROID
        _permissionGeneration++;
        StopAndroidCaptureOnly();
#endif
        lock (_microphoneStateGate)
        {
            Volatile.Write(ref _microphoneTestActive, 0);
            Volatile.Write(ref _listening, 0);
            _level = 0f;
            _speaking = false;
            SetMicrophoneStatus("Mic check paused during the speaker test - restart it when ready");
        }
    }

    internal void StopAllTests()
    {
        bool outputWasBusy = IsSpeakerTestBusy;
        CancelTone();
#if WINDOWS
        CancelDesktopOutputWait();
#elif ANDROID
        CleanupAndroidTone();
#endif
        StopMicrophone();
        if (outputWasBusy)
            FailOutput("Output test paused", 2500);
    }

    private static float EffectiveVadThreshold(FirstRunSetupDraft draft)
        => draft.BaseVadThreshold / Math.Max(0.25f, draft.MicSensitivity);

    private static float EffectiveNoiseGateThreshold(FirstRunSetupDraft draft)
        => (VoiceSettings.Instance?.NoiseGateThreshold.Value ?? 0.003f) /
           Math.Max(0.25f, draft.MicSensitivity);

    private MicrophoneRouteResolution ResolveMicrophoneRoute(FirstRunSetupDraft draft)
    {
        if (string.IsNullOrEmpty(draft.MicrophoneDevice) || draft.MicrophoneIndex() >= 0)
            return MicrophoneRouteResolution.Ready;
#if WINDOWS
        if (VoiceChatLocalSettings.SidecarDeviceProbePending)
            return MicrophoneRouteResolution.WaitingForDeviceList;
#endif
        draft.MicrophoneDevice = string.Empty;
        Interlocked.Exchange(ref _uiRefreshPending, 1);
        return MicrophoneRouteResolution.FellBackToDefault;
    }

#if WINDOWS
    private void BeginDesktopOutputTest(FirstRunSetupDraft draft)
    {
        CancelTone();
        while (_playbackStates.TryDequeue(out _)) { }
        _pendingSpeakerDraft = draft;
        _pendingOutputDevice = draft.SpeakerDevice ?? string.Empty;
        _pendingPlaybackGeneration = 0;
        _outputSelectionAccepted = false;
        _outputReadyDeadlineTick = Environment.TickCount64 + 5000;
        Volatile.Write(ref _waitingForOutput, 1);
        Interlocked.Exchange(ref _outputTestCompleted, 0);
        SetOutputStatus("Opening the selected speaker...");
        try
        {
            _lease?.SelectOutputDevice(_pendingOutputDevice);
        }
        catch (Exception ex)
        {
            CancelDesktopOutputWait();
            FailOutput("Could not select that speaker: " + ex.Message, 5000);
        }
    }

    private void OnPlaybackState(SidecarPlaybackState state)
        => _playbackStates.Enqueue(state);

    private void TickDesktopOutputReadiness()
    {
        while (_playbackStates.TryDequeue(out var state))
        {
            if (Volatile.Read(ref _waitingForOutput) == 0)
            {
                if (IsPlayingTone && state.State == "error" &&
                    _activeTonePlaybackGeneration != 0 &&
                    state.StreamGeneration == _activeTonePlaybackGeneration)
                {
                    CancelTone();
                    _activeTonePlaybackGeneration = 0;
                    FailOutput("The selected speaker stopped during the test");
                }
                continue;
            }

            if (state.State == "command-accepted" &&
                state.Action == "select-output-device" &&
                RequestedOutputMatches(state))
            {
                _outputSelectionAccepted = true;
                SetOutputStatus("Waiting for the selected speaker...");
                continue;
            }
            if (!_outputSelectionAccepted) continue;

            if (state.State == "error")
            {
                CancelDesktopOutputWait();
                FailOutput("Could not open the selected speaker", 5000);
                continue;
            }
            if (state.State == "stream-started" && RequestedOutputMatches(state))
            {
                if (state.FellBackToDefault || (!_pendingOutputDevice.Equals(string.Empty) && !state.RequestedMatched))
                {
                    if (_pendingSpeakerDraft != null) _pendingSpeakerDraft.SpeakerDevice = string.Empty;
                    Interlocked.Exchange(ref _uiRefreshPending, 1);
                    CancelDesktopOutputWait();
                    FailOutput(
                        "That speaker is no longer available - switched to Default. Press Play Test Sound again.",
                        6500);
                    continue;
                }
                _pendingPlaybackGeneration = state.StreamGeneration;
                SetOutputStatus("Speaker is ready - starting the chime...");
                continue;
            }
            if (state.State == "first-callback" &&
                _pendingPlaybackGeneration != 0 &&
                state.StreamGeneration == _pendingPlaybackGeneration)
            {
                var draft = _pendingSpeakerDraft;
                _activeTonePlaybackGeneration = _pendingPlaybackGeneration;
                CancelDesktopOutputWait();
                if (draft == null) continue;
                PauseMicrophoneForTone();
                StartDesktopTone(draft.MasterVolume);
            }
        }

        if (Volatile.Read(ref _waitingForOutput) != 0 &&
            Environment.TickCount64 >= Volatile.Read(ref _outputReadyDeadlineTick))
        {
            CancelDesktopOutputWait();
            FailOutput("The selected speaker did not become ready - try again or choose Default");
        }
    }

    private bool RequestedOutputMatches(SidecarPlaybackState state)
        => string.IsNullOrEmpty(_pendingOutputDevice)
            ? state.RequestedDefault || string.IsNullOrEmpty(state.RequestedDevice)
            : string.Equals(_pendingOutputDevice, state.RequestedDevice, StringComparison.OrdinalIgnoreCase);

    private void CancelDesktopOutputWait()
    {
        Volatile.Write(ref _waitingForOutput, 0);
        _outputSelectionAccepted = false;
        _pendingPlaybackGeneration = 0;
        _pendingSpeakerDraft = null;
        _pendingOutputDevice = string.Empty;
    }

    private void StartDesktopTone(float volume)
    {
        CancelTone();
        var toneLease = _lease;
        int leaseGeneration = Volatile.Read(ref _desktopLeaseGeneration);
        int toneGeneration = Interlocked.Increment(ref _toneGeneration);
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _toneCancellation = cancellation;
        Volatile.Write(ref _playingTone, 1);
        Interlocked.Exchange(ref _outputTestCompleted, 0);
        SetOutputStatus("Playing test sound...");
        if (Volatile.Read(ref _desktopFailurePending) != 0)
        {
            try { cancellation.Cancel(); } catch { }
            Volatile.Write(ref _playingTone, 0);
        }
        _ = Task.Run(async () =>
        {
            try
            {
                for (int frame = 0; frame < FirstRunToneGenerator.FrameCount; frame++)
                {
                    token.ThrowIfCancellationRequested();
                    // This worker must remain pure managed. FirstRunToneGenerator intentionally
                    // has no engine/IL2CPP dependency.
                    toneLease?.SendOutputTestFrame(FirstRunToneGenerator.CreateFrame(frame, volume));
                    await Task.Delay(FirstRunToneGenerator.FrameMilliseconds, token).ConfigureAwait(false);
                }
                await Task.Delay(220, token).ConfigureAwait(false);
                if (DesktopToneIsCurrent(toneLease, leaseGeneration, toneGeneration, cancellation))
                    CompleteOutputTest();
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (DesktopToneIsCurrent(toneLease, leaseGeneration, toneGeneration, cancellation))
                    FailOutput("Could not play the test sound");
            }
            finally
            {
                if (toneGeneration == Volatile.Read(ref _toneGeneration))
                {
                    Volatile.Write(ref _playingTone, 0);
                    _activeTonePlaybackGeneration = 0;
                }
                Interlocked.CompareExchange(ref _toneCancellation, null, cancellation);
                cancellation.Dispose();
            }
        });
    }

    private bool DesktopToneIsCurrent(
        SidecarVoiceLease? toneLease,
        int leaseGeneration,
        int toneGeneration,
        CancellationTokenSource cancellation)
        => !_disposed && !cancellation.IsCancellationRequested &&
           toneGeneration == Volatile.Read(ref _toneGeneration) &&
           leaseGeneration == Volatile.Read(ref _desktopLeaseGeneration) &&
           Volatile.Read(ref _desktopFailurePending) == 0 &&
           ReferenceEquals(_lease, toneLease);

    private void StopDesktopLease()
    {
        CancelTone();
        Interlocked.Increment(ref _desktopLeaseGeneration);
        Interlocked.Exchange(ref _desktopFailurePending, 0);
        Volatile.Write(ref _desktopFailureChannel, 0);
        Volatile.Write(ref _desktopFailureAffectedOutput, 0);
        CancelDesktopOutputWait();
        _activeTonePlaybackGeneration = 0;
        Volatile.Write(ref _listening, 0);
        var lease = _lease;
        _lease = null;
        if (lease == null) return;
        try { lease.SetMicActive(false); } catch { }
        try { lease.Dispose(); } catch { }
    }

    private void FailDesktop(
        string message,
        int generation,
        DesktopFailureChannel channel)
    {
        if (generation != Volatile.Read(ref _desktopLeaseGeneration)) return;
        _desktopFailureMessage = message;
        Volatile.Write(ref _desktopFailureChannel, (int)channel);
        Volatile.Write(ref _desktopFailureAffectedOutput, IsSpeakerTestBusy ? 1 : 0);
        Interlocked.Exchange(ref _desktopFailurePending, 1);
        try { Volatile.Read(ref _toneCancellation)?.Cancel(); } catch { }
        Volatile.Write(ref _playingTone, 0);
    }
#endif

#if ANDROID
    private FirstRunSetupDraft? _androidDraft;

    private void StartAndroidCaptureAfterPermission(FirstRunSetupDraft? draft = null)
    {
        if (draft != null) _androidDraft = draft;
        var activeDraft = draft ?? _androidDraft;
        if (_disposed || activeDraft == null) return;
        if (VoiceChatRoom.Current != null)
        {
            FailMicrophone("Mic Check is available from the main menu, outside a voice room");
            StopAndroidCaptureOnly();
            return;
        }
        VoiceChatLocalSettings.RefreshDeviceLists();
        var microphoneRoute = ResolveMicrophoneRoute(activeDraft);
        StopAndroidCaptureOnly();
        _androidMicrophone = new AndroidMicrophone { ReuseBuffer = true };
        _androidMicrophone.SetVolume(activeDraft.MicVolume);
        _androidMicrophone.DataAvailable += OnAndroidSamples;
        if (!_androidMicrophone.Start(activeDraft.MicrophoneDevice))
        {
            FailMicrophone(_androidMicrophone.LeaseUnavailable
                ? "Mic Check is unavailable while voice chat is using the microphone"
                : "Could not open the Android microphone");
            StopAndroidCaptureOnly();
            return;
        }
        MarkListening();
        if (microphoneRoute == MicrophoneRouteResolution.FellBackToDefault)
            SetMicrophonePriorityStatus(
                "The saved microphone is unavailable - Mic Check is using Default", 6500);
    }

    private void OnAndroidSamples(float[] samples, int count)
    {
        float peak = 0f;
        for (int i = 0; i < count; i++)
            peak = Math.Max(peak, Math.Abs(samples[i]));
        OnLevel(peak, peak >= EffectiveVadThreshold(_androidDraft!));
    }

    private void StopAndroidCaptureOnly()
    {
        if (_androidMicrophone == null) return;
        _androidMicrophone.DataAvailable -= OnAndroidSamples;
        _androidMicrophone.Dispose();
        _androidMicrophone = null;
    }

    private void StartAndroidTone(float volume)
    {
        CancelTone();
        CleanupAndroidTone();
        var all = new float[FirstRunToneGenerator.FrameCount * SidecarProtocol.AudioOutSamples];
        for (int frame = 0; frame < FirstRunToneGenerator.FrameCount; frame++)
            Array.Copy(FirstRunToneGenerator.CreateFrame(frame, volume), 0, all,
                frame * SidecarProtocol.AudioOutSamples, SidecarProtocol.AudioOutSamples);

        _toneObject = new GameObject("PerfectComms_SetupTestSound");
        Object.DontDestroyOnLoad(_toneObject);
        _toneSource = _toneObject.AddComponent<AudioSource>();
        _toneSource.volume = 1f;
        _toneClip = AudioClip.Create("PerfectComms Setup Chime",
            all.Length / 2, 2, FirstRunToneGenerator.SampleRate, false);
        var il2cpp = new Il2CppStructArray<float>(all.Length);
        for (int i = 0; i < all.Length; i++) il2cpp[i] = all[i];
        _toneClip.SetData(il2cpp, 0);
        _toneSource.clip = _toneClip;
        Volatile.Write(ref _playingTone, 1);
        Interlocked.Exchange(ref _outputTestCompleted, 0);
        SetOutputStatus("Playing test sound...");
        _toneSource.Play();
    }

    private void CleanupAndroidTone()
    {
        if (_toneSource != null) _toneSource.Stop();
        if (_toneClip != null) Object.Destroy(_toneClip);
        if (_toneObject != null) Object.Destroy(_toneObject);
        _toneSource = null;
        _toneClip = null;
        _toneObject = null;
    }
#endif

    private void CancelTone()
    {
        Interlocked.Increment(ref _toneGeneration);
        Volatile.Write(ref _playingTone, 0);
        var cancellation = Interlocked.Exchange(ref _toneCancellation, null);
        if (cancellation == null) return;
        try { cancellation.Cancel(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelTone();
        StopMicrophone();
#if ANDROID
        CleanupAndroidTone();
#endif
    }
}

#if ANDROID
internal sealed class FirstRunSetupPermissionRequester : MonoBehaviour
{
    private static bool _registered;
    private static FirstRunSetupPermissionRequester? _instance;

    internal static void Request(Action<bool> completed)
    {
        if (!_registered)
        {
            ClassInjector.RegisterTypeInIl2Cpp<FirstRunSetupPermissionRequester>();
            _registered = true;
        }
        if (_instance == null)
            _instance = VoiceUiKit.Canvas.gameObject.AddComponent<FirstRunSetupPermissionRequester>();
        _instance.StartCoroutine(_instance.RequestRoutine(completed).WrapToIl2Cpp());
    }

    public FirstRunSetupPermissionRequester(IntPtr ptr) : base(ptr) { }

    private IEnumerator RequestRoutine(Action<bool> completed)
    {
        yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
        completed(Application.HasUserAuthorization(UserAuthorization.Microphone));
    }
}
#endif
