// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"errors"
	"fmt"
	"net"
	"runtime"
	"runtime/debug"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"

	"github.com/pion/ice/v4"
)

type panickingUDPMux struct{}

func (panickingUDPMux) Close() error { panic("close panic") }
func (panickingUDPMux) GetConn(string, net.Addr) (net.PacketConn, error) {
	return nil, errors.New("unused")
}
func (panickingUDPMux) RemoveConnByUfrag(string)       {}
func (panickingUDPMux) GetListenAddresses() []net.Addr { return nil }

var _ ice.UDPMux = panickingUDPMux{}

func TestABIHandleLifecycle(t *testing.T) {
	if got := uint32(pc_pion_abi_version()); got != pionABIVersion {
		t.Fatalf("ABI version = %d, want %d", got, pionABIVersion)
	}
	if got := uint32(pc_pion_version()); got != pionVersion {
		t.Fatalf("Pion version = %d, want %d", got, pionVersion)
	}
	handle := pc_pion_engine_new()
	if handle == 0 {
		t.Fatal("engine_new returned zero")
	}
	if got := int32(pc_pion_engine_close(handle)); got != statusOK {
		t.Fatalf("first close status = %d", got)
	}
	if got := int32(pc_pion_engine_close(handle)); got != statusHandle {
		t.Fatalf("second close status = %d, want %d", got, statusHandle)
	}
	if _, code := registeredEngine(handle); code != statusHandle {
		t.Fatalf("closed handle lookup status = %d", code)
	}
}

func TestPionContractMarkerMatchesABIAndVersion(t *testing.T) {
	want := strings.Join([]string{
		"PERFECT", "COMMS_PC_PION_ABI=", strconv.Itoa(pionABIVersion),
		";PION=", strconv.Itoa(int(pionVersion / 1_000_000)), ".",
		strconv.Itoa(int(pionVersion / 1_000 % 1_000)), ".",
		strconv.Itoa(int(pionVersion % 1_000)),
	}, "")
	if got := pionContractMarkerText(); got != want {
		t.Fatalf("Pion contract marker = %q, want %q", got, want)
	}
}

func TestPionVersionMatchesLockedWebRTCModule(t *testing.T) {
	want := "v" + strings.Join([]string{
		strconv.Itoa(int(pionVersion / 1_000_000)),
		strconv.Itoa(int(pionVersion / 1_000 % 1_000)),
		strconv.Itoa(int(pionVersion % 1_000)),
	}, ".")
	buildInfo, ok := debug.ReadBuildInfo()
	if !ok {
		t.Fatal("Go build information is unavailable")
	}
	for _, dependency := range buildInfo.Deps {
		if dependency.Path != "github.com/pion/webrtc/v4" {
			continue
		}
		if dependency.Replace != nil {
			dependency = dependency.Replace
		}
		if dependency.Version != want {
			t.Fatalf("locked Pion WebRTC module = %q, want %q", dependency.Version, want)
		}
		return
	}
	t.Fatal("locked Pion WebRTC module is absent from Go build information")
}

func TestABIPanicRecoveryAndEarlyHandleRemoval(t *testing.T) {
	const handle = 0xf00d1234
	e := &engine{
		peers: make(map[string]*peer), rtp: newRTPQueue(),
		udpMux: panickingUDPMux{}, muxClosing: &atomic.Bool{},
	}
	engineRegistry.Lock()
	engineRegistry.engines[handle] = e
	engineRegistry.Unlock()
	if got := int32(pc_pion_engine_close(handle)); got != statusInternal {
		t.Fatalf("panic recovery status = %d, want %d", got, statusInternal)
	}
	if _, code := registeredEngine(handle); code != statusHandle {
		t.Fatal("panicking close left the handle registered")
	}
}

