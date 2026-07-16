using System;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Process-local ownership gate for capture APIs that expose one global recording surface.
/// Unity's Microphone API is global per device, so a setup preview must never stop or replace
/// the live voice session's recording clip.
/// </summary>
internal sealed class ExclusiveCaptureLease
{
    private readonly object _sync = new();
    private long _owner;

    internal bool TryAcquire(long owner)
    {
        if (owner <= 0) throw new ArgumentOutOfRangeException(nameof(owner));
        lock (_sync)
        {
            if (_owner != 0 && _owner != owner) return false;
            _owner = owner;
            return true;
        }
    }

    internal bool IsOwnedBy(long owner)
    {
        lock (_sync) return owner > 0 && _owner == owner;
    }

    internal bool Release(long owner)
    {
        lock (_sync)
        {
            if (owner <= 0 || _owner != owner) return false;
            _owner = 0;
            return true;
        }
    }
}
