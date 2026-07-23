// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"encoding/json"
	"fmt"
	"net"
	"strings"
	"testing"
	"time"

	"github.com/pion/webrtc/v4"
)

type signalingTrace struct {
	kinds           []string
	offers          []string
	eoc             int
	candidates      []string
	candidateFilter func(string) bool
}

func pumpSignaling(source *engine, target *peer, trace *signalingTrace) (int, error) {
	processed := 0
	for {
		id, data := source.control.peek()
		if data == nil {
			return processed, nil
		}
		var event controlEvent
		if err := json.Unmarshal(data, &event); err != nil {
			return processed, fmt.Errorf("decode control: %w", err)
		}
		if !source.control.pop(id) {
			continue
		}
		processed++
		trace.kinds = append(trace.kinds, event.Kind)
		switch event.Kind {
		case "sdp":
			if event.SDPType == "offer" {
				trace.offers = append(trace.offers, event.SDP)
			}
			if err := target.setRemoteSDP(event.SDPType, event.SDP); err != nil {
				return processed, fmt.Errorf("apply %s: %w", event.SDPType, err)
			}
		case "candidate":
			if event.Candidate == nil {
				return processed, errorsForLoopback("candidate event omitted candidate field")
			}
			if *event.Candidate != "" && trace.candidateFilter != nil &&
				!trace.candidateFilter(*event.Candidate) {
				continue
			}
			if *event.Candidate == "" {
				trace.eoc++
			}
			trace.candidates = append(trace.candidates, *event.Candidate)
			if err := target.addICECandidate(*event.Candidate); err != nil {
				return processed, fmt.Errorf("apply candidate: %w", err)
			}
		case "error":
			return processed, errorsForLoopback(event.Message)
		}
	}
}

type loopbackError string

func (e loopbackError) Error() string { return string(e) }

func errorsForLoopback(message string) error { return loopbackError(message) }

func waitForLoopback(
	t *testing.T,
	left, right *engine,
	leftPeer, rightPeer *peer,
	leftTrace, rightTrace *signalingTrace,
	condition func() bool,
) {
	t.Helper()
	deadline := time.Now().Add(15 * time.Second)
	for time.Now().Before(deadline) {
		processed := 0
		count, err := pumpSignaling(left, rightPeer, leftTrace)
		if err != nil {
			t.Fatalf("left signaling: %v", err)
		}
		processed += count
		count, err = pumpSignaling(right, leftPeer, rightTrace)
		if err != nil {
			t.Fatalf("right signaling: %v", err)
		}
		processed += count
		if condition() {
			return
		}
		if leftPeer.pc.ConnectionState() == webrtc.PeerConnectionStateFailed ||
			rightPeer.pc.ConnectionState() == webrtc.PeerConnectionStateFailed {
			t.Fatalf("loopback failed: left=%s right=%s", leftPeer.pc.ConnectionState(), rightPeer.pc.ConnectionState())
		}
		if processed == 0 {
			time.Sleep(5 * time.Millisecond)
		}
	}
	t.Fatalf(
		"loopback timeout: left=%s/%s right=%s/%s ufrags left=%q/%q right=%q/%q candidates left=%q right=%q left-events=%v right-events=%v",
		leftPeer.pc.ConnectionState(), leftPeer.pc.ICEConnectionState(),
		rightPeer.pc.ConnectionState(), rightPeer.pc.ICEConnectionState(),
		descriptionUfrag(leftPeer.pc.LocalDescription()), descriptionUfrag(leftPeer.pc.RemoteDescription()),
		descriptionUfrag(rightPeer.pc.LocalDescription()), descriptionUfrag(rightPeer.pc.RemoteDescription()),
		leftTrace.candidates, rightTrace.candidates,
		leftTrace.kinds, rightTrace.kinds,
	)
}

func waitForRTP(t *testing.T, q *rtpQueue, count int) []inboundRTP {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	packets := make([]inboundRTP, 0, count)
	for len(packets) < count && time.Now().Before(deadline) {
		packet, _, ok := q.peek()
		if !ok {
			time.Sleep(2 * time.Millisecond)
			continue
		}
		if q.pop(packet.queueID) {
			packets = append(packets, packet)
		}
	}
	if len(packets) != count {
		t.Fatalf("received %d RTP packets, want %d", len(packets), count)
	}
	return packets
}

