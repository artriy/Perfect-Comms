using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SidecarDiagnosticsSafetyTests
{
    [Fact]
    public void StderrSanitizerRedactsTokensAndSignalingBodies()
    {
        const string token = "0123456789ABCDEF0123456789ABCDEF";

        var tokenLine = SidecarLauncher.SanitizeStderrForDiagnostics(
            $"fatal token={token}\r\nnext-line",
            token);
        var candidateLine = SidecarLauncher.SanitizeStderrForDiagnostics(
            "candidate=candidate:1 1 udp 2122260223 192.0.2.1 5000 typ host",
            token);
        var sdpLine = SidecarLauncher.SanitizeStderrForDiagnostics(
            "remote sdp: v=0 o=- 123 2 IN IP4 127.0.0.1",
            token);

        Assert.DoesNotContain(token, tokenLine);
        Assert.DoesNotContain('\r', tokenLine);
        Assert.DoesNotContain('\n', tokenLine);
        Assert.Contains("token=[redacted]", tokenLine);
        Assert.DoesNotContain("192.0.2.1", candidateLine);
        Assert.Contains("candidate=[redacted]", candidateLine);
        Assert.DoesNotContain("v=0", sdpLine);
        Assert.Contains("sdp:[redacted]", sdpLine);
    }

    [Fact]
    public void StderrSanitizerBoundsLineLength()
    {
        var safe = SidecarLauncher.SanitizeStderrForDiagnostics(new string('x', 2000), string.Empty);

        Assert.Equal(SidecarProcessDiagnostics.MaxLineChars + 3, safe.Length);
        Assert.EndsWith("...", safe);
    }

    [Fact]
    public void DeviceDiagnosticsUseStableFingerprintWithoutRawId()
    {
        const string id = "private microphone device name";

        var first = SidecarVoiceClient.DescribeDeviceForDiagnostics(id);
        var second = SidecarVoiceClient.DescribeDeviceForDiagnostics(id);

        Assert.Equal(first, second);
        Assert.DoesNotContain(id, first);
        Assert.Contains("default=false", first);
        Assert.Contains("idHash=", first);
        Assert.Equal("default=true", SidecarVoiceClient.DescribeDeviceForDiagnostics(string.Empty));
    }
}
