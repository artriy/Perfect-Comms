// SPDX-License-Identifier: LGPL-2.1-only

package main

import (
	"errors"
	"net"
	"strings"
	"sync/atomic"

	"github.com/pion/logging"
)

// muxLoggerFilter is scoped only to the shared ICE UDP mux. Pion's mux read
// worker can observe the socket close just before its closed flag and otherwise
// logs a normal shutdown as an error. Real read failures still pass through.
type muxLoggerFilter struct {
	logging.LeveledLogger
	closing *atomic.Bool
}

func newMuxLogger(closing *atomic.Bool) logging.LeveledLogger {
	return &muxLoggerFilter{
		LeveledLogger: logging.NewDefaultLoggerFactory().NewLogger("ice-udp-mux"),
		closing:       closing,
	}
}

func (l *muxLoggerFilter) Errorf(format string, args ...any) {
	if l.expectedShutdownReadError(format, args) {
		l.LeveledLogger.Debugf(format, args...)
		return
	}
	l.LeveledLogger.Errorf(format, args...)
}

func (l *muxLoggerFilter) expectedShutdownReadError(format string, args []any) bool {
	if l.closing == nil || !l.closing.Load() || format != "Failed to read UDP packet: %v" || len(args) != 1 {
		return false
	}
	err, ok := args[0].(error)
	if !ok {
		return false
	}
	return errors.Is(err, net.ErrClosed) || strings.Contains(err.Error(), "use of closed network connection")
}
