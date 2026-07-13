using System;
using System.Collections.Generic;
using System.Linq;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class PeerSessionManagerTests
{
    private sealed class MockTransport : IVoiceTransport
    {
        public readonly List<int> Added = new();
        public readonly List<bool> AddedOfferer = new();
        public readonly List<bool> AddedRelayOnly = new();
        public readonly List<int> AddedGenerations = new();
        public readonly List<int> Removed = new();
        public readonly List<(int Id, string Type, string Sdp)> RemoteSdp = new();
        public readonly List<(int Id, string Candidate)> Candidates = new();
        public Action<int, bool, bool, int>? PeerAdded;

        public void AddPeer(int clientId, bool isOfferer, bool relayOnly, int generation)
        {
            Added.Add(clientId);
            AddedOfferer.Add(isOfferer);
            AddedRelayOnly.Add(relayOnly);
            AddedGenerations.Add(generation);
            PeerAdded?.Invoke(clientId, isOfferer, relayOnly, generation);
        }
        public void RemovePeer(int clientId) => Removed.Add(clientId);
        public void SetRemoteSdp(int clientId, string sdpType, string sdp) => RemoteSdp.Add((clientId, sdpType, sdp));
        public void AddIceCandidate(int clientId, string candidate) => Candidates.Add((clientId, candidate));

        public int LatestGeneration(int clientId)
        {
            for (var i = Added.Count - 1; i >= 0; i--)
                if (Added[i] == clientId)
                    return AddedGenerations[i];
            throw new InvalidOperationException($"peer {clientId} has not been added");
        }
    }

    private sealed class MockSender : ISignalingSender
    {
        public readonly List<(int Target, SignalMsgType Type, byte[] Payload)> Sent = new();
        public Action<int, SignalMsgType, byte[]>? Forward;
        public Func<int, SignalMsgType, byte[], bool>? TrySend;

        public bool Send(int targetClientId, SignalMsgType type, byte[] payload)
        {
            Sent.Add((targetClientId, type, payload));
            if (TrySend != null)
                return TrySend(targetClientId, type, payload);
            Forward?.Invoke(targetClientId, type, payload);
            return true;
        }
    }

    private static byte[] CompatHello() => SignalPayload.Hello(PeerSessionManager.ProtocolVersion, PeerSessionManager.MinCompatibleVersion);

    [Fact]
    public void NegotiationPayloadsRoundTripAndRejectTrailingBytes()
    {
        var sdp = SignalPayload.Sdp(42, "offer", "v=0");
        Xunit.Assert.True(SignalPayload.TryReadSdp(sdp, out var sdpId, out var kind, out var text));
        Xunit.Assert.Equal(42, sdpId);
        Xunit.Assert.Equal("offer", kind);
        Xunit.Assert.Equal("v=0", text);

        var candidate = SignalPayload.Candidate(42, "candidate:1");
        Xunit.Assert.True(SignalPayload.TryReadCandidate(candidate, out var candidateId, out var value));
        Xunit.Assert.Equal(42, candidateId);
        Xunit.Assert.Equal("candidate:1", value);

        Xunit.Assert.False(SignalPayload.TryReadSdp(sdp.Concat(new byte[] { 0 }).ToArray(), out _, out _, out _));
        Xunit.Assert.False(SignalPayload.TryReadCandidate(candidate.Take(7).ToArray(), out _, out _));
    }

    [Theory]
    [InlineData(3, 7, true)]
    [InlineData(7, 3, false)]
    public void LowerClientIdIsTheOfferer(int local, int peer, bool expectOffer)
    {
        var transport = new MockTransport();
        var manager = new PeerSessionManager(local, transport, new MockSender());

        manager.OnSignal(peer, SignalMsgType.Hello, CompatHello());

        Xunit.Assert.True(manager.TryGetPeerState(peer, out var state));
        if (expectOffer)
        {
            Xunit.Assert.Equal(new[] { peer }, transport.Added);
            Xunit.Assert.Equal(PeerState.Offering, state);
        }
        else
        {
            Xunit.Assert.Empty(transport.Added);
            Xunit.Assert.Equal(PeerState.Answering, state);
        }
    }

    [Fact]
    public void IncompatibleHelloDoesNotPair()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnSignal(7, SignalMsgType.Hello, SignalPayload.Hello(99, 99));

        Xunit.Assert.Empty(transport.Added);
        Xunit.Assert.Empty(sender.Sent);
        Xunit.Assert.False(manager.TryGetPeerState(7, out _));
    }

    [Fact]
    public void SessionPayloadsBeforeCompatibleHelloAreIgnored()
    {
        var transport = new MockTransport();
        var manager = new PeerSessionManager(7, transport, new MockSender(), relayAvailable: () => true);

        manager.OnSignal(3, SignalMsgType.Offer, SignalPayload.Sdp(1, "offer", "untrusted"));
        manager.OnSignal(3, SignalMsgType.Candidate, SignalPayload.Candidate(1, "candidate"));
        manager.OnSignal(3, SignalMsgType.IceMode, SignalPayload.IceMode(true));

        Xunit.Assert.Empty(transport.Added);
        Xunit.Assert.Empty(transport.RemoteSdp);
        Xunit.Assert.Empty(transport.Candidates);
        Xunit.Assert.False(manager.TryGetPeerState(3, out _));
    }

    [Fact]
    public void SdpMessageTypeMustMatchItsSignalKind()
    {
        var answererTransport = new MockTransport();
        var answerer = new PeerSessionManager(7, answererTransport, new MockSender());
        answerer.OnSignal(3, SignalMsgType.Hello, CompatHello());
        answerer.OnSignal(3, SignalMsgType.Offer, SignalPayload.Sdp(1, "answer", "wrong-kind"));
        Xunit.Assert.Empty(answererTransport.RemoteSdp);

        var offererTransport = new MockTransport();
        var offerer = new PeerSessionManager(3, offererTransport, new MockSender());
        offerer.OnSignal(7, SignalMsgType.Hello, CompatHello());
        offerer.OnSignal(7, SignalMsgType.Answer, SignalPayload.Sdp(1, "offer", "wrong-kind"));
        Xunit.Assert.Empty(offererTransport.RemoteSdp);
    }

    [Fact]
    public void FullHappyPathCrossFedBetweenTwoManagers()
    {
        const int idA = 3;
        const int idB = 7;

        var transportA = new MockTransport();
        var transportB = new MockTransport();
        var senderA = new MockSender();
        var senderB = new MockSender();

        var managerA = new PeerSessionManager(idA, transportA, senderA);
        var managerB = new PeerSessionManager(idB, transportB, senderB);

        senderA.Forward = (_, type, payload) => managerB.OnSignal(idA, type, payload);
        senderB.Forward = (_, type, payload) => managerA.OnSignal(idB, type, payload);

        managerA.OnPlayerJoined(idB);
        managerB.OnPlayerJoined(idA);

        Xunit.Assert.Equal(new[] { idB }, transportA.Added);
        Xunit.Assert.Equal(new[] { true }, transportA.AddedOfferer);
        Xunit.Assert.Empty(transportB.Added);

        managerA.OnLocalSdp(idB, transportA.AddedGenerations.Last(), "offer", "SDP_OFFER");
        Xunit.Assert.Equal(new[] { idA }, transportB.Added);
        Xunit.Assert.Equal(new[] { false }, transportB.AddedOfferer);
        Xunit.Assert.Equal(new[] { (idA, "offer", "SDP_OFFER") }, transportB.RemoteSdp);

        managerB.OnLocalSdp(idA, transportB.AddedGenerations.Last(), "answer", "SDP_ANSWER");
        Xunit.Assert.Equal(new[] { (idB, "answer", "SDP_ANSWER") }, transportA.RemoteSdp);

        managerA.OnLocalCandidate(idB, transportA.AddedGenerations.Last(), "cand_from_a");
        managerB.OnLocalCandidate(idA, transportB.AddedGenerations.Last(), "cand_from_b");

        Xunit.Assert.Equal(new[] { (idA, "cand_from_a") }, transportB.Candidates);
        Xunit.Assert.Equal(new[] { (idB, "cand_from_b") }, transportA.Candidates);

        Xunit.Assert.True(managerA.TryGetPeerState(idB, out var stateA));
        Xunit.Assert.True(managerB.TryGetPeerState(idA, out var stateB));
        Xunit.Assert.Equal(PeerState.Connected, stateA);
        Xunit.Assert.Equal(PeerState.Connected, stateB);
    }

    [Fact]
    public void LostInitialHelloRecoversFromReciprocalHelloWithoutWaitingForTick()
    {
        const int idA = 3;
        const int idB = 7;
        var transportA = new MockTransport();
        var transportB = new MockTransport();
        var senderA = new MockSender();
        var senderB = new MockSender();
        var managerA = new PeerSessionManager(idA, transportA, senderA);
        var managerB = new PeerSessionManager(idB, transportB, senderB);
        var droppedFirstHello = false;

        senderA.TrySend = (_, type, payload) =>
        {
            if (type == SignalMsgType.Hello && !droppedFirstHello)
            {
                droppedFirstHello = true;
                return true; // The RPC writer succeeded, but this packet never reached B.
            }

            managerB.OnSignal(idA, type, payload, 1000);
            return true;
        };
        senderB.TrySend = (_, type, payload) =>
        {
            managerA.OnSignal(idB, type, payload, 1000);
            return true;
        };

        managerA.OnPlayerJoined(idB, 1000);
        Xunit.Assert.Equal(PeerState.Greeted, StateOf(managerA, idB));
        Xunit.Assert.Empty(transportA.Added);

        managerB.OnPlayerJoined(idA, 1000);

        Xunit.Assert.True(droppedFirstHello);
        Xunit.Assert.Equal(PeerState.Offering, StateOf(managerA, idB));
        Xunit.Assert.Equal(PeerState.Answering, StateOf(managerB, idA));
        Xunit.Assert.Equal(new[] { idB }, transportA.Added);
        Xunit.Assert.Empty(transportB.Added);
        Xunit.Assert.Equal(2, HelloCount(senderA));
        Xunit.Assert.Equal(2, HelloCount(senderB));
    }

    [Fact]
    public void FirstRemoteHelloIsAcknowledgedBeforeSynchronousOffer()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);
        transport.PeerAdded = (clientId, isOfferer, _, generation) =>
        {
            if (isOfferer)
                manager.OnLocalSdp(clientId, generation, "offer", "SYNCHRONOUS_OFFER");
        };

        manager.OnPlayerJoined(7, 1000);
        sender.Sent.Clear();

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);

        Xunit.Assert.Equal(
            new[] { SignalMsgType.Hello, SignalMsgType.Offer },
            sender.Sent.Select(message => message.Type).ToArray());
    }

    [Fact]
    public void FailedReciprocalHelloSendRetriesQuicklyBeforeStartingOffer()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnPlayerJoined(7, 900);
        var acknowledgementAttempts = 0;
        sender.TrySend = (_, type, _) => type != SignalMsgType.Hello || ++acknowledgementAttempts > 1;

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);

        Xunit.Assert.Equal(PeerState.Greeted, StateOf(manager, 7));
        Xunit.Assert.Empty(transport.Added);
        manager.Tick(1499);
        Xunit.Assert.Empty(transport.Added);

        manager.Tick(1500);

        Xunit.Assert.Equal(2, acknowledgementAttempts);
        Xunit.Assert.Equal(PeerState.Offering, StateOf(manager, 7));
        Xunit.Assert.Equal(new[] { 7 }, transport.Added);
    }

    [Fact]
    public void FailedInitialHelloSendRetriesAfterShortWriterBackoff()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);
        var helloAttempts = 0;
        sender.TrySend = (_, type, _) => type != SignalMsgType.Hello || ++helloAttempts > 1;

        manager.OnPlayerJoined(7, 1000);

        Xunit.Assert.Equal(1, helloAttempts);
        Xunit.Assert.Equal(PeerState.Greeted, StateOf(manager, 7));
        manager.Tick(1499);
        Xunit.Assert.Equal(1, helloAttempts);

        manager.Tick(1500);

        Xunit.Assert.Equal(2, helloAttempts);
        Xunit.Assert.Equal(2, HelloCount(sender));
        Xunit.Assert.Equal(PeerState.Greeted, StateOf(manager, 7));
    }

    [Fact]
    public void FailedOfferAndCandidatesRetryWithSdpFirstAndBoundedCandidateOrder()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);
        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        var generation = transport.LatestGeneration(7);
        var permitSessionTraffic = false;
        sender.TrySend = (_, type, _) =>
            type == SignalMsgType.Hello || type == SignalMsgType.Bye || permitSessionTraffic;

        manager.OnLocalSdp(7, generation, "offer", "RETRY_OFFER", nowMs: 1000);
        for (var index = 0; index < 70; index++)
            manager.OnLocalCandidate(7, generation, $"CANDIDATE_{index:D2}", nowMs: 1000);

        permitSessionTraffic = true;
        sender.Sent.Clear();
        manager.Tick(1499);
        Xunit.Assert.Empty(sender.Sent);

        manager.Tick(1500);

        var retried = sender.Sent
            .Where(message => message.Type is SignalMsgType.Offer or SignalMsgType.Candidate)
            .ToArray();
        Xunit.Assert.NotEmpty(retried);
        Xunit.Assert.Equal(SignalMsgType.Offer, retried[0].Type);
        Xunit.Assert.True(SignalPayload.TryReadSdp(
            retried[0].Payload,
            out _,
            out var retriedSdpType,
            out var retriedSdp));
        Xunit.Assert.Equal("offer", retriedSdpType);
        Xunit.Assert.Equal("RETRY_OFFER", retriedSdp);

        var retriedCandidates = retried
            .Where(message => message.Type == SignalMsgType.Candidate)
            .Select(message =>
            {
                Xunit.Assert.True(SignalPayload.TryReadCandidate(message.Payload, out _, out var candidate));
                return candidate;
            })
            .ToArray();
        Xunit.Assert.NotEmpty(retriedCandidates);
        Xunit.Assert.InRange(retriedCandidates.Length, 1, 64);
        Xunit.Assert.Equal(
            retriedCandidates.OrderBy(candidate => candidate, StringComparer.Ordinal).ToArray(),
            retriedCandidates);
        Xunit.Assert.Equal(retriedCandidates.Length, retriedCandidates.Distinct().Count());
    }

    [Fact]
    public void CandidateAfterFailedCandidateWaitsAndRetriesInOriginalOrder()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);
        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        var generation = transport.LatestGeneration(7);
        manager.OnLocalSdp(7, generation, "offer", "SENT_OFFER", nowMs: 1000);
        var candidateAttempts = 0;
        sender.TrySend = (_, type, _) => type != SignalMsgType.Candidate || ++candidateAttempts > 1;

        manager.OnLocalCandidate(7, generation, "FIRST", nowMs: 1100);
        manager.OnLocalCandidate(7, generation, "SECOND", nowMs: 1100);

        // FIRST reached the sender and failed. SECOND would succeed if attempted directly, but it
        // must wait behind FIRST so trickle ICE preserves the native callback order.
        Xunit.Assert.Equal(1, candidateAttempts);
        Xunit.Assert.Single(sender.Sent, message => message.Type == SignalMsgType.Candidate);
        sender.Sent.Clear();

        manager.Tick(1599);
        Xunit.Assert.Empty(sender.Sent);
        manager.Tick(1600);

        var candidates = sender.Sent
            .Where(message => message.Type == SignalMsgType.Candidate)
            .Select(message =>
            {
                Xunit.Assert.True(SignalPayload.TryReadCandidate(message.Payload, out _, out var candidate));
                return candidate;
            })
            .ToArray();
        Xunit.Assert.Equal(new[] { "FIRST", "SECOND" }, candidates);
        Xunit.Assert.Equal(3, candidateAttempts);
    }

    [Fact]
    public void FailedAnswerStaysAnsweringUntilTickSuccessfullyRetriesIt()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(7, transport, sender);
        const long negotiationId = 9001;
        manager.OnSignal(3, SignalMsgType.Hello, CompatHello(), 1000);
        manager.OnSignal(3, SignalMsgType.Offer, SignalPayload.Sdp(negotiationId, "offer", "REMOTE_OFFER"), 2000);
        var generation = transport.LatestGeneration(3);
        var answerAttempts = 0;
        sender.TrySend = (_, type, _) => type != SignalMsgType.Answer || ++answerAttempts > 1;

        manager.OnLocalSdp(3, generation, "answer", "RETRY_ANSWER", nowMs: 2000);

        Xunit.Assert.Equal(1, answerAttempts);
        Xunit.Assert.Equal(PeerState.Answering, StateOf(manager, 3));
        manager.Tick(2499);
        Xunit.Assert.Equal(1, answerAttempts);
        Xunit.Assert.Equal(PeerState.Answering, StateOf(manager, 3));

        manager.Tick(2500);

        Xunit.Assert.Equal(2, answerAttempts);
        Xunit.Assert.Equal(PeerState.Connected, StateOf(manager, 3));
        var answer = sender.Sent.Last(message => message.Type == SignalMsgType.Answer);
        Xunit.Assert.True(SignalPayload.TryReadSdp(
            answer.Payload,
            out var retriedNegotiationId,
            out var sdpType,
            out var sdp));
        Xunit.Assert.Equal(negotiationId, retriedNegotiationId);
        Xunit.Assert.Equal("answer", sdpType);
        Xunit.Assert.Equal("RETRY_ANSWER", sdp);
    }

    [Fact]
    public void DuplicateHelloIsReacknowledgedOnlyAfterTheBoundedResendInterval()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(7, transport, sender);

        manager.OnSignal(3, SignalMsgType.Hello, CompatHello(), 1000);
        Xunit.Assert.Equal(1, HelloCount(sender));
        sender.Sent.Clear();

        manager.OnSignal(3, SignalMsgType.Hello, CompatHello(), 3999);
        Xunit.Assert.Empty(sender.Sent);

        manager.OnSignal(3, SignalMsgType.Hello, CompatHello(), 4000);
        manager.OnSignal(3, SignalMsgType.Hello, CompatHello(), 4001);

        Xunit.Assert.Equal(1, HelloCount(sender));
        Xunit.Assert.Equal(PeerState.Answering, StateOf(manager, 3));
        Xunit.Assert.Empty(transport.Added);
    }

    [Fact]
    public void ReplacementHelloRebuildsAnEstablishedLocalOffererAfterAcknowledgement()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        var oldGeneration = transport.LatestGeneration(7);
        manager.OnPeerConnected(7, oldGeneration);
        transport.Added.Clear();
        transport.AddedGenerations.Clear();
        transport.Removed.Clear();
        sender.Sent.Clear();

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 4000);

        Xunit.Assert.Equal(1, HelloCount(sender));
        Xunit.Assert.Equal(new[] { 7 }, transport.Removed);
        Xunit.Assert.Equal(new[] { 7 }, transport.Added);
        Xunit.Assert.NotEqual(oldGeneration, transport.LatestGeneration(7));
        Xunit.Assert.Equal(PeerState.Offering, StateOf(manager, 7));
    }

    [Fact]
    public void ReplacementHelloLetsAnEstablishedAnswererAcceptAResetNegotiationCounter()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(7, transport, sender);

        manager.OnSignal(3, SignalMsgType.Hello, CompatHello(), 1000);
        manager.OnSignal(3, SignalMsgType.Offer, SignalPayload.Sdp(9000, "offer", "OLD_OFFER"), 1100);
        var oldGeneration = transport.LatestGeneration(3);
        manager.OnLocalSdp(3, oldGeneration, "answer", "OLD_ANSWER", nowMs: 1200);
        manager.OnPeerConnected(3, oldGeneration);
        transport.Added.Clear();
        transport.AddedGenerations.Clear();
        transport.Removed.Clear();
        transport.RemoteSdp.Clear();
        sender.Sent.Clear();

        manager.OnSignal(3, SignalMsgType.Hello, CompatHello(), 4000);

        Xunit.Assert.Equal(1, HelloCount(sender));
        Xunit.Assert.Equal(new[] { 3 }, transport.Removed);
        Xunit.Assert.Empty(transport.Added);
        Xunit.Assert.Equal(PeerState.Answering, StateOf(manager, 3));

        manager.OnSignal(3, SignalMsgType.Offer, SignalPayload.Sdp(1, "offer", "FRESH_OFFER"), 4100);

        Xunit.Assert.Equal(new[] { 3 }, transport.Added);
        Xunit.Assert.NotEqual(oldGeneration, transport.LatestGeneration(3));
        Xunit.Assert.Equal(new[] { (3, "offer", "FRESH_OFFER") }, transport.RemoteSdp);
    }

    [Fact]
    public void ResetAndNotifySendsByeBeforeFreshManagerRestartsHandshake()
    {
        const int idA = 3;
        const int idB = 7;
        var firstTransportA = new MockTransport();
        var transportB = new MockTransport();
        var senderA = new MockSender();
        var senderB = new MockSender();
        var firstManagerA = new PeerSessionManager(idA, firstTransportA, senderA);
        var managerB = new PeerSessionManager(idB, transportB, senderB);

        senderA.Forward = (_, type, payload) => managerB.OnSignal(idA, type, payload, 1000);
        senderB.Forward = (_, type, payload) => firstManagerA.OnSignal(idB, type, payload, 1000);
        firstManagerA.OnPlayerJoined(idB, 1000);
        managerB.OnPlayerJoined(idA, 1000);
        Xunit.Assert.Equal(PeerState.Offering, StateOf(firstManagerA, idB));
        Xunit.Assert.Equal(PeerState.Answering, StateOf(managerB, idA));

        senderA.Sent.Clear();
        firstManagerA.ResetAndNotify("backend-replaced");

        Xunit.Assert.Equal(SignalMsgType.Bye, senderA.Sent.Single().Type);
        Xunit.Assert.False(firstManagerA.TryGetPeerState(idB, out _));
        Xunit.Assert.False(managerB.TryGetPeerState(idA, out _));
        Xunit.Assert.Equal(new[] { idA }, transportB.Removed);

        var replacementTransportA = new MockTransport();
        var replacementManagerA = new PeerSessionManager(idA, replacementTransportA, senderA);
        senderB.Forward = (_, type, payload) => replacementManagerA.OnSignal(idB, type, payload, 2000);
        senderA.Forward = (_, type, payload) => managerB.OnSignal(idA, type, payload, 2000);
        replacementManagerA.OnPlayerJoined(idB, 2000);

        Xunit.Assert.Equal(
            new[] { SignalMsgType.Bye, SignalMsgType.Hello, SignalMsgType.Hello },
            senderA.Sent.Select(message => message.Type).ToArray());
        Xunit.Assert.Equal(PeerState.Offering, StateOf(replacementManagerA, idB));
        Xunit.Assert.Equal(PeerState.Answering, StateOf(managerB, idA));
        Xunit.Assert.Equal(new[] { idB }, replacementTransportA.Added);
    }

    [Fact]
    public void CandidateArrivingBeforeOfferIsReplayedAfterMatchingOffer()
    {
        var transport = new MockTransport();
        var manager = new PeerSessionManager(7, transport, new MockSender());
        const long negotiationId = 1234;

        manager.OnSignal(3, SignalMsgType.Hello, CompatHello(), 1000);
        manager.OnSignal(3, SignalMsgType.Candidate, SignalPayload.Candidate(negotiationId, "EARLY_ONE"), 1100);
        manager.OnSignal(3, SignalMsgType.Candidate, SignalPayload.Candidate(negotiationId, "EARLY_TWO"), 1200);
        manager.OnSignal(3, SignalMsgType.Candidate, SignalPayload.Candidate(negotiationId, "EARLY_ONE"), 1300);

        Xunit.Assert.Empty(transport.Added);
        Xunit.Assert.Empty(transport.Candidates);

        manager.OnSignal(3, SignalMsgType.Offer, SignalPayload.Sdp(negotiationId, "offer", "SDP_OFFER"), 1400);

        Xunit.Assert.Equal(new[] { 3 }, transport.Added);
        Xunit.Assert.Equal(new[] { (3, "offer", "SDP_OFFER") }, transport.RemoteSdp);
        Xunit.Assert.Equal(new[] { (3, "EARLY_ONE"), (3, "EARLY_TWO") }, transport.Candidates);
        var diagnostics = manager.GetDiagnosticsSnapshot();
        Xunit.Assert.Equal(3, diagnostics.RemoteCandidatesReceived);
        Xunit.Assert.Equal(2, diagnostics.RemoteCandidatesForwarded);
        Xunit.Assert.Equal(0, diagnostics.RejectedCandidates);
    }

    [Fact]
    public void OnPlayerLeftSendsByeAndRemovesPeer()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello());
        Xunit.Assert.Equal(new[] { 7 }, transport.Added);

        manager.OnPlayerLeft(7);

        Xunit.Assert.Contains(sender.Sent, m => m.Target == 7 && m.Type == SignalMsgType.Bye);
        Xunit.Assert.Equal(new[] { 7 }, transport.Removed);
        Xunit.Assert.False(manager.TryGetPeerState(7, out _));

        manager.OnSignal(7, SignalMsgType.Candidate, SignalPayload.Candidate(1, "late"));
        Xunit.Assert.Empty(transport.Candidates);
    }

    [Fact]
    public void ByeFromPeerRemovesWithoutReplying()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello());
        sender.Sent.Clear();

        manager.OnSignal(7, SignalMsgType.Bye, Array.Empty<byte>());

        Xunit.Assert.Equal(new[] { 7 }, transport.Removed);
        Xunit.Assert.DoesNotContain(sender.Sent, m => m.Type == SignalMsgType.Bye);
        Xunit.Assert.False(manager.TryGetPeerState(7, out _));
    }

    [Fact]
    public void DuplicateJoinAndHelloAreIdempotent()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnPlayerJoined(7);
        manager.OnPlayerJoined(7);
        manager.OnSignal(7, SignalMsgType.Hello, CompatHello());
        manager.OnSignal(7, SignalMsgType.Hello, CompatHello());

        Xunit.Assert.Equal(new[] { 7 }, transport.Added);
        Xunit.Assert.Equal(2, sender.Sent.Count(m => m.Type == SignalMsgType.Hello));
    }

    [Fact]
    public void ResetDropsAllPeers()
    {
        var transport = new MockTransport();
        var manager = new PeerSessionManager(3, transport, new MockSender());

        manager.OnSignal(5, SignalMsgType.Hello, CompatHello());
        manager.OnSignal(9, SignalMsgType.Hello, CompatHello());
        Xunit.Assert.Equal(new[] { 5, 9 }, transport.Added.OrderBy(x => x).ToArray());

        manager.Reset();

        Xunit.Assert.Equal(new[] { 5, 9 }, transport.Removed.OrderBy(x => x).ToArray());
        Xunit.Assert.False(manager.TryGetPeerState(5, out _));
        Xunit.Assert.False(manager.TryGetPeerState(9, out _));
    }

    private static int HelloCount(MockSender sender) => sender.Sent.Count(m => m.Type == SignalMsgType.Hello);

    private static PeerState StateOf(PeerSessionManager manager, int clientId)
    {
        Xunit.Assert.True(manager.TryGetPeerState(clientId, out var state));
        return state;
    }

    [Fact]
    public void GreetedPeerResendsHelloOncePerInterval()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnPlayerJoined(7, 1000);
        Xunit.Assert.Equal(PeerState.Greeted, StateOf(manager, 7));
        Xunit.Assert.Equal(1, HelloCount(sender));

        manager.Tick(1000);
        manager.Tick(3999);
        Xunit.Assert.Equal(1, HelloCount(sender));

        manager.Tick(4000);
        Xunit.Assert.Equal(2, HelloCount(sender));

        manager.Tick(6999);
        Xunit.Assert.Equal(2, HelloCount(sender));

        manager.Tick(7000);
        Xunit.Assert.Equal(3, HelloCount(sender));
    }

    [Fact]
    public void ConnectedPeerNeverResendsHello()
    {
        const int idA = 3;
        const int idB = 7;

        var transportA = new MockTransport();
        var transportB = new MockTransport();
        var senderA = new MockSender();
        var senderB = new MockSender();

        var managerA = new PeerSessionManager(idA, transportA, senderA);
        var managerB = new PeerSessionManager(idB, transportB, senderB);

        senderA.Forward = (_, type, payload) => managerB.OnSignal(idA, type, payload);
        senderB.Forward = (_, type, payload) => managerA.OnSignal(idB, type, payload);

        managerA.OnPlayerJoined(idB, 1000);
        managerB.OnPlayerJoined(idA, 1000);
        managerA.OnLocalSdp(idB, transportA.AddedGenerations.Last(), "offer", "SDP_OFFER");
        managerB.OnLocalSdp(idA, transportB.AddedGenerations.Last(), "answer", "SDP_ANSWER");
        managerA.OnPeerConnected(idB, transportA.AddedGenerations.Last());
        managerB.OnPeerConnected(idA, transportB.AddedGenerations.Last());

        Xunit.Assert.Equal(PeerState.Established, StateOf(managerA, idB));
        var before = HelloCount(senderA);

        managerA.Tick(1000 + 100000);

        Xunit.Assert.Equal(before, HelloCount(senderA));
    }

    [Fact]
    public void OnPlayerLeftStopsHelloResend()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnPlayerJoined(7, 1000);
        manager.OnPlayerLeft(7);
        var before = HelloCount(sender);

        manager.Tick(1000 + 100000);

        Xunit.Assert.Equal(before, HelloCount(sender));
    }

    [Fact]
    public void ResetStopsHelloResend()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnPlayerJoined(7, 1000);
        manager.Reset();
        var before = HelloCount(sender);

        manager.Tick(1000 + 100000);

        Xunit.Assert.Equal(before, HelloCount(sender));
    }

    [Fact]
    public void StuckInOfferingReinitiatesAfterTimeoutButNotBefore()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        Xunit.Assert.Equal(PeerState.Offering, StateOf(manager, 7));
        Xunit.Assert.Equal(new[] { 7 }, transport.Added);

        manager.Tick(1000 + 11999);
        Xunit.Assert.Equal(new[] { 7 }, transport.Added);
        Xunit.Assert.Empty(transport.Removed);

        manager.Tick(1000 + 12000);
        Xunit.Assert.Equal(new[] { 7, 7 }, transport.Added);
        Xunit.Assert.Equal(new[] { 7 }, transport.Removed);
        Xunit.Assert.Equal(PeerState.Offering, StateOf(manager, 7));
    }

    [Fact]
    public void ActiveNegotiationDefersOuterRecoveryForTheEntireHandshakeDeadline()
    {
        var transport = new MockTransport();
        var manager = new PeerSessionManager(3, transport, new MockSender());

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);

        Xunit.Assert.True(manager.HasActiveNegotiation(1000));
        Xunit.Assert.True(manager.HasActiveNegotiation(1000 + PeerSessionManager.HandshakeTimeoutMs - 1));
        Xunit.Assert.False(manager.HasActiveNegotiation(1000 + PeerSessionManager.HandshakeTimeoutMs));

        // Once the manager itself owns the timeout it starts a fresh generation. The outer room
        // watchdog must likewise respect that new generation instead of tearing it down early.
        manager.Tick(1000 + PeerSessionManager.HandshakeTimeoutMs);
        Xunit.Assert.True(manager.HasActiveNegotiation(1000 + PeerSessionManager.HandshakeTimeoutMs));
    }

    [Fact]
    public void SuccessfulDelayedLocalOfferRefreshesHandshakeDeadline()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        var generation = transport.AddedGenerations.Last();
        manager.OnLocalSdp(7, generation, "offer", "DELAYED_OFFER", nowMs: 11_000);

        manager.Tick(13_000); // old AddPeer-based deadline; must no longer recover here
        Xunit.Assert.Empty(transport.Removed);
        Xunit.Assert.Equal(generation, transport.AddedGenerations.Last());

        manager.Tick(22_999);
        Xunit.Assert.Empty(transport.Removed);
        manager.Tick(23_000);
        Xunit.Assert.NotEmpty(transport.Removed);
        Xunit.Assert.NotEqual(generation, transport.AddedGenerations.Last());
    }

    [Fact]
    public void SuccessfulDelayedLocalAnswerRefreshesHandshakeDeadline()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(7, transport, sender);

        manager.OnSignal(3, SignalMsgType.Hello, CompatHello(), 1000);
        manager.OnSignal(3, SignalMsgType.Offer,
            SignalPayload.Sdp(42, "offer", "REMOTE_OFFER"), 2000);
        var generation = transport.AddedGenerations.Last();
        manager.OnLocalSdp(3, generation, "answer", "DELAYED_ANSWER", nowMs: 11_000);

        manager.Tick(14_000); // old remote-Offer-based deadline
        Xunit.Assert.Empty(transport.Removed);
        Xunit.Assert.Equal(PeerState.Connected, StateOf(manager, 3));

        manager.Tick(22_999);
        Xunit.Assert.Empty(transport.Removed);
        manager.Tick(23_000);
        Xunit.Assert.NotEmpty(transport.Removed);
    }

    [Fact]
    public void OnPeerConnectionLostReOffersWhenOfferer()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        Xunit.Assert.Equal(new[] { 7 }, transport.Added);

        manager.OnPeerConnectionLost(7, transport.AddedGenerations.Last(), 5000);

        Xunit.Assert.Equal(new[] { 7, 7 }, transport.Added);
        Xunit.Assert.Equal(new[] { 7 }, transport.Removed);
        Xunit.Assert.Equal(PeerState.Offering, StateOf(manager, 7));
    }

    [Fact]
    public void OnPeerConnectionLostReHellosWhenAnswerer()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(7, transport, sender);

        manager.OnSignal(3, SignalMsgType.Hello, CompatHello(), 1000);
        Xunit.Assert.Equal(PeerState.Answering, StateOf(manager, 3));
        manager.OnSignal(3, SignalMsgType.Offer, SignalPayload.Sdp(1, "offer", "SDP_OFFER"), 2000);
        var generation = transport.AddedGenerations.Last();
        transport.Added.Clear();
        var before = HelloCount(sender);

        manager.OnPeerConnectionLost(3, generation, 5000);

        Xunit.Assert.Empty(transport.Added);
        Xunit.Assert.Equal(before + 1, HelloCount(sender));
        Xunit.Assert.Equal(PeerState.Greeted, StateOf(manager, 3));
    }

    [Fact]
    public void OnPeerConnectedStopsHandshakeTimeout()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        Xunit.Assert.Equal(PeerState.Offering, StateOf(manager, 7));

        manager.OnPeerConnected(7, transport.AddedGenerations.Last());
        Xunit.Assert.Equal(PeerState.Established, StateOf(manager, 7));

        manager.Tick(1000 + 100000);

        Xunit.Assert.Equal(new[] { 7 }, transport.Added);
        Xunit.Assert.Empty(transport.Removed);
        Xunit.Assert.Equal(PeerState.Established, StateOf(manager, 7));
    }

    [Fact]
    public void SdpConnectedWithoutNativeIceSuccessTimesOutIntoRelay()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender, relayAvailable: () => true);

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        manager.OnLocalSdp(7, transport.LatestGeneration(7), "offer", "SDP_OFFER");
        var offer = sender.Sent.Last(message => message.Type == SignalMsgType.Offer);
        Xunit.Assert.True(SignalPayload.TryReadSdp(offer.Payload, out var negotiationId, out _, out _));
        manager.OnSignal(7, SignalMsgType.Answer, SignalPayload.Sdp(negotiationId, "answer", "SDP_ANSWER"), 2000);
        Xunit.Assert.Equal(PeerState.Connected, StateOf(manager, 7));
        transport.Added.Clear();
        transport.AddedRelayOnly.Clear();
        transport.Removed.Clear();

        manager.Tick(13999);
        Xunit.Assert.Empty(transport.Removed);

        manager.Tick(14000);
        Xunit.Assert.Equal(new[] { 7 }, transport.Removed);
        Xunit.Assert.Equal(new[] { 7 }, transport.Added);
        Xunit.Assert.Equal(new[] { true }, transport.AddedRelayOnly);
    }

    [Fact]
    public void ReinitiationIsThrottled()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        Xunit.Assert.Equal(new[] { 7 }, transport.Added);

        manager.OnPeerConnectionLost(7, transport.AddedGenerations.Last(), 5000);
        Xunit.Assert.Equal(new[] { 7, 7 }, transport.Added);

        manager.OnPeerConnectionLost(7, transport.AddedGenerations.Last(), 5100);
        Xunit.Assert.Equal(new[] { 7, 7 }, transport.Added);

        manager.OnPeerConnectionLost(7, transport.AddedGenerations.Last(), 5000 + 3000);
        Xunit.Assert.Equal(new[] { 7, 7, 7 }, transport.Added);
    }

    [Fact]
    public void FailedPeerEscalatesAloneWhenTurnIsReady()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender, relayAvailable: () => true);

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        manager.OnSignal(9, SignalMsgType.Hello, CompatHello(), 1000);
        manager.OnPeerConnected(7, transport.AddedGenerations[0]);
        manager.OnPeerConnected(9, transport.AddedGenerations[1]);
        transport.Added.Clear();
        transport.AddedRelayOnly.Clear();
        transport.Removed.Clear();
        sender.Sent.Clear();

        manager.OnPeerConnectionLost(7, transport.AddedGenerations[0], 5000);

        Xunit.Assert.Equal(new[] { 7 }, transport.Removed);
        Xunit.Assert.Equal(new[] { 7 }, transport.Added);
        Xunit.Assert.Equal(new[] { true }, transport.AddedRelayOnly);
        Xunit.Assert.Contains(sender.Sent, m =>
            m.Target == 7 && m.Type == SignalMsgType.IceMode &&
            SignalPayload.TryReadIceMode(m.Payload, out var relay) && relay);
        Xunit.Assert.DoesNotContain(sender.Sent, m => m.Target == 9);
        Xunit.Assert.Equal(PeerState.Established, StateOf(manager, 9));
        Xunit.Assert.True(manager.TryGetPeerRelayOnly(7, out var sevenRelay) && sevenRelay);
        Xunit.Assert.True(manager.TryGetPeerRelayOnly(9, out var nineRelay) && !nineRelay);
    }

    [Fact]
    public void RemoteRelayRequestWaitsForCredentialsWithoutTearingDownDirectPeer()
    {
        var relayAvailable = false;
        var requested = new List<int>();
        var transport = new MockTransport();
        var manager = new PeerSessionManager(
            3,
            transport,
            new MockSender(),
            relayAvailable: () => relayAvailable,
            requestRelay: requested.Add);

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        manager.OnPeerConnected(7, transport.AddedGenerations.Last());
        transport.Added.Clear();
        transport.AddedRelayOnly.Clear();
        transport.Removed.Clear();

        manager.OnSignal(7, SignalMsgType.IceMode, SignalPayload.IceMode(true), 5000);

        Xunit.Assert.Equal(new[] { 7 }, requested);
        Xunit.Assert.Empty(transport.Removed);
        Xunit.Assert.Empty(transport.Added);
        Xunit.Assert.Equal(PeerState.Established, StateOf(manager, 7));

        relayAvailable = true;
        manager.EscalatePeer(7, 6000);

        Xunit.Assert.Equal(new[] { 7 }, transport.Removed);
        Xunit.Assert.Equal(new[] { 7 }, transport.Added);
        Xunit.Assert.Equal(new[] { true }, transport.AddedRelayOnly);
    }

    [Fact]
    public void RefreshRecreatesOnlyRelayPeers()
    {
        var transport = new MockTransport();
        var manager = new PeerSessionManager(3, transport, new MockSender(), relayAvailable: () => true);
        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        manager.OnSignal(9, SignalMsgType.Hello, CompatHello(), 1000);
        manager.OnPeerConnected(7, transport.AddedGenerations[0]);
        manager.OnPeerConnected(9, transport.AddedGenerations[1]);
        manager.RequestRelayForPeer(7, 5000);
        manager.OnPeerConnected(7, transport.AddedGenerations.Last());
        transport.Added.Clear();
        transport.Removed.Clear();

        var count = manager.RefreshRelayPeers(9000);

        Xunit.Assert.Equal(1, count);
        Xunit.Assert.Equal(new[] { 7 }, transport.Removed);
        Xunit.Assert.Equal(new[] { 7 }, transport.Added);
        Xunit.Assert.DoesNotContain(9, transport.Removed);
    }

    [Fact]
    public void StaleNativeGenerationCannotSignalOrChangeReplacementPeer()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender, relayAvailable: () => true);
        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        var directGeneration = transport.LatestGeneration(7);

        manager.RequestRelayForPeer(7, 5000);
        var relayGeneration = transport.LatestGeneration(7);
        Xunit.Assert.NotEqual(directGeneration, relayGeneration);
        sender.Sent.Clear();
        var addedBefore = transport.Added.Count;
        var removedBefore = transport.Removed.Count;

        manager.OnLocalSdp(7, directGeneration, "offer", "STALE_SDP");
        manager.OnLocalCandidate(7, directGeneration, "STALE_CANDIDATE");
        manager.OnPeerConnected(7, directGeneration);
        manager.OnPeerConnectionDegraded(7, directGeneration, 8000);
        manager.OnPeerConnectionLost(7, directGeneration, 9000);

        Xunit.Assert.Empty(sender.Sent);
        Xunit.Assert.Equal(addedBefore, transport.Added.Count);
        Xunit.Assert.Equal(removedBefore, transport.Removed.Count);
        Xunit.Assert.Equal(PeerState.Offering, StateOf(manager, 7));

        manager.OnLocalSdp(7, relayGeneration, "offer", "CURRENT_SDP");
        manager.OnLocalCandidate(7, relayGeneration, "CURRENT_CANDIDATE");
        manager.OnPeerConnected(7, relayGeneration);

        Xunit.Assert.Contains(sender.Sent, m => m.Type == SignalMsgType.Offer);
        Xunit.Assert.Contains(sender.Sent, m => m.Type == SignalMsgType.Candidate);
        Xunit.Assert.Equal(PeerState.Established, StateOf(manager, 7));
    }

    [Fact]
    public void LeaveAndRejoinUsesANewGeneration()
    {
        var transport = new MockTransport();
        var manager = new PeerSessionManager(3, transport, new MockSender());
        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        var firstGeneration = transport.LatestGeneration(7);

        manager.OnPlayerLeft(7);
        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 5000);
        var secondGeneration = transport.LatestGeneration(7);

        Xunit.Assert.NotEqual(firstGeneration, secondGeneration);
        manager.OnPeerConnected(7, firstGeneration);
        Xunit.Assert.Equal(PeerState.Offering, StateOf(manager, 7));
        manager.OnPeerConnected(7, secondGeneration);
        Xunit.Assert.Equal(PeerState.Established, StateOf(manager, 7));
    }

    [Fact]
    public void ReusedHelperHandoffCannotCollideWithPreviousManagerGeneration()
    {
        var oldTransport = new MockTransport();
        var oldManager = new PeerSessionManager(3, oldTransport, new MockSender());
        oldManager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        var delayedOldGeneration = oldTransport.LatestGeneration(7);

        var newTransport = new MockTransport();
        var newManager = new PeerSessionManager(3, newTransport, new MockSender());
        newManager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 2000);
        var newGeneration = newTransport.LatestGeneration(7);

        Xunit.Assert.NotEqual(delayedOldGeneration, newGeneration);
        newManager.OnPeerConnectionLost(7, delayedOldGeneration, 3000);
        Xunit.Assert.Equal(PeerState.Offering, StateOf(newManager, 7));

        newManager.OnPeerConnected(7, newGeneration);
        Xunit.Assert.Equal(PeerState.Established, StateOf(newManager, 7));
    }

    [Fact]
    public void ReplacedManagerUsesANewProcessWideNegotiationId()
    {
        var oldTransport = new MockTransport();
        var oldSender = new MockSender();
        var oldManager = new PeerSessionManager(3, oldTransport, oldSender);
        oldManager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        oldManager.OnLocalSdp(7, oldTransport.LatestGeneration(7), "offer", "OLD_OFFER");
        var oldOffer = oldSender.Sent.Last(message => message.Type == SignalMsgType.Offer);
        Xunit.Assert.True(SignalPayload.TryReadSdp(oldOffer.Payload, out var oldNegotiationId, out _, out _));

        var newTransport = new MockTransport();
        var newSender = new MockSender();
        var newManager = new PeerSessionManager(3, newTransport, newSender);
        newManager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 2000);
        newManager.OnLocalSdp(7, newTransport.LatestGeneration(7), "offer", "NEW_OFFER");
        var newOffer = newSender.Sent.Last(message => message.Type == SignalMsgType.Offer);
        Xunit.Assert.True(SignalPayload.TryReadSdp(newOffer.Payload, out var newNegotiationId, out _, out _));

        Xunit.Assert.True(newNegotiationId > oldNegotiationId);
    }

    [Fact]
    public void RelayPeerWaitsForFreshCredentialsWithoutDirectDowngrade()
    {
        var relayAvailable = true;
        var requested = new List<int>();
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(
            3,
            transport,
            sender,
            relayAvailable: () => relayAvailable,
            requestRelay: requested.Add);
        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        manager.RequestRelayForPeer(7, 5000);
        var relayGeneration = transport.LatestGeneration(7);
        manager.OnPeerConnected(7, relayGeneration);
        transport.Added.Clear();
        transport.AddedGenerations.Clear();
        transport.AddedRelayOnly.Clear();
        transport.Removed.Clear();
        sender.Sent.Clear();

        relayAvailable = false;
        manager.OnPeerConnectionLost(7, relayGeneration, 9000);

        Xunit.Assert.Equal(new[] { 7 }, requested);
        Xunit.Assert.Empty(transport.Removed);
        Xunit.Assert.Empty(transport.Added);
        Xunit.Assert.True(manager.TryGetPeerRelayOnly(7, out var relayOnly) && relayOnly);
        Xunit.Assert.DoesNotContain(sender.Sent, m =>
            m.Type == SignalMsgType.IceMode &&
            SignalPayload.TryReadIceMode(m.Payload, out var relay) && !relay);

        relayAvailable = true;
        manager.EscalatePeer(7, 12000);
        Xunit.Assert.Equal(new[] { 7 }, transport.Removed);
        Xunit.Assert.Equal(new[] { 7 }, transport.Added);
        Xunit.Assert.Equal(new[] { true }, transport.AddedRelayOnly);
        Xunit.Assert.NotEqual(relayGeneration, transport.AddedGenerations.Single());
    }

    [Fact]
    public void HealthQueriesAndTargetedRecoveryReflectHandshakeState()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);
        manager.OnPlayerJoined(7, 1000);
        manager.OnSignal(9, SignalMsgType.Hello, CompatHello(), 1000);

        Xunit.Assert.False(manager.IsCompatiblePeer(7));
        Xunit.Assert.True(manager.IsCompatiblePeer(9));
        Xunit.Assert.False(manager.IsPeerEstablished(9));
        Xunit.Assert.Equal(1, manager.CompatiblePeerCount);
        Xunit.Assert.Equal(0, manager.EstablishedPeerCount);

        var hellosBefore = HelloCount(sender);
        Xunit.Assert.True(manager.TryRecoverPeer(7, 5000));
        Xunit.Assert.Equal(hellosBefore + 1, HelloCount(sender));
        Xunit.Assert.False(manager.TryRecoverPeer(7, 7999));
        Xunit.Assert.True(manager.TryRecoverPeer(7, 8000));

        var firstGeneration = transport.LatestGeneration(9);
        Xunit.Assert.True(manager.TryRecoverPeer(9, 5000));
        Xunit.Assert.Contains(9, transport.Removed);
        Xunit.Assert.NotEqual(firstGeneration, transport.LatestGeneration(9));

        var generation = transport.LatestGeneration(9);
        manager.OnPeerConnected(9, generation);
        Xunit.Assert.True(manager.IsPeerEstablished(9));
        Xunit.Assert.Equal(1, manager.EstablishedPeerCount);
        Xunit.Assert.False(manager.TryRecoverPeer(9, 10000));

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 11000);
        Xunit.Assert.Equal(2, manager.CompatiblePeerCount);
    }

    [Fact]
    public void DelayedAnswerAndCandidateCannotCrossNegotiations()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender, relayAvailable: () => true);
        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);

        var directGeneration = transport.LatestGeneration(7);
        manager.OnLocalSdp(7, directGeneration, "offer", "DIRECT_OFFER");
        var directOffer = sender.Sent.Last(m => m.Type == SignalMsgType.Offer).Payload;
        Xunit.Assert.True(SignalPayload.TryReadSdp(
            directOffer,
            out var directNegotiation,
            out _,
            out _));

        manager.RequestRelayForPeer(7, 5000);
        var relayGeneration = transport.LatestGeneration(7);
        manager.OnLocalSdp(7, relayGeneration, "offer", "RELAY_OFFER");
        var relayOffer = sender.Sent.Last(m => m.Type == SignalMsgType.Offer).Payload;
        Xunit.Assert.True(SignalPayload.TryReadSdp(
            relayOffer,
            out var relayNegotiation,
            out _,
            out _));
        Xunit.Assert.True(relayNegotiation > directNegotiation);

        manager.OnSignal(
            7,
            SignalMsgType.Answer,
            SignalPayload.Sdp(directNegotiation, "answer", "STALE_ANSWER"),
            6000);
        manager.OnSignal(
            7,
            SignalMsgType.Candidate,
            SignalPayload.Candidate(directNegotiation, "STALE_CANDIDATE"),
            6000);

        Xunit.Assert.Empty(transport.RemoteSdp);
        Xunit.Assert.Empty(transport.Candidates);
        Xunit.Assert.Equal(PeerState.Offering, StateOf(manager, 7));

        manager.OnSignal(
            7,
            SignalMsgType.Answer,
            SignalPayload.Sdp(relayNegotiation, "answer", "CURRENT_ANSWER"),
            7000);
        manager.OnSignal(
            7,
            SignalMsgType.Candidate,
            SignalPayload.Candidate(relayNegotiation, "CURRENT_CANDIDATE"),
            7000);

        Xunit.Assert.Equal(new[] { (7, "answer", "CURRENT_ANSWER") }, transport.RemoteSdp);
        Xunit.Assert.Equal(new[] { (7, "CURRENT_CANDIDATE") }, transport.Candidates);
    }

    [Fact]
    public void DelayedOfferCannotReplaceNewerAnswererNegotiation()
    {
        var transport = new MockTransport();
        var manager = new PeerSessionManager(7, transport, new MockSender());
        manager.OnSignal(3, SignalMsgType.Hello, CompatHello(), 1000);

        manager.OnSignal(3, SignalMsgType.Offer, SignalPayload.Sdp(10, "offer", "FIRST"), 2000);
        manager.OnSignal(3, SignalMsgType.Offer, SignalPayload.Sdp(11, "offer", "CURRENT"), 3000);
        var addCount = transport.Added.Count;
        var removeCount = transport.Removed.Count;

        manager.OnSignal(3, SignalMsgType.Offer, SignalPayload.Sdp(10, "offer", "STALE"), 4000);
        manager.OnSignal(3, SignalMsgType.Candidate, SignalPayload.Candidate(10, "STALE"), 4000);
        manager.OnSignal(3, SignalMsgType.Candidate, SignalPayload.Candidate(11, "CURRENT"), 4000);

        Xunit.Assert.Equal(addCount, transport.Added.Count);
        Xunit.Assert.Equal(removeCount, transport.Removed.Count);
        Xunit.Assert.DoesNotContain(transport.RemoteSdp, item => item.Sdp == "STALE");
        Xunit.Assert.Equal(new[] { (3, "CURRENT") }, transport.Candidates);
    }

    [Fact]
    public void RelayAnswererFailureRequestsFreshOffer()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(7, transport, sender, relayAvailable: () => true);
        manager.OnSignal(3, SignalMsgType.Hello, CompatHello(), 1000);
        manager.OnSignal(3, SignalMsgType.Offer, SignalPayload.Sdp(1, "offer", "DIRECT"), 2000);
        manager.RequestRelayForPeer(3, 6000);
        var relayGeneration = transport.LatestGeneration(3);
        manager.OnSignal(3, SignalMsgType.Offer, SignalPayload.Sdp(2, "offer", "RELAY"), 7000);
        relayGeneration = transport.LatestGeneration(3);
        manager.OnPeerConnected(3, relayGeneration);
        sender.Sent.Clear();

        manager.OnPeerConnectionLost(3, relayGeneration, 11000);

        Xunit.Assert.Contains(sender.Sent, message => message.Type == SignalMsgType.Restart);
        Xunit.Assert.Equal(PeerState.Greeted, StateOf(manager, 3));
    }

    [Fact]
    public void RestartSignalReoffersOnlyForEstablishedOfferer()
    {
        var transport = new MockTransport();
        var manager = new PeerSessionManager(3, transport, new MockSender());
        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        var generation = transport.LatestGeneration(7);
        manager.OnPeerConnected(7, generation);
        var addsBefore = transport.Added.Count;

        manager.OnSignal(7, SignalMsgType.Restart, new byte[] { 1 }, 5000);
        Xunit.Assert.Equal(addsBefore, transport.Added.Count);

        manager.OnSignal(7, SignalMsgType.Restart, Array.Empty<byte>(), 5000);
        Xunit.Assert.Equal(addsBefore + 1, transport.Added.Count);
        Xunit.Assert.Contains(7, transport.Removed);
        Xunit.Assert.Equal(PeerState.Offering, StateOf(manager, 7));
    }

    [Fact]
    public void DiagnosticsSnapshotTracksPeerStateAndCandidateFlow()
    {
        var transport = new MockTransport();
        var sender = new MockSender();
        var manager = new PeerSessionManager(3, transport, sender);

        manager.OnSignal(7, SignalMsgType.Hello, CompatHello(), 1000);
        var generation = transport.LatestGeneration(7);
        manager.OnLocalSdp(7, generation, "offer", "LOCAL_OFFER");
        var offer = sender.Sent.Last(message => message.Type == SignalMsgType.Offer);
        Xunit.Assert.True(SignalPayload.TryReadSdp(offer.Payload, out var negotiationId, out _, out _));
        manager.OnLocalCandidate(7, generation, "LOCAL_CANDIDATE");
        manager.OnSignal(7, SignalMsgType.Candidate, SignalPayload.Candidate(negotiationId, "REMOTE_CANDIDATE"), 2000);
        manager.OnSignal(7, SignalMsgType.Candidate, SignalPayload.Candidate(negotiationId + 1, "STALE_CANDIDATE"), 2000);

        var negotiating = manager.GetDiagnosticsSnapshot();
        Xunit.Assert.Equal(1, negotiating.KnownPeers);
        Xunit.Assert.Equal(1, negotiating.CompatiblePeers);
        Xunit.Assert.Equal(1, negotiating.NegotiatingPeers);
        Xunit.Assert.Equal(0, negotiating.EstablishedPeers);
        Xunit.Assert.Equal(1, negotiating.LocalCandidatesAttempted);
        Xunit.Assert.Equal(2, negotiating.RemoteCandidatesReceived);
        Xunit.Assert.Equal(1, negotiating.RemoteCandidatesForwarded);
        Xunit.Assert.Equal(1, negotiating.RejectedCandidates);
        Xunit.Assert.Contains("7:Offering/", negotiating.PeerStates);
        Xunit.Assert.Contains("candAttempts1-rx2-forwarded1-rejected1", negotiating.PeerStates);

        manager.OnPeerConnected(7, generation);

        var established = manager.GetDiagnosticsSnapshot();
        Xunit.Assert.Equal(0, established.NegotiatingPeers);
        Xunit.Assert.Equal(1, established.EstablishedPeers);
        Xunit.Assert.Contains("7:Established/", established.PeerStates);
    }
}
