// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"crypto/rand"
	"encoding/binary"
	"errors"
	"fmt"
	"math"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/pion/rtcp"
	"github.com/pion/rtp"
	"github.com/pion/webrtc/v4"
)

type outboundFrame struct {
	payload       []byte
	epoch         uint64
	mediaSequence uint64
}

type peer struct {
	engine          *engine
	id              string
	generation      uint32
	instance        uint64
	pc              *webrtc.PeerConnection
	track           *webrtc.TrackLocalStaticRTP
	sender          *webrtc.RTPSender
	estimator       *feedbackAwareEstimator
	minEpoch        atomic.Uint64
	relayOnly       atomic.Bool
	active          atomic.Bool
	sentRTP         atomic.Bool
	remoteFeedback  remoteSenderFeedback
	outbound        chan outboundFrame
	stop            chan struct{}
	closeOnce       sync.Once
	workerMu        sync.Mutex
	workersClosed   bool
	wg              sync.WaitGroup
	signalMu        sync.Mutex
	remoteSet       bool
	pendingICE      []webrtc.ICECandidateInit
	candidateMu     sync.Mutex
	holdCandidates  bool
	localCandidates []string
	sequenceBase    uint16
	timestampBase   uint32
}

func randomRTPBases() (uint16, uint32) {
	var data [6]byte
	if _, err := rand.Read(data[:]); err != nil {
		seed := uint64(time.Now().UnixNano())
		return uint16(seed), uint32(seed >> 16)
	}
	return binary.BigEndian.Uint16(data[:2]), binary.BigEndian.Uint32(data[2:])
}

func newPeer(
	engine *engine,
	id string,
	generation uint32,
	instance uint64,
	minEpoch uint64,
	relayOnly bool,
	pc *webrtc.PeerConnection,
	track *webrtc.TrackLocalStaticRTP,
	sender *webrtc.RTPSender,
	estimator *feedbackAwareEstimator,
) *peer {
	sequence, timestamp := randomRTPBases()
	p := &peer{
		engine: engine, id: id, generation: generation, instance: instance,
		pc: pc, track: track, sender: sender, estimator: estimator,
		outbound: make(chan outboundFrame, outboundQueueCapacity),
		stop:     make(chan struct{}), sequenceBase: sequence, timestampBase: timestamp,
	}
	p.minEpoch.Store(minEpoch)
	p.relayOnly.Store(relayOnly)
	p.active.Store(true)
	return p
}

func (p *peer) raiseMinEpoch(epoch uint64) {
	for {
		current := p.minEpoch.Load()
		if epoch <= current || p.minEpoch.CompareAndSwap(current, epoch) {
			return
		}
	}
}

func (p *peer) start() {
	p.pc.OnConnectionStateChange(func(state webrtc.PeerConnectionState) {
		p.emit(controlEvent{
			Kind: "state", PeerID: p.id, Generation: p.generation, State: state.String(),
		})
	})
	p.pc.OnICECandidate(func(candidate *webrtc.ICECandidate) {
		if !p.engine.isCurrent(p) {
			return
		}
		value := ""
		if candidate != nil {
			value = candidate.ToJSON().Candidate
		}
		p.emitOrBufferCandidate(value)
	})
	p.pc.OnTrack(func(track *webrtc.TrackRemote, receiver *webrtc.RTPReceiver) {
		if !strings.EqualFold(track.Codec().MimeType, webrtc.MimeTypeOpus) {
			return
		}
		p.startWorker(func() { p.readRTP(track) })
		// Draining receiver-side RTCP lets Pion's report interceptor observe
		// sender reports, so outbound Receiver Reports carry LSR/DLSR for RTT.
		p.startWorker(func() { p.readReceiverRTCP(receiver) })
	})
	p.startWorker(p.writeRTP)
	p.startWorker(p.readRTCP)
	p.startWorker(p.sampleStats)
}

