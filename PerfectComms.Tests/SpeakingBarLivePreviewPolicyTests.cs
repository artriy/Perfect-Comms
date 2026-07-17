using System;
using System.Collections.Generic;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SpeakingBarLivePreviewWorkspacePolicyTests
{
    [Fact]
    public void FullHdKeepsNormalScaleAndCentersTheCombinedWorkspace()
    {
        var workspace = SpeakingBarLivePreviewWorkspacePolicy.Compute(1920f, 1080f);

        Assert.Equal(SpeakingBarLivePreviewWorkspacePolicy.NormalScale, workspace.Scale, 4);
        Assert.Equal(-304.2f, workspace.SettingsOffsetX, 3);
        Assert.Equal(697f, workspace.PreviewLocalCenterX, 3);
        AssertWorkspaceIsCentered(workspace);
        AssertWorkspaceFits(workspace, 1920f, 1080f);
    }

    [Fact]
    public void NarrowCanvasShrinksToFitItsSafeHorizontalBounds()
    {
        var workspace = SpeakingBarLivePreviewWorkspacePolicy.Compute(1280f, 720f);
        float expectedScale =
            (1280f - SpeakingBarLivePreviewWorkspacePolicy.SafeMargin * 2f) /
            (SpeakingBarLivePreviewWorkspacePolicy.SettingsWidth +
             SpeakingBarLivePreviewWorkspacePolicy.Gap +
             SpeakingBarLivePreviewWorkspacePolicy.PreviewWidth);

        Assert.Equal(expectedScale, workspace.Scale, 4);
        AssertWorkspaceIsCentered(workspace);
        AssertWorkspaceFits(workspace, 1280f, 720f);
    }

    [Fact]
    public void ShortCanvasUsesHeightAsTheLimitingDimension()
    {
        var workspace = SpeakingBarLivePreviewWorkspacePolicy.Compute(1920f, 600f);
        float expectedScale =
            (600f - SpeakingBarLivePreviewWorkspacePolicy.SafeMargin * 2f) /
            SpeakingBarLivePreviewWorkspacePolicy.SettingsHeight;

        Assert.Equal(expectedScale, workspace.Scale, 4);
        AssertWorkspaceIsCentered(workspace);
        AssertWorkspaceFits(workspace, 1920f, 600f);
    }

    [Fact]
    public void ExtremelySmallCanvasStopsAtTheUsabilityScaleFloor()
    {
        var workspace = SpeakingBarLivePreviewWorkspacePolicy.Compute(640f, 360f);

        Assert.Equal(0.50f, workspace.Scale, 4);
        AssertWorkspaceIsCentered(workspace);
    }

    [Fact]
    public void InvalidCanvasDimensionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SpeakingBarLivePreviewWorkspacePolicy.Compute(0f, 1080f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SpeakingBarLivePreviewWorkspacePolicy.Compute(1920f, -1f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SpeakingBarLivePreviewWorkspacePolicy.Compute(float.NaN, 1080f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SpeakingBarLivePreviewWorkspacePolicy.Compute(1920f, float.PositiveInfinity));
    }

    private static void AssertWorkspaceIsCentered(SpeakingBarPreviewWorkspace workspace)
    {
        var (left, right, _, _) = WorkspaceBounds(workspace);
        Assert.Equal(-left, right, 3);
    }

    private static void AssertWorkspaceFits(
        SpeakingBarPreviewWorkspace workspace,
        float canvasWidth,
        float canvasHeight)
    {
        var (left, right, bottom, top) = WorkspaceBounds(workspace);
        float horizontalLimit = canvasWidth * 0.5f - SpeakingBarLivePreviewWorkspacePolicy.SafeMargin;
        float verticalLimit = canvasHeight * 0.5f - SpeakingBarLivePreviewWorkspacePolicy.SafeMargin;

        Assert.InRange(left, -horizontalLimit - 0.01f, horizontalLimit + 0.01f);
        Assert.InRange(right, -horizontalLimit - 0.01f, horizontalLimit + 0.01f);
        Assert.InRange(bottom, -verticalLimit - 0.01f, verticalLimit + 0.01f);
        Assert.InRange(top, -verticalLimit - 0.01f, verticalLimit + 0.01f);
    }

    private static (float Left, float Right, float Bottom, float Top) WorkspaceBounds(
        SpeakingBarPreviewWorkspace workspace)
    {
        float settingsHalfWidth =
            SpeakingBarLivePreviewWorkspacePolicy.SettingsWidth * workspace.Scale * 0.5f;
        float previewHalfWidth =
            SpeakingBarLivePreviewWorkspacePolicy.PreviewWidth * workspace.Scale * 0.5f;
        float previewCenter = workspace.SettingsOffsetX +
            workspace.PreviewLocalCenterX * workspace.Scale;
        float halfHeight = SpeakingBarLivePreviewWorkspacePolicy.SettingsHeight * workspace.Scale * 0.5f;

        return (
            workspace.SettingsOffsetX - settingsHalfWidth,
            previewCenter + previewHalfWidth,
            -halfHeight,
            halfHeight);
    }
}

public sealed class SpeakingBarLivePreviewTransitionPolicyTests
{
    [Fact]
    public void ClosedAndOpenStatesHaveExactStableEndpoints()
    {
        Assert.Equal(new SpeakingBarPreviewTransition(0f, 0f),
            SpeakingBarLivePreviewTransitionPolicy.Resolve(0f));
        Assert.Equal(new SpeakingBarPreviewTransition(1f, 1f),
            SpeakingBarLivePreviewTransitionPolicy.Resolve(1f));
    }

    [Fact]
    public void SettingsMovementLeadsThePreviewReveal()
    {
        var whenRevealBegins = SpeakingBarLivePreviewTransitionPolicy.Resolve(
            SpeakingBarLivePreviewTransitionPolicy.RevealStart);
        var whenMoveFinishes = SpeakingBarLivePreviewTransitionPolicy.Resolve(
            SpeakingBarLivePreviewTransitionPolicy.MoveEnd);

        Assert.True(whenRevealBegins.Move > 0.98f);
        Assert.Equal(0f, whenRevealBegins.Reveal, 5);
        Assert.Equal(1f, whenMoveFinishes.Move, 5);
        Assert.True(whenMoveFinishes.Reveal < 0.02f);
    }

    [Fact]
    public void BothChannelsAreMonotonicAndClampOutsideTheTransition()
    {
        var before = SpeakingBarLivePreviewTransitionPolicy.Resolve(-1f);
        var previous = before;
        for (int i = 1; i <= 100; i++)
        {
            var current = SpeakingBarLivePreviewTransitionPolicy.Resolve(i / 100f);
            Assert.True(current.Move >= previous.Move);
            Assert.True(current.Reveal >= previous.Reveal);
            previous = current;
        }

        Assert.Equal(new SpeakingBarPreviewTransition(0f, 0f), before);
        Assert.Equal(new SpeakingBarPreviewTransition(1f, 1f),
            SpeakingBarLivePreviewTransitionPolicy.Resolve(2f));
    }

    [Fact]
    public void ProgressDriverReversesCleanlyMidAnimationAndClampsLargeFrames()
    {
        float halfway = SpeakingBarLivePreviewTransitionPolicy.Advance(
            0f,
            enabled: true,
            SpeakingBarLivePreviewTransitionPolicy.DurationSeconds * 0.5f);
        float reversed = SpeakingBarLivePreviewTransitionPolicy.Advance(
            halfway,
            enabled: false,
            SpeakingBarLivePreviewTransitionPolicy.DurationSeconds * 0.25f);

        Assert.Equal(0.5f, halfway, 5);
        Assert.Equal(0.25f, reversed, 5);
        Assert.Equal(reversed, SpeakingBarLivePreviewTransitionPolicy.Advance(
            reversed, enabled: true, unscaledDeltaTime: 0f));
        Assert.Equal(1f, SpeakingBarLivePreviewTransitionPolicy.Advance(
            reversed, enabled: true, unscaledDeltaTime: 99f));
        Assert.Equal(0f, SpeakingBarLivePreviewTransitionPolicy.Advance(
            reversed, enabled: false, unscaledDeltaTime: 99f));
    }
}

public sealed class SpeakingBarLivePreviewLifecyclePolicyTests
{
    [Fact]
    public void LivePreviewDisablesOutsideTheHudTab()
    {
        foreach (VoiceSettingsCategory category in Enum.GetValues<VoiceSettingsCategory>())
        {
            Assert.Equal(
                category != VoiceSettingsCategory.Hud,
                SpeakingBarLivePreviewLifecyclePolicy.ShouldDisableForCategory(category));
        }
    }
}

public sealed class SpeakingBarPreviewRosterTests
{
    [Fact]
    public void RosterUsesFifteenRoundTrippableIdsWithAlivePlayersBeforeGhosts()
    {
        Assert.Equal(15, SpeakingBarPreviewRoster.PlayerCount);
        Assert.Equal(10, SpeakingBarPreviewRoster.GhostStartIndex);
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < SpeakingBarPreviewRoster.PlayerCount; index++)
        {
            byte id = SpeakingBarPreviewRoster.PlayerId(index);
            Assert.Equal((byte)(240 + index), id);
            Assert.True(SpeakingBarPreviewRoster.IsPlayerId(id));
            Assert.Equal(index, SpeakingBarPreviewRoster.Index(id));
            Assert.Equal(index >= 10, SpeakingBarPreviewRoster.IsGhost(index));
            string name = SpeakingBarPreviewRoster.Name(index);
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.True(names.Add(name));
        }

        Assert.Equal("Player 01", SpeakingBarPreviewRoster.Name(0));
        Assert.Equal("Player 10", SpeakingBarPreviewRoster.Name(9));
        Assert.Equal("Ghost 11", SpeakingBarPreviewRoster.Name(10));
        Assert.Equal("Ghost 15", SpeakingBarPreviewRoster.Name(14));
        Assert.False(SpeakingBarPreviewRoster.IsPlayerId(239));
        Assert.False(SpeakingBarPreviewRoster.IsPlayerId(255));
    }

    [Fact]
    public void SyntheticVoiceActivityIsDeterministicAndSparse()
    {
        int[] speaking = { 0, 4, 9, 12 };
        for (int index = 0; index < SpeakingBarPreviewRoster.PlayerCount; index++)
            Assert.Equal(Array.IndexOf(speaking, index) >= 0,
                SpeakingBarPreviewRoster.VoiceLevel(index) > 0f);
    }
}

