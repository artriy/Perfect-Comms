// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"errors"
	"fmt"
	"os"
	"sync"
	"sync/atomic"
	"time"

	"github.com/pion/ice/v4"
	"github.com/pion/interceptor"
	"github.com/pion/interceptor/pkg/cc"
	"github.com/pion/interceptor/pkg/gcc"
	"github.com/pion/rtcp"
	"github.com/pion/webrtc/v4"
)

const (
	pionABIVersion         = 1
	outboundQueueCapacity  = 4
	statsInterval          = 2 * time.Second
	relayAcceptanceMinWait = 2 * time.Second
	writeTimeout           = 100 * time.Millisecond
	privacyDrainTimeout    = 500 * time.Millisecond
	maxPendingCandidates   = 64
)

type iceServerJSON struct {
	URLs       []string `json:"urls"`
	Username   *string  `json:"username,omitempty"`
	Credential *string  `json:"credential,omitempty"`
}

// feedbackAwareEstimator distinguishes GCC's configured startup bitrate from an estimate backed
// by actual transport-wide congestion-control feedback.
type feedbackAwareEstimator struct {
	cc.BandwidthEstimator
	feedback atomic.Bool
}

func (e *feedbackAwareEstimator) WriteRTCP(packets []rtcp.Packet, attributes interceptor.Attributes) error {
	if err := e.BandwidthEstimator.WriteRTCP(packets, attributes); err != nil {
		return err
	}
	for _, packet := range packets {
		switch packet.(type) {
		case *rtcp.TransportLayerCC, *rtcp.CCFeedbackReport:
			e.feedback.Store(true)
		}
	}
	return nil
}

func (e *feedbackAwareEstimator) hasFeedback() bool {
	return e != nil && e.feedback.Load()
}

type transportCounters struct {
	rtpTXAttempts          atomic.Uint64
	rtpTXOK                atomic.Uint64
	rtpTXErrors            atomic.Uint64
	rtpTXQueueDropped      atomic.Uint64
	rtpTXStaleEpochDropped atomic.Uint64
	rtpTXWriteTimeouts     atomic.Uint64
	rtpTXQueueDepthMax     atomic.Uint64
	rtpRXPackets           atomic.Uint64
	rtpRXBytes             atomic.Uint64
	staleRTPRXDropped      atomic.Uint64
}

func (c *transportCounters) recordQueueDepth(depth uint64) {
	for {
		current := c.rtpTXQueueDepthMax.Load()
		if depth <= current || c.rtpTXQueueDepthMax.CompareAndSwap(current, depth) {
			return
		}
	}
}

type engine struct {
	mu           sync.RWMutex
	peerOps      sync.Mutex
	peers        map[string]*peer
	iceServers   []webrtc.ICEServer
	api          *webrtc.API
	estimators   chan *feedbackAwareEstimator
	udpMux       ice.UDPMux
	muxClosing   *atomic.Bool
	control      controlQueue
	rtp          *rtpQueue
	privacyFloor atomic.Uint64
	privacyMu    sync.RWMutex
	closed       atomic.Bool
	instance     atomic.Uint64
	counters     transportCounters
}

