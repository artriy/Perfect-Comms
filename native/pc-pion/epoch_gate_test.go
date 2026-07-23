// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"errors"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/pion/interceptor"
	"github.com/pion/interceptor/pkg/nack"
	"github.com/pion/rtcp"
	"github.com/pion/rtp"
)

const epochGateTestSSRC = 0x10203040

func nackGateChain(t *testing.T, sink interceptor.RTPWriter) (*epochGate, *interceptor.Chain, interceptor.RTPWriter) {
	t.Helper()
	gate := newEpochGate()
	factory, err := nack.NewResponderInterceptor(nack.ResponderSize(epochGateHistorySize))
	if err != nil {
		t.Fatalf("new NACK responder: %v", err)
	}
	responder, err := factory.NewInterceptor("test")
	if err != nil {
		t.Fatalf("build NACK responder: %v", err)
	}
	chain := interceptor.NewChain([]interceptor.Interceptor{gate, responder})
	t.Cleanup(func() { _ = chain.Close() })
	writer := chain.BindLocalStream(&interceptor.StreamInfo{
		SSRC:         epochGateTestSSRC,
		RTCPFeedback: []interceptor.RTCPFeedback{{Type: "nack"}},
	}, sink)
	return gate, chain, writer
}

func sendTestNACK(t *testing.T, chain *interceptor.Chain, sequence uint16) {
	t.Helper()
	packet := &rtcp.TransportLayerNack{
		MediaSSRC: epochGateTestSSRC,
		Nacks:     rtcp.NackPairsFromSequenceNumbers([]uint16{sequence}),
	}
	raw, err := packet.Marshal()
	if err != nil {
		t.Fatalf("marshal NACK: %v", err)
	}
	reader := chain.BindRTCPReader(interceptor.RTCPReaderFunc(
		func(destination []byte, attributes interceptor.Attributes) (int, interceptor.Attributes, error) {
			return copy(destination, raw), attributes, nil
		},
	))
	if _, _, err = reader.Read(make([]byte, len(raw)), nil); err != nil {
		t.Fatalf("deliver NACK: %v", err)
	}
}

func waitForGateWrite(t *testing.T, writes <-chan uint16) uint16 {
	t.Helper()
	select {
	case sequence := <-writes:
		return sequence
	case <-time.After(time.Second):
		t.Fatal("timed out waiting for RTP write")
		return 0
	}
}

func TestEpochGateAllowsCurrentPacketAndNACKResend(t *testing.T) {
	writes := make(chan uint16, 2)
	sink := interceptor.RTPWriterFunc(func(header *rtp.Header, payload []byte, _ interceptor.Attributes) (int, error) {
		writes <- header.SequenceNumber
		return len(payload), nil
	})
	gate, chain, writer := nackGateChain(t, sink)
	header := &rtp.Header{Version: 2, SSRC: epochGateTestSSRC, SequenceNumber: 65535, Timestamp: 960}
	payload := []byte{1, 2, 3}
	if !gate.recordOriginal(header, payload, 7) {
		t.Fatal("current original was rejected before write")
	}
	if _, err := writer.Write(header, payload, nil); err != nil {
		t.Fatalf("write current original: %v", err)
	}
	if got := waitForGateWrite(t, writes); got != header.SequenceNumber {
		t.Fatalf("original sequence = %d, want %d", got, header.SequenceNumber)
	}

	sendTestNACK(t, chain, header.SequenceNumber)
	if got := waitForGateWrite(t, writes); got != header.SequenceNumber {
		t.Fatalf("resent sequence = %d, want %d", got, header.SequenceNumber)
	}
}

func TestEpochGateRejectsCachedNACKAfterAdvance(t *testing.T) {
	writes := make(chan uint16, 2)
	sink := interceptor.RTPWriterFunc(func(header *rtp.Header, payload []byte, _ interceptor.Attributes) (int, error) {
		writes <- header.SequenceNumber
		return len(payload), nil
	})
	gate, chain, writer := nackGateChain(t, sink)
	header := &rtp.Header{Version: 2, SSRC: epochGateTestSSRC, SequenceNumber: 44, Timestamp: 1920}
	payload := []byte{4, 5, 6}
	if !gate.recordOriginal(header, payload, 1) {
		t.Fatal("current original was rejected before write")
	}
	if _, err := writer.Write(header, payload, nil); err != nil {
		t.Fatalf("write original: %v", err)
	}
	_ = waitForGateWrite(t, writes)

	gate.advanceEpoch(2)
	sendTestNACK(t, chain, header.SequenceNumber)
	select {
	case sequence := <-writes:
		t.Fatalf("stale NACK wrote sequence %d after epoch advance", sequence)
	case <-time.After(50 * time.Millisecond):
	}
}

