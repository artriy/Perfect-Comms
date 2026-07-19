// SPDX-License-Identifier: LGPL-2.1-only

package main

/*
#include <stdint.h>
#include <stddef.h>

extern const char PC_PION_CONTRACT_MARKER[];

static inline const char *pc_pion_contract_marker_data(void) {
    return PC_PION_CONTRACT_MARKER;
}

typedef struct {
    uint32_t generation;
    uint16_t sequence;
    uint16_t reserved;
    uint32_t timestamp;
    uint32_t peer_len;
    uint32_t payload_len;
    uint64_t arrival_age_ns;
    uint64_t ingress_overflow;
} pc_pion_rtp_event_v1;

typedef struct {
    uint32_t attempted;
    uint32_t enqueued;
    uint32_t queue_full;
    uint32_t stale_epoch;
} pc_pion_send_result_v1;

typedef struct {
    uint64_t rtp_tx_attempts;
    uint64_t rtp_tx_ok;
    uint64_t rtp_tx_errors;
    uint64_t rtp_tx_queue_dropped;
    uint64_t rtp_tx_stale_epoch_dropped;
    uint64_t rtp_tx_write_timeouts;
    uint64_t rtp_tx_queue_depth_max;
    uint64_t rtp_rx_packets;
    uint64_t rtp_rx_bytes;
    uint64_t stale_rtp_rx_dropped;
} pc_pion_transport_counters_v1;
*/
import "C"

import (
	"encoding/json"
	"errors"
	"sync"
	"sync/atomic"
	"time"
	"unicode/utf8"
	"unsafe"
)

const (
	statusOK             = 0
	statusEmpty          = 1
	statusBufferTooSmall = 2
	statusHandle         = -1
	statusArgument       = -2
	statusState          = -3
	statusNotFound       = -4
	statusInternal       = -7
)

const pionVersion uint32 = 4_002_017

func pionContractMarkerText() string {
	return C.GoString(C.pc_pion_contract_marker_data())
}

var engineRegistry = struct {
	sync.RWMutex
	next    atomic.Uint64
	engines map[uint64]*engine
}{engines: make(map[uint64]*engine)}

func registeredEngine(handle C.uint64_t) (*engine, int) {
	if handle == 0 {
		return nil, statusHandle
	}
	engineRegistry.RLock()
	e := engineRegistry.engines[uint64(handle)]
	engineRegistry.RUnlock()
	if e == nil {
		return nil, statusHandle
	}
	return e, statusOK
}

func copiedBytes(pointer *C.uint8_t, length C.uint32_t, maximum uint32) ([]byte, error) {
	if uint32(length) > maximum {
		return nil, errors.New("input too large")
	}
	if length == 0 {
		return nil, nil
	}
	if pointer == nil {
		return nil, errors.New("nil input")
	}
	return C.GoBytes(unsafe.Pointer(pointer), C.int(length)), nil
}

func copiedString(pointer *C.uint8_t, length C.uint32_t, maximum uint32) (string, error) {
	b, err := copiedBytes(pointer, length, maximum)
	if err != nil {
		return "", err
	}
	if !utf8.Valid(b) {
		return "", errors.New("input is not UTF-8")
	}
	return string(b), nil
}

func recoverStatus(status *C.int32_t) {
	if recover() != nil {
		*status = statusInternal
	}
}

func pollControlData(q *controlQueue, capacity uint32, sink func([]byte)) (uint32, int) {
	q.mu.Lock()
	defer q.mu.Unlock()

	eventID, event := q.peekLocked()
	if event == nil {
		return 0, statusEmpty
	}
	required := uint32(len(event))
	if capacity < required || sink == nil {
		return required, statusBufferTooSmall
	}
	sink(event)
	if !q.popLocked(eventID) {
		return required, statusState
	}
	return required, statusOK
}

