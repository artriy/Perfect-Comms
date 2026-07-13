using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceChatPlugin.VoiceChat;

internal readonly record struct SignalingSessionScope(int GameId, int LocalClientId)
{
    public bool IsValid => GameId != 0 && LocalClientId >= 0;
}

internal readonly record struct DeferredSignalingHello(
    SignalingSessionScope Scope,
    int SenderClientId,
    uint SenderNetId,
    byte[] Payload,
    long ReceivedAtMs);

/// <summary>
/// Retains only the compatibility Hello needed to bootstrap a voice backend that has not attached
/// its signaling subscriber yet. SDP, candidates and control messages deliberately never enter
/// this mailbox: unlike Hello they belong to a particular peer negotiation and can be unsafe to
/// replay after a backend replacement.
/// </summary>
internal sealed class SignalingHelloMailbox
{
    internal const int DefaultCapacity = 32;
    internal const long DefaultTtlMs = 15_000;

    private readonly object _sync = new();
    private readonly Dictionary<(SignalingSessionScope Scope, int SenderClientId), Entry> _entries = new();
    private readonly int _capacity;
    private readonly long _ttlMs;
    private long _sequence;

    private sealed class Entry
    {
        public SignalingSessionScope Scope { get; init; }
        public int SenderClientId { get; init; }
        public uint SenderNetId { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public long ReceivedAtMs { get; set; }
        public long Sequence { get; set; }
    }

    internal SignalingHelloMailbox(int capacity = DefaultCapacity, long ttlMs = DefaultTtlMs)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (ttlMs <= 0) throw new ArgumentOutOfRangeException(nameof(ttlMs));
        _capacity = capacity;
        _ttlMs = ttlMs;
    }

    internal int Count
    {
        get
        {
            lock (_sync) return _entries.Count;
        }
    }

    internal bool Store(
        SignalingSessionScope scope,
        int senderClientId,
        uint senderNetId,
        byte[] payload,
        long nowMs,
        out string result)
    {
        if (!scope.IsValid)
        {
            result = "invalid-session";
            return false;
        }
        if (senderClientId < 0)
        {
            result = "invalid-sender";
            return false;
        }
        if (payload == null || payload.Length == 0)
        {
            result = "invalid-payload";
            return false;
        }

        lock (_sync)
        {
            PruneExpiredLocked(nowMs);
            var key = (scope, senderClientId);
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.SenderNetId = senderNetId;
                existing.Payload = (byte[])payload.Clone();
                existing.ReceivedAtMs = nowMs;
                existing.Sequence = NextSequenceLocked();
                result = "replaced";
                return true;
            }

            result = "stored";
            if (_entries.Count >= _capacity)
            {
                var oldest = _entries.OrderBy(pair => pair.Value.Sequence).First();
                _entries.Remove(oldest.Key);
                result = "stored-after-oldest-evicted";
            }

            _entries[key] = new Entry
            {
                Scope = scope,
                SenderClientId = senderClientId,
                SenderNetId = senderNetId,
                Payload = (byte[])payload.Clone(),
                ReceivedAtMs = nowMs,
                Sequence = NextSequenceLocked(),
            };
            return true;
        }
    }

    internal bool Remove(SignalingSessionScope scope, int senderClientId)
    {
        lock (_sync)
            return _entries.Remove((scope, senderClientId));
    }

    internal IReadOnlyList<DeferredSignalingHello> Drain(
        SignalingSessionScope scope,
        Func<int, bool> senderIsPresent,
        long nowMs,
        out int expired,
        out int wrongSession,
        out int absent)
    {
        if (senderIsPresent == null) throw new ArgumentNullException(nameof(senderIsPresent));

        lock (_sync)
        {
            expired = 0;
            wrongSession = 0;
            absent = 0;
            var ready = new List<DeferredSignalingHello>();

            foreach (var pair in _entries.OrderBy(pair => pair.Value.Sequence).ToArray())
            {
                var entry = pair.Value;
                _entries.Remove(pair.Key);
                if (HasExpired(entry.ReceivedAtMs, nowMs))
                {
                    expired++;
                    continue;
                }
                if (entry.Scope != scope)
                {
                    wrongSession++;
                    continue;
                }
                if (!senderIsPresent(entry.SenderClientId))
                {
                    absent++;
                    continue;
                }

                ready.Add(new DeferredSignalingHello(
                    entry.Scope,
                    entry.SenderClientId,
                    entry.SenderNetId,
                    (byte[])entry.Payload.Clone(),
                    entry.ReceivedAtMs));
            }

            return ready;
        }
    }

    internal void Clear()
    {
        lock (_sync)
            _entries.Clear();
    }

    private void PruneExpiredLocked(long nowMs)
    {
        foreach (var pair in _entries.ToArray())
            if (HasExpired(pair.Value.ReceivedAtMs, nowMs))
                _entries.Remove(pair.Key);
    }

    private bool HasExpired(long receivedAtMs, long nowMs)
        => nowMs >= receivedAtMs && nowMs - receivedAtMs > _ttlMs;

    private long NextSequenceLocked()
    {
        if (_sequence == long.MaxValue) _sequence = 0;
        return ++_sequence;
    }
}