func TestBlockedNACKPreventsDrainSuccessUntilDownstreamWriteExits(t *testing.T) {
	entered := make(chan struct{})
	exited := make(chan struct{})
	release := make(chan struct{})
	var releaseOnce sync.Once
	releaseWrite := func() { releaseOnce.Do(func() { close(release) }) }
	defer releaseWrite()
	var calls atomic.Uint32
	sink := interceptor.RTPWriterFunc(func(_ *rtp.Header, payload []byte, _ interceptor.Attributes) (int, error) {
		if calls.Add(1) == 1 {
			return len(payload), nil
		}
		close(entered)
		<-release
		close(exited)
		return len(payload), nil
	})
	gate, chain, writer := nackGateChain(t, sink)
	header := &rtp.Header{Version: 2, SSRC: epochGateTestSSRC, SequenceNumber: 9, Timestamp: 2880}
	payload := []byte{7, 8, 9}
	if !gate.recordOriginal(header, payload, 1) {
		t.Fatal("current original was rejected before write")
	}
	if _, err := writer.Write(header, payload, nil); err != nil {
		t.Fatalf("write original: %v", err)
	}
	sendTestNACK(t, chain, header.SequenceNumber)
	select {
	case <-entered:
	case <-time.After(time.Second):
		t.Fatal("NACK retransmission did not enter downstream writer")
	}

	e := &engine{peers: make(map[string]*peer), rtp: newRTPQueue()}
	p := &peer{
		engine: e, id: "blocked", generation: 1, instance: 1, epochGate: gate,
		outbound: make(chan outboundFrame, outboundQueueCapacity), stop: make(chan struct{}),
	}
	p.active.Store(true)
	e.peers[p.id] = p
	drained := make(chan bool, 1)
	go func() { drained <- e.advanceEpoch(2, time.Second) }()
	select {
	case <-drained:
		t.Fatal("epoch drain reported success while the old NACK write and peer were active")
	case <-time.After(20 * time.Millisecond):
	}

	p.close()
	select {
	case ok := <-drained:
		t.Fatalf("epoch drain returned %v while the closed peer still had an old NACK write", ok)
	case <-time.After(20 * time.Millisecond):
	}

	releaseWrite()
	select {
	case <-exited:
	case <-time.After(time.Second):
		t.Fatal("NACK retransmission did not exit after downstream release")
	}
	select {
	case ok := <-drained:
		if !ok {
			t.Fatal("epoch drain failed after the old NACK write exited")
		}
	case <-time.After(time.Second):
		t.Fatal("epoch drain remained blocked after the old NACK write exited")
	}
}

