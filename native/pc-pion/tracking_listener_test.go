// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"errors"
	"net"
	"sync"
	"testing"
	"time"
)

func newLoopbackTrackingListener(t *testing.T, firstPacketTimeout time.Duration) *trackingTCPListener {
	t.Helper()

	inner, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	listener, ok := newTrackingTCPListener(inner, firstPacketTimeout).(*trackingTCPListener)
	if !ok {
		_ = inner.Close()
		t.Fatal("newTrackingTCPListener returned an unexpected implementation")
	}
	t.Cleanup(func() { _ = listener.Close() })
	return listener
}

func newLoopbackTrackingPair(
	t *testing.T,
	firstPacketTimeout time.Duration,
) (*trackingTCPListener, net.Conn, net.Conn) {
	t.Helper()

	listener := newLoopbackTrackingListener(t, firstPacketTimeout)
	client, err := net.DialTimeout("tcp4", listener.Addr().String(), time.Second)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	t.Cleanup(func() { _ = client.Close() })

	server, err := listener.Accept()
	if err != nil {
		t.Fatalf("accept: %v", err)
	}
	t.Cleanup(func() { _ = server.Close() })
	return listener, client, server
}

func trackedConnectionCount(listener *trackingTCPListener) int {
	listener.mu.Lock()
	defer listener.mu.Unlock()
	return len(listener.connections)
}

