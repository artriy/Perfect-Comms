#if ANDROID
using System.Linq;
using Xunit;

namespace VoiceChatPlugin.VoiceChat.Tests;

public sealed class AndroidMicrophoneMonitorTests
{
    [Fact]
    public void GainOnlyReconfigurationPreservesPrimedAudio()
    {
        var monitor = new AndroidMicrophoneMonitor();
        monitor.Configure(enabled: true, delayed: false, gain: 0.5f);
        var captured = Enumerable.Repeat(0.25f, 4_000).ToArray();
        monitor.Write(captured, captured.Length);

        monitor.Configure(enabled: true, delayed: false, gain: 2f);

        var stereo = new float[3_840];
        monitor.MixInto(stereo, stereo.Length);
        Assert.Contains(stereo, sample => sample > 0.45f);
    }

    [Fact]
    public void DelayTopologyChangeDropsOldAudio()
    {
        var monitor = new AndroidMicrophoneMonitor();
        monitor.Configure(enabled: true, delayed: false, gain: 1f);
        var captured = Enumerable.Repeat(0.5f, 4_000).ToArray();
        monitor.Write(captured, captured.Length);

        monitor.Configure(enabled: true, delayed: true, gain: 1f);

        var stereo = new float[3_840];
        monitor.MixInto(stereo, stereo.Length);
        Assert.All(stereo, sample => Assert.Equal(0f, sample));
    }
}
#endif
