using System.Diagnostics;
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
            await RunAsync(options).ConfigureAwait(false);
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

    private static async Task RunAsync(ProbeOptions options)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
        {
            throw new ProbeFailureException("platform");
        }

        await CodecInterop.RunAsync(options.CodecProbePath, options.Timeout).ConfigureAwait(false);

        ulong nativeHandle = NativePion.EngineNew();
        if (nativeHandle == 0)
            throw new ProbeFailureException("engine-create");

        try
        {
            await RunRoleAsync(nativeHandle, true, 1, options.Timeout).ConfigureAwait(false);
            await RunRoleAsync(nativeHandle, false, 2, options.Timeout).ConfigureAwait(false);
        }
        finally
        {
            if (NativePion.EngineClose(nativeHandle) != NativePion.Ok)
                throw new ProbeFailureException("engine-close");
        }
    }

    private static async Task RunRoleAsync(
        ulong nativeHandle,
        bool managedOfferer,
        int generation,
        TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var managed = new ManagedVoiceEngine();
        var session = new InteropSession(nativeHandle, managed, deadline, managedOfferer, generation);
        try
        {
            await session.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }
}

internal static class CodecInterop
{
    private static readonly byte[] FixtureMagic = "PCOPUS01"u8.ToArray();
    private const int FixtureFrames = 100;
    private const double ManagedToneFrequency = 659.2551138257398d;
    private const double NativeToneFrequency = 173d;

