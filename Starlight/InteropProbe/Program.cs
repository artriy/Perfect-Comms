using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PerfectComms.Starlight.Media;

namespace PerfectComms.Starlight.InteropProbe;

internal static class Program
{
    private const string SuccessLine = "starlight-pion-interop.ok";
    private const string FailurePrefix = "starlight-pion-interop.failed code=";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            ProbeOptions options = ProbeOptions.Parse(args);
            NativePion.Configure(options.PionPath);
            await RunAsync(options.Timeout).ConfigureAwait(false);
            Console.WriteLine(SuccessLine);
            return 0;
        }
        catch (ProbeFailureException failure)
        {
            Console.Error.WriteLine(FailurePrefix + failure.Code);
            return 1;
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("PERFECTCOMMS_INTEROP_DIAGNOSTICS") == "1")
            {
                string? missingAssembly = ex is FileNotFoundException fileNotFound
                    ? GetMissingAssemblyName(fileNotFound)
                    : null;
                Console.Error.WriteLine(
                    FailurePrefix + "unexpected" +
                    $" type={ex.GetType().FullName} hresult={ex.HResult}" +
                    (missingAssembly is null ? string.Empty : $" assembly={missingAssembly}"));
            }
            else
                Console.Error.WriteLine(FailurePrefix + "unexpected");
            return 1;
        }
    }

    private static string? GetMissingAssemblyName(FileNotFoundException exception)
    {
        if (exception.FileName is null)
            return null;

        try
        {
            string? assemblyName = new AssemblyName(exception.FileName).Name;
            if (assemblyName is null)
                return null;

            string sanitized = string.Concat(
                assemblyName.Where(character => char.IsAsciiLetterOrDigit(character) ||
                                                character is '.' or '-'));
            return sanitized.Length == 0 ? null : sanitized;
        }
        catch (FileLoadException)
        {
            return null;
        }
    }

    private static async Task RunAsync(TimeSpan timeout)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
        {
            throw new ProbeFailureException("platform");
        }

        ulong nativeHandle = NativePion.EngineNew();
        if (nativeHandle == 0)
            throw new ProbeFailureException("engine-create");

        using var deadline = new CancellationTokenSource(timeout);
        ManagedVoiceEngine? managed = null;
        InteropSession? session = null;
        try
        {
            managed = new ManagedVoiceEngine();
            session = new InteropSession(nativeHandle, managed, deadline);
            await session.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            if (session is not null)
                await session.CloseAsync().ConfigureAwait(false);
            else
            {
                managed?.Dispose();
                NativePion.EngineClose(nativeHandle);
            }
        }
    }
}

internal sealed class InteropSession
{
    private const int Generation = 1;
    private const int MaximumControlBytes = 128 * 1024;
    private const int MaximumCandidateBytes = 16 * 1024;
    private const int MaximumOpusBytes = 1_275;
    private const int MaximumEventsPerPoll = 64;
    private const int MaximumMediaFrames = 100;
    private const string PeerId = "pion";

    private readonly ulong _nativeHandle;
    private readonly ManagedVoiceEngine _managed;
    private readonly CancellationTokenSource _deadline;
    private readonly byte[] _peer = Encoding.UTF8.GetBytes(PeerId);
    private readonly TaskCompletionSource<bool> _managedConnected = NewSignal();
    private readonly TaskCompletionSource<bool> _pionConnected = NewSignal();
    private readonly TaskCompletionSource<bool> _playbackAudible = NewSignal();
    private readonly TaskCompletionSource<string> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _pollTask;
    private Task? _playbackTask;
    private Task? _mediaTask;
    private bool _managedPeerAdded;
    private bool _nativePeerAdded;
    private int _closed;
    private long _nativeReceivedPackets;
    private long _echoedPackets;
    private ulong _mediaSequence;

    public InteropSession(ulong nativeHandle, ManagedVoiceEngine managed, CancellationTokenSource deadline)
    {
        _nativeHandle = nativeHandle;
        _managed = managed;
        _deadline = deadline;
    }

