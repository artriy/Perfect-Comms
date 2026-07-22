// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"encoding/json"
	"fmt"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/pion/webrtc/v4"
)

const (
	tenClientMeshSize          = 10
	tenClientInitialGeneration = uint32(41)
	tenClientChurnGeneration   = uint32(42)
	tenClientTestBudget        = 75 * time.Second
)

type meshSignalEvent struct {
	kind            string
	sdpType         string
	generation      uint32
	endOfCandidates bool
	detail          string
}

type meshSignalTrace struct {
	events               []meshSignalEvent
	offers               []string
	offerGenerations     []uint32
	answers              []string
	answerGenerations    []uint32
	descriptionsByGen    map[uint32]int
	candidatesByGen      map[uint32]int
	nonemptyByGen        map[uint32]int
	endOfCandidatesByGen map[uint32]int
}

func (trace *meshSignalTrace) recordDescription(event controlEvent) {
	if trace.descriptionsByGen == nil {
		trace.descriptionsByGen = make(map[uint32]int)
	}
	trace.events = append(trace.events, meshSignalEvent{
		kind: event.Kind, sdpType: event.SDPType, generation: event.Generation,
		detail: iceUfrag(event.SDP),
	})
	trace.descriptionsByGen[event.Generation]++
	switch event.SDPType {
	case "offer":
		trace.offers = append(trace.offers, event.SDP)
		trace.offerGenerations = append(trace.offerGenerations, event.Generation)
	case "answer":
		trace.answers = append(trace.answers, event.SDP)
		trace.answerGenerations = append(trace.answerGenerations, event.Generation)
	}
}

func (trace *meshSignalTrace) recordCandidate(event controlEvent) error {
	if event.Candidate == nil {
		return errorsForLoopback("candidate event omitted candidate field")
	}
	if trace.descriptionsByGen[event.Generation] == 0 {
		return fmt.Errorf("candidate preceded SDP for generation %d", event.Generation)
	}
	if trace.candidatesByGen == nil {
		trace.candidatesByGen = make(map[uint32]int)
		trace.nonemptyByGen = make(map[uint32]int)
		trace.endOfCandidatesByGen = make(map[uint32]int)
	}
	trace.events = append(trace.events, meshSignalEvent{
		kind: event.Kind, generation: event.Generation, endOfCandidates: *event.Candidate == "",
		detail: *event.Candidate,
	})
	trace.candidatesByGen[event.Generation]++
	if *event.Candidate == "" {
		trace.endOfCandidatesByGen[event.Generation]++
	} else {
		trace.nonemptyByGen[event.Generation]++
	}
	return nil
}

func latestSDPForGeneration(sdps []string, generations []uint32, generation uint32) string {
	for i := len(sdps) - 1; i >= 0; i-- {
		if generations[i] == generation {
			return sdps[i]
		}
	}
	return ""
}

type meshTransportSnapshot struct {
	txAttempts      uint64
	txOK            uint64
	txErrors        uint64
	txQueueDropped  uint64
	txStaleDropped  uint64
	txWriteTimeouts uint64
	rxPackets       uint64
	rxBytes         uint64
}

func snapshotMeshTransport(e *engine) meshTransportSnapshot {
	return meshTransportSnapshot{
		txAttempts:      e.counters.rtpTXAttempts.Load(),
		txOK:            e.counters.rtpTXOK.Load(),
		txErrors:        e.counters.rtpTXErrors.Load(),
		txQueueDropped:  e.counters.rtpTXQueueDropped.Load(),
		txStaleDropped:  e.counters.rtpTXStaleEpochDropped.Load(),
		txWriteTimeouts: e.counters.rtpTXWriteTimeouts.Load(),
		rxPackets:       e.counters.rtpRXPackets.Load(),
		rxBytes:         e.counters.rtpRXBytes.Load(),
	}
}

type tenClientMesh struct {
	t                  *testing.T
	clients            []*engine
	allEngines         []*engine
	ids                []string
	indexByID          map[string]int
	traces             [tenClientMeshSize][tenClientMeshSize]meshSignalTrace
	generations        [tenClientMeshSize][tenClientMeshSize]uint32
	lastPackets        [tenClientMeshSize][tenClientMeshSize]inboundRTP
	lastMediaSequences [tenClientMeshSize][tenClientMeshSize]uint64
	lastPeers          [tenClientMeshSize][tenClientMeshSize]*peer
	haveLastPacket     [tenClientMeshSize][tenClientMeshSize]bool
	nextMediaSequence  uint64
	deadline           time.Time
}

func newTenClientMesh(t *testing.T) *tenClientMesh {
	t.Helper()
	mesh := &tenClientMesh{
		t:                 t,
		clients:           make([]*engine, tenClientMeshSize),
		ids:               make([]string, tenClientMeshSize),
		indexByID:         make(map[string]int, tenClientMeshSize),
		nextMediaSequence: 1,
		deadline:          time.Now().Add(tenClientTestBudget),
	}
	for i := 0; i < tenClientMeshSize; i++ {
		mesh.ids[i] = fmt.Sprintf("client-%02d", i)
		mesh.indexByID[mesh.ids[i]] = i
		client, err := newEngine()
		if err != nil {
			for _, created := range mesh.allEngines {
				created.close()
			}
			t.Fatalf("new client %d: %v", i, err)
		}
		if err = client.setICEServers(nil); err != nil {
			client.close()
			for _, created := range mesh.allEngines {
				created.close()
			}
			t.Fatalf("disable external ICE servers for client %d: %v", i, err)
		}
		mesh.clients[i] = client
		mesh.allEngines = append(mesh.allEngines, client)
	}
	for receiver := 0; receiver < tenClientMeshSize; receiver++ {
		for sender := 0; sender < tenClientMeshSize; sender++ {
			if receiver != sender {
				mesh.generations[receiver][sender] = tenClientInitialGeneration
			}
		}
	}
	t.Cleanup(func() {
		for i := len(mesh.allEngines) - 1; i >= 0; i-- {
			mesh.allEngines[i].close()
		}
	})
	return mesh
}

