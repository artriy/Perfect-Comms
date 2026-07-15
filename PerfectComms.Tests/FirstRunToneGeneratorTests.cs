using System;
using System.IO;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class FirstRunToneGeneratorTests
{
    [Fact]
    public void ChimeFramesAreFiniteBoundedStereoAndAudible()
    {
        float peak = 0f;
        double energy = 0d;
        for (int frame = 0; frame < FirstRunToneGenerator.FrameCount; frame++)
        {
            var samples = FirstRunToneGenerator.CreateFrame(frame, 1f);
            Assert.Equal(SidecarProtocol.AudioOutSamples, samples.Length);
            for (int i = 0; i < samples.Length; i += 2)
            {
                float left = samples[i];
                float right = samples[i + 1];
                Assert.True(float.IsFinite(left));
                Assert.InRange(left, -0.7f, 0.7f);
                Assert.Equal(left, right);
                peak = Math.Max(peak, Math.Abs(left));
                energy += left * left;
            }
        }

        Assert.True(peak > 0.05f);
        Assert.True(energy > 1d);
        Assert.Equal(0f, FirstRunToneGenerator.CreateFrame(0, 1f)[0]);
        Assert.True(Math.Abs(FirstRunToneGenerator.CreateFrame(
            FirstRunToneGenerator.FrameCount - 1, 1f)[^1]) < 0.00001f);
    }

    [Fact]
    public void InvalidOrMutedVolumeCannotCreateUnsafeSamples()
    {
        Assert.All(FirstRunToneGenerator.CreateFrame(10, float.NaN), sample => Assert.Equal(0f, sample));
        Assert.All(FirstRunToneGenerator.CreateFrame(10, float.PositiveInfinity), sample => Assert.Equal(0f, sample));
        Assert.All(FirstRunToneGenerator.CreateFrame(10, -1f), sample => Assert.Equal(0f, sample));
        Assert.Equal(
            FirstRunToneGenerator.CreateFrame(10, 2f),
            FirstRunToneGenerator.CreateFrame(10, 200f));
        Assert.Throws<ArgumentOutOfRangeException>(() => FirstRunToneGenerator.CreateFrame(-1, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FirstRunToneGenerator.CreateFrame(FirstRunToneGenerator.FrameCount, 1f));
    }

    [Fact]
    public void DesktopWorkerAndReaderCallbackStayFreeOfUnityCalls()
    {
        string root = FindRepositoryRoot();
        string generator = File.ReadAllText(Path.Combine(root, "Comms", "FirstRunToneGenerator.cs"));
        Assert.DoesNotContain("UnityEngine", generator);
        Assert.DoesNotContain("Mathf", generator);

        string preview = File.ReadAllText(Path.Combine(root, "Comms", "FirstRunAudioPreview.cs"));
        string worker = Slice(preview, "_ = Task.Run", "        });");
        Assert.DoesNotContain("UnityEngine", worker);
        Assert.DoesNotContain("Mathf", worker);

        string levelCallback = Slice(preview, "private void OnLevel", "private void MarkListening");
        Assert.DoesNotContain("UnityEngine", levelCallback);
        Assert.DoesNotContain("Mathf", levelCallback);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source.Substring(start, end - start);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PerfectComms.csproj")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the PerfectComms repository root.");
    }
}
