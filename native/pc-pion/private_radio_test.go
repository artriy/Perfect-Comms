// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/pion/interceptor"
	"github.com/pion/rtp"
	"github.com/pion/webrtc/v4"
)

func TestPrivateRadioDeliveryAndPublicCutover(t *testing.T) {
	t.Setenv("PC_PION_TEST_DISABLE_MDNS", "1")
	sender, err := newEngine()
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(sender.close)
	remotes := make(map[string]*engine)
	originalPeers := make(map[string]*peer)
	ufrags := make(map[string]string)
	connect := func(id string) {
		t.Helper()
		remote, err := newEngine()
		if err != nil {
			t.Fatal(err)
		}
		t.Cleanup(remote.close)
		remotes[id] = remote
		if err = remote.addPeer("sender", false, false, 1, 0); err != nil {
			t.Fatal(err)
		}
		if err = sender.addPeer(id, true, false, 1, 0); err != nil {
			t.Fatal(err)
		}
		deadline := time.Now().Add(15 * time.Second)
		for time.Now().Before(deadline) {
			for {
				queueID, data := sender.control.peek()
				if data == nil {
					break
				}
				if !sender.control.pop(queueID) {
					continue
				}
				var event controlEvent
				if err := json.Unmarshal(data, &event); err != nil {
					t.Fatal(err)
				}
				target := remotes[event.PeerID].peer("sender")
				switch event.Kind {
				case "sdp":
					err = target.setRemoteSDP(event.SDPType, event.SDP)
				case "candidate":
					if event.Candidate == nil {
						t.Fatal("missing candidate")
					}
					err = target.addICECandidate(*event.Candidate)
				case "error":
					t.Fatal(event.Message)
				}
				if err != nil {
					t.Fatal(err)
				}
			}
			for peerID, remote := range remotes {
				if _, err := pumpSignaling(remote, sender.peer(peerID), &signalingTrace{}); err != nil {
					t.Fatal(err)
				}
			}
			if sender.peer(id).pc.ConnectionState() == webrtc.PeerConnectionStateConnected &&
				remote.peer("sender").pc.ConnectionState() == webrtc.PeerConnectionStateConnected {
				originalPeers[id] = sender.peer(id)
				ufrags[id] = descriptionUfrag(sender.peer(id).pc.LocalDescription())
				return
			}
			time.Sleep(5 * time.Millisecond)
		}
		t.Fatalf("peer %s failed to connect", id)
	}
	connect("1")
	connect("2")
	connect("3")
	if !sender.setPrivateRadio(true, []string{"2", "1", "1"}, 1, time.Second) {
		t.Fatal("private scope failed")
	}
	connect("4")
	privatePayload := []byte{0xf8, 0xff, 0xa1}
	if got := sender.sendOpus(privatePayload, 1, 10); got.attempted != 2 || got.enqueued != 2 || got.stale != 0 {
		t.Fatalf("private fanout = %+v", got)
	}
	for _, id := range []string{"1", "2"} {
		waitForRTPPayload(t, remotes[id].rtp, privatePayload)
	}
	if !sender.setPrivateRadio(true, nil, 2, time.Second) {
		t.Fatal("empty scope failed")
	}
	if got := sender.sendOpus([]byte{0xf8, 0xff, 0xa2}, 2, 11); got != (sendResult{}) {
		t.Fatalf("empty fanout = %+v", got)
	}
	if !sender.setPrivateRadio(false, []string{"ignored"}, 3, time.Second) {
		t.Fatal("public scope failed")
	}
	publicPayload := []byte{0xf8, 0xff, 0xa3}
	if got := sender.sendOpus(publicPayload, 3, 12); got.attempted != 4 || got.enqueued != 4 {
		t.Fatalf("public fanout = %+v", got)
	}
	for id, remote := range remotes {
		packets := waitForRTP(t, remote.rtp, 1)
		if string(packets[0].payload) != string(publicPayload) {
			t.Fatalf("peer %s received excluded private audio: %x", id, packets[0].payload)
		}
		p := sender.peer(id)
		if p != originalPeers[id] || !p.active.Load() || p.pc.ConnectionState() != webrtc.PeerConnectionStateConnected || descriptionUfrag(p.pc.LocalDescription()) != ufrags[id] {
			t.Fatalf("scope change reset peer %s", id)
		}
	}
}

