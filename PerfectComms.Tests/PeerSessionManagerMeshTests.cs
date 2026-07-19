using System;
using System.Collections.Generic;
using System.Linq;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class PeerSessionManagerMeshTests
{
    private sealed class MeshTransport : IVoiceTransport
    {
        public readonly List<int> Added = new();
        public readonly List<bool> AddedOfferer = new();
        public readonly List<int> AddedGenerations = new();
        public readonly List<int> Removed = new();
        public readonly List<(int Id, string Type, string Sdp)> RemoteSdp = new();
        public readonly List<(int Id, string Candidate)> Candidates = new();
        public readonly List<(int Id, bool CreateOffer)> IceRestarts = new();

        public bool AddPeer(int clientId, bool isOfferer, int generation)
        {
            Added.Add(clientId);
            AddedOfferer.Add(isOfferer);
            AddedGenerations.Add(generation);
            return true;
        }

        public bool RemovePeer(int clientId)
        {
            Removed.Add(clientId);
            return true;
        }

        public bool SetRemoteSdp(int clientId, string sdpType, string sdp)
        {
            RemoteSdp.Add((clientId, sdpType, sdp));
            return true;
        }

        public bool AddIceCandidate(int clientId, string candidate)
        {
            Candidates.Add((clientId, candidate));
            return true;
        }

        public bool RestartIce(int clientId, bool createOffer)
        {
            IceRestarts.Add((clientId, createOffer));
            return true;
        }

        public int LatestGeneration(int clientId)
        {
            for (var index = Added.Count - 1; index >= 0; index--)
                if (Added[index] == clientId)
                    return AddedGenerations[index];
            throw new InvalidOperationException($"peer {clientId} has not been added");
        }
    }

    private sealed class MeshSender : ISignalingSender
    {
        public readonly List<(int Target, SignalMsgType Type, byte[] Payload)> Sent = new();
        public Action<int, SignalMsgType, byte[]>? Forward;

        public bool Send(int targetClientId, SignalMsgType type, byte[] payload)
        {
            Sent.Add((targetClientId, type, payload));
            Forward?.Invoke(targetClientId, type, payload);
            return true;
        }
    }

    [Fact]
    public void TenClientMeshRoutesEveryNegotiationAndSurvivesOneClientRejoin()
    {
        const int clientCount = 10;
        const int leavingClientId = 5;
        var mesh = new Mesh(clientCount);

        // Mirror a full roster notification on every client. Signaling is delivered synchronously
        // through the same payloads that the Among Us RPC sender frames for the network.
        mesh.NotifyFullRoster();
        for (var first = 1; first <= clientCount; first++)
        for (var second = first + 1; second <= clientCount; second++)
            mesh.CompletePair(first, second, "INITIAL");

        AssertInitialMesh(mesh, clientCount);

        var originalLeavingTransport = mesh.Transports[leavingClientId];
        var oldGenerations = new Dictionary<(int Local, int Remote), int>();
        foreach (var remoteClientId in mesh.ClientIds.Where(id => id != leavingClientId))
        {
            oldGenerations[(leavingClientId, remoteClientId)] =
                originalLeavingTransport.LatestGeneration(remoteClientId);
            oldGenerations[(remoteClientId, leavingClientId)] =
                mesh.Transports[remoteClientId].LatestGeneration(leavingClientId);
        }

        var stableGenerations = new Dictionary<(int Local, int Remote), int>();
        foreach (var localClientId in mesh.ClientIds.Where(id => id != leavingClientId))
        foreach (var remoteClientId in mesh.ClientIds.Where(id =>
                     id != leavingClientId && id != localClientId))
            stableGenerations[(localClientId, remoteClientId)] =
                mesh.Transports[localClientId].LatestGeneration(remoteClientId);

        // Every surviving roster removes the departed client. Its synchronous Bye also clears the
        // corresponding peer from the departing manager, while survivor-to-survivor links stay up.
        mesh.NowMs = 2000;
        foreach (var survivorId in mesh.ClientIds.Where(id => id != leavingClientId))
            mesh.Managers[survivorId].OnPlayerLeft(leavingClientId);
        mesh.Managers[leavingClientId].Reset();

        AssertStableLinks(mesh, stableGenerations);
        foreach (var survivorId in mesh.ClientIds.Where(id => id != leavingClientId))
        {
            Assert.False(mesh.Managers[survivorId].TryGetPeerState(leavingClientId, out _));
            Assert.Equal(new[] { leavingClientId }, mesh.Transports[survivorId].Removed);
        }
        Assert.Equal(
            mesh.ClientIds.Where(id => id != leavingClientId).OrderBy(id => id).ToArray(),
            originalLeavingTransport.Removed.OrderBy(id => id).ToArray());

        // Replacing the manager models the same client ID returning with a fresh mod/backend
        // session. Existing sender closures resolve the replacement dynamically from Managers.
        mesh.InstallClient(leavingClientId);
        foreach (var survivorId in mesh.ClientIds.Where(id => id != leavingClientId))
        {
            mesh.Managers[leavingClientId].OnPlayerJoined(survivorId, mesh.NowMs);
            mesh.Managers[survivorId].OnPlayerJoined(leavingClientId, mesh.NowMs);
        }
        foreach (var survivorId in mesh.ClientIds.Where(id => id != leavingClientId))
            mesh.CompletePair(leavingClientId, survivorId, "REJOIN");

        AssertOldGenerationsAreFenced(mesh, oldGenerations);
        AssertStableLinks(mesh, stableGenerations);
    }

    private sealed class Mesh
    {
        public Mesh(int clientCount)
        {
            ClientIds = Enumerable.Range(1, clientCount).ToArray();
            foreach (var clientId in ClientIds)
                InstallClient(clientId);
        }

        public int[] ClientIds { get; }
        public Dictionary<int, PeerSessionManager> Managers { get; } = new();
        public Dictionary<int, MeshTransport> Transports { get; } = new();
        public Dictionary<int, MeshSender> Senders { get; } = new();
        public long NowMs { get; set; } = 1000;

        public void InstallClient(int clientId)
        {
            var transport = new MeshTransport();
            var sender = new MeshSender();
            var manager = new PeerSessionManager(clientId, transport, sender);
            Transports[clientId] = transport;
            Senders[clientId] = sender;
            Managers[clientId] = manager;
            sender.Forward = (target, type, payload) =>
                Managers[target].OnSignal(clientId, type, payload, NowMs);
        }

        public void NotifyFullRoster()
        {
            foreach (var localClientId in ClientIds)
            foreach (var remoteClientId in ClientIds)
                if (localClientId != remoteClientId)
                    Managers[localClientId].OnPlayerJoined(remoteClientId, NowMs);
        }

        public void CompletePair(int firstClientId, int secondClientId, string epoch)
        {
            var offererId = Math.Min(firstClientId, secondClientId);
            var answererId = Math.Max(firstClientId, secondClientId);
            var offerer = Managers[offererId];
            var answerer = Managers[answererId];
            var offererTransport = Transports[offererId];
            var answererTransport = Transports[answererId];
            var offererCandidateCount = offererTransport.Candidates.Count;
            var answererCandidateCount = answererTransport.Candidates.Count;

            Assert.Equal(PeerState.Offering, StateOf(offerer, answererId));
            Assert.Equal(PeerState.Answering, StateOf(answerer, offererId));

            var offererGeneration = offererTransport.LatestGeneration(answererId);
            offerer.OnLocalSdp(
                answererId,
                offererGeneration,
                "offer",
                $"{epoch}_OFFER_{offererId}_TO_{answererId}",
                NowMs);

            var answererGeneration = answererTransport.LatestGeneration(offererId);
            offerer.OnLocalCandidate(
                answererId,
                offererGeneration,
                $"{epoch}_CANDIDATE_{offererId}_TO_{answererId}",
                NowMs);
            offerer.OnLocalCandidate(answererId, offererGeneration, string.Empty, NowMs);

            answerer.OnLocalSdp(
                offererId,
                answererGeneration,
                "answer",
                $"{epoch}_ANSWER_{answererId}_TO_{offererId}",
                NowMs);
            answerer.OnLocalCandidate(
                offererId,
                answererGeneration,
                $"{epoch}_CANDIDATE_{answererId}_TO_{offererId}",
                NowMs);
            answerer.OnLocalCandidate(offererId, answererGeneration, string.Empty, NowMs);

            offerer.OnPeerConnected(answererId, offererGeneration);
            answerer.OnPeerConnected(offererId, answererGeneration);

            Assert.Equal(PeerState.Established, StateOf(offerer, answererId));
            Assert.Equal(PeerState.Established, StateOf(answerer, offererId));
            Assert.Equal(answererCandidateCount + 2, answererTransport.Candidates.Count);
            Assert.Equal((offererId, string.Empty), answererTransport.Candidates[^1]);
            Assert.Equal(offererCandidateCount + 2, offererTransport.Candidates.Count);
            Assert.Equal((answererId, string.Empty), offererTransport.Candidates[^1]);
        }
    }

    private static void AssertInitialMesh(Mesh mesh, int clientCount)
    {
        // Ten clients form 45 links and therefore 90 independently fenced native peer objects.
        Assert.Equal(90, mesh.Transports.Values.Sum(transport => transport.Added.Count));
        Assert.Equal(45, mesh.Senders.Values.Sum(sender =>
            sender.Sent.Count(message => message.Type == SignalMsgType.Offer)));
        Assert.Equal(45, mesh.Senders.Values.Sum(sender =>
            sender.Sent.Count(message => message.Type == SignalMsgType.Answer)));
        Assert.Equal(180, mesh.Senders.Values.Sum(sender =>
            sender.Sent.Count(message => message.Type == SignalMsgType.Candidate)));

        foreach (var localClientId in mesh.ClientIds)
        {
            var transport = mesh.Transports[localClientId];
            Assert.Equal(clientCount - 1, transport.Added.Count);
            Assert.Equal(clientCount - 1, transport.RemoteSdp.Count);
            Assert.Equal((clientCount - 1) * 2, transport.Candidates.Count);
            Assert.Equal(
                clientCount - 1,
                transport.Candidates.Count(item => item.Candidate.Length == 0));

            foreach (var remoteClientId in mesh.ClientIds.Where(id => id != localClientId))
            {
                var addIndex = transport.Added.IndexOf(remoteClientId);
                Assert.True(addIndex >= 0);
                Assert.Equal(localClientId < remoteClientId, transport.AddedOfferer[addIndex]);
                Assert.Equal(
                    PeerState.Established,
                    StateOf(mesh.Managers[localClientId], remoteClientId));
            }

            var diagnostics = mesh.Managers[localClientId].GetDiagnosticsSnapshot();
            Assert.Equal(clientCount - 1, diagnostics.KnownPeers);
            Assert.Equal(clientCount - 1, diagnostics.CompatiblePeers);
            Assert.Equal(0, diagnostics.NegotiatingPeers);
            Assert.Equal(clientCount - 1, diagnostics.EstablishedPeers);
            Assert.Equal((clientCount - 1) * 2, diagnostics.LocalCandidatesAttempted);
            Assert.Equal((clientCount - 1) * 2, diagnostics.RemoteCandidatesReceived);
            Assert.Equal((clientCount - 1) * 2, diagnostics.RemoteCandidatesForwarded);
            Assert.Equal(0, diagnostics.RejectedCandidates);
        }
    }

    private static void AssertStableLinks(
        Mesh mesh,
        IReadOnlyDictionary<(int Local, int Remote), int> stableGenerations)
    {
        foreach (var pair in stableGenerations)
        {
            Assert.Equal(
                pair.Value,
                mesh.Transports[pair.Key.Local].LatestGeneration(pair.Key.Remote));
            Assert.Equal(
                PeerState.Established,
                StateOf(mesh.Managers[pair.Key.Local], pair.Key.Remote));
        }
    }

    private static void AssertOldGenerationsAreFenced(
        Mesh mesh,
        IReadOnlyDictionary<(int Local, int Remote), int> oldGenerations)
    {
        // Delayed callbacks from every old native generation must be inert on both sides of all
        // nine rebuilt links. They may not signal, restart, remove, or demote the fresh sessions.
        foreach (var pair in oldGenerations)
        {
            var manager = mesh.Managers[pair.Key.Local];
            var transport = mesh.Transports[pair.Key.Local];
            var sender = mesh.Senders[pair.Key.Local];
            var currentGeneration = transport.LatestGeneration(pair.Key.Remote);
            var sentBefore = sender.Sent.Count;
            var addedBefore = transport.Added.Count;
            var removedBefore = transport.Removed.Count;
            var restartsBefore = transport.IceRestarts.Count;

            Assert.NotEqual(pair.Value, currentGeneration);
            manager.OnLocalSdp(
                pair.Key.Remote,
                pair.Value,
                "offer",
                "STALE_OFFER",
                mesh.NowMs);
            manager.OnLocalCandidate(
                pair.Key.Remote,
                pair.Value,
                "STALE_CANDIDATE",
                mesh.NowMs);
            manager.OnPeerConnected(pair.Key.Remote, pair.Value);
            manager.OnPeerConnectionLost(pair.Key.Remote, pair.Value, mesh.NowMs);

            Assert.Equal(sentBefore, sender.Sent.Count);
            Assert.Equal(addedBefore, transport.Added.Count);
            Assert.Equal(removedBefore, transport.Removed.Count);
            Assert.Equal(restartsBefore, transport.IceRestarts.Count);
            Assert.Equal(PeerState.Established, StateOf(manager, pair.Key.Remote));
        }
    }

    private static PeerState StateOf(PeerSessionManager manager, int clientId)
    {
        Assert.True(manager.TryGetPeerState(clientId, out var state));
        return state;
    }
}
