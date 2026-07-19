// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"encoding/json"
	"sync"
	"time"
)

const (
	controlQueueCapacity = 1024
	ingressQueueCapacity = 512
	ingressPeerCapacity  = 32
)

type controlEvent struct {
	Kind       string `json:"kind"`
	PeerID     string `json:"peer_id,omitempty"`
	Generation uint32 `json:"generation,omitempty"`
	SDPType    string `json:"sdp_type,omitempty"`
	SDP        string `json:"sdp,omitempty"`
	// Candidate is a pointer so an explicit end-of-candidates marker is encoded
	// as "candidate":"" instead of being lost to omitempty.
	Candidate *string `json:"candidate,omitempty"`
	State     string  `json:"state,omitempty"`
	Message   string  `json:"message,omitempty"`

	CandidatePairID          string  `json:"candidate_pair_id,omitempty"`
	CandidateState           string  `json:"candidate_state,omitempty"`
	LocalCandidateType       string  `json:"local_candidate_type,omitempty"`
	RemoteCandidateType      string  `json:"remote_candidate_type,omitempty"`
	Relay                    bool    `json:"relay,omitempty"`
	BandwidthEstimateValid   bool    `json:"bandwidth_estimate_valid,omitempty"`
	AvailableOutgoingBitrate float64 `json:"available_outgoing_bitrate,omitempty"`
	AvailableIncomingBitrate float64 `json:"available_incoming_bitrate,omitempty"`
	CurrentRTTMS             float64 `json:"current_rtt_ms,omitempty"`
	RemotePacketsReceived    uint64  `json:"remote_packets_received,omitempty"`
	RemotePacketsLost        int64   `json:"remote_packets_lost,omitempty"`
	RemoteFractionLost       float64 `json:"remote_fraction_lost,omitempty"`
	RemoteReportRTTMS        float64 `json:"remote_report_rtt_ms,omitempty"`
	RemoteRTTMeasurements    uint64  `json:"remote_rtt_measurements,omitempty"`
}

func (e controlEvent) encode() []byte {
	b, err := json.Marshal(e)
	if err != nil {
		return []byte(`{"kind":"error","message":"control-event-json"}`)
	}
	return b
}

type controlQueue struct {
	mu      sync.Mutex
	nextID  uint64
	events  []queuedControl
	dropped uint64
}

type queuedControl struct {
	id         uint64
	kind       string
	peerID     string
	generation uint32
	data       []byte
}

func refreshableControlKind(kind string) bool {
	return kind == "stats" || kind == "state"
}

func (q *controlQueue) push(event controlEvent) {
	encoded := event.encode()
	q.mu.Lock()
	defer q.mu.Unlock()

	// Only the newest refreshable snapshot for a peer/generation is useful.
	// Replacing it in place prevents stats from starving signaling events.
	if refreshableControlKind(event.Kind) {
		for i := len(q.events) - 1; i >= 0; i-- {
			queued := &q.events[i]
			if queued.kind == event.Kind && queued.peerID == event.PeerID && queued.generation == event.Generation {
				queued.id = q.newIDLocked()
				queued.data = encoded
				return
			}
		}
	}

	if len(q.events) >= controlQueueCapacity {
		// Preserve SDP/candidates/errors whenever a refreshable event can be
		// discarded. A refreshable event never evicts signaling.
		evict := -1
		for i, candidate := range q.events {
			if refreshableControlKind(candidate.kind) {
				evict = i
				break
			}
		}
		if evict < 0 {
			if refreshableControlKind(event.Kind) {
				q.dropped++
				return
			}
			// The queue is entirely critical and must remain bounded. Dropping
			// the oldest item makes forward progress; this condition means the
			// consumer has stopped draining for an extended period.
			evict = 0
		}
		copy(q.events[evict:], q.events[evict+1:])
		q.events[len(q.events)-1] = queuedControl{}
		q.events = q.events[:len(q.events)-1]
		q.dropped++
	}
	q.events = append(q.events, queuedControl{
		id: q.newIDLocked(), kind: event.Kind, peerID: event.PeerID,
		generation: event.Generation, data: encoded,
	})
}

func (q *controlQueue) newIDLocked() uint64 {
	q.nextID++
	if q.nextID == 0 {
		q.nextID++
	}
	return q.nextID
}

func (q *controlQueue) peek() (uint64, []byte) {
	q.mu.Lock()
	defer q.mu.Unlock()
	return q.peekLocked()
}

func (q *controlQueue) peekLocked() (uint64, []byte) {
	if len(q.events) == 0 {
		return 0, nil
	}
	return q.events[0].id, q.events[0].data
}

func (q *controlQueue) pop(id uint64) bool {
	q.mu.Lock()
	defer q.mu.Unlock()
	return q.popLocked(id)
}