    public async Task RunAsync()
    {
        _managed.LocalSdp += OnManagedSdp;
        _managed.LocalCandidate += OnManagedCandidate;
        _managed.PeerState += OnManagedPeerState;
        _managed.ConfigureGameState(false, 1f, [new ManagedPeerRoute(PeerId, 1f, 0f, 0, false)]);
        _managed.SetInput(1f, 0f, 0f);

        if (!_managed.Start())
            throw new ProbeFailureException("managed-start");

        int status = NativePion.AddPeer(
            _nativeHandle,
            _peer,
            checked((uint)_peer.Length),
            0,
            0,
            Generation,
            0);
        if (status != NativePion.Ok)
            throw new ProbeFailureException("native-peer");
        _nativePeerAdded = true;

        _pollTask = PollNativeAsync(_deadline.Token);
        if (!_managed.AddPeer(PeerId, true, Generation))
            throw new ProbeFailureException("managed-peer");
        _managedPeerAdded = true;

        await AwaitSignalAsync(Task.WhenAll(_managedConnected.Task, _pionConnected.Task)).ConfigureAwait(false);

        _managed.SetMicActive(true);
        _playbackTask = ReadPlaybackAsync(_deadline.Token);
        _mediaTask = PushMicrophoneAsync(_deadline.Token);
        await AwaitSignalAsync(_playbackAudible.Task).ConfigureAwait(false);

        if (_failure.Task.IsCompleted)
            throw new ProbeFailureException(await _failure.Task.ConfigureAwait(false));

        if (Interlocked.Read(ref _nativeReceivedPackets) == 0 ||
            Interlocked.Read(ref _echoedPackets) == 0 ||
            _managed.SentPackets == 0 ||
            _managed.ReceivedPackets == 0)
        {
            throw new ProbeFailureException("media-aggregate");
        }
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        _deadline.Cancel();
        await ObserveBoundedAsync(_mediaTask).ConfigureAwait(false);
        await ObserveBoundedAsync(_playbackTask).ConfigureAwait(false);
        await ObserveBoundedAsync(_pollTask).ConfigureAwait(false);

        try
        {
            _managed.SetMicActive(false);
            if (_managedPeerAdded)
            {
                _managed.RemovePeer(PeerId, Generation);
                _managedPeerAdded = false;
            }
            _managed.Dispose();
        }
        finally
        {
            try
            {
                if (_nativePeerAdded)
                {
                    NativePion.RemovePeer(_nativeHandle, _peer, checked((uint)_peer.Length));
                    _nativePeerAdded = false;
                }
            }
            finally
            {
                NativePion.EngineClose(_nativeHandle);
            }
        }
    }

    private void OnManagedSdp(string peerId, int generation, string type, string sdp)
    {
        if (!IsExpectedPeer(peerId, generation))
            return;

        try
        {
            if (!string.Equals(type, "offer", StringComparison.Ordinal) ||
                !TryEncodeBounded(type, 16, out byte[] typeBytes) ||
                !TryEncodeBounded(sdp, MaximumControlBytes, out byte[] sdpBytes))
            {
                Fail("managed-sdp");
                return;
            }

            int status = NativePion.SetRemoteSdp(
                _nativeHandle,
                _peer,
                checked((uint)_peer.Length),
                Generation,
                typeBytes,
                checked((uint)typeBytes.Length),
                sdpBytes,
                checked((uint)sdpBytes.Length));
            if (status != NativePion.Ok)
                Fail("managed-sdp");
        }
        catch
        {
            Fail("managed-sdp");
        }
    }

    private void OnManagedCandidate(string peerId, int generation, string candidate)
    {
        if (!IsExpectedPeer(peerId, generation))
            return;

        try
        {
            if (!TryEncodeBounded(candidate, MaximumCandidateBytes, out byte[] candidateBytes))
            {
                Fail("managed-candidate");
                return;
            }

            int status = NativePion.AddCandidate(
                _nativeHandle,
                _peer,
                checked((uint)_peer.Length),
                Generation,
                candidateBytes,
                checked((uint)candidateBytes.Length));
            if (status != NativePion.Ok)
                Fail("managed-candidate");
        }
        catch
        {
            Fail("managed-candidate");
        }
    }

    private void OnManagedPeerState(string peerId, int generation, string state)
    {
        if (!IsExpectedPeer(peerId, generation))
            return;
        if (string.Equals(state, "connected", StringComparison.Ordinal))
            _managedConnected.TrySetResult(true);
        else if (string.Equals(state, "failed", StringComparison.Ordinal))
            Fail("managed-state");
    }

