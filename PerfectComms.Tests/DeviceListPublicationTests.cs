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

    [Fact]
    public void StructuredListsPublishNamesForUiAndIdsForSelection()
    {
        string suffix = Guid.NewGuid().ToString("N");
        var devices = new[]
        {
            new VoiceDeviceInfo("stable-a-" + suffix, "Same Name", false),
            new VoiceDeviceInfo("stable-b-" + suffix, "Same Name", true),
        };

        VoiceChatLocalSettings.SetMicDevicesFromSidecar(devices);

        Assert.Equal(new[] { "Default", "Same Name", "Same Name" }, VoiceChatLocalSettings.MicDeviceNames);
        Assert.Equal("stable-a-" + suffix, VoiceChatLocalSettings.MicDevices[1].Id);
        Assert.Equal("stable-b-" + suffix, VoiceChatLocalSettings.MicDevices[2].Id);
    }

#if WINDOWS
    [Fact]
    public void FriendlyNamesSurviveStructuredProtocolParsePublicationAndSetupDisplay()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string micId = "wasapi-mic-" + suffix;
        string speakerId = "wasapi-speaker-" + suffix;
        const string micName = "Razer Seiren Mini";
        const string speakerName = "HyperX Cloud III";
        string json = "{\"op\":\"devices\",\"devices\":[{" +
                      "\"id\":\"" + micId + "\",\"name\":\"" + micName + "\",\"default\":true}]," +
                      "\"outputDevices\":[{" +
                      "\"id\":\"" + speakerId + "\",\"name\":\"" + speakerName + "\",\"default\":true}]}";

        Assert.True(SidecarVoiceClient.TryReadDeviceUpdate(
            json, out var microphones, out var speakers));
        Assert.True(VoiceChatLocalSettings.PublishSidecarDeviceEnumeration(
            SidecarDeviceEnumerationResult.Success(microphones, speakers)));

        int micIndex = Array.FindIndex(
            VoiceChatLocalSettings.MicDevices, device => device.Id == micId);
        int speakerIndex = Array.FindIndex(
            VoiceChatLocalSettings.SpkDevices, device => device.Id == speakerId);
        Assert.True(micIndex > 0);
        Assert.True(speakerIndex > 0);
        Assert.Equal(micName, VoiceChatLocalSettings.MicDeviceNames[micIndex]);
        Assert.Equal(speakerName, VoiceChatLocalSettings.SpkDeviceNames[speakerIndex]);

        var draft = new FirstRunSetupDraft
        {
            MicrophoneDevice = micId,
            MicrophoneDeviceName = "Microphone",
            SpeakerDevice = speakerId,
            SpeakerDeviceName = "Headphones",
        };
        Assert.Equal(micName, draft.MicrophoneDisplayName());
        Assert.Equal(speakerName, draft.SpeakerDisplayName());
    }

    [Fact]
    public void FailedEnumerationRetainsLastKnownGoodDeviceLists()
    {
        string suffix = Guid.NewGuid().ToString("N");
        var microphones = new[]
        {
            new VoiceDeviceInfo("mic-" + suffix, "Known Mic", true),
        };
        var speakers = new[]
        {
            new VoiceDeviceInfo("spk-" + suffix, "Known Speaker", true),
        };
        Assert.True(VoiceChatLocalSettings.PublishSidecarDeviceEnumeration(
            SidecarDeviceEnumerationResult.Success(microphones, speakers)));
        int micVersion = VoiceChatLocalSettings.MicDeviceListVersion;
        int speakerVersion = VoiceChatLocalSettings.SpkDeviceListVersion;

        Assert.False(VoiceChatLocalSettings.PublishSidecarDeviceEnumeration(
            SidecarDeviceEnumerationResult.Failure));

        Assert.Equal(micVersion, VoiceChatLocalSettings.MicDeviceListVersion);
        Assert.Equal(speakerVersion, VoiceChatLocalSettings.SpkDeviceListVersion);
        Assert.Equal("mic-" + suffix, VoiceChatLocalSettings.MicDevices[1].Id);
        Assert.Equal("spk-" + suffix, VoiceChatLocalSettings.SpkDevices[1].Id);
    }

    [Fact]
    public void AuthoritativeEmptyEnumerationPublishesEmptyEndpointLists()
    {
        string suffix = Guid.NewGuid().ToString("N");
        VoiceChatLocalSettings.SetMicDevicesFromSidecar(new[]
        {
            new VoiceDeviceInfo("mic-" + suffix, "Old Mic", true),
        });
        VoiceChatLocalSettings.SetSpkDevicesFromSidecar(new[]
        {
            new VoiceDeviceInfo("spk-" + suffix, "Old Speaker", true),
        });

        Assert.True(VoiceChatLocalSettings.PublishSidecarDeviceEnumeration(
            SidecarDeviceEnumerationResult.Success(
                Array.Empty<VoiceDeviceInfo>(), Array.Empty<VoiceDeviceInfo>())));

        Assert.Equal(new[] { "Default" }, VoiceChatLocalSettings.MicDeviceNames);
        Assert.Equal(new[] { "Default" }, VoiceChatLocalSettings.SpkDeviceNames);
        Assert.Single(VoiceChatLocalSettings.MicDevices);
        Assert.Single(VoiceChatLocalSettings.SpkDevices);
    }
#endif
}