    public static async Task RunAsync(string codecProbePath, TimeSpan timeout)
    {
        string directory = Path.Combine(Path.GetTempPath(), "perfectcomms-opus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string managedFixture = Path.Combine(directory, "managed.opus-fixture");
        string nativeFixture = Path.Combine(directory, "native.opus-fixture");
        try
        {
            WriteManagedFixture(managedFixture);
            await RunNativeProbeAsync(codecProbePath, managedFixture, nativeFixture, timeout)
                .ConfigureAwait(false);
            ValidateNativeFixture(nativeFixture);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch
            {
            }
        }
    }

    private static void WriteManagedFixture(string path)
    {
        using var encoder = new ManagedOpusEncoder();
        encoder.Configure(1f, 0f, 0f);
        var frame = new float[ManagedOpusEncoder.FrameSamples];
        var packet = new byte[ManagedOpusEncoder.MaxPacketBytes];
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(FixtureMagic);
        writer.Write(FixtureFrames);
        long sampleOffset = 0;
        for (int frameIndex = 0; frameIndex < FixtureFrames; frameIndex++)
        {
            FillTone(frame, ManagedToneFrequency, sampleOffset);
            sampleOffset += frame.Length;
            int length = encoder.Encode(frame, packet, out float peak, out bool speaking);
            if (length <= 0 || length > packet.Length || !float.IsFinite(peak) || peak <= 0f || !speaking)
                throw new ProbeFailureException("codec-managed-encode");
            writer.Write(length);
            writer.Write(packet, 0, length);
        }
    }

    private static async Task RunNativeProbeAsync(
        string codecProbePath,
        string managedFixture,
        string nativeFixture,
        TimeSpan timeout)
    {
        var start = new ProcessStartInfo(codecProbePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("exchange");
        start.ArgumentList.Add(managedFixture);
        start.ArgumentList.Add(nativeFixture);

        using Process process = Process.Start(start) ?? throw new ProbeFailureException("codec-probe-start");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
            }
            throw new ProbeFailureException("codec-probe-timeout");
        }
        await Task.WhenAll(output, error).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string stderr = await error.ConfigureAwait(false);
            throw new ProbeFailureException(TryParseNativeFailure(stderr, out string code)
                ? code
                : "codec-native");
        }
    }

    private static bool TryParseNativeFailure(string stderr, out string code)
    {
        const string prefix = "starlight-opus-probe.failed code=";
        string line = stderr.EndsWith("\r\n", StringComparison.Ordinal)
            ? stderr[..^2]
            : stderr.EndsWith('\n') ? stderr[..^1] : stderr;
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            code = string.Empty;
            return false;
        }
        string token = line[prefix.Length..];
        if (token.Length is < 1 or > 64 ||
            token.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            code = string.Empty;
            return false;
        }
        code = token;
        return true;
    }

    private static void ValidateNativeFixture(string path)
    {
        if (!File.Exists(path))
            throw new ProbeFailureException("codec-native-fixture");

        byte[][] packets = ReadFixture(path);
        var baseline = new float[packets.Length][];
        int primaryCorrelated = 0;
        using (var baselineDecoder = new ManagedOpusDecoder())
        {
            for (int index = 0; index < packets.Length; index++)
            {
                baseline[index] = new float[ManagedOpusDecoder.FrameSamples];
                if (baselineDecoder.Decode(packets[index], baseline[index]) != baseline[index].Length)
                    throw new ProbeFailureException("codec-native-decode");
                if (index >= 2 && IsFiniteCorrelatedTone(baseline[index], NativeToneFrequency))
                    primaryCorrelated++;
            }
        }

        int fecCorrelated = 0;
        var frame = new float[ManagedOpusDecoder.FrameSamples];
        for (int index = 1; index < packets.Length && fecCorrelated == 0; index++)
        {
            using var recoveryDecoder = new ManagedOpusDecoder();
            for (int prime = 0; prime < index - 1; prime++)
            {
                if (recoveryDecoder.Decode(packets[prime], frame) != frame.Length)
                    throw new ProbeFailureException("codec-native-decode");
            }
            int recovered = recoveryDecoder.DecodeFec(packets[index], frame);
            if (recovered == frame.Length && IsFiniteCorrelatedPair(frame, baseline[index - 1]))
                fecCorrelated++;
        }

        if (primaryCorrelated < FixtureFrames - 4)
            throw new ProbeFailureException("codec-native-tone");
        if (fecCorrelated == 0)
            throw new ProbeFailureException("codec-native-fec");
    }

    private static byte[][] ReadFixture(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (!reader.ReadBytes(FixtureMagic.Length).AsSpan().SequenceEqual(FixtureMagic))
            throw new ProbeFailureException("codec-native-fixture");
        int count = reader.ReadInt32();
        if (count != FixtureFrames)
            throw new ProbeFailureException("codec-native-fixture");

        var packets = new byte[count][];
        for (int index = 0; index < packets.Length; index++)
        {
            int length = reader.ReadInt32();
            if (length <= 0 || length > ManagedOpusEncoder.MaxPacketBytes)
                throw new ProbeFailureException("codec-native-fixture");
            packets[index] = reader.ReadBytes(length);
            if (packets[index].Length != length)
                throw new ProbeFailureException("codec-native-fixture");
        }
        if (stream.Position != stream.Length)
            throw new ProbeFailureException("codec-native-fixture");
        return packets;
    }

    private static void FillTone(float[] frame, double frequency, long sampleOffset)
    {
        for (int index = 0; index < frame.Length; index++)
        {
            double time = (double)(sampleOffset + index) / ManagedOpusEncoder.SampleRate;
            double envelope = 0.55d + 0.45d * Math.Abs(Math.Sin(Math.Tau * 3.7d * time));
            frame[index] = (float)(envelope *
                (0.20d * Math.Sin(Math.Tau * frequency * time) +
                 0.08d * Math.Sin(Math.Tau * frequency * 0.51d * time) +
                 0.05d * Math.Sin(Math.Tau * frequency * 1.83d * time)));
        }
    }

    internal static bool IsFiniteCorrelatedTone(ReadOnlySpan<float> samples, double frequency)
    {
        double energy = 0d;
        double sine = 0d;
        double cosine = 0d;
        for (int index = 0; index < samples.Length; index++)
        {
            float sample = samples[index];
            if (!float.IsFinite(sample))
                return false;
            double phase = Math.Tau * frequency * index / ManagedOpusEncoder.SampleRate;
            energy += sample * sample;
            sine += sample * Math.Sin(phase);
            cosine += sample * Math.Cos(phase);
        }
        if (energy < samples.Length * 0.00001d)
            return false;
        double correlatedEnergy = 2d * (sine * sine + cosine * cosine) / samples.Length;
        return correlatedEnergy / energy >= 0.55d;
    }

    private static bool IsFiniteCorrelatedPair(ReadOnlySpan<float> actual, ReadOnlySpan<float> expected)
    {
        if (actual.Length != expected.Length)
            return false;
        double actualEnergy = 0d;
        double expectedEnergy = 0d;
        double product = 0d;
        for (int index = 0; index < actual.Length; index++)
        {
            if (!float.IsFinite(actual[index]) || !float.IsFinite(expected[index]))
                return false;
            actualEnergy += actual[index] * actual[index];
            expectedEnergy += expected[index] * expected[index];
            product += actual[index] * expected[index];
        }
        if (actualEnergy < actual.Length * 0.00001d || expectedEnergy < expected.Length * 0.00001d)
            return false;
        return product / Math.Sqrt(actualEnergy * expectedEnergy) >= 0.5d;
    }
}