    private async Task PollNativeAsync(CancellationToken cancellationToken)
    {
        var controlBuffer = new byte[MaximumControlBytes];
        var peerBuffer = new byte[256];
        var payloadBuffer = new byte[MaximumOpusBytes];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool progressed = DrainControl(controlBuffer);
                progressed |= DrainRtp(peerBuffer, payloadBuffer);
                if (!progressed)
                    await Task.Delay(2, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            Fail("native-poll");
        }
    }

    private bool DrainControl(byte[] buffer)
    {
        bool progressed = false;
        for (int index = 0; index < MaximumEventsPerPoll; index++)
        {
            int status = NativePion.PollControl(_nativeHandle, buffer, checked((uint)buffer.Length), out uint required);
            if (status == NativePion.Empty)
                return progressed;
            if (status != NativePion.Ok || required == 0 || required > (uint)buffer.Length)
            {
                Fail("native-control");
                return progressed;
            }

            progressed = true;
            ProcessControl(buffer.AsMemory(0, checked((int)required)));
            if (_failure.Task.IsCompleted)
                return progressed;
        }
        return progressed;
    }

    private void ProcessControl(ReadOnlyMemory<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("kind", out JsonElement kindElement))
        {
            Fail("native-control");
            return;
        }

        string? kind = kindElement.GetString();
        switch (kind)
        {
            case "sdp":
                if (!HasExpectedScope(root) ||
                    !root.TryGetProperty("sdp_type", out JsonElement sdpTypeElement) ||
                    !string.Equals(sdpTypeElement.GetString(), "answer", StringComparison.Ordinal) ||
                    !root.TryGetProperty("sdp", out JsonElement sdpElement))
                {
                    Fail("native-sdp");
                    return;
                }
                string? sdp = sdpElement.GetString();
                if (sdp is null || Encoding.UTF8.GetByteCount(sdp) > MaximumControlBytes ||
                    !_managed.SetRemoteSdp(PeerId, Generation, "answer", sdp))
                {
                    Fail("native-sdp");
                }
                return;

            case "candidate":
                if (!HasExpectedScope(root) ||
                    !root.TryGetProperty("candidate", out JsonElement candidateElement))
                {
                    Fail("native-candidate");
                    return;
                }
                string? candidate = candidateElement.GetString();
                if (candidate is null || Encoding.UTF8.GetByteCount(candidate) > MaximumCandidateBytes ||
                    !_managed.AddIceCandidate(PeerId, Generation, candidate))
                {
                    Fail("native-candidate");
                }
                return;

            case "state":
                if (!HasExpectedScope(root) || !root.TryGetProperty("state", out JsonElement stateElement))
                {
                    Fail("native-state");
                    return;
                }
                string? state = stateElement.GetString();
                if (string.Equals(state, "connected", StringComparison.Ordinal))
                    _pionConnected.TrySetResult(true);
                else if (string.Equals(state, "failed", StringComparison.Ordinal) ||
                    string.Equals(state, "closed", StringComparison.Ordinal))
                    Fail("native-state");
                return;

            case "error":
                Fail("native-error");
                return;

            case "bandwidth":
            case "ice-state":
            case "path":
            case "stats":
                return;

            default:
                Fail("native-control-kind");
                return;
        }
    }

    private bool DrainRtp(byte[] peerBuffer, byte[] payloadBuffer)
    {
        bool progressed = false;
        for (int index = 0; index < MaximumEventsPerPoll; index++)
        {
            var rtpEvent = new NativePion.RtpEvent();
            int status = NativePion.PollRtp(
                _nativeHandle,
                ref rtpEvent,
                peerBuffer,
                checked((uint)peerBuffer.Length),
                payloadBuffer,
                checked((uint)payloadBuffer.Length));
            if (status == NativePion.Empty)
                return progressed;
            if (status != NativePion.Ok ||
                rtpEvent.Generation != Generation ||
                rtpEvent.PeerLength != (uint)_peer.Length ||
                rtpEvent.PayloadLength == 0 ||
                rtpEvent.PayloadLength > (uint)payloadBuffer.Length ||
                !peerBuffer.AsSpan(0, checked((int)rtpEvent.PeerLength)).SequenceEqual(_peer))
            {
                Fail("native-rtp");
                return progressed;
            }

            progressed = true;
            Interlocked.Increment(ref _nativeReceivedPackets);
            ulong sequence = ++_mediaSequence;
            int sendStatus = NativePion.SendOpus(
                _nativeHandle,
                payloadBuffer,
                rtpEvent.PayloadLength,
                0,
                sequence,
                out NativePion.SendResult sendResult);
            if (sendStatus != NativePion.Ok || sendResult.Enqueued == 0 ||
                sendResult.QueueFull != 0 || sendResult.StaleEpoch != 0)
            {
                Fail("native-echo");
                return progressed;
            }
            Interlocked.Increment(ref _echoedPackets);
        }
        return progressed;
    }

    private async Task PushMicrophoneAsync(CancellationToken cancellationToken)
    {
        var frame = new float[960];
        long sampleOffset = 0;
        try
        {
            for (int frameIndex = 0; frameIndex < MaximumMediaFrames && !_playbackAudible.Task.IsCompleted; frameIndex++)
            {
                for (int sampleIndex = 0; sampleIndex < frame.Length; sampleIndex++)
                {
                    double phase = Math.Tau * 440d * (sampleOffset + sampleIndex) / 48_000d;
                    frame[sampleIndex] = (float)(0.2d * Math.Sin(phase));
                }
                sampleOffset += frame.Length;
                _managed.PushMic(frame, frame.Length, 0);
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
            if (!_playbackAudible.Task.IsCompleted)
                Fail("playback-empty");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            Fail("managed-media");
        }
    }

    private async Task ReadPlaybackAsync(CancellationToken cancellationToken)
    {
        var playback = new float[1_920];
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_playbackAudible.Task.IsCompleted)
            {
                _managed.ReadPlayback(playback, playback.Length);
                for (int index = 0; index < playback.Length; index++)
                {
                    if (float.IsFinite(playback[index]) && Math.Abs(playback[index]) > 0.0001f)
                    {
                        _playbackAudible.TrySetResult(true);
                        return;
                    }
                }
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            Fail("managed-playback");
        }
    }

    private async Task AwaitSignalAsync(Task signal)
    {
        Task timeout = Task.Delay(Timeout.InfiniteTimeSpan, _deadline.Token);
        Task completed = await Task.WhenAny(signal, _failure.Task, timeout).ConfigureAwait(false);
        if (completed == _failure.Task)
            throw new ProbeFailureException(await _failure.Task.ConfigureAwait(false));
        if (completed == timeout)
            throw new ProbeFailureException("timeout");
        await signal.ConfigureAwait(false);
    }

    private bool HasExpectedScope(JsonElement root)
    {
        return root.TryGetProperty("peer_id", out JsonElement peerElement) &&
            string.Equals(peerElement.GetString(), PeerId, StringComparison.Ordinal) &&
            root.TryGetProperty("generation", out JsonElement generationElement) &&
            generationElement.TryGetInt32(out int generation) && generation == Generation;
    }

    private static bool TryEncodeBounded(string value, int maximumBytes, out byte[] encoded)
    {
        if (value is null || Encoding.UTF8.GetByteCount(value) > maximumBytes)
        {
            encoded = Array.Empty<byte>();
            return false;
        }
        encoded = Encoding.UTF8.GetBytes(value);
        return true;
    }

    private static bool IsExpectedPeer(string peerId, int generation) =>
        generation == Generation && string.Equals(peerId, PeerId, StringComparison.Ordinal);

    private void Fail(string code) => _failure.TrySetResult(code);

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ObserveBoundedAsync(Task? task)
    {
        if (task is null)
            return;
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}

