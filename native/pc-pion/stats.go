// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"fmt"
	"sync"
	"time"

	"github.com/pion/rtcp"
	"github.com/pion/webrtc/v4"
)

const ntpEpochOffsetSeconds = 2_208_988_800

type remoteSenderSnapshot struct {
	valid           bool
	packetsReceived uint64
	packetsLost     int64
	fractionLost    float64
	rttMS           float64
	rttMeasurements uint64
}

type remoteSenderFeedback struct {
	mu              sync.RWMutex
	valid           bool
	highestSequence uint32
	packetsReceived uint64
	packetsLost     int64
	fractionLost    float64
	rttMS           float64
	rttMeasurements uint64
}

func signed24(value uint32) int64 {
	value &= 0x00ff_ffff
	if value&0x0080_0000 != 0 {
		return int64(value) - 1<<24
	}
	return int64(value)
}

func ntpMiddle32(value time.Time) uint32 {
	seconds := uint64(value.Unix() + ntpEpochOffsetSeconds)
	fraction := (uint64(value.Nanosecond()) << 32) / uint64(time.Second)
	return uint32(seconds<<16) | uint32(fraction>>16)
}

func receiverReportRTT(now time.Time, report rtcp.ReceptionReport) (time.Duration, bool) {
	// A zero LSR means the receiver has not observed a Sender Report. DLSR may
	// legitimately be zero when the Receiver Report follows it immediately.
	if report.LastSenderReport == 0 {
		return 0, false
	}
	elapsed := ntpMiddle32(now) - report.LastSenderReport
	// A short NTP timestamp wraps every 18 hours. Values in the upper half of
	// that range represent a future/invalid sender report for live voice.
	if elapsed >= 1<<31 || elapsed < report.Delay {
		return 0, false
	}
	rttUnits := elapsed - report.Delay
	return time.Duration(uint64(rttUnits) * uint64(time.Second) / 65_536), true
}

func (feedback *remoteSenderFeedback) record(
	report rtcp.ReceptionReport,
	now time.Time,
	firstSequence uint16,
	haveSentRTP bool,
) {
	rtt, hasRTT := receiverReportRTT(now, report)
	feedback.mu.Lock()
	defer feedback.mu.Unlock()

	// SRTCP can deliver valid packets out of order. Do not let an older report
	// regress cumulative sender feedback or turn network reordering into RTT.
	if haveSentRTP && feedback.valid && report.LastSequenceNumber < feedback.highestSequence {
		return
	}
	if haveSentRTP && report.LastSequenceNumber >= uint32(firstSequence) &&
		(!feedback.valid || report.LastSequenceNumber >= feedback.highestSequence) {
		lost := signed24(report.TotalLost)
		expected := int64(uint64(report.LastSequenceNumber)-uint64(firstSequence)) + 1
		received := expected - lost
		if received < 0 {
			received = 0
		}
		feedback.valid = true
		feedback.highestSequence = report.LastSequenceNumber
		feedback.packetsReceived = uint64(received)
		feedback.packetsLost = lost
		feedback.fractionLost = float64(report.FractionLost) / 256
	}
	if hasRTT {
		feedback.rttMS = float64(rtt) / float64(time.Millisecond)
		feedback.rttMeasurements++
	}
}

func (feedback *remoteSenderFeedback) snapshot() remoteSenderSnapshot {
	feedback.mu.RLock()
	defer feedback.mu.RUnlock()
	return remoteSenderSnapshot{
		valid:           feedback.valid,
		packetsReceived: feedback.packetsReceived,
		packetsLost:     feedback.packetsLost,
		fractionLost:    feedback.fractionLost,
		rttMS:           feedback.rttMS,
		rttMeasurements: feedback.rttMeasurements,
	}
}

func (p *peer) statsEvent() (controlEvent, bool) {
	event := controlEvent{Kind: "stats", PeerID: p.id, Generation: p.generation}
	transport := p.sender.Transport()
	if transport == nil || transport.ICETransport() == nil {
		return event, true
	}
	iceTransport := transport.ICETransport()
	pair, err := iceTransport.GetSelectedCandidatePair()
	if err == nil && pair != nil && pair.Local != nil && pair.Remote != nil {
		event.CandidatePairID = fmt.Sprintf("%s-%s", pair.Local.Foundation, pair.Remote.Foundation)
		event.LocalCandidateType = pair.Local.Typ.String()
		event.RemoteCandidateType = pair.Remote.Typ.String()
		event.Relay = pair.Local.Typ == webrtc.ICECandidateTypeRelay ||
			pair.Remote.Typ == webrtc.ICECandidateTypeRelay
		if pairStats, ok := iceTransport.GetSelectedCandidatePairStats(); ok {
			event.CandidateState = string(pairStats.State)
			event.CurrentRTTMS = finiteNonnegative(pairStats.CurrentRoundTripTime * 1000)
			event.AvailableIncomingBitrate = finiteNonnegative(pairStats.AvailableIncomingBitrate)
		}
	}

	if p.estimator != nil && p.estimator.hasFeedback() {
		bitrate := p.estimator.GetTargetBitrate()
		if bitrate > 0 {
			event.BandwidthEstimateValid = true
			event.AvailableOutgoingBitrate = float64(bitrate)
		}
	}

	remote := p.remoteFeedback.snapshot()
	if remote.valid {
		event.RemotePacketsReceived = remote.packetsReceived
		event.RemotePacketsLost = remote.packetsLost
		event.RemoteFractionLost = remote.fractionLost
	}
	event.RemoteReportRTTMS = remote.rttMS
	event.RemoteRTTMeasurements = remote.rttMeasurements
	return event, true
}