func (p *peer) startWorker(worker func()) bool {
	p.workerMu.Lock()
	if p.workersClosed {
		p.workerMu.Unlock()
		return false
	}
	p.wg.Add(1)
	p.workerMu.Unlock()
	go func() {
		defer p.wg.Done()
		worker()
	}()
	return true
}

func (p *peer) emit(event controlEvent) bool {
	return p.engine.emitPeerControl(p, event)
}

func (p *peer) holdLocalCandidates() {
	p.candidateMu.Lock()
	p.holdCandidates = true
	p.localCandidates = p.localCandidates[:0]
	p.candidateMu.Unlock()
}

func (p *peer) emitOrBufferCandidate(candidate string) {
	p.candidateMu.Lock()
	if p.holdCandidates {
		if len(p.localCandidates) >= maxPendingCandidates {
			p.candidateMu.Unlock()
			p.fail("local ICE candidate buffer exhausted")
			return
		}
		p.localCandidates = append(p.localCandidates, candidate)
		p.candidateMu.Unlock()
		return
	}
	p.emit(candidateControlEvent(p.id, p.generation, candidate))
	p.candidateMu.Unlock()
}

func (p *peer) releaseLocalCandidates() {
	p.candidateMu.Lock()
	for _, candidate := range p.localCandidates {
		if !p.emit(candidateControlEvent(p.id, p.generation, candidate)) {
			break
		}
	}
	p.localCandidates = p.localCandidates[:0]
	p.holdCandidates = false
	p.candidateMu.Unlock()
}

func (p *peer) discardLocalCandidates() {
	p.candidateMu.Lock()
	p.localCandidates = p.localCandidates[:0]
	p.holdCandidates = false
	p.candidateMu.Unlock()
}

func candidateControlEvent(peerID string, generation uint32, candidate string) controlEvent {
	value := candidate
	return controlEvent{
		Kind: "candidate", PeerID: peerID, Generation: generation, Candidate: &value,
	}
}

func trickleOptions() webrtc.OfferAnswerOptions {
	return webrtc.OfferAnswerOptions{ICETricklingSupported: true}
}

func (p *peer) createOffer(restart bool) error {
	p.signalMu.Lock()
	defer p.signalMu.Unlock()
	return p.createOfferLocked(restart)
}

func (p *peer) createOfferLocked(restart bool) error {
	if !p.engine.isCurrent(p) {
		return errors.New("stale peer")
	}
	p.holdLocalCandidates()
	offer, err := p.pc.CreateOffer(&webrtc.OfferOptions{
		OfferAnswerOptions: trickleOptions(),
		ICERestart:         restart,
	})
	if err != nil {
		p.discardLocalCandidates()
		return fmt.Errorf("create offer: %w", err)
	}
	if err = p.pc.SetLocalDescription(offer); err != nil {
		p.discardLocalCandidates()
		return fmt.Errorf("set local offer: %w", err)
	}
	local := p.pc.LocalDescription()
	if local == nil {
		p.discardLocalCandidates()
		return errors.New("local offer unavailable")
	}
	// Candidates for the new remote description must not be applied against
	// an older answer while an ICE restart is in flight.
	p.remoteSet = false
	p.pendingICE = p.pendingICE[:0]
	p.emit(controlEvent{
		Kind: "sdp", PeerID: p.id, Generation: p.generation,
		SDPType: local.Type.String(), SDP: local.SDP,
	})
	p.releaseLocalCandidates()
	return nil
}

