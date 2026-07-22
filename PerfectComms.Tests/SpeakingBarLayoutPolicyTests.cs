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

    [Theory]
    [InlineData((int)SpeakingBarPosition.TopLeft, true)]
    [InlineData((int)SpeakingBarPosition.TopMiddle, false)]
    [InlineData((int)SpeakingBarPosition.TopRight, true)]
    [InlineData((int)SpeakingBarPosition.MiddleLeft, true)]
    [InlineData((int)SpeakingBarPosition.MiddleRight, true)]
    [InlineData((int)SpeakingBarPosition.BottomLeft, true)]
    [InlineData((int)SpeakingBarPosition.BottomMiddle, false)]
    [InlineData((int)SpeakingBarPosition.BottomRight, true)]
    public void SidePresetClassificationIsStable(int positionValue, bool sidePreset)
    {
        Assert.Equal(sidePreset,
            SpeakingBarLayoutPolicy.IsSidePreset((SpeakingBarPosition)positionValue));
    }

    [Theory]
    [InlineData((int)SpeakingBarPosition.TopLeft, (int)SpeakingBarSideLayout.SingleLane, true)]
    [InlineData((int)SpeakingBarPosition.TopLeft, (int)SpeakingBarSideLayout.Wrapped, false)]
    [InlineData((int)SpeakingBarPosition.MiddleLeft, (int)SpeakingBarSideLayout.SingleLane, true)]
    [InlineData((int)SpeakingBarPosition.MiddleLeft, (int)SpeakingBarSideLayout.Wrapped, false)]
    [InlineData((int)SpeakingBarPosition.MiddleRight, (int)SpeakingBarSideLayout.SingleLane, true)]
    [InlineData((int)SpeakingBarPosition.BottomRight, (int)SpeakingBarSideLayout.Wrapped, false)]
    [InlineData((int)SpeakingBarPosition.TopMiddle, (int)SpeakingBarSideLayout.SingleLane, false)]
    [InlineData((int)SpeakingBarPosition.BottomMiddle, (int)SpeakingBarSideLayout.SingleLane, false)]
    public void SideLayoutChoiceControlsOnlySidePresetWrapping(
        int positionValue,
        int sideLayoutValue,
        bool singleLane)
    {
        Assert.Equal(singleLane, SpeakingBarLayoutPolicy.UsesSingleVerticalLaneFor(
            (SpeakingBarPosition)positionValue,
            (SpeakingBarSideLayout)sideLayoutValue));
    }

    [Theory]
    [InlineData((int)SpeakingBarPosition.TopLeft, false)]
    [InlineData((int)SpeakingBarPosition.TopMiddle, false)]
    [InlineData((int)SpeakingBarPosition.TopRight, true)]
    [InlineData((int)SpeakingBarPosition.MiddleLeft, false)]
    [InlineData((int)SpeakingBarPosition.MiddleRight, true)]
    [InlineData((int)SpeakingBarPosition.BottomLeft, false)]
    [InlineData((int)SpeakingBarPosition.BottomMiddle, false)]
    [InlineData((int)SpeakingBarPosition.BottomRight, true)]
    public void RightSidePresetsFaceAvatarsTowardTheScreen(int positionValue, bool facesLeft)
    {
        Assert.Equal(facesLeft,
            SpeakingBarLayoutPolicy.AvatarFacesLeftFor((SpeakingBarPosition)positionValue));
    }

    [Theory]
    [InlineData((int)SpeakingBarAvatarFacing.Right, false)]
    [InlineData((int)SpeakingBarAvatarFacing.Left, true)]
    public void ManualLayoutUsesTheSavedAvatarFacingChoice(int facingValue, bool facesLeft)
    {
        Assert.Equal(facesLeft, SpeakingBarLayoutPolicy.ResolveAvatarFacesLeft(
            manualLayout: true,
            SpeakingBarPosition.TopRight,
            (SpeakingBarAvatarFacing)facingValue));
    }

    [Fact]
    public void PresetLayoutIgnoresTheSavedManualAvatarFacingChoice()
    {
        Assert.True(SpeakingBarLayoutPolicy.ResolveAvatarFacesLeft(
            manualLayout: false,
            SpeakingBarPosition.TopRight,
            SpeakingBarAvatarFacing.Right));
        Assert.False(SpeakingBarLayoutPolicy.ResolveAvatarFacesLeft(
            manualLayout: false,
            SpeakingBarPosition.TopLeft,
            SpeakingBarAvatarFacing.Left));
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

    [Fact]
    public void TwoDimensionalSolverKeepsBalancedNineItemLimitWhenItFits()
    {
        var solution = Solve(itemCount: 15, vertical: false, availableWidth: 8f, availableHeight: 2f);

        Assert.Equal(2, solution.LineCount);
        Assert.Equal(8, solution.MaxItemsInLine);
        Assert.Equal(8f, solution.ContentWidth);
        Assert.Equal(2f, solution.ContentHeight);
        Assert.Equal(1f, solution.EffectiveScale);
        Assert.True(solution.FitsRequestedScale);
    }

    [Fact]
    public void TwoDimensionalSolverAddsRowsBeforeShrinkingRequestedScale()
    {
        var solution = Solve(itemCount: 15, vertical: false, availableWidth: 5f, availableHeight: 3f);

        Assert.Equal(3, solution.LineCount);
        Assert.Equal(5, solution.MaxItemsInLine);
        Assert.Equal(5f, solution.ContentWidth);
        Assert.Equal(3f, solution.ContentHeight);
        Assert.Equal(1f, solution.EffectiveScale);
        Assert.True(solution.FitsRequestedScale);
    }

    [Fact]
    public void TwoDimensionalSolverAddsColumnsBeforeShrinkingRequestedScale()
    {
        var solution = Solve(itemCount: 15, vertical: true, availableWidth: 3f, availableHeight: 5f);

        Assert.Equal(3, solution.LineCount);
        Assert.Equal(5, solution.MaxItemsInLine);
        Assert.Equal(3f, solution.ContentWidth);
        Assert.Equal(5f, solution.ContentHeight);
        Assert.Equal(1f, solution.EffectiveScale);
        Assert.True(solution.FitsRequestedScale);
    }

    [Fact]
    public void TwoDimensionalSolverMaximizesScaleWhenNoLayoutFitsRequestedScale()
    {
        var solution = Solve(itemCount: 15, vertical: false, availableWidth: 3f, availableHeight: 3f);

        Assert.Equal(4, solution.LineCount);
        Assert.Equal(4, solution.MaxItemsInLine);
        Assert.Equal(4f, solution.ContentWidth);
        Assert.Equal(4f, solution.ContentHeight);
        Assert.Equal(0.75f, solution.EffectiveScale);
        Assert.False(solution.FitsRequestedScale);
    }

    [Fact]
    public void TwoDimensionalSolverTestsRequestedRenderedScaleBeforeAddingLines()
    {
        var solution = Solve(
            itemCount: 15,
            vertical: false,
            availableWidth: 4f,
            availableHeight: 1f,
            requestedScale: 0.5f);

        Assert.Equal(2, solution.LineCount);
        Assert.Equal(0.5f, solution.EffectiveScale);
        Assert.True(solution.FitsRequestedScale);
    }

    [Fact]
    public void TwoDimensionalSolverSupportsAConfigurableMaximumItemsPerLine()
    {
        var solution = SpeakingBarLayoutPolicy.SolveTwoDimensional(
            itemCount: 4,
            vertical: true,
            itemWidth: 1f,
            itemHeight: 1f,
            horizontalPitch: 1f,
            verticalPitch: 1f,
            availableWidth: 4f,
            availableHeight: 4f,
            requestedScale: 1f,
            maxItemsPerLine: 1);

        Assert.Equal(4, solution.LineCount);
        Assert.Equal(1, solution.MaxItemsInLine);
        Assert.True(solution.FitsRequestedScale);
    }

    [Fact]
    public void RequiredSingleLaneShrinksInsteadOfAddingASecondColumn()
    {
        var solution = SpeakingBarLayoutPolicy.SolveTwoDimensional(
            itemCount: 15,
            vertical: true,
            itemWidth: 1f,
            itemHeight: 1f,
            horizontalPitch: 1f,
            verticalPitch: 1f,
            availableWidth: 4f,
            availableHeight: 5f,
            requestedScale: 1f,
            maxItemsPerLine: 15,
            requiredLineCount: 1);

        Assert.Equal(1, solution.LineCount);
        Assert.Equal(15, solution.MaxItemsInLine);
        Assert.Equal(1f / 3f, solution.EffectiveScale, 4);
        Assert.False(solution.FitsRequestedScale);
    }

    [Fact]
    public void TwoDimensionalSolverBreaksEqualScaleTiesWithFewerLines()
    {
        var solution = Solve(itemCount: 2, vertical: false, availableWidth: 1f, availableHeight: 1f);

        Assert.Equal(1, solution.LineCount);
        Assert.Equal(0.5f, solution.EffectiveScale);
        Assert.False(solution.FitsRequestedScale);
    }

    [Theory]
    [InlineData(15, false, 2)]
    [InlineData(15, true, 1)]
    [InlineData(10, false, 2)]
    [InlineData(9, false, 1)]
    public void MiniaturePreviewPreservesLiveHudTopology(
        int itemCount,
        bool singleLane,
        int expectedLineCount)
    {
        Assert.Equal(
            expectedLineCount,
            SpeakingBarLivePreviewLayoutPolicy.RequiredLineCount(itemCount, singleLane));
    }

    private static int[] LineSizes(int itemCount, int safeCapacity = int.MaxValue)
    {
        int lineCount = SpeakingBarLayoutPolicy.GetLineCount(itemCount, safeCapacity);
        var sizes = new int[lineCount];
        for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
            sizes[lineIndex] = SpeakingBarLayoutPolicy.GetLineSize(itemCount, lineCount, lineIndex);
        return sizes;
    }

    private static SpeakingBarLayoutSolution Solve(
        int itemCount,
        bool vertical,
        float availableWidth,
        float availableHeight,
        float requestedScale = 1f)
        => SpeakingBarLayoutPolicy.SolveTwoDimensional(
            itemCount,
            vertical,
            itemWidth: 1f,
            itemHeight: 1f,
            horizontalPitch: 1f,
            verticalPitch: 1f,
            availableWidth,
            availableHeight,
            requestedScale,
            maxItemsPerLine: SpeakingBarLayoutPolicy.PreferredMaxItemsPerLine);
}