public sealed class SpeakingBarSharedLayoutPolicyTests
{
    [Theory]
    [InlineData((int)SpeakingBarNamePosition.Bottom)]
    [InlineData((int)SpeakingBarNamePosition.Top)]
    [InlineData((int)SpeakingBarNamePosition.Left)]
    [InlineData((int)SpeakingBarNamePosition.Right)]
    public void ExplicitNamePositionOverridesPresetAndManualInference(int configuredValue)
    {
        var configured = (SpeakingBarNamePosition)configuredValue;

        Assert.Equal(configured, SpeakingBarLayoutPolicy.ResolveNamePosition(
            configured,
            manualLayout: true,
            VoiceControlsLayout.Vertical,
            manualX: 0.99f,
            manualY: 0.01f,
            SpeakingBarPosition.BottomRight));
    }

    [Theory]
    [InlineData((int)SpeakingBarPosition.TopMiddle, (int)SpeakingBarNamePosition.Bottom)]
    [InlineData((int)SpeakingBarPosition.BottomMiddle, (int)SpeakingBarNamePosition.Top)]
    [InlineData((int)SpeakingBarPosition.TopLeft, (int)SpeakingBarNamePosition.Right)]
    [InlineData((int)SpeakingBarPosition.TopRight, (int)SpeakingBarNamePosition.Left)]
    public void AutoNamePositionUsesPresetRecommendationOutsideManualMode(
        int presetValue,
        int expectedValue)
    {
        Assert.Equal((SpeakingBarNamePosition)expectedValue,
            SpeakingBarLayoutPolicy.ResolveNamePosition(
                SpeakingBarNamePosition.Auto,
                manualLayout: false,
                VoiceControlsLayout.Horizontal,
                manualX: 0f,
                manualY: 0f,
                (SpeakingBarPosition)presetValue));
    }