func TestTrackingTCPListenerCloseClosesNoDataConnection(t *testing.T) {
	listener, _, server := newLoopbackTrackingPair(t, time.Minute)

	readDone := make(chan error, 1)
	go func() {
		var buffer [1]byte
		_, err := server.Read(buffer[:])
		readDone <- err
	}()

	closeDone := make(chan error, 1)
	go func() { closeDone <- listener.Close() }()

	select {
	case err := <-closeDone:
		if err != nil {
			t.Fatalf("listener close: %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("listener Close blocked on a no-data accepted connection")
	}

	select {
	case err := <-readDone:
		if err == nil {
			t.Fatal("accepted connection read succeeded after listener Close")
		}
	case <-time.After(time.Second):
		t.Fatal("listener Close did not unblock the accepted connection")
	}

	if got := trackedConnectionCount(listener); got != 0 {
		t.Fatalf("tracked connections after close = %d, want 0", got)
	}
}

func TestTrackingTCPListenerSuccessfulConnectionUnregisters(t *testing.T) {
	listener, client, server := newLoopbackTrackingPair(t, time.Second)
	if got := trackedConnectionCount(listener); got != 1 {
		t.Fatalf("tracked connections after accept = %d, want 1", got)
	}

	if _, err := client.Write([]byte{0x7f}); err != nil {
		t.Fatalf("write: %v", err)
	}
	var buffer [1]byte
	if n, err := server.Read(buffer[:]); err != nil || n != len(buffer) || buffer[0] != 0x7f {
		t.Fatalf("read = (%d, %v, %x), want (1, nil, 7f)", n, err, buffer[:n])
	}
	if err := server.Close(); err != nil {
		t.Fatalf("connection close: %v", err)
	}
	if got := trackedConnectionCount(listener); got != 0 {
		t.Fatalf("tracked connections after connection close = %d, want 0", got)
	}
}

func TestTrackingTCPListenerPreservesConsumerReadDeadline(t *testing.T) {
	const initialTimeout = 100 * time.Millisecond
	listener, client, server := newLoopbackTrackingPair(t, initialTimeout)

	consumerDeadline := time.Now().Add(time.Second)
	if err := server.SetReadDeadline(consumerDeadline); err != nil {
		t.Fatalf("set consumer read deadline: %v", err)
	}
	if _, err := client.Write([]byte{1}); err != nil {
		t.Fatalf("write first byte: %v", err)
	}
	var first [1]byte
	if _, err := server.Read(first[:]); err != nil {
		t.Fatalf("read first byte: %v", err)
	}

	secondRead := make(chan error, 1)
	go func() {
		var second [1]byte
		_, err := server.Read(second[:])
		secondRead <- err
	}()
	// The initial deadline has elapsed before the second byte arrives. The read
	// must still use the consumer's later deadline after the first successful read.
	time.Sleep(2 * initialTimeout)
	if _, err := client.Write([]byte{2}); err != nil {
		t.Fatalf("write second byte: %v", err)
	}
	select {
	case err := <-secondRead:
		if err != nil {
			t.Fatalf("second read retained the initial deadline: %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("second read did not complete")
	}

	thirdRead := make(chan error, 1)
	go func() {
		var third [1]byte
		_, err := server.Read(third[:])
		thirdRead <- err
	}()
	select {
	case err := <-thirdRead:
		var netErr net.Error
		if !errors.As(err, &netErr) || !netErr.Timeout() {
			t.Fatalf("read after consumer deadline = %v, want timeout", err)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("consumer read deadline was cleared after the first read")
	}

	if got := trackedConnectionCount(listener); got != 1 {
		t.Fatalf("tracked connections before wrapped close = %d, want 1", got)
	}
}

func TestTrackingTCPListenerCloseIsIdempotent(t *testing.T) {
	listener := newLoopbackTrackingListener(t, time.Second)
	if err := listener.Close(); err != nil {
		t.Fatalf("first close: %v", err)
	}
	if err := listener.Close(); err != nil {
		t.Fatalf("second close: %v", err)
	}
	if _, err := listener.Accept(); !errors.Is(err, net.ErrClosed) {
		t.Fatalf("accept after close = %v, want underlying closed-listener error", err)
	}
}

func TestTrackingTCPListenerConcurrentCloseDoesNotDeadlock(t *testing.T) {
	listener := newLoopbackTrackingListener(t, time.Minute)

	const connectionCount = 8
	clients := make([]net.Conn, 0, connectionCount)
	servers := make([]net.Conn, 0, connectionCount)
	for range connectionCount {
		client, err := net.DialTimeout("tcp4", listener.Addr().String(), time.Second)
		if err != nil {
			t.Fatalf("dial: %v", err)
		}
		clients = append(clients, client)
		t.Cleanup(func() { _ = client.Close() })

		server, err := listener.Accept()
		if err != nil {
			t.Fatalf("accept: %v", err)
		}
		servers = append(servers, server)
		t.Cleanup(func() { _ = server.Close() })
	}

	acceptDone := make(chan error, 1)
	acceptStarted := make(chan struct{})
	go func() {
		close(acceptStarted)
		_, err := listener.Accept()
		acceptDone <- err
	}()
	<-acceptStarted

	start := make(chan struct{})
	var closes sync.WaitGroup
	for _, server := range servers {
		closes.Add(1)
		go func(conn net.Conn) {
			defer closes.Done()
			<-start
			_ = conn.Close()
		}(server)
	}
	for range 8 {
		closes.Add(1)
		go func() {
			defer closes.Done()
			<-start
			_ = listener.Close()
		}()
	}

	allClosed := make(chan struct{})
	go func() {
		closes.Wait()
		close(allClosed)
	}()
	close(start)

	select {
	case <-allClosed:
	case <-time.After(2 * time.Second):
		t.Fatal("concurrent connection and listener Close calls deadlocked")
	}
	select {
	case err := <-acceptDone:
		if !errors.Is(err, net.ErrClosed) {
			t.Fatalf("concurrent Accept error = %v, want underlying closed-listener error", err)
		}
	case <-time.After(time.Second):
		t.Fatal("listener Close did not unblock concurrent Accept")
	}
	if got := trackedConnectionCount(listener); got != 0 {
		t.Fatalf("tracked connections after concurrent close = %d, want 0", got)
	}

	for _, client := range clients {
		_ = client.Close()
	}
}