func (mesh *tenClientMesh) addInitialPeers() {
	mesh.t.Helper()
	// Install every answerer first, so no offer can race a missing remote peer.
	for lower := 0; lower < tenClientMeshSize; lower++ {
		for higher := lower + 1; higher < tenClientMeshSize; higher++ {
			if err := mesh.clients[higher].addPeer(
				mesh.ids[lower], false, false, tenClientInitialGeneration, 0,
			); err != nil {
				mesh.t.Fatalf("add answerer %s -> %s: %v", mesh.ids[higher], mesh.ids[lower], err)
			}
		}
	}
	for lower := 0; lower < tenClientMeshSize; lower++ {
		for higher := lower + 1; higher < tenClientMeshSize; higher++ {
			if err := mesh.clients[lower].addPeer(
				mesh.ids[higher], true, false, tenClientInitialGeneration, 0,
			); err != nil {
				mesh.t.Fatalf("add offerer %s -> %s: %v", mesh.ids[lower], mesh.ids[higher], err)
			}
		}
	}
	mesh.requirePeerObjectCount(90)
}

func (mesh *tenClientMesh) requirePeerObjectCount(want int) {
	mesh.t.Helper()
	total := 0
	seen := make(map[*peer]struct{}, want)
	for clientIndex, client := range mesh.clients {
		client.mu.RLock()
		count := len(client.peers)
		for _, current := range client.peers {
			seen[current] = struct{}{}
		}
		client.mu.RUnlock()
		if count != tenClientMeshSize-1 {
			mesh.t.Fatalf("client %d peer count = %d, want %d", clientIndex, count, tenClientMeshSize-1)
		}
		total += count
	}
	if total != want || len(seen) != want {
		mesh.t.Fatalf("peer objects = %d total/%d unique, want %d", total, len(seen), want)
	}
}

func (mesh *tenClientMesh) signalSnapshot() [tenClientMeshSize][tenClientMeshSize]int {
	var snapshot [tenClientMeshSize][tenClientMeshSize]int
	for source := 0; source < tenClientMeshSize; source++ {
		for target := 0; target < tenClientMeshSize; target++ {
			snapshot[source][target] = len(mesh.traces[source][target].events)
		}
	}
	return snapshot
}

func (mesh *tenClientMesh) requireNegotiationDirection(
	source int,
	target int,
	baseline int,
	generation uint32,
	wantSDPType string,
) {
	mesh.t.Helper()
	trace := &mesh.traces[source][target]
	if baseline < 0 || baseline > len(trace.events) {
		mesh.t.Fatalf(
			"signaling baseline %s -> %s = %d, events = %d",
			mesh.ids[source], mesh.ids[target], baseline, len(trace.events),
		)
	}
	events := trace.events[baseline:]
	descriptions := 0
	nonemptyCandidates := 0
	endOfCandidates := 0
	for index, event := range events {
		if event.generation != generation {
			mesh.t.Fatalf(
				"negotiation %s -> %s event %d generation = %d, want %d",
				mesh.ids[source], mesh.ids[target], index, event.generation, generation,
			)
		}
		switch event.kind {
		case "sdp":
			descriptions++
			if index != 0 || event.sdpType != wantSDPType {
				mesh.t.Fatalf(
					"negotiation %s -> %s event %d = sdp:%s, want event 0 sdp:%s; events=%+v",
					mesh.ids[source], mesh.ids[target], index, event.sdpType, wantSDPType, events,
				)
			}
		case "candidate":
			if descriptions != 1 {
				mesh.t.Fatalf(
					"negotiation %s -> %s candidate event %d preceded its SDP; events=%+v",
					mesh.ids[source], mesh.ids[target], index, events,
				)
			}
			if event.endOfCandidates {
				endOfCandidates++
			} else {
				if endOfCandidates != 0 {
					mesh.t.Fatalf(
						"negotiation %s -> %s candidate event %d ufrag=%q followed end-of-candidates for sdp ufrag=%q",
						mesh.ids[source], mesh.ids[target], index, event.detail, events[0].detail,
					)
				}
				nonemptyCandidates++
			}
		default:
			mesh.t.Fatalf(
				"negotiation %s -> %s recorded unexpected event %q",
				mesh.ids[source], mesh.ids[target], event.kind,
			)
		}
	}
	if descriptions != 1 || nonemptyCandidates == 0 || endOfCandidates == 0 {
		mesh.t.Fatalf(
			"negotiation %s -> %s signaling counts: sdp=%d nonempty_candidates=%d eoc=%d; want 1/>0/>0; events=%+v",
			mesh.ids[source], mesh.ids[target], descriptions, nonemptyCandidates, endOfCandidates, events,
		)
	}
}

func (mesh *tenClientMesh) requirePairNegotiation(
	lower int,
	higher int,
	baseline [tenClientMeshSize][tenClientMeshSize]int,
	generation uint32,
) {
	mesh.t.Helper()
	mesh.requireNegotiationDirection(
		lower, higher, baseline[lower][higher], generation, "offer",
	)
	mesh.requireNegotiationDirection(
		higher, lower, baseline[higher][lower], generation, "answer",
	)
}

func (mesh *tenClientMesh) requireAllPairNegotiations(
	baseline [tenClientMeshSize][tenClientMeshSize]int,
	generation uint32,
) {
	mesh.t.Helper()
	for lower := 0; lower < tenClientMeshSize; lower++ {
		for higher := lower + 1; higher < tenClientMeshSize; higher++ {
			mesh.requirePairNegotiation(lower, higher, baseline, generation)
		}
	}
}

