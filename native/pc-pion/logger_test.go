// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"errors"
	"net"
	"sync/atomic"
	"testing"
)

type recordingLogger struct {
	debugf int
	errorf int
}

func (*recordingLogger) Trace(string)            {}
func (*recordingLogger) Tracef(string, ...any)   {}
func (*recordingLogger) Debug(string)            {}
func (l *recordingLogger) Debugf(string, ...any) { l.debugf++ }
func (*recordingLogger) Info(string)             {}
func (*recordingLogger) Infof(string, ...any)    {}
func (*recordingLogger) Warn(string)             {}
func (*recordingLogger) Warnf(string, ...any)    {}
func (*recordingLogger) Error(string)            {}
func (l *recordingLogger) Errorf(string, ...any) { l.errorf++ }

func TestMuxLoggerOnlyDowngradesExpectedShutdownReadError(t *testing.T) {
	var closing atomic.Bool
	recorder := &recordingLogger{}
	logger := &muxLoggerFilter{LeveledLogger: recorder, closing: &closing}
	closedErr := &net.OpError{Op: "read", Net: "udp", Err: net.ErrClosed}

	logger.Errorf("Failed to read UDP packet: %v", closedErr)
	if recorder.errorf != 1 || recorder.debugf != 0 {
		t.Fatalf("pre-shutdown log counts = error:%d debug:%d", recorder.errorf, recorder.debugf)
	}

	closing.Store(true)
	logger.Errorf("Failed to read UDP packet: %v", closedErr)
	logger.Errorf("Failed to read UDP packet: %v", errors.New("read udp: use of closed network connection"))
	if recorder.errorf != 1 || recorder.debugf != 2 {
		t.Fatalf("shutdown log counts = error:%d debug:%d", recorder.errorf, recorder.debugf)
	}

	logger.Errorf("Failed to read UDP packet: %v", errors.New("permission denied"))
	logger.Errorf("another mux error: %v", closedErr)
	if recorder.errorf != 3 || recorder.debugf != 2 {
		t.Fatalf("real error log counts = error:%d debug:%d", recorder.errorf, recorder.debugf)
	}
}