internal sealed class InteropSession
{
    private const int MaximumControlBytes = 128 * 1024;
    private const int MaximumCandidateBytes = 16 * 1024;
    private const int MaximumOpusBytes = 1_275;
    private const int MaximumEventsPerPoll = 64;
    private const int MaximumMediaFrames = 150;
    private const int RequiredMediaFrames = 16;
    private const int MaximumQueuedCandidates = 256;

    private readonly ulong _nativeHandle;
    private readonly ManagedVoiceEngine _managed;
    private readonly CancellationTokenSource _deadline;
    private readonly bool _managedOfferer;
    private readonly int _generation;
    private readonly string _peerId;
    private readonly byte[] _peer;
    private readonly object _signalGate = new();
    private readonly List<string> _managedCandidates = [];
    private readonly List<string> _nativeCandidates = [];
    private readonly TaskCompletionSource<bool> _managedConnected = NewSignal();
    private readonly TaskCompletionSource<bool> _pionConnected = NewSignal();
    private readonly TaskCompletionSource<bool> _playbackCorrelated = NewSignal();
    private readonly TaskCompletionSource<string> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _pollTask;
    private Task? _playbackTask;
    private Task? _mediaTask;
    private bool _managedSdpForwarded;
    private bool _nativeSdpForwarded;
    private bool _managedPeerAdded;
    private bool _nativePeerAdded;
    private int _closed;
    private long _nativeReceivedPackets;
    private long _echoedPackets;
    private ulong _mediaSequence;

    public InteropSession(
        ulong nativeHandle,
        ManagedVoiceEngine managed,
        CancellationTokenSource deadline,
        bool managedOfferer,
        int generation)
    {
        _nativeHandle = nativeHandle;
        _managed = managed;
        _deadline = deadline;
        _managedOfferer = managedOfferer;
        _generation = generation;
        _peerId = "pion";
        _peer = Encoding.UTF8.GetBytes(_peerId);
    }

