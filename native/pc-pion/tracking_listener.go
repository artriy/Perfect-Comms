// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"net"
	"sync"
	"time"
)

type trackingTCPListener struct {
	inner              net.Listener
	firstPacketTimeout time.Duration

	mu          sync.Mutex
	connections map[*trackingTCPConn]struct{}
	closed      bool
	closeOnce   sync.Once
	closeErr    error
}

func newTrackingTCPListener(inner net.Listener, firstPacketTimeout time.Duration) net.Listener {
	return &trackingTCPListener{
		inner:              inner,
		firstPacketTimeout: firstPacketTimeout,
		connections:        make(map[*trackingTCPConn]struct{}),
	}
}

func (l *trackingTCPListener) Accept() (net.Conn, error) {
	for {
		conn, err := l.inner.Accept()
		if err != nil {
			return nil, err
		}

		initialDeadline := time.Now().Add(l.firstPacketTimeout)
		if err = conn.SetReadDeadline(initialDeadline); err != nil {
			_ = conn.Close()
			return nil, err
		}

		tracked := &trackingTCPConn{
			Conn:             conn,
			owner:            l,
			initialDeadline:  initialDeadline,
			firstReadPending: true,
		}

		l.mu.Lock()
		if l.closed {
			l.mu.Unlock()
			_ = tracked.Close()
			// Accept again so callers observe the underlying listener's close error.
			continue
		}
		l.connections[tracked] = struct{}{}
		l.mu.Unlock()

		return tracked, nil
	}
}

func (l *trackingTCPListener) Close() error {
	l.closeOnce.Do(func() {
		l.mu.Lock()
		l.closed = true
		l.mu.Unlock()

		l.closeErr = l.inner.Close()

		l.mu.Lock()
		connections := make([]*trackingTCPConn, 0, len(l.connections))
		for conn := range l.connections {
			connections = append(connections, conn)
		}
		l.mu.Unlock()

		// Connection Close unregisters from the listener, so do not hold l.mu here.
		for _, conn := range connections {
			_ = conn.Close()
		}
	})

	return l.closeErr
}

func (l *trackingTCPListener) Addr() net.Addr {
	return l.inner.Addr()
}

func (l *trackingTCPListener) unregister(conn *trackingTCPConn) {
	l.mu.Lock()
	delete(l.connections, conn)
	l.mu.Unlock()
}

type trackingTCPConn struct {
	net.Conn
	owner *trackingTCPListener

	closeOnce sync.Once
	closeErr  error

	deadlineMu       sync.Mutex
	initialDeadline  time.Time
	callerDeadline   time.Time
	firstReadPending bool
}

func (c *trackingTCPConn) Read(buffer []byte) (int, error) {
	n, err := c.Conn.Read(buffer)
	if n > 0 {
		c.deadlineMu.Lock()
		if c.firstReadPending {
			c.firstReadPending = false
			// Restore the deadline selected by the consumer instead of clearing a
			// later deadline that it installed while the first read was in flight.
			_ = c.Conn.SetReadDeadline(c.callerDeadline)
		}
		c.deadlineMu.Unlock()
	}
	return n, err
}

func (c *trackingTCPConn) SetDeadline(deadline time.Time) error {
	c.deadlineMu.Lock()
	defer c.deadlineMu.Unlock()

	if err := c.Conn.SetWriteDeadline(deadline); err != nil {
		return err
	}
	return c.setCallerReadDeadline(deadline)
}

func (c *trackingTCPConn) SetReadDeadline(deadline time.Time) error {
	c.deadlineMu.Lock()
	defer c.deadlineMu.Unlock()

	return c.setCallerReadDeadline(deadline)
}

func (c *trackingTCPConn) setCallerReadDeadline(deadline time.Time) error {
	effective := deadline
	if c.firstReadPending && (effective.IsZero() || c.initialDeadline.Before(effective)) {
		effective = c.initialDeadline
	}
	if err := c.Conn.SetReadDeadline(effective); err != nil {
		return err
	}
	c.callerDeadline = deadline
	return nil
}

func (c *trackingTCPConn) Close() error {
	c.closeOnce.Do(func() {
		c.closeErr = c.Conn.Close()
		c.owner.unregister(c)
	})
	return c.closeErr
}