func pollRTPData(
	q *rtpQueue,
	peerCapacity, payloadCapacity uint32,
	peerWritable, payloadWritable bool,
	sink func(string, []byte),
) (inboundRTP, uint64, int) {
	q.mu.Lock()
	defer q.mu.Unlock()

	packet, overflow, ok := q.peekLocked()
	if !ok {
		return inboundRTP{}, overflow, statusEmpty
	}
	if peerCapacity < uint32(len(packet.peerID)) ||
		payloadCapacity < uint32(len(packet.payload)) ||
		(len(packet.peerID) > 0 && !peerWritable) ||
		(len(packet.payload) > 0 && !payloadWritable) || sink == nil {
		return packet, overflow, statusBufferTooSmall
	}
	sink(packet.peerID, packet.payload)
	if !q.popLocked(packet.queueID) {
		return packet, overflow, statusState
	}
	return packet, overflow, statusOK
}

//export pc_pion_abi_version
func pc_pion_abi_version() C.uint32_t {
	return pionABIVersion
}

//export pc_pion_version
func pc_pion_version() C.uint32_t {
	return C.uint32_t(pionVersion)
}

//export pc_pion_engine_new
func pc_pion_engine_new() (handle C.uint64_t) {
	defer func() {
		if recover() != nil {
			handle = 0
		}
	}()
	e, err := newEngine()
	if err != nil {
		return 0
	}
	engineRegistry.Lock()
	id := engineRegistry.next.Add(1)
	for id == 0 || engineRegistry.engines[id] != nil {
		id = engineRegistry.next.Add(1)
	}
	engineRegistry.engines[id] = e
	engineRegistry.Unlock()
	return C.uint64_t(id)
}

//export pc_pion_engine_close
func pc_pion_engine_close(handle C.uint64_t) (status C.int32_t) {
	defer recoverStatus(&status)
	engineRegistry.Lock()
	e := engineRegistry.engines[uint64(handle)]
	if e == nil {
		engineRegistry.Unlock()
		return statusHandle
	}
	delete(engineRegistry.engines, uint64(handle))
	engineRegistry.Unlock()
	e.close()
	return statusOK
}

//export pc_pion_set_ice_servers
func pc_pion_set_ice_servers(handle C.uint64_t, data *C.uint8_t, length C.uint32_t) (status C.int32_t) {
	defer recoverStatus(&status)
	e, code := registeredEngine(handle)
	if code != statusOK {
		return C.int32_t(code)
	}
	b, err := copiedBytes(data, length, 256*1024)
	if err != nil {
		return statusArgument
	}
	var servers []iceServerJSON
	if err = json.Unmarshal(b, &servers); err != nil {
		return statusArgument
	}
	if err = e.setICEServers(servers); err != nil {
		return statusState
	}
	return statusOK
}

//export pc_pion_add_peer
func pc_pion_add_peer(
	handle C.uint64_t,
	peerData *C.uint8_t,
	peerLength C.uint32_t,
	offerer C.uint32_t,
	relayOnly C.uint32_t,
	generation C.uint32_t,
	minEpoch C.uint64_t,
) (status C.int32_t) {
	defer recoverStatus(&status)
	e, code := registeredEngine(handle)
	if code != statusOK {
		return C.int32_t(code)
	}
	peerID, err := copiedString(peerData, peerLength, 256)
	if err != nil || peerID == "" {
		return statusArgument
	}
	if err = e.addPeer(peerID, offerer != 0, relayOnly != 0, uint32(generation), uint64(minEpoch)); err != nil {
		e.control.push(controlEvent{Kind: "error", PeerID: peerID, Generation: uint32(generation), Message: err.Error()})
		return statusState
	}
	return statusOK
}

//export pc_pion_remove_peer
func pc_pion_remove_peer(handle C.uint64_t, peerData *C.uint8_t, peerLength C.uint32_t) (status C.int32_t) {
	defer recoverStatus(&status)
	e, code := registeredEngine(handle)
	if code != statusOK {
		return C.int32_t(code)
	}
	peerID, err := copiedString(peerData, peerLength, 256)
	if err != nil || peerID == "" {
		return statusArgument
	}
	e.removePeer(peerID)
	return statusOK
}

