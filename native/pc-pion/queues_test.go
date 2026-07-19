// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"encoding/json"
	"fmt"
	"testing"
	"time"
)

func decodeControlForTest(t *testing.T, data []byte) controlEvent {
	t.Helper()
	var event controlEvent
	if err := json.Unmarshal(data, &event); err != nil {
		t.Fatalf("decode control event: %v", err)
	}
	return event
}

func TestEndOfCandidatesIsEncodedExplicitly(t *testing.T) {
	encoded := candidateControlEvent("peer", 7, "").encode()
	var raw map[string]any
	if err := json.Unmarshal(encoded, &raw); err != nil {
		t.Fatalf("decode candidate JSON: %v", err)
	}
	value, present := raw["candidate"]
	if !present || value != "" {
		t.Fatalf("end-of-candidates was not explicit: %s", encoded)
	}
}

func TestControlQueueCoalescesRefreshableEvents(t *testing.T) {
	var q controlQueue
	for i := 0; i < 100; i++ {
		q.push(controlEvent{
			Kind: "stats", PeerID: "peer", Generation: 3,
			AvailableOutgoingBitrate: float64(i),
		})
	}
	if got := len(q.events); got != 1 {
		t.Fatalf("refreshable event count = %d, want 1", got)
	}
	_, data := q.peek()
	event := decodeControlForTest(t, data)
	if event.AvailableOutgoingBitrate != 99 {
		t.Fatalf("latest bitrate = %v, want 99", event.AvailableOutgoingBitrate)
	}
}

func TestControlQueuePriorityAndTokenFencing(t *testing.T) {
	var q controlQueue
	for i := 0; i < controlQueueCapacity; i++ {
		q.push(controlEvent{Kind: "error", PeerID: fmt.Sprintf("p-%d", i), Message: "critical"})
	}
	oldID, _ := q.peek()
	q.push(controlEvent{Kind: "stats", PeerID: "refreshable"})
	if got := len(q.events); got != controlQueueCapacity {
		t.Fatalf("queue length = %d, want %d", got, controlQueueCapacity)
	}
	newID, _ := q.peek()
	if newID != oldID {
		t.Fatal("refreshable event evicted a critical event")
	}
	if q.dropped != 1 {
		t.Fatalf("dropped = %d, want 1", q.dropped)
	}

	q.push(controlEvent{Kind: "sdp", PeerID: "new", SDPType: "offer", SDP: "v=0"})
	if q.pop(oldID) {
		t.Fatal("stale poll token removed a newer head event")
	}
	currentID, _ := q.peek()
	if currentID == 0 || !q.pop(currentID) {
		t.Fatal("current poll token did not remove the head event")
	}
}

func TestControlQueueRemovesOnlyTargetPeer(t *testing.T) {
	var q controlQueue
	q.push(controlEvent{Kind: "sdp", PeerID: "old", Generation: 1})
	q.push(controlEvent{Kind: "candidate", PeerID: "keep", Generation: 2})
	q.push(controlEvent{Kind: "error", PeerID: "old", Generation: 1})
	q.removePeer("old")
	if len(q.events) != 1 || q.events[0].peerID != "keep" {
		t.Fatalf("remaining events = %+v", q.events)
	}
}

func testRTP(peer string, sequence uint16) inboundRTP {
	return inboundRTP{
		peerID: peer, generation: 1, sequence: sequence,
		timestamp: uint32(sequence) * 960, arrival: time.Now(), payload: []byte{byte(sequence)},
	}
}

func popRTPForTest(t *testing.T, q *rtpQueue) inboundRTP {
	t.Helper()
	packet, _, ok := q.peek()
	if !ok {
		t.Fatal("RTP queue unexpectedly empty")
	}
	if !q.pop(packet.queueID) {
		t.Fatal("RTP queue rejected its current token")
	}
	return packet
}

func TestRTPQueueRoundRobinFairness(t *testing.T) {
	q := newRTPQueue()
	q.push(testRTP("a", 1))
	q.push(testRTP("a", 2))
	q.push(testRTP("b", 10))
	q.push(testRTP("b", 11))
	wantPeers := []string{"a", "b", "a", "b"}
	for i, want := range wantPeers {
		if got := popRTPForTest(t, q).peerID; got != want {
			t.Fatalf("pop %d peer = %q, want %q", i, got, want)
		}
	}
}

func TestRTPQueuePerPeerBoundKeepsNewest(t *testing.T) {
	q := newRTPQueue()
	for i := 0; i < ingressPeerCapacity+5; i++ {
		q.push(testRTP("peer", uint16(i)))
	}
	packet, overflow, ok := q.peek()
	if !ok {
		t.Fatal("queue unexpectedly empty")
	}
	if packet.sequence != 5 || overflow != 5 {
		t.Fatalf("first sequence/overflow = %d/%d, want 5/5", packet.sequence, overflow)
	}
	if q.queued != ingressPeerCapacity || len(q.queues["peer"]) != ingressPeerCapacity {
		t.Fatalf("queue counts = %d/%d, want %d", q.queued, len(q.queues["peer"]), ingressPeerCapacity)
	}
}

func TestRTPQueueGlobalEvictionDoesNotResurrectHead(t *testing.T) {
	q := newRTPQueue()
	q.push(testRTP("head", 1))
	for i := 0; i < ingressQueueCapacity-1; i++ {
		q.push(testRTP(fmt.Sprintf("peer-%d", i), uint16(i)))
	}
	q.push(testRTP("head", 2))
	if q.queued != ingressQueueCapacity || q.overflow != 1 {
		t.Fatalf("queue count/overflow = %d/%d, want %d/1", q.queued, q.overflow, ingressQueueCapacity)
	}
	headQueue := q.queues["head"]
	if len(headQueue) != 1 || headQueue[0].sequence != 2 {
		t.Fatalf("head queue after eviction = %+v, want only sequence 2", headQueue)
	}

	seenHead := 0
	for q.queued > 0 {
		packet := popRTPForTest(t, q)
		if packet.peerID == "head" {
			seenHead++
			if packet.sequence != 2 {
				t.Fatalf("resurrected sequence %d", packet.sequence)
			}
		}
	}
	if seenHead != 1 {
		t.Fatalf("head packets seen = %d, want 1", seenHead)
	}
}

func TestRTPQueueStaleTokenCannotPopNewHead(t *testing.T) {
	q := newRTPQueue()
	q.push(testRTP("old", 1))
	old, _, _ := q.peek()
	for i := 0; i < ingressQueueCapacity-1; i++ {
		q.push(testRTP(fmt.Sprintf("peer-%d", i), uint16(i)))
	}
	q.push(testRTP("new", 2))
	if q.pop(old.queueID) {
		t.Fatal("stale RTP token removed a newer head")
	}
	if q.queued != ingressQueueCapacity {
		t.Fatalf("queued = %d, want %d", q.queued, ingressQueueCapacity)
	}
}