    public async Task RunAsync()
    {
        _managed.LocalSdp += OnManagedSdp;
        _managed.LocalCandidate += OnManagedCandidate;
        _managed.PeerState += OnManagedPeerState;
        _managed.ConfigureGameState(false, 1f, [new ManagedPeerRoute(_peerId, 1f, 0f, 0, false)]);
        _managed.SetInput(1f, 0f, 0f);

        if (!_managed.Start())
            throw new ProbeFailureException("managed-start");

        int status = NativePion.AddPeer(
            _nativeHandle,
            _peer,
            checked((uint)_peer.Length),
            _managedOfferer ? 0u : 1u,
            0,
            checked((uint)_generation),
            0);
        if (status != NativePion.Ok)
            throw new ProbeFailureException("native-peer");
        _nativePeerAdded = true;

        if (!_managed.AddPeer(_peerId, _managedOfferer, _generation))
            throw new ProbeFailureException("managed-peer");
        _managedPeerAdded = true;
        _pollTask = PollNativeAsync(_deadline.Token);

        await AwaitSignalAsync(Task.WhenAll(_managedConnected.Task, _pionConnected.Task)).ConfigureAwait(false);

        _managed.SetMicActive(true);
        _playbackTask = ReadPlaybackAsync(_deadline.Token);
        _mediaTask = PushMicrophoneAsync(_deadline.Token);
        await AwaitSignalAsync(_playbackCorrelated.Task).ConfigureAwait(false);

        if (_failure.Task.IsCompleted)
            throw new ProbeFailureException(await _failure.Task.ConfigureAwait(false));

        if (Interlocked.Read(ref _nativeReceivedPackets) < RequiredMediaFrames ||
            Interlocked.Read(ref _echoedPackets) < RequiredMediaFrames ||
            _managed.SentPackets < RequiredMediaFrames ||
            _managed.ReceivedPackets < RequiredMediaFrames)
        {
            throw new ProbeFailureException("media-sustained");
        }
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        string? failure = null;
        _deadline.Cancel();
        bool mediaStopped = await ObserveBoundedAsync(_mediaTask).ConfigureAwait(false);
        bool playbackStopped = await ObserveBoundedAsync(_playbackTask).ConfigureAwait(false);
        bool pollStopped = await ObserveBoundedAsync(_pollTask).ConfigureAwait(false);
        if (!mediaStopped || !playbackStopped || !pollStopped)
            failure = "task-close";

        try
        {
            _managed.SetMicActive(false);
            if (_managedPeerAdded)
            {
                if (!_managed.RemovePeer(_peerId, _generation))
                    failure ??= "managed-peer-remove";
                _managedPeerAdded = false;
            }
        }
        catch
        {
            failure ??= "managed-peer-remove";
        }

        try
        {
            _managed.LocalSdp -= OnManagedSdp;
            _managed.LocalCandidate -= OnManagedCandidate;
            _managed.PeerState -= OnManagedPeerState;
            _managed.Dispose();
        }
        catch
        {
            failure ??= "managed-close";
        }

        try
        {
            if (_nativePeerAdded)
            {
                if (NativePion.RemovePeer(_nativeHandle, _peer, checked((uint)_peer.Length)) != NativePion.Ok)
                    failure ??= "native-peer-remove";
                _nativePeerAdded = false;
            }
        }
        catch
        {
            failure ??= "native-peer-remove";
        }

        if (failure is not null)
            throw new ProbeFailureException(failure);
    }

    private void OnManagedSdp(string peerId, int generation, string type, string sdp)
    {
        if (!IsExpectedPeer(peerId, generation))
            return;

        lock (_signalGate)
        {
            try
            {
                string expected = _managedOfferer ? "offer" : "answer";
                if (_managedSdpForwarded ||
                    !string.Equals(type, expected, StringComparison.Ordinal) ||
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
                    checked((uint)_generation),
                    typeBytes,
                    checked((uint)typeBytes.Length),
                    sdpBytes,
                    checked((uint)sdpBytes.Length));
                if (status != NativePion.Ok)
                {
                    Fail("managed-sdp");
                    return;
                }

                _managedSdpForwarded = true;
                foreach (string candidate in _managedCandidates)
                    ForwardManagedCandidate(candidate);
                _managedCandidates.Clear();
            }
            catch
            {
                Fail("managed-sdp");
            }
        }
    }

    private void OnManagedCandidate(string peerId, int generation, string candidate)
    {
        if (!IsExpectedPeer(peerId, generation))
            return;

        lock (_signalGate)
        {
            if (!_managedSdpForwarded)
            {
                if (_managedCandidates.Count >= MaximumQueuedCandidates)
                    Fail("managed-candidate-order");
                else
                    _managedCandidates.Add(candidate);
                return;
            }
            ForwardManagedCandidate(candidate);
        }
    }