func waitForRTPPayload(t *testing.T, q *rtpQueue, payload []byte) inboundRTP {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		packet, _, ok := q.peek()
		if !ok {
			time.Sleep(2 * time.Millisecond)
			continue
		}
		if !q.pop(packet.queueID) {
			continue
		}
		if string(packet.payload) == string(payload) {
			return packet
		}
	}
	t.Fatalf("timed out waiting for RTP payload %x", payload)
	return inboundRTP{}
}

func waitForRemoteFeedback(t *testing.T, p *peer, packetsReceived uint64) remoteSenderSnapshot {
	t.Helper()
	deadline := time.Now().Add(6 * time.Second)
	for time.Now().Before(deadline) {
		snapshot := p.remoteFeedback.snapshot()
		if snapshot.valid && snapshot.packetsReceived >= packetsReceived && snapshot.rttMeasurements > 0 {
			return snapshot
		}
		time.Sleep(10 * time.Millisecond)
	}
	snapshot := p.remoteFeedback.snapshot()
	t.Fatalf("remote sender feedback timeout: %+v", snapshot)
	return remoteSenderSnapshot{}
}

func descriptionUfrag(description *webrtc.SessionDescription) string {
	if description == nil {
		return ""
	}
	return iceUfrag(description.SDP)
}

func iceUfrag(sdp string) string {
	for _, field := range strings.Fields(sdp) {
		if value, ok := strings.CutPrefix(field, "a=ice-ufrag:"); ok {
			return value
		}
	}
	return ""
}

func firstIndex(items []string, wanted string) int {
	for i, item := range items {
		if item == wanted {
			return i
		}
	}
	return -1
}