func (mesh *tenClientMesh) pumpSignaling() (int, error) {
	processed := 0
	for source, client := range mesh.clients {
		for {
			queueID, data := client.control.peek()
			if data == nil {
				break
			}
			var event controlEvent
			if err := json.Unmarshal(data, &event); err != nil {
				return processed, fmt.Errorf("decode signaling from %s: %w", mesh.ids[source], err)
			}
			if !client.control.pop(queueID) {
				continue
			}
			processed++
			target, known := mesh.indexByID[event.PeerID]
			if !known {
				if event.Kind == "error" {
					return processed, fmt.Errorf("engine error from %s: %s", mesh.ids[source], event.Message)
				}
				return processed, fmt.Errorf("%s event from %s named unknown peer %q", event.Kind, mesh.ids[source], event.PeerID)
			}
			trace := &mesh.traces[source][target]
			switch event.Kind {
			case "sdp", "candidate", "error":
				targetPeer := mesh.clients[target].peer(mesh.ids[source])
				if targetPeer == nil {
					return processed, fmt.Errorf("%s event %s -> %s has no target peer", event.Kind, mesh.ids[source], mesh.ids[target])
				}
				if targetPeer.generation != event.Generation {
					return processed, fmt.Errorf(
						"%s event %s -> %s generation = %d, target = %d",
						event.Kind, mesh.ids[source], mesh.ids[target], event.Generation, targetPeer.generation,
					)
				}
				switch event.Kind {
				case "sdp":
					if event.SDPType != "offer" && event.SDPType != "answer" {
						return processed, fmt.Errorf("invalid SDP type %q from %s", event.SDPType, mesh.ids[source])
					}
					trace.recordDescription(event)
					if err := targetPeer.setRemoteSDP(event.SDPType, event.SDP); err != nil {
						return processed, fmt.Errorf(
							"apply %s %s -> %s: %w", event.SDPType, mesh.ids[source], mesh.ids[target], err,
						)
					}
				case "candidate":
					if err := trace.recordCandidate(event); err != nil {
						return processed, fmt.Errorf("candidate %s -> %s: %w", mesh.ids[source], mesh.ids[target], err)
					}
					if err := targetPeer.addICECandidate(*event.Candidate); err != nil {
						return processed, fmt.Errorf("apply candidate %s -> %s: %w", mesh.ids[source], mesh.ids[target], err)
					}
				case "error":
					return processed, fmt.Errorf("peer error %s -> %s: %s", mesh.ids[source], mesh.ids[target], event.Message)
				}
			case "state", "ice-state", "path", "bandwidth", "stats":
				// Runtime state and telemetry are sampled separately after connection,
				// so these refreshable queue events can simply be drained.
			default:
				return processed, fmt.Errorf("unknown control event kind %q from %s", event.Kind, mesh.ids[source])
			}
		}
	}
	return processed, nil
}

func (mesh *tenClientMesh) phaseDeadline(timeout time.Duration) time.Time {
	deadline := time.Now().Add(timeout)
	if mesh.deadline.Before(deadline) {
		return mesh.deadline
	}
	return deadline
}

func (mesh *tenClientMesh) waitFor(label string, timeout time.Duration, condition func() bool) time.Duration {
	mesh.t.Helper()
	started := time.Now()
	deadline := mesh.phaseDeadline(timeout)
	for time.Now().Before(deadline) {
		processed, err := mesh.pumpSignaling()
		if err != nil {
			mesh.t.Fatalf("%s signaling: %v", label, err)
		}
		if condition() {
			return time.Since(started)
		}
		if failed := mesh.failedPeers(); len(failed) > 0 {
			mesh.t.Fatalf("%s failed peers: %s", label, strings.Join(failed, ", "))
		}
		if processed == 0 {
			time.Sleep(5 * time.Millisecond)
		}
	}
	connected, ready := mesh.connectedAndReadyCounts()
	mesh.t.Fatalf(
		"%s timed out after %s: connected=%d/90 media-ready=%d/90 missing-eoc=%s",
		label, time.Since(started).Round(time.Millisecond), connected, ready, strings.Join(mesh.missingEOC(), ","),
	)
	return 0
}

func (mesh *tenClientMesh) failedPeers() []string {
	var failed []string
	for source := 0; source < tenClientMeshSize; source++ {
		for target := 0; target < tenClientMeshSize; target++ {
			if source == target {
				continue
			}
			current := mesh.clients[source].peer(mesh.ids[target])
			if current == nil {
				failed = append(failed, fmt.Sprintf("%s->%s(missing)", mesh.ids[source], mesh.ids[target]))
				continue
			}
			if current.pc.ConnectionState() == webrtc.PeerConnectionStateFailed ||
				current.pc.ICEConnectionState() == webrtc.ICEConnectionStateFailed {
				failed = append(failed, fmt.Sprintf(
					"%s->%s(pc=%s ice=%s)", mesh.ids[source], mesh.ids[target],
					current.pc.ConnectionState(), current.pc.ICEConnectionState(),
				))
			}
		}
	}
	return failed
}

func (mesh *tenClientMesh) connectedAndReadyCounts() (int, int) {
	connected := 0
	ready := 0
	for source := 0; source < tenClientMeshSize; source++ {
		for target := 0; target < tenClientMeshSize; target++ {
			if source == target {
				continue
			}
			current := mesh.clients[source].peer(mesh.ids[target])
			if current == nil {
				continue
			}
			iceState := current.pc.ICEConnectionState()
			if current.pc.ConnectionState() == webrtc.PeerConnectionStateConnected &&
				(iceState == webrtc.ICEConnectionStateConnected || iceState == webrtc.ICEConnectionStateCompleted) {
				connected++
				if current.readyToSendRTP() {
					ready++
				}
			}
		}
	}
	return connected, ready
}

func (mesh *tenClientMesh) allConnectedAndReady() bool {
	connected, ready := mesh.connectedAndReadyCounts()
	return connected == tenClientMeshSize*(tenClientMeshSize-1) && ready == connected
}

func (mesh *tenClientMesh) allInitialEOC() bool {
	for source := 0; source < tenClientMeshSize; source++ {
		for target := 0; target < tenClientMeshSize; target++ {
			if source == target {
				continue
			}
			trace := &mesh.traces[source][target]
			if trace.nonemptyByGen[tenClientInitialGeneration] == 0 ||
				trace.endOfCandidatesByGen[tenClientInitialGeneration] == 0 {
				return false
			}
		}
	}
	return true
}

