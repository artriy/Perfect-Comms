// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"testing"
	"time"
)

func queueOnlyEngineAndPeer(peerID string, generation uint32) (*engine, *peer) {
	e := &engine{peers: make(map[string]*peer), rtp: newRTPQueue()}
	p := &peer{
		engine: e, id: peerID, generation: generation, instance: 1,
		outbound: make(chan outboundFrame, outboundQueueCapacity), stop: make(chan struct{}),
	}
	p.active.Store(true)
	e.peers[peerID] = p
	return e, p
}

func TestAdvanceEpochPurgesQueuedAudioAndRejectsStaleSends(t *testing.T) {
	e, p := queueOnlyEngineAndPeer("peer", 1)
	for _, frame := range []struct {
		epoch    uint64
		sequence uint64
	}{{1, 1}, {3, 2}, {1, 3}} {
		result := e.sendOpus([]byte{byte(frame.sequence)}, frame.epoch, frame.sequence)
		if result.enqueued != 1 {
			t.Fatalf("send epoch %d sequence %d = %+v", frame.epoch, frame.sequence, result)
		}
	}
	if !e.advanceEpoch(2, 100*time.Millisecond) {
		t.Fatal("epoch advance timed out")
	}
	if got := len(p.outbound); got != 1 {
		t.Fatalf("queued frames after purge = %d, want 1", got)
	}
	kept := <-p.outbound
	if kept.epoch != 3 || kept.mediaSequence != 2 {
		t.Fatalf("kept frame = %+v, want epoch 3 sequence 2", kept)
	}

	stale := e.sendOpus([]byte{9}, 1, 4)
	if stale.stale != 1 || stale.attempted != 0 || stale.enqueued != 0 {
		t.Fatalf("stale send result = %+v", stale)
	}
	current := e.sendOpus([]byte{10}, 2, 5)
	if current.attempted != 1 || current.enqueued != 1 {
		t.Fatalf("current send result = %+v", current)
	}
	if got := e.counters.rtpTXStaleEpochDropped.Load(); got != 3 {
		t.Fatalf("stale drop counter = %d, want 3", got)
	}
}

func TestAdvanceEpochTimeoutDoesNotLeaveAWaiter(t *testing.T) {
	e, p := queueOnlyEngineAndPeer("peer", 1)
	p.outbound <- outboundFrame{epoch: 1}
	e.privacyMu.RLock()
	if e.advanceEpoch(2, 2*time.Millisecond) {
		t.Fatal("epoch advance unexpectedly acquired a held write lock")
	}
	e.privacyMu.RUnlock()
	if !e.advanceEpoch(2, 100*time.Millisecond) {
		t.Fatal("second epoch advance was blocked by a leaked waiter")
	}
	if len(p.outbound) != 0 {
		t.Fatal("stale frame was not purged after retry")
	}
}

func TestPeerInstanceFencesControlAndRTPAcrossSameGeneration(t *testing.T) {
	e, old := queueOnlyEngineAndPeer("peer", 8)
	old.instance = 11
	old.emit(controlEvent{Kind: "state", PeerID: old.id, Generation: old.generation, State: "old"})
	e.enqueuePeerRTP(old, testRTP(old.id, 1))

	newPeer := &peer{
		engine: e, id: "peer", generation: 8, instance: 12,
		outbound: make(chan outboundFrame, outboundQueueCapacity), stop: make(chan struct{}),
	}
	newPeer.active.Store(true)
	e.mu.Lock()
	old.active.Store(false)
	e.control.removePeer(old.id)
	e.rtp.removePeer(old.id)
	e.peers[old.id] = newPeer
	e.mu.Unlock()

	if old.emit(controlEvent{Kind: "state", PeerID: old.id, Generation: 8, State: "late-old"}) {
		t.Fatal("old peer emitted control after replacement")
	}
	if e.enqueuePeerRTP(old, testRTP(old.id, 2)) {
		t.Fatal("old peer enqueued RTP after replacement")
	}
	if !newPeer.emit(controlEvent{Kind: "state", PeerID: newPeer.id, Generation: 8, State: "new"}) {
		t.Fatal("new peer control was rejected")
	}
	if !e.enqueuePeerRTP(newPeer, testRTP(newPeer.id, 3)) {
		t.Fatal("new peer RTP was rejected")
	}

	_, data := e.control.peek()
	event := decodeControlForTest(t, data)
	if event.State != "new" {
		t.Fatalf("control state = %q, want new", event.State)
	}
	packet, _, ok := e.rtp.peek()
	if !ok || packet.sequence != 3 {
		t.Fatalf("queued RTP = %+v, present=%v", packet, ok)
	}
}

func TestCandidateBufferIsOrderedAndFailsClosedAtBound(t *testing.T) {
	e, p := queueOnlyEngineAndPeer("peer", 2)
	for i := 0; i < maxPendingCandidates; i++ {
		candidate := string(rune('a' + (i % 26)))
		if err := p.addICECandidate(candidate); err != nil {
			t.Fatalf("candidate %d: %v", i, err)
		}
	}
	if err := p.addICECandidate(""); err == nil {
		t.Fatal("overflowing remote candidate buffer did not fail closed")
	}
	if len(p.pendingICE) != maxPendingCandidates {
		t.Fatalf("pending candidates = %d, want %d", len(p.pendingICE), maxPendingCandidates)
	}
	for i, candidate := range p.pendingICE {
		want := string(rune('a' + (i % 26)))
		if candidate.Candidate != want {
			t.Fatalf("candidate %d = %q, want %q", i, candidate.Candidate, want)
		}
	}

	// Prove SDP is emitted before the buffered trickle candidates and EOC.
	p.pendingICE = nil
	p.holdLocalCandidates()
	p.emitOrBufferCandidate("candidate:host")
	p.emitOrBufferCandidate("")
	p.emit(controlEvent{Kind: "sdp", PeerID: p.id, Generation: p.generation, SDPType: "offer", SDP: "v=0"})
	p.releaseLocalCandidates()
	wantKinds := []string{"sdp", "candidate", "candidate"}
	for i, want := range wantKinds {
		id, data := e.control.peek()
		if data == nil {
			t.Fatalf("missing control event %d", i)
		}
		event := decodeControlForTest(t, data)
		if event.Kind != want {
			t.Fatalf("event %d kind = %q, want %q", i, event.Kind, want)
		}
		if i == 2 && (event.Candidate == nil || *event.Candidate != "") {
			t.Fatalf("event %d is not explicit EOC: %+v", i, event)
		}
		e.control.pop(id)
	}
}