func TestABIPollControlBufferRetryDoesNotConsume(t *testing.T) {
	var q controlQueue
	want := controlEvent{Kind: "error", PeerID: "peer", Generation: 4, Message: "buffer-retry"}
	q.push(want)
	required, code := pollControlData(&q, 1, nil)
	if code != statusBufferTooSmall || required <= 1 || len(q.events) != 1 {
		t.Fatalf("small poll required/status/len = %d/%d/%d", required, code, len(q.events))
	}
	buffer := make([]byte, required)
	requiredAgain, code := pollControlData(&q, uint32(len(buffer)), func(data []byte) { copy(buffer, data) })
	if code != statusOK || requiredAgain != required || len(q.events) != 0 {
		t.Fatalf("retry required/status/len = %d/%d/%d", requiredAgain, code, len(q.events))
	}
	got := decodeControlForTest(t, buffer)
	if got.Message != want.Message || got.PeerID != want.PeerID {
		t.Fatalf("retried event = %+v", got)
	}
}

func TestABIPollRTPBufferRetryDoesNotConsume(t *testing.T) {
	q := newRTPQueue()
	want := testRTP("peer-name", 42)
	q.push(want)
	packet, overflow, code := pollRTPData(q, 1, 1, false, false, nil)
	if code != statusBufferTooSmall || packet.peerID != want.peerID || overflow != 0 || q.queued != 1 {
		t.Fatalf("small RTP poll packet/overflow/status/queued = %+v/%d/%d/%d", packet, overflow, code, q.queued)
	}
	var peer string
	var payload []byte
	packetAgain, _, code := pollRTPData(q, uint32(len(want.peerID)), uint32(len(want.payload)), true, true, func(gotPeer string, gotPayload []byte) {
		peer = gotPeer
		payload = append([]byte(nil), gotPayload...)
	})
	if code != statusOK || packetAgain.queueID != packet.queueID || q.queued != 0 {
		t.Fatalf("RTP retry packet/status/queued = %+v/%d/%d", packetAgain, code, q.queued)
	}
	if peer != want.peerID || len(payload) != 1 || payload[0] != want.payload[0] {
		t.Fatalf("RTP retry data = %q/%v", peer, payload)
	}
}

func useSingleSchedulerForQueueRace(t *testing.T) {
	t.Helper()
	previous := runtime.GOMAXPROCS(1)
	t.Cleanup(func() { runtime.GOMAXPROCS(previous) })
}

func mutationCompletedAfterYield(started, done <-chan struct{}) bool {
	<-started
	runtime.Gosched()
	select {
	case <-done:
		return true
	default:
		return false
	}
}

func TestABIPollControlSerializesCoalescingWithDelivery(t *testing.T) {
	useSingleSchedulerForQueueRace(t)

	var q controlQueue
	q.push(controlEvent{Kind: "state", PeerID: "peer", Generation: 7, State: "old"})

	sinkEntered := make(chan struct{})
	releaseSink := make(chan struct{})
	type pollResult struct {
		required uint32
		status   int
	}
	pollDone := make(chan pollResult, 1)
	var delivered []byte
	go func() {
		required, status := pollControlData(&q, 1024, func(data []byte) {
			close(sinkEntered)
			<-releaseSink
			delivered = append([]byte(nil), data...)
		})
		pollDone <- pollResult{required: required, status: status}
	}()
	<-sinkEntered

	mutationStarted := make(chan struct{})
	mutationDone := make(chan struct{})
	go func() {
		close(mutationStarted)
		q.push(controlEvent{Kind: "state", PeerID: "peer", Generation: 7, State: "new"})
		close(mutationDone)
	}()
	if mutationCompletedAfterYield(mutationStarted, mutationDone) {
		close(releaseSink)
		<-pollDone
		t.Fatal("control coalescing completed between delivery and dequeue")
	}

	close(releaseSink)
	result := <-pollDone
	<-mutationDone
	if result.status != statusOK || result.required != uint32(len(delivered)) {
		t.Fatalf("control poll required/status = %d/%d", result.required, result.status)
	}
	if got := decodeControlForTest(t, delivered); got.State != "old" {
		t.Fatalf("delivered state = %q, want old", got.State)
	}
	_, remaining := q.peek()
	if got := decodeControlForTest(t, remaining); got.State != "new" {
		t.Fatalf("queued state = %q, want new", got.State)
	}
}