//export pc_pion_set_remote_sdp
func pc_pion_set_remote_sdp(
	handle C.uint64_t,
	peerData *C.uint8_t,
	peerLength C.uint32_t,
	generation C.uint32_t,
	typeData *C.uint8_t,
	typeLength C.uint32_t,
	sdpData *C.uint8_t,
	sdpLength C.uint32_t,
) (status C.int32_t) {
	defer recoverStatus(&status)
	e, code := registeredEngine(handle)
	if code != statusOK {
		return C.int32_t(code)
	}
	peerID, err := copiedString(peerData, peerLength, 256)
	if err != nil {
		return statusArgument
	}
	sdpType, err := copiedString(typeData, typeLength, 16)
	if err != nil {
		return statusArgument
	}
	sdp, err := copiedString(sdpData, sdpLength, 1024*1024)
	if err != nil {
		return statusArgument
	}
	p := e.peer(peerID)
	if p == nil || p.generation != uint32(generation) {
		return statusNotFound
	}
	if err = p.setRemoteSDP(sdpType, sdp); err != nil {
		p.fail(err.Error())
		return statusState
	}
	return statusOK
}

//export pc_pion_add_ice_candidate
func pc_pion_add_ice_candidate(
	handle C.uint64_t,
	peerData *C.uint8_t,
	peerLength C.uint32_t,
	generation C.uint32_t,
	candidateData *C.uint8_t,
	candidateLength C.uint32_t,
) (status C.int32_t) {
	defer recoverStatus(&status)
	e, code := registeredEngine(handle)
	if code != statusOK {
		return C.int32_t(code)
	}
	peerID, err := copiedString(peerData, peerLength, 256)
	if err != nil {
		return statusArgument
	}
	candidate, err := copiedString(candidateData, candidateLength, 64*1024)
	if err != nil {
		return statusArgument
	}
	p := e.peer(peerID)
	if p == nil || p.generation != uint32(generation) {
		return statusNotFound
	}
	if err = p.addICECandidate(candidate); err != nil {
		p.fail(err.Error())
		return statusState
	}
	return statusOK
}

//export pc_pion_restart_ice
func pc_pion_restart_ice(
	handle C.uint64_t,
	peerData *C.uint8_t,
	peerLength C.uint32_t,
	generation C.uint32_t,
	relayOnly C.uint32_t,
	createOffer C.uint32_t,
) (status C.int32_t) {
	defer recoverStatus(&status)
	e, code := registeredEngine(handle)
	if code != statusOK {
		return C.int32_t(code)
	}
	peerID, err := copiedString(peerData, peerLength, 256)
	if err != nil {
		return statusArgument
	}
	p := e.peer(peerID)
	if p == nil || p.generation != uint32(generation) {
		return statusNotFound
	}
	if err = p.restartICE(relayOnly != 0, createOffer != 0); err != nil {
		p.fail(err.Error())
		return statusState
	}
	return statusOK
}

//export pc_pion_send_opus
func pc_pion_send_opus(
	handle C.uint64_t,
	payloadData *C.uint8_t,
	payloadLength C.uint32_t,
	epoch C.uint64_t,
	mediaSequence C.uint64_t,
	result *C.pc_pion_send_result_v1,
) (status C.int32_t) {
	defer recoverStatus(&status)
	e, code := registeredEngine(handle)
	if code != statusOK {
		return C.int32_t(code)
	}
	if result == nil {
		return statusArgument
	}
	payload, err := copiedBytes(payloadData, payloadLength, 64*1024)
	if err != nil {
		return statusArgument
	}
	r := e.sendOpus(payload, uint64(epoch), uint64(mediaSequence))
	result.attempted = C.uint32_t(r.attempted)
	result.enqueued = C.uint32_t(r.enqueued)
	result.queue_full = C.uint32_t(r.queueFull)
	result.stale_epoch = C.uint32_t(r.stale)
	return statusOK
}

//export pc_pion_advance_epoch
func pc_pion_advance_epoch(handle C.uint64_t, epoch C.uint64_t, timeoutMS C.uint32_t) (status C.int32_t) {
	defer recoverStatus(&status)
	e, code := registeredEngine(handle)
	if code != statusOK {
		return C.int32_t(code)
	}
	if !e.advanceEpoch(uint64(epoch), time.Duration(timeoutMS)*time.Millisecond) {
		return statusState
	}
	return statusOK
}