func (p *peer) setRemoteSDP(sdpType, sdp string) error {
	p.signalMu.Lock()
	defer p.signalMu.Unlock()
	if !p.engine.isCurrent(p) {
		return errors.New("stale peer")
	}
	typ := webrtc.NewSDPType(sdpType)
	if typ != webrtc.SDPTypeOffer && typ != webrtc.SDPTypeAnswer {
		return fmt.Errorf("invalid SDP type %q", sdpType)
	}
	isOffer := typ == webrtc.SDPTypeOffer
	if isOffer {
		p.holdLocalCandidates()
	}
	if err := p.pc.SetRemoteDescription(webrtc.SessionDescription{Type: typ, SDP: sdp}); err != nil {
		if isOffer {
			p.discardLocalCandidates()
		}
		return fmt.Errorf("set remote SDP: %w", err)
	}
	p.remoteSet = true
	pending := append([]webrtc.ICECandidateInit(nil), p.pendingICE...)
	p.pendingICE = p.pendingICE[:0]
	for _, candidate := range pending {
		if err := p.pc.AddICECandidate(candidate); err != nil {
			if isOffer {
				p.discardLocalCandidates()
			}
			return fmt.Errorf("apply buffered candidate: %w", err)
		}
	}
	if !isOffer {
		return nil
	}
	answer, err := p.pc.CreateAnswer(&webrtc.AnswerOptions{OfferAnswerOptions: trickleOptions()})
	if err != nil {
		p.discardLocalCandidates()
		return fmt.Errorf("create answer: %w", err)
	}
	if err = p.pc.SetLocalDescription(answer); err != nil {
		p.discardLocalCandidates()
		return fmt.Errorf("set local answer: %w", err)
	}
	local := p.pc.LocalDescription()
	if local == nil {
		p.discardLocalCandidates()
		return errors.New("local answer unavailable")
	}
	p.emit(controlEvent{
		Kind: "sdp", PeerID: p.id, Generation: p.generation,
		SDPType: local.Type.String(), SDP: local.SDP,
	})
	p.releaseLocalCandidates()
	return nil
}

func (p *peer) addICECandidate(candidate string) error {
	p.signalMu.Lock()
	defer p.signalMu.Unlock()
	if !p.engine.isCurrent(p) {
		return errors.New("stale peer")
	}
	init := webrtc.ICECandidateInit{Candidate: candidate}
	if !p.remoteSet {
		if len(p.pendingICE) >= maxPendingCandidates {
			return errors.New("remote ICE candidate buffer exhausted")
		}
		p.pendingICE = append(p.pendingICE, init)
		return nil
	}
	return p.pc.AddICECandidate(init)
}

func (p *peer) restartICE(relayOnly, createOffer bool) error {
	p.signalMu.Lock()
	defer p.signalMu.Unlock()
	if !p.engine.isCurrent(p) {
		return errors.New("stale peer")
	}
	if err := p.pc.SetConfiguration(p.engine.configuration(relayOnly)); err != nil {
		return fmt.Errorf("set restart configuration: %w", err)
	}
	p.relayOnly.Store(relayOnly)
	if createOffer {
		return p.createOfferLocked(true)
	}
	return nil
}

func (p *peer) writeRTP() {
	var firstMediaSequence uint64
	var haveFirst bool
	for {
		select {
		case <-p.stop:
			return
		case frame := <-p.outbound:
			p.engine.privacyMu.RLock()
			if !p.engine.isCurrent(p) || frame.epoch < p.engine.privacyFloor.Load() || frame.epoch < p.minEpoch.Load() {
				p.engine.privacyMu.RUnlock()
				p.engine.counters.rtpTXStaleEpochDropped.Add(1)
				continue
			}
			if !p.readyToSendRTP() {
				p.engine.privacyMu.RUnlock()
				continue
			}
			if !haveFirst {
				firstMediaSequence = frame.mediaSequence
				haveFirst = true
			}
			delta := frame.mediaSequence - firstMediaSequence
			packet := &rtp.Packet{
				Header: rtp.Header{
					Version:        2,
					PayloadType:    111,
					SequenceNumber: p.sequenceBase + uint16(delta),
					Timestamp:      p.timestampBase + uint32(delta*960),
				},
				Payload: frame.payload,
			}
			started := time.Now()
			err := p.track.WriteRTP(packet)
			elapsed := time.Since(started)
			p.engine.privacyMu.RUnlock()
			if elapsed > writeTimeout {
				p.engine.counters.rtpTXWriteTimeouts.Add(1)
			}
			if err != nil {
				p.engine.counters.rtpTXErrors.Add(1)
			} else {
				p.sentRTP.Store(true)
				p.engine.counters.rtpTXOK.Add(1)
			}
		}
	}
}