func newEngine() (*engine, error) {
	mediaEngine := &webrtc.MediaEngine{}
	if err := mediaEngine.RegisterCodec(webrtc.RTPCodecParameters{
		RTPCodecCapability: webrtc.RTPCodecCapability{
			MimeType:    webrtc.MimeTypeOpus,
			ClockRate:   48_000,
			Channels:    2,
			SDPFmtpLine: "minptime=10;useinbandfec=1",
		},
		PayloadType: 111,
	}, webrtc.RTPCodecTypeAudio); err != nil {
		return nil, fmt.Errorf("register opus: %w", err)
	}

	registry := &interceptor.Registry{}
	estimators := make(chan *feedbackAwareEstimator, 32)
	congestionController, err := cc.NewInterceptor(func() (cc.BandwidthEstimator, error) {
		estimator, createErr := gcc.NewSendSideBWE(
			gcc.SendSideBWEInitialBitrate(64_000),
			// Voice frames are already produced every 20 ms. A queued asynchronous pacer
			// could retain an old privacy epoch after advanceEpoch returns, so keep the
			// congestion estimator while writing synchronously through the no-op pacer.
			gcc.SendSideBWEPacer(gcc.NewNoOpPacer()),
		)
		if createErr != nil {
			return nil, createErr
		}
		return &feedbackAwareEstimator{BandwidthEstimator: estimator}, nil
	})
	if err != nil {
		return nil, fmt.Errorf("congestion controller: %w", err)
	}
	congestionController.OnNewPeerConnection(func(_ string, estimator cc.BandwidthEstimator) {
		observed, ok := estimator.(*feedbackAwareEstimator)
		if !ok {
			return
		}
		select {
		case estimators <- observed:
		default:
		}
	})
	registry.Add(congestionController)
	if err = webrtc.ConfigureTWCCHeaderExtensionSender(mediaEngine, registry); err != nil {
		return nil, fmt.Errorf("twcc header sender: %w", err)
	}
	if err = webrtc.RegisterDefaultInterceptors(mediaEngine, registry); err != nil {
		return nil, fmt.Errorf("default interceptors: %w", err)
	}

	settings := webrtc.SettingEngine{}
	settings.SetICETimeouts(8*time.Second, 15*time.Second, 2*time.Second)
	settings.SetRelayAcceptanceMinWait(relayAcceptanceMinWait)
	settings.SetICEMulticastDNSMode(multicastDNSMode())
	settings.SetICEUseCandidateCheckPriority(true)
	// One shared mux per engine keeps host ICE traffic on one port per usable
	// network family. Each ICE agent still owns its mDNS sockets, and Pion
	// WebRTC v4.2.17 normal srflx gathering uses one socket per STUN URL.
	// Prefer dual stack, while retaining an IPv4 fallback for hosts without a
	// usable IPv6 interface. Active ICE-TCP and TURN TCP/TLS stay enabled.
	muxClosing := &atomic.Bool{}
	udpMux, networkTypes, err := newSharedUDPMux(muxClosing)
	if err != nil {
		return nil, fmt.Errorf("shared udp mux: %w", err)
	}
	settings.SetICEUDPMux(udpMux)
	settings.SetNetworkTypes(networkTypes)

	return &engine{
		peers:      make(map[string]*peer),
		iceServers: defaultICEServers(),
		api: webrtc.NewAPI(
			webrtc.WithMediaEngine(mediaEngine),
			webrtc.WithInterceptorRegistry(registry),
			webrtc.WithSettingEngine(settings),
		),
		estimators: estimators,
		udpMux:     udpMux,
		muxClosing: muxClosing,
		rtp:        newRTPQueue(),
	}, nil
}

func multicastDNSMode() ice.MulticastDNSMode {
	if os.Getenv("PC_PION_TEST_DISABLE_MDNS") == "1" {
		// Hermetic same-host tests must not depend on runner multicast policy.
		// The shared mux already includes loopback; production keeps mDNS privacy.
		return ice.MulticastDNSModeDisabled
	}
	return ice.MulticastDNSModeQueryAndGather
}

func newSharedUDPMux(closing *atomic.Bool) (ice.UDPMux, []webrtc.NetworkType, error) {
	options := func(networks ...ice.NetworkType) []ice.UDPMuxFromPortOption {
		result := []ice.UDPMuxFromPortOption{
			ice.UDPMuxFromPortWithLoopback(),
			ice.UDPMuxFromPortWithReadBufferSize(1 << 20),
			ice.UDPMuxFromPortWithWriteBufferSize(1 << 20),
			ice.UDPMuxFromPortWithLogger(newMuxLogger(closing)),
		}
		if len(networks) > 0 {
			result = append(result, ice.UDPMuxFromPortWithNetworks(networks...))
		}
		return result
	}
	mux, err := ice.NewMultiUDPMuxFromPort(0, options()...)
	if err == nil {
		return mux, []webrtc.NetworkType{
			webrtc.NetworkTypeUDP4, webrtc.NetworkTypeUDP6,
			webrtc.NetworkTypeTCP4, webrtc.NetworkTypeTCP6,
		}, nil
	}
	mux, ipv4Err := ice.NewMultiUDPMuxFromPort(0, options(ice.NetworkTypeUDP4)...)
	if ipv4Err == nil {
		return mux, []webrtc.NetworkType{webrtc.NetworkTypeUDP4, webrtc.NetworkTypeTCP4}, nil
	}
	mux, ipv6Err := ice.NewMultiUDPMuxFromPort(0, options(ice.NetworkTypeUDP6)...)
	if ipv6Err == nil {
		return mux, []webrtc.NetworkType{webrtc.NetworkTypeUDP6, webrtc.NetworkTypeTCP6}, nil
	}
	return nil, nil, fmt.Errorf("dual stack: %v; IPv4 fallback: %v; IPv6 fallback: %w", err, ipv4Err, ipv6Err)
}

func defaultICEServers() []webrtc.ICEServer {
	return []webrtc.ICEServer{{URLs: []string{
		"stun:stun.l.google.com:19302",
		"stun:stun1.l.google.com:19302",
		"stun:stun2.l.google.com:19302",
		"stun:stun.cloudflare.com:3478",
		"stun:global.stun.twilio.com:3478",
	}}}
}