func (mesh *tenClientMesh) missingEOC() []string {
	var missing []string
	for source := 0; source < tenClientMeshSize; source++ {
		for target := 0; target < tenClientMeshSize; target++ {
			if source == target {
				continue
			}
			generation := mesh.generations[source][target]
			trace := &mesh.traces[source][target]
			if trace.endOfCandidatesByGen[generation] == 0 {
				missing = append(missing, fmt.Sprintf("%d>%d", source, target))
			}
		}
	}
	return missing
}

func (mesh *tenClientMesh) eocSnapshot() [tenClientMeshSize][tenClientMeshSize]int {
	var snapshot [tenClientMeshSize][tenClientMeshSize]int
	for source := 0; source < tenClientMeshSize; source++ {
		for target := 0; target < tenClientMeshSize; target++ {
			generation := mesh.generations[source][target]
			snapshot[source][target] = mesh.traces[source][target].endOfCandidatesByGen[generation]
		}
	}
	return snapshot
}

func (mesh *tenClientMesh) allEOCAdvanced(baseline [tenClientMeshSize][tenClientMeshSize]int) bool {
	for source := 0; source < tenClientMeshSize; source++ {
		for target := 0; target < tenClientMeshSize; target++ {
			if source == target {
				continue
			}
			generation := mesh.generations[source][target]
			if mesh.traces[source][target].endOfCandidatesByGen[generation] <= baseline[source][target] {
				return false
			}
		}
	}
	return true
}

func (mesh *tenClientMesh) allClientEOC(clientIndex int) bool {
	for other := 0; other < tenClientMeshSize; other++ {
		if other == clientIndex {
			continue
		}
		generation := mesh.generations[clientIndex][other]
		if mesh.traces[clientIndex][other].nonemptyByGen[generation] == 0 ||
			mesh.traces[clientIndex][other].endOfCandidatesByGen[generation] == 0 ||
			mesh.traces[other][clientIndex].nonemptyByGen[generation] == 0 ||
			mesh.traces[other][clientIndex].endOfCandidatesByGen[generation] == 0 {
			return false
		}
	}
	return true
}

func (mesh *tenClientMesh) selectedPairSummary() (map[string]int, error) {
	result := make(map[string]int)
	total := 0
	for source := 0; source < tenClientMeshSize; source++ {
		for target := 0; target < tenClientMeshSize; target++ {
			if source == target {
				continue
			}
			current := mesh.clients[source].peer(mesh.ids[target])
			if current == nil || current.sender.Transport() == nil || current.sender.Transport().ICETransport() == nil {
				return nil, fmt.Errorf("%s -> %s has no ICE transport", mesh.ids[source], mesh.ids[target])
			}
			pair, err := current.sender.Transport().ICETransport().GetSelectedCandidatePair()
			if err != nil {
				return nil, fmt.Errorf("selected pair %s -> %s: %w", mesh.ids[source], mesh.ids[target], err)
			}
			if pair == nil || pair.Local == nil || pair.Remote == nil {
				return nil, fmt.Errorf("%s -> %s has no selected candidate pair", mesh.ids[source], mesh.ids[target])
			}
			if pair.Local.Typ == webrtc.ICECandidateTypeRelay || pair.Remote.Typ == webrtc.ICECandidateTypeRelay {
				return nil, fmt.Errorf(
					"%s -> %s selected relay candidate pair %s -> %s in direct-only mesh",
					mesh.ids[source], mesh.ids[target], pair.Local.Typ, pair.Remote.Typ,
				)
			}
			key := pair.Local.Typ.String() + "->" + pair.Remote.Typ.String()
			result[key]++
			total++
		}
	}
	if total != tenClientMeshSize*(tenClientMeshSize-1) {
		return nil, fmt.Errorf("selected candidate pairs = %d, want %d", total, tenClientMeshSize*(tenClientMeshSize-1))
	}
	return result, nil
}