    [Theory]
    [InlineData(0.10f, 0.50f, (int)VoiceControlsLayout.Vertical, (int)SpeakingBarNamePosition.Right)]
    [InlineData(0.90f, 0.50f, (int)VoiceControlsLayout.Vertical, (int)SpeakingBarNamePosition.Left)]
    [InlineData(0.50f, 0.10f, (int)VoiceControlsLayout.Horizontal, (int)SpeakingBarNamePosition.Top)]
    [InlineData(0.50f, 0.90f, (int)VoiceControlsLayout.Horizontal, (int)SpeakingBarNamePosition.Bottom)]
    [InlineData(0.50f, 0.50f, (int)VoiceControlsLayout.Vertical, (int)SpeakingBarNamePosition.Right)]
    [InlineData(0.50f, 0.50f, (int)VoiceControlsLayout.Horizontal, (int)SpeakingBarNamePosition.Bottom)]
    public void ManualAutoNamePositionPointsIntoTheNearestScreenEdge(
        float manualX,
        float manualY,
        int orientationValue,
        int expectedValue)
    {
        Assert.Equal((SpeakingBarNamePosition)expectedValue,
            SpeakingBarLayoutPolicy.ResolveNamePosition(
                SpeakingBarNamePosition.Auto,
                manualLayout: true,
                (VoiceControlsLayout)orientationValue,
                manualX,
                manualY,
                SpeakingBarPosition.TopMiddle));
    }