func mapICEServers(servers []iceServerJSON) []webrtc.ICEServer {
	mapped := make([]webrtc.ICEServer, 0, len(servers))
	for _, server := range servers {
		if len(server.URLs) == 0 {
			continue
		}
		entry := webrtc.ICEServer{
			URLs:           append([]string(nil), server.URLs...),
			CredentialType: webrtc.ICECredentialTypePassword,
		}
		if server.Username != nil {
			entry.Username = *server.Username
		}
		if server.Credential != nil {
			entry.Credential = *server.Credential
		}
		mapped = append(mapped, entry)
	}
	return mapped
}

func (e *engine) configuration(relayOnly bool) webrtc.Configuration {
	e.mu.RLock()
	servers := append([]webrtc.ICEServer(nil), e.iceServers...)
	e.mu.RUnlock()
	policy := webrtc.ICETransportPolicyAll
	if relayOnly {
		policy = webrtc.ICETransportPolicyRelay
	}
	return webrtc.Configuration{
		ICEServers:         servers,
		ICETransportPolicy: policy,
		BundlePolicy:       webrtc.BundlePolicyMaxBundle,
		RTCPMuxPolicy:      webrtc.RTCPMuxPolicyRequire,
		SDPSemantics:       webrtc.SDPSemanticsUnifiedPlan,
	}
}

func (e *engine) setICEServers(servers []iceServerJSON) error {
	mapped := mapICEServers(servers)
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.closed.Load() {
		return errors.New("engine closed")
	}
	e.iceServers = mapped
	return nil
}

func (e *engine) isCurrent(p *peer) bool {
	if !p.active.Load() || e.closed.Load() {
		return false
	}
	e.mu.RLock()
	current := e.peers[p.id]
	e.mu.RUnlock()
	return current == p && current.instance == p.instance
}

func (e *engine) emitPeerControl(p *peer, event controlEvent) bool {
	e.mu.RLock()
	current := e.peers[p.id]
	ok := !e.closed.Load() && p.active.Load() && current == p && current.instance == p.instance
	if ok {
		e.control.push(event)
	}
	e.mu.RUnlock()
	return ok
}

func (e *engine) enqueuePeerRTP(p *peer, packet inboundRTP) bool {
	e.mu.RLock()
	current := e.peers[p.id]
	ok := !e.closed.Load() && p.active.Load() && current == p && current.instance == p.instance
	if ok {
		e.rtp.push(packet)
	}
	e.mu.RUnlock()
	return ok
}

func (e *engine) addPeer(id string, offerer, relayOnly bool, generation uint32, minEpoch uint64) error {
	e.peerOps.Lock()
	defer e.peerOps.Unlock()
	return e.addPeerLocked(id, offerer, relayOnly, generation, minEpoch)
}

func (e *engine) addPeerLocked(id string, offerer, relayOnly bool, generation uint32, minEpoch uint64) error {
	if id == "" {
		return errors.New("empty peer id")
	}
	if e.closed.Load() {
		return errors.New("engine closed")
	}
	e.mu.RLock()
	existing := e.peers[id]
	e.mu.RUnlock()
	if existing != nil && existing.generation == generation {
		existing.raiseMinEpoch(minEpoch)
		e.privacyMu.Lock()
		dropped := existing.purgeOutboundBelow(existing.minEpoch.Load())
		e.privacyMu.Unlock()
		e.counters.rtpTXStaleEpochDropped.Add(uint64(dropped))
		return nil
	}
	if existing != nil {
		e.removePeerLocked(id)
	}

	pc, err := e.api.NewPeerConnection(e.configuration(relayOnly))
	if err != nil {
		// The interceptor factory can publish before a later PeerConnection setup step fails.
		// Serialized peer creation means any queued estimator belongs to this failed attempt.
		select {
		case stale := <-e.estimators:
			_ = stale.Close()
		default:
		}
		return fmt.Errorf("new peer connection: %w", err)
	}
	var estimator *feedbackAwareEstimator
	select {
	case estimator = <-e.estimators:
	case <-time.After(time.Second):
		_ = pc.Close()
		return errors.New("pion congestion estimator unavailable")
	}
	track, err := webrtc.NewTrackLocalStaticRTP(webrtc.RTPCodecCapability{
		MimeType:    webrtc.MimeTypeOpus,
		ClockRate:   48_000,
		Channels:    2,
		SDPFmtpLine: "minptime=10;useinbandfec=1",
	}, "audio", "perfectcomms")
	if err != nil {
		_ = pc.Close()
		return fmt.Errorf("new opus track: %w", err)
	}
	sender, err := pc.AddTrack(track)
	if err != nil {
		_ = pc.Close()
		return fmt.Errorf("add opus track: %w", err)
	}
	p := newPeer(e, id, generation, e.instance.Add(1), minEpoch, relayOnly, pc, track, sender, estimator)
	e.mu.Lock()
	if e.closed.Load() {
		e.mu.Unlock()
		p.close()
		return errors.New("engine closed")
	}
	e.control.removePeer(id)
	e.rtp.removePeer(id)
	e.peers[id] = p
	e.mu.Unlock()
	p.start()
	if offerer {
		if err = p.createOffer(false); err != nil {
			e.removePeerLocked(id)
			return err
		}
	}
	return nil
}

