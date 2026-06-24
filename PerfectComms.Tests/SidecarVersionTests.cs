using VoiceChatPlugin.VoiceChat;
using Xunit;

public class SidecarVersionTests
{
    [Fact]
    public void Protocol_constant_is_one()
    {
        Xunit.Assert.Equal(1, SidecarVersion.Protocol);
    }

    [Fact]
    public void Matching_version_is_compatible_and_no_reextract()
    {
        Xunit.Assert.True(SidecarVersion.IsCompatible(1));
        Xunit.Assert.False(SidecarVersion.ShouldReExtract(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void Mismatched_version_is_rejected_and_triggers_reextract(int helperProto)
    {
        Xunit.Assert.False(SidecarVersion.IsCompatible(helperProto));
        Xunit.Assert.True(SidecarVersion.ShouldReExtract(helperProto));
    }
}
