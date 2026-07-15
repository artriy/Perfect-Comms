using System;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class DeviceListPublicationTests
{
    [Fact]
    public void IdenticalSidecarDeviceListsDoNotPublishAnotherUiVersion()
    {
        string suffix = Guid.NewGuid().ToString("N");
        var microphones = new[] { "Mic " + suffix };
        var speakers = new[] { "Speaker " + suffix };

        VoiceChatLocalSettings.SetMicDeviceNamesFromSidecar(microphones);
        int micVersion = VoiceChatLocalSettings.MicDeviceListVersion;
        VoiceChatLocalSettings.SetMicDeviceNamesFromSidecar(microphones);
        Assert.Equal(micVersion, VoiceChatLocalSettings.MicDeviceListVersion);

#if WINDOWS
        VoiceChatLocalSettings.SetSpkDeviceNamesFromSidecar(speakers);
        int speakerVersion = VoiceChatLocalSettings.SpkDeviceListVersion;
        VoiceChatLocalSettings.SetSpkDeviceNamesFromSidecar(speakers);
        Assert.Equal(speakerVersion, VoiceChatLocalSettings.SpkDeviceListVersion);
#endif
    }

    [Fact]
    public void AChangedSidecarDeviceListPublishesExactlyOneUiVersion()
    {
        string suffix = Guid.NewGuid().ToString("N");
        VoiceChatLocalSettings.SetMicDeviceNamesFromSidecar(new[] { "Mic A " + suffix });
        int before = VoiceChatLocalSettings.MicDeviceListVersion;

        VoiceChatLocalSettings.SetMicDeviceNamesFromSidecar(new[] { "Mic B " + suffix });

        Assert.Equal(before + 1, VoiceChatLocalSettings.MicDeviceListVersion);
    }
}