func (e *engine) removePeer(id string) {
	e.peerOps.Lock()
	defer e.peerOps.Unlock()
	e.removePeerLocked(id)
}

func (e *engine) removePeerLocked(id string) {
	e.mu.Lock()
	p := e.peers[id]
	if p != nil {
		delete(e.peers, id)
	}
	e.control.removePeer(id)
	e.rtp.removePeer(id)
	e.mu.Unlock()
	if p != nil {
		p.close()
	}
}

func (e *engine) peer(id string) *peer {
	e.mu.RLock()
	p := e.peers[id]
	e.mu.RUnlock()
	return p
}

func (e *engine) sendOpus(payload []byte, epoch, mediaSequence uint64) sendResult {
	result := sendResult{}
	if e.closed.Load() {
		return result
	}
	e.privacyMu.RLock()
	defer e.privacyMu.RUnlock()
	e.mu.RLock()
	peers := make([]*peer, 0, len(e.peers))
	for _, p := range e.peers {
		peers = append(peers, p)
	}
	e.mu.RUnlock()
	if epoch < e.privacyFloor.Load() {
		for _, p := range peers {
			if p.active.Load() {
				result.stale++
			}
		}
		e.counters.rtpTXStaleEpochDropped.Add(uint64(result.stale))
		return result
	}
	for _, p := range peers {
		if epoch < p.minEpoch.Load() || !p.active.Load() {
			result.stale++
			e.counters.rtpTXStaleEpochDropped.Add(1)
			continue
		}
		result.attempted++
		e.counters.rtpTXAttempts.Add(1)
		frame := outboundFrame{
			payload:       append([]byte(nil), payload...),
			epoch:         epoch,
			mediaSequence: mediaSequence,
		}
		select {
		case p.outbound <- frame:
			result.enqueued++
			e.counters.recordQueueDepth(uint64(len(p.outbound)))
		default:
			result.queueFull++
			e.counters.rtpTXQueueDropped.Add(1)
		}
	}
	return result
}

func (e *engine) advanceEpoch(epoch uint64, timeout time.Duration) bool {
	target := epoch
	for {
		current := e.privacyFloor.Load()
		if epoch <= current {
			target = current
			break
		}
		if e.privacyFloor.CompareAndSwap(current, epoch) {
			break
		}
	}
	deadline := time.Now().Add(timeout)
	for {
		if e.privacyMu.TryLock() {
			e.mu.RLock()
			var dropped uint64
			for _, p := range e.peers {
				dropped += uint64(p.purgeOutboundBelow(target))
			}
			e.mu.RUnlock()
			e.privacyMu.Unlock()
			e.counters.rtpTXStaleEpochDropped.Add(dropped)
			return true
		}
		if timeout <= 0 || !time.Now().Before(deadline) {
			return false
		}
		remaining := time.Until(deadline)
		if remaining > time.Millisecond {
			remaining = time.Millisecond
		}
		time.Sleep(remaining)
	}
}

func (e *engine) close() {
	e.peerOps.Lock()
	defer e.peerOps.Unlock()
	if !e.closed.CompareAndSwap(false, true) {
		return
	}
	e.mu.Lock()
	peers := make([]*peer, 0, len(e.peers))
	for _, p := range e.peers {
		peers = append(peers, p)
	}
	e.peers = make(map[string]*peer)
	for _, p := range peers {
		e.control.removePeer(p.id)
		e.rtp.removePeer(p.id)
	}
	e.mu.Unlock()
	for _, p := range peers {
		p.close()
	}
	if e.udpMux != nil {
		if e.muxClosing != nil {
			e.muxClosing.Store(true)
		}
		_ = e.udpMux.Close()
	}
}

type sendResult struct {
	attempted uint32
	enqueued  uint32
	queueFull uint32
	stale     uint32
}