func (p *peer) readyToSendRTP() bool {
	if p.pc.ConnectionState() != webrtc.PeerConnectionStateConnected {
		return false
	}
	for _, encoding := range p.sender.GetParameters().Encodings {
		if encoding.SSRC != 0 {
			return true
		}
	}
	return false
}

func (p *peer) readRTCP() {
	for {
		packets, _, err := p.sender.ReadRTCP()
		if err != nil {
			return
		}
		p.recordReceiverReports(packets, time.Now())
	}
}

func (p *peer) recordReceiverReports(packets []rtcp.Packet, now time.Time) {
	parameters := p.sender.GetParameters()
	senderSSRCs := make(map[uint32]struct{}, len(parameters.Encodings))
	for _, encoding := range parameters.Encodings {
		senderSSRCs[uint32(encoding.SSRC)] = struct{}{}
	}
	for _, packet := range packets {
		receiverReport, ok := packet.(*rtcp.ReceiverReport)
		if !ok {
			continue
		}
		for _, report := range receiverReport.Reports {
			if _, ok = senderSSRCs[report.SSRC]; !ok {
				continue
			}
			p.remoteFeedback.record(report, now, p.sequenceBase, p.sentRTP.Load())
		}
	}
}

func (p *peer) readRTP(track *webrtc.TrackRemote) {
	for {
		packet, _, err := track.ReadRTP()
		if err != nil {
			return
		}
		payload := append([]byte(nil), packet.Payload...)
		if !p.engine.enqueuePeerRTP(p, inboundRTP{
			peerID: p.id, generation: p.generation,
			sequence: packet.SequenceNumber, timestamp: packet.Timestamp,
			arrival: time.Now(), payload: payload,
		}) {
			p.engine.counters.staleRTPRXDropped.Add(1)
			continue
		}
		p.engine.counters.rtpRXPackets.Add(1)
		p.engine.counters.rtpRXBytes.Add(uint64(len(payload)))
	}
}

func (p *peer) readReceiverRTCP(receiver *webrtc.RTPReceiver) {
	for {
		if _, _, err := receiver.ReadRTCP(); err != nil {
			return
		}
	}
}

func finiteNonnegative(value float64) float64 {
	if math.IsNaN(value) || math.IsInf(value, 0) || value < 0 {
		return 0
	}
	return value
}

func (p *peer) sampleStats() {
	ticker := time.NewTicker(statsInterval)
	defer ticker.Stop()
	for {
		select {
		case <-p.stop:
			return
		case <-ticker.C:
			if event, ok := p.statsEvent(); ok {
				p.emit(event)
			}
		}
	}
}

func (p *peer) fail(message string) {
	p.emit(controlEvent{
		Kind: "error", PeerID: p.id, Generation: p.generation, Message: message,
	})
	p.emit(controlEvent{
		Kind: "state", PeerID: p.id, Generation: p.generation, State: "failed",
	})
}

func (p *peer) close() {
	p.closeOnce.Do(func() {
		p.active.Store(false)
		p.workerMu.Lock()
		p.workersClosed = true
		p.workerMu.Unlock()
		close(p.stop)
		_ = p.pc.Close()
		p.wg.Wait()
	})
}

func (p *peer) purgeOutboundBelow(epoch uint64) int {
	kept := make([]outboundFrame, 0, cap(p.outbound))
	dropped := 0
	for {
		select {
		case frame := <-p.outbound:
			if frame.epoch < epoch {
				dropped++
			} else {
				kept = append(kept, frame)
			}
		default:
			for _, frame := range kept {
				p.outbound <- frame
			}
			return dropped
		}
	}
}