func TestPionLoopbackRTPOrderingEOCAndICERestart(t *testing.T) {
	t.Setenv("PC_PION_TEST_DISABLE_MDNS", "1")
	t.Setenv("PC_PION_TEST_DISABLE_RENOMINATION", "")

	left, err := newEngine()
	if err != nil {
		t.Fatalf("new left engine: %v", err)
	}
	defer left.close()
	right, err := newEngine()
	if err != nil {
		t.Fatalf("new right engine: %v", err)
	}
	defer right.close()
	if err = left.setICEServers(nil); err != nil {
		t.Fatal(err)
	}
	if err = right.setICEServers(nil); err != nil {
		t.Fatal(err)
	}
	if err = right.addPeer("left", false, false, 17, 0); err != nil {
		t.Fatalf("add right peer: %v", err)
	}
	if err = left.addPeer("right", true, false, 17, 0); err != nil {
		t.Fatalf("add left peer: %v", err)
	}
	leftPeer := left.peer("right")
	rightPeer := right.peer("left")
	if result := left.sendOpus([]byte{0xf8, 0xff, 0xff}, 1, 90); result.enqueued != 1 {
		t.Fatalf("pre-connection send = %+v", result)
	}
	preconnectDeadline := time.Now().Add(time.Second)
	for len(leftPeer.outbound) != 0 && time.Now().Before(preconnectDeadline) {
		time.Sleep(time.Millisecond)
	}
	if len(leftPeer.outbound) != 0 || leftPeer.sentRTP.Load() || left.counters.rtpTXOK.Load() != 0 {
		t.Fatalf("pre-connection audio was treated as sent: queued=%d sent=%v tx_ok=%d",
			len(leftPeer.outbound), leftPeer.sentRTP.Load(), left.counters.rtpTXOK.Load())
	}
	leftTrace := &signalingTrace{}
	rightTrace := &signalingTrace{}
	waitForLoopback(t, left, right, leftPeer, rightPeer, leftTrace, rightTrace, func() bool {
		return leftPeer.pc.ConnectionState() == webrtc.PeerConnectionStateConnected &&
			rightPeer.pc.ConnectionState() == webrtc.PeerConnectionStateConnected &&
			leftTrace.eoc > 0 && rightTrace.eoc > 0
	})
	for name, peer := range map[string]*peer{"left": leftPeer, "right": rightPeer} {
		requireSelectedProtocol(t, name, peer, "udp")
	}
	if firstIndex(leftTrace.kinds, "sdp") < 0 || firstIndex(leftTrace.kinds, "candidate") < firstIndex(leftTrace.kinds, "sdp") {
		t.Fatalf("left signaling order = %v", leftTrace.kinds)
	}
	if firstIndex(rightTrace.kinds, "sdp") < 0 || firstIndex(rightTrace.kinds, "candidate") < firstIndex(rightTrace.kinds, "sdp") {
		t.Fatalf("right signaling order = %v", rightTrace.kinds)
	}
	if len(leftTrace.offers) != 1 {
		t.Fatalf("initial offer count = %d, want 1", len(leftTrace.offers))
	}
	if strings.Contains(strings.ToLower(leftTrace.offers[0]), "renomination") {
		t.Fatal("default ICE offer advertised automatic renomination")
	}
	initialUfrag := iceUfrag(leftTrace.offers[0])
	if initialUfrag == "" {
		t.Fatalf("initial offer has no ICE ufrag: %q", leftTrace.offers[0])
	}

	firstPayload := []byte{0xf8, 0xff, 0xfe}
	secondPayload := []byte{0xf8, 0xff, 0xfd}
	if result := left.sendOpus(firstPayload, 1, 100); result.enqueued != 1 {
		t.Fatalf("first send = %+v", result)
	}
	if result := left.sendOpus(secondPayload, 1, 103); result.enqueued != 1 {
		t.Fatalf("second send = %+v", result)
	}
	packets := waitForRTP(t, right.rtp, 2)
	if string(packets[0].payload) != string(firstPayload) || string(packets[1].payload) != string(secondPayload) {
		t.Fatalf("payloads = %x / %x", packets[0].payload, packets[1].payload)
	}
	if packets[0].sequence != leftPeer.sequenceBase {
		t.Fatalf("first on-wire sequence = %d, want randomized base %d", packets[0].sequence, leftPeer.sequenceBase)
	}
	if uint16(packets[1].sequence-packets[0].sequence) != 1 {
		t.Fatalf("RTP sequence gap = %d, want one on-wire packet", uint16(packets[1].sequence-packets[0].sequence))
	}
	if uint32(packets[1].timestamp-packets[0].timestamp) != 3*960 {
		t.Fatalf("RTP timestamp gap = %d, want %d", uint32(packets[1].timestamp-packets[0].timestamp), 3*960)
	}
	if packets[0].generation != 17 || packets[1].generation != 17 {
		t.Fatalf("RTP generations = %d/%d", packets[0].generation, packets[1].generation)
	}

	if result := right.sendOpus([]byte{0xf8, 0xff, 0xfc}, 1, 500); result.enqueued != 1 {
		t.Fatalf("reverse send = %+v", result)
	}
	if packet := waitForRTP(t, left.rtp, 1)[0]; packet.generation != 17 {
		t.Fatalf("reverse RTP generation = %d", packet.generation)
	}
	feedback := waitForRemoteFeedback(t, leftPeer, 2)
	if feedback.packetsLost != 0 {
		t.Fatalf("remote cumulative packets lost = %d, want 0 for a local media-time gap", feedback.packetsLost)
	}
	if feedback.fractionLost < 0 || feedback.fractionLost > 1 {
		t.Fatalf("remote fraction lost = %v, want a valid interval fraction", feedback.fractionLost)
	}

	initialAnswerUfrag := descriptionUfrag(rightPeer.pc.LocalDescription())
	if initialAnswerUfrag == "" {
		t.Fatal("initial answer has no ICE ufrag")
	}
	initialLeftEOC := leftTrace.eoc
	initialRightEOC := rightTrace.eoc
	baselineAttempts := left.counters.rtpTXAttempts.Load()
	baselineErrors := left.counters.rtpTXErrors.Load()
	stopRestartTraffic := make(chan struct{})
	restartTrafficDone := make(chan struct{})
	go func() {
		defer close(restartTrafficDone)
		ticker := time.NewTicker(10 * time.Millisecond)
		defer ticker.Stop()
		mediaSequence := uint64(1_000)
		for {
			select {
			case <-stopRestartTraffic:
				return
			case <-ticker.C:
				left.sendOpus([]byte{0xf8, 0xff, 0xf0}, 1, mediaSequence)
				mediaSequence++
			}
		}
	}()
	stopTraffic := func() {
		select {
		case <-restartTrafficDone:
			return
		default:
			close(stopRestartTraffic)
			<-restartTrafficDone
		}
	}
	t.Cleanup(stopTraffic)
	trafficDeadline := time.Now().Add(time.Second)
	for left.counters.rtpTXAttempts.Load() == baselineAttempts && time.Now().Before(trafficDeadline) {
		time.Sleep(time.Millisecond)
	}
	if left.counters.rtpTXAttempts.Load() == baselineAttempts {
		t.Fatal("continuous restart traffic did not reach the RTP writer")
	}
	if err = leftPeer.restartICE(false, true); err != nil {
		t.Fatalf("restart ICE: %v", err)
	}
	waitForLoopback(t, left, right, leftPeer, rightPeer, leftTrace, rightTrace, func() bool {
		leftLocalUfrag := descriptionUfrag(leftPeer.pc.LocalDescription())
		rightLocalUfrag := descriptionUfrag(rightPeer.pc.LocalDescription())
		return len(leftTrace.offers) >= 2 &&
			leftLocalUfrag != "" && leftLocalUfrag != initialUfrag &&
			rightLocalUfrag != "" && rightLocalUfrag != initialAnswerUfrag &&
			descriptionUfrag(rightPeer.pc.RemoteDescription()) == leftLocalUfrag &&
			descriptionUfrag(leftPeer.pc.RemoteDescription()) == rightLocalUfrag &&
			leftTrace.eoc > initialLeftEOC && rightTrace.eoc > initialRightEOC &&
			leftPeer.pc.ConnectionState() == webrtc.PeerConnectionStateConnected &&
			rightPeer.pc.ConnectionState() == webrtc.PeerConnectionStateConnected
	})
	stopTraffic()
	if got := left.counters.rtpTXErrors.Load(); got != baselineErrors {
		t.Fatalf("RTP transmit errors during ICE restart = %d, want %d", got-baselineErrors, 0)
	}
	restartUfrag := iceUfrag(leftTrace.offers[1])
	if restartUfrag == "" || restartUfrag == initialUfrag {
		t.Fatalf("restart ICE ufrag = %q, initial = %q", restartUfrag, initialUfrag)
	}

	postRestartLeft := []byte{0xf8, 0xff, 0xfb}
	if result := left.sendOpus(postRestartLeft, 1, 20_000); result.enqueued != 1 {
		t.Fatalf("post-restart left send = %+v", result)
	}
	if packet := waitForRTPPayload(t, right.rtp, postRestartLeft); string(packet.payload) != string(postRestartLeft) {
		t.Fatalf("post-restart left payload = %x, want %x", packet.payload, postRestartLeft)
	}
	postRestartRight := []byte{0xf8, 0xff, 0xfa}
	if result := right.sendOpus(postRestartRight, 1, 510); result.enqueued != 1 {
		t.Fatalf("post-restart right send = %+v", result)
	}
	if packet := waitForRTP(t, left.rtp, 1)[0]; string(packet.payload) != string(postRestartRight) {
		t.Fatalf("post-restart right payload = %x, want %x", packet.payload, postRestartRight)
	}
}

