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
}