    [Fact]
    public void ManualAutoNamePositionUsesHorizontalEdgeOnACornerTie()
    {
        Assert.Equal(SpeakingBarNamePosition.Right,
            SpeakingBarLayoutPolicy.ResolveNamePosition(
                SpeakingBarNamePosition.Auto,
                manualLayout: true,
                VoiceControlsLayout.Vertical,
                manualX: 0.10f,
                manualY: 0.10f,
                SpeakingBarPosition.TopMiddle));
    }

    [Theory]
    [InlineData(true, 0.49f, (int)SpeakingBarLayoutDirection.Up)]
    [InlineData(true, 0.50f, (int)SpeakingBarLayoutDirection.Down)]
    [InlineData(false, 0.01f, (int)SpeakingBarLayoutDirection.Right)]
    [InlineData(false, 0.99f, (int)SpeakingBarLayoutDirection.Right)]
    public void ManualPrimaryDirectionFollowsOrientationAndVerticalAnchor(
        bool vertical,
        float manualY,
        int expectedValue)
    {
        Assert.Equal((SpeakingBarLayoutDirection)expectedValue,
            SpeakingBarLayoutPolicy.ResolvePrimaryDirection(
                manualLayout: true,
                SpeakingBarPosition.BottomRight,
                vertical,
                manualY));
    }

    [Theory]
    [InlineData(true, 0.50f, 0.90f, (int)SpeakingBarLayoutDirection.Right)]
    [InlineData(true, 0.51f, 0.10f, (int)SpeakingBarLayoutDirection.Left)]
    [InlineData(false, 0.10f, 0.49f, (int)SpeakingBarLayoutDirection.Up)]
    [InlineData(false, 0.90f, 0.50f, (int)SpeakingBarLayoutDirection.Down)]
    public void ManualOverflowDirectionFlowsTowardAvailableScreenSpace(
        bool vertical,
        float manualX,
        float manualY,
        int expectedValue)
    {
        Assert.Equal((SpeakingBarLayoutDirection)expectedValue,
            SpeakingBarLayoutPolicy.ResolveOverflowDirection(
                manualLayout: true,
                SpeakingBarPosition.TopLeft,
                vertical,
                manualX,
                manualY));
    }

    [Fact]
    public void PresetDirectionsIgnoreManualOrientationAndCoordinates()
    {
        Assert.Equal(SpeakingBarLayoutDirection.Up,
            SpeakingBarLayoutPolicy.ResolvePrimaryDirection(
                manualLayout: false,
                SpeakingBarPosition.BottomRight,
                vertical: false,
                manualY: 0f));
        Assert.Equal(SpeakingBarLayoutDirection.Left,
            SpeakingBarLayoutPolicy.ResolveOverflowDirection(
                manualLayout: false,
                SpeakingBarPosition.BottomRight,
                vertical: false,
                manualX: 0f,
                manualY: 0f));
    }