internal sealed class ProbeFailureException : Exception
{
    public ProbeFailureException(string code) => Code = code;

    public string Code { get; }
}

internal readonly record struct ProbeOptions(string PionPath, TimeSpan Timeout)
{
    private const int DefaultTimeoutSeconds = 20;
    private const int MaximumTimeoutSeconds = 120;

    public static ProbeOptions Parse(string[] args)
    {
        string? pionPath = null;
        int timeoutSeconds = DefaultTimeoutSeconds;

        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            if (string.Equals(option, "--pion", StringComparison.Ordinal) && index + 1 < args.Length)
            {
                pionPath = args[++index];
            }
            else if (string.Equals(option, "--timeout", StringComparison.Ordinal) && index + 1 < args.Length &&
                int.TryParse(args[++index], out int parsedTimeout) && parsedTimeout is >= 1 and <= MaximumTimeoutSeconds)
            {
                timeoutSeconds = parsedTimeout;
            }
            else
            {
                throw new ProbeFailureException("arguments");
            }
        }

        if (string.IsNullOrWhiteSpace(pionPath) || !Path.IsPathFullyQualified(pionPath))
            throw new ProbeFailureException("pion-path");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(pionPath);
        }
        catch
        {
            throw new ProbeFailureException("pion-path");
        }

        if (!File.Exists(fullPath))
            throw new ProbeFailureException("pion-missing");

        return new ProbeOptions(fullPath, TimeSpan.FromSeconds(timeoutSeconds));
    }
}