func requireSelectedProtocol(t *testing.T, name string, p *peer, want string) {
	t.Helper()
	transport := p.sender.Transport()
	if transport == nil || transport.ICETransport() == nil {
		t.Fatalf("%s peer has no ICE transport", name)
	}
	pair, err := transport.ICETransport().GetSelectedCandidatePair()
	if err != nil {
		t.Fatalf("%s selected pair: %v", name, err)
	}
	if pair == nil || pair.Local == nil || pair.Remote == nil {
		t.Fatalf("%s selected pair is incomplete: %+v", name, pair)
	}
	if !strings.EqualFold(pair.Local.Protocol.String(), want) ||
		!strings.EqualFold(pair.Remote.Protocol.String(), want) {
		t.Fatalf(
			"%s selected protocols = %s/%s, want %s/%s",
			name, pair.Local.Protocol, pair.Remote.Protocol, want, want,
		)
	}
}

func directTCPCandidate(candidate string) bool {
	fields := strings.Fields(candidate)
	return len(fields) >= 3 && strings.EqualFold(fields[2], "tcp")
}

func TestPionDirectICETCPCarriesRTP(t *testing.T) {
	t.Setenv("PC_PION_TEST_DISABLE_MDNS", "1")

	left, err := newEngine()
	if err != nil {
		t.Fatalf("new left engine: %v", err)
	}
	defer left.close()
	right, err := newEngine()
	if err != nil {
		t.Fatalf("new right engine: %v", err)
	}
	defer right.close()
	if err = left.setICEServers(nil); err != nil {
		t.Fatal(err)
	}
	if err = right.setICEServers(nil); err != nil {
		t.Fatal(err)
	}
	if err = right.addPeer("left", false, false, 29, 0); err != nil {
		t.Fatalf("add right peer: %v", err)
	}
	if err = left.addPeer("right", true, false, 29, 0); err != nil {
		t.Fatalf("add left peer: %v", err)
	}

	leftPeer := left.peer("right")
	rightPeer := right.peer("left")
	leftTrace := &signalingTrace{candidateFilter: directTCPCandidate}
	rightTrace := &signalingTrace{candidateFilter: directTCPCandidate}
	waitForLoopback(t, left, right, leftPeer, rightPeer, leftTrace, rightTrace, func() bool {
		return leftPeer.pc.ConnectionState() == webrtc.PeerConnectionStateConnected &&
			rightPeer.pc.ConnectionState() == webrtc.PeerConnectionStateConnected &&
			leftTrace.eoc > 0 && rightTrace.eoc > 0
	})

	for name, peer := range map[string]*peer{"left": leftPeer, "right": rightPeer} {
		transport := peer.sender.Transport()
		if transport == nil || transport.ICETransport() == nil {
			t.Fatalf("%s peer has no ICE transport", name)
		}
		pair, pairErr := transport.ICETransport().GetSelectedCandidatePair()
		if pairErr != nil {
			t.Fatalf("%s selected pair: %v", name, pairErr)
		}
		if pair == nil || pair.Local == nil || pair.Remote == nil {
			t.Fatalf("%s selected pair is incomplete: %+v", name, pair)
		}
		if !strings.EqualFold(pair.Local.Protocol.String(), "tcp") ||
			!strings.EqualFold(pair.Remote.Protocol.String(), "tcp") {
			t.Fatalf(
				"%s selected protocols = %s/%s, want tcp/tcp",
				name, pair.Local.Protocol, pair.Remote.Protocol,
			)
		}
		if pair.Local.Typ == webrtc.ICECandidateTypeRelay ||
			pair.Remote.Typ == webrtc.ICECandidateTypeRelay {
			t.Fatalf("%s selected a relay pair: %+v", name, pair)
		}
	}

	leftPayload := []byte{0xf8, 0xff, 0xe1}
	if result := left.sendOpus(leftPayload, 1, 700); result.enqueued != 1 {
		t.Fatalf("left TCP send = %+v", result)
	}
	if packet := waitForRTP(t, right.rtp, 1)[0]; string(packet.payload) != string(leftPayload) {
		t.Fatalf("right TCP payload = %x, want %x", packet.payload, leftPayload)
	}
	rightPayload := []byte{0xf8, 0xff, 0xe2}
	if result := right.sendOpus(rightPayload, 1, 900); result.enqueued != 1 {
		t.Fatalf("right TCP send = %+v", result)
	}
	if packet := waitForRTP(t, left.rtp, 1)[0]; string(packet.payload) != string(rightPayload) {
		t.Fatalf("left TCP payload = %x, want %x", packet.payload, rightPayload)
	}
	if left.counters.rtpTXErrors.Load() != 0 || right.counters.rtpTXErrors.Load() != 0 {
		t.Fatalf(
			"TCP transmit errors left=%d right=%d",
			left.counters.rtpTXErrors.Load(), right.counters.rtpTXErrors.Load(),
		)
	}
}