    [Fact]
    public void CenteringPoliciesUseMiddlePresetsAndStrictManualThirds()
    {
        Assert.True(SpeakingBarLayoutPolicy.CentersVerticalPrimaryLine(
            manualLayout: false,
            SpeakingBarPosition.MiddleLeft,
            manualY: 0f));
        Assert.False(SpeakingBarLayoutPolicy.CentersVerticalPrimaryLine(
            manualLayout: false,
            SpeakingBarPosition.TopLeft,
            manualY: 0.5f));

        Assert.False(SpeakingBarLayoutPolicy.CentersVerticalPrimaryLine(
            manualLayout: true,
            SpeakingBarPosition.MiddleLeft,
            manualY: 0.33f));
        Assert.True(SpeakingBarLayoutPolicy.CentersVerticalPrimaryLine(
            manualLayout: true,
            SpeakingBarPosition.TopLeft,
            manualY: 0.50f));
        Assert.False(SpeakingBarLayoutPolicy.CentersVerticalPrimaryLine(
            manualLayout: true,
            SpeakingBarPosition.MiddleLeft,
            manualY: 0.67f));

        Assert.True(SpeakingBarLayoutPolicy.CentersOverflowLines(
            manualLayout: true,
            vertical: true,
            manualX: 0.50f,
            manualY: 0f));
        Assert.False(SpeakingBarLayoutPolicy.CentersOverflowLines(
            manualLayout: true,
            vertical: true,
            manualX: 0.33f,
            manualY: 0.50f));
        Assert.True(SpeakingBarLayoutPolicy.CentersOverflowLines(
            manualLayout: true,
            vertical: false,
            manualX: 0f,
            manualY: 0.50f));
        Assert.False(SpeakingBarLayoutPolicy.CentersOverflowLines(
            manualLayout: false,
            vertical: false,
            manualX: 0.50f,
            manualY: 0.50f));
    }

    [Fact]
    public void VerticalSingleLaneStartsAtItsAnchorAndFlowsDownOrUp()
    {
        var first = Coordinates(
            new SpeakingBarLinePlacement(0, 0, 3),
            lineCount: 1,
            vertical: true,
            SpeakingBarLayoutDirection.Down,
            SpeakingBarLayoutDirection.Left);
        var downLast = Coordinates(
            new SpeakingBarLinePlacement(0, 2, 3),
            lineCount: 1,
            vertical: true,
            SpeakingBarLayoutDirection.Down,
            SpeakingBarLayoutDirection.Left);
        var upLast = Coordinates(
            new SpeakingBarLinePlacement(0, 2, 3),
            lineCount: 1,
            vertical: true,
            SpeakingBarLayoutDirection.Up,
            SpeakingBarLayoutDirection.Right);

        AssertCoordinates(first, 0f, 0f);
        AssertCoordinates(downLast, 0f, -1.28f);
        AssertCoordinates(upLast, 0f, 1.28f);
    }

    [Fact]
    public void CenteredVerticalLaneBalancesPlayersAboveAndBelowTheAnchor()
    {
        var top = Coordinates(
            new SpeakingBarLinePlacement(0, 0, 3),
            lineCount: 1,
            vertical: true,
            SpeakingBarLayoutDirection.Down,
            SpeakingBarLayoutDirection.Right,
            centerVerticalPrimaryLine: true);
        var middle = Coordinates(
            new SpeakingBarLinePlacement(0, 1, 3),
            lineCount: 1,
            vertical: true,
            SpeakingBarLayoutDirection.Down,
            SpeakingBarLayoutDirection.Right,
            centerVerticalPrimaryLine: true);
        var bottom = Coordinates(
            new SpeakingBarLinePlacement(0, 2, 3),
            lineCount: 1,
            vertical: true,
            SpeakingBarLayoutDirection.Down,
            SpeakingBarLayoutDirection.Right,
            centerVerticalPrimaryLine: true);

        AssertCoordinates(top, 0f, 0.64f);
        AssertCoordinates(middle, 0f, 0f);
        AssertCoordinates(bottom, 0f, -0.64f);
    }

    [Fact]
    public void VerticalWrappedLinesOverflowLeftOrCenterAroundTheAnchor()
    {
        var leftOverflow = Coordinates(
            new SpeakingBarLinePlacement(1, 0, 2),
            lineCount: 2,
            vertical: true,
            SpeakingBarLayoutDirection.Down,
            SpeakingBarLayoutDirection.Left);
        var centeredFirst = Coordinates(
            new SpeakingBarLinePlacement(0, 0, 2),
            lineCount: 2,
            vertical: true,
            SpeakingBarLayoutDirection.Down,
            SpeakingBarLayoutDirection.Left,
            centerOverflowLines: true);
        var centeredSecond = Coordinates(
            new SpeakingBarLinePlacement(1, 0, 2),
            lineCount: 2,
            vertical: true,
            SpeakingBarLayoutDirection.Down,
            SpeakingBarLayoutDirection.Left,
            centerOverflowLines: true);

        AssertCoordinates(leftOverflow, -0.52f, 0f);
        AssertCoordinates(centeredFirst, -0.26f, 0f);
        AssertCoordinates(centeredSecond, 0.26f, 0f);
    }

