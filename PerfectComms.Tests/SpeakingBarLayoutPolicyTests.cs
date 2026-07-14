using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SpeakingBarLayoutPolicyTests
{
    [Theory]
    [InlineData((int)SpeakingBarPosition.TopLeft, (int)VoiceControlsLayout.Vertical, (int)SpeakingBarNamePosition.Right,
        (int)SpeakingBarLayoutDirection.Down, (int)SpeakingBarLayoutDirection.Right)]
    [InlineData((int)SpeakingBarPosition.TopMiddle, (int)VoiceControlsLayout.Horizontal, (int)SpeakingBarNamePosition.Bottom,
        (int)SpeakingBarLayoutDirection.Right, (int)SpeakingBarLayoutDirection.Down)]
    [InlineData((int)SpeakingBarPosition.TopRight, (int)VoiceControlsLayout.Vertical, (int)SpeakingBarNamePosition.Left,
        (int)SpeakingBarLayoutDirection.Down, (int)SpeakingBarLayoutDirection.Left)]
    [InlineData((int)SpeakingBarPosition.MiddleLeft, (int)VoiceControlsLayout.Vertical, (int)SpeakingBarNamePosition.Right,
        (int)SpeakingBarLayoutDirection.Down, (int)SpeakingBarLayoutDirection.Right)]
    [InlineData((int)SpeakingBarPosition.MiddleRight, (int)VoiceControlsLayout.Vertical, (int)SpeakingBarNamePosition.Left,
        (int)SpeakingBarLayoutDirection.Down, (int)SpeakingBarLayoutDirection.Left)]
    [InlineData((int)SpeakingBarPosition.BottomLeft, (int)VoiceControlsLayout.Vertical, (int)SpeakingBarNamePosition.Right,
        (int)SpeakingBarLayoutDirection.Up, (int)SpeakingBarLayoutDirection.Right)]
    [InlineData((int)SpeakingBarPosition.BottomMiddle, (int)VoiceControlsLayout.Horizontal, (int)SpeakingBarNamePosition.Top,
        (int)SpeakingBarLayoutDirection.Right, (int)SpeakingBarLayoutDirection.Up)]
    [InlineData((int)SpeakingBarPosition.BottomRight, (int)VoiceControlsLayout.Vertical, (int)SpeakingBarNamePosition.Left,
        (int)SpeakingBarLayoutDirection.Up, (int)SpeakingBarLayoutDirection.Left)]
    public void PresetsExposeRequestedOrientationNameAndFlow(
        int positionValue,
        int orientationValue,
        int namePositionValue,
        int primaryDirectionValue,
        int overflowDirectionValue)
    {
        var position = (SpeakingBarPosition)positionValue;
        var orientation = (VoiceControlsLayout)orientationValue;
        var namePosition = (SpeakingBarNamePosition)namePositionValue;
        var primaryDirection = (SpeakingBarLayoutDirection)primaryDirectionValue;
        var overflowDirection = (SpeakingBarLayoutDirection)overflowDirectionValue;
        var preset = SpeakingBarLayoutPolicy.ForPreset(position);

        Assert.Equal(orientation, preset.Orientation);
        Assert.Equal(namePosition, preset.RecommendedNamePosition);
        Assert.Equal(primaryDirection, preset.PrimaryDirection);
        Assert.Equal(overflowDirection, preset.OverflowDirection);
        Assert.Equal(orientation, SpeakingBarLayoutPolicy.OrientationFor(position));
        Assert.Equal(namePosition, SpeakingBarLayoutPolicy.RecommendedNamePositionFor(position));
        Assert.Equal(primaryDirection, SpeakingBarLayoutPolicy.PrimaryDirectionFor(position));
        Assert.Equal(overflowDirection, SpeakingBarLayoutPolicy.OverflowDirectionFor(position));
    }

    [Fact]
    public void PreferredCapacityStartsBalancedWrappingAtTenPlayers()
    {
        Assert.Equal(new[] { 9 }, LineSizes(9));
        Assert.Equal(new[] { 5, 5 }, LineSizes(10));
        Assert.Equal(new[] { 6, 5 }, LineSizes(11));
        Assert.Equal(new[] { 8, 7 }, LineSizes(15));
        Assert.Equal(new[] { 9, 9 }, LineSizes(18));
        Assert.Equal(new[] { 7, 6, 6 }, LineSizes(19));
    }

    [Fact]
    public void SafeCapacityCanAddBalancedLinesBeforeTenPlayers()
    {
        Assert.Equal(new[] { 4, 4 }, LineSizes(8, safeCapacity: 5));
        Assert.Equal(new[] { 3, 3, 3 }, LineSizes(9, safeCapacity: 4));
        Assert.Equal(new[] { 5, 5, 5 }, LineSizes(15, safeCapacity: 6));
    }

    [Fact]
    public void PlacementReportsBalancedLineIndexOffsetAndSize()
    {
        int lineCount = SpeakingBarLayoutPolicy.GetLineCount(11);

        Assert.Equal(new SpeakingBarLinePlacement(0, 0, 6),
            SpeakingBarLayoutPolicy.GetPlacement(0, 11, lineCount));
        Assert.Equal(new SpeakingBarLinePlacement(0, 5, 6),
            SpeakingBarLayoutPolicy.GetPlacement(5, 11, lineCount));
        Assert.Equal(new SpeakingBarLinePlacement(1, 0, 5),
            SpeakingBarLayoutPolicy.GetPlacement(6, 11, lineCount));
        Assert.Equal(new SpeakingBarLinePlacement(1, 4, 5),
            SpeakingBarLayoutPolicy.GetPlacement(10, 11, lineCount));

        Assert.Equal(6, SpeakingBarLayoutPolicy.GetLineStartIndex(11, lineCount, 1));
        Assert.Equal(1, SpeakingBarLayoutPolicy.GetLineIndex(10, 11, lineCount));
        Assert.Equal(4, SpeakingBarLayoutPolicy.GetIndexInLine(10, 11, lineCount));
    }

    [Fact]
    public void EmptyRosterHasNoLinesAndZeroSafeCapacityFallsBackToOnePerLine()
    {
        Assert.Equal(0, SpeakingBarLayoutPolicy.GetLineCount(0));
        Assert.Equal(3, SpeakingBarLayoutPolicy.GetLineCount(3, safeCapacity: 0));
    }

    private static int[] LineSizes(int itemCount, int safeCapacity = int.MaxValue)
    {
        int lineCount = SpeakingBarLayoutPolicy.GetLineCount(itemCount, safeCapacity);
        var sizes = new int[lineCount];
        for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
            sizes[lineIndex] = SpeakingBarLayoutPolicy.GetLineSize(itemCount, lineCount, lineIndex);
        return sizes;
    }
}
