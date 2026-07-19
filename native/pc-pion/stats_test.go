// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"math"
	"sync"
	"testing"
	"time"

	"github.com/pion/rtcp"
)

func ntpDurationUnits(value time.Duration) uint32 {
	return uint32(uint64(value) * 65_536 / uint64(time.Second))
}

func reportWithRTT(now time.Time, delay, rtt time.Duration) rtcp.ReceptionReport {
	delayUnits := ntpDurationUnits(delay)
	rttUnits := ntpDurationUnits(rtt)
	return rtcp.ReceptionReport{
		LastSenderReport: ntpMiddle32(now) - delayUnits - rttUnits,
		Delay:            delayUnits,
	}
}

func TestRemoteSenderFeedbackRecordsReceiverReport(t *testing.T) {
	now := time.Date(2026, time.July, 19, 12, 34, 56, 789_000_000, time.UTC)
	report := reportWithRTT(now, 120*time.Millisecond, 80*time.Millisecond)
	report.LastSequenceNumber = 1<<16 | 7
	report.TotalLost = 44
	report.FractionLost = 64

	feedback := &remoteSenderFeedback{}
	feedback.record(report, now, 65_000, true)
	snapshot := feedback.snapshot()
	if !snapshot.valid {
		t.Fatal("receiver report did not produce a valid snapshot")
	}
	if snapshot.packetsReceived != 500 {
		t.Fatalf("packets received = %d, want 500", snapshot.packetsReceived)
	}
	if snapshot.packetsLost != 44 {
		t.Fatalf("packets lost = %d, want 44", snapshot.packetsLost)
	}
	if snapshot.fractionLost != 0.25 {
		t.Fatalf("fraction lost = %v, want 0.25", snapshot.fractionLost)
	}
	if math.Abs(snapshot.rttMS-80) > 0.1 {
		t.Fatalf("RTT = %.3f ms, want about 80 ms", snapshot.rttMS)
	}
	if snapshot.rttMeasurements != 1 {
		t.Fatalf("RTT measurements = %d, want 1", snapshot.rttMeasurements)
	}
}

func TestRemoteSenderFeedbackPreservesSignedCumulativeLoss(t *testing.T) {
	feedback := &remoteSenderFeedback{}
	feedback.record(rtcp.ReceptionReport{
		LastSequenceNumber: 109,
		TotalLost:          0x00ff_ffff,
	}, time.Time{}, 100, true)
	snapshot := feedback.snapshot()
	if snapshot.packetsLost != -1 {
		t.Fatalf("packets lost = %d, want -1", snapshot.packetsLost)
	}
	if snapshot.packetsReceived != 11 {
		t.Fatalf("packets received = %d, want 11", snapshot.packetsReceived)
	}
}

func TestRemoteSenderFeedbackRejectsRegressiveAndInvalidReports(t *testing.T) {
	now := time.Date(2026, time.July, 19, 13, 0, 0, 0, time.UTC)
	feedback := &remoteSenderFeedback{}
	latest := reportWithRTT(now, 100*time.Millisecond, 50*time.Millisecond)
	latest.LastSequenceNumber = 1_100
	latest.TotalLost = 10
	latest.FractionLost = 32
	feedback.record(latest, now, 1_000, true)

	older := reportWithRTT(now, 100*time.Millisecond, 500*time.Millisecond)
	older.LastSequenceNumber = 1_099
	older.TotalLost = 99
	older.FractionLost = 255
	feedback.record(older, now, 1_000, true)

	invalid := rtcp.ReceptionReport{
		LastSequenceNumber: 1_101,
		TotalLost:          11,
		FractionLost:       16,
		LastSenderReport:   ntpMiddle32(now) - 10,
		Delay:              20,
	}
	feedback.record(invalid, now, 1_000, true)

	snapshot := feedback.snapshot()
	if snapshot.packetsReceived != 91 || snapshot.packetsLost != 11 || snapshot.fractionLost != 0.0625 {
		t.Fatalf("latest cumulative snapshot = %+v", snapshot)
	}
	if math.Abs(snapshot.rttMS-50) > 0.1 || snapshot.rttMeasurements != 1 {
		t.Fatalf("RTT snapshot after stale/invalid reports = %+v", snapshot)
	}
}

func TestReceiverReportRTTRejectsFutureSenderReport(t *testing.T) {
	now := time.Date(2026, time.July, 19, 13, 30, 0, 0, time.UTC)
	report := rtcp.ReceptionReport{
		LastSenderReport: ntpMiddle32(now) + ntpDurationUnits(100*time.Millisecond),
		Delay:            ntpDurationUnits(10 * time.Millisecond),
	}
	if rtt, ok := receiverReportRTT(now, report); ok {
		t.Fatalf("future sender report produced RTT %v", rtt)
	}
}

func TestReceiverReportRTTAcceptsZeroReceiverDelay(t *testing.T) {
	now := time.Date(2026, time.July, 19, 13, 45, 0, 0, time.UTC)
	report := rtcp.ReceptionReport{
		LastSenderReport: ntpMiddle32(now) - ntpDurationUnits(25*time.Millisecond),
	}
	rtt, ok := receiverReportRTT(now, report)
	if !ok || math.Abs(float64(rtt-25*time.Millisecond)) > float64(100*time.Microsecond) {
		t.Fatalf("zero-delay receiver report RTT = %v/%v, want about 25ms/true", rtt, ok)
	}
}

func TestRemoteSenderFeedbackConcurrentRecordAndSnapshot(t *testing.T) {
	now := time.Date(2026, time.July, 19, 14, 0, 0, 0, time.UTC)
	feedback := &remoteSenderFeedback{}
	const reports = 128
	var wg sync.WaitGroup
	for i := range reports {
		wg.Add(2)
		go func(sequence uint32) {
			defer wg.Done()
			report := reportWithRTT(now, 20*time.Millisecond, 10*time.Millisecond)
			report.LastSequenceNumber = 10_000 + sequence
			feedback.record(report, now, 10_000, true)
		}(uint32(i))
		go func() {
			defer wg.Done()
			_ = feedback.snapshot()
		}()
	}
	wg.Wait()
	snapshot := feedback.snapshot()
	if !snapshot.valid || snapshot.packetsReceived != reports {
		t.Fatalf("concurrent final snapshot = %+v", snapshot)
	}
}