func (q *controlQueue) popLocked(id uint64) bool {
	if len(q.events) == 0 || q.events[0].id != id {
		return false
	}
	copy(q.events, q.events[1:])
	q.events[len(q.events)-1] = queuedControl{}
	q.events = q.events[:len(q.events)-1]
	return true
}

func (q *controlQueue) removePeer(peerID string) {
	q.mu.Lock()
	defer q.mu.Unlock()
	events := q.events[:0]
	for _, event := range q.events {
		if event.peerID != peerID {
			events = append(events, event)
		}
	}
	clear(q.events[len(events):])
	q.events = events
}

type inboundRTP struct {
	queueID    uint64
	peerID     string
	generation uint32
	sequence   uint16
	timestamp  uint32
	arrival    time.Time
	payload    []byte
}

type rtpQueue struct {
	mu       sync.Mutex
	nextID   uint64
	queues   map[string][]inboundRTP
	ready    []string
	queued   int
	overflow uint64
}

func newRTPQueue() *rtpQueue {
	return &rtpQueue{queues: make(map[string][]inboundRTP)}
}

func (q *rtpQueue) push(packet inboundRTP) {
	q.mu.Lock()
	defer q.mu.Unlock()
	q.nextID++
	if q.nextID == 0 {
		q.nextID++
	}
	packet.queueID = q.nextID
	peerQueue := q.queues[packet.peerID]
	if len(peerQueue) >= ingressPeerCapacity {
		peerQueue[0] = inboundRTP{}
		peerQueue = peerQueue[1:]
		q.queued--
		q.overflow++
		q.queues[packet.peerID] = peerQueue
	}
	if q.queued >= ingressQueueCapacity {
		q.dropOldestReadyLocked()
	}
	// A global eviction may have selected this peer, so reload its queue
	// instead of restoring a stale slice and resurrecting the dropped packet.
	peerQueue = q.queues[packet.peerID]
	if len(peerQueue) == 0 {
		q.ready = append(q.ready, packet.peerID)
	}
	peerQueue = append(peerQueue, packet)
	q.queues[packet.peerID] = peerQueue
	q.queued++
}

func (q *rtpQueue) dropOldestReadyLocked() {
	for len(q.ready) > 0 {
		peerID := q.ready[0]
		q.ready = q.ready[1:]
		peerQueue := q.queues[peerID]
		if len(peerQueue) == 0 {
			delete(q.queues, peerID)
			continue
		}
		peerQueue[0] = inboundRTP{}
		peerQueue = peerQueue[1:]
		q.queued--
		q.overflow++
		if len(peerQueue) == 0 {
			delete(q.queues, peerID)
		} else {
			q.queues[peerID] = peerQueue
			q.ready = append(q.ready, peerID)
		}
		return
	}
}

func (q *rtpQueue) peek() (inboundRTP, uint64, bool) {
	q.mu.Lock()
	defer q.mu.Unlock()
	return q.peekLocked()
}

func (q *rtpQueue) peekLocked() (inboundRTP, uint64, bool) {
	for len(q.ready) > 0 {
		peerQueue := q.queues[q.ready[0]]
		if len(peerQueue) > 0 {
			return peerQueue[0], q.overflow, true
		}
		delete(q.queues, q.ready[0])
		q.ready = q.ready[1:]
	}
	return inboundRTP{}, q.overflow, false
}

func (q *rtpQueue) pop(id uint64) bool {
	q.mu.Lock()
	defer q.mu.Unlock()
	return q.popLocked(id)
}

func (q *rtpQueue) popLocked(id uint64) bool {
	for len(q.ready) > 0 {
		peerID := q.ready[0]
		peerQueue := q.queues[peerID]
		if len(peerQueue) == 0 {
			delete(q.queues, peerID)
			q.ready = q.ready[1:]
			continue
		}
		if peerQueue[0].queueID != id {
			return false
		}
		q.ready = q.ready[1:]
		peerQueue[0] = inboundRTP{}
		peerQueue = peerQueue[1:]
		q.queued--
		if len(peerQueue) == 0 {
			delete(q.queues, peerID)
		} else {
			q.queues[peerID] = peerQueue
			q.ready = append(q.ready, peerID)
		}
		return true
	}
	return false
}

func (q *rtpQueue) removePeer(peerID string) {
	q.mu.Lock()
	defer q.mu.Unlock()
	q.queued -= len(q.queues[peerID])
	delete(q.queues, peerID)
	ready := q.ready[:0]
	for _, id := range q.ready {
		if id != peerID {
			ready = append(ready, id)
		}
	}
	clear(q.ready[len(ready):])
	q.ready = ready
}