func TestAdvanceEpochDrainsAdmittedOriginalBeforeAdvancingGate(t *testing.T) {
	writes := make(chan uint16, 2)
	sink := interceptor.RTPWriterFunc(func(header *rtp.Header, payload []byte, _ interceptor.Attributes) (int, error) {
		writes <- header.SequenceNumber
		return len(payload), nil
	})
	gate, chain, writer := nackGateChain(t, sink)
	e := &engine{peers: make(map[string]*peer), rtp: newRTPQueue()}
	p := &peer{
		engine: e, id: "original", generation: 1, instance: 1, epochGate: gate,
		outbound: make(chan outboundFrame, outboundQueueCapacity), stop: make(chan struct{}),
	}
	p.active.Store(true)
	e.peers[p.id] = p

	header := &rtp.Header{Version: 2, SSRC: epochGateTestSSRC, SequenceNumber: 10, Timestamp: 3840}
	payload := []byte{10, 11, 12}
	p.writeEpoch.Store(1)
	p.writeInFlight.Store(true)
	if !gate.recordOriginal(header, payload, 1) {
		t.Fatal("admitted original was rejected while being recorded")
	}
	p.outbound <- outboundFrame{epoch: 1}

	// Hold the gate after recordOriginal. Queue purge proves advanceEpoch acquired
	// privacyMu; acquiring a read lock afterward proves it released privacyMu
	// without trying to advance the gate ahead of this admitted original.
	gate.mu.Lock()
	gateLocked := true
	releasePrivacy := make(chan struct{})
	var releasePrivacyOnce sync.Once
	defer func() {
		if gateLocked {
			gate.mu.Unlock()
		}
		p.finishRTPWrite()
		releasePrivacyOnce.Do(func() { close(releasePrivacy) })
	}()

	drained := make(chan bool, 1)
	go func() { drained <- e.advanceEpoch(2, time.Second) }()
	purgeDeadline := time.Now().Add(time.Second)
	for len(p.outbound) != 0 {
		if time.Now().After(purgeDeadline) {
			t.Fatal("epoch advance did not reach its peer snapshot and purge")
		}
		time.Sleep(time.Millisecond)
	}

	privacyAcquired := make(chan struct{})
	go func() {
		e.privacyMu.RLock()
		close(privacyAcquired)
		<-releasePrivacy
		e.privacyMu.RUnlock()
	}()
	select {
	case <-privacyAcquired:
	case <-time.After(time.Second):
		t.Fatal("epoch advance retained privacyMu while waiting for the admitted original")
	}

	gateLocked = false
	gate.mu.Unlock()
	if _, err := writer.Write(header, payload, nil); err != nil {
		t.Fatalf("admitted original failed its gate write: %v", err)
	}
	if got := waitForGateWrite(t, writes); got != header.SequenceNumber {
		t.Fatalf("original sequence = %d, want %d", got, header.SequenceNumber)
	}
	p.finishRTPWrite()
	releasePrivacyOnce.Do(func() { close(releasePrivacy) })

	select {
	case ok := <-drained:
		if !ok {
			t.Fatal("epoch advance failed after the admitted original drained")
		}
	case <-time.After(time.Second):
		t.Fatal("epoch advance did not finish after the admitted original drained")
	}

	sendTestNACK(t, chain, header.SequenceNumber)
	select {
	case sequence := <-writes:
		t.Fatalf("stale NACK wrote sequence %d after epoch advance", sequence)
	case <-time.After(50 * time.Millisecond):
	}
}

func TestEpochGateSequenceReuseCannotInheritOldEpoch(t *testing.T) {
	gate := newEpochGate()
	writes := make(chan uint32, 1)
	writer := gate.BindLocalStream(
		&interceptor.StreamInfo{SSRC: epochGateTestSSRC},
		interceptor.RTPWriterFunc(func(header *rtp.Header, payload []byte, _ interceptor.Attributes) (int, error) {
			writes <- header.Timestamp
			return len(payload), nil
		}),
	)
	oldHeader := &rtp.Header{Version: 2, SSRC: epochGateTestSSRC, SequenceNumber: 0, Timestamp: 100}
	oldPayload := []byte{1}
	if !gate.recordOriginal(oldHeader, oldPayload, 1) {
		t.Fatal("old original was rejected")
	}
	gate.advanceEpoch(2)
	newHeader := &rtp.Header{Version: 2, SSRC: epochGateTestSSRC, SequenceNumber: 0, Timestamp: 200}
	newPayload := []byte{2}
	if !gate.recordOriginal(newHeader, newPayload, 2) {
		t.Fatal("reused sequence was rejected for current epoch")
	}
	if _, err := writer.Write(oldHeader, oldPayload, nil); !errors.Is(err, errEpochGateRejected) {
		t.Fatalf("old packet with reused sequence error = %v, want epoch rejection", err)
	}
	if _, err := writer.Write(newHeader, newPayload, nil); err != nil {
		t.Fatalf("current packet with reused sequence: %v", err)
	}
	select {
	case got := <-writes:
		if got != newHeader.Timestamp {
			t.Fatalf("written timestamp = %d, want %d", got, newHeader.Timestamp)
		}
	case <-time.After(time.Second):
		t.Fatal("timed out waiting for current reused-sequence write")
	}
}