func TestPrivateRadioQueueCutoversAndFutureReceiver(t *testing.T) {
	e, first := queueOnlyEngineAndPeer("1", 1)
	_, second := queueOnlyEngineAndPeer("2", 1)
	second.engine = e
	e.peers["2"] = second
	if !e.setPrivateRadio(true, []string{"1", "future"}, 1, time.Second) {
		t.Fatal("private scope failed")
	}
	e.sendOpus([]byte{0x11}, 1, 1)
	if !e.setPrivateRadio(true, []string{"future", "1", "1"}, 99, time.Second) || len(first.outbound) != 1 || e.privacyFloor.Load() != 1 {
		t.Fatal("identical scope discarded current speech or advanced epoch")
	}
	_, future := queueOnlyEngineAndPeer("future", 1)
	future.engine = e
	e.peers["future"] = future
	if got := e.sendOpus([]byte{0x12}, 1, 2); got.enqueued != 2 {
		t.Fatalf("listed future peer not included: %+v", got)
	}
	if !e.setPrivateRadio(true, []string{"1", "2", "future"}, 2, time.Second) {
		t.Fatal("recipient expansion failed")
	}
	if len(first.outbound) != 0 || len(second.outbound) != 0 || len(future.outbound) != 0 {
		t.Fatal("private queue survived recipient expansion")
	}
	if got := e.sendOpus([]byte{0x13}, 1, 3); got.enqueued != 0 || got.stale != 3 {
		t.Fatalf("old-epoch private packet admitted: %+v", got)
	}
	e.sendOpus([]byte{0x14}, 2, 4)
	if !e.setPrivateRadio(false, nil, 3, time.Second) {
		t.Fatal("public cutover failed")
	}
	for _, p := range e.peers {
		if len(p.outbound) != 0 || !p.active.Load() {
			t.Fatal("private queue survived public cutover or healthy peer was closed")
		}
	}
	if got := e.sendOpus([]byte{0x15}, 3, 5); got.enqueued != 3 {
		t.Fatalf("public fanout = %+v", got)
	}
}

func TestRadioReceiverBoundaryRejectsInvalidIDs(t *testing.T) {
	for _, input := range []string{`[""]`, `["` + strings.Repeat("x", 257) + `"]`, `[1]`, string([]byte{'[', '"', 0xff, '"', ']'})} {
		if _, err := decodeRadioReceivers([]byte(input)); err == nil {
			t.Fatalf("accepted invalid receivers %q", input)
		}
	}
}

func TestPrivateRadioCutoverRejectsCachedNACK(t *testing.T) {
	e, p := queueOnlyEngineAndPeer("1", 1)
	writes := make(chan uint16, 2)
	gate, chain, writer := nackGateChain(t, interceptor.RTPWriterFunc(
		func(header *rtp.Header, payload []byte, _ interceptor.Attributes) (int, error) {
			writes <- header.SequenceNumber
			return len(payload), nil
		},
	))
	p.epochGate = gate
	if !e.setPrivateRadio(true, []string{"1"}, 1, time.Second) {
		t.Fatal("private scope failed")
	}
	header := &rtp.Header{Version: 2, SSRC: epochGateTestSSRC, SequenceNumber: 44}
	payload := []byte{1, 2, 3}
	if !gate.recordOriginal(header, payload, 1) {
		t.Fatal("private original rejected")
	}
	if _, err := writer.Write(header, payload, nil); err != nil {
		t.Fatal(err)
	}
	_ = waitForGateWrite(t, writes)
	if !e.setPrivateRadio(false, nil, 2, time.Second) {
		t.Fatal("public cutover failed")
	}
	sendTestNACK(t, chain, header.SequenceNumber)
	select {
	case sequence := <-writes:
		t.Fatalf("private cached payload retransmitted after public cutover: %d", sequence)
	case <-time.After(50 * time.Millisecond):
	}
}

func TestPrivateRadioFailedDrainStaysClosedAndPreservesHealthyPeer(t *testing.T) {
	e, blocked := queueOnlyEngineAndPeer("1", 1)
	_, healthy := queueOnlyEngineAndPeer("2", 1)
	healthy.engine = e
	e.peers["2"] = healthy
	if !e.setPrivateRadio(true, []string{"1"}, 1, time.Second) {
		t.Fatal("private scope failed")
	}
	blocked.writeEpoch.Store(1)
	blocked.writeInFlight.Store(true)
	if e.setPrivateRadio(false, nil, 2, 0) {
		t.Fatal("public scope succeeded before private writer drained")
	}
	if got := e.sendOpus([]byte{1}, 2, 1); got != (sendResult{}) {
		t.Fatalf("failed privacy fence reopened media: %+v", got)
	}
	if !healthy.active.Load() {
		t.Fatal("scope fence retired a healthy peer")
	}
	blocked.finishRTPWrite()
}
