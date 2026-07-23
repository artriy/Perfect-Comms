// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"crypto/sha256"
	"errors"
	"sync"

	"github.com/pion/interceptor"
	"github.com/pion/rtp"
)

const epochGateHistorySize = 64

var errEpochGateRejected = errors.New("RTP packet rejected by privacy epoch gate")

type epochGateFactory struct {
	created chan *epochGate
}

func newEpochGateFactory() *epochGateFactory {
	return &epochGateFactory{created: make(chan *epochGate, 32)}
}

func (f *epochGateFactory) NewInterceptor(_ string) (interceptor.Interceptor, error) {
	gate := newEpochGate()
	f.created <- gate
	return gate, nil
}

type epochGatePacket struct {
	sequence    uint16
	timestamp   uint32
	payloadSize int
	payloadHash [sha256.Size]byte
	epoch       uint64
	serial      uint64
}

type epochGateHistoryEntry struct {
	sequence uint16
	serial   uint64
}

// epochGate sits below Pion's NACK responder, so original packets and responder-owned
// retransmissions cross the same privacy boundary before reaching the transport.
type epochGate struct {
	interceptor.NoOp

	mu      sync.Mutex
	floor   uint64
	closed  bool
	serial  uint64
	next    int
	packets map[uint16]epochGatePacket
	history [epochGateHistorySize]epochGateHistoryEntry
	active  map[uint64]uint32
	streams map[uint32]struct{}
}

func newEpochGate() *epochGate {
	return &epochGate{
		packets: make(map[uint16]epochGatePacket, epochGateHistorySize),
		active:  make(map[uint64]uint32),
		streams: make(map[uint32]struct{}),
	}
}

func epochPacketMetadata(header *rtp.Header, payload []byte, epoch uint64) epochGatePacket {
	return epochGatePacket{
		sequence: header.SequenceNumber, timestamp: header.Timestamp,
		payloadSize: len(payload), payloadHash: sha256.Sum256(payload), epoch: epoch,
	}
}

// recordOriginal publishes packet ownership before TrackLocalStaticRTP enters the interceptor
// chain. The fixed history bound matches the responder cache, including across sequence wrap.
func (g *epochGate) recordOriginal(header *rtp.Header, payload []byte, epoch uint64) bool {
	packet := epochPacketMetadata(header, payload, epoch)
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.closed || epoch < g.floor {
		return false
	}

	g.serial++
	packet.serial = g.serial
	evicted := g.history[g.next]
	if current, ok := g.packets[evicted.sequence]; ok && current.serial == evicted.serial {
		delete(g.packets, evicted.sequence)
	}
	g.history[g.next] = epochGateHistoryEntry{sequence: packet.sequence, serial: packet.serial}
	g.next = (g.next + 1) % len(g.history)
	g.packets[packet.sequence] = packet
	return true
}

func (g *epochGate) BindLocalStream(info *interceptor.StreamInfo, writer interceptor.RTPWriter) interceptor.RTPWriter {
	g.mu.Lock()
	g.streams[info.SSRC] = struct{}{}
	g.mu.Unlock()

	return interceptor.RTPWriterFunc(func(header *rtp.Header, payload []byte, attributes interceptor.Attributes) (int, error) {
		observed := epochPacketMetadata(header, payload, 0)
		g.mu.Lock()
		packet, ok := g.packets[header.SequenceNumber]
		if g.closed || !ok || packet.epoch < g.floor || packet.timestamp != observed.timestamp ||
			packet.payloadSize != observed.payloadSize || packet.payloadHash != observed.payloadHash {
			g.mu.Unlock()
			return 0, errEpochGateRejected
		}
		g.active[packet.epoch]++
		g.mu.Unlock()

		n, err := writer.Write(header, payload, attributes)

		g.mu.Lock()
		if g.active[packet.epoch] == 1 {
			delete(g.active, packet.epoch)
		} else {
			g.active[packet.epoch]--
		}
		g.mu.Unlock()
		return n, err
	})
}

func (g *epochGate) UnbindLocalStream(info *interceptor.StreamInfo) {
	g.mu.Lock()
	delete(g.streams, info.SSRC)
	clear(g.packets)
	g.history = [epochGateHistorySize]epochGateHistoryEntry{}
	g.next = 0
	g.mu.Unlock()
}

func (g *epochGate) advanceEpoch(epoch uint64) {
	g.mu.Lock()
	if epoch > g.floor {
		g.floor = epoch
	}
	for sequence, packet := range g.packets {
		if packet.epoch < g.floor {
			delete(g.packets, sequence)
		}
	}
	g.mu.Unlock()
}

func (g *epochGate) hasWriteBefore(epoch uint64) bool {
	g.mu.Lock()
	defer g.mu.Unlock()
	for writeEpoch, count := range g.active {
		if count != 0 && writeEpoch < epoch {
			return true
		}
	}
	return false
}

func (g *epochGate) Close() error {
	g.mu.Lock()
	g.closed = true
	clear(g.packets)
	clear(g.streams)
	g.history = [epochGateHistorySize]epochGateHistoryEntry{}
	g.next = 0
	g.mu.Unlock()
	return nil
}