    private void ForwardManagedCandidate(string candidate)
    {
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
                checked((uint)_generation),
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
        else if (string.Equals(state, "failed", StringComparison.Ordinal) ||
                 (string.Equals(state, "closed", StringComparison.Ordinal) &&
                  Volatile.Read(ref _closed) == 0))
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
        if (HasStaleScope(root))
            return;
        switch (kind)
        {
            case "sdp":
                ProcessNativeSdp(root);
                return;

            case "candidate":
                ProcessNativeCandidate(root);
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

    private void ProcessNativeSdp(JsonElement root)
    {
        lock (_signalGate)
        {
            string expected = _managedOfferer ? "answer" : "offer";
            if (_nativeSdpForwarded || !HasExpectedScope(root) ||
                !root.TryGetProperty("sdp_type", out JsonElement sdpTypeElement) ||
                !string.Equals(sdpTypeElement.GetString(), expected, StringComparison.Ordinal) ||
                !root.TryGetProperty("sdp", out JsonElement sdpElement))
            {
                Fail("native-sdp");
                return;
            }
            string? sdp = sdpElement.GetString();
            if (sdp is null || Encoding.UTF8.GetByteCount(sdp) > MaximumControlBytes ||
                !_managed.SetRemoteSdp(_peerId, _generation, expected, sdp))
            {
                Fail("native-sdp");
                return;
            }

            _nativeSdpForwarded = true;
            foreach (string candidate in _nativeCandidates)
            {
                if (!_managed.AddIceCandidate(_peerId, _generation, candidate))
                {
                    Fail("native-candidate");
                    return;
                }
            }
            _nativeCandidates.Clear();
        }
    }

    private void ProcessNativeCandidate(JsonElement root)
    {
        lock (_signalGate)
        {
            if (!HasExpectedScope(root) ||
                !root.TryGetProperty("candidate", out JsonElement candidateElement))
            {
                Fail("native-candidate");
                return;
            }
            string? candidate = candidateElement.GetString();
            if (candidate is null || Encoding.UTF8.GetByteCount(candidate) > MaximumCandidateBytes)
            {
                Fail("native-candidate");
                return;
            }
            if (!_nativeSdpForwarded)
            {
                if (_nativeCandidates.Count >= MaximumQueuedCandidates)
                    Fail("native-candidate-order");
                else
                    _nativeCandidates.Add(candidate);
                return;
            }
            if (!_managed.AddIceCandidate(_peerId, _generation, candidate))
                Fail("native-candidate");
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
                rtpEvent.PeerLength > (uint)peerBuffer.Length ||
                rtpEvent.PayloadLength == 0 ||
                rtpEvent.PayloadLength > (uint)payloadBuffer.Length)
            {
                Fail("native-rtp");
                return progressed;
            }
            bool expectedPeer = rtpEvent.PeerLength == (uint)_peer.Length &&
                peerBuffer.AsSpan(0, checked((int)rtpEvent.PeerLength)).SequenceEqual(_peer);
            if (expectedPeer && rtpEvent.Generation < checked((uint)_generation))
            {
                progressed = true;
                continue;
            }
            if (!expectedPeer || rtpEvent.Generation != checked((uint)_generation))
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
            if (sendStatus != NativePion.Ok || sendResult.Attempted == 0 || sendResult.Enqueued == 0 ||
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
        var frame = new float[ManagedOpusEncoder.FrameSamples];
        long sampleOffset = 0;
        try
        {
            for (int frameIndex = 0; frameIndex < MaximumMediaFrames && !_playbackCorrelated.Task.IsCompleted; frameIndex++)
            {
                for (int sampleIndex = 0; sampleIndex < frame.Length; sampleIndex++)
                {
                    double phase = Math.Tau * 440d * (sampleOffset + sampleIndex) / ManagedOpusEncoder.SampleRate;
                    frame[sampleIndex] = (float)(0.2d * Math.Sin(phase));
                }
                sampleOffset += frame.Length;
                _managed.PushMic(frame, frame.Length, 0);
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
            if (!_playbackCorrelated.Task.IsCompleted)
                Fail("playback-unsustained");
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
        var playback = new float[ManagedOpusEncoder.FrameSamples * 2];
        var mono = new float[ManagedOpusEncoder.FrameSamples];
        int correlatedFrames = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_playbackCorrelated.Task.IsCompleted)
            {
                _managed.ReadPlayback(playback, playback.Length);
                bool finite = true;
                for (int index = 0; index < mono.Length; index++)
                {
                    float left = playback[index * 2];
                    float right = playback[index * 2 + 1];
                    if (!float.IsFinite(left) || !float.IsFinite(right))
                    {
                        finite = false;
                        break;
                    }
                    mono[index] = (left + right) * 0.5f;
                }
                if (!finite)
                {
                    Fail("playback-nonfinite");
                    return;
                }
                correlatedFrames = CodecInterop.IsFiniteCorrelatedTone(mono, 440d)
                    ? correlatedFrames + 1
                    : 0;
                if (correlatedFrames >= RequiredMediaFrames &&
                    Interlocked.Read(ref _nativeReceivedPackets) >= RequiredMediaFrames &&
                    Interlocked.Read(ref _echoedPackets) >= RequiredMediaFrames)
                {
                    _playbackCorrelated.TrySetResult(true);
                    return;
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
            string.Equals(peerElement.GetString(), _peerId, StringComparison.Ordinal) &&
            root.TryGetProperty("generation", out JsonElement generationElement) &&
            generationElement.TryGetInt32(out int generation) && generation == _generation;
    }

    private bool HasStaleScope(JsonElement root)
    {
        return root.TryGetProperty("peer_id", out JsonElement peerElement) &&
            string.Equals(peerElement.GetString(), _peerId, StringComparison.Ordinal) &&
            root.TryGetProperty("generation", out JsonElement generationElement) &&
            generationElement.TryGetInt32(out int generation) && generation < _generation;
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

    private bool IsExpectedPeer(string peerId, int generation) =>
        generation == _generation && string.Equals(peerId, _peerId, StringComparison.Ordinal);

    private void Fail(string code) => _failure.TrySetResult(code);

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<bool> ObserveBoundedAsync(Task? task)
    {
        if (task is null)
            return true;
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class ProbeFailureException : Exception
{
    public ProbeFailureException(string code) => Code = code;

    public string Code { get; }
}

internal readonly record struct ProbeOptions(string PionPath, string CodecProbePath, TimeSpan Timeout)
{
    private const int DefaultTimeoutSeconds = 20;
    private const int MaximumTimeoutSeconds = 120;

    public static ProbeOptions Parse(string[] args)
    {
        string? pionPath = null;
        string? codecProbePath = null;
        int timeoutSeconds = DefaultTimeoutSeconds;

        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            if (string.Equals(option, "--pion", StringComparison.Ordinal) && index + 1 < args.Length)
            {
                pionPath = args[++index];
            }
            else if (string.Equals(option, "--codec-probe", StringComparison.Ordinal) && index + 1 < args.Length)
            {
                codecProbePath = args[++index];
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

        string resolvedPion = ResolveFile(pionPath, "pion-path", "pion-missing");
        string resolvedCodecProbe = ResolveFile(codecProbePath, "codec-probe-path", "codec-probe-missing");
        return new ProbeOptions(resolvedPion, resolvedCodecProbe, TimeSpan.FromSeconds(timeoutSeconds));
    }

    private static string ResolveFile(string? path, string invalidCode, string missingCode)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ProbeFailureException(invalidCode);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            throw new ProbeFailureException(invalidCode);
        }

        if (!File.Exists(fullPath))
            throw new ProbeFailureException(missingCode);
        return fullPath;
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
