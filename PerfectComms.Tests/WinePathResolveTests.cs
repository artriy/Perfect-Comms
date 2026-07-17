using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class WinePathResolveTests
{
    [Fact]
    public void BuildsWinePathUnixArguments()
    {
        var psi = WineEnvironment.BuildWinePathStartInfo(@"C:\Users\x\.wine\drive_c\pc-capture.exe");
        Xunit.Assert.Equal("winepath", psi.FileName);
        Xunit.Assert.Equal("-u \"C:\\Users\\x\\.wine\\drive_c\\pc-capture.exe\"", psi.Arguments);
        Xunit.Assert.True(psi.RedirectStandardOutput);
        Xunit.Assert.False(psi.UseShellExecute);
        Xunit.Assert.True(psi.CreateNoWindow);
    }

    [Fact]
    public void CheckedHostExecUsesWineWaitBeforeProgramAndCanObserveExitCode()
    {
        var psi = WineEnvironment.BuildHostExecStartInfo("/bin/chmod", "700 \"/tmp/private\"");

        Assert.Equal("start.exe", psi.FileName);
        Assert.Equal("/wait /unix \"/bin/chmod\" 700 \"/tmp/private\"", psi.Arguments);
        Assert.Equal(15_000, WineEnvironment.HostExecTimeoutMs);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
    }
}