    [Fact]
    public void HorizontalRowsCenterPlayersAndOverflowInTheRequestedDirection()
    {
        var first = Coordinates(
            new SpeakingBarLinePlacement(0, 0, 3),
            lineCount: 2,
            vertical: false,
            SpeakingBarLayoutDirection.Right,
            SpeakingBarLayoutDirection.Down);
        var last = Coordinates(
            new SpeakingBarLinePlacement(0, 2, 3),
            lineCount: 2,
            vertical: false,
            SpeakingBarLayoutDirection.Right,
            SpeakingBarLayoutDirection.Down);
        var secondRowDown = Coordinates(
            new SpeakingBarLinePlacement(1, 0, 2),
            lineCount: 2,
            vertical: false,
            SpeakingBarLayoutDirection.Right,
            SpeakingBarLayoutDirection.Down);
        var secondRowUp = Coordinates(
            new SpeakingBarLinePlacement(1, 0, 2),
            lineCount: 2,
            vertical: false,
            SpeakingBarLayoutDirection.Right,
            SpeakingBarLayoutDirection.Up);

        AssertCoordinates(first, -0.52f, 0f);
        AssertCoordinates(last, 0.52f, 0f);
        AssertCoordinates(secondRowDown, -0.26f, -0.64f);
        AssertCoordinates(secondRowUp, -0.26f, 0.64f);
    }

    [Fact]
    public void CenteredHorizontalRowsBalanceAroundTheAnchor()
    {
        var firstRow = Coordinates(
            new SpeakingBarLinePlacement(0, 0, 2),
            lineCount: 2,
            vertical: false,
            SpeakingBarLayoutDirection.Right,
            SpeakingBarLayoutDirection.Down,
            centerOverflowLines: true);
        var secondRow = Coordinates(
            new SpeakingBarLinePlacement(1, 0, 2),
            lineCount: 2,
            vertical: false,
            SpeakingBarLayoutDirection.Right,
            SpeakingBarLayoutDirection.Down,
            centerOverflowLines: true);

        AssertCoordinates(firstRow, -0.26f, 0.32f);
        AssertCoordinates(secondRow, -0.26f, -0.32f);
    }

    [Fact]
    public void CoordinatePolicyRejectsInvalidGeometry()
    {
        var placement = new SpeakingBarLinePlacement(0, 0, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeakingBarLayoutPolicy.CoordinatesFor(
            placement,
            lineCount: 0,
            vertical: true,
            SpeakingBarLayoutDirection.Down,
            SpeakingBarLayoutDirection.Right,
            centerVerticalPrimaryLine: false,
            centerOverflowLines: false,
            crossPitch: 0.52f,
            primaryPitch: 0.64f));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeakingBarLayoutPolicy.CoordinatesFor(
            placement,
            lineCount: 1,
            vertical: true,
            SpeakingBarLayoutDirection.Down,
            SpeakingBarLayoutDirection.Right,
            centerVerticalPrimaryLine: false,
            centerOverflowLines: false,
            crossPitch: 0f,
            primaryPitch: 0.64f));
    }

    private static SpeakingBarSlotCoordinates Coordinates(
        SpeakingBarLinePlacement placement,
        int lineCount,
        bool vertical,
        SpeakingBarLayoutDirection primaryDirection,
        SpeakingBarLayoutDirection overflowDirection,
        bool centerVerticalPrimaryLine = false,
        bool centerOverflowLines = false)
        => SpeakingBarLayoutPolicy.CoordinatesFor(
            placement,
            lineCount,
            vertical,
            primaryDirection,
            overflowDirection,
            centerVerticalPrimaryLine,
            centerOverflowLines,
            crossPitch: 0.52f,
            primaryPitch: 0.64f);

    private static void AssertCoordinates(
        SpeakingBarSlotCoordinates coordinates,
        float expectedX,
        float expectedY)
    {
        Assert.Equal(expectedX, coordinates.X, 4);
        Assert.Equal(expectedY, coordinates.Y, 4);
    }
}