func formatPairSummary(summary map[string]int) string {
	keys := make([]string, 0, len(summary))
	for key := range summary {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	parts := make([]string, 0, len(keys))
	for _, key := range keys {
		parts = append(parts, fmt.Sprintf("%s=%d", key, summary[key]))
	}
	return strings.Join(parts, ", ")
}

func meshPayload(stage byte, sender, frame int) []byte {
	return []byte{0xf8, 0xff, stage, byte(sender), byte(frame), 0xa5}
}

func (mesh *tenClientMesh) drainOneMediaRound(
	stage byte,
	frame int,
	mediaSequence uint64,
) [tenClientMeshSize][tenClientMeshSize]inboundRTP {
	mesh.t.Helper()
	var packets [tenClientMeshSize][tenClientMeshSize]inboundRTP
	var received [tenClientMeshSize][tenClientMeshSize]bool
	want := tenClientMeshSize * (tenClientMeshSize - 1)
	got := 0
	deadline := mesh.phaseDeadline(10 * time.Second)
	for got < want && time.Now().Before(deadline) {
		processed, err := mesh.pumpSignaling()
		if err != nil {
			mesh.t.Fatalf("media stage %d frame %d signaling: %v", stage, frame, err)
		}
		for receiver, client := range mesh.clients {
			for {
				packet, _, ok := client.rtp.peek()
				if !ok {
					break
				}
				if !client.rtp.pop(packet.queueID) {
					continue
				}
				sender, known := mesh.indexByID[packet.peerID]
				if !known || sender == receiver {
					mesh.t.Fatalf("stage %d frame %d receiver %d got invalid peer %q", stage, frame, receiver, packet.peerID)
				}
				if received[receiver][sender] {
					mesh.t.Fatalf("stage %d frame %d duplicate RTP %s -> %s", stage, frame, mesh.ids[sender], mesh.ids[receiver])
				}
				wantPayload := meshPayload(stage, sender, frame)
				if string(packet.payload) != string(wantPayload) {
					mesh.t.Fatalf(
						"stage %d frame %d payload %s -> %s = %x, want %x",
						stage, frame, mesh.ids[sender], mesh.ids[receiver], packet.payload, wantPayload,
					)
				}
				wantGeneration := mesh.generations[receiver][sender]
				if packet.generation != wantGeneration {
					mesh.t.Fatalf(
						"stage %d frame %d generation %s -> %s = %d, want %d",
						stage, frame, mesh.ids[sender], mesh.ids[receiver], packet.generation, wantGeneration,
					)
				}
				currentPeer := client.peer(mesh.ids[sender])
				if currentPeer == nil {
					mesh.t.Fatalf("stage %d frame %d missing current peer %s -> %s", stage, frame, mesh.ids[sender], mesh.ids[receiver])
				}
				if mesh.haveLastPacket[receiver][sender] && mesh.lastPeers[receiver][sender] == currentPeer {
					mediaDelta := mediaSequence - mesh.lastMediaSequences[receiver][sender]
					sequenceDelta := uint16(packet.sequence - mesh.lastPackets[receiver][sender].sequence)
					timestampDelta := uint32(packet.timestamp - mesh.lastPackets[receiver][sender].timestamp)
					if sequenceDelta != uint16(mediaDelta) || timestampDelta != uint32(mediaDelta*960) {
						mesh.t.Fatalf(
							"stage %d frame %d RTP order %s -> %s: sequence delta=%d/%d timestamp delta=%d/%d",
							stage, frame, mesh.ids[sender], mesh.ids[receiver],
							sequenceDelta, mediaDelta, timestampDelta, mediaDelta*960,
						)
					}
				}
				mesh.lastPackets[receiver][sender] = packet
				mesh.lastMediaSequences[receiver][sender] = mediaSequence
				mesh.lastPeers[receiver][sender] = currentPeer
				mesh.haveLastPacket[receiver][sender] = true
				packets[receiver][sender] = packet
				received[receiver][sender] = true
				got++
			}
		}
		if got < want && processed == 0 {
			time.Sleep(2 * time.Millisecond)
		}
	}
	if got != want {
		var missing []string
		for receiver := 0; receiver < tenClientMeshSize; receiver++ {
			for sender := 0; sender < tenClientMeshSize; sender++ {
				if receiver != sender && !received[receiver][sender] {
					missing = append(missing, fmt.Sprintf("%d>%d", sender, receiver))
				}
			}
		}
		mesh.t.Fatalf("stage %d frame %d received %d/%d RTP packets; missing=%s", stage, frame, got, want, strings.Join(missing, ","))
	}
	return packets
}

func (mesh *tenClientMesh) exerciseMedia(stage byte, frames int) time.Duration {
	mesh.t.Helper()
	started := time.Now()
	before := make([]meshTransportSnapshot, tenClientMeshSize)
	for i, client := range mesh.clients {
		before[i] = snapshotMeshTransport(client)
		if _, overflow, ok := client.rtp.peek(); ok {
			mesh.t.Fatalf("stage %d began with queued RTP on client %d", stage, i)
		} else if overflow != 0 {
			mesh.t.Fatalf("stage %d client %d ingress overflow before send = %d", stage, i, overflow)
		}
	}
	for frame := 0; frame < frames; frame++ {
		mediaSequence := mesh.nextMediaSequence
		for sender, client := range mesh.clients {
			result := client.sendOpus(meshPayload(stage, sender, frame), 1, mediaSequence)
			if result.attempted != tenClientMeshSize-1 || result.enqueued != tenClientMeshSize-1 ||
				result.queueFull != 0 || result.stale != 0 {
				mesh.t.Fatalf("stage %d frame %d send client %d = %+v", stage, frame, sender, result)
			}
		}
		mesh.drainOneMediaRound(stage, frame, mediaSequence)
		mesh.nextMediaSequence++
	}
	wantPerClient := uint64(frames * (tenClientMeshSize - 1))
	wantBytesPerClient := wantPerClient * uint64(len(meshPayload(stage, 0, 0)))
	for i, client := range mesh.clients {
		after := snapshotMeshTransport(client)
		if after.txAttempts-before[i].txAttempts != wantPerClient ||
			after.txOK-before[i].txOK != wantPerClient ||
			after.rxPackets-before[i].rxPackets != wantPerClient ||
			after.rxBytes-before[i].rxBytes != wantBytesPerClient {
			mesh.t.Fatalf(
				"stage %d client %d counter delta: tx_attempts=%d tx_ok=%d rx_packets=%d rx_bytes=%d; want %d/%d/%d/%d",
				stage, i,
				after.txAttempts-before[i].txAttempts, after.txOK-before[i].txOK,
				after.rxPackets-before[i].rxPackets, after.rxBytes-before[i].rxBytes,
				wantPerClient, wantPerClient, wantPerClient, wantBytesPerClient,
			)
		}
		if after.txErrors != before[i].txErrors || after.txQueueDropped != before[i].txQueueDropped ||
			after.txStaleDropped != before[i].txStaleDropped || after.txWriteTimeouts != before[i].txWriteTimeouts {
			mesh.t.Fatalf(
				"stage %d client %d error counter delta: errors=%d queue_dropped=%d stale=%d timeouts=%d",
				stage, i,
				after.txErrors-before[i].txErrors, after.txQueueDropped-before[i].txQueueDropped,
				after.txStaleDropped-before[i].txStaleDropped, after.txWriteTimeouts-before[i].txWriteTimeouts,
			)
		}
		if _, overflow, ok := client.rtp.peek(); ok || overflow != 0 {
			mesh.t.Fatalf("stage %d client %d residual RTP=%v overflow=%d", stage, i, ok, overflow)
		}
	}
	return time.Since(started)
}

type meshPairUfrags struct {
	offers  [tenClientMeshSize][tenClientMeshSize]string
	answers [tenClientMeshSize][tenClientMeshSize]string
}

func (mesh *tenClientMesh) latestPairUfrags(generation uint32, label string) meshPairUfrags {
	mesh.t.Helper()
	var result meshPairUfrags
	for lower := 0; lower < tenClientMeshSize; lower++ {
		for higher := lower + 1; higher < tenClientMeshSize; higher++ {
			offerTrace := &mesh.traces[lower][higher]
			answerTrace := &mesh.traces[higher][lower]
			offer := iceUfrag(latestSDPForGeneration(
				offerTrace.offers, offerTrace.offerGenerations, generation,
			))
			answer := iceUfrag(latestSDPForGeneration(
				answerTrace.answers, answerTrace.answerGenerations, generation,
			))
			if offer == "" || answer == "" {
				mesh.t.Fatalf(
					"%s %s <-> %s ICE ufrags: offer=%q answer=%q",
					label, mesh.ids[lower], mesh.ids[higher], offer, answer,
				)
			}
			result.offers[lower][higher] = offer
			result.answers[lower][higher] = answer
		}
	}
	return result
}

func (mesh *tenClientMesh) requireDescriptionUfrag(
	label string,
	description *webrtc.SessionDescription,
	wantType webrtc.SDPType,
	wantUfrag string,
) {
	mesh.t.Helper()
	if description == nil {
		mesh.t.Fatalf("%s description is nil", label)
	}
	if description.Type != wantType {
		mesh.t.Fatalf("%s type = %s, want %s", label, description.Type, wantType)
	}
	if got := iceUfrag(description.SDP); got != wantUfrag {
		mesh.t.Fatalf("%s ICE ufrag = %q, want %q", label, got, wantUfrag)
	}
}

func (mesh *tenClientMesh) requireCurrentPairDescriptions(
	lower int,
	higher int,
	ufrags meshPairUfrags,
) {
	mesh.t.Helper()
	offerer := mesh.clients[lower].peer(mesh.ids[higher])
	answerer := mesh.clients[higher].peer(mesh.ids[lower])
	if offerer == nil || answerer == nil {
		mesh.t.Fatalf("current descriptions %s <-> %s missing peer", mesh.ids[lower], mesh.ids[higher])
	}
	offerUfrag := ufrags.offers[lower][higher]
	answerUfrag := ufrags.answers[lower][higher]
	mesh.requireDescriptionUfrag(
		fmt.Sprintf("%s -> %s current local", mesh.ids[lower], mesh.ids[higher]),
		offerer.pc.CurrentLocalDescription(), webrtc.SDPTypeOffer, offerUfrag,
	)
	mesh.requireDescriptionUfrag(
		fmt.Sprintf("%s -> %s current remote", mesh.ids[lower], mesh.ids[higher]),
		offerer.pc.CurrentRemoteDescription(), webrtc.SDPTypeAnswer, answerUfrag,
	)
	mesh.requireDescriptionUfrag(
		fmt.Sprintf("%s -> %s current local", mesh.ids[higher], mesh.ids[lower]),
		answerer.pc.CurrentLocalDescription(), webrtc.SDPTypeAnswer, answerUfrag,
	)
	mesh.requireDescriptionUfrag(
		fmt.Sprintf("%s -> %s current remote", mesh.ids[higher], mesh.ids[lower]),
		answerer.pc.CurrentRemoteDescription(), webrtc.SDPTypeOffer, offerUfrag,
	)
}

func (mesh *tenClientMesh) restartEveryPair(initial meshPairUfrags) time.Duration {
	mesh.t.Helper()
	started := time.Now()
	baselineEOC := mesh.eocSnapshot()
	baselineSignals := mesh.signalSnapshot()
	var restartChecking [tenClientMeshSize][tenClientMeshSize]atomic.Uint64
	var restartCompleted [tenClientMeshSize][tenClientMeshSize]atomic.Uint64
	var selectedPairChanged [tenClientMeshSize][tenClientMeshSize]atomic.Uint64
	for source := 0; source < tenClientMeshSize; source++ {
		for target := 0; target < tenClientMeshSize; target++ {
			if source == target {
				continue
			}
			current := mesh.clients[source].peer(mesh.ids[target])
			if current == nil || current.sender.Transport() == nil ||
				current.sender.Transport().ICETransport() == nil {
				mesh.t.Fatalf("install restart observer %s -> %s: missing transport", mesh.ids[source], mesh.ids[target])
			}
			checking := &restartChecking[source][target]
			completed := &restartCompleted[source][target]
			selected := &selectedPairChanged[source][target]
			current.pc.OnICEConnectionStateChange(func(state webrtc.ICEConnectionState) {
				switch state {
				case webrtc.ICEConnectionStateChecking:
					checking.Add(1)
				case webrtc.ICEConnectionStateConnected, webrtc.ICEConnectionStateCompleted:
					completed.Add(1)
				}
			})
			current.sender.Transport().ICETransport().OnSelectedCandidatePairChange(
				func(*webrtc.ICECandidatePair) {
					selected.Add(1)
				},
			)
		}
	}
	type restartResult struct {
		lower  int
		higher int
		err    error
	}
	results := make(chan restartResult, tenClientMeshSize*(tenClientMeshSize-1)/2)
	start := make(chan struct{})
	var workers sync.WaitGroup
	for lower := 0; lower < tenClientMeshSize; lower++ {
		for higher := lower + 1; higher < tenClientMeshSize; higher++ {
			workers.Add(1)
			go func(lower, higher int) {
				defer workers.Done()
				<-start
				current := mesh.clients[lower].peer(mesh.ids[higher])
				if current == nil {
					results <- restartResult{lower: lower, higher: higher, err: errorsForLoopback("missing restart peer")}
					return
				}
				results <- restartResult{lower: lower, higher: higher, err: current.restartICE(false, true)}
			}(lower, higher)
		}
	}
	close(start)
	restartTimer := time.NewTimer(time.Until(mesh.phaseDeadline(10 * time.Second)))
	defer restartTimer.Stop()
	for completed := 0; completed < cap(results); completed++ {
		select {
		case result := <-results:
			if result.err != nil {
				mesh.t.Fatalf("restart %s -> %s: %v", mesh.ids[result.lower], mesh.ids[result.higher], result.err)
			}
		case <-restartTimer.C:
			mesh.t.Fatalf("timed out starting %d concurrent ICE restarts", cap(results))
		}
	}
	workers.Wait()
	mesh.waitFor("concurrent ICE restart", 30*time.Second, func() bool {
		if !mesh.allConnectedAndReady() || !mesh.allEOCAdvanced(baselineEOC) {
			return false
		}
		for lower := 0; lower < tenClientMeshSize; lower++ {
			for higher := lower + 1; higher < tenClientMeshSize; higher++ {
				offerTrace := &mesh.traces[lower][higher]
				answerTrace := &mesh.traces[higher][lower]
				offer := iceUfrag(offerTrace.offers[len(offerTrace.offers)-1])
				answer := iceUfrag(answerTrace.answers[len(answerTrace.answers)-1])
				if offer == "" || answer == "" ||
					offer == initial.offers[lower][higher] ||
					answer == initial.answers[lower][higher] {
					return false
				}
				for _, endpoint := range [][2]int{{lower, higher}, {higher, lower}} {
					source, target := endpoint[0], endpoint[1]
					if restartChecking[source][target].Load() == 0 ||
						restartCompleted[source][target].Load() == 0 ||
						selectedPairChanged[source][target].Load() == 0 {
						return false
					}
				}
				if len(offerTrace.events) == baselineSignals[lower][higher] ||
					len(answerTrace.events) == baselineSignals[higher][lower] {
					return false
				}
			}
		}
		return true
	})
	mesh.requireAllPairNegotiations(baselineSignals, tenClientInitialGeneration)
	restarted := mesh.latestPairUfrags(tenClientInitialGeneration, "restart")
	for lower := 0; lower < tenClientMeshSize; lower++ {
		for higher := lower + 1; higher < tenClientMeshSize; higher++ {
			offer := restarted.offers[lower][higher]
			answer := restarted.answers[lower][higher]
			if offer == initial.offers[lower][higher] || answer == initial.answers[lower][higher] {
				mesh.t.Fatalf(
					"restart ICE ufrags %s <-> %s = %q/%q, initial = %q/%q",
					mesh.ids[lower], mesh.ids[higher], offer, answer,
					initial.offers[lower][higher], initial.answers[lower][higher],
				)
			}
			mesh.requireCurrentPairDescriptions(lower, higher, restarted)
		}
	}
	return time.Since(started)
}

func (mesh *tenClientMesh) churnClient(clientIndex int) time.Duration {
	mesh.t.Helper()
	started := time.Now()
	if clientIndex != tenClientMeshSize-1 {
		mesh.t.Fatalf("churn client = %d, want deterministic highest-index answerer %d", clientIndex, tenClientMeshSize-1)
	}
	if _, err := mesh.pumpSignaling(); err != nil {
		mesh.t.Fatalf("pre-churn signaling: %v", err)
	}
	baselineSignals := mesh.signalSnapshot()
	var peersBefore [tenClientMeshSize][tenClientMeshSize]*peer
	for source := 0; source < tenClientMeshSize; source++ {
		for target := 0; target < tenClientMeshSize; target++ {
			if source != target {
				peersBefore[source][target] = mesh.clients[source].peer(mesh.ids[target])
			}
		}
	}
	var previousOffers [tenClientMeshSize]string
	var previousAnswers [tenClientMeshSize]string
	oldClient := mesh.clients[clientIndex]
	oldPeers := make([]*peer, 0, 2*(tenClientMeshSize-1))
	for other := 0; other < tenClientMeshSize; other++ {
		if other == clientIndex {
			continue
		}
		oldPeers = append(oldPeers, peersBefore[clientIndex][other])
		oldPeers = append(oldPeers, peersBefore[other][clientIndex])
		offerTrace := &mesh.traces[other][clientIndex]
		answerTrace := &mesh.traces[clientIndex][other]
		previousOffers[other] = iceUfrag(offerTrace.offers[len(offerTrace.offers)-1])
		previousAnswers[other] = iceUfrag(answerTrace.answers[len(answerTrace.answers)-1])
	}
	for other := 0; other < tenClientMeshSize; other++ {
		if other != clientIndex {
			mesh.clients[other].removePeer(mesh.ids[clientIndex])
		}
	}
	oldClient.close()
	closeDeadline := mesh.phaseDeadline(2 * time.Second)
	for {
		allClosed := true
		for _, oldPeer := range oldPeers {
			if oldPeer == nil || oldPeer.active.Load() ||
				oldPeer.pc.ConnectionState() != webrtc.PeerConnectionStateClosed {
				allClosed = false
				break
			}
		}
		if allClosed {
			break
		}
		if !time.Now().Before(closeDeadline) {
			var open []string
			for index, oldPeer := range oldPeers {
				if oldPeer == nil {
					open = append(open, fmt.Sprintf("%d:nil", index))
				} else if oldPeer.active.Load() ||
					oldPeer.pc.ConnectionState() != webrtc.PeerConnectionStateClosed {
					open = append(open, fmt.Sprintf(
						"%d:active=%v,state=%s", index, oldPeer.active.Load(), oldPeer.pc.ConnectionState(),
					))
				}
			}
			mesh.t.Fatalf("churn old peers did not fully close: %s", strings.Join(open, ","))
		}
		time.Sleep(5 * time.Millisecond)
	}

	replacement, err := newEngine()
	if err != nil {
		mesh.t.Fatalf("new replacement client %d: %v", clientIndex, err)
	}
	if err = replacement.setICEServers(nil); err != nil {
		replacement.close()
		mesh.t.Fatalf("disable replacement external ICE servers: %v", err)
	}
	mesh.clients[clientIndex] = replacement
	mesh.allEngines = append(mesh.allEngines, replacement)
	for other := 0; other < tenClientMeshSize; other++ {
		if other == clientIndex {
			continue
		}
		mesh.generations[clientIndex][other] = tenClientChurnGeneration
		mesh.generations[other][clientIndex] = tenClientChurnGeneration
	}
	// client-09 is deliberately chosen by the test, so it remains the answerer
	// for every deterministic lower-index offerer after reconnecting.
	for other := 0; other < tenClientMeshSize; other++ {
		if other == clientIndex {
			continue
		}
		if err = replacement.addPeer(mesh.ids[other], false, false, tenClientChurnGeneration, 0); err != nil {
			mesh.t.Fatalf("replacement answerer %s -> %s: %v", mesh.ids[clientIndex], mesh.ids[other], err)
		}
	}
	for other := 0; other < tenClientMeshSize; other++ {
		if other == clientIndex {
			continue
		}
		if err = mesh.clients[other].addPeer(mesh.ids[clientIndex], true, false, tenClientChurnGeneration, 0); err != nil {
			mesh.t.Fatalf("replacement offerer %s -> %s: %v", mesh.ids[other], mesh.ids[clientIndex], err)
		}
	}
	mesh.requirePeerObjectCount(90)
	mesh.waitFor("client churn reconnect", 25*time.Second, func() bool {
		return mesh.allConnectedAndReady() && mesh.allClientEOC(clientIndex)
	})
	unchangedEndpoints := 0
	replacedEndpoints := 0
	for source := 0; source < tenClientMeshSize; source++ {
		for target := 0; target < tenClientMeshSize; target++ {
			if source == target {
				continue
			}
			current := mesh.clients[source].peer(mesh.ids[target])
			affected := source == clientIndex || target == clientIndex
			if affected {
				if current == peersBefore[source][target] {
					mesh.t.Fatalf(
						"churn endpoint %s -> %s retained old peer object",
						mesh.ids[source], mesh.ids[target],
					)
				}
				replacedEndpoints++
			} else {
				if current != peersBefore[source][target] {
					mesh.t.Fatalf(
						"churn replaced unaffected endpoint %s -> %s",
						mesh.ids[source], mesh.ids[target],
					)
				}
				unchangedEndpoints++
			}
		}
	}
	if unchangedEndpoints != 72 || replacedEndpoints != 18 {
		mesh.t.Fatalf(
			"churn endpoint identity: unchanged=%d replaced=%d, want 72/18",
			unchangedEndpoints, replacedEndpoints,
		)
	}
	var churnUfrags meshPairUfrags
	for other := 0; other < tenClientMeshSize; other++ {
		if other == clientIndex {
			continue
		}
		local := mesh.clients[other].peer(mesh.ids[clientIndex])
		remote := replacement.peer(mesh.ids[other])
		if local == nil || remote == nil || local.generation != tenClientChurnGeneration ||
			remote.generation != tenClientChurnGeneration {
			mesh.t.Fatalf("replacement pair %d/%d did not install generation %d", other, clientIndex, tenClientChurnGeneration)
		}
		mesh.requireNegotiationDirection(
			other, clientIndex, baselineSignals[other][clientIndex], tenClientChurnGeneration, "offer",
		)
		mesh.requireNegotiationDirection(
			clientIndex, other, baselineSignals[clientIndex][other], tenClientChurnGeneration, "answer",
		)
		offerTrace := &mesh.traces[other][clientIndex]
		answerTrace := &mesh.traces[clientIndex][other]
		newOffer := iceUfrag(latestSDPForGeneration(
			offerTrace.offers, offerTrace.offerGenerations, tenClientChurnGeneration,
		))
		newAnswer := iceUfrag(latestSDPForGeneration(
			answerTrace.answers, answerTrace.answerGenerations, tenClientChurnGeneration,
		))
		if newOffer == "" || newAnswer == "" ||
			newOffer == previousOffers[other] || newAnswer == previousAnswers[other] {
			mesh.t.Fatalf(
				"churn ufrags %s <-> %s = %q/%q, previous = %q/%q",
				mesh.ids[other], mesh.ids[clientIndex], newOffer, newAnswer,
				previousOffers[other], previousAnswers[other],
			)
		}
		churnUfrags.offers[other][clientIndex] = newOffer
		churnUfrags.answers[other][clientIndex] = newAnswer
		mesh.requireCurrentPairDescriptions(other, clientIndex, churnUfrags)
	}
	return time.Since(started)
}

func TestPionTenClientFullMeshStress(t *testing.T) {
	if testing.Short() {
		t.Skip("skipping 10-client Pion integration stress test in short mode")
	}
	started := time.Now()
	mesh := newTenClientMesh(t)
	initialSignals := mesh.signalSnapshot()
	mesh.addInitialPeers()
	connectDuration := mesh.waitFor("initial full mesh", 30*time.Second, func() bool {
		return mesh.allConnectedAndReady() && mesh.allInitialEOC()
	})
	mesh.requireAllPairNegotiations(initialSignals, tenClientInitialGeneration)
	initialPairs, err := mesh.selectedPairSummary()
	if err != nil {
		t.Fatal(err)
	}
	initialUfrags := mesh.latestPairUfrags(tenClientInitialGeneration, "initial")
	for lower := 0; lower < tenClientMeshSize; lower++ {
		for higher := lower + 1; higher < tenClientMeshSize; higher++ {
			mesh.requireCurrentPairDescriptions(lower, higher, initialUfrags)
		}
	}
	initialMediaDuration := mesh.exerciseMedia(1, 3)

	restartDuration := mesh.restartEveryPair(initialUfrags)
	restartPairs, err := mesh.selectedPairSummary()
	if err != nil {
		t.Fatal(err)
	}
	postRestartMediaDuration := mesh.exerciseMedia(2, 2)

	churnDuration := mesh.churnClient(tenClientMeshSize - 1)
	churnPairs, err := mesh.selectedPairSummary()
	if err != nil {
		t.Fatal(err)
	}
	postChurnMediaDuration := mesh.exerciseMedia(3, 2)

	t.Logf(
		"10-client mesh: clients=10 undirected_connections=45 peer_objects=90 initial_connect=%s initial_media=%s",
		connectDuration.Round(time.Millisecond), initialMediaDuration.Round(time.Millisecond),
	)
	t.Logf(
		"10-client mesh: concurrent_restarts=45 restart=%s post_restart_media=%s churn=%s post_churn_media=%s",
		restartDuration.Round(time.Millisecond), postRestartMediaDuration.Round(time.Millisecond),
		churnDuration.Round(time.Millisecond), postChurnMediaDuration.Round(time.Millisecond),
	)
	t.Logf("10-client mesh candidate pairs: initial=[%s] restart=[%s] churn=[%s]",
		formatPairSummary(initialPairs), formatPairSummary(restartPairs), formatPairSummary(churnPairs))
	t.Logf("10-client mesh delivered 630/630 expected RTP packets in %s", time.Since(started).Round(time.Millisecond))
}