internal static class NativePion
{
    public const int Ok = 0;
    public const int Empty = 1;
    private const string LibraryName = "pc-pion-starlight-interop";
    private static string? _libraryPath;
    private static nint _libraryHandle;
    private static readonly object LibraryLock = new();
    private static int _configured;

    public static void Configure(string libraryPath)
    {
        if (Interlocked.Exchange(ref _configured, 1) != 0)
            throw new ProbeFailureException("native-configure");
        _libraryPath = libraryPath;
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveLibrary);
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal) || _libraryPath is null)
            return 0;

        nint current = Volatile.Read(ref _libraryHandle);
        if (current != 0)
            return current;

        lock (LibraryLock)
        {
            current = _libraryHandle;
            if (current != 0)
                return current;

            current = NativeLibrary.Load(_libraryPath);
            Volatile.Write(ref _libraryHandle, current);
            return current;
        }
    }

    [DllImport(LibraryName, EntryPoint = "pc_pion_engine_new", CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong EngineNew();

    [DllImport(LibraryName, EntryPoint = "pc_pion_engine_close", CallingConvention = CallingConvention.Cdecl)]
    public static extern int EngineClose(ulong handle);

    [DllImport(LibraryName, EntryPoint = "pc_pion_add_peer", CallingConvention = CallingConvention.Cdecl)]
    public static extern int AddPeer(
        ulong handle,
        byte[] peer,
        uint peerLength,
        uint offerer,
        uint relayOnly,
        uint generation,
        ulong minimumEpoch);

    [DllImport(LibraryName, EntryPoint = "pc_pion_remove_peer", CallingConvention = CallingConvention.Cdecl)]
    public static extern int RemovePeer(ulong handle, byte[] peer, uint peerLength);

    [DllImport(LibraryName, EntryPoint = "pc_pion_set_remote_sdp", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SetRemoteSdp(
        ulong handle,
        byte[] peer,
        uint peerLength,
        uint generation,
        byte[] type,
        uint typeLength,
        byte[] sdp,
        uint sdpLength);

    [DllImport(LibraryName, EntryPoint = "pc_pion_add_ice_candidate", CallingConvention = CallingConvention.Cdecl)]
    public static extern int AddCandidate(
        ulong handle,
        byte[] peer,
        uint peerLength,
        uint generation,
        byte[] candidate,
        uint candidateLength);

    [DllImport(LibraryName, EntryPoint = "pc_pion_send_opus", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SendOpus(
        ulong handle,
        byte[] payload,
        uint payloadLength,
        ulong epoch,
        ulong mediaSequence,
        out SendResult result);

    [DllImport(LibraryName, EntryPoint = "pc_pion_poll_control", CallingConvention = CallingConvention.Cdecl)]
    public static extern int PollControl(ulong handle, byte[] buffer, uint capacity, out uint required);

    [DllImport(LibraryName, EntryPoint = "pc_pion_poll_rtp", CallingConvention = CallingConvention.Cdecl)]
    public static extern int PollRtp(
        ulong handle,
        ref RtpEvent rtpEvent,
        byte[] peerBuffer,
        uint peerCapacity,
        byte[] payloadBuffer,
        uint payloadCapacity);

    [StructLayout(LayoutKind.Sequential)]
    public struct SendResult
    {
        public uint Attempted;
        public uint Enqueued;
        public uint QueueFull;
        public uint StaleEpoch;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RtpEvent
    {
        public uint Generation;
        public ushort Sequence;
        public ushort Reserved;
        public uint Timestamp;
        public uint PeerLength;
        public uint PayloadLength;
        public ulong ArrivalAgeNanoseconds;
        public ulong IngressOverflow;
    }
}