func TestABIPollRTPSerializesRemovalWithDelivery(t *testing.T) {
	useSingleSchedulerForQueueRace(t)

	q := newRTPQueue()
	q.push(testRTP("remove", 1))
	q.push(testRTP("keep", 2))

	sinkEntered := make(chan struct{})
	releaseSink := make(chan struct{})
	type pollResult struct {
		packet inboundRTP
		status int
	}
	pollDone := make(chan pollResult, 1)
	var deliveredPeer string
	go func() {
		packet, _, status := pollRTPData(q, 32, 32, true, true, func(peer string, _ []byte) {
			close(sinkEntered)
			<-releaseSink
			deliveredPeer = peer
		})
		pollDone <- pollResult{packet: packet, status: status}
	}()
	<-sinkEntered

	mutationStarted := make(chan struct{})
	mutationDone := make(chan struct{})
	go func() {
		close(mutationStarted)
		q.removePeer("remove")
		close(mutationDone)
	}()
	if mutationCompletedAfterYield(mutationStarted, mutationDone) {
		close(releaseSink)
		<-pollDone
		t.Fatal("peer removal completed between RTP delivery and dequeue")
	}

	close(releaseSink)
	result := <-pollDone
	<-mutationDone
	if result.status != statusOK || result.packet.peerID != "remove" || deliveredPeer != "remove" {
		t.Fatalf("RTP removal poll packet/delivery/status = %q/%q/%d", result.packet.peerID, deliveredPeer, result.status)
	}
	packet, _, ok := q.peek()
	if !ok || packet.peerID != "keep" || q.queued != 1 {
		t.Fatalf("remaining RTP packet/queued = %+v/%d", packet, q.queued)
	}
}

func TestABIPollRTPSerializesOverflowWithDelivery(t *testing.T) {
	useSingleSchedulerForQueueRace(t)

	q := newRTPQueue()
	for i := 0; i < ingressQueueCapacity; i++ {
		q.push(testRTP(fmt.Sprintf("peer-%03d", i), uint16(i+1)))
	}

	sinkEntered := make(chan struct{})
	releaseSink := make(chan struct{})
	type pollResult struct {
		packet   inboundRTP
		overflow uint64
		status   int
	}
	pollDone := make(chan pollResult, 1)
	var deliveredPeer string
	go func() {
		packet, overflow, status := pollRTPData(q, 32, 32, true, true, func(peer string, _ []byte) {
			close(sinkEntered)
			<-releaseSink
			deliveredPeer = peer
		})
		pollDone <- pollResult{packet: packet, overflow: overflow, status: status}
	}()
	<-sinkEntered

	mutationStarted := make(chan struct{})
	mutationDone := make(chan struct{})
	go func() {
		close(mutationStarted)
		q.push(testRTP("extra", 600))
		close(mutationDone)
	}()
	if mutationCompletedAfterYield(mutationStarted, mutationDone) {
		close(releaseSink)
		<-pollDone
		t.Fatal("RTP overflow eviction completed between delivery and dequeue")
	}

	close(releaseSink)
	result := <-pollDone
	<-mutationDone
	if result.status != statusOK || result.packet.peerID != "peer-000" || deliveredPeer != "peer-000" {
		t.Fatalf("RTP overflow poll packet/delivery/status = %q/%q/%d", result.packet.peerID, deliveredPeer, result.status)
	}
	if result.overflow != 0 || q.overflow != 0 {
		t.Fatalf("overflow snapshot/current = %d/%d, want 0/0", result.overflow, q.overflow)
	}
	if q.queued != ingressQueueCapacity || len(q.queues["extra"]) != 1 {
		t.Fatalf("post-poll queued/extra = %d/%d", q.queued, len(q.queues["extra"]))
	}
}