//export pc_pion_poll_control
func pc_pion_poll_control(
	handle C.uint64_t,
	buffer *C.uint8_t,
	capacity C.uint32_t,
	required *C.uint32_t,
) (status C.int32_t) {
	defer recoverStatus(&status)
	e, code := registeredEngine(handle)
	if code != statusOK {
		return C.int32_t(code)
	}
	if required == nil {
		return statusArgument
	}
	var sink func([]byte)
	if buffer != nil {
		sink = func(event []byte) {
			copy(unsafe.Slice((*byte)(unsafe.Pointer(buffer)), len(event)), event)
		}
	}
	requiredBytes, code := pollControlData(&e.control, uint32(capacity), sink)
	*required = C.uint32_t(requiredBytes)
	return C.int32_t(code)
}

//export pc_pion_poll_rtp
func pc_pion_poll_rtp(
	handle C.uint64_t,
	event *C.pc_pion_rtp_event_v1,
	peerBuffer *C.uint8_t,
	peerCapacity C.uint32_t,
	payloadBuffer *C.uint8_t,
	payloadCapacity C.uint32_t,
) (status C.int32_t) {
	defer recoverStatus(&status)
	e, code := registeredEngine(handle)
	if code != statusOK {
		return C.int32_t(code)
	}
	if event == nil {
		return statusArgument
	}
	packet, overflow, code := pollRTPData(
		e.rtp, uint32(peerCapacity), uint32(payloadCapacity),
		peerBuffer != nil, payloadBuffer != nil,
		func(peer string, payload []byte) {
			if len(peer) > 0 {
				copy(unsafe.Slice((*byte)(unsafe.Pointer(peerBuffer)), len(peer)), peer)
			}
			if len(payload) > 0 {
				copy(unsafe.Slice((*byte)(unsafe.Pointer(payloadBuffer)), len(payload)), payload)
			}
		},
	)
	if code == statusEmpty {
		return statusEmpty
	}
	event.generation = C.uint32_t(packet.generation)
	event.sequence = C.uint16_t(packet.sequence)
	event.reserved = 0
	event.timestamp = C.uint32_t(packet.timestamp)
	event.peer_len = C.uint32_t(len(packet.peerID))
	event.payload_len = C.uint32_t(len(packet.payload))
	age := time.Since(packet.arrival)
	if age < 0 {
		age = 0
	}
	event.arrival_age_ns = C.uint64_t(age.Nanoseconds())
	event.ingress_overflow = C.uint64_t(overflow)
	return C.int32_t(code)
}

//export pc_pion_get_counters
func pc_pion_get_counters(handle C.uint64_t, out *C.pc_pion_transport_counters_v1) (status C.int32_t) {
	defer recoverStatus(&status)
	e, code := registeredEngine(handle)
	if code != statusOK {
		return C.int32_t(code)
	}
	if out == nil {
		return statusArgument
	}
	out.rtp_tx_attempts = C.uint64_t(e.counters.rtpTXAttempts.Load())
	out.rtp_tx_ok = C.uint64_t(e.counters.rtpTXOK.Load())
	out.rtp_tx_errors = C.uint64_t(e.counters.rtpTXErrors.Load())
	out.rtp_tx_queue_dropped = C.uint64_t(e.counters.rtpTXQueueDropped.Load())
	out.rtp_tx_stale_epoch_dropped = C.uint64_t(e.counters.rtpTXStaleEpochDropped.Load())
	out.rtp_tx_write_timeouts = C.uint64_t(e.counters.rtpTXWriteTimeouts.Load())
	out.rtp_tx_queue_depth_max = C.uint64_t(e.counters.rtpTXQueueDepthMax.Load())
	out.rtp_rx_packets = C.uint64_t(e.counters.rtpRXPackets.Load())
	out.rtp_rx_bytes = C.uint64_t(e.counters.rtpRXBytes.Load())
	out.stale_rtp_rx_dropped = C.uint64_t(e.counters.staleRTPRXDropped.Load())
	return statusOK
}