func TestPionCloseIsNotHeldByTCPPeerWithoutSTUN(t *testing.T) {
	t.Setenv("PC_PION_TEST_DISABLE_MDNS", "1")

	e, err := newEngine()
	if err != nil {
		t.Fatalf("new engine: %v", err)
	}
	localAddresser, ok := e.tcpMux.(interface{ LocalAddr() net.Addr })
	if !ok {
		e.close()
		t.Fatalf("TCP mux does not expose its listen address: %T", e.tcpMux)
	}
	tcpAddr, ok := localAddresser.LocalAddr().(*net.TCPAddr)
	if !ok {
		e.close()
		t.Fatalf("TCP mux listen address = %T, want *net.TCPAddr", localAddresser.LocalAddr())
	}
	dialAddr := *tcpAddr
	if dialAddr.IP.IsUnspecified() {
		if dialAddr.IP.To4() != nil {
			dialAddr.IP = net.IPv4(127, 0, 0, 1)
		} else {
			dialAddr.IP = net.IPv6loopback
		}
	}
	conn, err := net.DialTimeout("tcp", dialAddr.String(), time.Second)
	if err != nil {
		e.close()
		t.Fatalf("dial TCP mux: %v", err)
	}
	defer conn.Close()

	// Allow the mux to accept the socket and block waiting for its initial STUN packet.
	time.Sleep(100 * time.Millisecond)
	closed := make(chan struct{})
	go func() {
		e.close()
		close(closed)
	}()
	select {
	case <-closed:
	case <-time.After(time.Second):
		t.Fatal("engine close blocked on TCP peer that sent no initial STUN")
	}
}
